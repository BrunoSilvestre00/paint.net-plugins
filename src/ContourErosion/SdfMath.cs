using System;

namespace ContourErosion;

/// <summary>Matemática pura (sem dependência do Paint.NET), para poder ser testada isoladamente.</summary>
internal static class SdfMath
{
    /// <summary>Teto da profundidade (px): evita valores enormes na suavização.</summary>
    public const float MaxDepth = 1000f;

    /// <summary>
    /// SDF com precisão de subpixel a partir do alfa (0..1). Positivo dentro da forma,
    /// negativo fora, zero no contorno (alfa = 0,5).
    /// </summary>
    public static float[] BuildSignedDistance(float[] alpha, int w, int h)
    {
        const float Inf = 1e20f;
        int n = w * h;

        alpha = NormalizeAlpha(alpha);

        // Pixel parcial (0 < a < 1) é uma semente a distância 'a' (dentro) ou '1 - a' (fora)
        // do contorno; pixels totalmente opacos/transparentes são infinitos para o lado oposto.
        var inner = new float[n]; // distância até o exterior
        var outer = new float[n]; // distância até o interior
        for (int i = 0; i < n; i++)
        {
            float a = alpha[i];
            inner[i] = a >= 1f ? Inf : a * a;
            outer[i] = a <= 0f ? Inf : (1f - a) * (1f - a);
        }

        DistanceTransform2D(inner, w, h);
        DistanceTransform2D(outer, w, h);

        var sdf = new float[n];
        for (int i = 0; i < n; i++)
        {
            // Dentro: profundidade = D - 0.5. Fora: valor negativo equivalente. Contínuo em a = 0.5.
            sdf[i] = alpha[i] > 0.5f
                ? Math.Min(inner[i], MaxDepth) - 0.5f
                : -(Math.Min(outer[i], MaxDepth) - 0.5f);
        }
        return sdf;
    }

    /// <summary>
    /// Normaliza o alfa pelo nível de opacidade "cheio" da imagem. Muitas imagens têm o corpo em
    /// alfa 253/254 (ou translúcido de propósito); sem isso, todo pixel interior parece um pixel
    /// de borda e a forma inteira é comida. Usa o percentil 98 dos pixels visíveis como "opaco" e
    /// encaixa em 0/1 o que está a menos de 2% dos extremos.
    /// </summary>
    private static float[] NormalizeAlpha(float[] alpha)
    {
        var hist = new int[256];
        int visible = 0;
        foreach (float a in alpha)
        {
            int b = (int)MathF.Round(a * 255f);
            if (b > 0) { hist[b]++; visible++; }
        }
        if (visible == 0) return alpha;

        // Valor de alfa abaixo do qual estão 98% dos pixels visíveis.
        int target = (int)MathF.Ceiling(visible * 0.98f), acc = 0, refByte = 255;
        for (int b = 1; b < 256; b++)
        {
            acc += hist[b];
            if (acc >= target) { refByte = b; break; }
        }
        float reference = Math.Max(refByte, 1) / 255f;

        var result = new float[alpha.Length];
        for (int i = 0; i < alpha.Length; i++)
        {
            float a = Math.Min(alpha[i] / reference, 1f);
            result[i] = a >= 0.98f ? 1f : (a <= 0.02f ? 0f : a);
        }
        return result;
    }

    /// <summary>Transformada de distância euclidiana O(N) (Felzenszwalb); entra d² semente, sai distância.</summary>
    private static void DistanceTransform2D(float[] f, int w, int h)
    {
        int max = Math.Max(w, h);
        var col = new float[max];
        var tmp = new float[max];
        var v = new int[max];
        var z = new float[max + 1];

        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++) col[y] = f[y * w + x];
            Transform1D(col, tmp, v, z, h);
            for (int y = 0; y < h; y++) f[y * w + x] = tmp[y];
        }
        for (int y = 0; y < h; y++)
        {
            Array.Copy(f, y * w, col, 0, w);
            Transform1D(col, tmp, v, z, w);
            for (int x = 0; x < w; x++) f[y * w + x] = MathF.Sqrt(tmp[x]);
        }
    }

    private static void Transform1D(float[] src, float[] dst, int[] v, float[] z, int n)
    {
        int k = 0;
        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            float s;
            while (true)
            {
                int p = v[k];
                s = ((src[q] + (float)q * q) - (src[p] + (float)p * p)) / (2f * q - 2f * p);
                if (s <= z[k] && k > 0) { k--; continue; }
                break;
            }
            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = float.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            float dq = q - v[k];
            dst[q] = dq * dq + src[v[k]];
        }
    }

    /// <summary>Gaussiana aproximada por 3 passes de box blur separável de raio r.</summary>
    public static float[] GaussianApprox(float[] src, int w, int h, int r)
    {
        var a = (float[])src.Clone();
        var b = new float[a.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxBlur(a, b, w, h, r, horizontal: true);
            BoxBlur(b, a, w, h, r, horizontal: false);
        }
        return a;
    }

    private static void BoxBlur(float[] src, float[] dst, int w, int h, int r, bool horizontal)
    {
        int lines = horizontal ? h : w;
        int len = horizontal ? w : h;
        int stride = horizontal ? 1 : w;
        float inv = 1f / (2 * r + 1);

        for (int line = 0; line < lines; line++)
        {
            int start = horizontal ? line * w : line;
            double sum = 0;
            for (int i = -r; i <= r; i++) sum += src[start + Math.Clamp(i, 0, len - 1) * stride];
            for (int i = 0; i < len; i++)
            {
                dst[start + i * stride] = (float)(sum * inv);
                sum += src[start + Math.Min(i + r + 1, len - 1) * stride]
                     - src[start + Math.Max(i - r, 0) * stride];
            }
        }
    }
}
