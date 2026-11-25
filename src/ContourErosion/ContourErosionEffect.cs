using System;
using System.Collections.Generic;
using System.IO;
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

    private enum PropertyNames { Amount, Mode, Smoothing }
    private enum ErosionMode { Dry, Blur }

    private float[]? signedDistance;   // SDF bruto (positivo = dentro da forma)
    private float[]? smoothed;         // SDF suavizado, usado na renderização
    private int smoothedRadius = -1;   // raio de box blur do 'smoothed' atual
    private int width, height;
    private ulong sourceHash;          // hash do alfa da imagem a que o SDF em cache corresponde
    private readonly object smoothLock = new object();

    public ContourErosionEffect()
        : base("Erosão de Contorno", LoadIcon(), SubMenu, BitmapEffectOptions.Create() with { IsConfigurable = true })
    {
    }

    /// <summary>Ícone do menu, embutido na DLL (src\ContourErosion\icon.png).</summary>
    private static System.Drawing.Image LoadIcon()
    {
        using Stream stream = typeof(ContourErosionEffect).Assembly.GetManifestResourceStream("icon.png")!;
        using var raw = new System.Drawing.Bitmap(stream);
        return new System.Drawing.Bitmap(raw); // cópia: não depende do stream aberto
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
        // O SDF não depende dos controles, só da imagem de origem. O Paint.NET reutiliza a mesma
        // instância do efeito entre execuções (outra imagem, ou a mesma já editada), então o cache
        // é validado por tamanho + hash do alfa a cada render, e só refeito se a imagem mudou.
        using IEffectInputBitmap<ColorBgra32> source = Environment.GetSourceBitmapBgra32();
        SizeInt32 size = source.Size;
        using IBitmapLock<ColorBgra32> src = source.Lock(new RectInt32(0, 0, size.Width, size.Height));
        float[] alpha = ReadAlpha(src, size.Width, size.Height, out ulong hash);

        lock (smoothLock)
        {
            if (signedDistance == null || size.Width != width || size.Height != height || hash != sourceHash)
            {
                width = size.Width;
                height = size.Height;
                sourceHash = hash;
                signedDistance = SdfMath.BuildSignedDistance(alpha, width, height);
                smoothed = null;
                smoothedRadius = -1;
            }
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
                    : SdfMath.GaussianApprox(signedDistance!, width, height, radius);
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

    /// <summary>Lê o alfa (0..1) e devolve também um hash FNV-1a 64 bits dele, para detectar mudança de imagem.</summary>
    private static unsafe float[] ReadAlpha(IBitmapLock<ColorBgra32> src, int w, int h, out ulong hash)
    {
        var alpha = new float[w * h];
        ulong hv = 14695981039346656037UL;
        for (int y = 0; y < h; y++)
        {
            ColorBgra32* row = (ColorBgra32*)((byte*)src.Buffer + (long)y * src.BufferStride);
            for (int x = 0; x < w; x++)
            {
                byte a = row[x].A;
                alpha[y * w + x] = a / 255f;
                hv = (hv ^ a) * 1099511628211UL;
            }
        }
        hash = hv;
        return alpha;
    }
}
