using PaintDotNet.Direct2D1;
using PaintDotNet.Rendering;
using PluginShared;

namespace InlineObject;

/// <summary>Ponte entre a geometria da seleção do Paint.NET e o rasterizador puro (<see cref="MaskRaster"/>).</summary>
internal static class SelectionMask
{
    public static float[] Rasterize(IGeometry geometry, RectFloat bounds, RectInt32 region) =>
        MaskRaster.Rasterize(
            (x, y) => x >= bounds.Left && x <= bounds.Right && y >= bounds.Top && y <= bounds.Bottom &&
                      geometry.FillContainsPoint(new Point2Float((float)x, (float)y), null, null),
            region.Left, region.Top, region.Width, region.Height);
}