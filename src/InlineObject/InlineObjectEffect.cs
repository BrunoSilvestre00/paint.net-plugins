using System;
using System.Collections.Generic;
using ContourErosion;
using PaintDotNet;
using PaintDotNet.Effects;
using PaintDotNet.IndirectUI;
using PaintDotNet.Imaging;
using PaintDotNet.PropertySystem;
using PaintDotNet.Rendering;
using PluginShared;

namespace InlineObject;

public sealed class InlineObjectInfo : IPluginSupportInfo
{
    public string DisplayName => "Inline Object";
    public string Author => "Bruno";
    public string Copyright => "Bruno";
    public Version Version => new Version(1, 1, 0, 0);
    public Uri? WebsiteUri => null;
}

/// <summary>
/// Borda INTERNA da área selecionada: o oposto do Outline Object (BoltBait), que faz a borda por fora do
/// objeto. Usa as mesmas propriedades dele: Length (espessura, 1-100, padrão 10), Color, AntiAlias (padrão
/// ligado) e BlurRadius (0-100, padrão 0). A cor já abre com a cor PRIMÁRIA do Paint.NET. A borda é
/// desenhada sobre o que já existe dentro da seleção, junto ao contorno dela.
///
/// Prévia ao vivo: a parte cara (rasterizar a seleção e medir a distância ao contorno) é feita UMA vez por
/// seleção e guardada em um cache estático. Depois, cada bloco calcula a própria cobertura a partir da
/// distância (espessura, suavização e desfoque são só uma função dela), em paralelo e em poucos ms,
/// então mexer nos controles não recalcula nada da região inteira.
/// </summary>
[PluginSupportInfo(typeof(InlineObjectInfo))]
[EffectCategory(EffectCategory.Effect)]
public sealed class InlineObjectEffect : PropertyBasedBitmapEffect
{
    // Mesmo submenu dos outros efeitos de objeto (Erosão de Contorno).
    private const string SubMenu = "Objeto";

    private enum PropertyNames { Length, Color, AntiAlias, BlurRadius }

    // Seleção rasterizada + distância ao contorno. Estático: sobrevive se o Paint.NET recriar o efeito
    // a cada ajuste (senão a rasterização, que é o custo grande, seria refeita a cada movimento do slider).
    private sealed record MaskEntry(SelectionSignature Signature, RectInt32 Region, float[] Depth);
    private static MaskEntry? cached;
    private static readonly object cacheGate = new();

    // Assinatura da seleção atual (preenchida em OnInitializeRenderInfo).
    private SelectionSignature currentSignature;
    private SizeInt32 imageSize;

    public InlineObjectEffect()
        : base("Inline Object", ResourceIcon.Load(typeof(InlineObjectEffect).Assembly), SubMenu,
               BitmapEffectOptions.Create() with { IsConfigurable = true })
    {
    }

    protected override PropertyCollection OnCreatePropertyCollection()
    {
        var props = new List<Property>
        {
            new Int32Property(PropertyNames.Length, 10, 1, 100),
            // Padrão: a cor primária atual do Paint.NET (o Outline Object abre sempre preto).
            new Int32Property(PropertyNames.Color, PrimaryColorArgb(), int.MinValue, int.MaxValue),
            new BooleanProperty(PropertyNames.AntiAlias, true),
            new Int32Property(PropertyNames.BlurRadius, 0, 0, 100),
        };
        return new PropertyCollection(props);
    }

    protected override ControlInfo OnCreateConfigUI(PropertyCollection props)
    {
        ControlInfo ui = CreateDefaultConfigUI(props);
        ui.SetPropertyControlType(PropertyNames.Color, PropertyControlType.ColorWheel);
        ui.SetPropertyControlValue(PropertyNames.Length, ControlInfoPropertyNames.DisplayName, "Espessura (px)");
        ui.SetPropertyControlValue(PropertyNames.Color, ControlInfoPropertyNames.DisplayName, "Cor");
        ui.SetPropertyControlValue(PropertyNames.AntiAlias, ControlInfoPropertyNames.DisplayName, "Suavizar bordas");
        ui.SetPropertyControlValue(PropertyNames.BlurRadius, ControlInfoPropertyNames.DisplayName, "Desfoque (px)");
        return ui;
    }

    /// <summary>Cor primária do Paint.NET como ARGB (0xAARRGGBB); preto opaco se não der para ler.</summary>
    private int PrimaryColorArgb()
    {
        try
        {
            // A cor primária é um ManagedColor: precisa do contexto de cor do documento para virar valores.
            ColorRgba128Float linear = Environment.PrimaryColor.Get(Environment.Document.ColorContext);
            ColorBgra32 c = ColorBgra32.Round(linear, MidpointRounding.AwayFromZero);
            return unchecked((int)(((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B));
        }
        catch
        {
            return unchecked((int)0xFF000000);
        }
    }

    protected override void OnInitializeRenderInfo(IBitmapEffectRenderInfo renderInfo)
    {
        var geometry = Environment.Selection.Geometry;
        imageSize = Environment.Document.Size;
        currentSignature = SelectionSignature.Of(geometry, imageSize);
        base.OnInitializeRenderInfo(renderInfo);
    }

    protected override unsafe void OnRender(IBitmapEffectOutput output)
    {
        int length = Token.GetProperty<Int32Property>(PropertyNames.Length).Value;
        uint argb = unchecked((uint)Token.GetProperty<Int32Property>(PropertyNames.Color).Value);
        bool antiAlias = Token.GetProperty<BooleanProperty>(PropertyNames.AntiAlias).Value;
        int blur = Token.GetProperty<Int32Property>(PropertyNames.BlurRadius).Value;

        MaskEntry mask = EnsureMask();
        RectInt32 reg = mask.Region;

        float colorA = ((argb >> 24) & 0xFF) / 255f;
        int cr = (int)((argb >> 16) & 0xFF), cg = (int)((argb >> 8) & 0xFF), cb = (int)(argb & 0xFF);

        RectInt32 rect = output.Bounds;

        // Cobertura só deste bloco (com margem para o desfoque), a partir da distância já calculada.
        int m = BandMath.Margin(blur);
        int ww = rect.Width + 2 * m;
        float[] cov = BandMath.CoverageWindow(mask.Depth, reg.Width, reg.Height,
                                              rect.Left - reg.Left - m, rect.Top - reg.Top - m,
                                              ww, rect.Height + 2 * m, length, antiAlias, blur);

        using IEffectInputBitmap<ColorBgra32> source = Environment.GetSourceBitmapBgra32();
        using IBitmapLock<ColorBgra32> src = source.Lock(rect);
        using IBitmapLock<ColorBgra32> dst = output.LockBgra32();

        for (int y = 0; y < rect.Height; y++)
        {
            ColorBgra32* s = (ColorBgra32*)((byte*)src.Buffer + (long)y * src.BufferStride);
            ColorBgra32* d = (ColorBgra32*)((byte*)dst.Buffer + (long)y * dst.BufferStride);
            int ry = rect.Top + y - reg.Top;

            for (int x = 0; x < rect.Width; x++)
            {
                ColorBgra32 p = s[x];
                int rx = rect.Left + x - reg.Left;
                if (rx < 0 || ry < 0 || rx >= reg.Width || ry >= reg.Height) { d[x] = p; continue; }

                float a = colorA * cov[(y + m) * ww + x + m];
                if (a <= 0f) { d[x] = p; continue; }

                // "Cor sobre o pixel" em alfa direto.
                float srcA = p.A / 255f;
                float outA = a + srcA * (1f - a);
                if (outA <= 0f) { d[x] = ColorBgra32.FromBgra(0, 0, 0, 0); continue; }
                float w1 = a / outA, w2 = srcA * (1f - a) / outA;
                d[x] = ColorBgra32.FromBgra(
                    ToByte(cb * w1 + p.B * w2), ToByte(cg * w1 + p.G * w2), ToByte(cr * w1 + p.R * w2), ToByte(outA * 255f));
            }
        }
    }

    // ---- Máscara da seleção (uma vez por seleção, cache estático) ----

    private MaskEntry EnsureMask()
    {
        lock (cacheGate)
        {
            if (cached != null && cached.Signature == currentSignature) return cached;

            RectInt32 region = Geometry.ContentBounds(currentSignature.Bounds, imageSize);
            float[] alpha = SelectionMask.Rasterize(Environment.Selection.Geometry, currentSignature.Bounds, region);
            float[] depth = EdgeDistance.InsideDepth(alpha, region.Width, region.Height);

            cached = new MaskEntry(currentSignature, region, depth);
            return cached;
        }
    }

    private static byte ToByte(float v) => (byte)(v < 0f ? 0 : (v > 255f ? 255 : v + 0.5f));
}
