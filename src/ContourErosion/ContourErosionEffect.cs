using System;
using System.Collections.Generic;
using PaintDotNet;
using PaintDotNet.Effects;
using PaintDotNet.IndirectUI;
using PaintDotNet.Imaging;
using PaintDotNet.PropertySystem;
using PaintDotNet.Rendering;

namespace ContourErosion;

public sealed class ContourErosionInfo : IPluginSupportInfo
{
    public string DisplayName => "Erosão de Contorno";
    public string Author => "Bruno";
    public string Copyright => "Bruno";
    public Version Version => new Version(1, 1, 0, 0);
    public Uri? WebsiteUri => null;
}

/// <summary>
/// Erosão morfológica do canal alfa: recua o contorno de uma forma N pixels para dentro.
///
/// Algoritmo:
///  1. Campo de distância com sinal (SDF) com precisão de subpixel, usando o alfa
///     antialiased das bordas (sem binarizar, que é o que gerava dentes de serra).
///  2. Leve suavização gaussiana do SDF, que arredonda as pontas formadas onde a
///     erosão cruza cantos côncavos.
///  3. Alfa final = cobertura do pixel em relação ao raio de erosão.
/// </summary>
[PluginSupportInfo(typeof(ContourErosionInfo))]
[EffectCategory(EffectCategory.Effect)]
public sealed class ContourErosionEffect : PropertyBasedBitmapEffect
{
    // Submenu onde o efeito aparece em Efeitos. Nome novo = cria submenu.
    private const string SubMenu = "Objeto";

    private const float MaxDepth = 1000f;

    private enum PropertyNames { Amount, Mode, Smoothing }
    private enum ErosionMode { Dry, Blur }

    private float[]? signedDistance;   // SDF bruto (positivo = dentro da forma)
    private float[]? smoothed;         // SDF suavizado, usado na renderização
    private int smoothedRadius = -1;   // raio de box blur do 'smoothed' atual
    private int width, height;
    private readonly object smoothLock = new object();

    public ContourErosionEffect()
        : base("Erosão de Contorno", SubMenu, BitmapEffectOptions.Create() with { IsConfigurable = true })
    {
    }

    protected override PropertyCollection OnCreatePropertyCollection()
    {
        var props = new List<Property>
        {
            new Int32Property(PropertyNames.Amount, 10, 1, 100),
            StaticListChoiceProperty.CreateForEnum(PropertyNames.Mode, ErosionMode.Dry),
            new Int32Property(PropertyNames.Smoothing, 4, 0, 20),
        };
        return new PropertyCollection(props);
    }

    protected override ControlInfo OnCreateConfigUI(PropertyCollection props)
    {
        ControlInfo ui = CreateDefaultConfigUI(props);
        ui.SetPropertyControlValue(PropertyNames.Amount, ControlInfoPropertyNames.DisplayName, "Profundidade da erosão (px)");
        ui.SetPropertyControlValue(PropertyNames.Mode, ControlInfoPropertyNames.DisplayName, "Tipo de borda");
        ui.SetPropertyControlValue(PropertyNames.Smoothing, ControlInfoPropertyNames.DisplayName, "Suavização do contorno");
        PropertyControlInfo mode = ui.FindControlForPropertyName(PropertyNames.Mode);
        mode.SetValueDisplayName(ErosionMode.Dry, "Seco");
        mode.SetValueDisplayName(ErosionMode.Blur, "Com blur");
        return ui;
    }

    protected override void OnInitializeRenderInfo(IBitmapEffectRenderInfo renderInfo)
    {
        // O SDF não depende dos controles: calcula uma vez por imagem.
        if (signedDistance == null)
        {
            using IEffectInputBitmap<ColorBgra32> source = Environment.GetSourceBitmapBgra32();
            SizeInt32 size = source.Size;
            using IBitmapLock<ColorBgra32> src = source.Lock(new RectInt32(0, 0, size.Width, size.Height));
            width = size.Width;
            height = size.Height;
            signedDistance = BuildSignedDistance(src, width, height);
            smoothed = null;
            smoothedRadius = -1;
        }

        base.OnInitializeRenderInfo(renderInfo);
    }

    /// <summary>
    /// SDF suavizado para o raio de blur pedido. A suavização cresce com a profundidade da
    /// erosão; só é refeita quando o raio muda. Chamado de várias threads de render.
    /// </summary>
    private float[] GetSmoothed(int radius)
    {
        lock (smoothLock)
        {
            if (smoothed == null || radius != smoothedRadius)
            {
                smoothed = radius == 0
                    ? signedDistance!
                    : GaussianApprox(signedDistance!, width, height, radius);
                smoothedRadius = radius;
            }
            return smoothed;
        }
    }

    protected override void OnDispose(bool disposing)
    {
        signedDistance = null;
        smoothed = null;
        base.OnDispose(disposing);
    }

    protected override unsafe void OnRender(IBitmapEffectOutput output)
    {
        int amount = Token.GetProperty<Int32Property>(PropertyNames.Amount).Value;
        int smoothing = Token.GetProperty<Int32Property>(PropertyNames.Smoothing).Value;
        float[] sdf = GetSmoothed(smoothing == 0 ? 0 : BoxRadiusFor(smoothing * 0.25f));
        var mode = (ErosionMode)Token.GetProperty<StaticListChoiceProperty>(PropertyNames.Mode).Value;

        // No modo blur, o degradê começa onde o corte seco terminaria.
        float feather = mode == ErosionMode.Blur ? Math.Max(2f, amount * 0.5f) : 1f;

        RectInt32 rect = output.Bounds;
        using IEffectInputBitmap<ColorBgra32> source = Environment.GetSourceBitmapBgra32();
        using IBitmapLock<ColorBgra32> src = source.Lock(rect);
        using IBitmapLock<ColorBgra32> dst = output.LockBgra32();

        for (int y = 0; y < rect.Height; y++)
        {
            ColorBgra32* s = (ColorBgra32*)((byte*)src.Buffer + (long)y * src.BufferStride);
            ColorBgra32* d = (ColorBgra32*)((byte*)dst.Buffer + (long)y * dst.BufferStride);
            int row = (rect.Top + y) * width + rect.Left;

            for (int x = 0; x < rect.Width; x++)
            {
                ColorBgra32 p = s[x];
                float depth = sdf[row + x]; // distância (px) até o contorno, positiva dentro

                float coverage;
                if (mode == ErosionMode.Dry)
                {
                    // Corte nítido com 1px de antialias, centrado em depth == amount.
                    coverage = Clamp01(depth - amount + 0.5f);
                }
                else
                {
                    float t = Clamp01((depth - amount) / feather);
                    coverage = t * t * (3f - 2f * t); // smoothstep
                }

                if (coverage < 1f)
                {
                    p = ColorBgra32.FromBgra(p.B, p.G, p.R, (byte)(p.A * coverage + 0.5f));
                }
                d[x] = p;
            }
        }
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    private static int BoxRadiusFor(float sigma)
    {
        // 3 passes de box blur aproximam uma gaussiana: largura = sqrt(12σ²/3 + 1).
        float w = MathF.Sqrt(4f * sigma * sigma + 1f);
        return Math.Max(1, (int)MathF.Round((w - 1f) / 2f));
    }

    // ---- SDF com precisão de subpixel a partir do alfa antialiased ----

    private static unsafe float[] BuildSignedDistance(IBitmapLock<ColorBgra32> src, int w, int h)
    {
        const float Inf = 1e20f;
        int n = w * h;
        var alpha = new float[n];
        for (int y = 0; y < h; y++)
        {
            ColorBgra32* row = (ColorBgra32*)((byte*)src.Buffer + (long)y * src.BufferStride);
            for (int x = 0; x < w; x++) alpha[y * w + x] = row[x].A / 255f;
        }

        // Cada pixel parcial (0 < a < 1) é uma semente a uma distância 'a' (dentro)
        // ou '1 - a' (fora) do contorno; pixels totalmente opacos/transparentes são infinitos
        // para o lado oposto. Isso localiza o contorno com precisão de subpixel.
        var inner = new float[n]; // distância até o exterior
        var outer = new float[n]; // distância até o interior
        for (int i = 0; i < n; i++)
        {
            float a = alpha[i];
            inner[i] = a >= 1f ? Inf : a * a;
            outer[i] = a <= 0f ? Inf : (1f - a) * (1f - a);
        }

        DistanceTransform2D(inner, w, h);
        DistanceTransform2D(outer, w, h);

        var sdf = new float[n];
        for (int i = 0; i < n; i++)
        {
            // Dentro: profundidade = D - 0.5. Fora: valor negativo equivalente. Contínuo em a = 0.5.
            sdf[i] = alpha[i] > 0.5f
                ? Math.Min(inner[i], MaxDepth) - 0.5f
                : -(Math.Min(outer[i], MaxDepth) - 0.5f);
        }
        return sdf;
    }

    /// <summary>Transformada de distância euclidiana O(N) (Felzenszwalb); entra d² semente, sai distância.</summary>
    private static void DistanceTransform2D(float[] f, int w, int h)
    {
        int max = Math.Max(w, h);
        var col = new float[max];
        var tmp = new float[max];
        var v = new int[max];
        var z = new float[max + 1];

        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++) col[y] = f[y * w + x];
            Transform1D(col, tmp, v, z, h);
            for (int y = 0; y < h; y++) f[y * w + x] = tmp[y];
        }
        for (int y = 0; y < h; y++)
        {
            Array.Copy(f, y * w, col, 0, w);
            Transform1D(col, tmp, v, z, w);
            for (int x = 0; x < w; x++) f[y * w + x] = MathF.Sqrt(tmp[x]);
        }
    }

    private static void Transform1D(float[] src, float[] dst, int[] v, float[] z, int n)
    {
        int k = 0;
        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            float s;
            while (true)
            {
                int p = v[k];
                s = ((src[q] + (float)q * q) - (src[p] + (float)p * p)) / (2f * q - 2f * p);
                if (s <= z[k] && k > 0) { k--; continue; }
                break;
            }
            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = float.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            float dq = q - v[k];
            dst[q] = dq * dq + src[v[k]];
        }
    }

    // ---- Suavização: gaussiana aproximada por 3 passes de box blur separável ----

    private static float[] GaussianApprox(float[] src, int w, int h, int r)
    {
        var a = (float[])src.Clone();
        var b = new float[a.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxBlur(a, b, w, h, r, horizontal: true);
            BoxBlur(b, a, w, h, r, horizontal: false);
        }
        return a;
    }

    private static void BoxBlur(float[] src, float[] dst, int w, int h, int r, bool horizontal)
    {
        int lines = horizontal ? h : w;
        int len = horizontal ? w : h;
        int stride = horizontal ? 1 : w;
        float inv = 1f / (2 * r + 1);

        for (int line = 0; line < lines; line++)
        {
            int start = horizontal ? line * w : line;
            double sum = 0;
            for (int i = -r; i <= r; i++) sum += src[start + Math.Clamp(i, 0, len - 1) * stride];
            for (int i = 0; i < len; i++)
            {
                dst[start + i * stride] = (float)(sum * inv);
                sum += src[start + Math.Min(i + r + 1, len - 1) * stride]
                     - src[start + Math.Max(i - r, 0) * stride];
            }
        }
    }
}
