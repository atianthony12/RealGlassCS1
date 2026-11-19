using ColossalFramework.IO;
using ColossalFramework.UI;
using ModsLibCS1.UI;
using RealGlass.PerAsset;
using UnityEngine;

namespace RealGlass.UI
{
    /// <summary>
    /// The creator window: the asset editor's own interface (the author, 2026-10-23: "a different
    /// interface just for making assets"), opened by the mod's key and button there in place of the F8
    /// panel. The glass of the building being made, for every player who has the mod
    /// (<see cref="CreatorAsset"/>): saved with the asset in RealGlassDefinition.xml and shared with it.
    /// </summary>
    /// <remarks>
    /// The status line says whether its glass is saved, how many settings it has of its own and what
    /// was found of its windows for the rooms; then "Restore settings" and "Open its folder"; then the
    /// preview's switches and the asset's rows (<see cref="AssetCatalog.Edited"/>), each ending in a
    /// reset icon that leaves that setting to each player. Built as the building window is.
    /// </remarks>
    public class CreatorPanel : UIPanel
    {
        private const float PanelWidth = 520f;
        private const float TitleBarHeight = 40f;
        /// <summary>Three lines: saved or not, its own settings, its rooms (RoomFit).</summary>
        private const float StatusHeight = 60f;
        private const float ButtonsHeight = 34f;
        private const float Margin = 14f;
        private const float RefreshInterval = 0.25f;

        /// <summary>Where to open, when replacing a window that was already on screen. Null = right of the centre.</summary>
        public Vector3? InitialPosition;

        private Builder _ui;
        private RowList _rows;
        private UILabel _status;
        private UIButton _useGlobal;
        private UIButton _folder;
        private float _nextRefreshTime;

        private static CreatorPanel _instance;

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

            Frame frame = _ui.AddFrame(this, Localization.Get("Creator.Title"), Hide);

            float inner = PanelWidth - 2f * Margin;
            float y = TitleBarHeight;

            _status = _ui.AddLabel(this, string.Empty, new Vector3(Margin, y + 2f), _ui.Theme.TextScale);
            _status.autoSize = false;
            _status.wordWrap = true;
            _status.size = new Vector2(inner, StatusHeight - 4f);
            _ui.Paint(_status, _ui.Theme.Status);
            y += StatusHeight;

            float half = (inner - 6f) / 2f;
            _useGlobal = _ui.AddButton(this, Localization.Get("Asset.UseGlobal"), new Vector2(half, 26f), new Vector3(Margin, y + 4f));
            _useGlobal.eventClick += (component, e) =>
            {
                CreatorAsset.UseGlobal();
                AssetCatalog.Edited.Sync();
                SettingsCatalog.RefreshAllUIs();
            };
            _folder = _ui.AddButton(this, Localization.Get("Creator.OpenFolder"), new Vector2(half, 26f),
                new Vector3(Margin + half + 6f, y + 4f));
            _folder.eventClick += (component, e) => OpenFolder();
            y += ButtonsHeight;

            // Two tabs, as the building window's: its glass (the preview's switches first), its rooms.
            new AssetTabs(_ui).Build(this, frame, _rows, AssetCatalog.Edited.Rows, y, Margin, inner, "creator window");

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
            Log.Msg("creator window built: " + _rows.Count + " controls (" + _ui.Skin + ")");
        }

        public override void Update()
        {
            base.Update();

            if (!isVisible || Time.time < _nextRefreshTime)
                return;

            _nextRefreshTime = Time.time + RefreshInterval;
            RefreshValues();
        }

        /// <summary>Everything back to the building being made: what it is, its glass, its preview.</summary>
        public void RefreshValues()
        {
            if (_rows == null)
                return;

            AssetCatalog.Edited.Sync();
            if (_status != null)
                _status.text = CreatorAsset.StatusText();

            SetEnabled(_useGlobal, CreatorAsset.Info != null && CreatorAsset.Record != null);
            SetEnabled(_folder, CreatorAsset.Info != null);

            int touched = _rows.RefreshValues();
            if (touched > 0 && Log.Detailed)
                Log.Detail("creator window: refreshed " + _rows.Count + " controls, " + touched + " had changed");
        }

        /// <summary>The folder of its RealGlassDefinition.xml: where it was opened from, else where the editor saves.</summary>
        private static void OpenFolder()
        {
            string folder = CreatorAsset.Folder ?? DataLocation.assetsPath;
            try
            {
                ColossalFramework.Utils.OpenInFileBrowser(folder);
                Log.Msg("creator: opened " + folder);
            }
            catch (System.Exception e)
            {
                Log.Warn("creator: could not open " + folder + ": " + e.Message);
            }
        }

        private static void SetEnabled(UIComponent part, bool wanted)
        {
            if (part == null || part.isEnabled == wanted)
                return;

            part.isEnabled = wanted;
            part.opacity = wanted ? 1f : 0.45f;
        }

        /// <summary>Right of the screen's centre, clear of the asset editor's own panels on the left.</summary>
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
