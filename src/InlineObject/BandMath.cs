using System;
using ContourErosion;

namespace InlineObject;

/// <summary>Matemática pura da borda interna (sem Paint.NET), para poder ser testada isoladamente.</summary>
internal static class BandMath
{
    /// <summary>
    /// Cobertura (0..1) da borda interna. <paramref name="depth"/> é o SDF da seleção (positivo dentro,
    /// distância em px até o contorno; &lt;= 0 fora). A borda é a banda de <paramref name="length"/> px a
    /// partir do contorno, para dentro. Fora da seleção a cobertura é 1 (a borda "continua"), de modo que o
    /// desfoque não enfraquece a borda junto ao contorno.
    /// </summary>
    public static float[] Coverage(float[] depth, int w, int h, int length, bool antiAlias, int blurRadius)
    {
        // Mesma semântica dos blocos: o que está fora da região conta como fora da seleção (cobertura 1),
        // então calcula com margem e recorta (com margem 0 não há desfoque e a janela já é a região).
        int m = Margin(blurRadius);
        if (m == 0) return CoverageWindow(depth, w, h, 0, 0, w, h, length, antiAlias, blurRadius);

        int ww = w + 2 * m;
        float[] win = CoverageWindow(depth, w, h, -m, -m, ww, h + 2 * m, length, antiAlias, blurRadius);
        var cov = new float[w * h];
        for (int y = 0; y < h; y++) Array.Copy(win, (y + m) * ww + m, cov, y * w, w);
        return cov;
    }

    /// <summary>
    /// Cobertura só de uma JANELA (wx, wy, ww, wh) em coordenadas da região (pode passar dos limites dela:
    /// ali conta como fora da seleção). Permite calcular bloco a bloco, em paralelo, sem recalcular a
    /// região inteira a cada mudança de parâmetro, o que mantém a prévia ao vivo fluida. O chamador deve
    /// incluir em volta do bloco uma margem de <see cref="Margin"/> px para o desfoque não ter borda.
    /// </summary>
    public static float[] CoverageWindow(float[] depth, int regionW, int regionH, int wx, int wy, int ww, int wh,
                                         int length, bool antiAlias, int blurRadius)
    {
        var cov = new float[ww * wh];
        for (int y = 0; y < wh; y++)
        {
            int ry = wy + y;
            for (int x = 0; x < ww; x++)
            {
                int rx = wx + x;
                float d = rx < 0 || ry < 0 || rx >= regionW || ry >= regionH ? 0f : depth[ry * regionW + rx];

                float c;
                if (d <= 0f) c = 1f;
                else if (antiAlias) c = Math.Clamp(length - d + 0.5f, 0f, 1f);
                else c = d <= length ? 1f : 0f;
                cov[y * ww + x] = c;
            }
        }

        return blurRadius > 0 ? SdfMath.GaussianApprox(cov, ww, wh, BoxRadiusFor(blurRadius / 2f)) : cov;
    }

    /// <summary>Margem (px) em volta de um bloco para o desfoque dar o mesmo resultado que na região inteira.</summary>
    public static int Margin(int blurRadius) => blurRadius <= 0 ? 0 : 3 * BoxRadiusFor(blurRadius / 2f) + 1;

    /// <summary>3 passes de box blur aproximam uma gaussiana: largura = sqrt(12σ²/3 + 1).</summary>
    internal static int BoxRadiusFor(float sigma)
    {
        float w = MathF.Sqrt(4f * sigma * sigma + 1f);
        return Math.Max(1, (int)MathF.Round((w - 1f) / 2f));
    }
}
