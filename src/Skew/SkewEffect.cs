using System;
using System.Collections.Generic;
using System.IO;
using PaintDotNet;
using PaintDotNet.Effects;
using PaintDotNet.IndirectUI;
using PaintDotNet.Imaging;
using PaintDotNet.PropertySystem;
using PaintDotNet.Rendering;

namespace Skew;

public sealed class SkewInfo : IPluginSupportInfo
{
    public string DisplayName => "Inclinação";
    public string Author => "Bruno";
    public string Copyright => "Bruno";
    public Version Version => new Version(1, 0, 0, 0);
    public Uri? WebsiteUri => null;
}

/// <summary>
/// Inclina (cisalhamento afim / skew) o conteúdo da seleção em torno do seu centro.
/// A roleta define o ângulo de inclinação; o eixo escolhe se são as bordas verticais
/// (horizontal) ou as horizontais (vertical) que inclinam.
///
/// Limitação do Paint.NET: um efeito só escreve dentro da seleção. O conteúdo inclinado
/// que sairia da seleção é cortado; selecione uma área maior que a imagem para ter folga.
/// </summary>
[PluginSupportInfo(typeof(SkewInfo))]
[EffectCategory(EffectCategory.Effect)]
public sealed class SkewEffect : PropertyBasedBitmapEffect
{
    private const string SubMenu = "Distorcer";
    private const double MaxTiltDegrees = 85.0;

    private enum PropertyNames { Angle, Axis }
    private enum SkewAxis { Horizontal, Vertical }

    private RectInt32 content;   // área de origem: bounding box da seleção
    private float cx, cy;        // centro da inclinação

    public SkewEffect()
        : base("Inclinação", LoadIcon(), SubMenu, BitmapEffectOptions.Create() with { IsConfigurable = true })
    {
    }

    /// <summary>Ícone do menu, embutido na DLL (src\Skew\icon.png).</summary>
    private static System.Drawing.Image LoadIcon()
    {
        using Stream stream = typeof(SkewEffect).Assembly.GetManifestResourceStream("icon.png")!;
        using var raw = new System.Drawing.Bitmap(stream);
        return new System.Drawing.Bitmap(raw); // cópia: não depende do stream aberto
    }

    protected override PropertyCollection OnCreatePropertyCollection()
    {
        var props = new List<Property>
        {
            new DoubleProperty(PropertyNames.Angle, 15, -180, 180),
            StaticListChoiceProperty.CreateForEnum(PropertyNames.Axis, SkewAxis.Horizontal),
        };
        return new PropertyCollection(props);
    }

    protected override ControlInfo OnCreateConfigUI(PropertyCollection props)
    {
        ControlInfo ui = CreateDefaultConfigUI(props);
        ui.SetPropertyControlType(PropertyNames.Angle, PropertyControlType.AngleChooser);
        ui.SetPropertyControlValue(PropertyNames.Angle, ControlInfoPropertyNames.DisplayName, "Ângulo de inclinação");
        ui.SetPropertyControlValue(PropertyNames.Axis, ControlInfoPropertyNames.DisplayName, "Eixo");
        PropertyControlInfo axis = ui.FindControlForPropertyName(PropertyNames.Axis);
        axis.SetValueDisplayName(SkewAxis.Horizontal, "Horizontal");
        axis.SetValueDisplayName(SkewAxis.Vertical, "Vertical");
        return ui;
    }

    protected override void OnInitializeRenderInfo(IBitmapEffectRenderInfo renderInfo)
    {
        using IEffectInputBitmap<ColorBgra32> source = Environment.GetSourceBitmapBgra32();
        SizeInt32 size = source.Size;
        var image = new RectInt32(0, 0, size.Width, size.Height);

        RectFloat sel = Environment.Selection.Geometry.GetBounds(null);
        RectInt32 selBounds = RectInt32.Intersect(
            RectInt32.FromEdges((int)MathF.Floor(sel.Left), (int)MathF.Floor(sel.Top),
                                (int)MathF.Ceiling(sel.Right), (int)MathF.Ceiling(sel.Bottom)),
            image);

        content = selBounds.HasArea ? selBounds : image;
        cx = content.Left + content.Width / 2f;
        cy = content.Top + content.Height / 2f;

        base.OnInitializeRenderInfo(renderInfo);
    }

    protected override unsafe void OnRender(IBitmapEffectOutput output)
    {
        double angle = Token.GetProperty<DoubleProperty>(PropertyNames.Angle).Value;
        var axis = (SkewAxis)Token.GetProperty<StaticListChoiceProperty>(PropertyNames.Axis).Value;
        float t = (float)Math.Tan(FoldTilt(angle) * Math.PI / 180.0);
        bool horizontal = axis == SkewAxis.Horizontal;

        RectInt32 rect = output.Bounds;
        using IBitmapLock<ColorBgra32> dst = output.LockBgra32();

        // Região da origem necessária para este bloco (inversa do cisalhamento), com folga p/ bilinear.
        RectInt32 need = SourceNeeded(rect, t, horizontal);
        if (!need.HasArea)
        {
            Clear(dst, rect.Width, rect.Height);
            return;
        }

        using IEffectInputBitmap<ColorBgra32> source = Environment.GetSourceBitmapBgra32();
        using IBitmapLock<ColorBgra32> src = source.Lock(need);

        for (int y = 0; y < rect.Height; y++)
        {
            ColorBgra32* d = (ColorBgra32*)((byte*)dst.Buffer + (long)y * dst.BufferStride);
            float py = rect.Top + y + 0.5f;

            for (int x = 0; x < rect.Width; x++)
            {
                float px = rect.Left + x + 0.5f;
                float sx, sy;
                if (horizontal) { sx = px + (py - cy) * t; sy = py; }
                else            { sx = px; sy = py + (px - cx) * t; }

                d[x] = SampleBilinear(src, need, sx - 0.5f, sy - 0.5f);
            }
        }
    }

    /// <summary>Dobra o ângulo da roleta para a faixa de inclinação útil (-85°..85°).</summary>
    private static double FoldTilt(double deg)
    {
        if (deg > 90) deg -= 180;
        else if (deg < -90) deg += 180;
        return Math.Clamp(deg, -MaxTiltDegrees, MaxTiltDegrees);
    }

    private RectInt32 SourceNeeded(RectInt32 rect, float t, bool horizontal)
    {
        // A coordenada de origem é linear no bloco: os extremos estão nos cantos.
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i < 4; i++)
        {
            float px = (i & 1) == 0 ? rect.Left : rect.Right;
            float py = (i & 2) == 0 ? rect.Top : rect.Bottom;
            float sx = horizontal ? px + (py - cy) * t : px;
            float sy = horizontal ? py : py + (px - cx) * t;
            minX = Math.Min(minX, sx); maxX = Math.Max(maxX, sx);
            minY = Math.Min(minY, sy); maxY = Math.Max(maxY, sy);
        }
        var r = RectInt32.FromEdges((int)MathF.Floor(minX) - 2, (int)MathF.Floor(minY) - 2,
                                    (int)MathF.Ceiling(maxX) + 2, (int)MathF.Ceiling(maxY) + 2);
        return RectInt32.Intersect(r, content);
    }

    private static unsafe void Clear(IBitmapLock<ColorBgra32> dst, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            ColorBgra32* d = (ColorBgra32*)((byte*)dst.Buffer + (long)y * dst.BufferStride);
            for (int x = 0; x < w; x++) d[x] = ColorBgra32.FromBgra(0, 0, 0, 0);
        }
    }

    /// <summary>Interpolação bilinear em alfa pré-multiplicado (evita halos). Fora da área = transparente.</summary>
    private static unsafe ColorBgra32 SampleBilinear(IBitmapLock<ColorBgra32> src, RectInt32 area, float u, float v)
    {
        int x0 = (int)MathF.Floor(u), y0 = (int)MathF.Floor(v);
        float fx = u - x0, fy = v - y0;

        float a = 0, r = 0, g = 0, b = 0;
        for (int j = 0; j < 2; j++)
        {
            int yy = y0 + j;
            float wy = j == 0 ? 1f - fy : fy;
            if (yy < area.Top || yy >= area.Bottom) continue;
            ColorBgra32* row = (ColorBgra32*)((byte*)src.Buffer + (long)(yy - area.Top) * src.BufferStride);

            for (int i = 0; i < 2; i++)
            {
                int xx = x0 + i;
                if (xx < area.Left || xx >= area.Right) continue;
                float w = wy * (i == 0 ? 1f - fx : fx);
                ColorBgra32 p = row[xx - area.Left];
                float pa = p.A / 255f * w;
                a += pa;
                r += p.R * pa; g += p.G * pa; b += p.B * pa;
            }
        }

        if (a <= 0.0001f) return ColorBgra32.FromBgra(0, 0, 0, 0);
        float inv = 1f / a;
        return ColorBgra32.FromBgra(
            (byte)Math.Clamp(b * inv + 0.5f, 0f, 255f),
            (byte)Math.Clamp(g * inv + 0.5f, 0f, 255f),
            (byte)Math.Clamp(r * inv + 0.5f, 0f, 255f),
            (byte)Math.Clamp(a * 255f + 0.5f, 0f, 255f));
    }
}
