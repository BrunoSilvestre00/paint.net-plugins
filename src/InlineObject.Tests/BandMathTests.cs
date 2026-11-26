using System;
using ContourErosion;
using Xunit;
using Xunit.Abstractions;

namespace InlineObject.Tests;

public class BandMathTests
{
    private const int N = 240;
    private readonly ITestOutputHelper output;

    public BandMathTests(ITestOutputHelper output) { this.output = output; }

    /// <summary>Círculo com antialias analítico (alfa = cobertura do pixel).</summary>
    private static float[] Circle(double cx, double cy, double r)
    {
        var a = new float[N * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                double d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                a[y * N + x] = (float)Math.Clamp(r - d + 0.5, 0, 1);
            }
        return a;
    }

    private static float[] Depth(float[] alpha) => EdgeDistance.InsideDepth(alpha, N, N);

    [Fact]
    public void Depth_is_accurate_to_a_fraction_of_a_pixel_on_a_circle()
    {
        const double cx = 120, cy = 120, r = 100;
        float[] depth = Depth(Circle(cx, cy, r));

        double worst = 0, sum = 0; int n = 0;
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                double truth = r - Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                if (truth < 0.5 || truth > 60) continue;           // só dentro, longe do eixo medial
                double err = Math.Abs(depth[y * N + x] - truth);
                worst = Math.Max(worst, err); sum += err; n++;
            }
        output.WriteLine($"erro de profundidade: máx {worst:F3} px, média {sum / n:F3} px ({n} pixels)");
        Assert.True(worst <= 0.30, $"erro máximo {worst:F3}");
        Assert.True(sum / n <= 0.08, $"erro médio {sum / n:F3}");
    }

    [Fact]
    public void Depth_of_an_axis_aligned_hard_edge_is_exact()
    {
        // Metade esquerda opaca, borda dura entre as colunas 99 e 100 (sem pixels parciais).
        var alpha = new float[N * N];
        for (int y = 0; y < N; y++) for (int x = 0; x < 100; x++) alpha[y * N + x] = 1f;
        float[] depth = Depth(alpha);

        // Pixel da coluna 99 tem centro a 0,5 do contorno; coluna 98 a 1,5; ...
        foreach (int col in new[] { 99, 98, 90, 60 })
            Assert.InRange(depth[120 * N + col], 100 - col - 0.5f - 0.01f, 100 - col - 0.5f + 0.01f);
    }

    [Fact]
    public void Shape_touching_the_region_edge_gets_an_outline_there_too()
    {
        // Tudo opaco: o contorno é a própria borda da máscara (seleção = imagem toda).
        var alpha = new float[N * N]; Array.Fill(alpha, 1f);
        float[] depth = Depth(alpha);
        Assert.InRange(depth[120 * N + 0], 0.49f, 0.51f);          // encostado na esquerda
        Assert.InRange(depth[0 * N + 120], 0.49f, 0.51f);          // encostado em cima
        Assert.InRange(depth[120 * N + 5], 5.49f, 5.51f);
    }

    /// <summary>Soma da cobertura só dentro da seleção.</summary>
    private static double InsideSum(float[] cov, float[] alpha)
    {
        double s = 0;
        for (int i = 0; i < cov.Length; i++) s += cov[i] * alpha[i];
        return s;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(25)]
    public void Band_area_matches_the_geometry_of_a_ring(int length)
    {
        const double r = 100;
        float[] alpha = Circle(120, 120, r);
        float[] cov = BandMath.Coverage(Depth(alpha), N, N, length, antiAlias: true, blurRadius: 0);

        double expected = Math.PI * (r * r - (r - length) * (r - length));
        double actual = InsideSum(cov, alpha);

        // Erro medido como diferença de espessura (px): área / comprimento do contorno.
        double thicknessError = (actual - expected) / (2 * Math.PI * (r - length / 2.0));
        output.WriteLine($"L={length}: esperado {expected:F0}, obtido {actual:F0} -> erro de espessura {thicknessError:F3} px");
        Assert.InRange(thicknessError, -0.1, 0.1);
    }

    [Fact]
    public void Band_is_solid_near_the_edge_and_empty_deep_inside()
    {
        float[] alpha = Circle(120, 120, 100);
        float[] cov = BandMath.Coverage(Depth(alpha), N, N, 10, true, 0);

        // Em y = 120, x = 120 + 100 - k: profundidade ≈ k - 0,5... (pixel centro em x + 0,5)
        float At(int depthPx) => cov[120 * N + (120 + 100 - depthPx)];
        Assert.True(At(3) > 0.99f, $"a 3 px do contorno: {At(3)}");
        Assert.True(At(8) > 0.99f, $"a 8 px do contorno: {At(8)}");
        Assert.True(At(30) < 0.001f, $"a 30 px do contorno: {At(30)}");
        Assert.True(cov[120 * N + 120] < 0.001f, "centro");
    }

    [Fact]
    public void Without_antialias_the_band_is_binary()
    {
        float[] alpha = Circle(120, 120, 100);
        float[] cov = BandMath.Coverage(Depth(alpha), N, N, 10, false, 0);
        foreach (float v in cov) Assert.True(v == 0f || v == 1f);
    }

    [Fact]
    public void Antialias_produces_intermediate_values_on_the_inner_edge()
    {
        float[] alpha = Circle(120, 120, 100);
        float[] cov = BandMath.Coverage(Depth(alpha), N, N, 10, true, 0);
        int partial = 0;
        for (int i = 0; i < cov.Length; i++) if (alpha[i] >= 1f && cov[i] > 0.02f && cov[i] < 0.98f) partial++;
        Assert.True(partial > 100, $"pixels parciais na borda interna: {partial}");
    }

    [Fact]
    public void Blur_softens_the_band_but_keeps_it_solid_at_the_outline()
    {
        float[] alpha = Circle(120, 120, 100);
        float[] depth = Depth(alpha);
        float[] hard = BandMath.Coverage(depth, N, N, 10, true, 0);
        float[] soft = BandMath.Coverage(depth, N, N, 10, true, 12);

        // Mais gradual: pixels com valor intermediário aumentam bastante.
        int Mid(float[] c) { int n = 0; foreach (float v in c) if (v > 0.05f && v < 0.95f) n++; return n; }
        Assert.True(Mid(soft) > Mid(hard) * 2, $"intermediários {Mid(soft)} vs {Mid(hard)}");

        // Junto ao contorno continua praticamente sólido (a borda "continua" para fora ao desfocar).
        Assert.True(soft[120 * N + (120 + 100 - 1)] > 0.85f, $"junto ao contorno: {soft[120 * N + 219]}");
        // E ainda some bem lá no fundo.
        Assert.True(soft[120 * N + 120] < 0.01f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(30)]
    [InlineData(100)]
    public void Tile_by_tile_gives_the_same_result_as_the_whole_region(int blur)
    {
        float[] alpha = Circle(120, 120, 100);
        float[] depth = Depth(alpha);
        float[] whole = BandMath.Coverage(depth, N, N, 12, true, blur);

        int m = BandMath.Margin(blur);
        double worst = 0;
        // Blocos de 64 px, incluindo os que passam da região e os do canto.
        for (int ty = -32; ty < N; ty += 64)
            for (int tx = -32; tx < N; tx += 64)
            {
                int ww = 64 + 2 * m;
                float[] win = BandMath.CoverageWindow(depth, N, N, tx - m, ty - m, ww, 64 + 2 * m, 12, true, blur);
                for (int y = 0; y < 64; y++)
                    for (int x = 0; x < 64; x++)
                    {
                        int rx = tx + x, ry = ty + y;
                        if (rx < 0 || ry < 0 || rx >= N || ry >= N) continue;
                        worst = Math.Max(worst, Math.Abs(win[(y + m) * ww + x + m] - whole[ry * N + rx]));
                    }
            }
        output.WriteLine($"desfoque {blur}: margem {m}, maior diferença entre bloco e região inteira = {worst:E2}");
        Assert.True(worst < 1e-3, $"diferença {worst}");
    }

    [Fact]
    public void Everything_is_outside_when_depth_is_nonpositive()
    {
        var depth = new float[N * N]; // zero = fora
        float[] cov = BandMath.Coverage(depth, N, N, 10, true, 0);
        foreach (float v in cov) Assert.Equal(1f, v);
    }
}
