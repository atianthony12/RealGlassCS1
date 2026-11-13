using UnityEngine;

namespace RealGlass.PerAsset
{
    /// <summary>
    /// One prop asset's surface: what the player made every copy of it -- glass or water drawn over
    /// the game's own draw -- wherever it stands (on its own, in a building, along a
    /// road), with the prop window's settings for it. A prop with no record is drawn as the game draws
    /// it; a setting left null takes the surface's default (<see cref="Resolve(Color32)"/>), not the
    /// Glass tab's: a prop's pane is clear glass, not a tower's. Pure: tools/test-settings.ps1 runs the
    /// file format on it. Kept in RealGlassAssets.xml as a &lt;Prop&gt; element (AssetFileFormat).
    /// </summary>
    public sealed class PropRecord
    {
        /// <summary>The game's draw only: nothing of ours. Never written (no record = this).</summary>
        public const int Game = 0;

        /// <summary>Glass over the prop (all of it, or its parts shinier than <see cref="Threshold"/>).</summary>
        public const int Glass = 1;

        /// <summary>Still water over the prop's faces that look up, rippled.</summary>
        public const int Water = 2;

        // 3 stays unused: it was the Reflective surface (taken out 2026-11-11, the author's call), and
        // the shader's mode 3 is still the other mods' finish (PropGlassDraw.FinishMode, Api.Reflective).

        /// <summary>A prop's glass by default: the kind "Clear low-E" (GlassPresets value 1).</summary>
        public const int DefaultGlassPreset = 1;

        /// <summary>Still water head-on (n 1.33): what water's Strength 100% reflects.</summary>
        public const float WaterReflectivity = 0.02f;

        /// <summary>Water lets this much of what is under it through by default; the rest is its own color.</summary>
        public const float WaterClarity = 0.7f;

        /// <summary>The Water tab's default color: water's when the caller has no live one.</summary>
        public static readonly Color32 DefaultWaterColour = new Color32(0x02, 0x0E, 0x13, 255);

        /// <summary>The sliders' ranges, as the file writes them (percent of these).</summary>
        public const float ReflectivityMax = 0.6f, ThresholdMax = 0.95f;
        public const float WaterStrengthMax = 3f, RippleStrengthMax = 2f, RippleSizeMin = 0.25f, RippleSizeMax = 4f,
                           RippleSpeedMax = 3f, GlintMax = 2f;

        /// <summary>Polish by default: glass clear (100%).</summary>
        public const float GlassPolish = 1f;

        /// <summary>The asset's name, PropInfo.name ("123456789.Water Surface1 16m_Data"): the same in every city.</summary>
        public string Name;

        /// <summary>The title the game shows for it, for whoever reads the file. Not a setting.</summary>
        public string Title;

        /// <summary><see cref="Glass"/> or <see cref="Water"/>; null (or <see cref="Game"/>) the game's own.</summary>
        public int? Surface;

        /// <summary>0..1: how much of ours goes over the game's prop (glass; water's own Strength is <see cref="WaterStrength"/>).</summary>
        public float? Strength;

        /// <summary>The kind of glass (GlassPresets' value, 0 Custom): what the window shows; glass only.</summary>
        public int? Preset;

        /// <summary>The tint, sRGB: the glass's, or the water's own color (null: the Water tab's).</summary>
        public Color32? Colour;

        /// <summary>0..0.6: the glass's reflectance head-on.</summary>
        public float? Reflectivity;

        /// <summary>0..1: the light it lets through (glass, water).</summary>
        public float? Clarity;

        /// <summary>0..0.95: how shiny a part must be to get ours (1 - _XYSMap.z); 0 all of it.</summary>
        public float? Threshold;

        /// <summary>0..1: 1 a mirror-smooth surface, lower blurs what it reflects (glass: 1 - Frosted).</summary>
        public float? Polish;

        /// <summary>0..3: water's reflection, 1 = real still water.</summary>
        public float? WaterStrength;

        /// <summary>Water's ripples: their slope (0 a still mirror .. 2), their size (0.25 .. 4) and speed (0 frozen .. 3), 1 each by default.</summary>
        public float? RippleStrength, RippleSize, RippleSpeed;

        /// <summary>0..2: the sun's highlight on it, 1 by default.</summary>
        public float? Glint;

        public PropRecord(string name)
        {
            Name = name;
        }

        /// <summary>Nothing of ours on it: the game's own prop.</summary>
        public bool IsEmpty
        {
            get { return !Surface.HasValue || !IsSurface(Surface.Value); }
        }

        /// <summary>One of ours: Glass or Water.</summary>
        public static bool IsSurface(int value)
        {
            return value == Glass || value == Water;
        }

        /// <summary>The surface it has: <see cref="Game"/>, <see cref="Glass"/> or <see cref="Water"/>.</summary>
        public int SurfaceOrGame
        {
            get { return IsEmpty ? Game : Surface.Value; }
        }

        public PropRecord Clone()
        {
            return (PropRecord)MemberwiseClone();
        }

        /// <summary>Every look setting back to the surface's default (a new surface starts from its own).</summary>
        public void ClearLook()
        {
            Preset = null;
            Colour = null;
            Reflectivity = null;
            Clarity = null;
            Polish = null;
        }

        /// <summary>Same settings (the title aside): a save that would change nothing is skipped.</summary>
        public bool SameSettings(PropRecord other)
        {
            return other != null && SurfaceOrGame == other.SurfaceOrGame && Strength == other.Strength && Preset == other.Preset &&
                   SameColour(Colour, other.Colour) && Reflectivity == other.Reflectivity &&
                   Clarity == other.Clarity && Threshold == other.Threshold && Polish == other.Polish &&
                   WaterStrength == other.WaterStrength && RippleStrength == other.RippleStrength && RippleSize == other.RippleSize &&
                   RippleSpeed == other.RippleSpeed && Glint == other.Glint;
        }

        private static bool SameColour(Color32? a, Color32? b)
        {
            if (a.HasValue != b.HasValue)
                return false;
            return !a.HasValue || ColorText.Same(a.Value, b.Value);
        }

        /// <summary>Every value it draws with: its own, else its kind of glass's, else the surface's default.</summary>
        public struct Look
        {
            public float Strength, Reflectivity, Clarity, Threshold, Polish, WaterStrength, RippleStrength, RippleSize, RippleSpeed,
                         Glint;
            public Color32 Colour;

            /// <summary>The color is the Water tab's (water with no color of its own).</summary>
            public bool WaterTabColour;

            /// <summary>The kind of glass shown (0 Custom).</summary>
            public int Preset;
        }

        /// <summary>Its look, water's color the Water tab's default.</summary>
        public Look Resolve()
        {
            return Resolve(DefaultWaterColour);
        }

        /// <summary>Its look; <paramref name="waterColour"/>: the Water tab's color now, water's when it has none of its own.</summary>
        public Look Resolve(Color32 waterColour)
        {
            var look = new Look
            {
                Strength = Strength ?? 1f,
                Threshold = Threshold ?? 0f,
                WaterStrength = WaterStrength ?? 1f,
                RippleStrength = RippleStrength ?? 1f,
                RippleSize = RippleSize ?? 1f,
                RippleSpeed = RippleSpeed ?? 1f,
                Glint = Glint ?? 1f,
                Preset = GlassPresets.Custom,
            };

            int surface = SurfaceOrGame;
            if (surface == Water)
            {
                // Before round 3 water had a Reflectivity row (head-on, 2% the default): one set and
                // no Strength yet is read as that Strength, at most 300%.
                if (!WaterStrength.HasValue && Reflectivity.HasValue)
                    look.WaterStrength = System.Math.Min(System.Math.Max(Reflectivity.Value / WaterReflectivity, 0f), WaterStrengthMax);
                look.WaterTabColour = !Colour.HasValue;
                look.Colour = Colour ?? waterColour;
                look.Reflectivity = WaterReflectivity;
                look.Clarity = Clarity ?? WaterClarity;
                look.Polish = 1f;
                look.Threshold = 0f;
                return look;
            }

            // Glass: a kind of glass gives its three numbers where the prop has none of its own; a
            // number of its own over no kind is Custom.
            int preset = Preset ?? (Colour.HasValue || Reflectivity.HasValue || Clarity.HasValue ? GlassPresets.Custom : DefaultGlassPreset);
            GlassPreset kind;
            if (!GlassPresets.TryGet(preset, out kind) && !GlassPresets.TryGet(DefaultGlassPreset, out kind))
                kind = default(GlassPreset);
            look.Preset = preset;
            look.Colour = Colour ?? kind.Colour;
            look.Reflectivity = System.Math.Min(Reflectivity ?? kind.Reflectivity, ReflectivityMax);
            look.Clarity = Clarity ?? kind.Clarity;
            look.Polish = Polish ?? GlassPolish;
            return look;
        }

        /// <summary>The surface's name for the log: "glass", "water", "the game's".</summary>
        public static string SurfaceName(int surface)
        {
            return surface == Glass ? "glass" : (surface == Water ? "water" : "the game's");
        }
    }
}
