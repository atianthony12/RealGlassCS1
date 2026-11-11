using System;

namespace RealGlass.Water
{
    /// <summary>
    /// The water's optics from under it (RealGlass/WaterUnder has the shader; docs/UNDERWATER-PLAN.md).
    /// Pure: the mod sets the shader's vectors from <see cref="Vectors"/>, the GPU preview computes the
    /// same (tools\preview\foam-render.ps1 -UnderColumns), the tests check them (tools\test-settings.ps1).
    /// </summary>
    /// <remarks>
    /// - THE WATER'S KIND: coastal water, as it is at <see cref="ReferenceVisibility"/> -- pure water,
    ///   absorbing red first (<see cref="PureWater"/>: Pope &amp; Fry 1997) and scattering blue most
    ///   (<see cref="WaterScattering"/>: Morel 1974); dissolved organic matter, coastal water's yellow,
    ///   absorbing blue most (a(440) <see cref="Dissolved440"/>, falling as e^(-0.014 (l - 440)):
    ///   Bricaud, Morel &amp; Prieur 1981); and particles, which make up the rest of that visibility -- a
    ///   fifth absorbed, four fifths scattered (<see cref="ParticleAbsorbed"/>), 1.83% of that backwards
    ///   (Petzold 1972's average, as Mobley 1994 tabulates it). Its teal is what these leave: the
    ///   colour stays that kind's at every visibility.
    /// - HOW FAR ONE SEES: the beam attenuation c = a + b, that kind's, scaled so a black target
    ///   vanishes at the visibility asked for, 4.8 / c at green (Zaneveld &amp; Pegau 2003, "Robust
    ///   underwater visibility parameter"); never below pure water's own a + b.
    /// - HOW THE DAYLIGHT FADES WITH DEPTH: the diffuse attenuation K_d = sqrt(a^2 + G a b) / mu0,
    ///   G = 0.473 mu0 - 0.218 (Kirk 1984, "Dependence of relationship between inherent and apparent
    ///   optical properties of water on solar altitude"), mu0 the cosine of the sun refracted into the
    ///   water (at least <see cref="DiffuseCosine"/>, the sky's light); scaled as c, never below pure
    ///   water's absorption.
    /// - HOW BRIGHT THE WATER IS: its reflectance just under the surface R = 0.33 b_b / (a + b_b)
    ///   (Morel &amp; Prieur 1977, bounded as Gordon et al. 1975 write it) x the daylight below the
    ///   surface is the light coming up; looking level, brighter than that (measured light fields are
    ///   brighter toward the horizon than toward the depths: Tyler 1960) -- and brighter again as the
    ///   eye adapts to the water's dimmer light, which the game's camera does not.
    ///   <see cref="VeilGain"/> holds both, and with <see cref="Dissolved440"/> and
    ///   <see cref="ReferenceVisibility"/> is set by eye (a player's picture of the look, 2026-10-28).
    /// - THE SUN: refracted into the water (n = <see cref="Water"/>), the share of the daylight below
    ///   the surface it brings after the surface's reflection (Schlick's Fresnel): the caustics' and the
    ///   shafts' share and the water's glow toward it.
    /// </remarks>
    public static class WaterUnderOptics
    {
        /// <summary>Pure water's absorption (1/m) at about 610, 550 and 465 nm, the screen's red, green and blue (Pope &amp; Fry 1997).</summary>
        public static readonly double[] PureWater = { 0.264, 0.0565, 0.0104 };

        /// <summary>The wavelengths those stand for (nm).</summary>
        public static readonly double[] Wavelengths = { 610.0, 550.0, 465.0 };

        /// <summary>Dissolved organic matter's absorption at 440 nm (1/m): coastal water's.</summary>
        public const double Dissolved440 = 0.12;

        /// <summary>Its spectral slope (1/nm; Bricaud, Morel &amp; Prieur 1981).</summary>
        public const double DissolvedSlope = 0.014;

        /// <summary>The visibility that defines c: a black target's (m) = this / c (Zaneveld &amp; Pegau 2003).</summary>
        public const double VisibilityConstant = 4.8;

        /// <summary>The particles' share of their attenuation that is absorbed (the rest scattered).</summary>
        public const double ParticleAbsorbed = 0.2;

        /// <summary>The share of the particles' scattering that goes backwards (Petzold 1972's average).</summary>
        public const double ParticleBackscatter = 0.0183;

        /// <summary>The least the particles attenuate (1/m): the clearest water still holds some.</summary>
        public const double MinParticles = 0.005;

        /// <summary>The least cosine the daylight's diffuse attenuation is taken at (the sky's light).</summary>
        public const double DiffuseCosine = 0.8;

        /// <summary>The level radiance under the surface over the light coming up, the eye adapted (set by eye).</summary>
        public const double VeilGain = 12.0;

        /// <summary>The visibility the water's kind (its colour) is taken at (m).</summary>
        public const double ReferenceVisibility = 30.0;

        /// <summary>Water's refractive index.</summary>
        public const double Water = 1.333;

        /// <summary>The visibilities the vectors are taken between (m; the row's own range lies inside).</summary>
        public const double MinVisibility = 3.0;

        public const double MaxVisibility = 80.0;

        /// <summary>Pure water's scattering (1/m) at a wavelength (nm): 0.00288 (l / 500)^-4.32 (Morel 1974).</summary>
        public static double WaterScattering(double nm)
        {
            return 0.00288 * Math.Pow(nm / 500.0, -4.32);
        }

        /// <summary>Dissolved organic matter's absorption (1/m) at a wavelength (nm; Bricaud, Morel &amp; Prieur 1981).</summary>
        public static double Dissolved(double nm)
        {
            return Dissolved440 * Math.Exp(-DissolvedSlope * (nm - 440.0));
        }

        /// <summary>
        /// The shader's four vectors, 4 numbers each (_RGUnderC, _RGUnderK, _RGUnderVeil, _RGUnderSun):
        /// c and K_d (1/m, red green blue), the veiling light looking level at the surface (linear), the
        /// sun refracted into the water (unit, toward it) and its share of the daylight below the surface.
        /// </summary>
        /// <param name="visibility">How far one sees (m).</param>
        /// <param name="toSun">Toward the sun (unit, world; y up).</param>
        /// <param name="sun">The sun's light (linear rgb: the colour a white Lambert surface facing it shows).</param>
        /// <param name="sky">The sky's ambient light (linear rgb).</param>
        public static double[] Vectors(double visibility, double[] toSun, double[] sun, double[] sky)
        {
            double v = Clamp(visibility, MinVisibility, MaxVisibility);
            double scale = ReferenceVisibility / v;
            double cGreen = VisibilityConstant / ReferenceVisibility;
            double particles = Math.Max(cGreen - PureWater[1] - Dissolved(Wavelengths[1]) - WaterScattering(Wavelengths[1]), MinParticles);
            double bp = (1.0 - ParticleAbsorbed) * particles;

            double up = Math.Max(Finite(toSun[1]), 0.0);
            double fx = Finite(toSun[0]) / Water, fz = Finite(toSun[2]) / Water;
            double mu0 = Math.Sqrt(Math.Max(1.0 - fx * fx - fz * fz, 0.0));
            double cosK = Math.Max(mu0, DiffuseCosine);
            double G = 0.473 * cosK - 0.218;

            double edge = 1.0 - up;
            double fresnel = 0.02 + 0.98 * edge * edge * edge * edge * edge;
            double through = up * (1.0 - fresnel);

            var result = new double[16];
            double sunGrey = 0.0, dayGrey = 0.0;
            for (int i = 0; i < 3; i++)
            {
                double bw = WaterScattering(Wavelengths[i]);
                double a = PureWater[i] + Dissolved(Wavelengths[i]) + ParticleAbsorbed * particles;
                double b = bw + bp;
                double bb = 0.5 * bw + ParticleBackscatter * bp;
                result[i] = Math.Max((a + b) * scale, PureWater[i] + bw);
                result[4 + i] = Math.Max(Math.Sqrt(a * a + G * a * b) / cosK * scale, PureWater[i]);
                double below = Math.Max(Finite(sun[i]), 0.0) * through;
                double daylight = below + Math.Max(Finite(sky[i]), 0.0);
                result[8 + i] = VeilGain * 0.33 * bb / (a + bb) * daylight;
                double w = i == 0 ? 0.2126 : i == 1 ? 0.7152 : 0.0722;
                sunGrey += w * below;
                dayGrey += w * daylight;
            }

            result[12] = fx;
            result[13] = mu0;
            result[14] = fz;
            result[15] = up > 0.0 && dayGrey > 1e-9 ? Clamp(sunGrey / dayGrey, 0.0, 1.0) : 0.0;
            return result;
        }

        /// <summary>
        /// The bed seen through this water from above (RealGlass/WaterSky's clear shallows; a trial,
        /// 2026-11-11): how fast its light fades per channel with the water H over it. Lee, Carder,
        /// Mobley, Steward &amp; Patch's shallow-water model as their 1999 paper writes it (Applied
        /// Optics 38, 3831: its Eq. 9 with the coefficients of its Eq. 5 and 6; the 1998 paper's were
        /// other numbers, for a view straight down): the bed's share of what leaves the water is
        /// rho / pi e^(-kappa H (1 / cos sun + D_u / cos view)), kappa = a + b_b, D_u = 1.04 sqrt(1 +
        /// 5.4 u) for the bed's light (u = b_b / (a + b_b)), both angles under the water. Taken here: that
        /// exponent alone, b_b for their b_b' (their view-dependent factor on the particles' share at
        /// 1), the bed's colour as the frame shows it; their water column's own term is not here (the
        /// water's colour stays the game's). The water's kind as <see cref="Vectors"/> takes it, its dissolved matter
        /// <paramref name="dissolved440"/> (1/m at 440 nm; <see cref="Dissolved440"/> the coastal water's;
        /// 0 = none, a clear blue water); scaled to the visibility as c is, never below pure water's.
        /// 8 numbers: per metre of H on the daylight's way down, kappa / cos sun (red green blue, 0);
        /// per metre of H on the way up before the view's 1 / cos, kappa D_u (red green blue, 0).
        /// </summary>
        public static double[] BedVectors(double visibility, double dissolved440, double[] toSun)
        {
            double v = Clamp(visibility, MinVisibility, MaxVisibility);
            double scale = ReferenceVisibility / v;
            double dissolved = Clamp(dissolved440, 0.0, 1.0);
            double cGreen = VisibilityConstant / ReferenceVisibility;
            double particles = Math.Max(cGreen - PureWater[1] - dissolved * Math.Exp(-DissolvedSlope * (Wavelengths[1] - 440.0)) - WaterScattering(Wavelengths[1]), MinParticles);
            double bp = (1.0 - ParticleAbsorbed) * particles;

            double fx = Finite(toSun[0]) / Water, fz = Finite(toSun[2]) / Water;
            double mu0 = Math.Sqrt(Math.Max(1.0 - fx * fx - fz * fz, 0.0));
            double cosK = Math.Max(mu0, DiffuseCosine);

            var result = new double[8];
            for (int i = 0; i < 3; i++)
            {
                double bw = WaterScattering(Wavelengths[i]);
                double a = PureWater[i] + dissolved * Math.Exp(-DissolvedSlope * (Wavelengths[i] - 440.0)) + ParticleAbsorbed * particles;
                double bb = 0.5 * bw + ParticleBackscatter * bp;
                double kappa = Math.Max((a + bb) * scale, PureWater[i] + 0.5 * bw);
                double u = bb / (a + bb);
                result[i] = kappa / cosK;
                result[4 + i] = kappa * 1.04 * Math.Sqrt(1.0 + 5.4 * u);
            }

            return result;
        }

        private static double Clamp(double x, double lo, double hi)
        {
            return double.IsNaN(x) ? lo : Math.Max(lo, Math.Min(hi, x));
        }

        private static double Finite(double x)
        {
            return double.IsNaN(x) || double.IsInfinity(x) ? 0.0 : x;
        }
    }
}
