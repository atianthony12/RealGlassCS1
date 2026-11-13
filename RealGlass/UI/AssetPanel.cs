using ColossalFramework.UI;
using ModsLibCS1.UI;
using RealGlass.PerAsset;
using UnityEngine;

namespace RealGlass.UI
{
    /// <summary>
    /// The building window: one building asset's own glass, on every copy of it, in every city
    /// (<see cref="AssetSelection"/>, <see cref="AssetCatalog"/>). Opened by the building picker
    /// (<see cref="AssetPicker"/>, Alt+G) when a building is clicked; a window of its own, apart from
    /// the F8 panel (the author, 2026-10-04: "it should open a new small standalone window").
    /// </summary>
    /// <remarks>
    /// The title bar names the asset; under it "Pick another building" and "Restore settings" (the
    /// status lines that stood above them were taken out, the author's call, 2026-11-13), then the rows,
    /// each ending in a reset icon that puts that one back to the F8 panel's setting. Narrower than
    /// the panel (<see cref="RowLayout.Compact"/>), and as tall as its rows. Drawn with ModsLibCS1 in
    /// the panels' look (<see cref="PanelLook"/>).
    /// </remarks>
    public class AssetPanel : UIPanel
    {
        private const float PanelWidth = 520f;
        private const float TitleBarHeight = 40f;
        private const float ButtonsHeight = 34f;
        private const float Margin = 14f;

        /// <summary>The smallest an asset's long name is drawn in the title bar, as a share of the title's size.</summary>
        private const float MinTitleShare = 0.7f;

        private const float RefreshInterval = 0.25f;

        /// <summary>Where to open, when replacing a window that was already on screen. Null = right of the centre.</summary>
        public Vector3? InitialPosition;

        private Builder _ui;
        private Frame _frame;
        private RowList _rows;
        private UIButton _pick;
        private UIButton _useGlobal;
        private UIButton _props;
        private BuildingInfo _propsCountedFor;
        private int _propsCount;
        private string _titleShown;
        private float _nextRefreshTime;

        private static AssetPanel _instance;

        /// <summary>Re-reads every control, if the window exists. Safe to call from anywhere.</summary>
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

            // Background, title, close button; dragging the title bar moves the whole window.
            _frame = _ui.AddFrame(this, Mod.DisplayName, Hide);

            float inner = PanelWidth - 2f * Margin;
            float y = TitleBarHeight + 6f;

            float half = (inner - 6f) / 2f;
            _pick = _ui.AddButton(this, Localization.Get("Asset.Pick"), new Vector2(half, 26f), new Vector3(Margin, y + 4f));
            _pick.eventClick += (component, e) => AssetPicker.Toggle();
            _useGlobal = _ui.AddButton(this, Localization.Get("Asset.UseGlobal"), new Vector2(half, 26f),
                new Vector3(Margin + half + 6f, y + 4f));
            _useGlobal.eventClick += (component, e) =>
            {
                AssetSelection.UseGlobal();
                AssetCatalog.Picked.Sync();
                SettingsCatalog.RefreshAllUIs();
            };
            y += ButtonsHeight;

            // Its props, glass or water each (2.0.0): a building's props are no props of the city's,
            // so they are chosen from a list in the prop window (the author's pick), not pointed at.
            _props = _ui.AddButton(this, Localization.Get("Asset.Props", 0), new Vector2(inner, 26f), new Vector3(Margin, y + 4f));
            _props.eventClick += (component, e) =>
            {
                if (!AssetSelection.HasSelection)
                    return;
                PropSelection.SelectOf(AssetSelection.Info);
                ModController.ShowPropWindow();
            };
            y += ButtonsHeight;

            UIPanel page = AddUIComponent<UIPanel>();
            page.size = new Vector2(inner, 10f);
            page.relativePosition = new Vector3(Margin, y);
            float content = _rows.Build(page, AssetCatalog.Picked.Rows, "building window");
            page.height = content;
            height = y + content + Margin;

            if (InitialPosition.HasValue)
                relativePosition = InitialPosition.Value;
            else
                PlaceRightOfCentre();

            eventVisibilityChanged += (component, visible) =>
            {
                if (visible)
                    RefreshValues();
            };

            RefreshValues();
            Log.Msg("building window built: " + _rows.Count + " controls (" + _ui.Skin + ")");
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
        /// Everything back to the picked asset: its name, its count, its glass (which can change under
        /// the window: the file, a global setting it follows, another pick).
        /// </summary>
        public void RefreshValues()
        {
            if (_rows == null)
                return;

            AssetCatalog.Picked.Sync();

            ShowTitle(AssetSelection.HasSelection ? AssetSelection.Title : Mod.DisplayName);

            SetEnabled(_pick, AssetPicker.Instance != null);
            SetEnabled(_useGlobal, AssetSelection.HasSelection && AssetSelection.Record != null &&
                                   (Settings.GlassEnabled == null || Settings.GlassEnabled.value));
            if (_props != null)
            {
                if (_propsCountedFor != AssetSelection.Info)
                {
                    _propsCountedFor = AssetSelection.Info;
                    _propsCount = PropSelection.CountOf(AssetSelection.Info);
                    _props.text = Localization.Get("Asset.Props", _propsCount);
                }
                SetEnabled(_props, AssetSelection.HasSelection && _propsCount > 0);
            }

            int touched = _rows.RefreshValues();
            if (touched > 0 && Log.Detailed)
                Log.Detail("building window: refreshed " + _rows.Count + " controls, " + touched + " had changed");
        }

        /// <summary>
        /// The asset's name in the title bar, as the look writes it (capitals in Frosted); a long one
        /// drawn smaller until it fits (down to <see cref="MinTitleShare"/>), then cut off by the bar.
        /// </summary>
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

        private static void SetEnabled(UIComponent part, bool wanted)
        {
            if (part == null || part.isEnabled == wanted)
                return;

            part.isEnabled = wanted;
            part.opacity = wanted ? 1f : 0.45f;
        }

        /// <summary>Right of the screen's centre, where the F8 panel (centred) leaves room for it.</summary>
        private void PlaceRightOfCentre()
        {
            UIView view = GetUIView();
            relativePosition = new Vector3(
                Mathf.Floor(Mathf.Max(0f, view.fixedWidth - PanelWidth - 40f)),
                Mathf.Floor(Mathf.Max(0f, (view.fixedHeight - height) / 2f)));
        }

        public override void OnDestroy()
        {
            if (_instance == this)
                _instance = null;

            base.OnDestroy();
        }
    }
}
