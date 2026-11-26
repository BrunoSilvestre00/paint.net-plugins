using System;

namespace InlineObject;

/// <summary>
/// Distância precisa (subpixel) de cada pixel DENTRO da forma até o contorno dela, a partir de uma máscara
/// de alfa com antialias. Não depende do Paint.NET (para poder ser testada).
///
/// O contorno é o iso-contorno alfa = 0,5. Os pontos dele são os cruzamentos de 0,5 entre pixels vizinhos
/// (interpolação linear), o que serve igual para bordas suavizadas e para bordas duras. Depois a distância
/// até o ponto mais próximo é propagada por varreduras (8SSEDT, com vetores float).
/// </summary>
internal static class EdgeDistance
{
    private const float Far = 1e6f;

    /// <summary>
    /// Para cada pixel com alfa &gt;= 0,5: distância (px, do centro do pixel até o contorno). Fora: -1.
    /// Tudo além da máscara conta como fora, então uma forma que toca a borda da imagem tem contorno ali.
    /// </summary>
    public static float[] InsideDepth(float[] alpha, int w, int h)
    {
        // Moldura de 1 px com alfa 0 em volta (a máscara é uma região recortada).
        int pw = w + 2, ph = h + 2;
        var a = new float[pw * ph];
        for (int y = 0; y < h; y++) Array.Copy(alpha, y * w, a, (y + 1) * pw + 1, w);

        var vx = new float[pw * ph];
        var vy = new float[pw * ph];
        Array.Fill(vx, Far);
        Array.Fill(vy, Far);

        // 1. Pontos do contorno: cruzamentos de 0,5 entre vizinhos (direita e abaixo).
        for (int j = 0; j < ph; j++)
            for (int i = 0; i < pw; i++)
            {
                float s = a[j * pw + i] - 0.5f;
                if (i + 1 < pw) Cross(vx, vy, pw, i, j, 1, 0, s, a[j * pw + i + 1] - 0.5f);
                if (j + 1 < ph) Cross(vx, vy, pw, i, j, 0, 1, s, a[(j + 1) * pw + i] - 0.5f);
            }

        // 2. Propagação do ponto mais próximo (duas passadas, cada uma com duas varreduras).
        for (int j = 0; j < ph; j++)
        {
            for (int i = 0; i < pw; i++)
            {
                Relax(vx, vy, pw, ph, i, j, -1, 0);
                Relax(vx, vy, pw, ph, i, j, -1, -1);
                Relax(vx, vy, pw, ph, i, j, 0, -1);
                Relax(vx, vy, pw, ph, i, j, 1, -1);
            }
            for (int i = pw - 1; i >= 0; i--) Relax(vx, vy, pw, ph, i, j, 1, 0);
        }
        for (int j = ph - 1; j >= 0; j--)
        {
            for (int i = pw - 1; i >= 0; i--)
            {
                Relax(vx, vy, pw, ph, i, j, 1, 0);
                Relax(vx, vy, pw, ph, i, j, 1, 1);
                Relax(vx, vy, pw, ph, i, j, 0, 1);
                Relax(vx, vy, pw, ph, i, j, -1, 1);
            }
            for (int i = 0; i < pw; i++) Relax(vx, vy, pw, ph, i, j, -1, 0);
        }

        var depth = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = (y + 1) * pw + x + 1;
                depth[y * w + x] = a[p] >= 0.5f ? MathF.Sqrt(vx[p] * vx[p] + vy[p] * vy[p]) : -1f;
            }
        return depth;
    }

    /// <summary>Se o contorno (alfa 0,5) passa entre o pixel (i,j) e o vizinho (i+dx, j+dy), registra o ponto.</summary>
    private static void Cross(float[] vx, float[] vy, int pw, int i, int j, int dx, int dy, float s, float s2)
    {
        if (!((s >= 0f && s2 < 0f) || (s < 0f && s2 >= 0f))) return;

        float t = s / (s - s2); // posição do cruzamento no segmento centro->centro, de 0 a 1
        Min(vx, vy, j * pw + i, t * dx, t * dy);
        Min(vx, vy, (j + dy) * pw + i + dx, (t - 1f) * dx, (t - 1f) * dy);
    }

    private static void Min(float[] vx, float[] vy, int p, float x, float y)
    {
        if (x * x + y * y < vx[p] * vx[p] + vy[p] * vy[p]) { vx[p] = x; vy[p] = y; }
    }

    /// <summary>Se o vizinho (i+dx, j+dy) conhece um ponto do contorno mais perto de (i,j) que o atual, adota.</summary>
    private static void Relax(float[] vx, float[] vy, int pw, int ph, int i, int j, int dx, int dy)
    {
        int ni = i + dx, nj = j + dy;
        if (ni < 0 || nj < 0 || ni >= pw || nj >= ph) return;

        int q = nj * pw + ni, p = j * pw + i;
        float cx = vx[q] + dx, cy = vy[q] + dy; // vetor de (i,j) até o ponto conhecido por q
        if (cx * cx + cy * cy < vx[p] * vx[p] + vy[p] * vy[p]) { vx[p] = cx; vy[p] = cy; }
    }
}
