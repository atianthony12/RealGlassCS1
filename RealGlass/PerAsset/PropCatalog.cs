using System;
using System.Collections.Generic;
using RealGlass.UI;
using UnityEngine;

namespace RealGlass.PerAsset
{
    /// <summary>
    /// The prop window's rows (1.1.9): the selected prop's surface and glass, as the building window
    /// has a building's -- the same rows and the same names where they mean the same (strength, kind of
    /// glass, reflectivity, clarity, shininess needed), and two of a prop's own: Surface (Off / Glass /
    /// Water) and its color (the glass's, or the water's over what is under it). Each row shows the
    /// prop's value, else the surface's default (PropRecord.Resolve: Clear low-E glass, untinted water;
    /// not the Glass tab's, a prop's pane is not a tower's); moving one gives the prop that value; a
    /// row's reset puts the default back. Shininess needed at 0% (the default) puts ours over all of
    /// the prop; above, only over its parts at least that shiny.
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
        private FloatSetting _strength, _reflectivity, _clarity, _threshold;
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

        private bool HasSurface()
        {
            return CanChoose() && _surface.value != PropRecord.Game;
        }

        private bool IsGlass()
        {
            return CanChoose() && _surface.value == PropRecord.Glass;
        }

        private void Build()
        {
            Settings.Init();
            GlassPreset clear;
            GlassPresets.TryGet(PropRecord.DefaultGlassPreset, out clear);

            _surface = new IntSetting("PropSurface", PropRecord.Game, false);
            _strength = new FloatSetting("AssetGlassStrength", 1f, false);
            _preset = new IntSetting("AssetGlassPreset", PropRecord.DefaultGlassPreset, false);
            _colour = new ColorSetting("PropColor", clear.Colour, false);
            _reflectivity = new FloatSetting("AssetGlassReflectivity", clear.Reflectivity, false);
            _clarity = new FloatSetting("AssetGlassClarity", clear.Clarity, false);
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
                AfterChange = () => Change(r =>
                {
                    // Glass and water each start from their own defaults: a glass's tint and
                    // reflectivity are no water's.
                    if (r.SurfaceOrGame != _surface.value)
                    {
                        r.Preset = null;
                        r.Colour = null;
                        r.Reflectivity = null;
                        r.Clarity = null;
                    }
                    r.Surface = _surface.value == PropRecord.Game ? (int?)null : _surface.value;
                }, "surface " + PropRecord.SurfaceName(_surface.value)),
            });

            rows.Add(Percent(_strength, 0f, 100f, r => r.Strength.HasValue, (r, v) => r.Strength = v, r => r.Strength = null, "strength"));

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
                Enabled = IsGlass,
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
                Enabled = HasSurface,
                ResetHintKey = "Reset.Default",
                Overridden = () => Has(r => r.Colour.HasValue),
                ClearOverride = () => Change(r => r.Colour = null, "colour back to the default"),
                AfterChange = () => Change(r =>
                {
                    r.Colour = _colour.value;
                    if (r.SurfaceOrGame == PropRecord.Glass)
                        r.Preset = GlassPresets.Custom;
                }, "colour " + ColorText.Format(_colour.value)),
            });

            rows.Add(Percent(_reflectivity, 0f, PropRecord.ReflectivityMax * 100f, r => r.Reflectivity.HasValue, (r, v) =>
            {
                r.Reflectivity = v;
                if (r.SurfaceOrGame == PropRecord.Glass)
                    r.Preset = GlassPresets.Custom;
            }, r => r.Reflectivity = null, "reflectivity"));

            rows.Add(Percent(_clarity, 0f, 100f, r => r.Clarity.HasValue, (r, v) =>
            {
                r.Clarity = v;
                if (r.SurfaceOrGame == PropRecord.Glass)
                    r.Preset = GlassPresets.Custom;
            }, r => r.Clarity = null, "clarity"));

            rows.Add(Percent(_threshold, 0f, PropRecord.ThresholdMax * 100f, r => r.Threshold.HasValue, (r, v) => r.Threshold = v,
                r => r.Threshold = null, "shininess needed"));

            _rows = rows;
            Sync();
        }

        private Row Percent(FloatSetting setting, float min, float max, Func<PropRecord, bool> has, Action<PropRecord, float> set,
                            Action<PropRecord> clear, string what)
        {
            return new Row
            {
                Kind = RowKind.Percent,
                Float = setting,
                Min = min, Max = max, Step = 1f,
                Enabled = HasSurface,
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
            PropRecord.Look look = shown.Resolve();
            _surface.value = shown.SurfaceOrGame;
            _strength.value = look.Strength;
            _preset.value = look.Preset;
            _colour.value = look.Colour;
            _reflectivity.value = look.Reflectivity;
            _clarity.value = look.Clarity;
            _threshold.value = look.Threshold;
        }
    }
}
