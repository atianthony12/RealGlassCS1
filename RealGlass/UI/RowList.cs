using System.Collections.Generic;
using ColossalFramework.UI;
using ModsLibCS1.UI;
using UnityEngine;

namespace RealGlass.UI
{
    /// <summary>
    /// How wide a row's parts are at least: the F8 panel's, and the building window's narrower ones.
    /// The names' column grows to a page's longest name; the readouts' column fits a page's widest
    /// readout in the new looks (<see cref="RowList.Build"/>).
    /// </summary>
    public sealed class RowLayout
    {
        /// <summary>The names' column, at least.</summary>
        public float LabelWidth;

        /// <summary>Vanilla's readout column (the new looks fit theirs to each page).</summary>
        public float ValueWidth;

        /// <summary>A colour row's #RRGGBB field.</summary>
        public float ColourTextWidth;

        /// <summary>A colour row's Copy and Paste buttons.</summary>
        public float ColourButtonWidth;

        /// <summary>
        /// Wide enough for the longest of each (Volumetric Weather's measure): a label of some thirty
        /// characters on the left, "x6 a frame" on the right.
        /// </summary>
        public static readonly RowLayout Panel = new RowLayout
        {
            LabelWidth = 196f, ValueWidth = 118f, ColourTextWidth = 116f, ColourButtonWidth = 62f,
        };

        /// <summary>The building window: a label of some twenty-five characters, a little less for the value.</summary>
        public static readonly RowLayout Compact = new RowLayout
        {
            LabelWidth = 166f, ValueWidth = 112f, ColourTextWidth = 84f, ColourButtonWidth = 56f,
        };
    }

    /// <summary>
    /// Catalog rows drawn with ModsLibCS1 in the panels' look, and kept current: the F8 panel's tabs
    /// (<see cref="GlassPanel"/>) and the building window (<see cref="AssetPanel"/>) both build their
    /// rows here. Every row with a setting ends in a reset icon: back to the default in the panel
    /// (<see cref="SettingsCatalog.ResetRow"/>), back to the F8 panel's setting in the building
    /// window (<see cref="Row.ClearOverride"/>, which had a "Global" button before). A row with a
    /// tooltip (<see cref="Row.Tooltip"/>) shows it over its name and its control.
    /// </summary>
    /// <remarks>
    /// FOLDING (the F8 panel, 2026-10-21, Volumetric Weather 1.5's accordions -- the author: "Like VW, we
    /// can do things in accordions"): a click on a heading folds its rows away and back, and the page
    /// closes up under them. The open ones are kept in Settings.PanelOpenGroups (each tab's first, at
    /// first). Rows above a tab's first heading (its switch) never fold. A row whose
    /// <see cref="Row.Shown"/> says no is hidden the same way (the status line with nothing to say).
    /// Every row is built where it would be with all shown; <see cref="Relayout"/> moves them.
    /// (Volumetric Weather's CloudsPanel.Relayout.)
    /// </remarks>
    internal sealed class RowList
    {
        private const float HeadingHeight = 24f;
        private const float NoteHeight = 32f;

        /// <summary>A folding heading's room above and below its strip, between groups (Volumetric Weather's).</summary>
        private const float FoldMarginTop = 8f;
        private const float FoldMarginBottom = 4f;

        /// <summary>The reset icon's column at the right end of each row, gap included (Volumetric Weather's).</summary>
        public const float ResetColumn = 28f;

        /// <summary>The shortest a slider gets in the new looks, where the names take what they need.</summary>
        private const float MinSlider = 70f;

        /// <summary>
        /// The new looks' readout column, per page: as wide as the page's widest readout plus this (a
        /// readout can run a little wider than the ones measured), never narrower than
        /// <see cref="MinValueWidth"/>, never wider than the skin's own (Volumetric Weather's).
        /// </summary>
        private const float ReadoutRoom = 8f;
        private const float MinValueWidth = 40f;

        /// <summary>A row's name while the picked asset has a value of its own there (the building window).</summary>
        private static readonly Color32 OwnValueColour = new Color32(255, 214, 120, 255);

        private readonly Builder _ui;
        private readonly RowLayout _layout;
        private readonly List<Control> _controls = new List<Control>();

        /// <summary>Every page built, in order: its stretches top to bottom, and its name for the log.</summary>
        private readonly List<List<Item>> _pages = new List<List<Item>>();
        private readonly List<string> _pageIds = new List<string>();

        /// <summary>Headings fold (the F8 panel); the building window's stay plain.</summary>
        private readonly bool _folding;

        /// <summary>The groups left open (Settings.PanelOpenGroups), as last read; every other one is folded away.</summary>
        private readonly List<string> _open = new List<string>();
        private string _openRead;

        /// <summary>
        /// Guards the refresh against its own callbacks: a row set from its setting must never write
        /// the setting straight back -- quantised to the slider's step, and with every AfterChange
        /// fired for a change nobody made. (The library's SetValue / SetIndex are quiet as well.)
        /// </summary>
        private bool _refreshing;

        /// <summary>The profile pick the reset icons' words were last set for (<see cref="RefreshValues"/>).</summary>
        private string _resetTipsFor;

        /// <summary>
        /// A page's rows were shown, hidden or folded after it was built: the page (in build order), its
        /// new height, and the stretch (top, bottom) a group just opened takes, to scroll into view --
        /// or -1, -1.
        /// </summary>
        public System.Action<int, float, float, float> PageResized;

        public RowList(Builder ui, RowLayout layout, bool folding)
        {
            _ui = ui;
            _layout = layout;
            _folding = folding;
            if (folding)
                ReadOpen();
        }

        public int Count
        {
            get { return _controls.Count; }
        }

        /// <summary>
        /// One stretch of a page: a group heading, or a row with its note -- every component put on
        /// the page for it, so it can be hidden and everything under it moved up as a whole.
        /// </summary>
        private class Item
        {
            public Control Control;   // null for a heading
            public readonly List<UIComponent> Components = new List<UIComponent>();
            public readonly List<UIComponent> HiddenByUs = new List<UIComponent>();
            public float Y;
            public float Height;
            public bool Shown = true;

            /// <summary>The group this heading starts, or this row sits under. Null above the first heading, and under a heading that cannot fold.</summary>
            public string Group;

            public HeadingRow Heading;
        }

        /// <summary>One built row: whatever components it made, so they can be refreshed and greyed.</summary>
        private class Control
        {
            public Row Source;
            public SliderRow Slider;
            public ChoiceRow Choice;
            public ToggleRow Toggle;
            public KeyRow Key;
            public string KeyShown;
            public UILabel Status;
            public ColourRow Colour;
            public UIButton Reset;
            public readonly List<UIComponent> Parts = new List<UIComponent>();
            public bool Enabled = true;

            /// <summary>The building window's rows: the name, coloured while the asset has its own value.</summary>
            public UILabel Name;
            public bool? ShownOwn;
        }

        /// <summary>
        /// Draws these rows down the page, in order, with the page's own names' and readouts' columns,
        /// then closes the page up under the folded groups and hidden rows. Returns the height the
        /// shown ones take. <paramref name="id"/> names the page in the log.
        /// </summary>
        public float Build(UIPanel page, IEnumerable<Row> rows, string id)
        {
            FitColumns(page, rows, id);
            _ui.ResetColumn = ResetColumn;

            int pageIndex = _pages.Count;
            List<Item> items = new List<Item>();
            HashSet<UIComponent> claimed = new HashSet<UIComponent>();
            string group = null;

            float y = 0f;
            foreach (Row row in rows)
            {
                if (row.Kind == RowKind.Text)
                    continue;

                if (row.Group != null)
                {
                    float height = HeadingHeight;
                    HeadingRow heading;
                    if (_folding)
                    {
                        // Its strip (Builder.FoldHeight), with room above and below it between groups.
                        string captured = row.Group;
                        heading = _ui.AddFoldingHeading(page, row.GroupTitle, y + FoldMarginTop + Builder.FoldAbove,
                            !IsFolded(captured), open => SetFolded(pageIndex, captured, !open));
                        height = FoldMarginTop + Builder.FoldHeight + FoldMarginBottom;
                    }
                    else
                    {
                        heading = _ui.AddHeading(page, row.GroupTitle, y + 4f);
                    }

                    // A heading drawn plain (Vanilla has no arrows, or the library's sheet is missing)
                    // can't open again: nothing under it folds, and it stands where a plain one always did.
                    group = heading != null && heading.Fold != null ? row.Group : null;
                    if (_folding && group == null && heading != null)
                    {
                        foreach (UIComponent part in heading.Parts)
                            part.relativePosition += new Vector3(0f, 4f - FoldMarginTop - Builder.FoldAbove);
                        height = HeadingHeight;
                    }

                    Item item = Claim(page, claimed, null, y, height);
                    item.Group = group;
                    item.Heading = heading;
                    items.Add(item);
                    y += height;
                }

                float top = y;
                Control control = Build(page, row, y);
                _controls.Add(control);
                y += _ui.RowHeight;

                if (row.Note != null)
                {
                    UILabel note = _ui.AddNote(page, row.Note, y - 4f, NoteHeight, _ui.Theme.Warning);
                    control.Parts.Add(note);
                    y += NoteHeight;
                }

                Item stretch = Claim(page, claimed, control, top, y - top);
                stretch.Group = group;
                items.Add(stretch);
            }

            _pages.Add(items);
            _pageIds.Add(id);
            return Relayout(pageIndex, false, -1f, -1f);
        }

        /// <summary>Every component on the page no stretch has yet: the ones just built.</summary>
        private static Item Claim(UIPanel page, HashSet<UIComponent> claimed, Control control, float y, float height)
        {
            Item item = new Item { Control = control, Y = y, Height = height };

            for (int i = 0; i < page.components.Count; i++)
            {
                UIComponent component = page.components[i];
                if (claimed.Add(component))
                    item.Components.Add(component);
            }

            return item;
        }

        // ---- folding ---------------------------------------------------------------------------

        private bool IsFolded(string group)
        {
            return _folding && group != null && !_open.Contains(group);
        }

        private void ReadOpen()
        {
            _open.Clear();
            _openRead = Settings.PanelOpenGroups != null ? Settings.PanelOpenGroups.value : string.Empty;
            if (string.IsNullOrEmpty(_openRead))
                return;

            foreach (string name in _openRead.Split(','))
            {
                string group = name.Trim();
                if (group.Length > 0 && !_open.Contains(group))
                    _open.Add(group);
            }
        }

        /// <summary>A heading clicked: its group folded or opened, kept in the settings file, the page closed up.</summary>
        private void SetFolded(int page, string group, bool folded)
        {
            if (folded != _open.Contains(group))
                return;

            if (folded)
                _open.Remove(group);
            else
                _open.Add(group);

            _openRead = string.Join(",", _open.ToArray());
            if (Settings.PanelOpenGroups != null)
                Settings.PanelOpenGroups.value = _openRead;
            SettingsXml.SaveNow();

            Log.Msg("panel: " + group + " " + (folded ? "folded" : "opened") + " (open now: " +
                    (_openRead.Length > 0 ? _openRead : "none") + ")");

            // The line above says what changed; the panel is told once, with the group just opened
            // to bring into view.
            float height = Relayout(page, false, -1f, -1f);
            float top = -1f, bottom = -1f;
            if (!folded)
                GroupSpan(page, group, out top, out bottom);

            if (PageResized != null)
                PageResized(page, height, top, bottom);
        }

        /// <summary>Where a group stands on its page now: its heading's top to its last shown row's bottom.</summary>
        private void GroupSpan(int page, string group, out float top, out float bottom)
        {
            top = -1f;
            bottom = -1f;
            foreach (Item item in _pages[page])
            {
                if (!item.Shown || item.Group != group)
                    continue;

                if (top < 0f)
                    top = item.Y;
                bottom = item.Y + item.Height;
            }
        }

        /// <summary>A row: shown by its own rule (<see cref="Row.Shown"/>), and its group not folded away.</summary>
        private bool RowShown(Item item)
        {
            return item.Control.Source.IsShown && !IsFolded(item.Group);
        }

        /// <summary>
        /// A heading shows while any row under it, up to the next heading, would by its own rule: a
        /// folded heading stays, to be opened again.
        /// </summary>
        private static bool HeadingShown(List<Item> items, int heading)
        {
            for (int i = heading + 1; i < items.Count && items[i].Control != null; i++)
            {
                if (items[i].Control.Source.IsShown)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Hides the rows the folds and their own rules say to and shows the rest, each page laid out
        /// again only when one of its rows has actually changed. With every refresh: a reset or a hand
        /// edit of the file can open or fold groups, the status line comes and goes.
        /// </summary>
        private void RefreshShown()
        {
            bool reread = false;
            if (_folding && Settings.PanelOpenGroups != null && Settings.PanelOpenGroups.value != _openRead)
            {
                ReadOpen();
                reread = true;
                foreach (List<Item> items in _pages)
                {
                    foreach (Item item in items)
                    {
                        if (item.Heading != null && item.Group != null)
                            item.Heading.SetOpen(!IsFolded(item.Group));
                    }
                }
            }

            for (int p = 0; p < _pages.Count; p++)
            {
                List<Item> items = _pages[p];
                for (int i = 0; i < items.Count; i++)
                {
                    bool shown = items[i].Control != null ? RowShown(items[i]) : HeadingShown(items, i);
                    if (shown != items[i].Shown || reread)
                    {
                        Relayout(p, true, -1f, -1f);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Walks a page top to bottom: each stretch shown is moved to where the ones above it end, each
        /// hidden one is hidden and takes no room. Returns the page's new height, and tells
        /// <see cref="PageResized"/> when <paramref name="notify"/> (not while the page is being built).
        /// </summary>
        private float Relayout(int index, bool notify, float revealTop, float revealBottom)
        {
            List<Item> items = _pages[index];
            float y = 0f;
            string changed = string.Empty;

            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                bool shown = item.Control != null ? RowShown(item) : HeadingShown(items, i);

                if (shown != item.Shown)
                {
                    SetShown(item, shown);
                    changed += (changed.Length == 0 ? "" : ", ") + (shown ? "+" : "-") +
                               (item.Control != null ? item.Control.Source.Name : "heading " + item.Group);
                }

                if (!shown)
                    continue;

                float dy = y - item.Y;
                if (dy != 0f)
                {
                    foreach (UIComponent component in item.Components)
                        component.relativePosition += new Vector3(0f, dy);

                    item.Y = y;
                }

                // A folding heading's arrow stands by its name: placed again where the name went.
                if (item.Heading != null && item.Group != null)
                    item.Heading.SetOpen(!IsFolded(item.Group));

                y += item.Height;
            }

            if (notify)
            {
                if (changed.Length > 0)
                    Log.Msg("panel: " + _pageIds[index] + " laid out again (" + changed + "), " + y.ToString("F0") + " high");

                if (PageResized != null)
                    PageResized(index, y, revealTop, revealBottom);
            }

            return y;
        }

        /// <summary>
        /// Hides a stretch's components, remembering which were showing, and shows exactly those again:
        /// a component its own row keeps hidden stays hidden.
        /// </summary>
        private static void SetShown(Item item, bool shown)
        {
            item.Shown = shown;

            if (!shown)
            {
                item.HiddenByUs.Clear();
                foreach (UIComponent component in item.Components)
                {
                    if (!component.isVisibleSelf)
                        continue;

                    component.isVisible = false;
                    item.HiddenByUs.Add(component);
                }

                return;
            }

            foreach (UIComponent component in item.HiddenByUs)
                component.isVisible = true;

            item.HiddenByUs.Clear();
        }

        /// <summary>
        /// The names' column, per page: as wide as the page's longest name needs (capitals in Frosted
        /// take more room), at least the layout's. At most 46% of the page in Vanilla; in the new looks
        /// as much as leaves a slider <see cref="MinSlider"/> long (the names come first, the sliders
        /// give way). The new looks' readouts' column fits the page's widest readout first. A name
        /// still too long is drawn smaller, then on two lines, never cut off (Builder.AddName).
        /// (Volumetric Weather's CloudsPanel.BuildPage.)
        /// </summary>
        private void FitColumns(UIPanel page, IEnumerable<Row> rows, string id)
        {
            float widest = 0f;
            float widestValue = 0f;
            foreach (Row row in rows)
            {
                if (UsesNameColumn(row.Kind))
                    widest = Mathf.Max(widest, _ui.NameWidth(row.Label));
                if (row.Kind == RowKind.Percent || row.Kind == RowKind.Value)
                    widestValue = Mathf.Max(widestValue, WidestReadout(row));
            }

            bool vanilla = _ui.Skin == Skin.Vanilla;
            _ui.ValueWidth = vanilla
                ? _layout.ValueWidth
                : Mathf.Clamp(widestValue + ReadoutRoom, MinValueWidth, _ui.Theme.ValueWidth);

            float most = vanilla
                ? Mathf.Floor(page.width * 0.46f)
                : page.width - ResetColumn - _ui.ValueWidth - 3f * _ui.Theme.Gap - MinSlider;
            _ui.LabelWidth = Mathf.Clamp(Mathf.Ceil(widest) + 8f, _layout.LabelWidth, Mathf.Max(_layout.LabelWidth, most));

            Log.Msg("panel: " + id + " names " + _ui.LabelWidth.ToString("F0") + " wide (longest " +
                    widest.ToString("F0") + "), values " + _ui.ValueWidth.ToString("F0") + " (widest " +
                    widestValue.ToString("F0") + ")");
        }

        /// <summary>
        /// The widest a slider row's readout is drawn, over its range (both ends, the quarters between
        /// them, and where it stands now): its text follows the value ("1.5 m", "12.0 m").
        /// </summary>
        private float WidestReadout(Row row)
        {
            float widest = _ui.ReadoutWidth(row.FormatDisplay(Mathf.Clamp(row.Display, row.Min, row.Max)));
            for (int i = 0; i <= 4; i++)
                widest = Mathf.Max(widest, _ui.ReadoutWidth(row.FormatDisplay(Mathf.Lerp(row.Min, row.Max, i * 0.25f))));

            return widest;
        }

        /// <summary>The rows whose name stands in the names' column (a tick box left of its name does not).</summary>
        private bool UsesNameColumn(RowKind kind)
        {
            switch (kind)
            {
                case RowKind.Status:
                case RowKind.Button:
                    return false;
                case RowKind.Toggle:
                    return _ui.Theme.Toggle != ToggleShape.TickLeft;
                default:
                    return true;
            }
        }

        /// <summary>The status lines' texts, now.</summary>
        public void UpdateStatus()
        {
            foreach (Control control in _controls)
            {
                if (control.Status != null && control.Source.StatusText != null)
                    control.Status.text = control.Source.StatusText();
            }
        }

        /// <summary>
        /// Re-reads every control from its setting. Cheap enough to run four times a second: a
        /// comparison per control, and a write only where something really changed. Returns how many
        /// had changed.
        /// </summary>
        public int RefreshValues()
        {
            _refreshing = true;
            int touched = 0;

            // The reset icons go back to the picked profile's values, or the defaults without one
            // (Volumetric Weather's): their words on hover say which, again whenever the pick changes.
            string pick = Profiles.Selected + "|" + Profiles.State;
            bool retip = pick != _resetTipsFor;
            _resetTipsFor = pick;

            try
            {
                foreach (Control control in _controls)
                {
                    Row row = control.Source;

                    if (retip && row.ClearOverride == null)
                    {
                        if (control.Reset != null)
                            control.Reset.tooltip = row.ResetHint;
                        if (control.Colour != null)
                            control.Colour.ResetHint = row.ResetHint;
                    }

                    // Both set quietly: the row's own callback never runs for a refresh.
                    if (control.Slider != null)
                    {
                        float display = Mathf.Clamp(row.Display, row.Min, row.Max);
                        if (!Mathf.Approximately(control.Slider.Slider.value, display))
                        {
                            control.Slider.SetValue(display);
                            touched++;
                        }
                    }

                    if (control.Choice != null && control.Choice.Index != row.ChoiceIndex)
                    {
                        control.Choice.SetIndex(row.ChoiceIndex);
                        touched++;
                    }

                    // Not while its confirmation is open: the value is still the old one, and this
                    // would untick the box the player has just ticked.
                    if (control.Toggle != null && row.Bool != null && !row.Pending
                        && control.Toggle.Check.isChecked != row.Bool.value)
                    {
                        control.Toggle.SetValue(row.Bool.value);
                        touched++;
                    }

                    // It keeps its own guard: its swatch fires events when set.
                    if (control.Colour != null)
                        control.Colour.Refresh();

                    // A key reset by the options page or a reset all.
                    if (control.Key != null && row.Key != null)
                    {
                        string shown = row.Key.ToLocalizedString("KEYNAME");
                        if (shown != control.KeyShown && !control.Key.Button.hasFocus)
                        {
                            control.KeyShown = shown;
                            control.Key.Button.text = shown;
                        }
                    }

                    if (control.Reset != null)
                        Builder.SetResetEnabled(control.Reset, ResetWanted(row));
                }
            }
            finally
            {
                _refreshing = false;
            }

            RefreshEnabled();
            RefreshShown();
            return touched;
        }

        /// <summary>
        /// A row's reset icon is lit while the row is off where it goes back to, and not greyed out:
        /// off the picked profile's value or the default (the panel), or with a value of the asset's
        /// own (the building window).
        /// </summary>
        private static bool ResetWanted(Row row)
        {
            if (!row.IsEnabled)
                return false;

            return row.ClearOverride != null
                ? row.Overridden != null && row.Overridden()
                : !row.IsAtResetValue;
        }

        /// <summary>Greys the rows whose switch is off, e.g. every glass row while the glass is off.</summary>
        private void RefreshEnabled()
        {
            foreach (Control control in _controls)
            {
                bool wanted = control.Source.IsEnabled;
                if (wanted != control.Enabled)
                {
                    control.Enabled = wanted;

                    foreach (UIComponent part in control.Parts)
                    {
                        part.isEnabled = wanted;
                        part.opacity = wanted ? 1f : 0.45f;
                    }
                }

                // The building window: the row's name in colour while the asset has a value of its
                // own there.
                if (control.Source.Overridden == null || control.Name == null)
                    continue;

                bool own = wanted && control.Source.Overridden();
                if (control.ShownOwn == own)
                    continue;

                control.ShownOwn = own;
                control.Name.textColor = own ? OwnValueColour : _ui.Theme.Text;
            }
        }

        private Control Build(UIPanel page, Row row, float y)
        {
            Control control = BuildControl(page, row, y);

            // Over its name and its control alike; a part with a hover text of its own keeps it.
            string tooltip = row.Tooltip;
            if (tooltip != null)
            {
                foreach (UIComponent part in control.Parts)
                {
                    if (string.IsNullOrEmpty(part.tooltip))
                        part.tooltip = tooltip;
                }
            }

            // The row's reset icon. A colour row has its own (it logs the pick its own way).
            if (row.CanReset && row.Kind != RowKind.Colour && row.Kind != RowKind.Status && row.Kind != RowKind.Button)
            {
                control.Reset = _ui.AddReset(page, y, row.ResetHint);
                if (control.Reset != null)
                {
                    Row captured = row;
                    control.Reset.eventClick += (component, e) => Reset(captured);
                    Builder.SetResetEnabled(control.Reset, ResetWanted(row));
                }
            }

            return control;
        }

        /// <summary>A reset icon clicked: the default back (the panel), or the F8 panel's setting (the building window).</summary>
        private static void Reset(Row row)
        {
            if (row.ClearOverride == null)
            {
                SettingsCatalog.ResetRow(row);
                return;
            }

            if (row.Overridden == null || !row.Overridden())
                return;

            row.ClearOverride();
            SettingsCatalog.RefreshAllUIs();
        }

        private Control BuildControl(UIPanel page, Row row, float y)
        {
            Control control = new Control { Source = row };

            switch (row.Kind)
            {
                case RowKind.Status:
                    control.Status = _ui.AddStatus(page, y, row.StatusText != null ? row.StatusText() : string.Empty);
                    break;

                case RowKind.Button:
                {
                    UIButton button = _ui.AddButton(page, row.Label, new Vector2(page.width, 26f), new Vector3(0f, y + 4f));
                    Row captured = row;
                    button.eventClick += (component, e) =>
                    {
                        if (captured.OnClick == null)
                            return;

                        captured.OnClick();
                        SettingsCatalog.RefreshAllUIs();
                    };
                    control.Parts.Add(button);
                    break;
                }

                case RowKind.Toggle:
                {
                    Row captured = row;
                    ToggleRow toggle = _ui.AddToggle(page, y, row.Label, row.Bool != null && row.Bool.value, isChecked =>
                    {
                        if (_refreshing)
                            return;

                        SettingsCatalog.ApplyToggle(captured, isChecked);
                    });
                    control.Toggle = toggle;
                    control.Name = toggle.Name;
                    control.Parts.AddRange(toggle.Parts);
                    break;
                }

                case RowKind.Key:
                {
                    KeyRow key = _ui.AddKeyBinding(page, y, row.Label, row.Key, Localization.Get("Key.Press"), SettingsXml.SaveNow);
                    control.Key = key;
                    control.KeyShown = key.Button.text;
                    control.Parts.AddRange(key.Parts);
                    break;
                }

                case RowKind.Colour:
                {
                    control.Colour = new ColourRow(_ui, page, y, row, _layout);
                    control.Name = control.Colour.NameLabel;
                    control.Parts.AddRange(control.Colour.Parts);
                    break;
                }

                case RowKind.Choice:
                {
                    // Vanilla: a slider over the choices, the choice's name as its value. The new
                    // looks: a row of buttons, or a dropdown when the names don't fit (measured, so
                    // it follows the language). Either way the row hands back an index, and what is
                    // stored is that choice's VALUE (choice rows are stored by value).
                    Row captured = row;
                    ChoiceRow choice = _ui.AddChoice(page, y, row.Label, row.Choices, row.ChoiceIndex, index =>
                    {
                        if (_refreshing || captured.Int == null || captured.ChoiceValues == null)
                            return;

                        index = Mathf.Clamp(index, 0, captured.ChoiceValues.Length - 1);
                        if (captured.Int.value == captured.ChoiceValues[index])
                            return;

                        captured.Int.value = captured.ChoiceValues[index];
                        Changed(captured);
                    });
                    control.Choice = choice;
                    control.Name = choice.Name;
                    control.Parts.AddRange(choice.Parts);
                    break;
                }

                default:
                {
                    Row captured = row;
                    SliderRow slider = _ui.AddSlider(page, y, row.Label, row.Min, row.Max, row.Step,
                        Mathf.Clamp(row.Display, row.Min, row.Max),
                        row.FormatDisplay,
                        v =>
                        {
                            if (_refreshing)
                                return;

                            captured.Store(v);
                            Changed(captured);
                        });
                    control.Slider = slider;
                    control.Name = slider.Name;
                    control.Parts.AddRange(slider.Parts);
                    break;
                }
            }

            return control;
        }

        /// <summary>
        /// After a slider stored its value. A row with a live-apply hook may have changed OTHER rows
        /// (the glass preset writes three of them; moving one of those three flips the preset to
        /// Custom), so every UI looks again. Rows without one change nothing but themselves, and are
        /// left alone -- this runs on every tick of a drag.
        /// </summary>
        private static void Changed(Row row)
        {
            if (row.AfterChange == null)
                return;

            row.AfterChange();
            SettingsCatalog.RefreshAllUIs();
        }
    }
}
