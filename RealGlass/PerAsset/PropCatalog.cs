using System;
using System.Collections.Generic;
using RealGlass.UI;
using UnityEngine;

namespace RealGlass.PerAsset
{
    /// <summary>
    /// The prop window's rows (2.0.0): the selected prop's surface and its look. Surface (Off / Glass /
    /// Water) first; under it only the rows its surface has (the rest hidden, the window closing up):
    /// glass the building window's rows where they mean the same (strength, kind of glass,
    /// reflectivity, clarity, shininess needed) and Frosted; water its Strength (100% real still water,
    /// up to 300%), its color (the Water tab's unless it has its own), clarity and ripples. Both: the
    /// sun's glint. Each row shows the prop's value, else the surface's default
    /// (PropRecord.Resolve); moving one gives the prop that value; a row's reset puts the default back.
    /// </summary>
    /// <remarks>
    /// The rows are the panel's ordinary rows over settings that are NOT saved (Setting's saved =
    /// false), re-read from the record before every refresh (<see cref="Sync"/>), as AssetCatalog's.
    /// </remarks>
    public sealed class PropCatalog
    {
        private static PropCatalog _instance;

        private List<Row> _rows;
        private IntSetting _surface, _preset;
        private FloatSetting _strength, _waterStrength, _reflectivity, _clarity, _threshold, _frosted,
                             _rippleStrength, _rippleSize, _rippleSpeed, _glint;
        private ColorSetting _colour;

        public static PropCatalog Instance
        {
            get { return _instance ?? (_instance = new PropCatalog()); }
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

        private static PropInfo Selected
        {
            get { return PropSelection.Selected; }
        }

        private static bool CanChoose()
        {
            return Selected != null && PropSelection.CanDraw(Selected);
        }

        /// <summary>A prop chosen: a building's list with none clicked yet shows no rows at all.</summary>
        private static bool HasProp()
        {
            return Selected != null;
        }

        private bool Is(int surface)
        {
            return CanChoose() && _surface.value == surface;
        }

        private bool IsGlass() { return Is(PropRecord.Glass); }
        private bool IsWater() { return Is(PropRecord.Water); }
        private bool IsGlassOrWater() { return IsGlass() || IsWater(); }
        private bool HasSurface() { return CanChoose() && _surface.value != PropRecord.Game; }

        /// <summary>The Water tab's color now (the water's default), outside a city the default's.</summary>
        private static Color32 WaterColour()
        {
            return Settings.IsInitialised ? Water.WaterController.TabColour() : PropRecord.DefaultWaterColour;
        }

        private void Build()
        {
            Settings.Init();
            GlassPreset clear;
            GlassPresets.TryGet(PropRecord.DefaultGlassPreset, out clear);

            _surface = new IntSetting("PropSurface", PropRecord.Game, false);
            _strength = new FloatSetting("AssetGlassStrength", 1f, false);
            _waterStrength = new FloatSetting("PropWaterStrength", 1f, false);
            _preset = new IntSetting("AssetGlassPreset", PropRecord.DefaultGlassPreset, false);
            _colour = new ColorSetting("PropColor", clear.Colour, false);
            _reflectivity = new FloatSetting("AssetGlassReflectivity", clear.Reflectivity, false);
            _clarity = new FloatSetting("AssetGlassClarity", clear.Clarity, false);
            _frosted = new FloatSetting("PropFrosted", 0f, false);
            _rippleStrength = new FloatSetting("PropRippleStrength", 1f, false);
            _rippleSize = new FloatSetting("PropRippleSize", 1f, false);
            _rippleSpeed = new FloatSetting("PropRippleSpeed", 1f, false);
            _glint = new FloatSetting("PropSunGlint", 1f, false);
            _threshold = new FloatSetting("PropThreshold", 0f, false);

            var rows = new List<Row>();
            rows.Add(new Row
            {
                Kind = RowKind.Choice,
                Int = _surface,
                DefaultInt = PropRecord.Game,
                ChoiceKeys = new[] { "Props.Off", "Props.Glass", "Props.Water" },
                ChoiceValues = new[] { PropRecord.Game, PropRecord.Glass, PropRecord.Water },
                Enabled = CanChoose,
                ResetHintKey = "Reset.Default",
                Overridden = () => Has(r => !r.IsEmpty),
                ClearOverride = () => Change(r => r.Surface = null, "back to the game's own"),
                Shown = HasProp,
                AfterChange = () => Change(r =>
                {
                    // Each surface starts from its own defaults: a glass's tint and reflectivity are
                    // no water's.
                    if (r.SurfaceOrGame != _surface.value)
                        r.ClearLook();
                    r.Surface = _surface.value == PropRecord.Game ? (int?)null : _surface.value;
                }, "surface " + PropRecord.SurfaceName(_surface.value)),
            });

            rows.Add(Percent(_strength, 0f, 100f, IsGlass, r => r.Strength.HasValue, (r, v) => r.Strength = v,
                r => r.Strength = null, "strength"));
            // (A water's Reflectivity from before this row reads as its Strength: the reset clears both.)
            rows.Add(Percent(_waterStrength, 0f, PropRecord.WaterStrengthMax * 100f, IsWater, r => r.WaterStrength.HasValue || r.Reflectivity.HasValue,
                (r, v) => r.WaterStrength = v, r =>
                {
                    r.WaterStrength = null;
                    r.Reflectivity = null;
                }, "water strength"));

            rows.Add(new Row
            {
                Kind = RowKind.Choice,
                Int = _preset,
                DefaultInt = PropRecord.DefaultGlassPreset,
                // The building window's choices, in its order: Custom last.
                ChoiceKeys = new[]
                {
                    "GlassPreset.ReflectiveBlue", "GlassPreset.SilverReflective", "GlassPreset.OceanBlue", "GlassPreset.AquaBlue",
                    "GlassPreset.SkyBlue", "GlassPreset.Green", "GlassPreset.Bronze", "GlassPreset.Grey", "GlassPreset.ClearLowE",
                    "GlassPreset.GoldReflective", "GlassPreset.Custom",
                },
                ChoiceValues = new[] { 10, 8, 3, 2, 4, 5, 6, 7, 1, 9, 0 },
                Enabled = CanChoose,
                Shown = IsGlass,
                ResetHintKey = "Reset.Default",
                Overridden = () => Has(r => r.Preset.HasValue || r.Colour.HasValue || r.Reflectivity.HasValue || r.Clarity.HasValue),
                ClearOverride = () => Change(r =>
                {
                    r.Preset = null;
                    r.Colour = null;
                    r.Reflectivity = null;
                    r.Clarity = null;
                }, "kind of glass back to the default"),
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
                Kind = RowKind.Colour,
                Colour = _colour,
                DefaultColour = clear.Colour,
                Enabled = CanChoose,
                Shown = HasSurface,
                ResetHintKey = "Reset.Default",
                Overridden = () => Has(r => r.Colour.HasValue),
                ClearOverride = () => Change(r => r.Colour = null, "colour back to the default"),
                AfterChange = () => Change(r =>
                {
                    r.Colour = _colour.value;
                    MadeCustom(r);
                }, "colour " + ColorText.Format(_colour.value)),
            });

            rows.Add(Percent(_reflectivity, 0f, PropRecord.ReflectivityMax * 100f, IsGlass, r => r.Reflectivity.HasValue, (r, v) =>
            {
                r.Reflectivity = v;
                MadeCustom(r);
            }, r => r.Reflectivity = null, "reflectivity"));

            rows.Add(Percent(_clarity, 0f, 100f, IsGlassOrWater, r => r.Clarity.HasValue, (r, v) =>
            {
                r.Clarity = v;
                MadeCustom(r);
            }, r => r.Clarity = null, "clarity"));

            rows.Add(Percent(_frosted, 0f, 100f, IsGlass, r => r.Polish.HasValue, (r, v) => r.Polish = 1f - v, r => r.Polish = null, "frosted"));

            rows.Add(Percent(_rippleStrength, 0f, PropRecord.RippleStrengthMax * 100f, IsWater, r => r.RippleStrength.HasValue,
                (r, v) => r.RippleStrength = v, r => r.RippleStrength = null, "ripple strength"));
            rows.Add(Percent(_rippleSize, PropRecord.RippleSizeMin * 100f, PropRecord.RippleSizeMax * 100f, IsWater, r => r.RippleSize.HasValue,
                (r, v) => r.RippleSize = v, r => r.RippleSize = null, "ripple size"));
            rows.Add(Percent(_rippleSpeed, 0f, PropRecord.RippleSpeedMax * 100f, IsWater, r => r.RippleSpeed.HasValue,
                (r, v) => r.RippleSpeed = v, r => r.RippleSpeed = null, "ripple speed"));

            rows.Add(Percent(_glint, 0f, PropRecord.GlintMax * 100f, HasSurface, r => r.Glint.HasValue, (r, v) => r.Glint = v, r => r.Glint = null,
                "sun glint"));
            rows.Add(Percent(_threshold, 0f, PropRecord.ThresholdMax * 100f, IsGlass, r => r.Threshold.HasValue,
                (r, v) => r.Threshold = v, r => r.Threshold = null, "shininess needed"));

            _rows = rows;
            Sync();
        }

        /// <summary>A color or number of its own: the kind of glass reads Custom.</summary>
        private static void MadeCustom(PropRecord r)
        {
            if (r.SurfaceOrGame == PropRecord.Glass)
                r.Preset = GlassPresets.Custom;
        }

        private Row Percent(FloatSetting setting, float min, float max, Func<bool> shown, Func<PropRecord, bool> has,
                            Action<PropRecord, float> set, Action<PropRecord> clear, string what)
        {
            return new Row
            {
                Kind = RowKind.Percent,
                Float = setting,
                Min = min, Max = max, Step = 1f,
                Enabled = CanChoose,
                Shown = shown,
                ResetHintKey = "Reset.Default",
                Overridden = () => Has(has),
                ClearOverride = () => Change(clear, what + " back to the default"),
                AfterChange = () => Change(r => set(r, setting.value), what + " " + Mathf.RoundToInt(setting.value * 100f) + "%"),
            };
        }

        private static bool Has(Func<PropRecord, bool> test)
        {
            PropRecord record = Selected != null ? AssetFile.GetProp(Selected.name) : null;
            return record != null && test(record);
        }

        /// <summary>One change to the selected prop: stored, on every copy, logged.</summary>
        private void Change(Action<PropRecord> edit, string what)
        {
            if (Selected == null)
                return;

            PropRecord stored = AssetFile.GetProp(Selected.name);
            PropRecord record = stored != null ? stored.Clone() : new PropRecord(Selected.name);
            edit(record);
            PropSelection.Store(record, what);
            Sync();
        }

        /// <summary>Every row back to the selected prop: its own value, else its surface's default.</summary>
        public void Sync()
        {
            if (_rows == null || !Settings.IsInitialised)
                return;

            PropRecord record = Selected != null ? AssetFile.GetProp(Selected.name) : null;
            PropRecord shown = record ?? new PropRecord(Selected != null ? Selected.name : "");
            PropRecord.Look look = shown.Resolve(WaterColour());
            _surface.value = shown.SurfaceOrGame;
            _strength.value = look.Strength;
            _waterStrength.value = look.WaterStrength;
            _preset.value = look.Preset;
            _colour.value = look.Colour;
            _reflectivity.value = Mathf.Min(look.Reflectivity, PropRecord.ReflectivityMax);
            _clarity.value = look.Clarity;
            _frosted.value = 1f - look.Polish;
            _rippleStrength.value = look.RippleStrength;
            _rippleSize.value = look.RippleSize;
            _rippleSpeed.value = look.RippleSpeed;
            _glint.value = look.Glint;
            _threshold.value = look.Threshold;
        }
    }
}
