using System;
using System.Collections.Generic;

namespace RealGlass.Glass
{
    /// <summary>
    /// One side of a building asset -- its upright faces turned one of <see cref="RoomFit.Sides"/>
    /// ways, in the asset's own space -- and the rhythm of its windows along it.
    /// </summary>
    public sealed class RoomFitSide
    {
        /// <summary>Square metres of upright surface measured on it.</summary>
        public float Area;

        /// <summary>A window rhythm was found along it.</summary>
        public bool Found;

        /// <summary>That rhythm's autocorrelation at its period.</summary>
        public float Rhythm;

        /// <summary>Metres from one window to the next.</summary>
        public float Module;

        /// <summary>A room's width: the whole number of modules nearest the room of the building's kind.</summary>
        public float Width;

        /// <summary>Where a room's side wall stands: metres along the side from the asset's middle, the middle of the wall between two windows.</summary>
        public float Phase;

        /// <summary>The share of it on faces narrower than <see cref="RoomFit.NarrowFace"/> (a round tower's facets).</summary>
        public float NarrowShare;

        public bool Narrow
        {
            get { return NarrowShare >= 0.5f; }
        }
    }

    /// <summary>What <see cref="RoomFit.Measure"/> found of one building asset's windows.</summary>
    public sealed class RoomFitResult
    {
        /// <summary>Why it could not be measured; "" when it was.</summary>
        public string Problem = "";

        public bool Measured;

        /// <summary>Square metres of upright surface measured, and the points read on it.</summary>
        public float UprightArea;
        public int Samples;

        /// <summary>The storey: metres from one row of windows to the next; 0 when there is no such rhythm.</summary>
        public float Storey;

        /// <summary>That rhythm's autocorrelation at its period.</summary>
        public float StoreyRhythm;

        /// <summary>Where a floor is: its height above the asset's base, modulo the storey.</summary>
        public float FloorPhase;

        /// <summary>The ground storey's top, the first floor of the storeys above (metres above the base); 0 when none.</summary>
        public float GroundTop;

        /// <summary>How much of a storey its windows take, metres (the rows of windows, folded over one storey).</summary>
        public float WindowBand;

        /// <summary>False: its glass holds no rooms (windows in strips under <see cref="RoomFit.MinWindow"/>).</summary>
        public bool Rooms = true;

        public readonly RoomFitSide[] Sides = new RoomFitSide[RoomFit.Sides];
        public int SidesFound, SidesNarrow;

        /// <summary>
        /// The shininess its glass needs, read from the same textures (ThresholdDetect, measured beside
        /// the fit since RoomFit.Version 2): null when not measured (an older cache line).
        /// </summary>
        public ThresholdResult Threshold;

        public RoomFitResult()
        {
            for (int k = 0; k < Sides.Length; k++)
                Sides[k] = new RoomFitSide();
        }
    }

    /// <summary>
    /// The rooms behind a building asset's glass fitted to ITS windows: the height of its storeys and
    /// where its floors are, and on each side how far apart its windows are and where the walls
    /// between them stand -- so a room's floor, ceiling and side walls fall where the asset's own
    /// walls are, not on a grid of the mod's that cuts through its windows (2026-10-21: of 544
    /// installed assets with a clear row of windows a storey, 28 had the grid's 3.8 m storeys).
    /// Pure: worker threads, the GPU preview, tools/test-settings.ps1 and the offline survey run it.
    /// </summary>
    /// <remarks>
    /// <para>READ: points spread evenly over the model's upright faces (25 a square metre, at most
    /// <see cref="MaxSamples"/>), each reading the asset's shine map (its _XYSMap's specular, the
    /// mask the glass goes by, read back from the GPU) where its uv falls; summed by height, and on
    /// each side by the distance along it, in <see cref="Bin"/> steps. Not thresholded: the rhythm
    /// of the shine is the rhythm of the windows whatever counts as glass.</para>
    /// <para>RHYTHM: the period of each profile is the lag of its strongest autocorrelation (as
    /// periods are found in speech: Rabiner, "On the use of autocorrelation analysis for pitch
    /// detection", IEEE Trans. ASSP 1977), the half or third of it when nearly as strong, refined to
    /// millimetres by epoch folding -- the period whose folded profile varies most (Leahy et al.,
    /// "On searches for pulsed emission", ApJ 1983): a storey wrong by a centimetre is a floor wrong
    /// by 30 cm thirty storeys up.</para>
    /// <para>PLACE: the profile folded at its period. A storey: its windows are the longest stretch
    /// shinier than half way (gaps under 0.3 m, a transom, bridged); the floor is under the sill,
    /// 0.9 m below the windows (less where the wall between them is short). The ground storey runs to
    /// the first floor from 2.4 m up where the storeys above begin -- where the profile matches the
    /// folded one (a lobby or a shop is taller). A side: the walls between its windows are the
    /// longest dull stretch; a room is as many windows wide as comes nearest the room of the
    /// building's kind.</para>
    /// <para>NO ROOMS: windows in strips under <see cref="MinWindow"/> a storey (the whole asset);
    /// a side mostly on faces narrower than <see cref="NarrowFace"/> (a round tower's facets: a
    /// room per facet is a stripe).</para>
    /// </remarks>
    public static class RoomFit
    {
        /// <summary>
        /// How it measures, as a number: what RoomFitCache remembers is measured again when this goes
        /// up. Raise it with ANY change to what Measure finds.
        /// </summary>
        public const int Version = 2;   // 2: the threshold measured beside the fit (ThresholdDetect), 2026-11-08

        /// <summary>The sides: upright faces by their heading in the asset's space, 22.5 degrees each, centred on 0, 22.5, 45...</summary>
        public const int Sides = 16;

        /// <summary>The profiles' step, metres.</summary>
        public const float Bin = 0.1f;

        /// <summary>The storeys looked for, metres.</summary>
        public const float MinStorey = 2.4f, MaxStorey = 6f;

        /// <summary>The window modules looked for along a side, metres.</summary>
        public const float MinModule = 0.8f, MaxModule = 8f;

        /// <summary>A rhythm counts from this autocorrelation.</summary>
        public const float MinRhythm = 0.3f;

        /// <summary>A folded profile flatter than this (shine 0..1) places nothing.</summary>
        public const float MinContrast = 0.04f;

        /// <summary>Windows under this tall, a storey: strips, no rooms.</summary>
        public const float MinWindow = 0.6f;

        /// <summary>A face narrower than this (all the faces turned its way) is too narrow for a room, metres.</summary>
        public const float NarrowFace = 3f;

        /// <summary>The ground storey's top: from this high, and no higher than this.</summary>
        public const float MinGround = 2.4f, MaxGround = 8f;

        /// <summary>The sill: the floor under a window, at most this far, metres.</summary>
        public const float Sill = 0.9f;

        /// <summary>Points read a square metre, and at most this many over the asset.</summary>
        public const float Density = 25f;
        public const int MaxSamples = 1500000;

        /// <summary>A face counts as upright within this of vertical (|n.y|): about 6 degrees.</summary>
        public const float Upright = 0.1f;

        /// <summary>A side smaller than this (square metres) is measured for no rhythm and counted for no report: a corner's bevel.</summary>
        public const float MinSideArea = 20f;

        /// <summary>
        /// A room's width by the building's kind (Interiors.Kind*), as Interiors.cginc lays the rooms
        /// out where nothing is fitted: offices 6 m, industry 8 m, the rest 3.6 m.
        /// </summary>
        public static float CellWidth(int kind)
        {
            return kind == 1 || kind == 7 ? 6f : (kind == 6 ? 8f : 3.6f);
        }

        /// <summary>
        /// Which side a heading belongs to: the atan2 of the normal's z and x in the asset's space, in
        /// <see cref="Sides"/> steps centred on 0. frac: where in its step (0..1, 0.5 its middle).
        /// Interiors.cginc's RoomFrameAt does the same.
        /// </summary>
        public static int SideOf(double nx, double nz, out double frac)
        {
            double u = Math.Atan2(nz, nx) / (2.0 * Math.PI) * Sides;
            double k = Math.Floor(u + 0.5);
            frac = u + 0.5 - k;
            int s = (int)k % Sides;
            return s < 0 ? s + Sides : s;
        }

        /// <summary>
        /// One asset measured. pos (x, y, z) / uv / tri: its model in its own space (y up from its
        /// base); shine: its shine map, side x side bytes (0..255, row 0 at v = 0); st: its
        /// _MainTex_ST (scale u, v, offset u, v), clamp: whether the map clamps (else wraps);
        /// cellWidth: the room of its kind (<see cref="CellWidth"/>). Never throws.
        /// </summary>
        public static RoomFitResult Measure(float[] pos, float[] uv, int[] tri, byte[] shine, int side,
                                            float stU, float stV, float offU, float offV, bool clamp, float cellWidth)
        {
            var r = new RoomFitResult();
            try
            {
                if (pos == null || pos.Length < 9 || tri == null || tri.Length < 3)
                    r.Problem = "no mesh";
                else if (uv == null || uv.Length / 2 < pos.Length / 3)
                    r.Problem = "no texture coordinates";
                else if (shine == null || side < 1 || shine.Length < side * side)
                    r.Problem = "no shine map";
                else
                    MeasureChecked(r, pos, uv, tri, shine, side, stU, stV, offU, offV, clamp, Math.Max(cellWidth, 1f));
            }
            catch (Exception e)
            {
                r.Problem = e.GetType().Name + ": " + e.Message;
                r.Measured = false;
            }
            return r;
        }

        private sealed class Profile
        {
            public double Lo;      // the coordinate where bin 0 starts
            public double[] Sum;
            public int[] Count;

            public Profile(double lo, double hi)
            {
                Lo = Math.Floor(lo / Bin) * Bin;
                int n = Math.Max(1, Math.Min(20000, (int)Math.Ceiling((hi - Lo) / Bin) + 1));
                Sum = new double[n];
                Count = new int[n];
            }

            public void Add(double x, double value)
            {
                int i = (int)((x - Lo) / Bin);
                if (i >= 0 && i < Sum.Length)
                {
                    Sum[i] += value;
                    Count[i]++;
                }
            }

            public double At(int i)
            {
                return Count[i] > 0 ? Sum[i] / Count[i] : double.NaN;
            }

            public double Centre(int i)
            {
                return Lo + (i + 0.5) * Bin;
            }
        }

        private static void MeasureChecked(RoomFitResult r, float[] pos, float[] uv, int[] tri, byte[] shine, int side,
                                           float stU, float stV, float offU, float offV, bool clamp, float cellWidth)
        {
            int vertices = pos.Length / 3;
            int tris = tri.Length / 3;

            // The upright faces: their normals, areas, sides, extents along their own tangents.
            var faces = new List<int>();
            var nxs = new float[tris];
            var nzs = new float[tris];
            var areas = new float[tris];
            var sideOf = new int[tris];
            var near = new int[tris];        // -1 none, else the neighbouring side it also counts for
            var headingBin = new int[tris];
            const int Headings = 720;        // half a degree
            var headLo = new double[Headings];
            var headHi = new double[Headings];
            for (int h = 0; h < Headings; h++)
            {
                headLo[h] = double.MaxValue;
                headHi[h] = double.MinValue;
            }
            var sideLo = new double[Sides];
            var sideHi = new double[Sides];
            for (int k = 0; k < Sides; k++)
            {
                sideLo[k] = double.MaxValue;
                sideHi[k] = double.MinValue;
            }
            double yLo = double.MaxValue, yHi = double.MinValue, area = 0;
            double nearBand = 2.0 / (360.0 / Sides);   // within 2 degrees of a side's edge: both sides'

            for (int t = 0; t < tris; t++)
            {
                int a = tri[3 * t], b = tri[3 * t + 1], c = tri[3 * t + 2];
                if (a < 0 || b < 0 || c < 0 || a >= vertices || b >= vertices || c >= vertices)
                    continue;
                double e1x = pos[3 * b] - pos[3 * a], e1y = pos[3 * b + 1] - pos[3 * a + 1], e1z = pos[3 * b + 2] - pos[3 * a + 2];
                double e2x = pos[3 * c] - pos[3 * a], e2y = pos[3 * c + 1] - pos[3 * a + 1], e2z = pos[3 * c + 2] - pos[3 * a + 2];
                double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (!(len > 1e-8))
                    continue;
                nx /= len;
                ny /= len;
                nz /= len;
                if (Math.Abs(ny) >= Upright)
                    continue;

                double l2 = Math.Sqrt(nx * nx + nz * nz);
                double tx = nz / l2, tz = -nx / l2;   // FacadeTangent: up x n, level
                double frac;
                int s = SideOf(nx, nz, out frac);
                int h = Math.Min(Headings - 1, Math.Max(0, (int)((Math.Atan2(nz, nx) + Math.PI) / (2.0 * Math.PI) * Headings)));
                faces.Add(t);
                nxs[t] = (float)nx;
                nzs[t] = (float)nz;
                areas[t] = (float)(0.5 * len);
                sideOf[t] = s;
                near[t] = frac < nearBand ? (s + Sides - 1) % Sides : (frac > 1.0 - nearBand ? (s + 1) % Sides : -1);
                headingBin[t] = h;
                area += 0.5 * len;

                foreach (int v in new[] { a, b, c })
                {
                    double y = pos[3 * v + 1], x = pos[3 * v] * tx + pos[3 * v + 2] * tz;
                    yLo = Math.Min(yLo, y);
                    yHi = Math.Max(yHi, y);
                    headLo[h] = Math.Min(headLo[h], x);
                    headHi[h] = Math.Max(headHi[h], x);
                    sideLo[s] = Math.Min(sideLo[s], x);
                    sideHi[s] = Math.Max(sideHi[s], x);
                    if (near[t] >= 0)
                    {
                        sideLo[near[t]] = Math.Min(sideLo[near[t]], x);
                        sideHi[near[t]] = Math.Max(sideHi[near[t]], x);
                    }
                }
            }

            r.UprightArea = (float)area;
            if (faces.Count == 0 || !(area > 1.0))
            {
                r.Problem = "no upright faces";
                return;
            }

            // How wide all the faces turned each way are together (a heading and its neighbours: one
            // flat side may straddle two half degrees): under NarrowFace, a facet of something round.
            var narrowAt = new bool[Headings];
            for (int h = 0; h < Headings; h++)
            {
                double lo = double.MaxValue, hi = double.MinValue;
                for (int d = -1; d <= 1; d++)
                {
                    int k = (h + d + Headings) % Headings;
                    lo = Math.Min(lo, headLo[k]);
                    hi = Math.Max(hi, headHi[k]);
                }
                narrowAt[h] = hi - lo < NarrowFace;
            }

            var vertical = new Profile(yLo, yHi);
            var along = new Profile[Sides];
            var sideArea = new double[Sides];
            var narrowArea = new double[Sides];
            for (int k = 0; k < Sides; k++)
            {
                if (sideHi[k] >= sideLo[k])
                    along[k] = new Profile(sideLo[k], sideHi[k]);
            }

            // Points over every upright face, as many as its area is worth.
            double density = Math.Min(Density, MaxSamples / area);
            double carry = 0;
            uint seed = 2463534242u;
            int samples = 0;
            for (int f = 0; f < faces.Count; f++)
            {
                int t = faces[f];
                int a = tri[3 * t], b = tri[3 * t + 1], c = tri[3 * t + 2];
                carry += areas[t] * density;
                int n = (int)carry;
                carry -= n;
                if (n == 0)
                    continue;

                double l2 = Math.Sqrt(nxs[t] * nxs[t] + nzs[t] * nzs[t]);
                double tx = nzs[t] / l2, tz = -nxs[t] / l2;
                int s = sideOf[t], s2 = near[t];
                double weight = 1.0 / density;
                sideArea[s] += n * weight;
                if (narrowAt[headingBin[t]])
                    narrowArea[s] += n * weight;
                if (s2 >= 0)
                {
                    sideArea[s2] += n * weight;
                    if (narrowAt[headingBin[t]])
                        narrowArea[s2] += n * weight;
                }

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
                    double px = w0 * pos[3 * a] + u1 * pos[3 * b] + u2 * pos[3 * c];
                    double py = w0 * pos[3 * a + 1] + u1 * pos[3 * b + 1] + u2 * pos[3 * c + 1];
                    double pz = w0 * pos[3 * a + 2] + u1 * pos[3 * b + 2] + u2 * pos[3 * c + 2];
                    double tu = (w0 * uv[2 * a] + u1 * uv[2 * b] + u2 * uv[2 * c]) * stU + offU;
                    double tv = (w0 * uv[2 * a + 1] + u1 * uv[2 * b + 1] + u2 * uv[2 * c + 1]) * stV + offV;
                    double value = Read(shine, side, tu, tv, clamp);

                    vertical.Add(py, value);
                    double x = px * tx + pz * tz;
                    if (along[s] != null)
                        along[s].Add(x, value);
                    if (s2 >= 0 && along[s2] != null)
                        along[s2].Add(x, value);
                    samples++;
                }
            }
            r.Samples = samples;
            r.Measured = true;

            // The storeys.
            double period, rhythm;
            double[] folded;
            if (Rhythm(vertical, MinStorey, MaxStorey, out period, out rhythm, out folded))
            {
                double bottom, height, wallMiddle, wallLength;
                if (Bands(folded, period, 0.25, out bottom, out height, out wallMiddle, out wallLength))
                {
                    r.Storey = (float)period;
                    r.StoreyRhythm = (float)rhythm;
                    r.WindowBand = (float)height;
                    double floor = bottom - Math.Min(Sill, 0.75 * (period - height));
                    floor -= period * Math.Floor(floor / period);
                    r.FloorPhase = (float)floor;
                    r.GroundTop = (float)GroundTop(vertical, folded, period, floor, yHi);
                    if (height < MinWindow)
                        r.Rooms = false;
                }
                else
                {
                    r.StoreyRhythm = (float)rhythm;
                }
            }

            // Each side.
            for (int k = 0; k < Sides; k++)
            {
                RoomFitSide fs = r.Sides[k];
                fs.Area = (float)sideArea[k];
                fs.NarrowShare = sideArea[k] > 0 ? (float)(narrowArea[k] / sideArea[k]) : 0f;
                if (fs.Narrow && fs.Area >= MinSideArea)
                    r.SidesNarrow++;   // a narrow bevel of a square metre keeps its flag (no room there), but is no side to speak of
                if (along[k] == null || sideArea[k] < MinSideArea)
                    continue;

                if (!Rhythm(along[k], MinModule, MaxModule, out period, out rhythm, out folded))
                {
                    fs.Rhythm = (float)rhythm;
                    continue;
                }
                double bottom, height, wallMiddle, wallLength;
                if (!Bands(folded, period, 0.5, out bottom, out height, out wallMiddle, out wallLength))
                    continue;
                fs.Found = true;
                fs.Rhythm = (float)rhythm;
                fs.Module = (float)period;
                fs.Width = (float)(period * Math.Max(1.0, Math.Round(cellWidth / period)));
                fs.Phase = (float)wallMiddle;
                r.SidesFound++;
            }
        }

        /// <summary>The shine map at (u, v), 0..1: the texel it falls in, wrapped or clamped.</summary>
        private static double Read(byte[] shine, int side, double u, double v, bool clamp)
        {
            if (double.IsNaN(u) || double.IsNaN(v) || double.IsInfinity(u) || double.IsInfinity(v))
                return 0.0;
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
            return shine[y * side + x] / 255.0;
        }

        /// <summary>
        /// A profile's rhythm between two periods (metres): its period refined, the autocorrelation
        /// there, and the profile folded over it (Fold). False when there is none worth the name.
        /// </summary>
        private static bool Rhythm(Profile p, double minPeriod, double maxPeriod, out double period, out double rhythm, out double[] folded)
        {
            period = 0;
            rhythm = 0;
            folded = null;
            int n = p.Sum.Length;
            int minLag = (int)Math.Round(minPeriod / Bin), maxLag = (int)Math.Round(maxPeriod / Bin);
            if (n < 2 * minLag)
                return false;

            double mean = 0;
            int valid = 0;
            for (int i = 0; i < n; i++)
            {
                if (p.Count[i] > 0)
                {
                    mean += p.At(i);
                    valid++;
                }
            }
            if (valid < 2 * minLag)
                return false;
            mean /= valid;

            var r = new double[maxLag + 2];
            double best = -2;
            int bestLag = 0;
            for (int lag = minLag; lag <= maxLag && lag < n; lag++)
            {
                double num = 0, d1 = 0, d2 = 0;
                int pairs = 0;
                for (int i = 0; i + lag < n; i++)
                {
                    if (p.Count[i] == 0 || p.Count[i + lag] == 0)
                        continue;
                    double x = p.At(i) - mean, z = p.At(i + lag) - mean;
                    num += x * z;
                    d1 += x * x;
                    d2 += z * z;
                    pairs++;
                }
                r[lag] = pairs >= Math.Max(10, (int)(1.5 * lag)) && d1 > 1e-12 && d2 > 1e-12 ? num / Math.Sqrt(d1 * d2) : -2;
                if (r[lag] > best)
                {
                    best = r[lag];
                    bestLag = lag;
                }
            }
            rhythm = Math.Max(best, 0);
            if (bestLag == 0 || best < MinRhythm)
                return false;

            // Twice or three times the period can be as strong (every other storey alike): the
            // shortest that nearly is.
            foreach (int d in new[] { 3, 2 })
            {
                int lag = (int)Math.Round(bestLag / (double)d);
                if (lag < minLag)
                    continue;
                int there = lag;
                for (int l = lag - 1; l <= lag + 1; l++)
                {
                    if (l >= minLag && l <= maxLag && l < n && r[l] > r[there])
                        there = l;
                }
                if (r[there] >= 0.85 * best)
                {
                    bestLag = there;
                    best = r[there];
                    break;
                }
            }
            rhythm = best;

            // Refined: the period whose folding varies most, within a bin and a half, in 5 mm steps.
            double centre = bestLag * Bin, bestSpread = -1;
            period = centre;
            for (double q = centre - 1.5 * Bin; q <= centre + 1.5 * Bin + 1e-9; q += 0.005)
            {
                if (q < minPeriod * 0.95)
                    continue;
                double spread = Spread(p, q);
                if (spread > bestSpread)
                {
                    bestSpread = spread;
                    period = q;
                }
            }

            folded = Fold(p, period);
            double lo = double.MaxValue, hi = double.MinValue;
            for (int j = 0; j < folded.Length; j++)
            {
                lo = Math.Min(lo, folded[j]);
                hi = Math.Max(hi, folded[j]);
            }
            return hi - lo >= MinContrast;
        }

        /// <summary>Epoch folding's statistic: how much the profile folded over a period varies between phases (each bin by its count).</summary>
        private static double Spread(Profile p, double period)
        {
            int bins = Math.Max(8, (int)Math.Round(period / Bin));
            var sum = new double[bins];
            var count = new double[bins];
            double all = 0, total = 0;
            for (int i = 0; i < p.Sum.Length; i++)
            {
                if (p.Count[i] == 0)
                    continue;
                double x = p.Centre(i) / period;
                int j = Math.Min(bins - 1, (int)((x - Math.Floor(x)) * bins));
                double v = p.At(i);
                sum[j] += v;
                count[j] += 1;
                all += v;
                total += 1;
            }
            if (total < 1)
                return 0;
            double mean = all / total, spread = 0;
            for (int j = 0; j < bins; j++)
            {
                if (count[j] > 0)
                {
                    double m = sum[j] / count[j] - mean;
                    spread += count[j] * m * m;
                }
            }
            return spread / total;
        }

        /// <summary>The profile folded over a period: its mean at each 5 cm of the period (phase 0 at coordinate 0), lightly smoothed.</summary>
        private static double[] Fold(Profile p, double period)
        {
            int bins = Math.Max(8, (int)Math.Round(period / 0.05));
            var sum = new double[bins];
            var count = new double[bins];
            for (int i = 0; i < p.Sum.Length; i++)
            {
                if (p.Count[i] == 0)
                    continue;
                double x = p.Centre(i) / period;
                int j = Math.Min(bins - 1, (int)((x - Math.Floor(x)) * bins));
                sum[j] += p.At(i);
                count[j] += 1;
            }

            // A phase no bin fell in takes its neighbours'.
            var mean = new double[bins];
            for (int j = 0; j < bins; j++)
            {
                if (count[j] > 0)
                {
                    mean[j] = sum[j] / count[j];
                    continue;
                }
                double s = 0;
                int c = 0;
                for (int d = 1; d < bins && c == 0; d++)
                {
                    int a = (j + d) % bins, b = (j - d + bins) % bins;
                    if (count[a] > 0) { s += sum[a] / count[a]; c++; }
                    if (count[b] > 0) { s += sum[b] / count[b]; c++; }
                }
                mean[j] = c > 0 ? s / c : 0;
            }

            var smooth = new double[bins];
            for (int j = 0; j < bins; j++)
                smooth[j] = (mean[(j + bins - 1) % bins] + 2 * mean[j] + mean[(j + 1) % bins]) * 0.25;
            return smooth;
        }

        /// <summary>
        /// A folded profile's bands: the windows (the longest stretch above level -- a share of the
        /// way from its dullest to its shiniest -- dull gaps under 0.3 m bridged): where they start
        /// and how long; the wall between them (the longest stretch below it): its middle and length.
        /// Metres from phase 0. False when it has no dull or no shiny part. A storey's windows are
        /// taken a quarter of the way up: a window whose glass is half painted curtain (a duller
        /// shine) is all window, its bright top alone would be a strip.
        /// </summary>
        private static bool Bands(double[] folded, double period, double level, out double windowStart, out double windowLength, out double wallMiddle, out double wallLength)
        {
            windowStart = windowLength = wallMiddle = wallLength = 0;
            int n = folded.Length;
            double lo = double.MaxValue, hi = double.MinValue;
            for (int j = 0; j < n; j++)
            {
                lo = Math.Min(lo, folded[j]);
                hi = Math.Max(hi, folded[j]);
            }
            double mid = lo + level * (hi - lo);
            var shiny = new bool[n];
            int count = 0;
            for (int j = 0; j < n; j++)
            {
                shiny[j] = folded[j] >= mid;
                if (shiny[j])
                    count++;
            }
            if (count == 0 || count == n)
                return false;

            double step = period / n;

            // The wall: the longest dull run (before any bridging).
            int wallStart, wallRun;
            Longest(shiny, false, out wallStart, out wallRun);
            wallLength = wallRun * step;
            wallMiddle = (wallStart + 0.5 * wallRun) * step;
            wallMiddle -= period * Math.Floor(wallMiddle / period);

            // The windows: every other dull run under 0.3 m bridged (a transom, a mullion's line).
            // Round the ring once from a shiny phase, run by run.
            int gap = Math.Max(1, (int)Math.Round(0.3 / step));
            var bridged = (bool[])shiny.Clone();
            int origin = 0;
            while (!shiny[origin])
                origin++;
            for (int d = 1; d <= n; d++)
            {
                int j = (origin + d) % n;
                if (shiny[j] || shiny[(j + n - 1) % n] == false)
                    continue;
                int len = 0;   // j starts a dull run
                while (len < n && !shiny[(j + len) % n])
                    len++;
                if (len <= gap && !(j == wallStart && len == wallRun))
                {
                    for (int e = 0; e < len; e++)
                        bridged[(j + e) % n] = true;
                }
            }
            int start, length;
            Longest(bridged, true, out start, out length);
            windowStart = start * step;
            windowLength = length * step;
            return true;
        }

        /// <summary>The longest circular run of value in a ring: its start and length.</summary>
        private static void Longest(bool[] ring, bool value, out int start, out int length)
        {
            int n = ring.Length;
            start = 0;
            length = 0;
            int first = -1;
            for (int j = 0; j < n; j++)
            {
                if (ring[j] != value)
                {
                    first = j;
                    break;
                }
            }
            if (first < 0)
            {
                length = n;
                return;
            }

            // Walk once round from a place where the run is broken.
            int run = 0, runStart = 0;
            for (int d = 1; d <= n; d++)
            {
                int j = (first + d) % n;
                if (ring[j] == value)
                {
                    if (run == 0)
                        runStart = j;
                    run++;
                    if (run > length)
                    {
                        length = run;
                        start = runStart;
                    }
                }
                else
                {
                    run = 0;
                }
            }
        }

        /// <summary>
        /// The ground storey's top: the first floor from <see cref="MinGround"/> up (at most
        /// <see cref="MaxGround"/>) above which the profile follows the folded storey -- below it a
        /// lobby or a shop, often taller. The lowest such floor when none matches; 0 when there is none.
        /// </summary>
        private static double GroundTop(Profile p, double[] folded, double period, double floor, double top)
        {
            double first = floor + period * Math.Ceiling((MinGround - floor) / period);
            if (first > MaxGround || first > top)
                return 0;

            int bins = folded.Length;
            for (double line = first; line <= MaxGround && line + period <= top + 1e-6; line += period)
            {
                double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
                int n = 0;
                for (int i = 0; i < p.Sum.Length; i++)
                {
                    double y = p.Centre(i);
                    if (p.Count[i] == 0 || y < line || y >= line + period)
                        continue;
                    double x = y / period;
                    double model = folded[Math.Min(bins - 1, (int)((x - Math.Floor(x)) * bins))], seen = p.At(i);
                    sx += model;
                    sy += seen;
                    sxx += model * model;
                    syy += seen * seen;
                    sxy += model * seen;
                    n++;
                }
                if (n < 10)
                    continue;
                double cov = sxy - sx * sy / n, vx = sxx - sx * sx / n, vy = syy - sy * sy / n;
                if (vx > 1e-12 && vy > 1e-12 && cov / Math.Sqrt(vx * vy) >= 0.3)
                    return line;
            }
            return first;
        }
    }
}
