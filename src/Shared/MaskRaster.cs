using System;
using System.Collections.Generic;

namespace PluginShared;

/// <summary>
/// Rasteriza uma forma, dada só pela pergunta "este ponto está dentro?", em uma máscara de alfa (0..1) com
/// antialias. Pura (sem Paint.NET) para ser testada. O número de perguntas é o custo a evitar (no Paint.NET
/// cada uma é uma chamada à API de geometria): a grade é examinada em 3 níveis (células de 8 px, depois de
/// 4 px, depois pixel a pixel) e só se desce onde a célula tem borda. Interior e exterior grandes custam
/// ~N/64 perguntas.
///
/// Limitação: partes da forma mais finas que ~4 px que passem entre as amostras de uma célula (cantos e
/// centro) podem ser perdidas.
/// </summary>
internal static class MaskRaster
{
    private const int Big = 8, Small = 4;

    /// <param name="contains">Ponto (x, y) em coordenadas da imagem; (left+i, top+j) é o canto do pixel (i, j).</param>
    public static float[] Rasterize(Func<double, double, bool> contains, int left, int top, int w, int h)
    {
        // Amostras em coordenadas dobradas (gx = 2·x): inteiros pares são cantos de pixel, ímpares são centros.
        var memo = new Dictionary<long, bool>();
        bool At(int gx, int gy)
        {
            long key = ((long)gy << 32) | (uint)gx;
            if (!memo.TryGetValue(key, out bool v)) memo[key] = v = contains(left + gx / 2.0, top + gy / 2.0);
            return v;
        }

        var binary = new byte[w * h];

        // Nível 1: células de 8 px (4 cantos + centro).
        for (int cy = 0; cy < h; cy += Big)
            for (int cx = 0; cx < w; cx += Big)
            {
                int x1 = Math.Min(cx + Big, w), y1 = Math.Min(cy + Big, h);
                if (Uniform(At, cx, cy, x1, y1, out bool inside))
                {
                    if (inside) Fill(binary, w, cx, cy, x1, y1);
                    continue;
                }

                // Nível 2: sub-células de 4 px dentro de uma célula com borda.
                for (int sy = cy; sy < y1; sy += Small)
                    for (int sx = cx; sx < x1; sx += Small)
                    {
                        int ex = Math.Min(sx + Small, x1), ey = Math.Min(sy + Small, y1);
                        if (Uniform(At, sx, sy, ex, ey, out bool subInside))
                        {
                            if (subInside) Fill(binary, w, sx, sy, ex, ey);
                            continue;
                        }

                        // Nível 3: pixel a pixel (centro do pixel).
                        for (int y = sy; y < ey; y++)
                            for (int x = sx; x < ex; x++)
                                binary[y * w + x] = At(2 * x + 1, 2 * y + 1) ? (byte)1 : (byte)0;
                    }
            }

        // Antialias: pixels de borda (algum vizinho diferente) ganham cobertura parcial (3x3 amostras).
        var alpha = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = binary[y * w + x];
                bool edge = (x > 0 && binary[y * w + x - 1] != v) || (x < w - 1 && binary[y * w + x + 1] != v)
                         || (y > 0 && binary[(y - 1) * w + x] != v) || (y < h - 1 && binary[(y + 1) * w + x] != v);
                if (!edge) { alpha[y * w + x] = v; continue; }

                int hit = 0;
                for (int sy = 0; sy < 3; sy++)
                    for (int sx = 0; sx < 3; sx++)
                        if (contains(left + x + (sx + 0.5) / 3, top + y + (sy + 0.5) / 3)) hit++;
                alpha[y * w + x] = hit / 9f;
            }
        return alpha;
    }

    /// <summary>Os 4 cantos e o centro da célula dão a mesma resposta?</summary>
    private static bool Uniform(Func<int, int, bool> at, int x0, int y0, int x1, int y1, out bool inside)
    {
        // Coordenadas dobradas: canto (x0,y0) -> (2·x0, 2·y0); centro da célula -> (x0+x1, y0+y1).
        bool a = at(2 * x0, 2 * y0), b = at(2 * x1, 2 * y0), c = at(2 * x0, 2 * y1), d = at(2 * x1, 2 * y1), m = at(x0 + x1, y0 + y1);
        inside = a;
        return a == b && b == c && c == d && d == m;
    }

    private static void Fill(byte[] binary, int w, int x0, int y0, int x1, int y1)
    {
        for (int y = y0; y < y1; y++) Array.Fill(binary, (byte)1, y * w + x0, x1 - x0);
    }
}
