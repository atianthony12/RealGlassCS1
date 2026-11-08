using UnityEngine;

namespace RealGlass.PerAsset
{
    /// <summary>
    /// One prop asset's surface: what the player made every copy of it -- glass or water drawn over
    /// the game's own draw -- wherever it stands (on its own, in a building, along a road), with the
    /// building window's glass settings for it. A prop with no record is drawn as the game draws it;
    /// a setting left null takes the surface's default (<see cref="Resolve"/>), not the Glass tab's: a
    /// prop's pane is clear glass, not a tower's. Pure: tools/test-settings.ps1 runs the file format on
    /// it. Kept in RealGlassAssets.xml as a &lt;Prop&gt; element (AssetFileFormat).
    /// </summary>
    public sealed class PropRecord
    {
        /// <summary>The game's draw only: nothing of ours. Never written (no record = this).</summary>
        public const int Game = 0;

        /// <summary>Glass over the prop (all of it, or its parts shinier than <see cref="Threshold"/>).</summary>
        public const int Glass = 1;

        /// <summary>Still water over the prop's faces that look up, rippled.</summary>
        public const int Water = 2;

        /// <summary>A prop's glass by default: the kind "Clear low-E" (GlassPresets value 1).</summary>
        public const int DefaultGlassPreset = 1;

        /// <summary>Still water, head-on (n 1.33), and untinted, all of what is under it seen.</summary>
        public const float WaterReflectivity = 0.02f, WaterClarity = 1f;

        /// <summary>The sliders' ranges, as the file writes them (percent).</summary>
        public const float ReflectivityMax = 0.6f, ThresholdMax = 0.95f;

        /// <summary>The asset's name, PropInfo.name ("123456789.Water Surface1 16m_Data"): the same in every city.</summary>
        public string Name;

        /// <summary>The title the game shows for it, for whoever reads the file. Not a setting.</summary>
        public string Title;

        /// <summary><see cref="Glass"/> or <see cref="Water"/>; null (or <see cref="Game"/>) the game's own.</summary>
        public int? Surface;

        /// <summary>0..1: how much of ours goes over the game's prop.</summary>
        public float? Strength;

        /// <summary>The kind of glass (GlassPresets' value, 0 Custom): what the window shows; glass only.</summary>
        public int? Preset;

        /// <summary>The tint, sRGB: the glass's, or the water's color over what is under it.</summary>
        public Color32? Colour;

        /// <summary>0..0.6: the reflectance head-on.</summary>
        public float? Reflectivity;

        /// <summary>0..1: the light it lets through.</summary>
        public float? Clarity;

        /// <summary>0..0.95: how shiny a part must be to get ours (1 - _XYSMap.z); 0 all of it.</summary>
        public float? Threshold;

        public PropRecord(string name)
        {
            Name = name;
        }

        /// <summary>Nothing of ours on it: the game's own prop.</summary>
        public bool IsEmpty
        {
            get { return !Surface.HasValue || (Surface.Value != Glass && Surface.Value != Water); }
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

        /// <summary>Same settings (the title aside): a save that would change nothing is skipped.</summary>
        public bool SameSettings(PropRecord other)
        {
            return other != null && SurfaceOrGame == other.SurfaceOrGame && Strength == other.Strength && Preset == other.Preset &&
                   SameColour(Colour, other.Colour) && Reflectivity == other.Reflectivity && Clarity == other.Clarity &&
                   Threshold == other.Threshold;
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
            public float Strength, Reflectivity, Clarity, Threshold;
            public Color32 Colour;
            /// <summary>The kind of glass shown (0 Custom).</summary>
            public int Preset;
        }

        public Look Resolve()
        {
            var look = new Look { Strength = Strength ?? 1f, Threshold = Threshold ?? 0f };
            if (SurfaceOrGame == Water)
            {
                look.Colour = Colour ?? new Color32(255, 255, 255, 255);
                look.Reflectivity = Reflectivity ?? WaterReflectivity;
                look.Clarity = Clarity ?? WaterClarity;
                look.Preset = GlassPresets.Custom;
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
            look.Reflectivity = Reflectivity ?? kind.Reflectivity;
            look.Clarity = Clarity ?? kind.Clarity;
            return look;
        }

        /// <summary>The surface's name for the log: "glass", "water", "the game's".</summary>
        public static string SurfaceName(int surface)
        {
            return surface == Glass ? "glass" : (surface == Water ? "water" : "the game's");
        }
    }
}
