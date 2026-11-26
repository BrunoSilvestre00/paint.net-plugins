using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ShapeFit;

/// <summary>
/// Encaixe não-rígido de silhuetas: deforma um objeto (moving) até a silhueta dele coincidir com
/// a de outra forma (fixed), sem Paint.NET (para poder ser testado isoladamente).
///
/// Método: registro "demons" entre campos de distância com sinal (SDF, positivo dentro da forma),
/// em pirâmide, do grosso para o fino. Um SDF é suave e quase linear perto da borda, então a
/// diferença F - M dá, em pixels, o quanto a borda precisa andar e o gradiente dá a direção. A cada
/// iteração o campo de deslocamento recebe esse empurrão e depois é suavizado por uma gaussiana
/// (a regularização: quanto maior o sigma, mais "rígida" e orgânica a deformação).
/// </summary>
internal static class ShapeRegistration
{
    // Pixels (do nível) acima dos quais a diferença de SDF é limitada: evita saltos enormes longe da borda.
    private const float DiffClamp = 6f;
    // Passo máximo por iteração ≈ StepAlpha / 2 pixels (normalização de Thirion).
    private const float StepAlpha = 2f;
    // Suavização mínima (px do nível): abaixo disso a gaussiana não regulariza nada.
    private const float MinLevelSigma = 0.8f;

    private static readonly int[] IterationsPerLevel = { 90, 60, 45, 35, 25, 18, 14, 10, 8 };

    /// <summary>
    /// <see cref="SigmaPx"/>: suavização FÍSICA do deslocamento, em pixels do nível 0. Todos os níveis
    /// da pirâmide usam essa mesma escala física (σ do nível = SigmaPx / 2^nível), então a deformação
    /// é global e suave no objeto inteiro, em vez de ajustes locais na borda.
    /// <see cref="BandWidth"/>: se &gt; 0, o empurrão só age perto da borda (estilo Liquify); 0 = o objeto
    /// inteiro participa (deformação global).
    /// </summary>
    internal readonly record struct Options(float SigmaPx, float BandWidth = 0f, float IterationScale = 1f,
                                           float ArapStep = 0f, int ArapPasses = 4);

    /// <summary>
    /// Configuração escolhida nas medições (ver ShapeFit.Tests, Tuning_table): força guiada pela borda
    /// (faixa de 10 px), relaxamento ARAP (2 passes de 0,1 por iteração) e gaussiana leve. Menor distorção
    /// que a base sem ARAP na maioria dos pares, com IoU ≥ 97,9% mesmo no caso mais difícil (estrela -> círculo).
    /// Só o σ varia: mais σ = mais rígido (menos distorção, encaixe mais frouxo).
    /// </summary>
    public static Options Default(double sigmaPx) => new((float)sigmaPx, 10f, 1f, 0.1f, 2);

    /// <summary>Deslocamento por pixel (em pixels do nível 0): M(p + u(p)) ≈ F(p).</summary>
    internal readonly struct Field
    {
        public readonly float[] Ux, Uy;
        public readonly int Width, Height;
        public Field(float[] ux, float[] uy, int w, int h) { Ux = ux; Uy = uy; Width = w; Height = h; }
    }

    /// <summary>
    /// Registra <paramref name="moving"/> em <paramref name="fixedSdf"/>. Ambos são SDFs (em pixels,
    /// positivo dentro) na mesma grade w×h. <paramref name="sigma"/> é a suavização do deslocamento
    /// em pixels de cada nível.
    /// </summary>
    public static Field Register(float[] fixedSdf, float[] moving, int w, int h, Options options, int maxLevels = 9)
    {
        var fs = new List<(float[] data, int w, int h)> { (fixedSdf, w, h) };
        var ms = new List<(float[] data, int w, int h)> { (moving, w, h) };
        while (fs.Count < maxLevels && Math.Min(fs[^1].w, fs[^1].h) >= 40)
        {
            fs.Add(Downsample(fs[^1].data, fs[^1].w, fs[^1].h));
            ms.Add(Downsample(ms[^1].data, ms[^1].w, ms[^1].h));
        }

        float[]? ux = null, uy = null;
        for (int level = fs.Count - 1; level >= 0; level--)
        {
            var (f, lw, lh) = fs[level];
            var (m, _, _) = ms[level];

            if (ux == null || uy == null)
            {
                ux = new float[lw * lh];
                uy = new float[lw * lh];
            }
            else
            {
                (ux, uy) = Upsample(ux, uy, fs[level + 1].w, fs[level + 1].h, lw, lh);
            }

            int order = fs.Count - 1 - level; // 0 = nível mais grosso
            int iterations = Math.Max(1, (int)(IterationsPerLevel[Math.Min(order, IterationsPerLevel.Length - 1)] * options.IterationScale));
            // Mesma escala física em todos os níveis: σ(nível) = σ(nível 0) / 2^nível.
            // SigmaPx <= 0 desliga a gaussiana (só o relaxamento ARAP regulariza).
            float levelSigma = options.SigmaPx <= 0f ? 0f : Math.Max(MinLevelSigma, options.SigmaPx / (1 << level));
            Refine(f, m, lw, lh, ux, uy, iterations, levelSigma, options.BandWidth / (1 << level),
                   options.ArapStep, options.ArapPasses);
        }

        return new Field(ux!, uy!, w, h);
    }

    // ---- Uma etapa da pirâmide ----

    private static void Refine(float[] f, float[] m, int w, int h, float[] ux, float[] uy, int iterations, float sigma, float band,
                               float arapStep, int arapPasses)
    {
        // Gradiente do SDF fixo (não muda).
        var gfx = new float[w * h];
        var gfy = new float[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                gfx[y * w + x] = (Sample(f, w, h, x + 1, y) - Sample(f, w, h, x - 1, y)) * 0.5f;
                gfy[y * w + x] = (Sample(f, w, h, x, y + 1) - Sample(f, w, h, x, y - 1)) * 0.5f;
            }
        });

        bool banded = band > 0.5f;
        float invBand2 = banded ? 1f / (2f * band * band) : 0f;
        float invAlpha2 = 1f / (StepAlpha * StepAlpha);
        var kernel = sigma > 0f ? GaussianKernel(sigma) : null;
        var tmp = new float[w * h];
        var p = arapStep > 0f ? new float[4][] { new float[w * h], new float[w * h], new float[w * h], new float[w * h] } : null;

        for (int it = 0; it < iterations; it++)
        {
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float sx = x + ux[i], sy = y + uy[i];

                    float mw = Sample(m, w, h, sx, sy);
                    float gmx = (Sample(m, w, h, sx + 1, sy) - Sample(m, w, h, sx - 1, sy)) * 0.5f;
                    float gmy = (Sample(m, w, h, sx, sy + 1) - Sample(m, w, h, sx, sy - 1)) * 0.5f;

                    // Gradiente simétrico (fixo + móvel): converge mais rápido e é mais estável.
                    float gx = 0.5f * (gfx[i] + gmx), gy = 0.5f * (gfy[i] + gmy);
                    float g2 = gx * gx + gy * gy;
                    if (g2 < 1e-3f) continue;

                    float diff = Math.Clamp(f[i] - mw, -DiffClamp, DiffClamp);
                    float weight = banded ? MathF.Exp(-f[i] * f[i] * invBand2) : 1f;
                    float k = weight * diff / (g2 + diff * diff * invAlpha2);

                    ux[i] += k * gx;
                    uy[i] += k * gy;
                }
            });

            if (p != null)
                for (int pass = 0; pass < arapPasses; pass++)
                    ArapRelax(ux, uy, p, w, h, arapStep);

            if (kernel != null)
            {
                Blur(ux, tmp, w, h, kernel);
                Blur(uy, tmp, w, h, kernel);
            }
        }
    }

    /// <summary>
    /// Um passo de descida da energia ARAP, E = ∫ ‖J − R‖², com J = I + ∇u e R a rotação mais próxima
    /// de J (decomposição polar). Penaliza esticar/cisalhar/mudar área mas NÃO a rotação local, que é
    /// de graça: é o que faz a deformação espalhar-se pelo objeto inteiro em vez de ficar só na borda.
    /// Gradiente: ∂E/∂u = −2 div(J − R); o passo é u += step · div(J − R) (estável para step ≤ 0,25).
    /// </summary>
    private static void ArapRelax(float[] ux, float[] uy, float[][] p, int w, int h, float step)
    {
        float[] p11 = p[0], p12 = p[1], p21 = p[2], p22 = p[3];

        Parallel.For(0, h, y =>
        {
            int ym = y > 0 ? y - 1 : 0, yp = y < h - 1 ? y + 1 : h - 1;
            for (int x = 0; x < w; x++)
            {
                int xm = x > 0 ? x - 1 : 0, xp = x < w - 1 ? x + 1 : w - 1;
                int i = y * w + x;
                float dxx = (ux[y * w + xp] - ux[y * w + xm]) / (xp - xm);
                float dxy = (ux[yp * w + x] - ux[ym * w + x]) / (yp - ym);
                float dyx = (uy[y * w + xp] - uy[y * w + xm]) / (xp - xm);
                float dyy = (uy[yp * w + x] - uy[ym * w + x]) / (yp - ym);

                float a = 1 + dxx, b = dxy, c = dyx, d = 1 + dyy;

                // Rotação mais próxima de J = [[a,b],[c,d]]: ângulo de (a+d, c-b).
                float cs = a + d, sn = c - b;
                float n = MathF.Sqrt(cs * cs + sn * sn);
                if (n < 1e-6f) { cs = 1; sn = 0; } else { cs /= n; sn /= n; }

                p11[i] = a - cs;  p12[i] = b + sn;
                p21[i] = c - sn;  p22[i] = d - cs;
            }
        });

        Parallel.For(0, h, y =>
        {
            int ym = y > 0 ? y - 1 : 0, yp = y < h - 1 ? y + 1 : h - 1;
            for (int x = 0; x < w; x++)
            {
                int xm = x > 0 ? x - 1 : 0, xp = x < w - 1 ? x + 1 : w - 1;
                int i = y * w + x;
                float divX = (p11[y * w + xp] - p11[y * w + xm]) / (xp - xm) + (p12[yp * w + x] - p12[ym * w + x]) / (yp - ym);
                float divY = (p21[y * w + xp] - p21[y * w + xm]) / (xp - xm) + (p22[yp * w + x] - p22[ym * w + x]) / (yp - ym);
                ux[i] += step * divX;
                uy[i] += step * divY;
            }
        });
    }

    // ---- Amostragem com extensão fora da grade ----

    /// <summary>Bilinear. Fora da grade o SDF continua decaindo (a forma não passa da borda da grade).</summary>
    internal static float Sample(float[] a, int w, int h, float x, float y)
    {
        float cx = x < 0 ? 0 : (x > w - 1 ? w - 1 : x);
        float cy = y < 0 ? 0 : (y > h - 1 ? h - 1 : y);

        int x0 = (int)cx, y0 = (int)cy;
        int x1 = x0 + 1 < w ? x0 + 1 : x0, y1 = y0 + 1 < h ? y0 + 1 : y0;
        float fx = cx - x0, fy = cy - y0;

        float top = a[y0 * w + x0] * (1 - fx) + a[y0 * w + x1] * fx;
        float bot = a[y1 * w + x0] * (1 - fx) + a[y1 * w + x1] * fx;
        float v = top * (1 - fy) + bot * fy;

        float dx = x - cx, dy = y - cy;
        if (dx != 0f || dy != 0f) v -= MathF.Sqrt(dx * dx + dy * dy);
        return v;
    }

    /// <summary>
    /// Se (x, y) está fora do objeto (ou a menos de <paramref name="margin"/> pixels da borda), empurra o
    /// ponto para dentro, pelo gradiente do SDF, até ficar a <paramref name="margin"/> da borda. Usado na
    /// amostragem de cor: onde o encaixe aponta para fora do objeto, a cor correta é a da borda mais
    /// próxima (continuação do conteúdo), e não uma média borrada. Coordenadas em índices do SDF.
    /// </summary>
    public static void ProjectInside(float[] sdf, int w, int h, ref float x, ref float y, float margin, int iterations = 3)
    {
        for (int it = 0; it < iterations; it++)
        {
            float depth = Sample(sdf, w, h, x, y);
            if (depth >= margin - 0.05f) return;

            float gx = (Sample(sdf, w, h, x + 1, y) - Sample(sdf, w, h, x - 1, y)) * 0.5f;
            float gy = (Sample(sdf, w, h, x, y + 1) - Sample(sdf, w, h, x, y - 1)) * 0.5f;
            float n = MathF.Sqrt(gx * gx + gy * gy);
            if (n < 1e-3f) return; // sem direção (eixo medial): não há para onde empurrar

            float push = margin - depth;
            x += gx / n * push;
            y += gy / n * push;
        }
    }

    // ---- Pirâmide ----

    /// <summary>Reduz à metade; os valores de SDF (em pixels) também caem pela metade.</summary>
    private static (float[] data, int w, int h) Downsample(float[] src, int w, int h)
    {
        int nw = (w + 1) / 2, nh = (h + 1) / 2;
        var dst = new float[nw * nh];
        for (int y = 0; y < nh; y++)
        {
            int y0 = Math.Min(2 * y, h - 1), y1 = Math.Min(2 * y + 1, h - 1);
            for (int x = 0; x < nw; x++)
            {
                int x0 = Math.Min(2 * x, w - 1), x1 = Math.Min(2 * x + 1, w - 1);
                float s = src[y0 * w + x0] + src[y0 * w + x1] + src[y1 * w + x0] + src[y1 * w + x1];
                dst[y * nw + x] = s * 0.25f * 0.5f;
            }
        }
        return (dst, nw, nh);
    }

    /// <summary>Leva o deslocamento do nível grosso para o fino: interpola e dobra os valores.</summary>
    private static (float[] ux, float[] uy) Upsample(float[] cux, float[] cuy, int cw, int ch, int w, int h)
    {
        var ux = new float[w * h];
        var uy = new float[w * h];
        Parallel.For(0, h, y =>
        {
            float sy = (y - 0.5f) / 2f;
            for (int x = 0; x < w; x++)
            {
                float sx = (x - 0.5f) / 2f;
                ux[y * w + x] = 2f * SampleClamped(cux, cw, ch, sx, sy);
                uy[y * w + x] = 2f * SampleClamped(cuy, cw, ch, sx, sy);
            }
        });
        return (ux, uy);
    }

    /// <summary>Bilinear com a borda replicada (para campos de deslocamento).</summary>
    internal static float SampleClamped(float[] a, int w, int h, float x, float y)
    {
        float cx = x < 0 ? 0 : (x > w - 1 ? w - 1 : x);
        float cy = y < 0 ? 0 : (y > h - 1 ? h - 1 : y);
        int x0 = (int)cx, y0 = (int)cy;
        int x1 = x0 + 1 < w ? x0 + 1 : x0, y1 = y0 + 1 < h ? y0 + 1 : y0;
        float fx = cx - x0, fy = cy - y0;
        float top = a[y0 * w + x0] * (1 - fx) + a[y0 * w + x1] * fx;
        float bot = a[y1 * w + x0] * (1 - fx) + a[y1 * w + x1] * fx;
        return top * (1 - fy) + bot * fy;
    }

    // ---- Gaussiana separável ----

    private static float[] GaussianKernel(float sigma)
    {
        int r = Math.Max(1, (int)MathF.Ceiling(3f * sigma));
        var k = new float[2 * r + 1];
        float sum = 0;
        for (int i = -r; i <= r; i++) { k[i + r] = MathF.Exp(-(i * i) / (2f * sigma * sigma)); sum += k[i + r]; }
        for (int i = 0; i < k.Length; i++) k[i] /= sum;
        return k;
    }

    private static void Blur(float[] a, float[] tmp, int w, int h, float[] k)
    {
        int r = k.Length / 2;
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int j = -r; j <= r; j++)
                {
                    int xx = x + j; xx = xx < 0 ? 0 : (xx >= w ? w - 1 : xx);
                    s += a[row + xx] * k[j + r];
                }
                tmp[row + x] = s;
            }
        });
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int j = -r; j <= r; j++)
                {
                    int yy = y + j; yy = yy < 0 ? 0 : (yy >= h ? h - 1 : yy);
                    s += tmp[yy * w + x] * k[j + r];
                }
                a[y * w + x] = s;
            }
        });
    }

    // ---- Alinhamento inicial por momentos ----

    /// <summary>Transformação afim 2D: q = (M11 x + M12 y + Tx, M21 x + M22 y + Ty).</summary>
    internal readonly record struct Affine(double M11, double M12, double M21, double M22, double Tx, double Ty)
    {
        public static Affine Identity => new(1, 0, 0, 1, 0, 0);
        public (double x, double y) Apply(double x, double y) => (M11 * x + M12 * y + Tx, M21 * x + M22 * y + Ty);
    }

    /// <summary>Centro de massa, covariância (2x2) e massa de uma máscara de alfa. Coordenadas contínuas: o pixel i tem centro i + 0,5.</summary>
    internal readonly record struct MomentSet(double Cx, double Cy, double Sxx, double Sxy, double Syy, double Mass);

    internal static MomentSet Moments(float[] alpha, int w, int h)
    {
        double m = 0, mx = 0, my = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double a = alpha[y * w + x];
                if (a <= 0) continue;
                m += a; mx += a * (x + 0.5); my += a * (y + 0.5);
            }
        if (m <= 0) return new MomentSet(0, 0, 1, 0, 1, 0);
        double cx = mx / m, cy = my / m, sxx = 0, sxy = 0, syy = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double a = alpha[y * w + x];
                if (a <= 0) continue;
                double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                sxx += a * dx * dx; sxy += a * dx * dy; syy += a * dy * dy;
            }
        return new MomentSet(cx, cy, sxx / m, sxy / m, syy / m, m);
    }

    /// <summary>
    /// Afim que leva a forma do alvo (target) para a do objeto (source) casando centro de massa e
    /// covariância (escala e eixos principais), com a MENOR rotação possível: as raízes quadradas
    /// simétricas das covariâncias. É o melhor ponto de partida: sobra o mínimo para o warp deformar.
    /// </summary>
    public static Affine AlignmentFromMoments(MomentSet t, MomentSet o)
    {
        var (a, b, c, d) = Mul(SqrtSym(o.Sxx, o.Sxy, o.Syy), InvSqrtSym(t.Sxx, t.Sxy, t.Syy));
        // q = o_c + L (p - t_c)
        return new Affine(a, b, c, d, o.Cx - (a * t.Cx + b * t.Cy), o.Cy - (c * t.Cx + d * t.Cy));
    }

    public static Affine MomentsAlignment(float[] targetAlpha, float[] objectAlpha, int w, int h, int ow, int oh) =>
        AlignmentFromMoments(Moments(targetAlpha, w, h), Moments(objectAlpha, ow, oh));

    private static (double a, double b, double c, double d) Mul(
        (double a, double b, double c, double d) p, (double a, double b, double c, double d) q) =>
        (p.a * q.a + p.b * q.c, p.a * q.b + p.b * q.d, p.c * q.a + p.d * q.c, p.c * q.b + p.d * q.d);

    // Raiz quadrada de uma matriz simétrica positiva [[xx, xy],[xy, yy]] via autovalores.
    private static (double a, double b, double c, double d) SqrtSym(double xx, double xy, double yy) => Fn(xx, xy, yy, Math.Sqrt);
    private static (double a, double b, double c, double d) InvSqrtSym(double xx, double xy, double yy) => Fn(xx, xy, yy, v => 1.0 / Math.Sqrt(v));

    private static (double a, double b, double c, double d) Fn(double xx, double xy, double yy, Func<double, double> f)
    {
        double tr = xx + yy, det = xx * yy - xy * xy;
        double disc = Math.Sqrt(Math.Max(0, tr * tr / 4 - det));
        double l1 = Math.Max(tr / 2 + disc, 1e-9), l2 = Math.Max(tr / 2 - disc, 1e-9);
        double vx, vy; // autovetor de l1
        if (Math.Abs(xy) > 1e-12) { vx = l1 - yy; vy = xy; }
        else if (xx >= yy) { vx = 1; vy = 0; }
        else { vx = 0; vy = 1; }
        double n = Math.Sqrt(vx * vx + vy * vy); vx /= n; vy /= n;
        double f1 = f(l1), f2 = f(l2);
        // f(M) = f1 v v^T + f2 (I - v v^T)
        return (f2 + (f1 - f2) * vx * vx, (f1 - f2) * vx * vy, (f1 - f2) * vx * vy, f2 + (f1 - f2) * vy * vy);
    }

    // ---- Diagnóstico (usado nos testes) ----

    /// <summary>
    /// Distorção do mapa p -> p + u(p) DENTRO da forma alvo (F &gt; 0): anisotropia média e p95 de
    /// ln(σmax/σmin) do jacobiano (0 = só rotação/escala uniforme, sem esticar nem cisalhar).
    /// </summary>
    public static (double meanAniso, double p95Aniso) Anisotropy(Field u, float[] fixedSdf) =>
        AnisotropyProfile(u, fixedSdf, 10f).all;

    /// <summary>
    /// Igual a <see cref="Anisotropy"/>, mas separando a faixa externa (profundidade &lt; bandPx) do interior:
    /// mostra se a distorção está concentrada na borda ou espalhada pelo objeto.
    /// </summary>
    public static ((double meanAniso, double p95Aniso) all, double outerMean, double innerMean) AnisotropyProfile(Field u, float[] fixedSdf, float bandPx)
    {
        int w = u.Width, h = u.Height;
        var values = new List<double>();
        double outerSum = 0, innerSum = 0; int outerN = 0, innerN = 0;
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                if (fixedSdf[y * w + x] <= 0) continue;
                double a = 1 + (u.Ux[y * w + x + 1] - u.Ux[y * w + x - 1]) * 0.5;
                double b = (u.Ux[(y + 1) * w + x] - u.Ux[(y - 1) * w + x]) * 0.5;
                double c = (u.Uy[y * w + x + 1] - u.Uy[y * w + x - 1]) * 0.5;
                double d = 1 + (u.Uy[(y + 1) * w + x] - u.Uy[(y - 1) * w + x]) * 0.5;
                double e = (a * a + b * b + c * c + d * d) / 2, det = a * d - b * c;
                double s = Math.Sqrt(Math.Max(0, e * e - det * det));
                double smax = Math.Sqrt(e + s), smin = Math.Sqrt(Math.Max(1e-12, e - s));
                double aniso = Math.Log(smax / smin);
                values.Add(aniso);
                if (fixedSdf[y * w + x] < bandPx) { outerSum += aniso; outerN++; } else { innerSum += aniso; innerN++; }
            }
        if (values.Count == 0) return ((0, 0), 0, 0);
        values.Sort();
        double sum = 0; foreach (double v in values) sum += v;
        return ((sum / values.Count, values[(int)(values.Count * 0.95)]),
                outerN == 0 ? 0 : outerSum / outerN, innerN == 0 ? 0 : innerSum / innerN);
    }


    /// <summary>Fração de pixels onde o mapa p -> p + u(p) dobra sobre si mesmo (determinante do jacobiano ≤ 0).</summary>
    public static double FoldedFraction(Field u)
    {
        int w = u.Width, h = u.Height, bad = 0, total = 0;
        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                float dxx = (u.Ux[y * w + x + 1] - u.Ux[y * w + x - 1]) * 0.5f;
                float dxy = (u.Ux[(y + 1) * w + x] - u.Ux[(y - 1) * w + x]) * 0.5f;
                float dyx = (u.Uy[y * w + x + 1] - u.Uy[y * w + x - 1]) * 0.5f;
                float dyy = (u.Uy[(y + 1) * w + x] - u.Uy[(y - 1) * w + x]) * 0.5f;
                float det = (1 + dxx) * (1 + dyy) - dxy * dyx;
                total++;
                if (det <= 0.05f) bad++;
            }
        }
        return total == 0 ? 0 : (double)bad / total;
    }
}
