using System;
using System.Collections.Generic;
using PaintDotNet;
using PaintDotNet.Effects;
using PaintDotNet.IndirectUI;
using PaintDotNet.Imaging;
using PaintDotNet.PropertySystem;
using PaintDotNet.Rendering;
using PluginShared;

namespace Rotate;

public sealed class RotateInfo : IPluginSupportInfo
{
    public string DisplayName => "Rotação";
    public string Author => "Bruno";
    public string Copyright => "Bruno";
    public Version Version => new Version(1, 0, 0, 0);
    public Uri? WebsiteUri => null;
}

/// <summary>
/// Rotaciona o conteúdo da seleção por uma roleta de ângulo (positivo = anti-horário).
/// O pivô pode ser o centro da seleção ou o centro do objeto (caixa dos pixels visíveis),
/// o que evita que sprites com margem transparente girem "fora do eixo".
///
/// Limitação do Paint.NET: um efeito só escreve dentro da seleção. O que sair dela é cortado;
/// selecione uma área maior que a imagem para ter folga.
/// </summary>
[PluginSupportInfo(typeof(RotateInfo))]
[EffectCategory(EffectCategory.Effect)]
public sealed class RotateEffect : PropertyBasedBitmapEffect
{
    private const string SubMenu = "Distorção";

    private enum PropertyNames { Angle, Pivot }
    private enum PivotMode { Object, Selection }

    private RectInt32 content;            // área de origem: caixa da seleção
    private float selCx, selCy;           // centro da seleção
    private float objCx, objCy;           // centro do objeto (pixels com alfa > 0)

    public RotateEffect()
        : base("Rotação", ResourceIcon.Load(typeof(RotateEffect).Assembly), SubMenu,
               BitmapEffectOptions.Create() with { IsConfigurable = true })
    {
    }

    protected override PropertyCollection OnCreatePropertyCollection()
    {
        var props = new List<Property>
        {
            new DoubleProperty(PropertyNames.Angle, 15, -180, 180),
            StaticListChoiceProperty.CreateForEnum(PropertyNames.Pivot, PivotMode.Object),
        };
        return new PropertyCollection(props);
    }

    protected override ControlInfo OnCreateConfigUI(PropertyCollection props)
    {
        ControlInfo ui = CreateDefaultConfigUI(props);
        ui.SetPropertyControlType(PropertyNames.Angle, PropertyControlType.AngleChooser);
        ui.SetPropertyControlValue(PropertyNames.Angle, ControlInfoPropertyNames.DisplayName, "Ângulo de rotação");
        ui.SetPropertyControlValue(PropertyNames.Pivot, ControlInfoPropertyNames.DisplayName, "Pivô");
        PropertyControlInfo pivot = ui.FindControlForPropertyName(PropertyNames.Pivot);
        pivot.SetValueDisplayName(PivotMode.Object, "Centro do objeto");
        pivot.SetValueDisplayName(PivotMode.Selection, "Centro da seleção");
        return ui;
    }

    protected override unsafe void OnInitializeRenderInfo(IBitmapEffectRenderInfo renderInfo)
    {
        using IEffectInputBitmap<ColorBgra32> source = Environment.GetSourceBitmapBgra32();
        content = Geometry.ContentBounds(Environment.Selection.Geometry.GetBounds(null), source.Size);

        selCx = content.Left + content.Width / 2f;
        selCy = content.Top + content.Height / 2f;
        objCx = selCx;
        objCy = selCy;

        // Caixa dos pixels visíveis dentro da área de origem.
        using IBitmapLock<ColorBgra32> src = source.Lock(content);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < content.Height; y++)
        {
            ColorBgra32* row = (ColorBgra32*)((byte*)src.Buffer + (long)y * src.BufferStride);
            for (int x = 0; x < content.Width; x++)
            {
                if (row[x].A == 0) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX >= 0)
        {
            objCx = content.Left + (minX + maxX + 1) / 2f;
            objCy = content.Top + (minY + maxY + 1) / 2f;
        }

        base.OnInitializeRenderInfo(renderInfo);
    }

    protected override unsafe void OnRender(IBitmapEffectOutput output)
    {
        double angle = Token.GetProperty<DoubleProperty>(PropertyNames.Angle).Value;
        var pivot = (PivotMode)Token.GetProperty<StaticListChoiceProperty>(PropertyNames.Pivot).Value;
        float cx = pivot == PivotMode.Object ? objCx : selCx;
        float cy = pivot == PivotMode.Object ? objCy : selCy;

        // Positivo = anti-horário na tela (eixo Y para baixo). Mapa inverso: destino -> origem.
        float rad = (float)(angle * Math.PI / 180.0);
        float cos = MathF.Cos(rad), sin = MathF.Sin(rad);

        RectInt32 rect = output.Bounds;
        using IBitmapLock<ColorBgra32> dst = output.LockBgra32();

        RectInt32 need = SourceNeeded(rect, cx, cy, cos, sin);
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
            float dy = rect.Top + y + 0.5f - cy;

            for (int x = 0; x < rect.Width; x++)
            {
                float dx = rect.Left + x + 0.5f - cx;
                float sx = cx + dx * cos - dy * sin;
                float sy = cy + dx * sin + dy * cos;

                d[x] = Sampling.SampleBilinear(src, need, sx - 0.5f, sy - 0.5f);
            }
        }
    }

    private RectInt32 SourceNeeded(RectInt32 rect, float cx, float cy, float cos, float sin)
    {
        // O mapa é linear: os extremos da origem para este bloco estão nos cantos.
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i < 4; i++)
        {
            float dx = ((i & 1) == 0 ? rect.Left : rect.Right) - cx;
            float dy = ((i & 2) == 0 ? rect.Top : rect.Bottom) - cy;
            float sx = cx + dx * cos - dy * sin;
            float sy = cy + dx * sin + dy * cos;
            minX = Math.Min(minX, sx); maxX = Math.Max(maxX, sx);
            minY = Math.Min(minY, sy); maxY = Math.Max(maxY, sy);
        }
        var r = RectInt32.FromEdges((int)MathF.Floor(minX) - 2, (int)MathF.Floor(minY) - 2,
                                    (int)MathF.Ceiling(maxX) + 2, (int)MathF.Ceiling(maxY) + 2);
        return RectInt32.Intersect(r, content);
    }
}
