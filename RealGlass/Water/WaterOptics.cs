using System;

namespace RealGlass.Water
{
    /// <summary>
    /// The maths of the map's water (the author, 2026-10-03: "the water shader itself -- i.e. ability
    /// to control brightness, SSR, and by default it should be off and have its own tab"; ported from
    /// RealLight's Sea.cs and CityReflection.cs, which it shares the model with). Pure: the shaders
    /// (RealGlass/WaterCube, RealGlass/CityReflection) read the same table, the tests check the numbers
    /// (tools\test-settings.ps1).
    /// </summary>
    /// <remarks>
    /// - THE REFLECTANCE: a rough sea of Cox &amp; Munk's (1954) Gaussian wave slopes, mean square slope
    ///   0.003 + 0.00512 U for a wind of U m/s; rho, the sea-surface reflectance factor (Mobley 1999,
    ///   "Estimation of the remote-sensing reflectance from above-surface measurements"): Schlick's
    ///   Fresnel for water (n 1.333, F0 0.0204) averaged over the facets the eye sees, each by its
    ///   projected area towards the eye. 2% looking down, about half toward the horizon.
    /// - THE CITY'S STREAKS: a facet tilted by a in the plane of the view turns the reflected ray by
    ///   2a in height, so the waves stretch a reflection up and down by <see cref="Spread"/>.
    /// - THE RIPPLES (<see cref="Ripples"/>): the game's own ripple texture (_WaterNormal) moves only
    ///   where the water flows; ours is redrawn every frame from a sum of directional sine waves
    ///   (Finch 2004, "Effective Water Simulation from Physical Models", GPU Gems ch. 1) at deep
    ///   water's speeds (omega^2 = g k; Tessendorf 2001, "Simulating Ocean Water"), with whole wave
    ///   numbers across the texture so it tiles as an FFT ocean does (Tessendorf), each wave with the
    ///   same slope -- Phillips's (1958) equilibrium range, where every octave of wavelength carries the
    ///   same slope. The game shades with it as with its own; the city's reflection bends its rays off
    ///   the same ripples (the author, 2026-10-05: "the water doesn't get blurry. BUT, it does get
    ///   distorted a bit, with the waves").
    /// - THE SKY ON RIPPLED WATER (2026-10-08, <see cref="SkyReflectance"/>): Fresnel on each ripple,
    ///   worked out per pixel by the sky layer (RealGlass/WaterSky), not baked into the cube the game
    ///   reads along the ripple's reflected ray (<see cref="OldCubeReflectance"/>: it counted each
    ///   ripple's tilt twice, about twice the sky on rippled water).
    /// - THE REFLECTION'S LENGTH (<see cref="MirrorSlope"/>): the mod's own stretch, not a model --
    ///   the city's reflection drawn longer than the city is tall, as reflections on open water look
    ///   drawn out toward the viewer (the author, 2026-10-04).
    /// </remarks>
    public static class WaterOptics
    {
        public const double WaterIndex = 1.333;

        /// <summary>Fresnel at normal incidence, air to water: ((n - 1) / (n + 1))^2 = 0.0204.</summary>
        public static readonly double F0 = Math.Pow((WaterIndex - 1.0) / (WaterIndex + 1.0), 2.0);

        /// <summary>Entries in the reflectance table, over x = sqrt(mu) from 0 to 1 (the shaders' _RGSeaTable[33]).</summary>
        public const int TableSize = 33;

        /// <summary>The wind the sky's reflectance is worked out for, m/s (a moderate breeze, Beaufort 3; RealLight's).</summary>
        public const double SkyWind = 5.0;

        /// <summary>The wind for the city's streaks, m/s (a light breeze, Beaufort 2: the sheltered water a city stands on).</summary>
        public const double CityWind = 2.0;

        /// <summary>The game's water shader adds the cube x this for clean water (Custom/Water/Default, disassembled by RealLight).</summary>
        public const double GameReflectivity = 0.5;

        /// <summary>
        /// The sky's share on the water (the sky layer's x this; 1 = real water's), for every new install
        /// and a Reset: 5% (the author's call, 2026-11-13: more sky brightened the water too much; the
        /// Sky reflection slider and its switch show with the advanced options only). A file from an older
        /// version keeps its own (Settings.WaterSky). The preview draws with it (FoamPreview).
        /// </summary>
        public const float SkyShare = 0.05f;

        /// <summary>
        /// How far a reflected ray is followed at most, metres: the reach is the camera's far plane x
        /// 0.9 up to this (the game's far plane is 20-23 km in the author's city). 8 km until
        /// 2026-10-08, faded out from 5.6 km: the author found the night reflection's "render distance
        /// a bit low".
        /// </summary>
        public const double MaxDistance = 30000.0;

        /// <summary>The least depth behind a surface that still counts as meeting it, metres.</summary>
        public const double Thickness = 30.0;

        /// <summary>The share of the screen, at each edge, over which the city's reflection fades out.</summary>
        public const double EdgeFade = 0.05;

        /// <summary>How many waves make the ripples (the generator's _RGRippleWaves holds two rows each).</summary>
        public const int RippleCount = 32;

        /// <summary>The longest ripple, metres: the chop a light breeze raises on a harbour.</summary>
        public const double RippleLongest = 16.0;

        /// <summary>The shortest, metres: 3 texels of the <see cref="RippleTexels"/> texture over <see cref="RippleTile"/>.</summary>
        public const double RippleShortest = 0.5;

        /// <summary>The Ripple size slider's range (1 = 100%).</summary>
        public const double MinRippleSize = 0.5, MaxRippleSize = 3.0;

        /// <summary>The Wave size slider's range (1 = 100%): the longest wave's share of its length at the ripple size.</summary>
        public const double MinWaveSize = 0.05, MaxWaveSize = 1.0;

        /// <summary>The ripple texture's size, texels a side.</summary>
        public const int RippleTexels = 1024;

        /// <summary>
        /// How the game lays its ripple texture on the water (Custom/Water/Default, disassembled
        /// 2026-10-05): once at world xz x <see cref="GameRippleScaleA"/>, once at (z, x) x
        /// <see cref="GameRippleScaleB"/> with x and y swapped -- a mirrored, inverted copy.
        /// </summary>
        public const double GameRippleScaleA = 0.0061;
        public const double GameRippleScaleB = 0.0057;

        /// <summary>The metres one ripple texture covers (the game's first copy: 1 / 0.0061 = 164 m).</summary>
        public static readonly double RippleTile = 1.0 / GameRippleScaleA;

        /// <summary>The way the ripples run, degrees from +x toward +z, and how far their directions spread either side of it.</summary>
        public const double WaveHeading = 30.0;
        public const double WaveSpread = 70.0;

        /// <summary>
        /// The slopes' root-mean-square of the waves the city's reflection bends its rays off, at 100%
        /// (rise over run): today's look kept, whatever the ripples' strength calibrates to.
        /// </summary>
        public const double WaveSlope = 0.03;

        public const double Gravity = 9.81;

        /// <summary>The numbers in each ripple's row pair (<see cref="Ripples"/>).</summary>
        public const int WaveStride = 8;

        /// <summary>Cox &amp; Munk's mean square slope of a clean sea for a wind of u m/s (both directions together).</summary>
        public static double SlopeVariance(double windSpeed)
        {
            return 0.003 + 0.00512 * Math.Max(0.0, windSpeed);
        }

        /// <summary>Schlick's Fresnel for water.</summary>
        public static double Fresnel(double cosTheta)
        {
            double c = 1.0 - Math.Max(0.0, Math.Min(1.0, cosTheta));
            double c2 = c * c;
            return F0 + (1.0 - F0) * c2 * c2 * c;
        }

        /// <summary>
        /// The rough sea's reflectance of the sky by mu = the sine of how far below the level one
        /// looks: entry i at x = i / 32, mu = x^2 (finer near grazing).
        /// </summary>
        public static double[] BuildTable(double slopeVariance)
        {
            var rho = new double[TableSize];
            const int n = 64;
            const double reach = 4.0;
            double perAxis = Math.Sqrt(slopeVariance / 2.0);
            double step = 2.0 * reach * perAxis / (n - 1);

            for (int i = 0; i < TableSize; i++)
            {
                double x = i / (double)(TableSize - 1);
                double mu = Math.Max(1e-4, x * x);
                double across = Math.Sqrt(1.0 - mu * mu);

                // Towards the eye from the surface: back along +x, up by mu.
                double ex = -across, ey = mu;
                double sumW = 0.0, sumWf = 0.0;
                for (int a = 0; a < n; a++)
                {
                    double zx = -reach * perAxis + a * step;
                    for (int b = 0; b < n; b++)
                    {
                        double zz = -reach * perAxis + b * step;
                        double weight = Math.Exp(-(zx * zx + zz * zz) / slopeVariance);
                        double len = Math.Sqrt(1.0 + zx * zx + zz * zz);
                        double nx = -zx / len, ny = 1.0 / len;
                        double cosI = nx * ex + ny * ey;
                        if (cosI <= 0.0)
                            continue;

                        double w = weight * cosI / ny;
                        sumW += w;
                        sumWf += w * Fresnel(cosI);
                    }
                }

                rho[i] = sumW > 0.0 ? sumWf / sumW : 1.0;
            }

            return rho;
        }

        /// <summary>
        /// REALISTIC DEPTH (the row; RealGlass/WaterSky's header has the model): the path through the
        /// water per metre of water over the bed, the light's way down (1 / cos of the sun's refracted
        /// ray) plus the view's way up (<see cref="DepthViewPath"/> / cos of the view's refracted ray) --
        /// Lee, Carder, Mobley, Steward &amp; Patch (Applied Optics 38, 1999), Eq. 9's exponent: looking
        /// straight down under a sun overhead, <see cref="DepthOverhead"/> x the depth.
        /// </summary>
        public const double DepthViewPath = 1.05;

        public const double DepthOverhead = 1.0 + DepthViewPath;

        /// <summary>The share of the bed's light the Clarity row's depth leaves, looking straight down under a high sun.</summary>
        public const double DepthClarityLeft = 0.05;

        /// <summary>The weakest a channel of the underwater colour counts for, x its strongest (a black channel is not infinitely murky).</summary>
        public const double DepthColourFloor = 0.04;

        /// <summary>
        /// The bed's light lost per metre of its path, per channel (red, green, blue), from the Clarity
        /// row (m: the depth at which <see cref="DepthClarityLeft"/> of the bed is left in the clearest
        /// channel, looking straight down under a high sun) and the underwater colour (linear; as the
        /// game's _WaterColorUnder, whose tint its seabed takes). The clearest channel gets
        /// sigma_min = ln 20 / (2.05 X); each other channel c sigma_min (1 + ln(m / c) / ln 20), m the
        /// strongest: at one Clarity the bed's channels stand as the colour's do (the picked tint); deeper
        /// it saturates toward it, shallower it fades in. A grey or black colour: every channel sigma_min.
        /// </summary>
        public static double[] DepthSigma(double clarity, double r, double g, double b)
        {
            double x = double.IsNaN(clarity) ? 12.0 : Math.Max(0.5, clarity);
            double least = Math.Log(1.0 / DepthClarityLeft) / (DepthOverhead * x);
            double[] c = { Finite(r), Finite(g), Finite(b) };
            double m = Math.Max(c[0], Math.Max(c[1], c[2]));
            var sigma = new double[3];
            for (int i = 0; i < 3; i++)
            {
                double ratio = m > 1e-6 ? m / Math.Max(c[i], DepthColourFloor * m) : 1.0;
                sigma[i] = least * (1.0 + Math.Log(ratio) / Math.Log(1.0 / DepthClarityLeft));
            }
            return sigma;
        }

        /// <summary>
        /// Toward the sun under the water (refracted at a flat surface, Snell; unit, up), from toward it
        /// above. Below the horizon it is the ray at the critical angle (48.6 deg from straight down,
        /// cos 0.66): the light down there is the sky's, and nothing steps as the sun sets.
        /// </summary>
        public static double[] DepthSunUnder(double x, double y, double z)
        {
            double flat = Math.Sqrt(x * x + z * z);
            double up = Math.Max(0.0, Finite(y));
            double len = Math.Sqrt(flat * flat + up * up);
            double sinAir = len > 1e-9 ? flat / len : 1.0;
            double sinWater = sinAir / WaterIndex;
            double cosWater = Math.Sqrt(Math.Max(0.0, 1.0 - sinWater * sinWater));
            double hx = flat > 1e-9 ? x / flat : 0.0, hz = flat > 1e-9 ? z / flat : 0.0;
            return new[] { hx * sinWater, cosWater, hz * sinWater };
        }

        /// <summary>
        /// The sun's share of the light on a level bed (0..1): its light on the level (luminance x the
        /// height of the sun, 0 below the horizon or with its Light off) over that plus the sky's
        /// ambient. The caustics move only that share.
        /// </summary>
        public static double DepthSunShare(double sunY, double sunLuminance, double skyLuminance)
        {
            double sun = Math.Max(0.0, Finite(sunY)) * Math.Max(0.0, Finite(sunLuminance));
            double sky = Math.Max(0.0, Finite(skyLuminance));
            return sun + sky > 1e-9 ? sun / (sun + sky) : 0.0;
        }

        /// <summary>What reaches the eye of a bed this deep (m), one channel: e^(-sigma depth (1 / cos sun' + 1.05 / cos view')), both cosines under the water.</summary>
        public static double DepthThrough(double sigma, double depth, double cosSunUnder, double cosViewUnder)
        {
            double path = Math.Max(0.0, depth) * (1.0 / Math.Max(cosSunUnder, 0.66) + DepthViewPath / Math.Max(cosViewUnder, 1e-2));
            return Math.Exp(-Math.Max(0.0, sigma) * path);
        }

        private static double Finite(double v)
        {
            return double.IsNaN(v) || double.IsInfinity(v) ? 0.0 : v;
        }

        /// <summary>The table at mu, linearly between its entries (as the shaders read it).</summary>
        public static double Lookup(double[] table, double mu)
        {
            double x = Math.Sqrt(Math.Max(0.0, Math.Min(1.0, mu))) * (TableSize - 1);
            int i = Math.Min(TableSize - 2, (int)Math.Floor(x));
            double f = x - i;
            return table[i] + (table[i + 1] - table[i]) * f;
        }

        /// <summary>
        /// The gain the cube carries: the theme's sky x rho x this, lit by the game's water shader
        /// (x 0.5 x its light), comes back as the theme's sky x its light x rho x the slider -- the
        /// game's own lighting kept, the reflectance real water's.
        /// </summary>
        public static double CubeGain(double skySlider)
        {
            return Math.Max(0.0, skySlider) / GameReflectivity;
        }

        /// <summary>
        /// The light the game's water lights its cube with, per channel, made safe to divide by: a
        /// published real sky (light already) is divided by it in the cube, so that the game's
        /// lighting gives it back. Each channel at least 5% of the light's luminance (and 1e-3):
        /// a deep-red dusk does not blow the blue up. Returned in place.
        /// </summary>
        public static void FloorLight(ref double r, ref double g, ref double b)
        {
            double floor = Math.Max(1e-3, (0.2126 * r + 0.7152 * g + 0.0722 * b) * 0.05);
            r = Math.Max(floor, r);
            g = Math.Max(floor, g);
            b = Math.Max(floor, b);
        }

        /// <summary>One standard deviation of the reflected rays' height about the mirror ray, radians: 2 x the slope's in one plane.</summary>
        public static double Spread(double slopeVariance)
        {
            return 2.0 * Math.Sqrt(slopeVariance / 2.0);
        }

        /// <summary>
        /// The city reflection's streaks: the Gaussian's sigma up and down, in uv of the screen's height.
        /// streaks: the Streaks slider (1 = the waves' <see cref="Spread"/> as an angle on the screen);
        /// fovY in degrees.
        /// </summary>
        public static double StreakSigma(double streaks, double spread, double fovY)
        {
            double perRadian = 1.0 / (2.0 * Math.Tan(fovY * Math.PI / 360.0));
            return Math.Max(0.0, streaks) * spread * perRadian;
        }

        /// <summary>
        /// The ripples, <see cref="WaveStride"/> numbers each (the generator's two rows: i and
        /// <see cref="RippleCount"/> + i): the WHOLE wave numbers m, n (cycles across the texture, along
        /// x and z: it tiles); whole cycles per hour (the game's hour clock, _SimulationTime2, wraps with
        /// no jump; deep water's speed for the wave's true length on <see cref="RippleTile"/>, to 0.1%);
        /// the slope; the phase in cycles; the direction's x and z (unit); 0. Lengths from
        /// <see cref="RippleLongest"/> to <see cref="RippleShortest"/> in equal ratios before rounding,
        /// directions in a scrambled order (golden-ratio steps over the spread: neighbouring lengths run
        /// far apart), every slope the same and the slopes' mean square, sum s^2 / 2, exactly 1 -- the
        /// generator multiplies by the root-mean-square slope it wants.
        /// </summary>
        public static double[] Ripples()
        {
            return Ripples(1.0);
        }

        /// <summary>
        /// The ripples at <paramref name="size"/> (the Ripple size slider, 1 = 100%): every length x size,
        /// the shortest never under <see cref="RippleShortest"/> (3 texels); the slopes as at 100%.
        /// </summary>
        public static double[] Ripples(double size)
        {
            return Ripples(size, 1.0);
        }

        /// <summary>
        /// The same with the longest wave x <paramref name="waveSize"/> (the Wave size slider, 1 = 100%):
        /// calm water keeps only the short ripples; never shorter than 1.5 x the shortest.
        /// </summary>
        public static double[] Ripples(double size, double waveSize)
        {
            double shortest, longest;
            RippleRange(size, waveSize, out shortest, out longest);
            var waves = new double[RippleCount * WaveStride];
            double ratio = Math.Pow(shortest / longest, 1.0 / (RippleCount - 1));
            double slope = Math.Sqrt(2.0 / RippleCount);
            var taken = new System.Collections.Generic.Dictionary<long, bool>();
            for (int i = 0; i < RippleCount; i++)
            {
                double cycles = RippleTile / (longest * Math.Pow(ratio, i));
                double heading = (WaveHeading + WaveSpread * (2.0 * Frac(0.5 + i * 0.6180339887) - 1.0)) * Math.PI / 180.0;
                double m = Math.Round(cycles * Math.Cos(heading)), n = Math.Round(cycles * Math.Sin(heading));
                // Two ripples on one wave vector would add into one: step the second aside.
                while ((m == 0.0 && n == 0.0) || taken.ContainsKey((long)m * 100000L + (long)n))
                    n += 1.0;
                taken[(long)m * 100000L + (long)n] = true;

                double across = Math.Sqrt(m * m + n * n);
                double k = 2.0 * Math.PI * across / RippleTile;
                int at = i * WaveStride;
                waves[at] = m;
                waves[at + 1] = n;
                waves[at + 2] = Math.Max(1.0, Math.Round(Math.Sqrt(Gravity * k) * 3600.0 / (2.0 * Math.PI)));
                waves[at + 3] = slope;
                waves[at + 4] = Frac(0.2 + i * 0.7548776662);
                waves[at + 5] = m / across;
                waves[at + 6] = n / across;
            }

            return waves;
        }

        /// <summary>The shortest and the longest ripple (m) at the Ripple size and Wave size sliders (1 = 100%).</summary>
        public static void RippleRange(double size, double waveSize, out double shortest, out double longest)
        {
            size = Math.Max(MinRippleSize, Math.Min(MaxRippleSize, size));
            waveSize = Math.Max(MinWaveSize, Math.Min(MaxWaveSize, waveSize));
            shortest = RippleShortest * Math.Max(1.0, size);
            longest = Math.Max(1.5 * shortest, RippleLongest * size * waveSize);
        }

        /// <summary>
        /// For the sky layer (RealGlass/WaterSky): how much of the ripples' slope a pixel averages away,
        /// by the mip L of the ripple texture the GPU reads there. A wave is averaged away once the mip's
        /// texels are half its length (fewer than 2 a cycle; a texel at mip 0 is <see cref="RippleTile"/>
        /// / <see cref="RippleTexels"/>). The waves have equal slopes and lengths in equal ratios
        /// (<see cref="Ripples"/>), so the share averaged away grows evenly in L: saturate((L - start) /
        /// span), start the mip where the shortest goes, start + span where the longest does.
        /// </summary>
        public static void SkyRippleMips(double size, double waveSize, out double start, out double span)
        {
            double shortest, longest;
            RippleRange(size, waveSize, out shortest, out longest);
            double texel = RippleTile / RippleTexels;
            start = Math.Log(shortest / (2.0 * texel), 2.0);
            span = Math.Max(0.5, Math.Log(longest / shortest, 2.0));
        }

        /// <summary>
        /// The sky layer's reflectance (RealGlass/WaterSky, the same sum): a facet seen at cosI (the
        /// cosine between the eye and its normal) reflects Schlick's Fresnel where its ripples are
        /// resolved, the rough sea's <paramref name="table"/> where they are averaged away
        /// (<paramref name="averaged"/> 0..1; Bruneton, Neyret &amp; Holzschuch 2010).
        /// </summary>
        public static double SkyReflectance(double[] table, double cosI, double averaged)
        {
            double u = Math.Max(0.0, Math.Min(1.0, averaged));
            return Fresnel(cosI) + (Lookup(table, cosI) - Fresnel(cosI)) * u;
        }

        /// <summary>
        /// What the water's cube gave until 2026-10-08 (RealGlass/WaterCube, read by the game along the
        /// ripple's reflected ray): the rough sea's reflectance by that ray's height, for a view
        /// <paramref name="viewDown"/> degrees below the level and a facet tilted <paramref name="tilt"/>
        /// degrees toward the eye in the plane of the view. The ray rises viewDown + 2 tilt; the true
        /// incidence on the facet is 90 - viewDown - tilt: the cube counted the tilt twice.
        /// </summary>
        public static double OldCubeReflectance(double[] table, double viewDown, double tilt)
        {
            double up = Math.Max(0.0, viewDown + 2.0 * tilt);
            return Lookup(table, Math.Sin(up * Math.PI / 180.0));
        }

        /// <summary>The same facet's true reflectance (Schlick on the facet): incidence 90 - viewDown - tilt degrees.</summary>
        public static double FacetReflectance(double viewDown, double tilt)
        {
            double incidence = 90.0 - viewDown - tilt;
            return Fresnel(Math.Cos(incidence * Math.PI / 180.0));
        }

        /// <summary>
        /// The game decodes its ripple texture x 2 - 1 and halves it into the normal's slope
        /// (Custom/Water/Default). So a texture whose decoded values have root-mean-square
        /// <paramref name="decodedRms"/> (both channels together) is a surface of root-mean-square slope
        /// decodedRms / 2: ours, at <paramref name="strength"/> (1 = as rough as the texture measured).
        /// </summary>
        public static double RippleSlope(double decodedRms, double strength)
        {
            return 0.5 * Math.Max(0.0, decodedRms) * Math.Max(0.0, strength);
        }

        /// <summary>
        /// The city reflection's gain on the ripples' slopes: at the Waves slider's 100% and ripples at
        /// 100%, the reflection's rays bend by <see cref="WaveSlope"/> root-mean-square (today's look,
        /// whatever the game's texture measured); rougher ripples bend them more.
        /// </summary>
        public static double CityRippleGain(double cityWaves, double decodedRms)
        {
            double slopeAt100 = RippleSlope(decodedRms, 1.0);
            return slopeAt100 > 1e-6 ? Math.Max(0.0, cityWaves) * WaveSlope / slopeAt100 : 0.0;
        }

        /// <summary>The decoded root-mean-square taken when the game's texture cannot be measured.</summary>
        public const double DefaultRippleRms = 0.3;

        /// <summary>
        /// A ripple texture's two channels as the game reads them (bytes, decoded v / 255 x 2 - 1):
        /// the mean of the decoded vector's length and its root-mean-square (both channels together).
        /// </summary>
        public static void DecodedStats(byte[] x, byte[] y, out double mean, out double rms)
        {
            mean = 0.0;
            rms = 0.0;
            int n = Math.Min(x != null ? x.Length : 0, y != null ? y.Length : 0);
            if (n == 0)
                return;

            double sumX = 0.0, sumY = 0.0, sumSq = 0.0;
            for (int i = 0; i < n; i++)
            {
                double a = x[i] / 255.0 * 2.0 - 1.0, b = y[i] / 255.0 * 2.0 - 1.0;
                sumX += a;
                sumY += b;
                sumSq += a * a + b * b;
            }

            mean = Math.Sqrt(sumX * sumX + sumY * sumY) / n;
            rms = Math.Sqrt(sumSq / n);
        }

        private static double Frac(double x)
        {
            return x - Math.Floor(x);
        }

        /// <summary>
        /// The slope (rise over run) of the ray the city's reflection follows from water seen at
        /// <paramref name="viewSlope"/> below the level: the mirror's, divided by the Length slider (1 =
        /// a mirror; less is taken as 1). Water seen at slope s then shows what stands at s / length
        /// above it, so everything is reflected length times as far below the level (in slope) as it
        /// stands above it: a tower's reflection is length times as long, its foot where it was.
        /// </summary>
        public static double MirrorSlope(double viewSlope, double length)
        {
            return viewSlope / Math.Max(1.0, length);
        }

        /// <summary>By day 1, by night 0, smooth through dusk: the glass's daylight (GlassUniforms), from the sun's height (y of the unit vector to it).</summary>
        public static double Daylight(double sunY)
        {
            double t = Math.Max(0.0, Math.Min(1.0, (sunY + 0.10) / 0.20));
            return t * t * (3.0 - 2.0 * t);
        }
    }
}
