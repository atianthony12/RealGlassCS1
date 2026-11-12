using System;
using System.Collections.Generic;
using ColossalFramework;
using ColossalFramework.UI;
using ModsLibCS1.UI;
using UnityEngine;

namespace RealGlass.UI
{
    /// <summary>
    /// One colour row of the in-game panel:
    ///   name | swatch | #RRGGBB field | Copy | Paste | reset
    /// (Volumetric Weather's colour row.)
    /// </summary>
    /// <remarks>
    /// The swatch is the game's OWN colour field, cloned from the transport-line template
    /// (PublicTransportLineInfo's "LineColor" in "LineTemplate"), so clicking it opens the game's
    /// own picker. The asset editor has no such template: there it is cloned from the editor's
    /// properties panel's ("Value" in DecorationPropertiesPanel's "PropertyColorSet", the material
    /// colours' field). If neither can be found the row still works -- field, buttons and
    /// reset -- and the probe line in the log says so. The field reads every spelling
    /// <see cref="ColorText"/> does; the game's text fields already do Ctrl+C / Ctrl+V (IL of
    /// UITextField.OnKeyDown), and Copy / Paste use the same clipboard (ColossalFramework.Clipboard).
    ///
    /// Each pick is logged once, when it is made -- on the picker's mouse-up, Enter, Paste or
    /// reset -- not on every frame of a drag through the picker. The row's AfterChange DOES run on
    /// every change (the glass colour marks the materials stale and turns the preset to Custom:
    /// both cheap), as a slider's does on every tick of a drag.
    /// </remarks>
    internal sealed class ColourRow
    {
        private const float SwatchSize = 26f;
        private const float Gap = 6f;

        private const string LineTemplate = "LineTemplate";
        private const string LineColour = "LineColor";
        private const string EditorTemplate = "PropertyColorSet";
        private const string EditorColour = "Value";

        /// <summary>How long a colour the row could not read stays on show, in red.</summary>
        private const float InvalidShownFor = 3f;
        private const float CopiedShownFor = 1.5f;

        /// <summary>The probe line last logged.</summary>
        private static string _probeLogged;

        /// <summary>The template the last swatch was cloned from, for the probe line.</summary>
        private static string _swatchFrom;

        private readonly Builder _ui;
        private readonly Row _row;
        private readonly UILabel _name;
        private readonly UIColorField _swatch;
        private readonly UITextField _text;
        private readonly UIButton _copy;
        private readonly UIButton _reset;
        private readonly List<UIComponent> _parts = new List<UIComponent>();

        private Color32 _shown;
        private string _logged;
        private bool _refreshing;
        private float _invalidUntil = -1f;
        private float _copiedUntil = -1f;

        public ColourRow(Builder ui, UIComponent parent, float y, Row row, RowLayout layout)
        {
            _ui = ui;
            _row = row;
            _shown = Current;
            _logged = ColorText.Format(_shown);

            float textWidth = layout.ColourTextWidth;
            float buttonWidth = layout.ColourButtonWidth;

            // The page's names' column (RowList widens it for long names).
            UILabel name = _ui.AddName(parent, row.Label, new Vector3(0f, y + 8f), _ui.LabelWidth);
            _parts.Add(name);
            _name = name;

            // The parts end where every row's controls end (before the reset). Beside a wide names'
            // column they reach left into it as far as the row's own name leaves room, as a choice
            // row's buttons do (Volumetric Weather: past it, Paste ran under the reset icon).
            float parts = SwatchSize + Gap + textWidth + Gap + buttonWidth + Gap + buttonWidth;
            float column = _ui.LabelWidth + 4f;
            float x = Mathf.Max(Mathf.Min(column, parent.width - _ui.ResetColumn - parts),
                _ui.NameWidth(row.Label) + ChoiceFit.NameRoom);
            if (x < column)
                name.width = x - 4f;

            _swatch = CreateSwatch(parent, new Vector3(x, y + 4f));
            if (_swatch != null)
            {
                // The clone still holds the template's colour; Refresh only writes on a change.
                _swatch.selectedColor = _shown;
                _swatch.eventSelectedColorChanged += (component, colour) =>
                {
                    if (!_refreshing)
                        Store(colour);
                };
                _swatch.eventSelectedColorReleased += (component, colour) =>
                {
                    if (_refreshing)
                        return;

                    Store(colour);
                    Commit("picked");
                };
                _parts.Add(_swatch);
            }

            x += SwatchSize + Gap;

            _text = _ui.AddTextField(parent, new Vector2(textWidth, 26f), new Vector3(x, y + 4f), 64);
            _text.text = ColorText.Format(_shown);
            _text.eventTextSubmitted += (component, text) => Submit(text);
            _text.eventTextCancelled += (component, text) => ShowValid();
            _parts.Add(_text);

            x += textWidth + Gap;

            _copy = _ui.AddButton(parent, Localization.Get("Colour.Copy"), new Vector2(buttonWidth, 26f), new Vector3(x, y + 4f));
            _copy.canFocus = false;
            _copy.eventClick += (component, e) => Copy();
            _parts.Add(_copy);

            x += buttonWidth + Gap;

            UIButton paste = _ui.AddButton(parent, Localization.Get("Colour.Paste"), new Vector2(buttonWidth, 26f), new Vector3(x, y + 4f));
            paste.canFocus = false;
            paste.eventClick += (component, e) => Paste();
            _parts.Add(paste);

            // The row's reset: the same icon as every other row's (it was a "Default" text button),
            // right-aligned. In the building window it is "back to the Glass tab's colour": the asset
            // follows the global colour again. Not in Parts: it fades on its own rule (already there).
            _reset = _ui.AddReset(parent, y, row.ResetHint);
            if (_reset != null)
                _reset.eventClick += (component, e) => Reset();

            // The faint line under the row, in the looks that draw one.
            _ui.AddRowLine(parent, y);

            Probe();
            Refresh();
        }

        /// <summary>Everything that greys out with the row.</summary>
        public IList<UIComponent> Parts
        {
            get { return _parts; }
        }

        /// <summary>The row's name (the building window colours it while the asset has a colour of its own).</summary>
        public UILabel NameLabel
        {
            get { return _name; }
        }

        private Color32 Current
        {
            get { return _row.Colour != null ? _row.Colour.value : _row.DefaultColour; }
        }

        /// <summary>
        /// Re-reads the setting (the panel calls this four times a second): a reset, a preset, a
        /// hand edit of RealGlass.xml or the other UI may have changed it. Writes only what
        /// changed, and never over text the player is typing.
        /// </summary>
        public void Refresh()
        {
            _refreshing = true;
            try
            {
                Color32 current = Current;
                if (!ColorText.Same(current, _shown))
                {
                    _shown = current;
                    if (_swatch != null)
                        _swatch.selectedColor = current;
                    if (!_text.hasFocus && _invalidUntil < 0f)
                        ShowValid();
                }

                float now = Time.realtimeSinceStartup;
                if (_invalidUntil >= 0f && now > _invalidUntil && !_text.hasFocus)
                    ShowValid();

                if (_copiedUntil >= 0f && now > _copiedUntil)
                {
                    _copiedUntil = -1f;
                    _copy.text = _ui.Caps(Localization.Get("Colour.Copy"));
                }

                Builder.SetResetEnabled(_reset, ResetWanted);
            }
            finally
            {
                _refreshing = false;
            }
        }

        private void Store(Color32 colour)
        {
            if (_row.Colour == null)
                return;

            bool moved = !ColorText.Same(_row.Colour.value, colour);
            _row.Colour.value = colour;
            _shown = _row.Colour.value;

            _refreshing = true;
            try
            {
                if (_swatch != null && !ColorText.Same((Color32)_swatch.selectedColor, _shown))
                    _swatch.selectedColor = _shown;
                if (!_text.hasFocus)
                    ShowValid();
            }
            finally
            {
                _refreshing = false;
            }

            Builder.SetResetEnabled(_reset, ResetWanted);

            // Only for a real change: the picker fires on every frame of a drag, most of them
            // with the colour it already had.
            if (moved && _row.AfterChange != null)
            {
                _row.AfterChange();
                SettingsCatalog.RefreshAllUIs();
            }
        }

        /// <summary>The reset icon: the default back, or (the building window) the Glass tab's colour.</summary>
        private void Reset()
        {
            if (_row.ClearOverride != null)
            {
                if (_row.Overridden == null || !_row.Overridden())
                    return;

                _row.ClearOverride();
                SettingsCatalog.RefreshAllUIs();
                return;
            }

            if (_row.IsAtResetValue)
                return;

            // The picked profile's colour (Volumetric Weather's), or the default without one.
            string kept = _row.ResetText;
            Color32 colour;
            if (kept == null || !ColorText.TryParse(kept, out colour))
            {
                Store(_row.DefaultColour);
                Commit("reset to the default");
                return;
            }

            Store(colour);
            Commit("reset to profile '" + Profiles.Selected + "'");
        }

        /// <summary>Its reset's words on hover, which follow the profile pick (the panel sets them again).</summary>
        public string ResetHint
        {
            set
            {
                if (_reset != null)
                    _reset.tooltip = value;
            }
        }

        /// <summary>
        /// The reset is not in Parts (it fades on its own rule: already where it goes back to), so it
        /// follows the row's own switch here -- the glass colour greys out with the glass.
        /// </summary>
        private bool ResetWanted
        {
            get
            {
                if (_row.Overridden != null)
                    return _row.IsEnabled && _row.Overridden();
                return !_row.IsAtResetValue && _row.IsEnabled;
            }
        }

        /// <summary>One log line per pick, and only when the colour really moved.</summary>
        private void Commit(string how)
        {
            string hex = ColorText.Format(Current);
            if (hex == _logged)
                return;

            _logged = hex;
            Log.Msg("setting: " + _row.Name + " = " + hex + " (" + how + ")");
        }

        private void Submit(string text)
        {
            if (_refreshing)
                return;

            Color32 colour;
            if (ColorText.TryParse(text, out colour))
            {
                Store(colour);
                Commit("typed");
                ShowValid();
                return;
            }

            // The text may be the colour already on show (Enter on an untouched field).
            if (text == ColorText.Format(_shown))
                return;

            ShowInvalid(text);
        }

        private void Copy()
        {
            try
            {
                Clipboard.text = ColorText.Format(Current);
                _copy.text = _ui.Caps(Localization.Get("Colour.Copied"));
                _copiedUntil = Time.realtimeSinceStartup + CopiedShownFor;
            }
            catch (Exception e)
            {
                Log.Warn("colour: could not copy to the clipboard: " + e.Message);
            }
        }

        private void Paste()
        {
            string text;
            try
            {
                text = Clipboard.text;
            }
            catch (Exception e)
            {
                Log.Warn("colour: could not read the clipboard: " + e.Message);
                return;
            }

            Color32 colour;
            if (ColorText.TryParse(text, out colour))
            {
                Store(colour);
                Commit("pasted");
                ShowValid();
            }
            else
            {
                ShowInvalid(text ?? string.Empty);
            }
        }

        private void ShowValid()
        {
            _invalidUntil = -1f;
            _text.text = ColorText.Format(_shown);
            _text.textColor = _ui.Theme.FieldText;
        }

        /// <summary>What could not be read stays on show in red for a moment, then goes back.</summary>
        private void ShowInvalid(string text)
        {
            string shown = text.Trim();
            if (shown.Length > 40)
                shown = shown.Substring(0, 40);

            _text.text = shown;
            _text.textColor = _ui.Theme.Invalid;
            _invalidUntil = Time.realtimeSinceStartup + InvalidShownFor;
        }

        /// <summary>The game's own colour field, cloned out of the transport-line template (the asset editor's in the editor).</summary>
        private static UIColorField CreateSwatch(UIComponent parent, Vector3 position)
        {
            try
            {
                _swatchFrom = LineTemplate + "/" + LineColour;
                UIColorField source = FindField(LineTemplate, LineColour);
                if (source == null)
                {
                    _swatchFrom = EditorTemplate + "/" + EditorColour;
                    source = FindField(EditorTemplate, EditorColour);
                }
                if (source == null)
                    return null;

                GameObject copy = UnityEngine.Object.Instantiate(source.gameObject);
                copy.SetActive(true);

                UIColorField field = parent.AttachUIComponent(copy) as UIColorField;
                if (field == null)
                {
                    UnityEngine.Object.Destroy(copy);
                    return null;
                }

                // The template's field arrives anchored Left | Right | CenterVertical (Volumetric
                // Weather logged it), and an anchored control follows its parent's size: each time
                // a tab page changed height the swatch was re-centred and stretched with it. A
                // swatch sits where it was put.
                field.anchor = UIAnchorStyle.None;

                field.name = "RealGlassColour";
                field.size = new Vector2(SwatchSize, SwatchSize);
                field.relativePosition = position;
                field.isVisible = true;
                field.pickerPosition = UIColorField.ColorPickerPosition.RightBelow;
                field.popupTopmostRoot = true;
                return field;
            }
            catch (Exception e)
            {
                Log.Warn("colour: the game's colour field could not be cloned (" + e.GetType().Name + ": " + e.Message +
                         "); the row works without its swatch");
                return null;
            }
        }

        /// <summary>A colour field in one of the scene's UI templates; null where the scene has no such template.</summary>
        private static UIColorField FindField(string templateName, string fieldName)
        {
            UIComponent template = UITemplateManager.Peek(templateName);
            return template != null ? template.Find<UIColorField>(fieldName) : null;
        }

        /// <summary>
        /// Whether the game's picker was found, so a plain-looking row explains itself: logged when it
        /// differs from the last line (a city and the asset editor clone it from different templates).
        /// </summary>
        private void Probe()
        {
            string line = "colour rows: the game's colour field " +
                          (_swatch != null
                              ? "cloned from " + _swatchFrom + ", picker " + (_swatch.colorPicker != null ? "ok" : "MISSING")
                              : "NOT FOUND (" + LineTemplate + "/" + LineColour + ", " + EditorTemplate + "/" + EditorColour + ") -- text field only");
            if (line == _probeLogged)
                return;

            _probeLogged = line;
            Log.Msg(line);
        }
    }
}
