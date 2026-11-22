using System.Collections.Generic;
using ColossalFramework.UI;
using ModsLibCS1.UI;
using UnityEngine;

namespace RealGlass.UI
{
    /// <summary>
    /// The asset windows' two tabs (2026-11-18, the author's call: the building window's rooms in a tab
    /// of their own): Glass, every row the asset catalog has but the rooms', and Interiors, the rows it
    /// marks <see cref="PanelPage.Interiors"/>. The building window and the creator window each have one;
    /// the window takes the open tab's height. The tab last opened is opened again in the next window.
    /// </summary>
    internal sealed class AssetTabs
    {
        private const float TabHeight = 28f;
        private const float IconTabHeight = 36f;
        private const float Gap = 6f;

        private static readonly PanelPage[] Pages = { PanelPage.Glass, PanelPage.Interiors };
        private static readonly string[] Icons = { "building-2", "sofa" };

        private static int _remembered;

        private readonly Builder _ui;
        private UIPanel _window;
        private Frame _frame;
        private TabStrip _strip;
        private readonly List<UIPanel> _pages = new List<UIPanel>();
        private readonly List<float> _heights = new List<float>();
        private readonly List<string> _names = new List<string>();
        private float _top, _margin;

        public AssetTabs(Builder ui)
        {
            _ui = ui;
        }

        /// <summary>
        /// The strip at <paramref name="y"/> in <paramref name="window"/>, then one page a tab under it,
        /// the rows built by <paramref name="rows"/>. Sets the window's height to the open tab's.
        /// frame: the window's frame, whose subtitle names the open tab (null: none).
        /// </summary>
        public void Build(UIPanel window, Frame frame, RowList rows, IList<Row> all, float y, float margin, float width, string what)
        {
            _window = window;
            _frame = frame;
            _margin = margin;
            float tabHeight = _ui.Skin == Skin.Vanilla ? TabHeight : IconTabHeight;

            foreach (PanelPage page in Pages)
                _names.Add(SettingsCatalog.TabName(page));
            _strip = _ui.AddTabStrip(window, new Vector3(margin, y), width, tabHeight, _names, Select, Icons);
            _top = y + tabHeight + Gap;

            foreach (PanelPage page in Pages)
            {
                UIPanel panel = window.AddUIComponent<UIPanel>();
                panel.size = new Vector2(width, 10f);
                panel.relativePosition = new Vector3(margin, _top);
                float content = rows.Build(panel, On(all, page), what + ", " + page + " tab");
                panel.height = content;
                _pages.Add(panel);
                _heights.Add(content);
            }

            Select(Mathf.Clamp(_remembered, 0, _pages.Count - 1));
        }

        private static IEnumerable<Row> On(IList<Row> all, PanelPage page)
        {
            foreach (Row row in all)
            {
                bool interiors = row.Panel == PanelPage.Interiors;
                if (interiors == (page == PanelPage.Interiors))
                    yield return row;
            }
        }

        private void Select(int index)
        {
            _remembered = index;
            for (int i = 0; i < _pages.Count; i++)
                _pages[i].isVisible = i == index;
            _strip.Select(index);
            if (_frame != null)
                _ui.SetSubtitle(_frame, _names[index]);
            _window.height = _top + _heights[index] + _margin;
        }
    }
}
