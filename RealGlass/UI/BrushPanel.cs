using ColossalFramework.UI;
using ModsLibCS1.UI;
using UnityEngine;

namespace RealGlass.UI
{
    /// <summary>
    /// The shore brush window: open while the Water tab's "Paint shores" is (Water.ShorePaintTool). The
    /// F8 panel steps aside for it and comes back when the brush is put away -- by Done, this window's
    /// close button, Escape or another tool (the author's call, 2026-11-13). It draws the catalog's
    /// <see cref="Row.InBrush"/> rows: the brush's size, strength
    /// and hardness, and clearing the city's painted shores.
    /// </summary>
    /// <remarks>
    /// Built when the brush is taken out, where the F8 panel stood, and destroyed when it is put away
    /// (ModController.ShoreBrushStarted / ShoreBrushEnded): its words are always the current language's.
    /// </remarks>
    public class BrushPanel : UIPanel
    {
        private const float PanelWidth = 400f;
        private const float TitleBarHeight = 40f;
        private const float Margin = 14f;
        private const float ButtonHeight = 26f;
        private const float RefreshInterval = 0.25f;

        /// <summary>Where to open: where the F8 panel stood. Null = a third of the way down, centred.</summary>
        public Vector3? InitialPosition;

        private Builder _ui;
        private RowList _rows;
        private float _nextRefreshTime;

        public override void Start()
        {
            base.Start();

            _ui = PanelLook.NewBuilder();
            _rows = new RowList(_ui, RowLayout.Compact, false);

            size = new Vector2(PanelWidth, 200f);
            canFocus = true;
            isInteractive = true;

            _ui.AddFrame(this, Localization.Get("WaterFoamPaint.Label"), () => Water.ShorePaintTool.TurnOff("its window"));

            float inner = PanelWidth - 2f * Margin;
            float y = TitleBarHeight + 6f;

            UIPanel page = AddUIComponent<UIPanel>();
            page.size = new Vector2(inner, 10f);
            page.relativePosition = new Vector3(Margin, y);
            var rows = new System.Collections.Generic.List<Row>();
            foreach (Row row in SettingsCatalog.Rows)
            {
                if (row.InBrush)
                    rows.Add(row);
            }
            float content = _rows.Build(page, rows, "shore brush window");
            page.height = content;
            y += content + 4f;

            UIButton done = _ui.AddButton(this, Localization.Get("ShorePaint.Done"), new Vector2(inner, ButtonHeight), new Vector3(Margin, y));
            done.eventClick += (component, e) => Water.ShorePaintTool.TurnOff("Done");
            y += ButtonHeight + Margin;

            height = y;
            Place();
            _rows.RefreshValues();
            Log.Msg("shore brush window built: " + _rows.Count + " controls (" + _ui.Skin + ")");
        }

        public override void Update()
        {
            base.Update();

            if (!isVisible || _rows == null || Time.time < _nextRefreshTime)
                return;

            _nextRefreshTime = Time.time + RefreshInterval;
            _rows.RefreshValues();
        }

        /// <summary>Where the F8 panel stood, kept on the screen.</summary>
        private void Place()
        {
            UIView view = GetUIView();
            if (view == null)
                return;

            Vector3 at = InitialPosition ?? new Vector3(Mathf.Floor((view.fixedWidth - width) / 2f), Mathf.Floor(view.fixedHeight / 3f));
            at.x = Mathf.Max(0f, Mathf.Min(at.x, view.fixedWidth - width));
            at.y = Mathf.Max(0f, Mathf.Min(at.y, view.fixedHeight - height));
            relativePosition = at;
        }
    }
}
