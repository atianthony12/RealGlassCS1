using System;
using System.Collections.Generic;
using System.Globalization;
using ColossalFramework;
using ColossalFramework.UI;
using RealGlass.Glass;
using RealGlass.Vehicles;
using UnityEngine;

namespace RealGlass.UI
{
    public enum RowKind
    {
        /// <summary>A 0..1 (or multiplier) setting shown as a percentage.</summary>
        Percent,

        /// <summary>A slider in the setting's own units; <see cref="Row.Format"/> writes the readout.</summary>
        Value,

        Toggle,
        Key,
        Button,

        /// <summary>A line of text the UI keeps up to date. The F8 panel only.</summary>
        Status,

        /// <summary>
        /// A choice over <see cref="Row.ChoiceValues"/>: in the panel a row of buttons, or a list when the
        /// names don't fit (Vanilla: a slider of names); a dropdown on the options page.
        /// </summary>
        Choice,

        /// <summary>A colour: swatch and picker, a text field, Copy, Paste, reset. The F8 panel only.</summary>
        Colour,

        /// <summary>A line of text (<see cref="Row.Text"/>) neither UI draws: the panel's open headings.</summary>
        Text,
    }

    /// <summary>
    /// Which tab of the in-game (F8) panel a row appears on. None = not in it. (One building asset's
    /// own glass is the building window's: PerAsset.AssetCatalog's rows, not this catalog's.)
    /// </summary>
    public enum PanelPage { None, Glass, Reflections, Panes, Interiors, Water, Vehicles }

    /// <summary>General = the game's mod options page (no tabs). None = not on it.</summary>
    public enum OptionsPage { None, General }

    /// <summary>
    /// One setting, everything either UI needs to draw it, and its default.
    /// </summary>
    /// <remarks>
    /// A row holds no words. Its texts are in Localization/en.xml (and its translations) under its
    /// <see cref="Name"/>: "FloorHeight.Label", and so on, read in the player's language every time
    /// a UI asks. A tooltip ("FloorHeight.Tooltip") only where the name leaves a question about what
    /// the row does -- not to say what 100% is or what off gives back (the author, 2026-10-24: "remove
    /// redundant tooltips ... all that are obvious what they do"; 2026-10-21 "add tooltips where
    /// needed"; on 2026-10-04 every row had one and he took them all out: "the labels are already
    /// pretty descriptive").
    /// <see cref="SettingsCatalog.LogLayout"/> checks at startup that English has every key a row
    /// needs. (Volumetric Weather's catalog row.)
    /// </remarks>
    public class Row
    {
        public RowKind Kind;
        public PanelPage Panel;
        public OptionsPage Options;

        /// <summary>
        /// The name of a row with no setting (a button, a status line). A row with a setting is
        /// named after it. Either way the name is its key in the language files.
        /// </summary>
        public string Id;

        /// <summary>
        /// Non-null starts a new group here. Its title is "Group.&lt;Group&gt;" in the language files.
        /// In the F8 panel a click on it folds the group away and back (Settings.PanelOpenGroups keeps
        /// the open ones by this name: a group's name is part of the settings file once released).
        /// </summary>
        public string Group;

        /// <summary>A line of text under the row ("&lt;Name&gt;.Note"), for a warning the label cannot carry.</summary>
        public bool HasNote;

        /// <summary>Toggles: ask "&lt;Name&gt;.Confirm" before switching ON. Never asked when switching off.</summary>
        public bool Confirm;

        public FloatSetting Float;
        public BoolSetting Bool;
        public IntSetting Int;
        public SavedInputKey Key;
        public ColorSetting Colour;
        public StringSetting Text;

        // The SAME constants Settings.Init() constructs from. A setting does not remember its own
        // default, so this is the only thing that can put one back.
        public float DefaultFloat;
        public bool DefaultBool;
        public int DefaultInt;
        public int DefaultKey;
        public Color32 DefaultColour;
        public string DefaultText;

        public float Min, Max, Step;

        /// <summary>Formats the slider readout. Null means "N%".</summary>
        public Func<float, string> Format;

        /// <summary>Choice rows: the language keys of the choices' names, one per value.</summary>
        public string[] ChoiceKeys;

        /// <summary>Choice rows without names: a unit ("Unit.FacesPerFrame") to write each value with. Null: the bare number.</summary>
        public string ChoiceUnit;

        /// <summary>Choice rows whose names are made in code (the languages' own names, sizes in pixels): value -> name.</summary>
        public Func<int, string> ChoiceText;

        public int[] ChoiceValues;

        /// <summary>
        /// A row added after release whose default is NOT what an existing player had: the value a
        /// settings file that lacks it stands for, when RealGlass.xml is read at startup (a file
        /// with no such element was written before the row existed). Null: the default, as for
        /// every other row. None yet: nothing is released.
        /// </summary>
        public int? OlderFileInt;

        /// <summary>
        /// A row added after release whose value for a file that LACKS it is worked out from that
        /// file (Volumetric Weather's): given the file's elements by name -- the retired ones
        /// included (SettingsXml.Retired) -- the text it stands for; null: as if it had no such
        /// hook. Read at startup only, like <see cref="OlderFileInt"/>. 1.1.7: IconPlace, from the
        /// switch "UseUnifiedUI" it replaced.
        /// </summary>
        public Func<IDictionary<string, string>, string> OlderFile;

        /// <summary>Live-apply hook. Must be safe with no city loaded: the options page is.</summary>
        public Action AfterChange;

        public Func<string> StatusText;
        public Action OnClick;

        /// <summary>Greys the row out, e.g. every glass row while the glass is off.</summary>
        public Func<bool> Enabled;

        /// <summary>
        /// False hides the row in the F8 panel and closes the page up under it (Volumetric Weather's):
        /// the Glass tab's status line while it has nothing to say. Null: always shown.
        /// </summary>
        public Func<bool> Shown;

        /// <summary>
        /// A row of one asset's own glass (the building window): whether the asset has a value of its
        /// own here. Null for every other row. The row then has a "Global" button (<see cref="ClearOverride"/>).
        /// </summary>
        public Func<bool> Overridden;

        /// <summary>The asset follows the global setting here again.</summary>
        public Action ClearOverride;

        /// <summary>
        /// The language key of the words over the reset icon of a row with <see cref="ClearOverride"/>:
        /// where it goes back to in that window. Null: "Asset.Global.Hint".
        /// </summary>
        public string ResetHintKey;

        /// <summary>
        /// False only for the machine (rendering cost) and housekeeping: what a future profile
        /// would not carry. Nothing reads it yet.
        /// </summary>
        public bool Profiled = true;

        /// <summary>
        /// True while this row's confirmation dialog is open. The stored value is still the old one
        /// at that point, so a refresh would untick the box the player just ticked.
        /// </summary>
        public bool Pending;

        /// <summary>The name this row is stored under in RealGlass.xml and the log, and its key in the language files.</summary>
        public string Name
        {
            get
            {
                if (Float != null) return Float.name;
                if (Bool != null) return Bool.name;
                if (Int != null) return Int.name;
                if (Key != null) return Key.name;
                if (Colour != null) return Colour.name;
                if (Text != null) return Text.name;
                return Id;
            }
        }

        // ---- the texts, in the player's language ----------------------------------------------

        public string Label
        {
            get { return Localization.Get(Name + ".Label"); }
        }

        /// <summary>Null when the row has none.</summary>
        public string Note
        {
            get { return HasNote ? Localization.Get(Name + ".Note") : null; }
        }

        /// <summary>
        /// What the row's name leaves unsaid, on hover; null where it says it all. English decides
        /// which rows have one (a translation can't add one English lacks).
        /// </summary>
        public string Tooltip
        {
            get
            {
                string key = Name + ".Tooltip";
                return Localization.HasEnglish(key) ? Localization.Get(key) : null;
            }
        }

        /// <summary>The question asked before switching ON; null when there is none.</summary>
        public string ConfirmOn
        {
            get { return Confirm ? Localization.Get(Name + ".Confirm") : null; }
        }

        /// <summary>Null unless a group starts here.</summary>
        public string GroupTitle
        {
            get { return Group != null ? Localization.Get("Group." + Group) : null; }
        }

        /// <summary>The name of every choice, in order. Null for a row that is not a choice.</summary>
        public string[] Choices
        {
            get
            {
                if (ChoiceValues == null)
                    return null;

                var names = new string[ChoiceValues.Length];
                for (int i = 0; i < names.Length; i++)
                    names[i] = ChoiceName(i);

                return names;
            }
        }

        public string ChoiceName(int index)
        {
            if (ChoiceText != null)
                return ChoiceText(ChoiceValues[index]);

            if (ChoiceKeys != null && index < ChoiceKeys.Length)
                return Localization.Get(ChoiceKeys[index]);

            string value = ChoiceValues[index].ToString(CultureInfo.InvariantCulture);
            return ChoiceUnit != null ? Localization.Get(ChoiceUnit, value) : value;
        }

        /// <summary>Every language key this row needs, for the startup check.</summary>
        public IEnumerable<string> TextKeys()
        {
            if (Kind == RowKind.Status)
                yield break;

            yield return Name + ".Label";

            if (HasNote)
                yield return Name + ".Note";

            if (Confirm)
                yield return Name + ".Confirm";

            if (Group != null)
                yield return "Group." + Group;

            if (ChoiceKeys != null)
            {
                foreach (string key in ChoiceKeys)
                    yield return key;
            }

            if (ChoiceUnit != null)
                yield return ChoiceUnit;
        }

        // ---- values ---------------------------------------------------------------------------

        public bool IsEnabled
        {
            get { return Enabled == null || Enabled(); }
        }

        /// <summary>See <see cref="Shown"/>.</summary>
        public bool IsShown
        {
            get { return Shown == null || Shown(); }
        }

        /// <summary>The slider position: percent rows are stored as a fraction and shown x100.</summary>
        public float Display
        {
            get
            {
                if (Float == null)
                    return 0f;

                return Kind == RowKind.Percent ? Float.value * 100f : Float.value;
            }
        }

        public void Store(float display)
        {
            if (Float == null)
                return;

            Float.value = Kind == RowKind.Percent ? display / 100f : display;
        }

        public string FormatDisplay(float display)
        {
            if (Format != null)
                return Format(display);

            return Localization.Get("Unit.Percent", display.ToString("F0"));
        }

        /// <summary>The index into <see cref="Choices"/> that the stored value means.</summary>
        public int ChoiceIndex
        {
            get
            {
                if (Int == null || ChoiceValues == null)
                    return 0;

                for (int i = 0; i < ChoiceValues.Length; i++)
                {
                    if (ChoiceValues[i] == Int.value)
                        return i;
                }

                // An unknown value (one from a newer version; the settings file refuses one typed
                // by hand) reads as the last entry, which is "Custom" wherever there is one.
                return ChoiceValues.Length - 1;
            }
        }

        /// <summary>Writes the shipping default back. Does not fire <see cref="AfterChange"/>.</summary>
        public void ResetToDefault()
        {
            if (Float != null) Float.value = DefaultFloat;
            if (Bool != null) Bool.value = DefaultBool;
            if (Int != null) Int.value = DefaultInt;
            if (Key != null) Key.value = DefaultKey;
            if (Colour != null) Colour.value = DefaultColour;
            if (Text != null) Text.value = DefaultText;
        }

        /// <summary>A colour row at its default.</summary>
        public bool IsDefaultColour
        {
            get { return Colour == null || ColorText.Same(Colour.value, DefaultColour); }
        }

        /// <summary>
        /// A row with a setting of its own the panel's reset button can put back: a slider (not a
        /// hidden one), a switch, a choice, a key or a colour. (Volumetric Weather's.)
        /// </summary>
        public bool CanReset
        {
            get { return Float != null || Bool != null || Int != null || Key != null || Colour != null; }
        }

        /// <summary>At its shipping default: what fades the row's reset button.</summary>
        public bool IsAtDefault
        {
            get
            {
                if (Float != null && !Mathf.Approximately(Float.value, DefaultFloat)) return false;
                if (Bool != null && Bool.value != DefaultBool) return false;
                if (Int != null && Int.value != DefaultInt) return false;
                if (Key != null && Key.value != DefaultKey) return false;
                if (Colour != null && !IsDefaultColour) return false;
                if (Text != null && Text.value != (DefaultText ?? string.Empty)) return false;
                return true;
            }
        }

        /// <summary>
        /// Where the row's reset button goes back to, as words on hover: the default here, the F8
        /// panel's setting in the building window (whose reset is <see cref="ClearOverride"/>). An
        /// icon has no label of its own to say it.
        /// </summary>
        public string ResetHint
        {
            get { return Localization.Get(ClearOverride != null ? (ResetHintKey ?? "Asset.Global.Hint") : "Reset.Default"); }
        }
    }

    /// <summary>
    /// Every setting in the mod, once, with where it is shown and what its default is.
    /// </summary>
    /// <remarks>
    /// Built because the same rows are needed by two different UIs with two different widget
    /// vocabularies (the F8 panel's hand-built controls and the options page's UIHelper
    /// factories), and keeping two imperative lists in step is how they drift. Reset-to-defaults
    /// walks this list, and so does RealGlass.xml: a setting with no row is never saved.
    ///
    /// The split: the look is in the panel (Row.Panel, four tabs); the options page has the mod
    /// itself and what the glass may cost (Row.Options == General).
    ///
    /// The words are in Localization/en.xml, in the same order as the rows here: a new row needs
    /// its Label there (the log says at startup if one is missing, and
    /// tools/test-localization.ps1 checks it offline). Every glass row's AfterChange ends in
    /// <see cref="GlassController.SettingsChanged"/>.
    /// </remarks>
    public static class SettingsCatalog
    {
        private static List<Row> _rows;

        /// <summary>True while a preset (or a reset) is writing rows, so they don't flip it to Custom.</summary>
        private static bool _applyingPreset;

        private static bool _loggedLayout;

        public static List<Row> Rows
        {
            get
            {
                if (_rows == null)
                    Build();

                return _rows;
            }
        }

        public static int CountForPanel(PanelPage page)
        {
            int n = 0;
            foreach (Row row in Rows)
            {
                if (row.Panel == page)
                    n++;
            }

            return n;
        }

        public static int CountForOptions(OptionsPage page)
        {
            int n = 0;
            foreach (Row row in Rows)
            {
                if (row.Options == page)
                    n++;
            }

            return n;
        }

        /// <summary>A tab's name, in the player's language.</summary>
        public static string TabName(PanelPage page)
        {
            return Localization.Get("Tab." + page);
        }

        /// <summary>
        /// One line per page, at build time: the cheapest proof the split is what was meant. And
        /// one warning if English lacks a text any row needs -- a missing key shows on screen as the
        /// key itself, which nobody would report as a bug in the mod.
        /// </summary>
        public static void LogLayout()
        {
            // Once per process: the catalog is static, and both the options page (at the main
            // menu) and the controller (in a city) ask for this line.
            if (_loggedLayout)
                return;

            _loggedLayout = true;

            int nowhere = 0;
            foreach (Row row in Rows)
            {
                if (row.Panel == PanelPage.None && row.Options == OptionsPage.None)
                    nowhere++;
            }

            Log.Msg("settings catalog: " + Rows.Count + " rows | panel Glass=" + CountForPanel(PanelPage.Glass) +
                    " Reflections=" + CountForPanel(PanelPage.Reflections) +
                    " Panes=" + CountForPanel(PanelPage.Panes) +
                    " Interiors=" + CountForPanel(PanelPage.Interiors) +
                    " Water=" + CountForPanel(PanelPage.Water) +
                    " | options page=" + CountForOptions(OptionsPage.General) +
                    " | building window=" + PerAsset.AssetCatalog.Picked.Rows.Count +
                    " | creator window=" + PerAsset.AssetCatalog.Edited.Rows.Count +
                    " | rows with no UI=" + nowhere);

            var missing = new List<string>();
            var every = new List<Row>(Rows);
            every.AddRange(PerAsset.AssetCatalog.Picked.Rows);
            every.AddRange(PerAsset.AssetCatalog.Edited.Rows);
            foreach (Row row in every)
            {
                foreach (string key in row.TextKeys())
                {
                    if (!Localization.HasEnglish(key))
                        missing.Add(key);
                }
            }

            foreach (PanelPage page in Enum.GetValues(typeof(PanelPage)))
            {
                if (page != PanelPage.None && !Localization.HasEnglish("Tab." + page))
                    missing.Add("Tab." + page);
            }

            if (missing.Count > 0)
                Log.Warn("localization: en.xml has no text for " + string.Join(", ", missing.ToArray()));
        }

        /// <summary>
        /// Puts every setting back to its shipping default -- including the rows with no UI at
        /// all, which is the other reason they are in this list. Always logged: a settings file
        /// that changed under the player is the first thing to suspect when a log looks wrong.
        /// </summary>
        public static void ResetAll()
        {
            foreach (Row row in Rows)
                row.ResetToDefault();

            // Under the preset's guard: every value is a default now, and the colour, reflectivity
            // and clarity rows' own hooks would otherwise flip the preset (reset a moment ago) to
            // Custom.
            _applyingPreset = true;
            try
            {
                foreach (Row row in Rows)
                {
                    if (row.AfterChange != null)
                        row.AfterChange();
                }
            }
            finally
            {
                _applyingPreset = false;
            }

            GlassController.SettingsChanged();
            SettingsXml.SaveNow();
            Log.Msg("settings: RESET to defaults (" + Rows.Count + " rows; glass = " + OnOff(Settings.GlassEnabled)() +
                    ", preset = " + GlassPresets.LogName(Settings.GlassPreset != null ? Settings.GlassPreset.value : Settings.Defaults.GlassPreset) +
                    ", panel key = " + (Settings.PanelKey != null ? SettingsXmlFormat.FormatKey(Settings.PanelKey.value) : "?") + ")");
        }

        /// <summary>
        /// Stores a checkbox, asking first where the row says to ask. The question is only ever put
        /// when switching ON -- nobody has to justify turning something off -- and the answer lands
        /// back in both UIs, which is what puts a refused checkbox back.
        /// </summary>
        public static void ApplyToggle(Row row, bool value)
        {
            if (row.Bool == null)
                return;

            if (!value || !row.Confirm)
            {
                Settle(row, value);
                return;
            }

            try
            {
                // Until the answer comes the stored value is still "off", and the panel re-reads
                // its controls four times a second: without this flag the box the player just
                // ticked unticks itself behind the dialog.
                row.Pending = true;

                ConfirmPanel.ShowModal(Mod.DisplayName, row.ConfirmOn,
                    (component, result) =>
                    {
                        row.Pending = false;
                        Settle(row, result == 1);
                    });
            }
            catch (Exception e)
            {
                // If the dialog cannot be shown, do what the player asked and say so. A checkbox
                // that silently refuses to tick reads as a broken mod.
                row.Pending = false;
                Log.Warn("Could not show the confirmation for '" + row.Name + "': " + e.Message);
                Settle(row, value);
            }
        }

        private static void Settle(Row row, bool value)
        {
            row.Bool.value = value;

            // Written to disk NOW rather than with the slider changes a second later. A switch is
            // a decision, and "I ticked it and it was off again next time" is the one report a
            // settings page must never earn.
            SettingsXml.SaveNow();

            if (row.AfterChange != null)
                row.AfterChange();

            RefreshAllUIs();
        }

        /// <summary>
        /// A row's reset button in the F8 panel (Volumetric Weather's): that one row back to its
        /// default, as if the player had moved it there -- saved now, its live hook run, every UI
        /// refreshed. A switch goes through <see cref="ApplyToggle"/> like a click. Not under the
        /// preset's guard, on purpose: a colour, reflectivity or clarity put back by hand turns the
        /// kind of glass to Custom as moving it would, and the kind of glass put back writes its three.
        /// </summary>
        public static void ResetRow(Row row)
        {
            if (row == null || !row.CanReset || row.IsAtDefault)
                return;

            Log.Msg("setting: " + row.Name + " reset to its default (the row's reset button)");

            if (row.Kind == RowKind.Toggle && row.Bool != null)
            {
                ApplyToggle(row, row.DefaultBool);
                return;
            }

            row.ResetToDefault();
            SettingsXml.SaveNow();

            if (row.AfterChange != null)
                row.AfterChange();

            RefreshAllUIs();
        }

        /// <summary>
        /// Re-reads every control in every UI: any can be open while another changes a setting, a
        /// preset changes rows the player did not touch, and the building window shows the global
        /// values its asset follows.
        /// </summary>
        public static void RefreshAllUIs()
        {
            if (OptionsUI.Instance != null)
                OptionsUI.Instance.RefreshValues();

            GlassPanel.RefreshOpenPanel();
            AssetPanel.RefreshOpenPanel();
            CreatorPanel.RefreshOpenPanel();
        }

        /// <summary>
        /// A glass preset writes the three rows it is made of (<see cref="GlassPresets"/>) and
        /// nothing else. Custom writes nothing: it is simply what the rows hold.
        /// </summary>
        public static void ApplyGlassPreset(int value)
        {
            GlassPreset preset;
            if (!GlassPresets.TryGet(value, out preset))
            {
                Log.Msg("glass preset: " + GlassPresets.LogName(value) + " (the colour, reflectivity and clarity stay as they are)");
                return;
            }

            // Restored, not cleared: ResetAll holds the same guard around this call.
            bool wasApplying = _applyingPreset;
            _applyingPreset = true;
            try
            {
                if (Settings.GlassColor != null)
                    Settings.GlassColor.value = preset.Colour;
                if (Settings.GlassReflectivity != null)
                    Settings.GlassReflectivity.value = preset.Reflectivity;
                if (Settings.GlassClarity != null)
                    Settings.GlassClarity.value = preset.Clarity;
            }
            finally
            {
                _applyingPreset = wasApplying;
            }

            Log.Msg("glass preset: " + preset.LogName + " -> colour " + ColorText.Format(preset.Colour) +
                    ", reflectivity " + (preset.Reflectivity * 100f).ToString("F0") +
                    "%, clarity " + (preset.Clarity * 100f).ToString("F0") + "%");
        }

        /// <summary>The colour, reflectivity or clarity was moved by hand, so the preset no longer describes them.</summary>
        private static void MarkCustomGlass()
        {
            if (_applyingPreset || Settings.GlassPreset == null || Settings.GlassPreset.value == GlassPresets.Custom)
                return;

            Settings.GlassPreset.value = GlassPresets.Custom;
            Log.Msg("glass preset: Custom (the colour, reflectivity or clarity was changed by hand)");
        }

        /// <summary>A glass row's hook: the renderer takes the new value on its next frame.</summary>
        private static void Changed()
        {
            GlassController.SettingsChanged();
        }

        /// <summary>
        /// A switch's hook: said in the log whatever the logging level (every switch that can
        /// suppress or replace something on screen says so -- Volumetric Weather's
        /// DebugSkipDrawLight lesson), then the renderer.
        /// </summary>
        private static Action State(string what, Func<string> value)
        {
            return () =>
            {
                Log.Msg("setting: " + what + " = " + value());
                GlassController.SettingsChanged();
            };
        }

        private static Func<string> OnOff(BoolSetting setting)
        {
            return () => setting != null && setting.value ? "ON" : "off";
        }

        private static Func<string> Chosen(IntSetting setting, Func<int, string> name)
        {
            return () => setting != null ? name(setting.value) : "?";
        }

        // ---- formatters ----------------------------------------------------------------------

        private static string Metres(float value)
        {
            return Localization.Get("Unit.Metres", value.ToString("F1"));
        }

        private static string WholeMetres(float value)
        {
            return Localization.Get("Unit.Metres", value.ToString("F0"));
        }

        private static string MetresPerSecond(float value)
        {
            return Localization.Get("Unit.MetresPerSecond", value.ToString("F1"));
        }

        private static string Steps(float value)
        {
            return value.ToString("F0");
        }

        /// <summary>A percentage to a tenth: "0.5%".</summary>
        private static string PercentTenths(float value)
        {
            return Localization.Get("Unit.Percent", value.ToString("F1"));
        }

        private static string CubeSize(int value)
        {
            return Localization.Get("Unit.Pixels", Settings.CubePixels(value).ToString(CultureInfo.InvariantCulture));
        }

        // ---- the switches other rows are greyed out by ----------------------------------------

        private static bool GlassOn()
        {
            return Settings.GlassEnabled == null || Settings.GlassEnabled.value;
        }

        private static bool ScreenReflectionsOn()
        {
            return GlassOn() && (Settings.SsrEnabled == null || Settings.SsrEnabled.value);
        }

        private static bool LiveSkyOn()
        {
            return GlassOn() && (Settings.SkyReflection == null || Settings.SkyReflection.value == 0);
        }

        private static bool InteriorsOn()
        {
            return GlassOn() && Settings.InteriorsEnabled != null && Settings.InteriorsEnabled.value;
        }

        private static bool RoomsByDayOn()
        {
            return InteriorsOn() && Settings.InteriorsByDay != null && Settings.InteriorsByDay.value;
        }

        private static bool VehiclesOn()
        {
            return GlassOn() && Settings.VehicleGlass != null && Settings.VehicleGlass.value;
        }

        private static bool WaterOn()
        {
            return Settings.WaterEnabled != null && Settings.WaterEnabled.value;
        }

        private static bool WaterOwnColourOn()
        {
            return WaterOn() && (Settings.WaterThemeColor == null || !Settings.WaterThemeColor.value);
        }

        private static bool WaterSkyOn()
        {
            return WaterOn() && (Settings.WaterRealSky == null || Settings.WaterRealSky.value);
        }

        private static bool WaterGlitterOn()
        {
            return WaterSkyOn() && (Settings.WaterGlitter == null || Settings.WaterGlitter.value > 0f);
        }

        private static bool WaterMoonGlitterOn()
        {
            return WaterSkyOn() && (Settings.WaterMoonGlitter == null || Settings.WaterMoonGlitter.value > 0f);
        }

        private static bool WaterRipplesOn()
        {
            return WaterOn() && (Settings.WaterRipples == null || Settings.WaterRipples.value);
        }

        private static bool WaterOceanOn()
        {
            return WaterRipplesOn() && (Settings.WaterOceanWaves == null || Settings.WaterOceanWaves.value);
        }

        /// <summary>The Wind speed row is the waves' wind: ocean waves, and Dynamic waves off (on: the weather's).</summary>
        private static bool WaterWindOn()
        {
            return WaterOceanOn() && Settings.WaterDynamicWaves != null && !Settings.WaterDynamicWaves.value;
        }

        private static bool WaterSinesOn()
        {
            return WaterRipplesOn() && Settings.WaterOceanWaves != null && !Settings.WaterOceanWaves.value;
        }

        private static bool WaterFoamOn()
        {
            return WaterOn() && (Settings.WaterFoam == null || Settings.WaterFoam.value);
        }

        private static bool WaterCityOn()
        {
            return WaterOn() && (Settings.WaterCity == null || Settings.WaterCity.value);
        }

        private static bool WaterUnderOn()
        {
            return WaterOn() && (Settings.WaterUnder == null || Settings.WaterUnder.value);
        }

        /// <summary>A water row's hook: the water controller reads the settings every frame, so only the log needs telling.</summary>
        private static Action WaterState(string what, Func<string> value)
        {
            return () => Log.Msg("setting: " + what + " = " + value());
        }

        // ---- the catalog ----------------------------------------------------------------------

        private static void Build()
        {
            Settings.Init();

            // A first Init reads RealGlass.xml, which is written from this list, so it may have
            // built it already.
            if (_rows != null)
                return;

            _rows = new List<Row>();

            BuildGlass();
            BuildReflections();
            BuildPanes();
            BuildInteriors();
            BuildWater();
            BuildVehicles();

            // The options page, top to bottom: the mod, what the glass may cost, then diagnostics
            // and the reset last.
            BuildGeneralOptions();
            BuildRendering();
            BuildHousekeeping();

            BuildHidden();
        }

        private static void Add(Row row)
        {
            _rows.Add(row);
        }

        /// <summary>F8 -> Glass: the glass itself, and what counts as glass.</summary>
        private static void BuildGlass()
        {
            Add(new Row
            {
                Kind = RowKind.Status,
                Panel = PanelPage.Glass,
                Id = "GlassStatus",
                StatusText = () => GlassController.Status,
                Shown = () => !string.IsNullOrEmpty(GlassController.Status),
                Profiled = false,
            });

            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Glass,
                Bool = Settings.GlassEnabled,
                DefaultBool = Settings.Defaults.GlassEnabled,
                AfterChange = State("realistic glass", OnOff(Settings.GlassEnabled)),
            });

            // Custom last: an unknown value reads as the last entry (Row.ChoiceIndex). Stored by
            // VALUE, so the order shown is free; the values are GlassPresets' and append only.
            Add(new Row
            {
                Kind = RowKind.Choice,
                Panel = PanelPage.Glass,
                Group = "TheGlass",
                Int = Settings.GlassPreset,
                DefaultInt = Settings.Defaults.GlassPreset,
                ChoiceKeys = new[]
                {
                    "GlassPreset.ReflectiveBlue", "GlassPreset.SilverReflective", "GlassPreset.OceanBlue", "GlassPreset.AquaBlue",
                    "GlassPreset.SkyBlue", "GlassPreset.Green", "GlassPreset.Bronze", "GlassPreset.Grey", "GlassPreset.ClearLowE",
                    "GlassPreset.GoldReflective", "GlassPreset.Custom",
                },
                ChoiceValues = new[] { 10, 8, 3, 2, 4, 5, 6, 7, 1, 9, 0 },
                Enabled = GlassOn,
                AfterChange = () =>
                {
                    if (Settings.GlassPreset != null)
                        ApplyGlassPreset(Settings.GlassPreset.value);
                    Changed();
                },
            });

            Add(new Row
            {
                Kind = RowKind.Colour,
                Panel = PanelPage.Glass,
                Colour = Settings.GlassColor,
                DefaultColour = Settings.Defaults.GlassColor,
                Enabled = GlassOn,
                AfterChange = () =>
                {
                    MarkCustomGlass();
                    Changed();
                },
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Glass,
                Float = Settings.GlassReflectivity,
                DefaultFloat = Settings.Defaults.GlassReflectivity,
                Min = 0f, Max = 60f, Step = 1f,
                Enabled = GlassOn,
                AfterChange = () =>
                {
                    MarkCustomGlass();
                    Changed();
                },
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Glass,
                Float = Settings.GlassGlancing,
                DefaultFloat = Settings.Defaults.GlassGlancing,
                Min = 0f, Max = 100f, Step = 1f,
                Enabled = GlassOn,
                AfterChange = Changed,
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Glass,
                Float = Settings.GlassEdgeSilver,
                DefaultFloat = Settings.Defaults.GlassEdgeSilver,
                Min = 0f, Max = 100f, Step = 1f,
                Enabled = GlassOn,
                AfterChange = Changed,
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Glass,
                Float = Settings.GlassClarity,
                DefaultFloat = Settings.Defaults.GlassClarity,
                Min = 0f, Max = 100f, Step = 1f,
                Enabled = GlassOn,
                AfterChange = () =>
                {
                    MarkCustomGlass();
                    Changed();
                },
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Glass,
                Float = Settings.GlassStrength,
                DefaultFloat = Settings.Defaults.GlassStrength,
                Min = 0f, Max = 100f, Step = 1f,
                Enabled = GlassOn,
                AfterChange = Changed,
            });

            // What the building painted on its glass that the new glass keeps (under Glass since
            // 2026-11-08, the author's call: no heading of their own).
            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Glass,
                Float = Settings.GlassOwnColour,
                DefaultFloat = Settings.Defaults.GlassOwnColour,
                Min = 0f, Max = 100f, Step = 1f,
                Enabled = GlassOn,
                AfterChange = Changed,
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Glass,
                Float = Settings.GlassPaintedDetail,
                DefaultFloat = Settings.Defaults.GlassPaintedDetail,
                Min = 0f, Max = 100f, Step = 1f,
                Enabled = GlassOn,
                AfterChange = Changed,
            });

            // The glass on each kind of building (Glass/GlassKinds.cs; the author's call, 2026-11-08:
            // all on). Off: nothing of ours on that kind, its rooms included, unless an asset has a
            // strength of its own. One row each, spelled out (tools/test-localization.ps1 reads them).
            Add(new Row { Kind = RowKind.Toggle, Panel = PanelPage.Glass, Group = "GlassOn", Bool = Settings.GlassOnResidential,
                          DefaultBool = Settings.Defaults.GlassOnKind, Enabled = GlassOn, AfterChange = Changed });
            Add(new Row { Kind = RowKind.Toggle, Panel = PanelPage.Glass, Bool = Settings.GlassOnCommercial,
                          DefaultBool = Settings.Defaults.GlassOnKind, Enabled = GlassOn, AfterChange = Changed });
            Add(new Row { Kind = RowKind.Toggle, Panel = PanelPage.Glass, Bool = Settings.GlassOnOffices,
                          DefaultBool = Settings.Defaults.GlassOnKind, Enabled = GlassOn, AfterChange = Changed });
            Add(new Row { Kind = RowKind.Toggle, Panel = PanelPage.Glass, Bool = Settings.GlassOnIndustrial,
                          DefaultBool = Settings.Defaults.GlassOnKind, Enabled = GlassOn, AfterChange = Changed });
            Add(new Row { Kind = RowKind.Toggle, Panel = PanelPage.Glass, Bool = Settings.GlassOnServices,
                          DefaultBool = Settings.Defaults.GlassOnKind, Enabled = GlassOn, AfterChange = Changed });
            Add(new Row { Kind = RowKind.Toggle, Panel = PanelPage.Glass, Bool = Settings.GlassOnTransport,
                          DefaultBool = Settings.Defaults.GlassOnKind, Enabled = GlassOn, AfterChange = Changed });
            Add(new Row { Kind = RowKind.Toggle, Panel = PanelPage.Glass, Bool = Settings.GlassOnUnique,
                          DefaultBool = Settings.Defaults.GlassOnKind, Enabled = GlassOn, AfterChange = Changed });
            Add(new Row { Kind = RowKind.Toggle, Panel = PanelPage.Glass, Bool = Settings.GlassOnMonuments,
                          DefaultBool = Settings.Defaults.GlassOnKind, Enabled = GlassOn, AfterChange = Changed });
            Add(new Row { Kind = RowKind.Toggle, Panel = PanelPage.Glass, Bool = Settings.GlassOnParks,
                          DefaultBool = Settings.Defaults.GlassOnKind, Enabled = GlassOn, AfterChange = Changed });
            Add(new Row { Kind = RowKind.Toggle, Panel = PanelPage.Glass, Bool = Settings.GlassOnOther,
                          DefaultBool = Settings.Defaults.GlassOnKind, Enabled = GlassOn, AfterChange = Changed });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Glass,
                Group = "WhatIsGlass",
                Float = Settings.GlassThreshold,
                DefaultFloat = Settings.Defaults.GlassThreshold,
                Min = 40f, Max = 95f, Step = 1f,
                Enabled = GlassOn,
                AfterChange = Changed,
            });

            // Each asset's shininess needed from its own textures (Glass/ThresholdDetect.cs).
            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Glass,
                Bool = Settings.GlassThresholdAuto,
                DefaultBool = Settings.Defaults.GlassThresholdAuto,
                Enabled = GlassOn,
                AfterChange = State("shininess detected per asset", OnOff(Settings.GlassThresholdAuto)),
            });

            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Glass,
                Bool = Settings.GlassNightWindows,
                DefaultBool = Settings.Defaults.GlassNightWindows,
                Enabled = GlassOn,
                AfterChange = State("lit-at-night windows count as glass", OnOff(Settings.GlassNightWindows)),
            });

            // LODs whose glass is not their model's -- none, or elsewhere -- show the model's
            // (Glass/LodGlass.cs). A building window can ask for it, or against it.
            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Glass,
                Bool = Settings.LodGlassFromModel,
                DefaultBool = Settings.Defaults.LodGlassFromModel,
                Enabled = GlassOn,
                AfterChange = State("LODs take their model's glass where theirs differs", OnOff(Settings.LodGlassFromModel)),
            });

            // It changes the picture (magenta buildings), so its state is always in the log -- and
            // it is a normal row with a checkbox: a debug switch must never outlive its UI.
            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Glass,
                Bool = Settings.GlassShowMask,
                DefaultBool = Settings.Defaults.GlassShowMask,
                Enabled = GlassOn,
                AfterChange = State("glass mask view (what counts as glass, in magenta)", OnOff(Settings.GlassShowMask)),
            });
        }

        /// <summary>
        /// F8 -> Reflections: what the glass mirrors. How bright and sharp first, then the city in it
        /// (the screen, the buildings around, below the horizon), then the sky.
        /// </summary>
        private static void BuildReflections()
        {
            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Reflections,
                Group = "TheReflections",
                Float = Settings.ReflectionBrightness,
                DefaultFloat = Settings.Defaults.ReflectionBrightness,
                Min = 0f, Max = 300f, Step = 5f,
                Enabled = GlassOn,
                AfterChange = Changed,
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Reflections,
                Float = Settings.GlassSmoothness,
                DefaultFloat = Settings.Defaults.GlassSmoothness,
                Min = 0f, Max = 100f, Step = 1f,
                Enabled = GlassOn,
                AfterChange = Changed,
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Reflections,
                Float = Settings.SunGlint,
                DefaultFloat = Settings.Defaults.SunGlint,
                Min = 0f, Max = 300f, Step = 5f,
                Enabled = GlassOn,
                AfterChange = Changed,
            });

            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Reflections,
                Bool = Settings.SteadyGlints,
                DefaultBool = Settings.Defaults.SteadyGlints,
                Enabled = GlassOn,
                AfterChange = State("steady glints (specular anti-aliasing)", OnOff(Settings.SteadyGlints)),
            });

            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Reflections,
                Group = "CityInGlass",
                Bool = Settings.SsrEnabled,
                DefaultBool = Settings.Defaults.SsrEnabled,
                Enabled = GlassOn,
                AfterChange = State("screen reflections", OnOff(Settings.SsrEnabled)),
            });

            // Whether or not the trace is on: the mirrored street below the horizon reads it too.
            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Reflections,
                Bool = Settings.ReflectFinished,
                DefaultBool = Settings.Defaults.ReflectFinished,
                Enabled = GlassOn,
                AfterChange = State("reflect the finished picture (fog, clouds, lamps)", OnOff(Settings.ReflectFinished)),
            });

            // Whether or not the trace is on: what a reflection meets off screen.
            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Reflections,
                Bool = Settings.ReflectNeighbours,
                DefaultBool = Settings.Defaults.ReflectNeighbours,
                Enabled = GlassOn,
                AfterChange = State("the buildings around in the reflections (city heights)", OnOff(Settings.ReflectNeighbours)),
            });

            Add(new Row
            {
                Kind = RowKind.Choice,
                Panel = PanelPage.Reflections,
                Int = Settings.SsrQuality,
                DefaultInt = Settings.Defaults.SsrQuality,
                ChoiceKeys = new[] { "SsrQuality.Low", "SsrQuality.Medium", "SsrQuality.High", "SsrQuality.Ultra" },
                ChoiceValues = new[] { 0, 1, 2, 3 },
                Profiled = false,
                Enabled = ScreenReflectionsOn,
                AfterChange = State("screen reflection quality", Chosen(Settings.SsrQuality, SystemReport.QualityName)),
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Reflections,
                Float = Settings.SsrEdgeFade,
                DefaultFloat = Settings.Defaults.SsrEdgeFade,
                Min = 0f, Max = 50f, Step = 1f,
                Enabled = ScreenReflectionsOn,
                AfterChange = Changed,
            });

            // Whether or not screen reflections are on: with them off, nothing on screen is ever
            // hit, and this is what every reflection below the horizon shows.
            Add(new Row
            {
                Kind = RowKind.Choice,
                Panel = PanelPage.Reflections,
                Int = Settings.OffscreenFill,
                DefaultInt = Settings.Defaults.OffscreenFill,
                ChoiceKeys = new[] { "OffscreenFill.Mirror", "OffscreenFill.City", "OffscreenFill.Sky" },
                ChoiceValues = new[] { 0, 1, 2 },
                Enabled = GlassOn,
                AfterChange = State("below the horizon, off screen", Chosen(Settings.OffscreenFill, SystemReport.FillName)),
            });

            Add(new Row
            {
                Kind = RowKind.Choice,
                Panel = PanelPage.Reflections,
                Group = "SkyInGlass",
                Int = Settings.SkyReflection,
                DefaultInt = Settings.Defaults.SkyReflection,
                ChoiceKeys = new[] { "SkyReflection.Live", "SkyReflection.Game" },
                ChoiceValues = new[] { 0, 1 },
                Enabled = GlassOn,
                AfterChange = State("sky in the glass", Chosen(Settings.SkyReflection, v => v == 0 ? "live sky" : "the game's environment map")),
            });

            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Reflections,
                Bool = Settings.SkyClouds,
                DefaultBool = Settings.Defaults.SkyClouds,
                Enabled = LiveSkyOn,
                AfterChange = State("clouds in the live sky", OnOff(Settings.SkyClouds)),
            });

            // The live sky's own sky at the sun's height (GlassUniforms.Dusk): drawn into its cube.
            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Reflections,
                Float = Settings.SkyDusk,
                DefaultFloat = Settings.Defaults.SkyDusk,
                Min = 0f, Max = 100f, Step = 5f,
                Enabled = LiveSkyOn,
                AfterChange = Changed,
            });
        }

        /// <summary>F8 -> Panes: the panes, and how they bend what they mirror.</summary>
        private static void BuildPanes()
        {
            Add(new Row
            {
                Kind = RowKind.Value,
                Panel = PanelPage.Panes,
                Group = "ThePanes",
                Float = Settings.PaneWidth,
                DefaultFloat = Settings.Defaults.PaneWidth,
                Min = 0.8f, Max = 4f, Step = 0.1f,
                Format = Metres,
                Enabled = GlassOn,
                AfterChange = Changed,
            });

            // A pane is one storey tall where the building painted no window of its own, and the
            // buildings mirrored where the screen cannot show them have their floors this far apart.
            Add(new Row
            {
                Kind = RowKind.Value,
                Panel = PanelPage.Panes,
                Float = Settings.FloorHeight,
                DefaultFloat = Settings.Defaults.FloorHeight,
                Min = 2.8f, Max = 6f, Step = 0.1f,
                Format = Metres,
                Enabled = GlassOn,
                AfterChange = Changed,
            });

            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Panes,
                Bool = Settings.PanesFollowWindows,
                DefaultBool = Settings.Defaults.PanesFollowWindows,
                Enabled = GlassOn,
                AfterChange = State("panes follow the painted windows", OnOff(Settings.PanesFollowWindows)),
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Panes,
                Group = "Distortion",
                Float = Settings.PanePillow,
                DefaultFloat = Settings.Defaults.PanePillow,
                Min = 0f, Max = 300f, Step = 5f,
                Enabled = GlassOn,
                AfterChange = Changed,
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Panes,
                Float = Settings.PaneTilt,
                DefaultFloat = Settings.Defaults.PaneTilt,
                Min = 0f, Max = 300f, Step = 5f,
                Enabled = GlassOn,
                AfterChange = Changed,
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Panes,
                Float = Settings.RollerWave,
                DefaultFloat = Settings.Defaults.RollerWave,
                Min = 0f, Max = 300f, Step = 5f,
                Enabled = GlassOn,
                AfterChange = Changed,
            });

            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Panes,
                Bool = Settings.PaneWeather,
                DefaultBool = Settings.Defaults.PaneWeather,
                Enabled = GlassOn,
                AfterChange = State("panes follow the weather", OnOff(Settings.PaneWeather)),
            });
        }

        /// <summary>
        /// F8 -> Interiors: rooms behind every building's windows (Glass/Interiors.cs), ours at night;
        /// on by default since 2026-10-11 (off from 2026-10-07: "by default it's off for now, and when
        /// turned on it affects all buildings"). An asset can say otherwise in the building window.
        /// </summary>
        private static void BuildInteriors()
        {
            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Interiors,
                Bool = Settings.InteriorsEnabled,
                DefaultBool = Settings.Defaults.InteriorsEnabled,
                Enabled = GlassOn,
                AfterChange = State("rooms behind the windows", OnOff(Settings.InteriorsEnabled)),
            });

            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Interiors,
                Group = "TheRooms",
                Bool = Settings.InteriorsByDay,
                DefaultBool = Settings.Defaults.InteriorsByDay,
                Enabled = InteriorsOn,
                AfterChange = State("rooms by day too", OnOff(Settings.InteriorsByDay)),
            });

            // 0%: by day the dark inside, as with the switch above off; 100%: the rooms in full.
            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Interiors,
                Float = Settings.InteriorsDayOpacity,
                DefaultFloat = Settings.Defaults.InteriorsDayOpacity,
                Min = 0f, Max = 100f, Step = 5f,
                Enabled = RoomsByDayOn,
                AfterChange = Changed,
            });

            // 0%: each room a flat picture on the glass; 100%: a room with depth behind it.
            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Interiors,
                Float = Settings.InteriorsDepth,
                DefaultFloat = Settings.Defaults.InteriorsDepth,
                Min = 0f, Max = 100f, Step = 5f,
                Enabled = InteriorsOn,
                AfterChange = Changed,
            });

            // On: up close the rooms are the LOD models' storey grid, so a building changing model
            // keeps its rooms; off: they follow the windows the asset painted (and switch at the LOD).
            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Interiors,
                Bool = Settings.InteriorsMatchLods,
                DefaultBool = Settings.Defaults.InteriorsMatchLods,
                Enabled = InteriorsOn,
                AfterChange = State("rooms the same up close and on LODs", OnOff(Settings.InteriorsMatchLods)),
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Interiors,
                Float = Settings.InteriorsCurtains,
                DefaultFloat = Settings.Defaults.InteriorsCurtains,
                Min = 0f, Max = 100f, Step = 1f,
                Enabled = InteriorsOn,
                AfterChange = Changed,
            });

            // Which rooms are lit: where and when the game lights each asset's windows, else ours (the
            // three rows after it, which still light every building whose asset says no, Alt+G).
            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Interiors,
                Group = "RoomLights",
                Bool = Settings.InteriorsFollowIllumination,
                DefaultBool = Settings.Defaults.InteriorsFollowIllumination,
                Enabled = InteriorsOn,
                AfterChange = State("rooms lit by the assets' illumination maps", OnOff(Settings.InteriorsFollowIllumination)),
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Interiors,
                Float = Settings.InteriorsLit,
                DefaultFloat = Settings.Defaults.InteriorsLit,
                Min = 5f, Max = 90f, Step = 1f,
                Enabled = InteriorsOn,
                AfterChange = Changed,
            });

            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Interiors,
                Bool = Settings.InteriorsLateNight,
                DefaultBool = Settings.Defaults.InteriorsLateNight,
                Enabled = InteriorsOn,
                AfterChange = State("fewer room lights late at night", OnOff(Settings.InteriorsLateNight)),
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Interiors,
                Float = Settings.InteriorsSwitching,
                DefaultFloat = Settings.Defaults.InteriorsSwitching,
                Min = 0f, Max = 300f, Step = 5f,
                Enabled = InteriorsOn,
                AfterChange = Changed,
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Interiors,
                Float = Settings.InteriorsBrightness,
                DefaultFloat = Settings.Defaults.InteriorsBrightness,
                Min = 25f, Max = 300f, Step = 5f,
                Enabled = InteriorsOn,
                AfterChange = Changed,
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Interiors,
                Float = Settings.InteriorsColoured,
                DefaultFloat = Settings.Defaults.InteriorsColoured,
                Min = 0f, Max = 30f, Step = 1f,
                Enabled = InteriorsOn,
                AfterChange = Changed,
            });

            // Workplaces' own share (the author's call, 2026-11-08: none in offices by default).
            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Interiors,
                Float = Settings.InteriorsColouredWork,
                DefaultFloat = Settings.Defaults.InteriorsColouredWork,
                Min = 0f, Max = 30f, Step = 1f,
                Enabled = InteriorsOn,
                AfterChange = Changed,
            });
        }

        /// <summary>
        /// Options page -> Rendering: how far the glass reaches, and what the live sky costs. The
        /// machine, not the look (the F8 panel's Rendering tab until 2026-10-04: the author moved it
        /// here).
        /// </summary>
        private static void BuildRendering()
        {
            Add(new Row
            {
                Kind = RowKind.Choice,
                Options = OptionsPage.General,
                Group = "Rendering",
                Int = Settings.GlassDistance,
                DefaultInt = Settings.Defaults.GlassDistance,
                ChoiceKeys = new[] { "GlassDistance.Near", "GlassDistance.Middle", "GlassDistance.City" },
                ChoiceValues = new[] { 0, 1, 2 },
                Profiled = false,
                Enabled = GlassOn,
                AfterChange = State("glass drawn at", Chosen(Settings.GlassDistance, SystemReport.DistanceName)),
            });

            Add(new Row
            {
                Kind = RowKind.Choice,
                Options = OptionsPage.General,
                Group = "LiveSky",
                Int = Settings.SkyCubeSize,
                DefaultInt = Settings.Defaults.SkyCubeSize,
                ChoiceValues = new[] { 0, 1, 2 },
                ChoiceText = CubeSize,
                Profiled = false,
                Enabled = LiveSkyOn,
                AfterChange = State("live sky reflection size", () => Settings.SkyCubePixels + " px"),
            });

            Add(new Row
            {
                Kind = RowKind.Choice,
                Options = OptionsPage.General,
                Int = Settings.SkyCubeRate,
                DefaultInt = Settings.Defaults.SkyCubeRate,
                ChoiceValues = new[] { 1, 2, 3, 4, 5, 6 },
                ChoiceUnit = "Unit.FacesPerFrame",
                Profiled = false,
                Enabled = LiveSkyOn,
                AfterChange = Changed,
            });
        }

        /// <summary>
        /// F8 -> Water: the map's water (Water/WaterController.cs), on by default, apart from the glass.
        /// Its switch, then six headings: color, ripples, foam, sky, glitter, city.
        /// </summary>
        /// <summary>
        /// F8 -> Vehicles: glass on every vehicle near enough for the game to draw it in full
        /// (Vehicles/VehicleGlassDraw.cs): the windows its own vehicle shader shows as windows, the
        /// lamps' lenses, a dark inside, and a clear coat over the paint. Its own look (automotive glass,
        /// Glass/VehicleGlassLook.cs: clear, tinted on a share of the vehicles), not the Glass tab's (the
        /// author, 2026-10-23). No headings: four rows, and a heading new to a player's file would open folded
        /// (PanelOpenGroups lists the open ones).
        /// </summary>
        private static void BuildVehicles()
        {
            // Shown only when the vehicles' glass stopped after an error (VehicleGlassDraw.Fail).
            Add(new Row
            {
                Kind = RowKind.Status,
                Panel = PanelPage.Vehicles,
                Id = "VehiclesStatus",
                StatusText = () => VehicleGlassDraw.Failed ? Localization.Get("Status.VehiclesFailed") : string.Empty,
                Shown = () => VehicleGlassDraw.Failed,
                Profiled = false,
            });

            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Vehicles,
                Bool = Settings.VehicleGlass,
                DefaultBool = Settings.Defaults.VehicleGlass,
                Enabled = GlassOn,
                AfterChange = State("glass on vehicles", OnOff(Settings.VehicleGlass)),
            });

            // The share of vehicles with tinted windows, chosen per vehicle; the rest clear glass (VehicleGlassLook).
            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Vehicles,
                Float = Settings.VehicleTinted,
                DefaultFloat = Settings.Defaults.VehicleTinted,
                Min = 0f, Max = 100f, Step = 1f,
                Enabled = VehiclesOn,
                AfterChange = Changed,
            });

            // 100% = three times real car glass's and lacquer's own reflectance (VehicleGlassLook.ReflectionBase):
            // the glass and the coat both.
            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Vehicles,
                Float = Settings.VehicleReflections,
                DefaultFloat = Settings.Defaults.VehicleReflections,
                Min = 0f, Max = 300f, Step = 5f,
                Enabled = VehiclesOn,
                AfterChange = Changed,
            });

            // The clear coat over the paint (what the game recolours): 100% real lacquer, 0% none.
            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Vehicles,
                Float = Settings.VehicleCoat,
                DefaultFloat = Settings.Defaults.VehicleCoat,
                Min = 0f, Max = 100f, Step = 5f,
                Enabled = VehiclesOn,
                AfterChange = Changed,
            });
        }

        private static void BuildWater()
        {
            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Water,
                Bool = Settings.WaterEnabled,
                DefaultBool = Settings.Defaults.WaterEnabled,
                AfterChange = WaterState("realistic water", OnOff(Settings.WaterEnabled)),
            });

            // The water's colour: ours (the author, 2026-10-08: "rather than rely on Theme Mixer to edit
            // the water color, we can do that with our mod"), or the map theme's -- Theme Mixer's when it
            // runs (Water/WaterSurface.cs: our value sits on the water's materials, the theme's is the global).
            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Water,
                Group = "WaterColour",
                Bool = Settings.WaterThemeColor,
                DefaultBool = Settings.Defaults.WaterThemeColor,
                Enabled = WaterOn,
                AfterChange = WaterState("the map theme's water color", OnOff(Settings.WaterThemeColor)),
            });

            Add(new Row
            {
                Kind = RowKind.Colour,
                Panel = PanelPage.Water,
                Colour = Settings.WaterColor,
                DefaultColour = Settings.Defaults.WaterColor,
                Enabled = WaterOwnColourOn,
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Water,
                Float = Settings.WaterBrightness,
                DefaultFloat = Settings.Defaults.WaterBrightness,
                Min = 25f, Max = 400f, Step = 5f,
                Enabled = WaterOn,
            });

            // A dark, defined sea (WaterOptics.DeepBody; off by default: opt-in, the author's call).
            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Water,
                Bool = Settings.WaterDeep,
                DefaultBool = Settings.Defaults.WaterDeep,
                // The sky layer paints it: nothing to see without Reflect the sky like real water.
                Enabled = WaterSkyOn,
                AfterChange = WaterState("deep water", OnOff(Settings.WaterDeep)),
            });

            // The ripples (Water/WaterRipples.cs): ours, moving, in place of the game's frozen ones.
            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Water,
                Group = "WaterRipples",
                Bool = Settings.WaterRipples,
                DefaultBool = Settings.Defaults.WaterRipples,
                Enabled = WaterOn,
                AfterChange = WaterState("moving ripples", OnOff(Settings.WaterRipples)),
            });

            // 100%: as rough as the game's own ripple texture (measured in each city).
            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Water,
                Float = Settings.WaterRippleStrength,
                DefaultFloat = Settings.Defaults.WaterRippleStrength,
                Min = 0f, Max = 300f, Step = 5f,
                Enabled = WaterRipplesOn,
            });

            // The ripples as an ocean's waves (Water/WaterSpectrum.cs; the author, 2026-10-17, after
            // web-ocean: "Yes to both"): thousands of waves from a wind-sea spectrum, by an FFT.
            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Water,
                Bool = Settings.WaterOceanWaves,
                DefaultBool = Settings.Defaults.WaterOceanWaves,
                Enabled = WaterRipplesOn,
                AfterChange = WaterState("ocean waves", OnOff(Settings.WaterOceanWaves)),
            });

            // Dynamic waves (Water/WaterWeather.cs; the author, 2026-10-21): the waves' wind and roughness
            // follow the game's rain, from the Wind row's default on a dry day; off, the Wind row's as before.
            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Water,
                Bool = Settings.WaterDynamicWaves,
                DefaultBool = Settings.Defaults.WaterDynamicWaves,
                Enabled = WaterOceanOn,
                AfterChange = WaterState("dynamic waves", OnOff(Settings.WaterDynamicWaves)),
            });

            // The wind the ocean waves are raised by: their lengths (JONSWAP's peak), not their roughness.
            // The weather's while Dynamic waves is on.
            Add(new Row
            {
                Kind = RowKind.Value,
                Panel = PanelPage.Water,
                Float = Settings.WaterWind,
                DefaultFloat = Settings.Defaults.WaterWind,
                Min = 2f, Max = 20f, Step = 0.5f,
                Format = MetresPerSecond,
                Enabled = WaterWindOn,
            });

            // 100%: the crests as sharp as Tessendorf's lambda 1 makes them (the pictures').
            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Water,
                Float = Settings.WaterChop,
                DefaultFloat = Settings.Defaults.WaterChop,
                Min = 0f, Max = 200f, Step = 5f,
                Enabled = WaterOceanOn,
            });

            // The 32 waves' (ocean waves off). 100%: 16 m down to 0.5 m; every length x the slider, the
            // slopes kept (WaterOptics.Ripples).
            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Water,
                Float = Settings.WaterRippleSize,
                DefaultFloat = Settings.Defaults.WaterRippleSize,
                Min = 50f, Max = 300f, Step = 5f,
                Enabled = WaterSinesOn,
            });

            // 100%: the longest wave as the ripple size makes it; lower = calmer water, short ripples only.
            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Water,
                Float = Settings.WaterWaveSize,
                DefaultFloat = Settings.Defaults.WaterWaveSize,
                Min = 5f, Max = 100f, Step = 5f,
                Enabled = WaterSinesOn,
            });

            // The foam (Water/WaterFoam.cs): on the game's own water, from its simulation.
            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Water,
                Group = "WaterFoam",
                Bool = Settings.WaterFoam,
                DefaultBool = Settings.Defaults.WaterFoam,
                Enabled = WaterOn,
                AfterChange = WaterState("foam", OnOff(Settings.WaterFoam)),
            });

            // 100%: the model's (FoamOptics); each source below on top of it.
            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Water,
                Float = Settings.WaterFoamAmount,
                DefaultFloat = Settings.Defaults.WaterFoamAmount,
                Min = 0f, Max = 300f, Step = 5f,
                Enabled = WaterFoamOn,
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Water,
                Float = Settings.WaterFoamShore,
                DefaultFloat = Settings.Defaults.WaterFoamShore,
                Min = 0f, Max = 200f, Step = 5f,
                Enabled = WaterFoamOn,
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Water,
                Float = Settings.WaterFoamRapids,
                DefaultFloat = Settings.Defaults.WaterFoamRapids,
                Min = 0f, Max = 200f, Step = 5f,
                Enabled = WaterFoamOn,
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Water,
                Float = Settings.WaterFoamFronts,
                DefaultFloat = Settings.Defaults.WaterFoamFronts,
                Min = 0f, Max = 200f, Step = 5f,
                Enabled = WaterFoamOn,
            });

            // The player paints where the shores foam (Water/ShorePaintTool.cs), kept in the city's save.
            Add(new Row
            {
                Kind = RowKind.Button,
                Panel = PanelPage.Water,
                Id = "WaterFoamPaint",
                OnClick = Water.ShorePaintTool.Toggle,
                Enabled = () => WaterFoamOn() && !PerAsset.CreatorAsset.InEditor,
                Profiled = false,
            });

            // The brush, as ForestPainter's: size, strength, hardness (kept between sessions, no look of their own).
            Add(new Row
            {
                Kind = RowKind.Value,
                Panel = PanelPage.Water,
                Float = Settings.WaterPaintSize,
                DefaultFloat = Settings.Defaults.WaterPaintSize,
                // Water.ShorePaintTool.MinSize / MaxSize (literals: test-settings reads the range).
                Min = 16f, Max = 512f, Step = 8f,
                Format = WholeMetres,
                Enabled = WaterFoamOn,
                Profiled = false,
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Water,
                Float = Settings.WaterPaintStrength,
                DefaultFloat = Settings.Defaults.WaterPaintStrength,
                Min = 5f, Max = 100f, Step = 5f,
                Enabled = WaterFoamOn,
                Profiled = false,
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Water,
                Float = Settings.WaterPaintHardness,
                DefaultFloat = Settings.Defaults.WaterPaintHardness,
                Min = 0f, Max = 100f, Step = 5f,
                Enabled = WaterFoamOn,
                Profiled = false,
            });

            Add(new Row
            {
                Kind = RowKind.Button,
                Panel = PanelPage.Water,
                Id = "WaterFoamPaintClear",
                OnClick = ConfirmClearShorePaint,
                Enabled = () => WaterFoamOn() && Water.ShorePaint.Mask.Any,
                Profiled = false,
            });

            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Water,
                Group = "WaterSky",
                Bool = Settings.WaterRealSky,
                DefaultBool = Settings.Defaults.WaterRealSky,
                Enabled = WaterOn,
                AfterChange = WaterState("water reflects the sky like real water", OnOff(Settings.WaterRealSky)),
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Water,
                Float = Settings.WaterSky,
                DefaultFloat = Settings.Defaults.WaterSky,
                Min = 0f, Max = 300f, Step = 5f,
                Enabled = WaterSkyOn,
            });

            // The sun's glitter (Water/WaterSkyLayer.cs): 100% is real water's -- a sunset's path
            // blinds as it does in a photograph; the game's own highlight is off while it draws.
            // A heading of its own: the players asked for these rows (1.1.0).
            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Water,
                Group = "WaterGlitter",
                Float = Settings.WaterGlitter,
                DefaultFloat = Settings.Defaults.WaterGlitter,
                Min = 0f, Max = 200f, Step = 5f,
                Enabled = WaterSkyOn,
            });

            // Its size: 100% the waves' own spread (a third of a city camera's screen); smaller, as bright.
            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Water,
                Float = Settings.WaterGlitterSize,
                DefaultFloat = Settings.Defaults.WaterGlitterSize,
                Min = 10f, Max = 200f, Step = 5f,
                Enabled = WaterGlitterOn,
            });

            // The moon's, at night (the game lights the water by the moon before 6:00 and after 18:00).
            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Water,
                Float = Settings.WaterMoonGlitter,
                DefaultFloat = Settings.Defaults.WaterMoonGlitter,
                Min = 0f, Max = 200f, Step = 5f,
                Enabled = WaterSkyOn,
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Water,
                Float = Settings.WaterMoonGlitterSize,
                DefaultFloat = Settings.Defaults.WaterMoonGlitterSize,
                Min = 10f, Max = 200f, Step = 5f,
                Enabled = WaterMoonGlitterOn,
            });

            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Water,
                Group = "WaterCity",
                Bool = Settings.WaterCity,
                DefaultBool = Settings.Defaults.WaterCity,
                Enabled = WaterOn,
                AfterChange = WaterState("water reflects the city", OnOff(Settings.WaterCity)),
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Water,
                Float = Settings.WaterCityDay,
                DefaultFloat = Settings.Defaults.WaterCityDay,
                Min = 0f, Max = 300f, Step = 5f,
                Enabled = WaterCityOn,
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Water,
                Float = Settings.WaterCityNight,
                DefaultFloat = Settings.Defaults.WaterCityNight,
                Min = 0f, Max = 300f, Step = 5f,
                Enabled = WaterCityOn,
            });

            // How much the ripples bend the city's reflection (WaterOptics.CityRippleGain): 100% today's look.
            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Water,
                Float = Settings.WaterCityWaves,
                DefaultFloat = Settings.Defaults.WaterCityWaves,
                Min = 0f, Max = 300f, Step = 5f,
                Enabled = WaterCityOn,
            });

            // In tenths of a percent (the author, 2026-10-04: 5%, then the first step, was already too
            // long): 100% is the waves of a light breeze, a smear of a fifth of the screen.
            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Water,
                Float = Settings.WaterCityStreaks,
                DefaultFloat = Settings.Defaults.WaterCityStreaks,
                Min = 0f, Max = 10f, Step = 0.1f,
                Format = PercentTenths,
                Enabled = WaterCityOn,
            });

            // How much longer than the city its reflection is drawn (WaterOptics.MirrorSlope).
            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Water,
                Float = Settings.WaterCityLength,
                DefaultFloat = Settings.Defaults.WaterCityLength,
                Min = 100f, Max = 300f, Step = 5f,
                Enabled = WaterCityOn,
            });

            // The view from under the water (Water/WaterUnder.cs): only camera mods take the camera there.
            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Water,
                Group = "WaterUnder",
                Bool = Settings.WaterUnder,
                DefaultBool = Settings.Defaults.WaterUnder,
                Enabled = WaterOn,
                AfterChange = WaterState("the view from under the water", OnOff(Settings.WaterUnder)),
            });

            // A black target's visibility (Zaneveld & Pegau 2003): how far one sees, red going first.
            Add(new Row
            {
                Kind = RowKind.Value,
                Panel = PanelPage.Water,
                Float = Settings.WaterUnderVisibility,
                DefaultFloat = Settings.Defaults.WaterUnderVisibility,
                Min = 5f, Max = 80f, Step = 1f,
                Format = WholeMetres,
                Enabled = WaterUnderOn,
            });

            // The sun's light in the water only where its shadows let it in (the game's shadow map).
            Add(new Row
            {
                Kind = RowKind.Toggle,
                Panel = PanelPage.Water,
                Bool = Settings.WaterUnderShafts,
                DefaultBool = Settings.Defaults.WaterUnderShafts,
                Enabled = WaterUnderOn,
                AfterChange = WaterState("light shafts under the water", OnOff(Settings.WaterUnderShafts)),
            });

            Add(new Row
            {
                Kind = RowKind.Percent,
                Panel = PanelPage.Water,
                Float = Settings.WaterUnderCaustics,
                DefaultFloat = Settings.Defaults.WaterUnderCaustics,
                Min = 0f, Max = 200f, Step = 5f,
                Enabled = WaterUnderOn,
            });
        }

        /// <summary>The game's options page: the mod itself, not the glass.</summary>
        private static void BuildGeneralOptions()
        {
            // First: whoever cannot read the rest must find this one.
            Add(new Row
            {
                Kind = RowKind.Choice,
                Options = OptionsPage.General,
                Group = "Language",
                Int = Settings.Language,
                DefaultInt = Settings.Defaults.Language,
                ChoiceValues = Localization.LanguageChoices(),
                ChoiceText = Localization.LanguageName,
                Profiled = false,
                AfterChange = () =>
                {
                    Log.Msg("setting: language = " + Localization.CurrentCode +
                            (Settings.Language != null && Settings.Language.value == 0 ? " (the game's)" : " (chosen)"));
                    ModController.RebuildPanel();
                },
            });

            Add(new Row
            {
                Kind = RowKind.Key,
                Options = OptionsPage.General,
                Group = "Panel",
                Key = Settings.PanelKey,
                DefaultKey = Settings.DefaultPanelKey,
                Profiled = false,
            });

            Add(new Row
            {
                Kind = RowKind.Key,
                Options = OptionsPage.General,
                Key = Settings.PickerKey,
                DefaultKey = Settings.DefaultPickerKey,
                Profiled = false,
            });

            Add(new Row
            {
                Kind = RowKind.Key,
                Options = OptionsPage.General,
                Key = Settings.PickerNextKey,
                DefaultKey = Settings.DefaultPickerNextKey,
                Profiled = false,
            });

            // The Reflections and Panes tabs (Volumetric Weather's row; the author's call, 2026-11-08).
            Add(new Row
            {
                Kind = RowKind.Toggle,
                Options = OptionsPage.General,
                Bool = Settings.ShowAdvancedInPanel,
                DefaultBool = Settings.Defaults.ShowAdvancedInPanel,
                Profiled = false,
                AfterChange = () =>
                {
                    Log.Msg("setting: advanced options in the in-game panel = " + OnOff(Settings.ShowAdvancedInPanel)());
                    ModController.RebuildPanel();
                },
            });

            // The panels' look (ModsLibCS1, Volumetric Weather's row). Stored by value, append only;
            // listed with the default (Frosted glass) first and the old look last. A change rebuilds
            // the F8 panel and the building window where they stand, and the HUD button, which wears
            // the look too.
            Add(new Row
            {
                Kind = RowKind.Choice,
                Options = OptionsPage.General,
                Int = Settings.PanelSkin,
                DefaultInt = Settings.Defaults.PanelSkin,
                ChoiceKeys = new[] { "PanelSkin.Frosted", "PanelSkin.SeeThrough", "PanelSkin.GameBlue", "PanelSkin.Vanilla" },
                ChoiceValues = new[]
                {
                    (int)ModsLibCS1.UI.Skin.Frosted, (int)ModsLibCS1.UI.Skin.SeeThrough,
                    (int)ModsLibCS1.UI.Skin.GameBlue, (int)ModsLibCS1.UI.Skin.Vanilla,
                },
                Profiled = false,
                AfterChange = () =>
                {
                    Log.Msg("setting: panel look = " + PanelLook.Skin);
                    ModController.RebuildPanel();
                    ModController.RefreshButton();
                },
            });

            // Where the panel's button goes (1.1.7, the author's call: Unified UI, the HUD button --
            // "Toolbar" -- or none). Stored by value, append only. It replaced the switch
            // "UseUnifiedUI": a file with that off and no IconPlace meant the HUD button.
            Add(new Row
            {
                Kind = RowKind.Choice,
                Options = OptionsPage.General,
                Int = Settings.IconPlace,
                DefaultInt = Settings.Defaults.IconPlace,
                ChoiceKeys = new[] { "IconPlace.UnifiedUI", "IconPlace.Toolbar", "IconPlace.Never" },
                ChoiceValues = new[] { Settings.IconPlaces.UnifiedUI, Settings.IconPlaces.Toolbar, Settings.IconPlaces.Never },
                Profiled = false,
                OlderFile = written =>
                {
                    string old;
                    bool on;
                    if (written.TryGetValue("UseUnifiedUI", out old) && SettingsXmlFormat.TryParseBool(old, out on) && !on)
                        return Settings.IconPlaces.Toolbar.ToString(CultureInfo.InvariantCulture);

                    return null;
                },
                AfterChange = ModController.RefreshButton,
            });
        }

        /// <summary>The options page's last groups: diagnostics, and the reset.</summary>
        private static void BuildHousekeeping()
        {
            Add(new Row
            {
                Kind = RowKind.Toggle,
                Options = OptionsPage.General,
                Group = "Diagnostics",
                Bool = Settings.DetailedLogging,
                DefaultBool = Settings.Defaults.DetailedLogging,
                Profiled = false,
                AfterChange = Log.ReportLevel,
            });

            Add(new Row
            {
                Kind = RowKind.Button,
                Options = OptionsPage.General,
                Group = "StartingOver",
                Id = "ResetAll",
                OnClick = ConfirmReset,
                Profiled = false,
            });
        }

        /// <summary>
        /// Settings that are live but have no row anywhere. They are in the catalog because a
        /// reset walks this list and must reach them too, and the file is written from it.
        /// </summary>
        private static void BuildHidden()
        {
            // Where the player dragged the mod's own button (no Unified UI). Wide ranges on
            // purpose: a value out of range is clamped AND written back, so a narrower range would
            // move a saved spot on a wide screen for good. The button keeps itself on screen when
            // it is placed.
            Add(new Row
            {
                Kind = RowKind.Value,
                Float = Settings.HudButtonX,
                DefaultFloat = Settings.Defaults.HudButtonX,
                Min = 0f, Max = 10000f, Step = 1f,
                Format = Steps,
                Profiled = false,
                AfterChange = ModController.PlaceHudButton,
            });

            Add(new Row
            {
                Kind = RowKind.Value,
                Float = Settings.HudButtonY,
                DefaultFloat = Settings.Defaults.HudButtonY,
                Min = 0f, Max = 10000f, Step = 1f,
                Format = Steps,
                Profiled = false,
                AfterChange = ModController.PlaceHudButton,
            });

            // The foam's diagnostic view, set by hand in RealGlass.xml (WaterFoam.shader's _RGFoamDebug).
            Add(new Row
            {
                Kind = RowKind.Value,
                Float = Settings.WaterFoamView,
                DefaultFloat = Settings.Defaults.WaterFoamView,
                Min = 0f, Max = 3f, Step = 1f,
                Format = Steps,
                Profiled = false,
                AfterChange = WaterState("foam view", () => Settings.WaterFoamView.value.ToString("F0")),
            });

            // The F8 panel's open headings (Volumetric Weather's). Its headings are its UI; the panel
            // reads the value on every refresh, so a hand edit or a reset shows at once.
            Add(new Row
            {
                Kind = RowKind.Text,
                Text = Settings.PanelOpenGroups,
                DefaultText = Settings.Defaults.PanelOpenGroups,
                Profiled = false,
            });
        }

        /// <summary>The painted shores all taken away, once the player says yes (one step back in the paint tool).</summary>
        private static void ConfirmClearShorePaint()
        {
            try
            {
                ConfirmPanel.ShowModal(Mod.DisplayName, Localization.Get("WaterFoamPaintClear.Confirm"),
                    (component, result) =>
                    {
                        if (result == 1)
                            Water.ShorePaint.ClearAll();
                        RefreshAllUIs();
                    });
            }
            catch (Exception e)
            {
                Log.Error("water: could not ask about clearing the painted shores", e);
            }
        }

        private static void ConfirmReset()
        {
            try
            {
                ConfirmPanel.ShowModal(Mod.DisplayName, Localization.Get("ResetAll.Confirm"),
                    (component, result) =>
                    {
                        if (result != 1)
                        {
                            Log.Msg("settings: reset cancelled");
                            return;
                        }

                        ResetAll();
                        RefreshAllUIs();
                    });
            }
            catch (Exception e)
            {
                // Nothing is reset without an answer: this one throws a tuned look away.
                Log.Error("Could not ask about resetting the settings, so nothing was changed.", e);
            }
        }
    }
}
