using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ContourErosion;
using PaintDotNet;
using PaintDotNet.Effects;
using PaintDotNet.IndirectUI;
using PaintDotNet.Imaging;
using PaintDotNet.PropertySystem;
using PaintDotNet.Rendering;
using PluginShared;

namespace ShapeFit;

public sealed class ShapeFitInfo : IPluginSupportInfo
{
    public string DisplayName => "ShapeFit";
    public string Author => "Bruno";
    public string Copyright => "Bruno";
    public Version Version => new Version(1, 1, 0, 0);
    public Uri? WebsiteUri => null;
}

/// <summary>
/// "Liquify automático": deforma o objeto da camada atual (os pixels visíveis) até a silhueta dele
/// coincidir com a forma da seleção, preenchendo-a.
///
/// Único controle: "Fidelidade à forma" (0 a 100%). O ponto de origem de cada pixel é
/// q = (1 − s)·p + s·F(p), onde p é a própria posição e F(p) o mapa completo de encaixe:
/// 100% é o encaixe total; 0% é q = p, uma cópia exata do original (com o alfa original). O encaixe é
/// calculado uma vez (cache estático por camada + seleção), então o slider só interpola: tempo real.
///
/// Passos: (1) rasteriza a forma da seleção; (2) alinha o objeto a ela por momentos (centro, escala e
/// eixos); (3) registra as duas silhuetas com um warp não-rígido de baixa distorção (ShapeRegistration);
/// (4) amostra as cores do objeto pelo mapa de deformação.
///
/// De onde vem o objeto: da camada atual se ela tiver conteúdo FORA da seleção; senão (vazia, ou com
/// conteúdo só dentro da seleção, ou seja, um resultado anterior), da camada mais próxima abaixo com
/// conteúdo. Recomendado: crie uma camada nova vazia acima do original, selecione a forma e aplique;
/// o original fica intacto na camada de baixo (esconda ou apague essa camada se não quiser vê-lo).
/// Com o objeto na própria camada atual, o que estiver fora da seleção fica como estava (um efeito só
/// escreve dentro da seleção): depois de aplicar, Ctrl+I e Delete limpam o resto.
/// </summary>
[PluginSupportInfo(typeof(ShapeFitInfo))]
[EffectCategory(EffectCategory.Effect)]
public sealed class ShapeFitEffect : PropertyBasedBitmapEffect
{
    private const string SubMenu = "Distorção";

    private enum PropertyNames { Fidelity }

    // Rigidez fixa: σ da suavização = 2% do tamanho da forma (ver Tuning_table nos testes).
    private const double RigidityFraction = 0.02;
    // O registro roda numa grade de no máximo este lado; o deslocamento é interpolado para o tamanho real.
    private const int MaxGridSide = 1024;
    // Limite da pirâmide de cores (pixels no nível 0).
    private const int MaxPyramidPixels = 4_000_000;

    // Fonte desta renderização (preenchida em OnInitializeRenderInfo).
    private byte[]? bgra;
    private int width, height;
    private ulong sourceHash;
    private SelectionSignature selection;

    private sealed record FitKey(ulong SourceHash, SelectionSignature Selection);

    // Resultado do encaixe. Estático: sobrevive se o Paint.NET recriar o efeito a cada ajuste do slider
    // (o encaixe é o custo grande; o slider só interpola). Guarda só o último.
    private static FitKey? cachedKey;
    private static FitState? cachedFit;
    private static readonly object cacheGate = new();

    private sealed record FitState(
        RectInt32 Region, double Scale, int Pad, int GridW, int GridH,
        ShapeRegistration.Field Field, ShapeRegistration.Affine ToSource, ColorPyramid Colors, double Lod,
        float[] ObjectAlpha, RectInt32 ObjectBounds);

    public ShapeFitEffect()
        : base("ShapeFit", ResourceIcon.Load(typeof(ShapeFitEffect).Assembly), SubMenu,
               BitmapEffectOptions.Create() with { IsConfigurable = true })
    {
    }

    protected override PropertyCollection OnCreatePropertyCollection()
    {
        var props = new List<Property> { new Int32Property(PropertyNames.Fidelity, 100, 0, 100) };
        return new PropertyCollection(props);
    }

    protected override ControlInfo OnCreateConfigUI(PropertyCollection props)
    {
        ControlInfo ui = CreateDefaultConfigUI(props);
        ui.SetPropertyControlValue(PropertyNames.Fidelity, ControlInfoPropertyNames.DisplayName, "Fidelidade à forma (%)");
        return ui;
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

            // A camada atual só é a fonte se tiver conteúdo FORA da seleção (o objeto original mora nela).
            // Se o conteúdo dela está todo dentro da seleção, é o resultado de uma aplicação anterior
            // (o efeito só escreve dentro da seleção): encaixar isso de novo distorceria só a parte já
            // recortada. Nesse caso o objeto vem da camada mais próxima abaixo com conteúdo.
            RectInt32 selectionRegion = Geometry.ContentBounds(
                Environment.Selection.Geometry.GetBounds(null), new SizeInt32(w, h));
            bool objectIsHere = hasContent && HasContentOutside(buffer, w, h, selectionRegion);

            if (!objectIsHere)
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

        var sig = SelectionSignature.Of(Environment.Selection.Geometry, new SizeInt32(w, h));

        bgra = buffer;
        width = w;
        height = h;
        sourceHash = Hash(buffer);
        selection = sig;

        base.OnInitializeRenderInfo(renderInfo);
    }

    protected override unsafe void OnRender(IBitmapEffectOutput output)
    {
        double s = Token.GetProperty<Int32Property>(PropertyNames.Fidelity).Value / 100.0;

        FitState? state = EnsureFit();

        RectInt32 rect = output.Bounds;
        using IBitmapLock<ColorBgra32> dst = output.LockBgra32();

        // 0% (ou sem objeto/seleção): cópia exata do original.
        if (state == null || s <= 0.0)
        {
            CopySource(rect, dst);
            return;
        }

        var (ux, uy, gw, gh) = (state.Field.Ux, state.Field.Uy, state.GridW, state.GridH);
        int ow = state.ObjectBounds.Width, oh = state.ObjectBounds.Height;
        double lod = s * state.Lod;

        for (int y = 0; y < rect.Height; y++)
        {
            ColorBgra32* d = (ColorBgra32*)((byte*)dst.Buffer + (long)y * dst.BufferStride);
            double py = rect.Top + y + 0.5;
            double gy = (py - state.Region.Top) * state.Scale + state.Pad;

            for (int x = 0; x < rect.Width; x++)
            {
                double px = rect.Left + x + 0.5;
                double gx = (px - state.Region.Left) * state.Scale + state.Pad;

                // Mapa completo de encaixe F(p): deslocamento na grade (índice = coordenada - 0,5), depois
                // da grade para a camada. O encaixe mira 1 px para dentro da borda do objeto, então com
                // 100% o ponto de origem cai sempre dentro do objeto.
                float ix = (float)gx - 0.5f, iy = (float)gy - 0.5f;
                float du = ShapeRegistration.SampleClamped(ux, gw, gh, ix, iy);
                float dv = ShapeRegistration.SampleClamped(uy, gw, gh, ix, iy);
                var (fx, fy) = state.ToSource.Apply(gx + du, gy + dv);

                // q = (1 - s)·p + s·F(p): 0% lê o próprio pixel, 100% é o encaixe total.
                double qx = px + s * (fx - px), qy = py + s * (fy - py);

                state.Colors.Sample(qx, qy, lod, out float r, out float g, out float b, out float aRaw);

                // Alfa: do original com 0%; do objeto normalizado (corpo em alfa 253 vira opaco) com 100%.
                float aNorm = SampleAlphaZero(state.ObjectAlpha, ow, oh,
                                              qx - state.ObjectBounds.Left - 0.5, qy - state.ObjectBounds.Top - 0.5);
                float a = aRaw + (aNorm - aRaw) * (float)s;

                d[x] = ColorBgra32.FromBgra(ToByte(b), ToByte(g), ToByte(r), ToByte(a * 255f));
            }
        }
    }

    private static byte ToByte(float v) => (byte)(v < 0f ? 0 : (v > 255f ? 255 : v + 0.5f));

    // ---- Encaixe (calculado uma vez por camada + seleção, em cache estático) ----

    private FitState? EnsureFit()
    {
        if (bgra == null) return null;
        var key = new FitKey(sourceHash, selection);

        lock (cacheGate)
        {
            if (cachedKey == key) return cachedFit;

            FitState? fit = BuildFit();
            cachedKey = key;
            cachedFit = fit;
            return fit;
        }
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
        RectInt32 region = Geometry.ContentBounds(selection.Bounds, new SizeInt32(w, h));
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

        return new FitState(region, scale, pad, gw, gh, field, toSource, colors, lod, objectAlpha, objectBounds);
    }

    /// <summary>
    /// Máscara (0..1) da seleção na grade de trabalho, usando a geometria do Paint.NET e o rasterizador
    /// hierárquico (poucas consultas: só desce de nível perto da borda). Devolve null se a seleção é vazia.
    /// </summary>
    private float[]? RasterizeSelection(RectInt32 region, double scale, int pad, int gw, int gh)
    {
        var geometry = Environment.Selection.Geometry;
        var bounds = selection.Bounds;

        // (gx, gy) é coordenada contínua da grade; volta para a camada: region + (g - pad) / scale.
        bool Contains(double gx, double gy)
        {
            double cx = region.Left + (gx - pad) / scale, cy = region.Top + (gy - pad) / scale;
            return cx >= bounds.Left && cx <= bounds.Right && cy >= bounds.Top && cy <= bounds.Bottom &&
                   geometry.FillContainsPoint(new Point2Float((float)cx, (float)cy), null, null);
        }

        float[] mask = MaskRaster.Rasterize(Contains, 0, 0, gw, gh);
        foreach (float v in mask) if (v > 0f) return mask;
        return null;
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

    /// <summary>Há algum pixel visível (alfa &gt; 0) fora do retângulo <paramref name="region"/>?</summary>
    private static bool HasContentOutside(byte[] bgra, int w, int h, RectInt32 region)
    {
        for (int y = 0; y < h; y++)
        {
            bool rowInside = y >= region.Top && y < region.Bottom;
            for (int x = 0; x < w; x++)
            {
                if (rowInside && x >= region.Left && x < region.Right) { x = region.Right - 1; continue; } // pula o miolo
                if (bgra[(y * w + x) * 4 + 3] != 0) return true;
            }
        }
        return false;
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
