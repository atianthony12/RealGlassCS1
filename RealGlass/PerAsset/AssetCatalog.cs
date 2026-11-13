using System;
using System.Collections.Generic;
using RealGlass.Glass;
using RealGlass.UI;
using UnityEngine;

namespace RealGlass.PerAsset
{
    /// <summary>
    /// The rows of an asset window: one building asset's own glass. Each row shows the value the asset
    /// has -- its own, or what it follows -- and moving it gives the asset that value of its own. Two
    /// windows, two sources (<see cref="AssetSource"/>): the building window in a city
    /// (<see cref="Picked"/>: the building picked, for every copy of it in every city,
    /// RealGlassAssets.xml) and the creator window in the asset editor (<see cref="Edited"/>: the
    /// building being made, for every player, RealGlassDefinition.xml), which has the preview's
    /// switches above them. A row's reset puts it back to what the asset follows: the global
    /// setting (or its creator's) in a city, each player's own setting in the editor. A switch or a
    /// slider moved back onto what it follows does the same (no value of its own: its name is not
    /// coloured); the kind of glass and its numbers stay the asset's once chosen.
    /// </summary>
    /// <remarks>
    /// The rows are the panel's ordinary rows over settings that are NOT saved (Setting's saved =
    /// false): they only hold what is shown, re-read from the source before every refresh of the
    /// window (<see cref="Sync"/>). What is kept is the asset's record. Not in SettingsCatalog:
    /// RealGlass.xml and "Reset all settings" never see them. A window's status line and its buttons
    /// are the window's own.
    /// </remarks>
    public sealed class AssetCatalog
    {
        private static AssetCatalog _picked, _edited;

        private readonly AssetSource _source;
        private readonly bool _creator;
        private List<Row> _rows;

        // What the rows show: the asset's glass.
        private FloatSetting _strength, _detail, _reflectivity, _clarity, _own, _threshold, _dirt;
        private IntSetting _preset, _lodGlass;
        private ColorSetting _colour;
        private BoolSetting _nightWindows, _interiors, _curtains, _illumination;

        // The Interiors tab's curtains share is above 0: in a city, with none there is nothing to switch.
        private bool _curtainsShare;

        /// <summary>The building window's rows: the building picked in a city.</summary>
        public static AssetCatalog Picked
        {
            get { return _picked ?? (_picked = new AssetCatalog(new PickedAsset(), false)); }
        }

        /// <summary>The creator window's rows: the building being made in the asset editor.</summary>
        public static AssetCatalog Edited
        {
            get { return _edited ?? (_edited = new AssetCatalog(new EditedAsset(), true)); }
        }

        private AssetCatalog(AssetSource source, bool creator)
        {
            _source = source;
            _creator = creator;
        }

        public AssetSource Source
        {
            get { return _source; }
        }

        public List<Row> Rows
        {
            get
            {
                if (_rows == null)
                    Build();
                return _rows;
            }
        }

        private bool Editable()
        {
            return _source.CanEdit;
        }

        private void Build()
        {
            Settings.Init();

            _strength = new FloatSetting("AssetGlassStrength", Settings.Defaults.GlassStrength, false);
            _detail = new FloatSetting("AssetGlassPaintedDetail", Settings.Defaults.GlassPaintedDetail, false);
            _preset = new IntSetting("AssetGlassPreset", Settings.Defaults.GlassPreset, false);
            _colour = new ColorSetting("AssetGlassColor", Settings.Defaults.GlassColor, false);
            _reflectivity = new FloatSetting("AssetGlassReflectivity", Settings.Defaults.GlassReflectivity, false);
            _clarity = new FloatSetting("AssetGlassClarity", Settings.Defaults.GlassClarity, false);
            _own = new FloatSetting("AssetGlassOwnColour", Settings.Defaults.GlassOwnColour, false);
            _threshold = new FloatSetting("AssetGlassThreshold", Settings.Defaults.GlassThreshold, false);
            _dirt = new FloatSetting("AssetGlassDirt", Settings.Defaults.GlassDirt, false);
            _nightWindows = new BoolSetting("AssetGlassNightWindows", Settings.Defaults.GlassNightWindows, false);
            _interiors = new BoolSetting("AssetInteriorsEnabled", Settings.Defaults.InteriorsEnabled, false);
            _curtains = new BoolSetting("AssetInteriorsCurtains", Settings.Defaults.InteriorsCurtains > 0f, false);
            _illumination = new BoolSetting("AssetInteriorsFollowIllumination", Settings.Defaults.InteriorsFollowIllumination, false);
            _lodGlass = new IntSetting("AssetLodGlass", Glass.LodGlass.ModeAuto, false);

            var rows = new List<Row>();

            if (_creator)
                AddPreview(rows);

            rows.Add(Percent(_strength, "AssetGlass", 100f, r => r.Strength.HasValue, (r, v) => r.Strength = v, r => r.Strength = null,
                () => Followed(b => b.Strength, _source.Global.Strength), "strength"));
            rows.Add(Percent(_detail, null, 100f, r => r.PaintedDetail.HasValue, (r, v) => r.PaintedDetail = v, r => r.PaintedDetail = null,
                () => Followed(b => b.PaintedDetail, _source.Global.PaintedDetail), "painted detail"));

            rows.Add(new Row
            {
                Kind = RowKind.Choice,                Int = _preset,
                DefaultInt = Settings.Defaults.GlassPreset,
                // The global row's choices, in its order (SettingsCatalog): Custom last.
                ChoiceKeys = new[]
                {
                    "GlassPreset.ReflectiveBlue", "GlassPreset.SilverReflective", "GlassPreset.OceanBlue", "GlassPreset.AquaBlue",
                    "GlassPreset.SkyBlue", "GlassPreset.Green", "GlassPreset.Bronze", "GlassPreset.Grey", "GlassPreset.ClearLowE",
                    "GlassPreset.GoldReflective", "GlassPreset.Custom",
                },
                ChoiceValues = new[] { 10, 8, 3, 2, 4, 5, 6, 7, 1, 9, 0 },
                Enabled = Editable,
                ResetHintKey = _source.ResetHintKey,
                Overridden = () => Has(r => r.Preset.HasValue || r.Colour.HasValue || r.Reflectivity.HasValue || r.Clarity.HasValue),
                ClearOverride = () => Change(r =>
                {
                    r.Preset = null;
                    r.Colour = null;
                    r.Reflectivity = null;
                    r.Clarity = null;
                }, "kind of glass back to the one it follows"),
                AfterChange = () => Change(r =>
                {
                    r.Preset = _preset.value;
                    GlassPreset preset;
                    if (GlassPresets.TryGet(_preset.value, out preset))
                    {
                        r.Colour = preset.Colour;
                        r.Reflectivity = preset.Reflectivity;
                        r.Clarity = preset.Clarity;
                    }
                }, "kind of glass " + GlassPresets.LogName(_preset.value)),
            });

            rows.Add(new Row
            {
                Kind = RowKind.Colour,                Colour = _colour,
                DefaultColour = Settings.Defaults.GlassColor,
                Enabled = Editable,
                ResetHintKey = _source.ResetHintKey,
                Overridden = () => Has(r => r.Colour.HasValue),
                ClearOverride = () => Change(r => r.Colour = null, "colour back to the one it follows"),
                AfterChange = () => Change(r =>
                {
                    r.Colour = _colour.value;
                    r.Preset = GlassPresets.Custom;
                }, "colour " + ColorText.Format(_colour.value)),
            });

            // Part of the kind of glass (moving one makes it Custom): always the asset's own.
            rows.Add(Percent(_reflectivity, null, 60f, r => r.Reflectivity.HasValue, (r, v) =>
            {
                r.Reflectivity = v;
                r.Preset = GlassPresets.Custom;
            }, r => r.Reflectivity = null, null, "reflectivity"));

            rows.Add(Percent(_clarity, null, 100f, r => r.Clarity.HasValue, (r, v) =>
            {
                r.Clarity = v;
                r.Preset = GlassPresets.Custom;
            }, r => r.Clarity = null, null, "clarity"));

            rows.Add(Percent(_own, null, 100f, r => r.OwnColour.HasValue, (r, v) => r.OwnColour = v, r => r.OwnColour = null,
                () => Followed(b => b.OwnColour, _source.Global.OwnColour), "own colour"));

            // Dirt on its panes (the Glass tab's Dirt, for this asset): a cleaner or dirtier building.
            rows.Add(Percent(_dirt, null, AssetFileFormat.DirtMax, r => r.Dirt.HasValue, (r, v) => r.Dirt = v, r => r.Dirt = null,
                () => Followed(b => b.Dirt, _source.Global.Dirt), "dirt"));

            // What counts as this asset's glass: further down than the global row (40%), for glass
            // painted dull; the walls a low value pulls in are this asset's alone. The mask view
            // ("Show what counts as glass") shows it per asset.
            rows.Add(Percent(_threshold, "WhatIsGlass", AssetFileFormat.ThresholdMin, AssetFileFormat.ThresholdMax,
                r => r.Threshold.HasValue, (r, v) => r.Threshold = v, r => r.Threshold = null,
                () => Followed(b => b.Threshold, GlobalThreshold()), "shininess needed"));

            rows.Add(new Row
            {
                Kind = RowKind.Toggle,                Bool = _nightWindows,
                DefaultBool = Settings.Defaults.GlassNightWindows,
                Enabled = Editable,
                ResetHintKey = _source.ResetHintKey,
                Overridden = () => Has(r => r.NightWindows.HasValue),
                ClearOverride = () => Change(r => r.NightWindows = null, "lit windows back to the setting it follows"),
                AfterChange = () => Change(r => r.NightWindows = OwnOrNone(_nightWindows.value,
                        Followed(b => b.NightWindows, _source.Global.NightWindows)),
                    "lit-at-night windows count as glass: " + (_nightWindows.value ? "yes" : "no")),
            });

            // Its LODs' glass (the Glass tab's "LODs show the model's glass", for this asset):
            // Automatic follows that switch and the comparison; the model's whatever the LOD has;
            // the LOD's own always. The building window's status line says what Automatic found.
            rows.Add(new Row
            {
                Kind = RowKind.Choice,
                Int = _lodGlass,
                DefaultInt = Glass.LodGlass.ModeAuto,
                ChoiceKeys = new[] { "Asset.LodGlass.Auto", "Asset.LodGlass.Model", "Asset.LodGlass.Own" },
                ChoiceValues = new[] { Glass.LodGlass.ModeAuto, Glass.LodGlass.ModeModel, Glass.LodGlass.ModeOwn },
                Enabled = Editable,
                ResetHintKey = _source.ResetHintKey,
                Overridden = () => Has(r => r.LodGlass.HasValue),
                ClearOverride = () => Change(r => r.LodGlass = null, "LOD glass back to automatic"),
                AfterChange = () => Change(r =>
                    {
                        bool? chosen = _lodGlass.value == Glass.LodGlass.ModeAuto ? (bool?)null : _lodGlass.value == Glass.LodGlass.ModeModel;
                        r.LodGlass = chosen == FollowedLodGlass() ? null : chosen;
                    },
                    "LOD glass: " + (_lodGlass.value == Glass.LodGlass.ModeModel ? "the model's" : (_lodGlass.value == Glass.LodGlass.ModeOwn ? "its own" : "automatic"))),
            });

            // The rooms behind its windows (the Interiors tab's switch, for this asset): off keeps the
            // game's own windows on every copy of it.
            rows.Add(new Row
            {
                Kind = RowKind.Toggle,
                Group = "AssetInteriors",
                Bool = _interiors,
                DefaultBool = Settings.Defaults.InteriorsEnabled,
                Enabled = Editable,
                ResetHintKey = _source.ResetHintKey,
                Overridden = () => Has(r => r.Interiors.HasValue),
                ClearOverride = () => Change(r => r.Interiors = null, "rooms back to the Interiors tab's switch"),
                AfterChange = () => Change(r => r.Interiors = OwnOrNone(_interiors.value,
                        Followed(b => b.Interiors, _source.Global.Interiors)),
                    "rooms behind the windows: " + (_interiors.value ? "yes" : "no")),
            });

            // Curtains or blinds in those rooms (the Interiors tab's share, for this asset): off for an
            // asset whose glass is no window of a room, a fire station's doors. Grey while it shows no
            // rooms; in a city also while that share is 0 (the editor's switch is for every player).
            rows.Add(new Row
            {
                Kind = RowKind.Toggle,
                Bool = _curtains,
                DefaultBool = Settings.Defaults.InteriorsCurtains > 0f,
                Enabled = () => Editable() && _interiors.value && (_creator || _curtainsShare),
                ResetHintKey = _source.ResetHintKey,
                Overridden = () => Has(r => r.Curtains.HasValue),
                ClearOverride = () => Change(r => r.Curtains = null, "curtains back to the Interiors tab's share"),
                AfterChange = () => Change(r => r.Curtains = OwnOrNone(_curtains.value,
                        Followed(b => b.Curtains, _source.Global.InteriorsCurtains > 0f)),
                    "curtains or blinds in its rooms: " + (_curtains.value ? "yes" : "no")),
            });

            // Which of its rooms are lit (the Interiors tab's switch, for this asset): where and when the
            // game lights its windows -- its illumination map, a creator's own -- or ours. Grey while it
            // shows no rooms.
            rows.Add(new Row
            {
                Kind = RowKind.Toggle,
                Bool = _illumination,
                DefaultBool = Settings.Defaults.InteriorsFollowIllumination,
                Enabled = () => Editable() && _interiors.value,
                ResetHintKey = _source.ResetHintKey,
                Overridden = () => Has(r => r.FollowIllumination.HasValue),
                ClearOverride = () => Change(r => r.FollowIllumination = null, "lit rooms back to the Interiors tab's switch"),
                AfterChange = () => Change(r => r.FollowIllumination = OwnOrNone(_illumination.value,
                        Followed(b => b.FollowIllumination, _source.Global.InteriorsFollowIllumination)),
                    "rooms lit by its illumination map: " + (_illumination.value ? "yes" : "no")),
            });

            _rows = rows;
            Sync();
        }

        /// <summary>
        /// The creator window's preview, above the asset's rows (CreatorAsset's switches, never saved):
        /// the mod's glass (off: the asset as the game draws it, how most of a creator's subscribers see
        /// it; on: at the mod's default settings; the asset's rows grey out while it is off), what counts
        /// as glass, the rooms by day (the asset editor has no night).
        /// </summary>
        private static void AddPreview(List<Row> rows)
        {
            rows.Add(Preview(CreatorAsset.ShowGlass, CreatorAsset.DefaultShowGlass, "CreatorPreview", null, "the mod's glass"));
            rows.Add(Preview(CreatorAsset.ShowMask, CreatorAsset.DefaultShowMask, null, GlassShows, "what counts as glass"));
            rows.Add(Preview(CreatorAsset.RoomsByDay, CreatorAsset.DefaultRoomsByDay, null, GlassShows, "the rooms by day"));
        }

        private static bool GlassShows()
        {
            return CreatorAsset.ShowGlass.value;
        }

        private static Row Preview(BoolSetting setting, bool initial, string group, Func<bool> enabled, string what)
        {
            return new Row
            {
                Kind = RowKind.Toggle,
                Group = group,
                Bool = setting,
                DefaultBool = initial,
                Enabled = enabled,
                AfterChange = () =>
                {
                    GlassController.SettingsChanged();
                    Log.Msg("creator: preview with " + what + " " + (setting.value ? "on" : "off"));
                },
            };
        }

        private Row Percent(FloatSetting setting, string group, float max, Func<AssetRecord, bool> has,
                            Action<AssetRecord, float> set, Action<AssetRecord> clear, Func<float> followed, string what)
        {
            return Percent(setting, group, 0f, max, has, set, clear, followed, what);
        }

        /// <param name="followed">What the row shows with no value of its own; null: every value moved to is the asset's own.</param>
        private Row Percent(FloatSetting setting, string group, float min, float max, Func<AssetRecord, bool> has,
                            Action<AssetRecord, float> set, Action<AssetRecord> clear, Func<float> followed, string what)
        {
            return new Row
            {
                Kind = RowKind.Percent,                Group = group,
                Float = setting,
                Min = min, Max = max, Step = 1f,
                Enabled = Editable,
                ResetHintKey = _source.ResetHintKey,
                Overridden = () => Has(has),
                ClearOverride = () => Change(clear, what + " back to the one it follows"),
                AfterChange = () => Change(r =>
                {
                    // On the slider's step it follows: the same percent shown, so nothing of its own.
                    if (followed != null && Mathf.Abs(setting.value - followed()) < SameStep)
                        clear(r);
                    else
                        set(r, setting.value);
                }, what + " " + Mathf.RoundToInt(setting.value * 100f) + "%"),
            };
        }

        /// <summary>Half the percent rows' 1% step: two values closer than this show the same.</summary>
        private const float SameStep = 0.005f;

        /// <summary>
        /// What a row shows where the asset has no value of its own: its creator's (in a city), else
        /// the global setting -- what <see cref="Sync"/> falls back to.
        /// </summary>
        private T Followed<T>(Func<AssetRecord, T?> field, T global) where T : struct
        {
            AssetRecord beneath = _source.HasSelection ? _source.Beneath : null;
            T? value = beneath != null ? field(beneath) : null;
            return value ?? global;
        }

        /// <summary>The threshold an asset with none of its own (nor its creator's) follows: the one read from its textures, else the global.</summary>
        private float GlobalThreshold()
        {
            float detected = _source.DetectedThreshold;
            return detected > 0f ? detected : _source.Global.Threshold;
        }

        /// <summary>The LOD choice the asset follows: its creator's, else Automatic (null).</summary>
        private bool? FollowedLodGlass()
        {
            AssetRecord beneath = _source.HasSelection ? _source.Beneath : null;
            return beneath != null ? beneath.LodGlass : null;
        }

        /// <summary>A switch's value of the asset's own, or none where it is what the asset follows anyway.</summary>
        private static bool? OwnOrNone(bool value, bool followed)
        {
            return value == followed ? (bool?)null : value;
        }

        private bool Has(Func<AssetRecord, bool> test)
        {
            AssetRecord record = _source.Own;
            return record != null && test(record);
        }

        /// <summary>One change to the asset's glass: stored, shown on every copy, logged.</summary>
        private void Change(Action<AssetRecord> edit, string what)
        {
            if (!_source.HasSelection)
                return;

            AssetRecord record = _source.Editable();
            edit(record);
            _source.Store(record, what);
            Sync();
        }

        /// <summary>
        /// Every row back to what the asset has: its own value, else what it follows -- its creator's
        /// (in a city), else the global one (its kind of glass's numbers, where it chose a kind). With
        /// nothing to show, the global ones.
        /// </summary>
        public void Sync()
        {
            if (_rows == null || !Settings.IsInitialised)
                return;

            AssetRecord r = _source.HasSelection ? AssetRecord.Over(_source.Own, _source.Beneath) : null;
            GlassParams g = _source.Global;
            GlassPreset preset = default(GlassPreset);
            bool hasPreset = r != null && r.Preset.HasValue && GlassPresets.TryGet(r.Preset.Value, out preset);

            _strength.value = r != null && r.Strength.HasValue ? r.Strength.Value : g.Strength;
            _detail.value = r != null && r.PaintedDetail.HasValue ? r.PaintedDetail.Value : g.PaintedDetail;
            _own.value = r != null && r.OwnColour.HasValue ? r.OwnColour.Value : g.OwnColour;
            _threshold.value = r != null && r.Threshold.HasValue ? r.Threshold.Value : GlobalThreshold();
            _dirt.value = r != null && r.Dirt.HasValue ? r.Dirt.Value : g.Dirt;
            _nightWindows.value = r != null && r.NightWindows.HasValue ? r.NightWindows.Value : g.NightWindows;
            _interiors.value = r != null && r.Interiors.HasValue ? r.Interiors.Value : g.Interiors;
            _curtainsShare = g.InteriorsCurtains > 0f;
            _curtains.value = r != null && r.Curtains.HasValue ? r.Curtains.Value : _curtainsShare;
            _illumination.value = r != null && r.FollowIllumination.HasValue ? r.FollowIllumination.Value : g.InteriorsFollowIllumination;
            _lodGlass.value = r == null || !r.LodGlass.HasValue ? Glass.LodGlass.ModeAuto
                : (r.LodGlass.Value ? Glass.LodGlass.ModeModel : Glass.LodGlass.ModeOwn);

            Color32 global = new Color32(Channel(g.TintR), Channel(g.TintG), Channel(g.TintB), 255);
            _colour.value = r != null && r.Colour.HasValue ? r.Colour.Value : (hasPreset ? preset.Colour : global);
            _reflectivity.value = r != null && r.Reflectivity.HasValue ? r.Reflectivity.Value
                : (hasPreset ? preset.Reflectivity : g.Reflectivity);
            _clarity.value = r != null && r.Clarity.HasValue ? r.Clarity.Value : (hasPreset ? preset.Clarity : g.Clarity);

            if (r != null && r.Preset.HasValue)
                _preset.value = r.Preset.Value;
            else if (r != null && (r.Colour.HasValue || r.Reflectivity.HasValue || r.Clarity.HasValue))
                _preset.value = GlassPresets.Custom;
            else
                _preset.value = _source.GlobalPreset;
        }

        private static byte Channel(float value)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
        }
    }
}
