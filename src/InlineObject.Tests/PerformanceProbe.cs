using System;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace InlineObject.Tests;

/// <summary>Medições de tempo (informativas): onde está o custo para a prévia ao vivo.</summary>
public class PerformanceProbe
{
    private readonly ITestOutputHelper output;
    public PerformanceProbe(ITestOutputHelper output) { this.output = output; }

    private static float[] Disc(int n)
    {
        var a = new float[n * n];
        double c = n / 2.0, r = n * 0.45;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                double d = Math.Sqrt((x + 0.5 - c) * (x + 0.5 - c) + (y + 0.5 - c) * (y + 0.5 - c));
                a[y * n + x] = (float)Math.Clamp(r - d + 0.5, 0, 1);
            }
        return a;
    }

    [Theory(Skip = "Medição manual: tire o Skip para ver os tempos.")]
    [InlineData(1000)]
    [InlineData(2000)]
    [InlineData(4000)]
    public void Where_does_the_time_go(int n)
    {
        float[] alpha = Disc(n);
        var sw = Stopwatch.StartNew();
        float[] depth = EdgeDistance.InsideDepth(alpha, n, n);
        long tDepth = sw.ElapsedMilliseconds;

        sw.Restart();
        BandMath.Coverage(depth, n, n, 10, true, 0);
        long tCov = sw.ElapsedMilliseconds;

        sw.Restart();
        BandMath.Coverage(depth, n, n, 10, true, 20);
        long tBlur = sw.ElapsedMilliseconds;

        // Um bloco de 256x256 calculado sozinho (o que a prévia precisa por bloco).
        int m = BandMath.Margin(20);
        sw.Restart();
        BandMath.CoverageWindow(depth, n, n, n / 2 - 128 - m, 20 - m, 256 + 2 * m, 256 + 2 * m, 10, true, 20);
        long tTile = sw.ElapsedMilliseconds;

        output.WriteLine($"{n}x{n}: distância {tDepth} ms (uma vez por seleção) | cobertura global {tCov} ms | com desfoque 20 {tBlur} ms | UM bloco 256² com desfoque 20: {tTile} ms");
    }
}
