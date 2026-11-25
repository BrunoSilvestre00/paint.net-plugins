using System;
using System.Collections.Generic;
using PaintDotNet;
using PaintDotNet.Effects;
using PaintDotNet.IndirectUI;
using PaintDotNet.Imaging;
using PaintDotNet.PropertySystem;
using PaintDotNet.Rendering;
using PluginShared;

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
    private const string SubMenu = "Distorção";
    private const double MaxTiltDegrees = 85.0;

    private enum PropertyNames { Angle, Axis }
    private enum SkewAxis { Horizontal, Vertical }

    private RectInt32 content;   // área de origem: bounding box da seleção
    private float cx, cy;        // centro da inclinação

    public SkewEffect()
        : base("Inclinação", ResourceIcon.Load(typeof(SkewEffect).Assembly), SubMenu,
               BitmapEffectOptions.Create() with { IsConfigurable = true })
    {
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

        content = Geometry.ContentBounds(Environment.Selection.Geometry.GetBounds(null), size);
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
            Sampling.Clear(dst, rect.Width, rect.Height);
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

                d[x] = Sampling.SampleBilinear(src, need, sx - 0.5f, sy - 0.5f);
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
}
