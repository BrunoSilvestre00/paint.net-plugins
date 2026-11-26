using System;
using System.Collections.Generic;
using PaintDotNet.Imaging;
using PaintDotNet.Rendering;

namespace ShapeFit;

/// <summary>
/// Pirâmide de cores do objeto (alfa pré-multiplicado) para amostrar a cor onde o mapa de deformação
/// aponta. Serve para escolher o nível pelo quanto o objeto é encolhido (evita serrilhado). Não
/// preenche nada: fora do objeto a amostra é transparente.
/// </summary>
internal sealed class ColorPyramid
{
    private readonly List<float[]> levels = new();   // 4 floats por pixel: r·a, g·a, b·a, a
    private readonly List<int> widths = new(), heights = new();
    private readonly int left, top, shift;

    /// <param name="bgra">Camada inteira, 4 bytes por pixel (B, G, R, A).</param>
    /// <param name="layerWidth">Largura da camada em pixels.</param>
    /// <param name="crop">Região do objeto dentro da camada.</param>
    /// <param name="maxPixels">Acima disso o nível 0 já é uma versão reduzida (limita a memória).</param>
    public ColorPyramid(byte[] bgra, int layerWidth, RectInt32 crop, int maxPixels)
    {
        left = crop.Left;
        top = crop.Top;

        while ((long)Ceil(crop.Width, 1 << shift) * Ceil(crop.Height, 1 << shift) > maxPixels) shift++;
        int block = 1 << shift;
        int w0 = Ceil(crop.Width, block), h0 = Ceil(crop.Height, block);

        var l0 = new float[w0 * h0 * 4];
        for (int by = 0; by < h0; by++)
            for (int bx = 0; bx < w0; bx++)
            {
                float r = 0, g = 0, b = 0, a = 0; int n = 0;
                for (int y = by * block; y < Math.Min(crop.Height, (by + 1) * block); y++)
                    for (int x = bx * block; x < Math.Min(crop.Width, (bx + 1) * block); x++)
                    {
                        int i = ((crop.Top + y) * layerWidth + crop.Left + x) * 4;
                        float al = bgra[i + 3] / 255f;
                        b += bgra[i] * al; g += bgra[i + 1] * al; r += bgra[i + 2] * al; a += al; n++;
                    }
                int o = (by * w0 + bx) * 4;
                if (n > 0) { l0[o] = r / n; l0[o + 1] = g / n; l0[o + 2] = b / n; l0[o + 3] = a / n; }
            }
        levels.Add(l0); widths.Add(w0); heights.Add(h0);

        while (widths[^1] > 1 || heights[^1] > 1)
        {
            int pw = widths[^1], ph = heights[^1], nw = Ceil(pw, 2), nh = Ceil(ph, 2);
            var prev = levels[^1];
            var next = new float[nw * nh * 4];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    int x0 = x * 2, x1 = Math.Min(x0 + 1, pw - 1), y0 = y * 2, y1 = Math.Min(y0 + 1, ph - 1);
                    for (int c = 0; c < 4; c++)
                        next[(y * nw + x) * 4 + c] = 0.25f * (prev[(y0 * pw + x0) * 4 + c] + prev[(y0 * pw + x1) * 4 + c]
                                                            + prev[(y1 * pw + x0) * 4 + c] + prev[(y1 * pw + x1) * 4 + c]);
                }
            levels.Add(next); widths.Add(nw); heights.Add(nh);
        }
    }

    private static int Ceil(int a, int b) => (a + b - 1) / b;

    /// <summary>
    /// Amostra o objeto no ponto (qx, qy) da camada; <paramref name="lod"/> = log2 do encolhimento.
    /// Devolve a cor SEM alfa (r, g, b em 0..255, média só dos pixels reais da célula) e o alfa (0..1).
    /// Fora do objeto o alfa é 0: nada é preenchido nem inventado. Com lod = 0 e o ponto no centro de um
    /// pixel, devolve exatamente o pixel original.
    /// </summary>
    public void Sample(double qx, double qy, double lod, out float r, out float g, out float b, out float a)
    {
        int level = Math.Clamp((int)Math.Round(lod) - shift, 0, levels.Count - 1);
        double scale = 1 << (shift + level);
        Bilinear(level, (float)((qx - left) / scale - 0.5), (float)((qy - top) / scale - 0.5),
                 out float pr, out float pg, out float pb, out a);

        if (a <= 1e-6f) { r = g = b = 0f; a = 0f; return; }
        float inv = 1f / a; // premult / alfa
        r = pr * inv; g = pg * inv; b = pb * inv;
    }

    /// <summary>Bilinear em alfa pré-multiplicado; fora do nível conta como transparente (zero).</summary>
    private void Bilinear(int level, float x, float y, out float r, out float g, out float b, out float a)
    {
        var data = levels[level];
        int w = widths[level], h = heights[level];
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;

        r = g = b = a = 0f;
        for (int j = 0; j < 2; j++)
        {
            int yy = y0 + j;
            if (yy < 0 || yy >= h) continue;
            float wy = j == 0 ? 1 - fy : fy;
            for (int i = 0; i < 2; i++)
            {
                int xx = x0 + i;
                if (xx < 0 || xx >= w) continue;
                float wgt = wy * (i == 0 ? 1 - fx : fx);
                int o = (yy * w + xx) * 4;
                r += data[o] * wgt; g += data[o + 1] * wgt; b += data[o + 2] * wgt; a += data[o + 3] * wgt;
            }
        }
    }
}
