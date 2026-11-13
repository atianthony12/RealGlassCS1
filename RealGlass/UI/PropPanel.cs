using System.Collections.Generic;
using ColossalFramework.UI;
using ModsLibCS1.UI;
using RealGlass.PerAsset;
using UnityEngine;

namespace RealGlass.UI
{
    /// <summary>
    /// The prop window (2.0.0): glass or water on props, with the building window's glass rows for
    /// each. Opened by the building picker (Alt+G) when a prop standing on its own is clicked -- that
    /// prop's rows -- or by the building window's Props button: a list of every prop the building
    /// carries (the see-through ones first), a click choosing whose rows show below it
    /// (<see cref="PropSelection"/>, <see cref="PropCatalog"/>). A choice is the prop asset's, on every
    /// copy of it, in every city (RealGlassAssets.xml).
    /// </summary>
    /// <remarks>
    /// The list is built anew when it changes (the props' names are its buttons' texts); the rows once
    /// (the catalog's, re-read from the selected prop). The list scrolls past <see cref="MostListRows"/>.
    /// A prop the game bends over the ground or the water as it draws it (decals and the like) can be
    /// listed, but its rows stay grey: nothing of ours can go over it.
    /// </remarks>
    public class PropPanel : UIPanel
    {
        private const float PanelWidth = 520f;
        private const float TitleBarHeight = 40f;
        private const float StatusHeight = 44f;
        private const float ButtonsHeight = 34f;
        private const float Margin = 14f;
        private const float ListRowHeight = 30f;
        private const int MostListRows = 6;
        private const float ListGap = 10f;
        private const float RefreshInterval = 0.25f;
        private const float MinTitleShare = 0.7f;

        public Vector3? InitialPosition;

        private Builder _ui;
        private Frame _frame;
        private UILabel _status;
        private UIButton _pick;
        private ScrollView _list;
        private UIPanel _listPage;
        private UIPanel _rowsPage;
        private RowList _rows;
        private float _listTop, _rowsHeight;
        private readonly List<KeyValuePair<PropInfo, UIButton>> _items = new List<KeyValuePair<PropInfo, UIButton>>();
        private int _builtVersion = -1;
        private string _titleShown;
        private float _nextRefreshTime;

        private static PropPanel _instance;

        /// <summary>Re-reads every control, if the window exists.</summary>
        public static void RefreshOpenPanel()
        {
            if (_instance != null)
                _instance.RefreshValues();
        }

        public override void Start()
        {
            base.Start();
            _instance = this;

            _ui = PanelLook.NewBuilder();
            _rows = new RowList(_ui, RowLayout.Compact, false);

            size = new Vector2(PanelWidth, 200f);
            canFocus = true;
            isInteractive = true;
            _frame = _ui.AddFrame(this, Mod.DisplayName, Hide);

            float inner = PanelWidth - 2f * Margin;
            float y = TitleBarHeight;
            _status = _ui.AddLabel(this, string.Empty, new Vector3(Margin, y + 2f), _ui.Theme.TextScale);
            _status.autoSize = false;
            _status.wordWrap = true;
            _status.size = new Vector2(inner, StatusHeight - 4f);
            _ui.Paint(_status, _ui.Theme.Status);
            y += StatusHeight;

            _pick = _ui.AddButton(this, Localization.Get("Props.Pick"), new Vector2(inner, 26f), new Vector3(Margin, y + 4f));
            _pick.eventClick += (component, e) => AssetPicker.Toggle();
            y += ButtonsHeight;

            _listTop = y;
            _list = _ui.AddScrollView(this, new Vector3(Margin, y), new Vector2(inner, MostListRows * ListRowHeight), PanelWidth - Margin + 4f);

            _rowsPage = AddUIComponent<UIPanel>();
            _rowsPage.size = new Vector2(inner, 10f);
            _rowsPage.relativePosition = new Vector3(Margin, y);
            _rowsHeight = _rows.Build(_rowsPage, PropCatalog.Instance.Rows, "prop window");
            _rowsPage.height = _rowsHeight;

            // Each surface shows only its own rows: the window follows their height.
            _rows.PageResized = (page, content, revealTop, revealBottom) =>
            {
                _rowsHeight = content;
                _rowsPage.height = content;
                height = _rowsPage.relativePosition.y + _rowsHeight + Margin;
            };

            if (InitialPosition.HasValue)
                relativePosition = InitialPosition.Value;
            else
                PlaceLeftOfCentre();

            eventVisibilityChanged += (component, visible) =>
            {
                if (visible)
                    RefreshValues();
            };

            RebuildList();
            Log.Msg("prop window built: " + _rows.Count + " controls (" + _ui.Skin + ")");
        }

        public override void Update()
        {
            base.Update();
            if (!isVisible || Time.time < _nextRefreshTime)
                return;

            _nextRefreshTime = Time.time + RefreshInterval;
            RefreshValues();
        }

        /// <summary>
        /// A button for each prop of a building's list (none for a prop picked on its own), the rows
        /// under it, the window as tall as both.
        /// </summary>
        private void RebuildList()
        {
            _builtVersion = PropSelection.ListVersion;
            _items.Clear();
            if (_listPage != null)
                Destroy(_listPage.gameObject);
            _listPage = _list.AddPage();

            bool listed = PropSelection.Building != null;
            float y = 0f;
            if (listed)
            {
                foreach (PropInfo info in PropSelection.Props)
                {
                    PropInfo prop = info;
                    UIButton button = _ui.AddButton(_listPage, ItemText(prop), new Vector2(_listPage.width, ListRowHeight - 4f), new Vector3(0f, y));
                    button.textHorizontalAlignment = UIHorizontalAlignment.Left;
                    button.textPadding = new RectOffset(8, 8, 0, 0);
                    button.eventClick += (component, e) =>
                    {
                        PropSelection.Select(prop);
                        PropCatalog.Instance.Sync();
                        RefreshValues();
                    };
                    _items.Add(new KeyValuePair<PropInfo, UIButton>(prop, button));
                    y += ListRowHeight;
                }
            }

            float listHeight = listed ? Mathf.Min(y, MostListRows * ListRowHeight) : 0f;
            _list.Viewport.isVisible = listed && y > 0f;
            _list.Resize(Mathf.Max(listHeight, 1f));
            _list.Show(_listPage, y);
            float rowsTop = _listTop + (listHeight > 0f ? listHeight + ListGap : 0f);
            _rowsPage.relativePosition = new Vector3(Margin, rowsTop);
            height = rowsTop + _rowsHeight + Margin;
            PropCatalog.Instance.Sync();
        }

        private static string ItemText(PropInfo info)
        {
            int surface = PropSelection.SurfaceOf(info);
            string state = surface == PropRecord.Glass ? Localization.Get("Props.Glass")
                : (surface == PropRecord.Water ? Localization.Get("Props.Water") : Localization.Get("Props.Off"));
            return PropSelection.TitleOf(info) + ": " + state;
        }

        /// <summary>Everything back to the props (the file can change under the window), the list rebuilt when it changed.</summary>
        public void RefreshValues()
        {
            if (_rows == null)
                return;
            if (_builtVersion != PropSelection.ListVersion)
                RebuildList();

            PropCatalog.Instance.Sync();
            PropInfo selected = PropSelection.Selected;
            ShowTitle(PropSelection.Building != null ? PropSelection.Title
                : (selected != null ? PropSelection.TitleOf(selected) : Mod.DisplayName));
            if (_status != null)
                _status.text = StatusText();
            if (_pick != null)
            {
                bool can = AssetPicker.Instance != null;
                _pick.isEnabled = can;
                _pick.opacity = can ? 1f : 0.45f;
            }

            foreach (KeyValuePair<PropInfo, UIButton> item in _items)
            {
                string text = ItemText(item.Key);
                bool chosen = item.Key == selected;
                if (chosen)
                    text = "> " + text;
                if (item.Value.text != text)
                    item.Value.text = text;
            }

            _rows.RefreshValues();
        }

        private static string StatusText()
        {
            string key = Settings.PickerKey != null ? Settings.PickerKey.ToLocalizedString("KEYNAME") : "Alt+G";
            if (PropSelection.Building != null && !PropSelection.HasSelection)
                return Localization.Get("Props.NoneInBuilding");
            if (PropSelection.Building != null && PropSelection.Selected == null)
                return string.Empty;   // a building's list, none clicked yet: the list says it all
            if (PropSelection.Selected == null)
                return Localization.Get("Props.None", key);

            // A building's props: no line of its own (the author's call, 2026-11-08).
            string status = !PropSelection.CanDraw(PropSelection.Selected) ? Localization.Get("Props.CannotDraw")
                : (PropSelection.Building != null ? string.Empty : Localization.Get("Props.Status"));
            if (Settings.GlassEnabled != null && !Settings.GlassEnabled.value)
                status += (status.Length > 0 ? "\n" : string.Empty) + Localization.Get("Props.GlassOff");
            return status;
        }

        private void ShowTitle(string text)
        {
            if (_frame == null || _frame.Title == null || text == _titleShown)
                return;

            _titleShown = text;
            UILabel title = _frame.Title;
            string shown = _ui.Caps(text) ?? string.Empty;
            float full = _ui.Theme.TitleScale;
            float wide = _ui.Measure(shown, full) + title.characterSpacing * shown.Length;
            float room = title.width - 4f;
            title.textScale = wide > room && wide > 0f ? full * Mathf.Max(MinTitleShare, room / wide) : full;
            title.text = shown;
        }

        /// <summary>Left of the screen's centre: the building window opens right of it.</summary>
        private void PlaceLeftOfCentre()
        {
            UIView view = GetUIView();
            relativePosition = new Vector3(40f, Mathf.Floor(Mathf.Max(0f, (view.fixedHeight - height) / 2f - 120f)));
        }

        public override void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
            base.OnDestroy();
        }
    }
}
