using UnityEngine;

namespace RealGlass.PerAsset
{
    /// <summary>
    /// One building asset's own glass: the settings the player gave every copy of it, each one
    /// overriding the global setting of the same name. A setting left null follows the global one.
    /// Pure (Color32 is a plain struct): tools/test-settings.ps1 runs the file format on it.
    /// </summary>
    public sealed class AssetRecord
    {
        /// <summary>The asset's name, BuildingInfo.name ("123456789.Glass Tower_Data"): the same in every city.</summary>
        public string Name;

        /// <summary>The title the game shows for it, for whoever reads the file. Not a setting.</summary>
        public string Title;

        /// <summary>0..1: our glass over the asset's own windows (GlassStrength).</summary>
        public float? Strength;

        /// <summary>0..1: how much of the lines the asset painted on its glass stay (GlassPaintedDetail).</summary>
        public float? PaintedDetail;

        /// <summary>The kind of glass (GlassPresets' value, 0 = Custom): what the panel shows. A preset with
        /// no colour, reflectivity or clarity of the asset's own gives them its numbers.</summary>
        public int? Preset;

        /// <summary>The glass's tint, sRGB (GlassColor).</summary>
        public Color32? Colour;

        /// <summary>0..0.6 (GlassReflectivity).</summary>
        public float? Reflectivity;

        /// <summary>0..1 (GlassClarity).</summary>
        public float? Clarity;

        /// <summary>0..1: how much of the asset's own painted glass colour is kept (GlassOwnColour).</summary>
        public float? OwnColour;

        /// <summary>
        /// 0.10..0.95: how shiny a part of this asset must be to count as glass (GlassThreshold). Lower
        /// than the global row goes (0.40): an asset whose glass is painted dull still gets glass, and
        /// the walls a low threshold pulls in are this asset's alone.
        /// </summary>
        public float? Threshold;

        /// <summary>The windows this asset lights at night count as glass too (GlassNightWindows).</summary>
        public bool? NightWindows;

        /// <summary>0..1: dirt on its panes (GlassDirt): over the Glass tab's, as a cleaner or dirtier building.</summary>
        public float? Dirt;

        /// <summary>Rooms behind this asset's windows (InteriorsEnabled): false keeps the game's own windows.</summary>
        public bool? Interiors;

        /// <summary>
        /// Curtains or blinds in its rooms' windows: true at the Interiors tab's share (InteriorsCurtains),
        /// false none on any copy of it (a fire station's doors).
        /// </summary>
        public bool? Curtains;

        /// <summary>
        /// Its rooms lit where and when the game lights its windows (InteriorsFollowIllumination: its
        /// illumination map, the game's timing): true so, false ours (the Interiors tab's shares).
        /// </summary>
        public bool? FollowIllumination;

        /// <summary>
        /// Its LODs' glass (LodGlassFromModel, Glass/LodGlass.cs): true the model's, whatever the LOD has;
        /// false the LOD's own, always. Null: the switch, and the comparison.
        /// </summary>
        public bool? LodGlass;

        public AssetRecord(string name)
        {
            Name = name;
        }

        /// <summary>Nothing of its own: the asset follows every global setting.</summary>
        public bool IsEmpty
        {
            get
            {
                return !Strength.HasValue && !PaintedDetail.HasValue && !HasGlass && !Interiors.HasValue && !Curtains.HasValue &&
                       !FollowIllumination.HasValue && !LodGlass.HasValue;
            }
        }


        /// <summary>
        /// The glass itself, or what counts as glass, is the asset's own in part or whole: it needs a
        /// slot of the glass table (AssetTextures), which holds all of these, resolved.
        /// </summary>
        public bool HasGlass
        {
            get
            {
                return Preset.HasValue || Colour.HasValue || Reflectivity.HasValue || Clarity.HasValue || OwnColour.HasValue ||
                       Threshold.HasValue || NightWindows.HasValue || Dirt.HasValue;
            }
        }

        public AssetRecord Clone()
        {
            return (AssetRecord)MemberwiseClone();
        }

        /// <summary>
        /// The settings of <paramref name="top"/> where it has them, else <paramref name="under"/>'s: a
        /// player's own glass for an asset (RealGlassAssets.xml) over the glass its creator shipped with
        /// it (RealGlassDefinition.xml). Either may be null; with one missing the other is returned
        /// as it is. A kind of glass gives its colour, reflectivity and clarity to the side that chose
        /// it before the two are laid together: a player's kind of glass is its three numbers, not
        /// the creator's colour under the player's name.
        /// </summary>
        public static AssetRecord Over(AssetRecord top, AssetRecord under)
        {
            if (top == null || top.IsEmpty)
                return under;
            if (under == null || under.IsEmpty)
                return top;

            AssetRecord t = WithPresetNumbers(top), u = WithPresetNumbers(under);
            var r = new AssetRecord(t.Name)
            {
                Title = t.Title ?? u.Title,
                Strength = t.Strength ?? u.Strength,
                PaintedDetail = t.PaintedDetail ?? u.PaintedDetail,
                Colour = t.Colour ?? u.Colour,
                Reflectivity = t.Reflectivity ?? u.Reflectivity,
                Clarity = t.Clarity ?? u.Clarity,
                OwnColour = t.OwnColour ?? u.OwnColour,
                Threshold = t.Threshold ?? u.Threshold,
                NightWindows = t.NightWindows ?? u.NightWindows,
                Dirt = t.Dirt ?? u.Dirt,
                Interiors = t.Interiors ?? u.Interiors,
                Curtains = t.Curtains ?? u.Curtains,
                FollowIllumination = t.FollowIllumination ?? u.FollowIllumination,
                LodGlass = t.LodGlass ?? u.LodGlass,
            };

            // The kind of glass shown: the top's; a number of its own over the creator's kind is Custom.
            if (t.Preset.HasValue)
                r.Preset = t.Preset;
            else if (t.Colour.HasValue || t.Reflectivity.HasValue || t.Clarity.HasValue)
                r.Preset = GlassPresets.Custom;
            else
                r.Preset = u.Preset;
            return r;
        }

        /// <summary>A copy whose kind of glass has written its three numbers where the record has none of its own.</summary>
        private static AssetRecord WithPresetNumbers(AssetRecord record)
        {
            GlassPreset preset;
            if (!record.Preset.HasValue || !GlassPresets.TryGet(record.Preset.Value, out preset))
                return record;

            AssetRecord r = record.Clone();
            r.Colour = r.Colour ?? preset.Colour;
            r.Reflectivity = r.Reflectivity ?? preset.Reflectivity;
            r.Clarity = r.Clarity ?? preset.Clarity;
            return r;
        }

        /// <summary>Same settings (the title aside): a save that would change nothing is skipped.</summary>
        public bool SameSettings(AssetRecord other)
        {
            if (other == null)
                return false;

            return Strength == other.Strength && PaintedDetail == other.PaintedDetail && Preset == other.Preset &&
                   SameColour(Colour, other.Colour) && Reflectivity == other.Reflectivity && Clarity == other.Clarity &&
                   OwnColour == other.OwnColour && Threshold == other.Threshold && NightWindows == other.NightWindows && Dirt == other.Dirt &&
                   Interiors == other.Interiors && Curtains == other.Curtains && FollowIllumination == other.FollowIllumination &&
                   LodGlass == other.LodGlass;
        }

        private static bool SameColour(Color32? a, Color32? b)
        {
            if (a.HasValue != b.HasValue)
                return false;

            return !a.HasValue || ColorText.Same(a.Value, b.Value);
        }
    }
}
