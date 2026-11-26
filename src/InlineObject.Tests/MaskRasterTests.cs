using System;
using PluginShared;
using Xunit;
using Xunit.Abstractions;

namespace InlineObject.Tests;

public class MaskRasterTests
{
    private readonly ITestOutputHelper output;
    public MaskRasterTests(ITestOutputHelper output) { this.output = output; }

    /// <summary>Referência: pixel a pixel, mesma regra de antialias (3x3 nas bordas), sem otimização.</summary>
    private static float[] BruteForce(Func<double, double, bool> contains, int left, int top, int w, int h)
    {
        var binary = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) binary[y * w + x] = contains(left + x + 0.5, top + y + 0.5);

        var alpha = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool v = binary[y * w + x];
                bool edge = (x > 0 && binary[y * w + x - 1] != v) || (x < w - 1 && binary[y * w + x + 1] != v)
                         || (y > 0 && binary[(y - 1) * w + x] != v) || (y < h - 1 && binary[(y + 1) * w + x] != v);
                if (!edge) { alpha[y * w + x] = v ? 1f : 0f; continue; }
                int hit = 0;
                for (int sy = 0; sy < 3; sy++)
                    for (int sx = 0; sx < 3; sx++)
                        if (contains(left + x + (sx + 0.5) / 3, top + y + (sy + 0.5) / 3)) hit++;
                alpha[y * w + x] = hit / 9f;
            }
        return alpha;
    }

    private static Func<double, double, bool> Counting(Func<double, double, bool> inner, out Func<long> calls)
    {
        long n = 0;
        calls = () => n;
        return (x, y) => { n++; return inner(x, y); };
    }

    private void Compare(string name, Func<double, double, bool> shape, int left, int top, int w, int h, double allowedDiffFraction = 0)
    {
        var counted = Counting(shape, out var calls);
        float[] fast = MaskRaster.Rasterize(counted, left, top, w, h);
        long fastCalls = calls();

        float[] slow = BruteForce(shape, left, top, w, h);
        int diff = 0; double maxDiff = 0;
        for (int i = 0; i < fast.Length; i++)
        {
            double d = Math.Abs(fast[i] - slow[i]);
            if (d > 1e-6) diff++;
            maxDiff = Math.Max(maxDiff, d);
        }

        long full = (long)w * h;
        output.WriteLine($"{name,-22} {w}x{h}: {fastCalls} consultas ({(double)fastCalls / full:P1} do pixel a pixel) | pixels diferentes: {diff}");
        Assert.True(diff <= allowedDiffFraction * full, $"{diff} pixels diferentes ({(double)diff / full:P3})");
    }

    [Fact]
    public void Matches_the_brute_force_raster_on_a_circle()
    {
        Compare("círculo", (x, y) => (x - 300) * (x - 300) + (y - 250) * (y - 250) <= 220.0 * 220.0, 80, 30, 440, 440);
    }

    [Fact]
    public void Matches_the_brute_force_raster_on_a_rotated_rectangle()
    {
        double c = Math.Cos(0.4), s = Math.Sin(0.4);
        Compare("retângulo girado", (x, y) =>
        {
            double u = (x - 300) * c + (y - 250) * s, v = -(x - 300) * s + (y - 250) * c;
            return Math.Abs(u) <= 200 && Math.Abs(v) <= 120;
        }, 40, 40, 520, 420);
    }

    [Fact]
    public void Matches_the_brute_force_raster_on_a_ring_with_a_hole()
    {
        // O buraco de 60 px de raio no meio não pode ser perdido pelas amostras grossas.
        Compare("anel (com buraco)", (x, y) =>
        {
            double d2 = (x - 250) * (x - 250) + (y - 250) * (y - 250);
            return d2 <= 200.0 * 200.0 && d2 >= 60.0 * 60.0;
        }, 0, 0, 500, 500);
    }

    [Fact]
    public void Matches_the_brute_force_raster_on_a_star()
    {
        var pts = new (double x, double y)[10];
        for (int i = 0; i < 10; i++)
        {
            double ang = -Math.PI / 2 + i * Math.PI / 5, r = i % 2 == 0 ? 230 : 95;
            pts[i] = (250 + r * Math.Cos(ang), 260 + r * Math.Sin(ang));
        }
        Compare("estrela", (x, y) =>
        {
            bool inside = false;
            for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++)
                if ((pts[i].y > y) != (pts[j].y > y) &&
                    x < (pts[j].x - pts[i].x) * (y - pts[i].y) / (pts[j].y - pts[i].y) + pts[i].x)
                    inside = !inside;
            return inside;
        }, 0, 0, 500, 500,
        // As pontas da estrela são mais finas que ~4 px: algumas amostras grossas as perdem (limitação documentada).
        allowedDiffFraction: 0.0005);
    }

    [Fact]
    public void Everything_selected_costs_almost_nothing()
    {
        // Sem seleção, a "seleção" é a imagem toda: não deve custar milhões de consultas.
        var counted = Counting((x, y) => true, out var calls);
        float[] alpha = MaskRaster.Rasterize(counted, 0, 0, 2000, 2000);
        output.WriteLine($"imagem inteira 2000x2000: {calls()} consultas");
        Assert.True(calls() < 2000L * 2000 / 20, $"{calls()} consultas");
        Assert.All(alpha, v => Assert.Equal(1f, v));
    }

    [Fact]
    public void Query_count_is_a_small_fraction_of_the_pixel_count_for_big_shapes()
    {
        var counted = Counting((x, y) => (x - 1000) * (x - 1000) + (y - 1000) * (y - 1000) <= 900.0 * 900.0, out var calls);
        MaskRaster.Rasterize(counted, 0, 0, 2000, 2000);
        double fraction = (double)calls() / (2000L * 2000);
        output.WriteLine($"círculo 2000x2000: {calls()} consultas ({fraction:P2})");
        // Antes (células de 4 px em toda a imagem) eram ~12,5%; agora o custo é o perímetro.
        Assert.True(fraction < 0.08, $"{fraction:P2} dos pixels");
    }
}
