using System.Collections.Generic;
using ColossalFramework.UI;
using ModsLibCS1.UI;
using UnityEngine;

namespace RealGlass.UI
{
    /// <summary>
    /// The in-game panel (F8): every setting of the look, judged against the city.
    /// </summary>
    /// <remarks>
    /// Five tabs, one per <see cref="PanelPage"/>:
    ///   Glass       -- the glass itself: its kind, colour, how much it reflects and lets through,
    ///                  and what counts as glass. A status line at the top says what is drawn.
    ///   Reflections -- what the glass mirrors: the finished picture, the sky and its clouds, the sun.
    ///   Panes       -- the panes: their size, and how they bend the reflections.
    ///   Interiors   -- the rooms behind the windows: their lights at night, their curtains.
    ///   Water       -- the map's water: its brightness, the sky and the city in it.
    /// The mod itself (language, keys, button, the panels' look, what the glass may cost,
    /// diagnostics, reset) is on the options page (<see cref="OptionsUI"/>) alone: Row.Options ==
    /// General. One building asset's own glass has a window of its own (<see cref="AssetPanel"/>),
    /// opened by the building picker.
    ///
    /// Both UIs are drawn from <see cref="SettingsCatalog"/>, so where a row lives is a one-word
    /// edit rather than a second copy of the row. (Volumetric Weather's CloudsPanel, with these
    /// tabs.)
    ///
    /// Every control comes from ModsLibCS1, in the look the "Panel look" row picks
    /// (<see cref="PanelLook"/>); Vanilla is the panel as it was before. In the new looks the tabs are
    /// icons. Every look grows with the tab shown and as its groups open or fold, up to the screen's
    /// height, and only a taller tab scrolls (ModsLibCS1's ScrollView; 1.1.1: 1.1's Water tab ran off
    /// a 1080 canvas). Until 2026-11-08 the new looks kept ONE height for every tab, the Glass tab's as
    /// it opened -- 304 px in the author's game, every other tab scrolling: "kind of small, relative to
    /// VW". Volumetric Weather's panel grows the same way.
    ///
    /// The Reflections and Panes tabs show only with "Show advanced options in the in-game panel" on
    /// the options page (Volumetric Weather's row; the author's call, 2026-11-08). Their settings apply
    /// either way.
    ///
    /// Each tab's headings fold (Volumetric Weather's accordions, 2026-10-21; <see cref="RowList"/>):
    /// a tab opens with its first group open, a click on a heading opens or folds its group, and the
    /// page closes up or scrolls the group into view.
    /// </remarks>
    public class GlassPanel : UIPanel
    {
        private const float VanillaWidth = 620f;

        /// <summary>
        /// The new looks' width: the icon tabs need no room, the names keep their full size and the
        /// sliders give way (Volumetric Weather's, the author: "decrease the width ... the sliders
        /// themselves can be smaller"). 570 until 2026-11-08: the Interiors tab's names (279 px) left
        /// its sliders squeezed.
        /// </summary>
        private const float NewLooksWidth = 620f;
        private const float PanelHeight = 408f;
        private const float TitleBarHeight = 40f;
        private const float TabHeight = 28f;

        /// <summary>The new looks' icon tabs: taller, for the bigger icons (Volumetric Weather's).</summary>
        private const float IconTabHeight = 36f;
        private const float Margin = 14f;

        /// <summary>Kept free above and below the panel when a tab is taller than the screen.</summary>
        private const float ScreenRoom = 20f;

        /// <summary>Every tab, in order; the advanced ones only with the advanced options on.</summary>
        private static readonly PanelPage[] AllTabs =
        {
            PanelPage.Glass, PanelPage.Reflections, PanelPage.Panes, PanelPage.Interiors, PanelPage.Water, PanelPage.Vehicles,
        };

        private static bool IsAdvanced(PanelPage page)
        {
            return page == PanelPage.Reflections || page == PanelPage.Panes;
        }

        /// <summary>The tabs this panel shows (<see cref="AllTabs"/>, the advanced ones left out unless asked for).</summary>
        private readonly List<PanelPage> Tabs = new List<PanelPage>();

        /// <summary>
        /// A tab's icon in the new looks: a Lucide name the library draws (THIRD-PARTY-NOTICES.md).
        /// Vanilla keeps the names, and so does a look whose sheet lacks one of the icons.
        /// </summary>
        private static string IconOf(PanelPage page)
        {
            switch (page)
            {
                case PanelPage.Glass: return "building-2";
                case PanelPage.Reflections: return "sparkles";
                case PanelPage.Panes: return "grid-2x2";
                case PanelPage.Interiors: return "sofa";
                case PanelPage.Water: return "waves";
                case PanelPage.Vehicles: return "car";
                default: return null;
            }
        }

        /// <summary>Where to open, when replacing a panel that was already on screen. Null = centred.</summary>
        public Vector3? InitialPosition;

        private Builder _ui;
        private Frame _frame;
        private TabStrip _tabs;
        private RowList _rows;

        /// <summary>The window the pages scroll in: the new looks' one height, or Vanilla's as tall as each tab.</summary>
        private ScrollView _scroll;

        /// <summary>The tallest the window may be: the screen's room, and in the new looks <see cref="ScrollHeight"/>.</summary>
        private float _mostWindow;

        private bool _vanilla;

        private readonly List<string> _tabNames = new List<string>();
        private readonly List<UIPanel> _pages = new List<UIPanel>();
        private readonly List<float> _contentHeights = new List<float>();
        private float _width = VanillaWidth;
        private float _tabHeight = TabHeight;
        private int _selected;

        private const float RefreshInterval = 0.25f;
        private float _nextRefreshTime;

        private static GlassPanel _instance;

        private float MinContentHeight
        {
            get { return PanelHeight - TitleBarHeight - _tabHeight - 2f * Margin; }
        }

        /// <summary>Re-reads every control, if the panel exists. Safe to call from anywhere.</summary>
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
            _vanilla = _ui.Skin == Skin.Vanilla;
            _tabHeight = _vanilla ? TabHeight : IconTabHeight;
            _width = _vanilla ? VanillaWidth : NewLooksWidth;
            _rows = new RowList(_ui, RowLayout.Panel, true);
            _rows.PageResized = PageResized;

            size = new Vector2(_width, PanelHeight);
            canFocus = true;
            isInteractive = true;

            // Background, title, close button; dragging the title bar moves the whole panel.
            _frame = _ui.AddFrame(this, Mod.DisplayName, Hide);

            bool advanced = Settings.ShowAdvancedInPanel != null && Settings.ShowAdvancedInPanel.value;
            Tabs.Clear();
            foreach (PanelPage page in AllTabs)
            {
                if (advanced || !IsAdvanced(page))
                    Tabs.Add(page);
            }

            List<string> icons = new List<string>();
            foreach (PanelPage page in Tabs)
            {
                _tabNames.Add(SettingsCatalog.TabName(page));
                icons.Add(IconOf(page));
            }

            // The new looks draw each tab as its icon, its name the tooltip and, for the open one,
            // beside the panel's title (SelectPage). Vanilla: named tabs, as before.
            _tabs = _ui.AddTabStrip(this, new Vector3(Margin, TitleBarHeight + 2f), _width - 2f * Margin,
                _tabHeight, _tabNames, SelectPage, icons);
            FitTabs();

            BuildScrollView();

            foreach (PanelPage page in Tabs)
                _contentHeights.Add(_rows.Build(AddPage(), RowsOn(page), page + " tab"));

            FitWindow();
            SelectPage(0);

            if (InitialPosition.HasValue)
                relativePosition = InitialPosition.Value;
            else
                CenterOnScreen();

            KeepOnScreen();

            eventVisibilityChanged += (component, visible) =>
            {
                if (visible)
                    RefreshValues();
            };

            Log.Msg("panel built: " + _rows.Count + " controls over " + _pages.Count + " tabs (" + _ui.Skin +
                    (advanced ? ", advanced" : ", basic") + ")");
        }

        /// <summary>
        /// A tab name wider than its tab is drawn smaller until it fits (Volumetric Weather's: a
        /// translation, or capitals where the icons are missing): a button clips what runs past its edge.
        /// </summary>
        private void FitTabs()
        {
            foreach (UIButton tab in _tabs.Tabs)
            {
                if (string.IsNullOrEmpty(tab.text))
                    continue;

                float room = tab.width - 8f;
                float wide = _ui.Measure(tab.text, tab.textScale) + tab.characterSpacing * tab.text.Length;
                if (wide > room && wide > 0f)
                {
                    float scale = tab.textScale * room / wide;
                    Log.Msg("panel: tab '" + tab.text + "' drawn at " + (100f * scale / tab.textScale).ToString("F0") +
                            "% to fit its " + tab.width.ToString("F0") + " px");
                    tab.textScale = scale;
                }
            }
        }

        private static IEnumerable<Row> RowsOn(PanelPage page)
        {
            foreach (Row row in SettingsCatalog.Rows)
            {
                if (row.Panel == page)
                    yield return row;
            }
        }

        /// <summary>
        /// Keeps the status line, the greyed-out rows and the values current. All three depend on
        /// things the panel does not own: the renderer, checkboxes on another tab, and the options
        /// page, which can change a setting while this panel stays open behind it (so no visibility
        /// event ever says to look again).
        /// </summary>
        public override void Update()
        {
            base.Update();

            if (!isVisible || Time.time < _nextRefreshTime)
                return;

            _nextRefreshTime = Time.time + RefreshInterval;
            _rows.UpdateStatus();
            RefreshValues();
        }

        /// <summary>Re-reads every control from its setting.</summary>
        public void RefreshValues()
        {
            if (_rows == null)
                return;

            int touched = _rows.RefreshValues();
            if (touched > 0 && Log.Detailed)
                Log.Detail("panel: refreshed " + _rows.Count + " controls, " + touched + " had changed");
        }

        /// <summary>
        /// The window the pages scroll in: as wide as the pages, its bar in the right margin, at first
        /// as high as the screen allows -- the most it gets; <see cref="FitToPage"/> sets its height to
        /// each tab's.
        /// </summary>
        private void BuildScrollView()
        {
            float top = TitleBarHeight + _tabHeight + Margin;
            UIView view = GetUIView();
            float window = Mathf.Floor((view != null ? view.fixedHeight : 1080f) - 2f * ScreenRoom - top - Margin);
            window = Mathf.Max(window, MinContentHeight);
            _mostWindow = window;

            height = top + window + Margin;
            _scroll = _ui.AddScrollView(this, new Vector3(Margin, top), new Vector2(_width - 2f * Margin, window),
                _width - Margin + 4f);
        }

        /// <summary>
        /// For the log: each tab as tall as its rows (<see cref="FitToPage"/>), up to the screen; which
        /// ones scroll, as they open.
        /// </summary>
        private void FitWindow()
        {
            if (_contentHeights.Count == 0)
                return;

            var heights = new List<string>();
            for (int i = 0; i < _contentHeights.Count; i++)
                heights.Add(Tabs[i] + " " + _contentHeights[i].ToString("F0") + (_contentHeights[i] > _mostWindow + 0.5f ? " (scrolls)" : ""));
            Log.Msg("panel: each tab as tall as its rows, at most " + _mostWindow.ToString("F0") + " (the screen): " +
                    string.Join(", ", heights.ToArray()) + (_scroll.Bar == null ? " (no bar: the library's sheet is missing)" : ""));
        }

        private UIPanel AddPage()
        {
            UIPanel page = _scroll.AddPage();
            _pages.Add(page);
            return page;
        }

        private void SelectPage(int index)
        {
            _selected = index;
            for (int i = 0; i < _pages.Count; i++)
                _pages[i].isVisible = i == index;

            _tabs.Select(index);
            _ui.SetSubtitle(_frame, _tabNames[index]);

            // The window takes the tab's height; what the screen can't hold scrolls.
            FitToPage(index);
            _pages[index].height = Mathf.Max(_contentHeights[index], _scroll.Viewport.height);
            _scroll.Show(_pages[index], _contentHeights[index]);

            KeepOnScreen();
        }

        /// <summary>
        /// The window as tall as the selected page needs, never shorter than it started, never taller
        /// than the screen allows (<see cref="_mostWindow"/>): the rest of a taller tab scrolls.
        /// </summary>
        private void FitToPage(int index)
        {
            float window = Mathf.Min(Mathf.Max(MinContentHeight, Mathf.Ceil(_contentHeights[index])), _mostWindow);
            _scroll.Resize(window);
            height = TitleBarHeight + _tabHeight + 2f * Margin + window;
        }

        /// <summary>
        /// A page's rows were folded, opened, shown or hidden (<see cref="RowList.PageResized"/>). The
        /// open page: the panel grows or shrinks with it, up to the screen, its scrolling follows, and a
        /// group just opened is scrolled into view.
        /// </summary>
        private void PageResized(int index, float content, float revealTop, float revealBottom)
        {
            if (index < 0 || index >= _contentHeights.Count)
                return;

            _contentHeights[index] = content;
            if (index != _selected)
                return;

            FitToPage(index);
            KeepOnScreen();
            _pages[index].height = Mathf.Max(content, _scroll.Viewport.height);
            _scroll.SetHeight(content);
            if (revealTop >= 0f)
                _scroll.Reveal(revealTop, revealBottom);
        }

        private void CenterOnScreen()
        {
            UIView view = GetUIView();
            relativePosition = new Vector3(
                Mathf.Floor((view.fixedWidth - _width) / 2f),
                Mathf.Floor((view.fixedHeight - height) / 2f));
            KeepOnScreen();
        }

        /// <summary>
        /// The whole panel on the screen: a taller tab (Water, 21 rows: 906 of the canvas's 1080 in
        /// Vanilla) opened on a panel centred for a shorter one ran off the bottom, its last rows out
        /// of reach until the panel was dragged up (audit, 2026-10-11).
        /// </summary>
        private void KeepOnScreen()
        {
            UIView view = GetUIView();
            if (view == null)
                return;

            Vector3 at = relativePosition;
            at.x = Mathf.Max(0f, Mathf.Min(at.x, view.fixedWidth - width));
            at.y = Mathf.Max(0f, Mathf.Min(at.y, view.fixedHeight - height));
            relativePosition = at;
        }

        public override void OnDestroy()
        {
            if (_instance == this)
                _instance = null;

            base.OnDestroy();
        }
    }
}
