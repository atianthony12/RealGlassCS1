using System;
using System.Collections.Generic;
using ColossalFramework;
using ColossalFramework.UI;
using ICities;
using UnityEngine;

namespace RealGlass.UI
{
    /// <summary>
    /// The mod's page in the game's own options: the mod itself (language, the keys, the button,
    /// what the glass may cost, diagnostics, the reset). Every setting of the LOOK is in the
    /// in-game panel (<see cref="GlassPanel"/>, F8), where it can be judged against the city.
    /// </summary>
    /// <remarks>
    /// (Volumetric Weather's options page.) Two things about this page decide how it has to be
    /// written:
    ///
    /// 1. It is REBUILT, not cached. OptionsMainPanel.OnEnable -> RefreshPlugins ->
    ///    CreateCategories destroys every page and calls OnSettingsUI again, and the panel also
    ///    subscribes to eventPluginsChanged / eventPluginsStateChanged. So this runs at least once
    ///    per scene and whenever the player enables ANY mod. Every call starts from an empty
    ///    control list, nothing static holds a control from a previous call, and nothing here
    ///    subscribes to a manager (events on our own controls die with them).
    /// 2. It can run with NO CITY LOADED -- it is reachable from the main menu. Every AfterChange
    ///    in the catalog has to survive that, and so does everything here.
    ///
    /// Rows come from <see cref="SettingsCatalog"/>; the widgets are the game's own UIHelper
    /// factories, which style correctly in both contexts. UIHelper has exactly seven of them
    /// (checkbox, slider, dropdown, textfield, button, space, group) -- no key binding and no plain
    /// label -- so the key row is built by hand and group titles carry any text.
    /// </remarks>
    public class OptionsUI
    {
        /// <summary>The page as it exists right now, or null before the first build.</summary>
        public static OptionsUI Instance { get; private set; }

        private static int _buildCount;
        private static bool _loggedSliderChildren;

        private readonly List<Control> _controls = new List<Control>();
        private bool _refreshing;

        private class Control
        {
            public Row Source;
            public UISlider Slider;
            public UICheckBox Check;
            public UIDropDown Drop;
            public UIButton Button;
            public UILabel Readout;
            public UIComponent Grey;
            public bool Enabled = true;
        }

        /// <summary>Called from Mod.OnSettingsUI. Builds a whole new page every time.</summary>
        public static void Build(UIHelperBase helper)
        {
            Settings.Init();

            // Switched off after a game update (GameUpdate): one line saying so, nothing to set.
            if (GameUpdate.Off)
            {
                try
                {
                    helper.AddGroup(Localization.Get("GameUpdate.Options", ModsLibCS1.GameCheck.GameVersion));
                }
                catch (Exception e)
                {
                    Log.Error("Building the options page's off line threw.", e);
                }

                return;
            }

            _buildCount++;
            Log.Msg("options page: build #" + _buildCount +
                    " (city " + (ModController.Instance != null ? "loaded" : "not loaded") + ")");

            if (_buildCount == 1)
            {
                Log.ReportLevel();
                SettingsCatalog.LogLayout();
            }

            OptionsUI page = new OptionsUI();
            Instance = page;

            try
            {
                page.BuildPage(helper);
            }
            catch (Exception e)
            {
                Log.Error("Building the options page threw.", e);
            }
        }

        private void BuildPage(UIHelperBase helper)
        {
            BuildRows(helper, OptionsPage.General);

            UIComponent host = PanelOf(helper);
            if (host != null)
            {
                host.eventVisibilityChanged += (component, visible) =>
                {
                    if (visible)
                        RefreshValues();
                };
            }

            RefreshEnabled();

            if (_buildCount == 1)
                Log.Msg("options page: built with " + _controls.Count + " controls");
        }

        private void BuildRows(UIHelperBase page, OptionsPage which)
        {
            UIHelperBase current = page;
            UIComponent currentPanel = PanelOf(page);

            foreach (Row row in SettingsCatalog.Rows)
            {
                if (row.Options != which)
                    continue;

                if (row.Group != null)
                {
                    current = page.AddGroup(row.GroupTitle);
                    currentPanel = PanelOf(current);
                }

                // UIHelper has no plain label, so a row that carries a warning gets a group of its
                // own with the warning as the title -- above the control rather than under it.
                if (row.Note != null)
                {
                    current = page.AddGroup(row.Note);
                    currentPanel = PanelOf(current);
                }

                Control control = BuildRow(current, row, currentPanel);
                if (control != null)
                {
                    ShowTooltip(control, row.Tooltip);
                    _controls.Add(control);
                }
            }
        }

        /// <summary>A row's tooltip (<see cref="Row.Tooltip"/>) over its control and its caption.</summary>
        private static void ShowTooltip(Control control, string tooltip)
        {
            if (tooltip == null)
                return;

            UIComponent[] parts = { control.Slider, control.Check, control.Drop, control.Button, control.Readout };
            foreach (UIComponent part in parts)
            {
                if (part != null)
                    part.tooltip = tooltip;
            }
        }

        private static UIComponent PanelOf(UIHelperBase helper)
        {
            UIHelper concrete = helper as UIHelper;
            return concrete != null ? concrete.self as UIComponent : null;
        }

        private Control BuildRow(UIHelperBase helper, Row row, UIComponent groupPanel)
        {
            Control control = new Control { Source = row };

            switch (row.Kind)
            {
                case RowKind.Status:
                case RowKind.Colour:
                    // Live text and colour rows belong to the panel; this page has neither.
                    return null;

                case RowKind.Toggle:
                {
                    Row captured = row;
                    UICheckBox box = helper.AddCheckbox(row.Label, row.Bool != null && row.Bool.value,
                        isChecked =>
                        {
                            if (_refreshing)
                                return;

                            SettingsCatalog.ApplyToggle(captured, isChecked);
                        }) as UICheckBox;

                    if (box == null)
                        return null;

                    control.Check = box;
                    control.Grey = box;
                    break;
                }

                case RowKind.Button:
                {
                    Row captured = row;
                    UIButton button = helper.AddButton(row.Label, () =>
                    {
                        if (captured.OnClick == null)
                            return;

                        captured.OnClick();
                        SettingsCatalog.RefreshAllUIs();
                    }) as UIButton;

                    if (button == null)
                        return null;

                    control.Button = button;
                    control.Grey = button;
                    break;
                }

                case RowKind.Key:
                    control.Button = AddKeyRow(helper, row);
                    control.Grey = control.Button;
                    if (control.Button == null)
                        return null;
                    break;

                case RowKind.Choice:
                {
                    Row captured = row;
                    UIDropDown drop = helper.AddDropdown(row.Label, row.Choices, row.ChoiceIndex,
                        index =>
                        {
                            if (_refreshing || captured.Int == null || captured.ChoiceValues == null)
                                return;

                            if (index < 0 || index >= captured.ChoiceValues.Length)
                                return;

                            captured.Int.value = captured.ChoiceValues[index];
                            if (captured.AfterChange != null)
                                captured.AfterChange();

                            SettingsCatalog.RefreshAllUIs();
                        }) as UIDropDown;

                    if (drop == null)
                        return null;

                    control.Drop = drop;
                    control.Grey = RowPanel(drop, groupPanel);
                    break;
                }

                default:
                {
                    Row captured = row;
                    float value = Mathf.Clamp(row.Display, row.Min, row.Max);

                    UISlider slider = helper.AddSlider(row.Label, row.Min, row.Max, row.Step, value,
                        v =>
                        {
                            if (_refreshing)
                                return;

                            captured.Store(v);
                            UpdateReadout(captured, v);

                            // A row with a live-apply hook may have moved OTHER rows, so both UIs
                            // look again.
                            if (captured.AfterChange != null)
                            {
                                captured.AfterChange();
                                SettingsCatalog.RefreshAllUIs();
                            }
                        }) as UISlider;

                    if (slider == null)
                        return null;

                    control.Slider = slider;
                    control.Readout = FindSliderLabel(slider, row);
                    control.Grey = RowPanel(slider, groupPanel);

                    // The UIHelper slider has no readout of its own, so the row's own label carries
                    // the value: "Floor height: 3.8 m".
                    if (control.Readout != null)
                        control.Readout.text = row.Label + ": " + row.FormatDisplay(value);
                    break;
                }
            }

            return control;
        }

        /// <summary>
        /// Greys the whole row where the template gave the control a panel of its own, and just the
        /// control where it did not -- greying a group's own panel would take every other row in the
        /// group with it.
        /// </summary>
        private static UIComponent RowPanel(UIComponent control, UIComponent groupPanel)
        {
            UIComponent parent = control.parent;
            return parent != null && parent != groupPanel ? parent : control;
        }

        private void UpdateReadout(Row row, float display)
        {
            foreach (Control control in _controls)
            {
                if (control.Source == row && control.Readout != null)
                    control.Readout.text = row.Label + ": " + row.FormatDisplay(display);
            }
        }

        /// <summary>
        /// The slider's caption. Its name is not relied on: it is the first UILabel beside the
        /// slider, and the first time round every child's name goes in the log so the guess can be
        /// checked against the game.
        /// </summary>
        private static UILabel FindSliderLabel(UISlider slider, Row row)
        {
            UIComponent parent = slider.parent;
            if (parent == null)
                return null;

            UILabel found = null;
            string names = string.Empty;

            for (int i = 0; i < parent.components.Count; i++)
            {
                UIComponent child = parent.components[i];
                names += (names.Length == 0 ? "" : ", ") + child.name + ":" + child.GetType().Name;

                if (found == null)
                    found = child as UILabel;
            }

            if (!_loggedSliderChildren)
            {
                _loggedSliderChildren = true;
                Log.Msg("options page: slider '" + row.Name + "' parent=" + parent.name +
                        " (" + parent.GetType().Name + ") children=[" + names + "] label=" +
                        (found == null ? "NOT FOUND" : found.name));
            }

            return found;
        }

        /// <summary>
        /// A key-binding row, which UIHelper cannot make. A plain options button that captures the
        /// next key press: Escape cancels, Backspace unbinds. Modal while it listens, so the key
        /// being bound does not also fire as a game hotkey.
        /// </summary>
        private static UIButton AddKeyRow(UIHelperBase helper, Row row)
        {
            SavedInputKey key = row.Key;
            if (key == null)
                return null;

            // The click is handled by eventMouseDown below; UIHelper calls its callback with no
            // arguments and a null one would throw, so it gets an empty delegate.
            UIButton button = helper.AddButton(row.Label + ":  " + key.ToLocalizedString("KEYNAME"),
                () => { }) as UIButton;
            if (button == null)
                return null;

            bool editing = false;

            button.eventMouseDown += (component, e) =>
            {
                if (editing)
                    return;

                e.Use();
                editing = true;
                button.buttonsMask = UIMouseButton.Left | UIMouseButton.Right | UIMouseButton.Middle;
                button.text = row.Label + ":  " + Localization.Get("Key.Press");
                button.Focus();
                UIView.PushModal(button);
            };

            button.eventKeyDown += (component, e) =>
            {
                if (!editing)
                    return;

                e.Use();

                if (e.keycode == KeyCode.Backspace)
                    key.value = SavedInputKey.Encode(KeyCode.None, false, false, false);
                else if (IsModifier(e.keycode))
                    return;
                else if (e.keycode != KeyCode.Escape)
                    key.value = SavedInputKey.Encode(e.keycode, e.control, e.shift, e.alt);

                editing = false;
                UIView.PopModal();
                button.buttonsMask = UIMouseButton.Left;
                button.text = row.Label + ":  " + key.ToLocalizedString("KEYNAME");
                SettingsXml.SaveNow();
            };

            return button;
        }

        private static bool IsModifier(KeyCode code)
        {
            return code == KeyCode.LeftControl || code == KeyCode.RightControl
                || code == KeyCode.LeftShift || code == KeyCode.RightShift
                || code == KeyCode.LeftAlt || code == KeyCode.RightAlt;
        }

        /// <summary>
        /// Re-reads every control. The same settings can be changed from the in-game panel or the
        /// file, and the controls go stale between the page's own rebuilds.
        /// </summary>
        public void RefreshValues()
        {
            _refreshing = true;
            int touched = 0;

            try
            {
                foreach (Control control in _controls)
                {
                    Row row = control.Source;

                    if (control.Slider != null && row.Float != null)
                    {
                        float display = Mathf.Clamp(row.Display, row.Min, row.Max);
                        if (!Mathf.Approximately(control.Slider.value, display))
                        {
                            control.Slider.value = display;
                            touched++;
                        }

                        if (control.Readout != null)
                            control.Readout.text = row.Label + ": " + row.FormatDisplay(display);
                    }

                    if (control.Check != null && row.Bool != null && !row.Pending
                        && control.Check.isChecked != row.Bool.value)
                    {
                        control.Check.isChecked = row.Bool.value;
                        touched++;
                    }

                    if (control.Drop != null && row.Int != null && control.Drop.selectedIndex != row.ChoiceIndex)
                    {
                        control.Drop.selectedIndex = row.ChoiceIndex;
                        touched++;
                    }

                    if (control.Source.Kind == RowKind.Key && control.Button != null && row.Key != null)
                        control.Button.text = row.Label + ":  " + row.Key.ToLocalizedString("KEYNAME");
                }
            }
            finally
            {
                _refreshing = false;
            }

            RefreshEnabled();

            if (touched > 0 && Log.Detailed)
                Log.Detail("options page: refreshed " + _controls.Count + " controls, " + touched + " had changed");
        }

        private void RefreshEnabled()
        {
            foreach (Control control in _controls)
            {
                bool wanted = control.Source.IsEnabled;
                if (wanted == control.Enabled || control.Grey == null)
                    continue;

                control.Enabled = wanted;
                control.Grey.isEnabled = wanted;
                control.Grey.opacity = wanted ? 1f : 0.45f;
            }
        }
    }
}
