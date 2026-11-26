using System;
using System.Collections.Generic;
using System.Linq;
using ContourErosion;
using Xunit;
using Xunit.Abstractions;

namespace ShapeFit.Tests;

public class ShapeRegistrationTests
{
    private const int N = 256;
    private readonly ITestOutputHelper output;

    public ShapeRegistrationTests(ITestOutputHelper output) { this.output = output; }

    // ---- Formas (alfa 0..1 com antialias por supersampling 4x4) ----

    private delegate bool Inside(double x, double y);

    private static float[] Raster(Inside inside)
    {
        var a = new float[N * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                int hit = 0;
                for (int j = 0; j < 4; j++)
                    for (int i = 0; i < 4; i++)
                        if (inside(x + (i + 0.5) / 4, y + (j + 0.5) / 4)) hit++;
                a[y * N + x] = hit / 16f;
            }
        return a;
    }

    private static Inside Circle(double cx, double cy, double r) =>
        (x, y) => (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r;

    private static Inside Square(double cx, double cy, double half) =>
        (x, y) => Math.Abs(x - cx) <= half && Math.Abs(y - cy) <= half;

    private static Inside Rect(double cx, double cy, double hx, double hy) =>
        (x, y) => Math.Abs(x - cx) <= hx && Math.Abs(y - cy) <= hy;

    /// <summary>Elipse com semieixos a (ao longo do ângulo) e b.</summary>
    private static Inside Ellipse(double cx, double cy, double a, double b, double angleRad) => (x, y) =>
    {
        double dx = x - cx, dy = y - cy, c = Math.Cos(angleRad), s = Math.Sin(angleRad);
        double u = dx * c + dy * s, v = -dx * s + dy * c;
        return u * u / (a * a) + v * v / (b * b) <= 1;
    };

    private static Inside Heart(double cx, double cy, double s) => (x, y) =>
    {
        double u = (x - cx) / s, v = -(y - cy) / s + 0.1;
        double a = u * u + v * v - 1;
        return a * a * a - u * u * v * v * v <= 0;
    };

    private static Inside Star(double cx, double cy, double outer, double inner)
    {
        var pts = new List<(double x, double y)>();
        for (int i = 0; i < 10; i++)
        {
            double ang = -Math.PI / 2 + i * Math.PI / 5, r = i % 2 == 0 ? outer : inner;
            pts.Add((cx + r * Math.Cos(ang), cy + r * Math.Sin(ang)));
        }
        return (x, y) =>
        {
            bool c = false;
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
                if ((pts[i].y > y) != (pts[j].y > y) &&
                    x < (pts[j].x - pts[i].x) * (y - pts[i].y) / (pts[j].y - pts[i].y) + pts[i].x)
                    c = !c;
            return c;
        };
    }

    // ---- Medição ----

    private static float[] Sdf(float[] alpha) =>
        SdfMath.GaussianApprox(SdfMath.BuildSignedDistance(alpha, N, N), N, N, 1);

    private static double Area(float[] alpha) { double s = 0; foreach (float a in alpha) s += a; return s; }

    private static double Iou(float[] fixedSdf, float[] moving, ShapeRegistration.Field? u)
    {
        long inter = 0, union = 0;
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float mw = u.HasValue
                    ? ShapeRegistration.Sample(moving, N, N, x + u.Value.Ux[y * N + x], y + u.Value.Uy[y * N + x])
                    : moving[y * N + x];
                bool a = fixedSdf[y * N + x] > 0, b = mw > 0;
                if (a && b) inter++;
                if (a || b) union++;
            }
        return union == 0 ? 1 : (double)inter / union;
    }

    private record Result(double Before, double After, double Folded, double MeanAniso, double P95Aniso, double Outer, double Inner);

    private static Result Measure(string label, float[] f, float[] m, ShapeRegistration.Field u, double before, long ms, ITestOutputHelper output)
    {
        double after = Iou(f, m, u);
        double folded = ShapeRegistration.FoldedFraction(u);
        var (all, outer, inner) = ShapeRegistration.AnisotropyProfile(u, f, 10f);
        output.WriteLine($"{label,-36} IoU {before:P1} -> {after:P1} | dobras {folded:P2} | aniso média {all.meanAniso:F3} p95 {all.p95Aniso:F3} | borda {outer:F3} interior {inner:F3} | {ms} ms");
        return new Result(before, after, folded, all.meanAniso, all.p95Aniso, outer, inner);
    }

    /// <summary>
    /// Referência analítica para formas "em estrela" em torno do centro: cada raio é esticado
    /// uniformemente (p -> c + (p-c) · ρ_obj(θ)/ρ_alvo(θ)). Espalha a distorção pelo objeto todo.
    /// </summary>
    private static ShapeRegistration.Field RadialMap(Inside target, Inside obj, double cx, double cy)
    {
        double Rho(Inside s, double th)
        {
            double lo = 0, hi = N;
            for (int i = 0; i < 40; i++)
            {
                double mid = (lo + hi) / 2;
                if (s(cx + mid * Math.Cos(th), cy + mid * Math.Sin(th))) lo = mid; else hi = mid;
            }
            return lo;
        }
        var ux = new float[N * N]; var uy = new float[N * N];
        var ratio = new double[720];
        for (int i = 0; i < 720; i++) { double th = (i + 0.5) * Math.PI / 360; ratio[i] = Rho(obj, th) / Math.Max(1e-6, Rho(target, th)); }
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                double th = Math.Atan2(dy, dx); if (th < 0) th += 2 * Math.PI;
                double r = ratio[Math.Min(719, (int)(th / (2 * Math.PI) * 720))];
                ux[y * N + x] = (float)(dx * r - dx);
                uy[y * N + x] = (float)(dy * r - dy);
            }
        return new ShapeRegistration.Field(ux, uy, N, N);
    }

    /// <summary>Alinha o objeto ao alvo por momentos e depois registra com as opções dadas.</summary>
    private Result Run(string label, Inside target, Inside obj, Func<double, ShapeRegistration.Options> options, bool align = true)
    {
        float[] targetAlpha = Raster(target);
        Inside placed = obj;
        if (align)
        {
            var affine = ShapeRegistration.MomentsAlignment(targetAlpha, Raster(obj), N, N, N, N);
            placed = (x, y) => { var (qx, qy) = affine.Apply(x, y); return obj(qx, qy); };
        }

        float[] f = Sdf(targetAlpha), m = Sdf(Raster(placed));
        double before = Iou(f, m, null);
        double size = Math.Sqrt(Area(targetAlpha));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var u = ShapeRegistration.Register(f, m, N, N, options(size));
        sw.Stop();

        return Measure(label, f, m, u, before, sw.ElapsedMilliseconds, output);
    }

    // ---- A configuração escolhida: encaixe exato com pouca distorção ----
    // (Medições que levaram a ela, resumidas: força guiada pela borda + ARAP + gaussiana leve foi a de
    //  menor distorção em todos os pares; dar força ao objeto inteiro piorou; ARAP sem gaussiana diverge.)

    private static Func<double, ShapeRegistration.Options> Chosen(double fractionOfSize) =>
        size => ShapeRegistration.Default(fractionOfSize * size);

    private (string name, Inside target, Inside obj)[] Pairs => new (string, Inside, Inside)[]
    {
        ("círculo -> quadrado", Square(128, 128, 62), Circle(128, 128, 70)),
        ("círculo -> estrela", Star(128, 128, 100, 42), Circle(128, 128, 70)),
        ("círculo -> coração", Heart(128, 128, 70), Circle(128, 128, 62)),
        ("estrela -> círculo", Circle(128, 128, 70), Star(128, 128, 100, 42)),
    };

    [Fact(Skip = "Experimento: tire o Skip para recalibrar as opções (imprime IoU e distorção por configuração).")]
    public void Tuning_table()
    {
        var configs = new (string name, Func<double, ShapeRegistration.Options> opt)[]
        {
            ("atual ARAP.2x4 σ2%", s => new ShapeRegistration.Options((float)(0.02 * s), 10f, 1f, 0.2f, 4)),
            ("ARAP.1x2 σ2%", s => new ShapeRegistration.Options((float)(0.02 * s), 10f, 1f, 0.1f, 2)),
            ("ARAP.1x2 σ1.5% it×2", s => new ShapeRegistration.Options((float)(0.015 * s), 10f, 2f, 0.1f, 2)),
            ("ARAP.05x2 σ2% it×2", s => new ShapeRegistration.Options((float)(0.02 * s), 10f, 2f, 0.05f, 2)),
            ("sem ARAP σ2% (base)", s => new ShapeRegistration.Options((float)(0.02 * s), 10f, 1f)),
            ("ARAP.1x2 σ2% band14 it×2", s => new ShapeRegistration.Options((float)(0.02 * s), 14f, 2f, 0.1f, 2)),
        };
        foreach (var (cname, opt) in configs)
        {
            output.WriteLine($"=== {cname}");
            foreach (var (name, target, obj) in Pairs) Run($"  {name}", target, obj, opt);
        }
    }

    [Fact]
    public void Default_fits_the_outline_closely_without_folding()
    {
        foreach (var (name, target, obj) in Pairs)
        {
            var r = Run($"{name} (σ 2%)", target, obj, Chosen(0.02));
            Assert.True(r.After >= 0.97, $"{name}: IoU {r.After:P1}");
            Assert.True(r.Folded < 0.005, $"{name}: dobras {r.Folded:P2}");
        }
    }

    [Fact]
    public void Default_distorts_less_than_the_analytic_radial_stretch()
    {
        // Referência: cada raio esticado uniformemente a partir do centro (só vale para formas "em estrela").
        foreach (var (name, target, obj) in Pairs.Where(p => p.name.Contains("quadrado") || p.name.Contains("-> estrela")))
        {
            float[] f = Sdf(Raster(target)), m = Sdf(Raster(obj));
            var radial = Measure($"{name} REFERÊNCIA radial", f, m, RadialMap(target, obj, 128, 128), Iou(f, m, null), 0, output);
            var ours = Run($"{name} (σ 2%)", target, obj, Chosen(0.02));

            Assert.True(ours.MeanAniso < radial.MeanAniso, $"{name}: {ours.MeanAniso:F3} deveria ser < {radial.MeanAniso:F3}");
        }
    }

    [Fact]
    public void Distortion_is_spread_over_the_object_not_only_at_the_border()
    {
        foreach (var (name, target, obj) in Pairs)
        {
            var r = Run($"{name} (σ 2%)", target, obj, Chosen(0.02));
            // O interior não pode ficar quase sem deformação com tudo concentrado na borda (nem o contrário).
            Assert.True(r.Inner >= r.Outer * 0.4, $"{name}: borda {r.Outer:F3} x interior {r.Inner:F3}");
        }
    }

    [Fact]
    public void More_rigidity_means_less_distortion_and_a_looser_fit()
    {
        var target = Star(128, 128, 100, 42); var obj = Circle(128, 128, 70);
        var soft = Run("estrela σ 1%", target, obj, Chosen(0.01));
        var mid = Run("estrela σ 4%", target, obj, Chosen(0.04));
        var stiff = Run("estrela σ 8%", target, obj, Chosen(0.08));

        Assert.True(mid.MeanAniso <= soft.MeanAniso + 1e-6, $"{mid.MeanAniso:F3} vs {soft.MeanAniso:F3}");
        Assert.True(stiff.MeanAniso <= mid.MeanAniso + 1e-6, $"{stiff.MeanAniso:F3} vs {mid.MeanAniso:F3}");
        Assert.True(stiff.After <= soft.After + 1e-6);
    }

    // ---- Projeção para dentro do objeto (preenchimento por continuação da borda) ----

    [Fact]
    public void ProjectInside_moves_outside_points_to_just_inside_the_edge()
    {
        float[] sdf = Sdf(Raster(Circle(128, 128, 60)));

        foreach (var (px, py) in new[] { (128 + 70.0, 128.0), (128.0, 128 - 85.0), (128 + 50.0, 128 + 50.0), (128 - 66.0, 128.0) })
        {
            float x = (float)px - 0.5f, y = (float)py - 0.5f; // índice = coordenada - 0,5
            ShapeRegistration.ProjectInside(sdf, N, N, ref x, ref y, 1.5f);
            float depth = ShapeRegistration.Sample(sdf, N, N, x, y);
            output.WriteLine($"({px},{py}) -> profundidade {depth:F2}");
            Assert.InRange(depth, 1.0f, 2.0f);
        }
    }

    [Fact]
    public void ProjectInside_leaves_points_already_inside_alone()
    {
        float[] sdf = Sdf(Raster(Circle(128, 128, 60)));
        float x = 140, y = 120;
        ShapeRegistration.ProjectInside(sdf, N, N, ref x, ref y, 1.5f);
        Assert.Equal(140, x, 3);
        Assert.Equal(120, y, 3);
    }

    // ---- Alinhamento por momentos ----

    [Fact]
    public void Moments_alignment_matches_a_rotated_ellipse_to_a_circle_of_the_same_area_without_warping()
    {
        // Elipse girada x círculo: não é só rotação, mas as covariâncias casam depois do alinhamento.
        var target = Ellipse(128, 128, 90, 40, 0.0);
        var obj = Ellipse(90, 150, 90, 40, 0.8);   // mesma elipse, girada e deslocada

        float[] targetAlpha = Raster(target);
        var affine = ShapeRegistration.MomentsAlignment(targetAlpha, Raster(obj), N, N, N, N);
        Inside placed = (x, y) => { var (qx, qy) = affine.Apply(x, y); return obj(qx, qy); };

        double iou = Iou(Sdf(targetAlpha), Sdf(Raster(placed)), null);
        output.WriteLine($"elipse girada+deslocada, só momentos: IoU {iou:P1}");
        Assert.True(iou >= 0.97, $"IoU {iou:P1}");
    }

    [Fact]
    public void Moments_alignment_is_identity_for_identical_shapes()
    {
        float[] a = Raster(Heart(128, 128, 70));
        var t = ShapeRegistration.MomentsAlignment(a, a, N, N, N, N);
        Assert.InRange(t.M11, 0.999, 1.001);
        Assert.InRange(t.M22, 0.999, 1.001);
        Assert.InRange(Math.Abs(t.M12) + Math.Abs(t.M21), 0, 0.001);
        Assert.InRange(Math.Abs(t.Tx) + Math.Abs(t.Ty), 0, 0.01);
    }

    [Fact]
    public void Identical_shapes_stay_identical()
    {
        var r = Run("formas idênticas", Heart(128, 128, 70), Heart(128, 128, 70), Chosen(0.02));
        Assert.True(r.After >= 0.99, $"IoU {r.After:P1}");
        Assert.True(r.Folded == 0, $"dobras {r.Folded:P2}");
    }
}