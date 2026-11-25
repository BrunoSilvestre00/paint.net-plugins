using System;
using System.IO;
using System.Reflection;
using PaintDotNet.Imaging;
using PaintDotNet.Rendering;

namespace PluginShared;

/// <summary>Rotinas comuns aos plugins; este arquivo é compilado dentro de cada DLL (Compile Include).</summary>
internal static class ResourceIcon
{
    /// <summary>Carrega o 'icon.png' embutido na DLL do plugin como imagem do menu.</summary>
    public static System.Drawing.Image Load(Assembly assembly)
    {
        using Stream stream = assembly.GetManifestResourceStream("icon.png")!;
        using var raw = new System.Drawing.Bitmap(stream);
        return new System.Drawing.Bitmap(raw); // cópia: não depende do stream aberto
    }
}

internal static class Geometry
{
    /// <summary>Caixa da seleção em pixels inteiros, limitada à imagem; sem seleção vira a imagem inteira.</summary>
    public static RectInt32 ContentBounds(RectFloat selection, SizeInt32 imageSize)
    {
        var image = new RectInt32(0, 0, imageSize.Width, imageSize.Height);
        RectInt32 sel = RectInt32.Intersect(
            RectInt32.FromEdges((int)MathF.Floor(selection.Left), (int)MathF.Floor(selection.Top),
                                (int)MathF.Ceiling(selection.Right), (int)MathF.Ceiling(selection.Bottom)),
            image);
        return sel.HasArea ? sel : image;
    }
}

internal static class Sampling
{
    /// <summary>
    /// Interpolação bilinear em alfa pré-multiplicado (evita halos). (u, v) é a coordenada do
    /// canto do pixel; fora de 'area' é transparente. 'src' cobre exatamente 'area'.
    /// </summary>
    public static unsafe ColorBgra32 SampleBilinear(IBitmapLock<ColorBgra32> src, RectInt32 area, float u, float v)
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

    /// <summary>Preenche o bloco de saída com transparente.</summary>
    public static unsafe void Clear(IBitmapLock<ColorBgra32> dst, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            ColorBgra32* d = (ColorBgra32*)((byte*)dst.Buffer + (long)y * dst.BufferStride);
            for (int x = 0; x < w; x++) d[x] = ColorBgra32.FromBgra(0, 0, 0, 0);
        }
    }
}
