using System;
using System.Runtime.InteropServices;
using ContourErosion;
using PaintDotNet;
using PaintDotNet.Effects;
using PaintDotNet.Imaging;
using PaintDotNet.Rendering;
using PluginShared;

namespace ShapeFit;

public sealed class ShapeFitInfo : IPluginSupportInfo
{
    public string DisplayName => "ShapeFit";
    public string Author => "Bruno";
    public string Copyright => "Bruno";
    public Version Version => new Version(1, 0, 0, 0);
    public Uri? WebsiteUri => null;
}

/// <summary>
/// "Liquify automático": deforma o objeto da camada atual (os pixels visíveis) até a silhueta dele
/// coincidir com a forma da seleção, preenchendo-a. Sem janela de opções: é aplicado direto.
///
/// Passos: (1) rasteriza a forma da seleção; (2) alinha o objeto a ela por momentos (centro, escala e
/// eixos); (3) registra as duas silhuetas com um warp não-rígido de baixa distorção (ShapeRegistration);
/// (4) amostra as cores do objeto pelo mapa de deformação.
///
/// De onde vem o objeto: da camada atual; se ela estiver vazia, da camada mais próxima abaixo com
/// conteúdo. Recomendado: crie uma camada nova vazia acima do original, selecione a forma e aplique;
/// o original fica intacto na camada de baixo (esconda ou apague essa camada se não quiser vê-lo).
/// Com o objeto na própria camada atual, o que estiver fora da seleção fica como estava (um efeito só
/// escreve dentro da seleção): depois de aplicar, Ctrl+I e Delete limpam o resto.
/// </summary>
[PluginSupportInfo(typeof(ShapeFitInfo))]
[EffectCategory(EffectCategory.Effect)]
public sealed class ShapeFitEffect : BitmapEffect
{
    private const string SubMenu = "Distorção";

    // Rigidez fixa: σ da suavização = 2% do tamanho da forma (ver Tuning_table nos testes).
    private const double RigidityFraction = 0.02;
    // O registro roda numa grade de no máximo este lado; o deslocamento é interpolado para o tamanho real.
    private const int MaxGridSide = 1024;
    // Limite da pirâmide de cores (pixels no nível 0).
    private const int MaxPyramidPixels = 4_000_000;
    // Quanto (px da grade) o ponto de amostra é mantido para dentro da borda do objeto.
    private const float InsideMargin = 1.5f;

    private readonly object gate = new();

    // Fonte desta renderização (preenchida em OnInitializeRenderInfo).
    private byte[]? bgra;
    private int width, height;
    private ulong sourceHash;
    private RectFloat selectionBounds;
    private float selectionArea;

    // Resultado do encaixe, reaproveitado enquanto a fonte e a seleção forem as mesmas.
    private FitState? fit;
    private bool fitComputed;
    private ulong fitHash;
    private RectFloat fitBounds;
    private float fitArea;

    private sealed record FitState(
        RectInt32 Region, double Scale, int Pad, int GridW, int GridH,
        ShapeRegistration.Field Field, float[] MovingSdf, ShapeRegistration.Affine ToSource, ColorPyramid Colors, double Lod);

    public ShapeFitEffect()
        : base("ShapeFit", ResourceIcon.Load(typeof(ShapeFitEffect).Assembly), SubMenu,
               BitmapEffectOptions.Create() with { IsConfigurable = false })
    {
    }

    protected override unsafe void OnInitializeRenderInfo(IBitmapEffectRenderInfo renderInfo)
    {
        // O objeto vem da camada atual. Se ela estiver vazia, vem da camada mais próxima abaixo dela
        // com conteúdo: assim o original fica numa camada separada, intacto, e o resultado é escrito
        // na camada atual (um efeito só escreve dentro da seleção, então não dá para apagar o original).
        byte[] buffer;
        int w, h;
        using (IEffectInputBitmap<ColorBgra32> current = Environment.GetSourceBitmapBgra32())
        {
            (buffer, w, h) = ReadBgra(current, out bool hasContent);

            if (!hasContent)
            {
                var layers = Environment.Document.Layers;
                int top = Math.Min(Environment.SourceLayerIndex, layers.Count) - 1; // índice 0 = fundo
                for (int i = top; i >= 0; i--)
                {
                    using IEffectInputBitmap<ColorBgra32> below = layers[i].GetBitmapBgra32();
                    var (candidate, cw, ch) = ReadBgra(below, out bool belowHasContent);
                    if (!belowHasContent || cw != w || ch != h) continue;
                    buffer = candidate;
                    break;
                }
            }
        }

        var geometry = Environment.Selection.Geometry;
        RectFloat bounds = geometry.GetBounds(null);
        float area = geometry.ComputeArea(null, null);

        lock (gate)
        {
            bgra = buffer;
            width = w;
            height = h;
            sourceHash = Hash(buffer);
            selectionBounds = bounds;
            selectionArea = area;
        }

        base.OnInitializeRenderInfo(renderInfo);
    }

    protected override void OnDispose(bool disposing)
    {
        lock (gate)
        {
            bgra = null;
            fit = null;
            fitComputed = false;
        }
        base.OnDispose(disposing);
    }

    protected override unsafe void OnRender(IBitmapEffectOutput output)
    {
        FitState? state;
        lock (gate) state = EnsureFit();

        RectInt32 rect = output.Bounds;
        using IBitmapLock<ColorBgra32> dst = output.LockBgra32();

        if (state == null)
        {
            CopySource(rect, dst);
            return;
        }

        var (ux, uy, gw, gh) = (state.Field.Ux, state.Field.Uy, state.GridW, state.GridH);
        for (int y = 0; y < rect.Height; y++)
        {
            ColorBgra32* d = (ColorBgra32*)((byte*)dst.Buffer + (long)y * dst.BufferStride);
            double gy = (rect.Top + y + 0.5 - state.Region.Top) * state.Scale + state.Pad;

            for (int x = 0; x < rect.Width; x++)
            {
                double gx = (rect.Left + x + 0.5 - state.Region.Left) * state.Scale + state.Pad;

                // Deslocamento do encaixe nesta posição da grade (índice = coordenada - 0,5).
                float ix = (float)gx - 0.5f, iy = (float)gy - 0.5f;
                float sx = ix + ShapeRegistration.SampleClamped(ux, gw, gh, ix, iy);
                float sy = iy + ShapeRegistration.SampleClamped(uy, gw, gh, ix, iy);

                // Onde o encaixe aponta para fora do objeto (ele nunca é 100% exato), usa a cor da borda
                // mais próxima em vez de uma média borrada, que aparecia como cinza.
                ShapeRegistration.ProjectInside(state.MovingSdf, gw, gh, ref sx, ref sy, InsideMargin);

                var (qx, qy) = state.ToSource.Apply(sx + 0.5, sy + 0.5);
                d[x] = state.Colors.SampleOpaque(qx, qy, state.Lod);
            }
        }
    }

    // ---- Encaixe (calculado uma vez por fonte/seleção) ----

    private FitState? EnsureFit()
    {
        if (bgra == null) return null;
        if (fitComputed && fitHash == sourceHash && fitBounds == selectionBounds && fitArea == selectionArea)
            return fit;

        fit = BuildFit();
        fitComputed = true;
        fitHash = sourceHash;
        fitBounds = selectionBounds;
        fitArea = selectionArea;
        return fit;
    }

    private FitState? BuildFit()
    {
        int w = width, h = height;
        byte[] px = bgra!;

        // 1. Objeto: alfa da camada, normalizado (corpos em alfa 253 contam como opacos).
        var alpha = new float[w * h];
        for (int i = 0; i < alpha.Length; i++) alpha[i] = px[i * 4 + 3] / 255f;
        alpha = SdfMath.NormalizeAlpha(alpha);

        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (alpha[y * w + x] > 0f)
                {
                    if (x < x0) x0 = x; if (x > x1) x1 = x;
                    if (y < y0) y0 = y; if (y > y1) y1 = y;
                }
        if (x1 < 0) return null; // camada sem objeto

        RectInt32 objectBounds = RectInt32.FromEdges(x0, y0, x1 + 1, y1 + 1);
        int ow = objectBounds.Width, oh = objectBounds.Height;
        var objectAlpha = new float[ow * oh];
        for (int y = 0; y < oh; y++)
            Array.Copy(alpha, (objectBounds.Top + y) * w + objectBounds.Left, objectAlpha, y * ow, ow);

        // 2. Alvo: região da seleção e grade de trabalho (com margem, para os SDFs terem espaço).
        RectInt32 region = Geometry.ContentBounds(selectionBounds, new SizeInt32(w, h));
        double scale = Math.Min(1.0, (double)MaxGridSide / Math.Max(region.Width, region.Height));
        int gw0 = Math.Max(1, (int)Math.Ceiling(region.Width * scale));
        int gh0 = Math.Max(1, (int)Math.Ceiling(region.Height * scale));
        int pad = Math.Max(12, (int)(0.08 * Math.Max(gw0, gh0)));
        int gw = gw0 + 2 * pad, gh = gh0 + 2 * pad;

        float[]? target = RasterizeSelection(region, scale, pad, gw, gh);
        if (target == null) return null; // seleção vazia

        // 3. Alinhamento inicial por momentos (centro, escala e eixos), da grade para a camada.
        var targetMoments = ShapeRegistration.Moments(target, gw, gh);
        var objectMoments = ShapeRegistration.Moments(objectAlpha, ow, oh);
        objectMoments = objectMoments with { Cx = objectMoments.Cx + objectBounds.Left, Cy = objectMoments.Cy + objectBounds.Top };
        var toSource = ShapeRegistration.AlignmentFromMoments(targetMoments, objectMoments);

        // Máscara do objeto vista da grade do alvo.
        var moving = new float[gw * gh];
        for (int j = 0; j < gh; j++)
            for (int i = 0; i < gw; i++)
            {
                var (qx, qy) = toSource.Apply(i + 0.5, j + 0.5);
                moving[j * gw + i] = SampleAlphaZero(objectAlpha, ow, oh, qx - objectBounds.Left - 0.5, qy - objectBounds.Top - 0.5);
            }

        // 4. Registro das silhuetas (SDFs com subpixel, levemente suavizados).
        float[] f = SdfMath.GaussianApprox(SdfMath.BuildSignedDistance(target, gw, gh), gw, gh, 1);
        float[] m = SdfMath.GaussianApprox(SdfMath.BuildSignedDistance(moving, gw, gh), gw, gh, 1);

        double targetArea = 0;
        foreach (float a in target) targetArea += a;
        double sigma = RigidityFraction * Math.Sqrt(targetArea);

        var field = ShapeRegistration.Register(f, m, gw, gh, ShapeRegistration.Default(sigma));

        // 5. Cores do objeto; o nível de detalhe vem do quanto ele é reduzido/ampliado.
        var colors = new ColorPyramid(px, w, objectBounds, MaxPyramidPixels);
        double sourcePerCanvasPixel = Math.Sqrt(Math.Abs(toSource.M11 * toSource.M22 - toSource.M12 * toSource.M21)) * scale;
        double lod = Math.Max(0.0, Math.Log2(Math.Max(sourcePerCanvasPixel, 1e-6)));

        return new FitState(region, scale, pad, gw, gh, field, m, toSource, colors, lod);
    }

    /// <summary>
    /// Máscara (0..1) da seleção na grade de trabalho, usando a geometria do Paint.NET. Os pixels de borda
    /// são reamostrados 3x3 para ter alfa parcial (SDF com precisão de subpixel).
    /// </summary>
    private float[]? RasterizeSelection(RectInt32 region, double scale, int pad, int gw, int gh)
    {
        var geometry = Environment.Selection.Geometry;
        var bounds = selectionBounds;

        bool Contains(double cx, double cy) =>
            cx >= bounds.Left && cx <= bounds.Right && cy >= bounds.Top && cy <= bounds.Bottom &&
            geometry.FillContainsPoint(new Point2Float((float)cx, (float)cy), null, null);

        double CanvasX(double gx) => region.Left + (gx - pad) / scale;
        double CanvasY(double gy) => region.Top + (gy - pad) / scale;

        var binary = new float[gw * gh];
        bool any = false;
        for (int j = 0; j < gh; j++)
            for (int i = 0; i < gw; i++)
                if (Contains(CanvasX(i + 0.5), CanvasY(j + 0.5))) { binary[j * gw + i] = 1f; any = true; }
        if (!any) return null;

        var mask = (float[])binary.Clone();
        for (int j = 1; j < gh - 1; j++)
            for (int i = 1; i < gw - 1; i++)
            {
                float c = binary[j * gw + i];
                if (binary[j * gw + i - 1] == c && binary[j * gw + i + 1] == c &&
                    binary[(j - 1) * gw + i] == c && binary[(j + 1) * gw + i] == c) continue;

                int hit = 0;
                for (int sy = 0; sy < 3; sy++)
                    for (int sx = 0; sx < 3; sx++)
                        if (Contains(CanvasX(i + (sx + 0.5) / 3), CanvasY(j + (sy + 0.5) / 3))) hit++;
                mask[j * gw + i] = hit / 9f;
            }
        return mask;
    }

    // ---- Utilidades ----

    /// <summary>Copia a camada para um vetor B,G,R,A e diz se ela tem algum pixel visível (alfa &gt; 0).</summary>
    private static unsafe (byte[] data, int w, int h) ReadBgra(IEffectInputBitmap<ColorBgra32> bitmap, out bool hasContent)
    {
        SizeInt32 size = bitmap.Size;
        int w = size.Width, h = size.Height;
        using IBitmapLock<ColorBgra32> src = bitmap.Lock(new RectInt32(0, 0, w, h));

        var data = new byte[checked(w * h * 4)];
        bool any = false;
        for (int y = 0; y < h; y++)
        {
            byte* row = (byte*)src.Buffer + (long)y * src.BufferStride;
            new ReadOnlySpan<byte>(row, w * 4).CopyTo(data.AsSpan(y * w * 4, w * 4));
            if (!any)
                for (int x = 0; x < w; x++)
                    if (row[x * 4 + 3] != 0) { any = true; break; }
        }
        hasContent = any;
        return (data, w, h);
    }

    /// <summary>Alfa bilinear com zero fora da área (índices = coordenada contínua - 0,5).</summary>
    private static float SampleAlphaZero(float[] a, int w, int h, double x, double y)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        float fx = (float)(x - x0), fy = (float)(y - y0);

        float Px(int xx, int yy) => xx < 0 || yy < 0 || xx >= w || yy >= h ? 0f : a[yy * w + xx];
        float top = Px(x0, y0) * (1 - fx) + Px(x0 + 1, y0) * fx;
        float bot = Px(x0, y0 + 1) * (1 - fx) + Px(x0 + 1, y0 + 1) * fx;
        return top * (1 - fy) + bot * fy;
    }

    private unsafe void CopySource(RectInt32 rect, IBitmapLock<ColorBgra32> dst)
    {
        using IEffectInputBitmap<ColorBgra32> source = Environment.GetSourceBitmapBgra32();
        using IBitmapLock<ColorBgra32> src = source.Lock(rect);
        for (int y = 0; y < rect.Height; y++)
        {
            Buffer.MemoryCopy((byte*)src.Buffer + (long)y * src.BufferStride,
                              (byte*)dst.Buffer + (long)y * dst.BufferStride,
                              rect.Width * 4L, rect.Width * 4L);
        }
    }

    /// <summary>FNV-1a de 64 bits sobre palavras de 8 bytes: detecta que a camada mudou.</summary>
    private static ulong Hash(byte[] data)
    {
        ulong h = 14695981039346656037UL;
        var words = MemoryMarshal.Cast<byte, ulong>(data.AsSpan(0, data.Length / 8 * 8));
        foreach (ulong v in words) h = (h ^ v) * 1099511628211UL;
        for (int i = words.Length * 8; i < data.Length; i++) h = (h ^ data[i]) * 1099511628211UL;
        return h;
    }
}
