using System;

namespace RealGlass.Water
{
    /// <summary>
    /// The foam's numbers (RealGlass/WaterFoam has the shader; docs: the water plan's foam step, the
    /// author's pick 2026-10-06: "One thing I'd actually like is the foam"). Pure: the mod sets the
    /// shader's vectors from <see cref="Vectors"/>, the GPU preview reads the same (tools\preview\
    /// foam-render.ps1, from the built DLL), the tests check them (tools\test-settings.ps1).
    /// </summary>
    /// <remarks>
    /// - WHERE: the game's own simulation, as its water shader reads it (WaterSimulation.FillSurfaceMap:
    ///   32 m texels of the surface's normal, the flow, the depth; the surface heights in two frames).
    ///   SHORE foam within <see cref="ShoreDepth"/> of the waterline, on water at the sea's level only
    ///   (surf: lakes and rivers above it stay calm; Crest's shoreline foam, War Thunder's shore foam);
    ///   FAST water from <see cref="FastStart"/> to <see cref="FastFull"/> of the game's flow units,
    ///   sooner where the surface is steep (rapids, weirs, outflows); FRONTS where the surface rises
    ///   <see cref="FrontStart"/>..<see cref="FrontFull"/> m from one simulation frame to the next (a
    ///   tsunami's, a flood's, a dam break's: Chentanez &amp; Mueller 2010's breaking test).
    /// - HOW BRIGHT: <see cref="Reflectance"/>, dense foam in visible light (Koepke 1984, "Effective
    ///   reflectance of oceanic whitecaps": 20-55% as a wave breaks, 3-10% ten seconds on, 22% on
    ///   average) -- the coverage already thins it where it is old or sparse.
    /// - HOW IT MOVES: along the flow at the speed the game's own ripples move at
    ///   (<see cref="FlowSpeed"/>), in two phases <see cref="Cycle"/> s long (Vlachos 2010).
    /// - A water FRAME is 64 simulation frames (TerrainManager.HeightMap_sampleWaterHeightAndNormal blends
    ///   the two by (frame index &amp; 63 + timer) / 64): about a second at normal speed, so the rise a
    ///   frame is close to metres a second. The front thresholds still wait on a real tsunami's numbers.
    /// </remarks>
    public static class FoamOptics
    {
        /// <summary>How deep the shore foam reaches at its widest (m of water under the surface).</summary>
        public const double ShoreDepth = 1.2;

        /// <summary>How far the water may stand above or below the sea's level and still be the sea's (m).</summary>
        public const double SeaTolerance = 1.5;

        /// <summary>The flow speed (the game's units, 0..about 1.4) fast-water foam starts at, and is full at.</summary>
        public const double FastStart = 0.45;

        public const double FastFull = 0.95;

        /// <summary>A steep surface brings fast-water foam on sooner: the speed x (1 + this x the slope).</summary>
        public const double SteepBoost = 4.0;

        /// <summary>The rise from one simulation frame to the next (m) a front's foam starts at, and is full at.</summary>
        /// (The author's city, 2026-10-06: calm sea rises and falls ~0.3 m between frames everywhere --
        /// the first guess, 0.25..1, would have whitened whole seas.)
        public const double FrontStart = 1.5;

        public const double FrontFull = 3.0;

        /// <summary>Dense foam's reflectance in visible light (Koepke 1984).</summary>
        public const double Reflectance = 0.45;

        /// <summary>
        /// The speed the flow carries the foam at, m/s per unit of the game's flow: what its own ripples
        /// move at (Custom/Water/Default: 0.14 of a ripple tile of 1 / 0.0061 m every 2.5 s).
        /// </summary>
        public static readonly double FlowSpeed = 0.14 / WaterOptics.GameRippleScaleA / 2.5;

        /// <summary>The advection's cycle (s): divides the game's 60 s clock, so the wrap is seamless.</summary>
        public const double Cycle = 6.0;

        /// <summary>
        /// The shader's six vectors, 4 numbers each (_RGFoamScale, _RGFoamFeature, _RGFoamShore,
        /// _RGFoamFast, _RGFoamFront, _RGFoamLook): the amount (0 = no foam, 1 = the model's), each
        /// source's share (1 = the model's), and the sea's level.
        /// </summary>
        public static double[] Vectors(double amount, double shore, double fast, double front, double seaLevel)
        {
            return new[]
            {
                1.0 / FoamPattern.LaceTile, 1.0 / FoamPattern.PatchTile, FoamPattern.Size, StreakTip,
                Math.Log(FoamPattern.LaceFeature, 2.0), Math.Log(FoamPattern.PatchFeature, 2.0), SwashEdge, SwashDrain,
                Math.Max(0.0, shore), ShoreDepth, seaLevel, SeaTolerance,
                Math.Max(0.0, fast), FastStart, FastFull, SteepBoost,
                Math.Max(0.0, front), FrontStart, FrontFull, EdgeDepth,
                Reflectance, FlowSpeed, Cycle, Math.Max(0.0, amount),
            };
        }

        // ---- THE LAYERS (2026-10-24; docs: the layered-foam plan) ----
        // A whitecap's two stages (Monahan & Lu 1990): STAGE A, the foam a crest makes as it breaks --
        // dense, the brightest, 0.4-5 s (Potter et al. 2015); STAGE B, the sheet left behind, thinning
        // into lace over tens of seconds, its reflectance decaying exponentially (Koepke 1984: 20-55% as
        // a wave breaks, 3-10% ten seconds on; Callaghan et al. 2012); and under both the bubbles the
        // break pushed down (Terrill, Melville & Stramski 2001), seen as the water's own colour made
        // paler (the body reflectance R = f b_b / (a + b_b): Gordon et al. 1975, Morel & Prieur 1977) --
        // Crest's "foam bubbles" under its white foam.

        /// <summary>
        /// How long each layer lasts (s, e-folding): stage A (Potter et al. 2015: 0.4-5 s), the stage-B
        /// sheet's cover (residual foam lasts tens of seconds, Potter et al.; its reflectance falls
        /// faster, Koepke's ~6 s: the sheet's look dims with its share of fresh foam), the bubbles under
        /// them (Terrill et al. 2001: tens of seconds).
        /// </summary>
        public const double FreshLife = 1.2;

        public const double ResidualLife = 10.0;

        public const double BubbleLife = 20.0;

        /// <summary>Pure water's absorption (per m) in red (650 nm), green (550 nm) and blue (450 nm): Pope &amp; Fry 1997.</summary>
        public const double AbsorptionRed = 0.340, AbsorptionGreen = 0.0565, AbsorptionBlue = 0.0092;

        /// <summary>A fresh sheet's reflectance (Koepke's upper value) and an old one's.</summary>
        public const double FreshReflectance = 0.55;

        public const double ResidualReflectance = 0.40;

        /// <summary>How much of the water an old, thin sheet hides (a fresh one: all).</summary>
        public const double ResidualOpacity = 0.7;

        /// <summary>The share of a sheet in burst-bubble holes: fresh, old.</summary>
        public const double FreshHoles = 0.12;

        public const double ResidualHoles = 0.30;

        /// <summary>A fresh sheet's thickness for its lighting (m): the slope its edges are lit with.</summary>
        public const double FreshThickness = 0.25;

        /// <summary>Morel &amp; Prieur's f (R = f b_b / a), and the bubbles' backscattering over the water's own at their densest.</summary>
        public const double BodyF = 0.33;

        public const double BubbleBackscatter = 20.0;

        /// <summary>How deep the bubble clouds are seen (m: the parallax), how much of the water they hide at most, their spread over the white's and their edge.</summary>
        public const double BubbleDepth = 0.5;

        public const double BubbleOpacity = 0.65;

        public const double BubbleSpread = 1.4;

        public const double BubbleEdge = 0.15;

        /// <summary>
        /// The surf: a band <see cref="SurfWidth"/> m wide along the waterline, measured in distance
        /// from it (War Thunder phases its shore waves by the distance to the shore) -- the depth over
        /// the seabed's slope, that slope a smooth seabed's through points 2 x <see cref="SlopeSpan"/> m
        /// apart (no less than <see cref="MinSlope"/>), and no surf where the water is deeper than
        /// <see cref="SurfDepth"/> m. The game's coasts drop steeply (the author's city, 2026-10-24:
        /// under 6% of the shore's water shallower than 2 m), so a band of DEPTH (1.2 m) was a single
        /// thin line there. A broken wave (a bore) every <see cref="BorePeriod"/> s (8 a minute: the
        /// game's clock wraps at 60 s), the bigger breaking farther out, running in at the
        /// shallow-water speed sqrt(g d) of a beach of <see cref="BeachSlope"/> (on every coast: the
        /// lines keep apart on a steep one); sets of <see cref="SetLength"/> (wave groups); the swash
        /// white to <see cref="SwashWidth"/> m from the edge.
        /// </summary>
        public const double SurfWidth = 70.0;

        public const double SlopeSpan = 8.0;

        public const double MinSlope = 0.01;

        public const double SurfDepth = 8.0;

        public const double BorePeriod = 7.5;

        public const double BeachSlope = 1.0 / 30.0;

        public const double SetLength = 8.0;

        public const double SwashWidth = 3.0;

        /// <summary>How long a bore's churning crest stays nearly solid white (s): its width is its run over that.</summary>
        public const double CrestLife = 0.35;

        /// <summary>
        /// The foam's edges (2026-11-11, the author's report: pointy in places). The
        /// game's terrain runs straight between its 16 m points, creased along its grid, and the game cuts
        /// its water along its 32 m depth map's cells: the waterline is a polygon. Every line of the surf
        /// repeated its corners (the distance from the waterline was the real depth over the slope of the
        /// terrain <see cref="SlopeSpan"/> m either side), and the swash drew the polygon itself in white.
        /// - The surf's lines now follow a SMOOTH seabed: a cubic B-spline through the terrain's heights
        ///   at points 2 x <see cref="SlopeSpan"/> apart (its depth and its slope from the same reads).
        /// - The swash has an edge of its own, <see cref="SwashEdge"/> m from no foam to all of it: at
        ///   the smooth waterline as a bore arrives, draining back over <see cref="SwashDrain"/> s by up
        ///   to <see cref="SwashWidth"/> m (and half as much again, in lobes); the foam thins away in the
        ///   last <see cref="EdgeDepth"/> m of the game's water, wherever that ends first.
        /// </summary>
        public const double SwashEdge = 0.5;

        public const double SwashDrain = 3.0;

        public const double EdgeDepth = 0.06;

        /// <summary>
        /// Outside the surf (the author's picture, 2026-10-24: short streaks of foam out beyond the big
        /// foam): the swell's crests coming in break here and there (spilling breakers) out to
        /// <see cref="OuterReach"/> x the surf's width, in water no deeper than <see cref="OuterDepth"/> m;
        /// each streak's foam lasts <see cref="OuterLife"/> s; where along a crest it breaks, from noise
        /// in blotches <see cref="OuterScale"/> m across, a fresh draw for each wave.
        /// </summary>
        public const double OuterReach = 3.0;

        public const double OuterDepth = 20.0;

        public const double OuterLife = 0.4;

        public const double OuterScale = 80.0;

        /// <summary>
        /// A streak's ends (2026-11-11: they ran out into spears' tips tens of metres long): its strength
        /// comes up over this far inside the noise blotch's edge (m; the distance from the noise over its
        /// own slope), so every end thins away through the lace over about a streak's own width.
        /// </summary>
        public const double StreakTip = 4.0;

        /// <summary>
        /// Fronts foam only on a steep face (Chentanez &amp; Mueller's test is rising fast AND steep): the
        /// game's calm sea rises and falls up to a metre from one water frame to the next (the author's
        /// city, 2026-10-24: 95% within 0.9 m, on a surface sloping 6% at the most), which alone whitened
        /// the open sea at random, a new place every frame. The surface's slope it starts at, and is full at.
        /// </summary>
        public const double FrontSteepStart = 0.06;

        public const double FrontSteepFull = 0.15;

        /// <summary>
        /// The surf's distance from the waterline, kept within a corridor round the real one (the
        /// author's screenshot, 2026-10-25: in a bay the foam closed into a ring). The depth over the
        /// seabed's slope runs away where a bay's floor flattens (1.5 m over a slope of 1%: 150 m), so
        /// the band's edge and the bores' crests closed round the flattest spot. At sea it is held no
        /// nearer than the real distance (ShoreDistance's map) / <see cref="ShoreCorridor"/> and no
        /// farther than x it, give or take <see cref="ShoreSlack"/> m (the map's 8 m samples, the
        /// water's edge wandering with the swell); inland, on flooded land, only no nearer. A mild hold
        /// (the author: "the foam should bend/arc a bit anyway"): the seabed still bends the lines; on a
        /// plane beach the two agree and nothing changes.
        /// </summary>
        public const double ShoreCorridor = 1.5;

        public const double ShoreSlack = 12.0;

        /// <summary>
        /// The layers' ten vectors (_RGFoamLayers, _RGFoamSheet, _RGFoamBubbles, _RGFoamSurf,
        /// _RGFoamScale2, _RGFoamExtra, _RGFoamCrest, _RGFoamAbsorb, _RGFoamOuter, _RGFoamSteep -- the
        /// last also the surf's corridor).
        /// </summary>
        public static double[] LayerVectors()
        {
            return new[]
            {
                FreshLife, ResidualLife, BubbleLife, FreshReflectance,
                ResidualReflectance, ResidualOpacity, FreshHoles, ResidualHoles,
                BodyF, BubbleBackscatter, BubbleDepth, BubbleOpacity,
                BorePeriod, BeachSlope, SetLength, SwashWidth,
                1.0 / FoamPattern.BubbleTile, 1.0 / FoamPattern.CloudTile, Math.Log(FoamPattern.BubbleFeature, 2.0), Math.Log(FoamPattern.CloudFeature, 2.0),
                FreshThickness, BubbleSpread, BubbleEdge, SurfWidth,
                CrestLife, SlopeSpan, SurfDepth, MinSlope,
                AbsorptionRed, AbsorptionGreen, AbsorptionBlue, 0.0,
                OuterReach, OuterDepth, OuterLife, OuterScale,
                FrontSteepStart, FrontSteepFull, ShoreCorridor, ShoreSlack,
            };
        }
    }
}
