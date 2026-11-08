using System;

namespace RealGlass.Glass
{
    /// <summary>What <see cref="ThresholdDetect.Measure"/> found of one building asset's glass.</summary>
    public sealed class ThresholdResult
    {
        /// <summary>Why it could not be measured; "" when it was.</summary>
        public string Problem = "";

        public bool Measured;

        /// <summary>The shininess its glass needs, detected (0.40..0.95 in 0.05 steps); 0 when none (the asset keeps the global).</summary>
        public float Threshold;

        /// <summary>
        /// What a candidate found, whatever the global (0 none): what is remembered between loads.
        /// <see cref="Threshold"/> is it where it is not within a step of the global.
        /// </summary>
        public float Detected;

        /// <summary>The share of the facade at or over <see cref="Detected"/>.</summary>
        public float GlassShareDetected;

        /// <summary>
        /// <see cref="ThresholdDetect.SourceNone"/>, <see cref="ThresholdDetect.SourceNightMap"/> or
        /// <see cref="ThresholdDetect.SourceShininess"/>: what found <see cref="Detected"/>.
        /// </summary>
        public int Source;

        /// <summary>Square metres of upright surface measured, the points read on it.</summary>
        public float FacadeArea;
        public int Samples;

        /// <summary>The share of the facade the game lights at night (its windows).</summary>
        public float LitShare;

        /// <summary>Candidate A's balanced error at its threshold (1 when not tried).</summary>
        public float ErrorA = 1f;

        /// <summary>Candidate A's threshold before rounding, 0 when not tried.</summary>
        public float RawA;

        /// <summary>Candidate B's separability (between-class / total variance; 0 when not tried).</summary>
        public float Separability;

        /// <summary>Candidate B's threshold before rounding, 0 when not tried.</summary>
        public float RawB;

        /// <summary>The share of the facade at or over the global threshold, and over the detected one (the global's when none).</summary>
        public float GlassShareGlobal, GlassShareOwn;

        public string SourceName
        {
            get { return Source == ThresholdDetect.SourceNightMap ? "lit windows" : (Source == ThresholdDetect.SourceShininess ? "shininess" : "none"); }
        }
    }

    /// <summary>
    /// The shininess an asset's glass needs, read from its own textures: where its specular map parts
    /// its glass from its walls. The game (and the global "Shininess needed", 70%) takes one value for
    /// every asset; an asset whose walls are painted shiny gets glass walls, one whose windows are
    /// painted dull loses them.
    /// </summary>
    /// <remarks>
    /// <para>READ: the model's upright faces sampled as <see cref="RoomFit.Measure"/> samples them (its
    /// density, its seed), each point's shine (spec = 1 - _XYSMap.z) and illumination (_ACIMap.b).</para>
    /// <para>A, LABELLED BY THE NIGHT MAP: the texels the game lights at night (b over 0.48, not the
    /// never-lit 192) are windows. The threshold is the one that best parts them from the rest -- the
    /// least balanced error (walls taken for glass + windows missed, each as a share of its class);
    /// accepted when that error is small and the two classes' medians stand apart.</para>
    /// <para>B, THE SHINE ALONE (Otsu, "A threshold selection method from gray-level histograms", IEEE
    /// Trans. SMC 1979): the threshold that maximises the variance between the two classes of the
    /// shine over 0.20..1; accepted when the two are well separated (between / total variance), each
    /// holds a fair share of the facade and the shiny one is shiny.</para>
    /// <para>NONE is the common answer: the guards are there so that it changes a minority of assets,
    /// by a lot, never every asset by a little. Rounded to 0.05, and none within 0.05 of the global.</para>
    /// Pure: worker threads, the offline survey (docs/crpsurvey.ps1) and tools/test-settings.ps1 run it.
    /// </remarks>
    public static class ThresholdDetect
    {
        public const int SourceNone = 0, SourceNightMap = 1, SourceShininess = 2;

        /// <summary>A: the lit windows' share of the facade at least, the rest's at least.</summary>
        public const float MinLit = 0.02f, MinUnlit = 0.10f;

        /// <summary>A: the most balanced error accepted, and the least gap between the classes' median shines.</summary>
        public const float MaxError = 0.15f, MinMedianGap = 0.15f;

        /// <summary>
        /// B: the shine range Otsu splits, the least separability, each class's least share of the
        /// facade, the shiny class's least mean. Separability 0.8, not the plan's 0.6: ONE population
        /// already scores 0.64 (a normal one, 2 / pi) or 0.75 (a uniform one) -- Otsu would cut an
        /// asset's one kind of glass in half; two populations apart by four of their spreads score 0.8.
        /// </summary>
        public const float OtsuLo = 0.20f, MinSeparability = 0.8f, MinClass = 0.05f, MinGlassMean = 0.60f;

        /// <summary>The verdict's range and step; none within <see cref="Step"/> of the global.</summary>
        public const float Lowest = 0.40f, Highest = 0.95f, Step = 0.05f;

        /// <summary>The game's lit window (Interiors.cginc GameWindowLit): b over this; 192 never lights.</summary>
        public const int LitFrom = 123, NeverLit = 192;

        /// <summary>
        /// One asset. pos / uv / tri: its model in its own space; shine, illum: its shine and its
        /// illumination maps, side x side bytes each (row 0 at v = 0); st / clamp as RoomFit.Measure;
        /// global: the global threshold. Never throws.
        /// </summary>
        public static ThresholdResult Measure(float[] pos, float[] uv, int[] tri, byte[] shine, byte[] illum, int side,
                                              float stU, float stV, float offU, float offV, bool clamp, float global)
        {
            var r = new ThresholdResult();
            try
            {
                if (pos == null || pos.Length < 9 || tri == null || tri.Length < 3)
                    r.Problem = "no mesh";
                else if (uv == null || uv.Length / 2 < pos.Length / 3)
                    r.Problem = "no texture coordinates";
                else if (shine == null || side < 1 || shine.Length < side * side)
                    r.Problem = "no shine map";
                else
                {
                    var all = new double[256];
                    var lit = new double[256];
                    double area;
                    int samples;
                    Sample(pos, uv, tri, shine, illum != null && illum.Length >= side * side ? illum : null, side,
                           stU, stV, offU, offV, clamp, all, lit, out area, out samples);
                    r.FacadeArea = (float)area;
                    r.Samples = samples;
                    if (samples == 0)
                        r.Problem = "no upright faces";
                    else
                        Decide(r, all, lit, global);
                }
            }
            catch (Exception e)
            {
                r.Problem = e.GetType().Name + ": " + e.Message;
                r.Measured = false;
                r.Threshold = 0f;
                r.Source = SourceNone;
            }
            return r;
        }

        /// <summary>
        /// The verdict from the facade's histograms (256 bins of shine, area-weighted): all of it, and
        /// the part the game lights at night. Public for the tests.
        /// </summary>
        public static void Decide(ThresholdResult r, double[] all, double[] lit, float global)
        {
            r.Measured = true;
            double total = 0, totalLit = 0;
            for (int k = 0; k < 256; k++)
            {
                total += all[k];
                totalLit += lit[k];
            }
            if (!(total > 0))
            {
                r.Problem = "no upright faces";
                r.Measured = false;
                return;
            }
            r.LitShare = (float)(totalLit / total);
            int g = Bin(global);
            r.GlassShareGlobal = (float)(Above(all, g) / total);
            r.GlassShareOwn = r.GlassShareGlobal;

            // No specular map at all (every point 0): no glass by the game's rule; nothing to part.
            if (all[0] >= total)
                return;

            float t = 0f;
            int source = SourceNone;

            // A: labelled by the lit windows.
            double totalUnlit = total - totalLit;
            if (totalLit >= MinLit * total && totalUnlit >= MinUnlit * total)
            {
                // Over the whole range: the middle of the best run is the threshold, and a middle
                // outside Lowest..Highest is no glass threshold (survey, 2026-11-08: searched from
                // 0.30 up, 264 assets' best run began at 0.30 itself -- lit windows painted with no
                // shine to speak of, which the clamp would have turned into mirrors).
                int lo = 1, hi = 255;
                var e = new double[256];
                double best = double.MaxValue;
                for (int k = lo; k <= hi; k++)
                {
                    double unlitAbove = Above(all, k) - Above(lit, k);
                    double litBelow = totalLit - Above(lit, k);
                    e[k] = 0.5 * unlitAbove / totalUnlit + 0.5 * litBelow / totalLit;
                    if (e[k] < best)
                        best = e[k];
                }
                int at = Plateau(e, lo, hi, best, false);
                r.ErrorA = (float)best;
                r.RawA = at / 255f;
                var unlit = new double[256];
                for (int k = 0; k < 256; k++)
                    unlit[k] = all[k] - lit[k];
                double litMedian = Median(lit, totalLit);
                double gap = litMedian - Median(unlit, totalUnlit);
                if (best <= MaxError && Math.Abs(gap) >= MinMedianGap && InRange(at) && litMedian >= Lowest)
                {
                    t = at / 255f;
                    source = SourceNightMap;
                }
            }

            // B: Otsu on the shine alone.
            if (source == SourceNone)
            {
                int lo = Bin(OtsuLo);
                double n = 0, sum = 0, sumSq = 0;
                for (int k = lo; k < 256; k++)
                {
                    n += all[k];
                    sum += all[k] * k;
                    sumSq += all[k] * k * (double)k;
                }
                if (n > 0)
                {
                    double mean = sum / n, varT = sumSq / n - mean * mean;
                    var between = new double[256];
                    double best = -1, w0 = 0, s0 = 0;
                    for (int k = lo + 1; k < 256; k++)
                    {
                        // Class 0: bins lo .. k - 1; class 1: k .. 255.
                        w0 += all[k - 1];
                        s0 += all[k - 1] * (k - 1);
                        double w1 = n - w0;
                        if (w0 <= 0 || w1 <= 0)
                        {
                            between[k] = -1;
                            continue;
                        }
                        double m0 = s0 / w0, m1 = (sum - s0) / w1;
                        between[k] = (w0 / n) * (w1 / n) * (m1 - m0) * (m1 - m0);
                        if (between[k] > best)
                            best = between[k];
                    }
                    if (best > 0 && varT > 1e-9)
                    {
                        int at = Plateau(between, lo + 1, 255, best, true);
                        r.RawB = at / 255f;
                        r.Separability = (float)(best / varT);
                        double upper = Above(all, at), lower = n - upper;
                        double upperMean = 0;
                        for (int k = at; k < 256; k++)
                            upperMean += all[k] * k;
                        upperMean = upper > 0 ? upperMean / upper / 255.0 : 0;
                        if (r.Separability >= MinSeparability && upper >= MinClass * total && lower >= MinClass * total && upperMean >= MinGlassMean &&
                            InRange(at))
                        {
                            t = at / 255f;
                            source = SourceShininess;
                        }
                    }
                }
            }

            if (source == SourceNone)
                return;
            float rounded = Round(t);
            r.Detected = rounded;
            r.Source = source;
            r.GlassShareDetected = (float)(Above(all, Bin(rounded)) / total);
            if (!Applies(rounded, global))
                return;
            r.Threshold = rounded;
            r.GlassShareOwn = r.GlassShareDetected;
        }

        /// <summary>A candidate's bin rounds into <see cref="Lowest"/>..<see cref="Highest"/> (not clamped into it).</summary>
        private static bool InRange(int bin)
        {
            float t = bin / 255f;
            return t >= Lowest - Step * 0.5f && t < Highest + Step * 0.5f;
        }

        /// <summary>A detected threshold (0 none) is used under this global one: not within <see cref="Step"/> of it.</summary>
        public static bool Applies(float detected, float global)
        {
            return detected > 0f && Math.Abs(detected - global) >= Step - 1e-4f;
        }

        /// <summary>A detected threshold clamped to <see cref="Lowest"/>..<see cref="Highest"/> and rounded to <see cref="Step"/>.</summary>
        public static float Round(float t)
        {
            t = Math.Min(Math.Max(t, Lowest), Highest);
            return (float)Math.Round(Math.Round(t / Step) * Step, 2);
        }

        /// <summary>The first bin a shine of t falls in (spec = byte / 255 at or over t).</summary>
        private static int Bin(double t)
        {
            return Math.Min(255, Math.Max(0, (int)Math.Ceiling(t * 255.0 - 1e-6)));
        }

        private static double Above(double[] h, int k)
        {
            double s = 0;
            for (int i = Math.Max(0, k); i < 256; i++)
                s += h[i];
            return s;
        }

        private static double Median(double[] h, double total)
        {
            double half = total * 0.5, s = 0;
            for (int k = 0; k < 256; k++)
            {
                s += h[k];
                if (s >= half)
                    return k / 255.0;
            }
            return 1.0;
        }

        /// <summary>The middle of the run of bins (from the first) where v is at its best: a gap in the histogram is a plateau, and its middle the steadiest threshold.</summary>
        private static int Plateau(double[] v, int lo, int hi, double best, bool max)
        {
            double tol = Math.Abs(best) * 1e-9 + 1e-12;
            int first = -1;
            for (int k = lo; k <= hi; k++)
            {
                if (max ? v[k] >= best - tol : v[k] <= best + tol)
                {
                    first = k;
                    break;
                }
            }
            if (first < 0)
                return lo;
            int last = first;
            while (last + 1 <= hi && (max ? v[last + 1] >= best - tol : v[last + 1] <= best + tol))
                last++;
            return (first + last) / 2;
        }

        /// <summary>The facade's points (RoomFit's density and seed) into the histograms.</summary>
        private static void Sample(float[] pos, float[] uv, int[] tri, byte[] shine, byte[] illum, int side,
                                   float stU, float stV, float offU, float offV, bool clamp,
                                   double[] all, double[] lit, out double area, out int samples)
        {
            int vertices = pos.Length / 3;
            int tris = tri.Length / 3;
            area = 0;
            samples = 0;
            var areas = new double[tris];
            for (int t = 0; t < tris; t++)
            {
                int a = tri[3 * t], b = tri[3 * t + 1], c = tri[3 * t + 2];
                if (a < 0 || b < 0 || c < 0 || a >= vertices || b >= vertices || c >= vertices)
                    continue;
                double e1x = pos[3 * b] - pos[3 * a], e1y = pos[3 * b + 1] - pos[3 * a + 1], e1z = pos[3 * b + 2] - pos[3 * a + 2];
                double e2x = pos[3 * c] - pos[3 * a], e2y = pos[3 * c + 1] - pos[3 * a + 1], e2z = pos[3 * c + 2] - pos[3 * a + 2];
                double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (!(len > 1e-8) || Math.Abs(ny / len) >= RoomFit.Upright)
                    continue;
                areas[t] = 0.5 * len;
                area += 0.5 * len;
            }
            if (!(area > 1e-6))
                return;

            double density = Math.Min(RoomFit.Density, RoomFit.MaxSamples / area);
            double carry = 0, weight = 1.0 / density;
            uint seed = 2463534242u;
            for (int t = 0; t < tris; t++)
            {
                if (areas[t] <= 0)
                    continue;
                int a = tri[3 * t], b = tri[3 * t + 1], c = tri[3 * t + 2];
                carry += areas[t] * density;
                int n = (int)carry;
                carry -= n;
                for (int i = 0; i < n; i++)
                {
                    seed ^= seed << 13;
                    seed ^= seed >> 17;
                    seed ^= seed << 5;
                    double u1 = (seed & 0xFFFFFF) / 16777216.0;
                    seed ^= seed << 13;
                    seed ^= seed >> 17;
                    seed ^= seed << 5;
                    double u2 = (seed & 0xFFFFFF) / 16777216.0;
                    if (u1 + u2 > 1.0)
                    {
                        u1 = 1.0 - u1;
                        u2 = 1.0 - u2;
                    }
                    double w0 = 1.0 - u1 - u2;
                    double tu = (w0 * uv[2 * a] + u1 * uv[2 * b] + u2 * uv[2 * c]) * stU + offU;
                    double tv = (w0 * uv[2 * a + 1] + u1 * uv[2 * b + 1] + u2 * uv[2 * c + 1]) * stV + offV;
                    int texel = Texel(side, tu, tv, clamp);
                    int s = texel < 0 ? 0 : shine[texel];
                    all[s] += weight;
                    if (illum != null && texel >= 0)
                    {
                        int ib = illum[texel];
                        if (ib >= LitFrom && Math.Abs(ib - NeverLit) > 1)
                            lit[s] += weight;
                    }
                    samples++;
                }
            }
        }

        private static int Texel(int side, double u, double v, bool clamp)
        {
            if (double.IsNaN(u) || double.IsNaN(v) || double.IsInfinity(u) || double.IsInfinity(v))
                return -1;
            if (clamp)
            {
                u = Math.Min(Math.Max(u, 0.0), 1.0);
                v = Math.Min(Math.Max(v, 0.0), 1.0);
            }
            else
            {
                u -= Math.Floor(u);
                v -= Math.Floor(v);
            }
            int x = Math.Min(side - 1, (int)(u * side)), y = Math.Min(side - 1, (int)(v * side));
            return y * side + x;
        }
    }
}
