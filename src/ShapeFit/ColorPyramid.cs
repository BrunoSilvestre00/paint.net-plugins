using System;
using System.Collections.Generic;
using PaintDotNet.Imaging;
using PaintDotNet.Rendering;

namespace ShapeFit;

/// <summary>
/// Pirâmide de cores do objeto (alfa pré-multiplicado) para amostrar a cor onde o mapa de deformação
/// aponta. Dois usos: (1) escolher o nível pelo quanto o objeto é encolhido (evita serrilhado quando ele
/// é reduzido) e (2) "pull-push": se a amostra cai onde o objeto é transparente (borda imperfeita), sobe
/// de nível até achar cor, o que preenche a borda com a cor vizinha em vez de deixar buraco.
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

    /// <summary>Cor opaca do objeto no ponto (qx, qy) da camada; <paramref name="lod"/> = log2 do encolhimento.</summary>
    public ColorBgra32 SampleOpaque(double qx, double qy, double lod)
    {
        int level = Math.Clamp((int)Math.Round(lod) - shift, 0, levels.Count - 1);
        while (true)
        {
            double scale = 1 << (shift + level);
            float lx = (float)((qx - left) / scale - 0.5), ly = (float)((qy - top) / scale - 0.5);
            Bilinear(level, lx, ly, out float r, out float g, out float b, out float a);

            if (a >= 0.5f || level == levels.Count - 1)
            {
                if (a <= 1e-6f) return ColorBgra32.FromBgra(0, 0, 0, 255);
                float inv = 1f / a;
                return ColorBgra32.FromBgra(ToByte(b * inv), ToByte(g * inv), ToByte(r * inv), 255);
            }
            level++; // buraco: sobe de nível até achar cor ("pull-push")
        }
    }

    private static byte ToByte(float v) => (byte)(v < 0f ? 0 : (v > 255f ? 255 : v + 0.5f));

    private void Bilinear(int level, float x, float y, out float r, out float g, out float b, out float a)
    {
        var data = levels[level];
        int w = widths[level], h = heights[level];
        float cx = x < 0 ? 0 : (x > w - 1 ? w - 1 : x);
        float cy = y < 0 ? 0 : (y > h - 1 ? h - 1 : y);
        int x0 = (int)cx, y0 = (int)cy, x1 = x0 + 1 < w ? x0 + 1 : x0, y1 = y0 + 1 < h ? y0 + 1 : y0;
        float fx = cx - x0, fy = cy - y0;
        float w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
        int i00 = (y0 * w + x0) * 4, i10 = (y0 * w + x1) * 4, i01 = (y1 * w + x0) * 4, i11 = (y1 * w + x1) * 4;
        r = data[i00] * w00 + data[i10] * w10 + data[i01] * w01 + data[i11] * w11;
        g = data[i00 + 1] * w00 + data[i10 + 1] * w10 + data[i01 + 1] * w01 + data[i11 + 1] * w11;
        b = data[i00 + 2] * w00 + data[i10 + 2] * w10 + data[i01 + 2] * w01 + data[i11 + 2] * w11;
        a = data[i00 + 3] * w00 + data[i10 + 3] * w10 + data[i01 + 3] * w01 + data[i11 + 3] * w11;
    }
}
