using System;
using ColossalFramework;
using ColossalFramework.UI;
using HarmonyLib;
using UnityEngine;

namespace RealGlass.Water
{
    /// <summary>
    /// The tool that paints where the shores foam (the Water tab's Foam group, "Paint shores"):
    /// hold the left button for surf, the right button (or Shift + left) for no surf, Ctrl + left to
    /// give the place back to the automatic rule; [ and ] change the brush's size; Ctrl+Z / Ctrl+Y step
    /// back and forward; Escape or the button again puts it away. While it is out the foam shows its layers on the water (WaterFoam.shader's
    /// paint view: the waterline, the surf's band, each source of foam, the paint).
    /// </summary>
    /// <remarks>
    /// ForestPainter's brush (its PaintTool, the brush part only): the mouse ray taken in
    /// OnToolLateUpdate on the main thread, cast into the TERRAIN in SimulationStep, the stroke's dabs a
    /// quarter of the radius apart (strength 1 = full in half a second held still). The terrain's hit is
    /// the seabed under water: it is moved up to the water's surface along the ray, so the brush is where
    /// the water is seen. Added to the ToolController's own GameObject as the building picker is.
    /// </remarks>
    public sealed class ShorePaintTool : ToolBase
    {
        public static ShorePaintTool Instance { get; private set; }

        /// <summary>The brush's narrowest and widest (m): the Size row's range.</summary>
        public const float MinSize = 16f;
        public const float MaxSize = 512f;

        private static readonly Color SurfColour = new Color(0.55f, 0.95f, 1f, 0.9f);
        private static readonly Color NoSurfColour = new Color(1f, 0.40f, 0.30f, 0.9f);
        private static readonly Color AutoColour = new Color(0.85f, 0.85f, 0.85f, 0.9f);

        /// <summary>The brush's width (m): the Foam group's Size row (Settings.WaterPaintSize); [ and ] move it.</summary>
        public static float BrushSize
        {
            get { return Mathf.Clamp(Settings.WaterPaintSize != null ? Settings.WaterPaintSize.value : Settings.Defaults.WaterPaintSize, MinSize, MaxSize); }
            set
            {
                if (Settings.WaterPaintSize == null)
                    return;
                // On the row's 8 m steps, so the slider and the keys agree.
                Settings.WaterPaintSize.value = Mathf.Clamp(Mathf.Round(value / 8f) * 8f, MinSize, MaxSize);
                UI.SettingsCatalog.RefreshAllUIs();
            }
        }

        /// <summary>How fast it fills in, 0.05..1 (1 = full in half a second held still): the Strength row.</summary>
        private static float Strength
        {
            get { return Mathf.Clamp(Settings.WaterPaintStrength != null ? Settings.WaterPaintStrength.value : Settings.Defaults.WaterPaintStrength, 0.05f, 1f); }
        }

        /// <summary>The full-strength middle, a share of the radius (0 soft, 1 hard): the Hardness row.</summary>
        private static float Hardness
        {
            get { return Mathf.Clamp01(Settings.WaterPaintHardness != null ? Settings.WaterPaintHardness.value : Settings.Defaults.WaterPaintHardness); }
        }

        private Ray _ray;
        private float _rayLength;
        private volatile bool _rayValid;
        private readonly object _hitLock = new object();
        private Vector3 _hit;
        private volatile bool _hitValid;

        private bool _stroking;
        private int _strokeButton;
        private int _strokeTarget;
        private Vector3 _lastDab;
        private bool _lastDabValid;
        private int _strokeDabs;
        private string _shownHint;

        /// <summary>The tool is the game's current tool.</summary>
        public static bool IsActive
        {
            get
            {
                ToolController tools = ToolsModifierControl.toolController;
                return Instance != null && tools != null && tools.CurrentTool == Instance;
            }
        }

        /// <summary>Out, or away if it is out (the panel's button).</summary>
        public static void Toggle()
        {
            if (IsActive)
            {
                TurnOff("its button");
                return;
            }

            try
            {
                ToolController tools = ToolsModifierControl.toolController;
                if (tools == null)
                    return;
                if (ShorePaintSaveData.Keeping)
                {
                    Log.Warn("water: this city's painted shores could not be read (a newer version's?): they are kept as they were and cannot be painted");
                    return;
                }

                if (Instance == null)
                {
                    // Adding a tool makes it current at once (ToolBase.OnEnable).
                    Instance = tools.gameObject.AddComponent<ShorePaintTool>();
                }
                tools.CurrentTool = Instance;
                Log.Msg("water: painting the shores (brush " + BrushSize.ToString("F0") + " m; " + ShorePaint.Mask.TileCount + " tiles painted so far)");
            }
            catch (Exception e)
            {
                Log.Error("water: could not take out the shore paint tool", e);
            }
        }

        public static void TurnOff(string why)
        {
            if (!IsActive)
                return;
            ToolsModifierControl.SetTool<DefaultTool>();
            Log.Msg("water: painting the shores ended (" + why + "; " + ShorePaint.Mask.TileCount + " tiles painted)");
        }

        /// <summary>The city is closing: the tool goes.</summary>
        public static void Remove()
        {
            if (Instance == null)
                return;
            try
            {
                if (IsActive)
                    ToolsModifierControl.SetTool<DefaultTool>();
                Destroy(Instance);
            }
            catch (Exception e)
            {
                Log.Warn("water: removing the shore paint tool: " + e.Message);
            }
            Instance = null;
        }

        protected override void Awake()
        {
            base.Awake();
            Instance = this;
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            if (_stroking)
                EndStroke();
            _rayValid = false;
            _hitValid = false;
            _shownHint = null;
            ShowToolInfo(false, null, Vector3.zero);
        }

        protected override void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            base.OnDestroy();
        }

        protected override void OnToolLateUpdate()
        {
            Camera camera = GameCamera.Get();
            if (camera == null)
            {
                _rayValid = false;
                return;
            }
            _ray = camera.ScreenPointToRay(Input.mousePosition);
            _rayLength = camera.farClipPlane;
            _rayValid = !m_toolController.IsInsideUI && Cursor.visible;
        }

        /// <summary>The terrain under the pointer, moved up to the water's surface where there is water (simulation thread).</summary>
        public override void SimulationStep()
        {
            if (!_rayValid)
            {
                _hitValid = false;
                return;
            }

            Ray ray = _ray;
            var input = new RaycastInput(ray, _rayLength);
            RaycastOutput output;
            if (!RayCast(input, out output))
            {
                _hitValid = false;
                return;
            }

            Vector3 hit = output.m_hitPos;
            TerrainManager terrain = Singleton<TerrainManager>.instance;
            // Under water: back along the ray to the surface (two steps: the surface is nearly flat).
            for (int i = 0; i < 2 && ray.direction.y < -0.01f; i++)
            {
                float surface = terrain.SampleRawHeightSmoothWithWater(hit, false, 0f);
                if (surface <= hit.y + 0.05f)
                    break;
                float back = (surface - hit.y) / -ray.direction.y;
                hit -= ray.direction * back;
            }

            lock (_hitLock)
                _hit = hit;
            _hitValid = true;
        }

        private bool TryHit(out Vector3 hit)
        {
            lock (_hitLock)
                hit = _hit;
            return _hitValid && _rayValid;
        }

        protected override void OnToolGUI(Event e)
        {
            if (e.type != EventType.MouseDown || m_toolController.IsInsideUI || (e.button != 0 && e.button != 1))
                return;

            Vector3 hit;
            if (!TryHit(out hit) || _stroking)
                return;

            UIInput.MouseUsed();
            e.Use();
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            int target = e.button == 1 || shift ? -1 : control ? 0 : 1;
            BeginStroke(e.button, target, hit);
        }

        protected override void OnToolUpdate()
        {
            if (!SettingsCatalogAllowsPaint())
            {
                TurnOff("the foam is off");
                return;
            }

            Vector3 hit;
            bool valid = TryHit(out hit);
            if (_stroking)
            {
                if (!Input.GetMouseButton(_strokeButton))
                    EndStroke();
                else if (valid)
                    Dab(hit, Time.deltaTime);
            }

            Keys();
            string hint = Localization.Get(_stroking ? (_strokeTarget > 0 ? "ShorePaint.Surf" : _strokeTarget < 0 ? "ShorePaint.NoSurf" : "ShorePaint.Auto") : "ShorePaint.Hint",
                                           BrushSize.ToString("F0"));
            if (valid)
            {
                _shownHint = hint;
                ShowToolInfo(true, hint, hit);
            }
            else if (_shownHint != null)
            {
                _shownHint = null;
                ShowToolInfo(false, null, Vector3.zero);
            }
        }

        private static bool SettingsCatalogAllowsPaint()
        {
            return Settings.WaterEnabled != null && Settings.WaterEnabled.value && (Settings.WaterFoam == null || Settings.WaterFoam.value);
        }

        private void Keys()
        {
            if (UIView.HasInputFocus())
                return;

            if (!Input.GetMouseButton(0) && !Input.GetMouseButton(1))
            {
                if (Input.GetKeyDown(KeyCode.LeftBracket))
                    BrushSize = Mathf.Min(BrushSize / 1.25f, BrushSize - 8f);
                if (Input.GetKeyDown(KeyCode.RightBracket))
                    BrushSize = Mathf.Max(BrushSize * 1.25f, BrushSize + 8f);
            }

            bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (!control || _stroking)
                return;
            if (Input.GetKeyDown(KeyCode.Z))
                Log.Msg("water: shore paint " + (ShorePaint.StepBack() ? "stepped back" : "has nothing to step back"));
            else if (Input.GetKeyDown(KeyCode.Y))
                Log.Msg("water: shore paint " + (ShorePaint.StepForward() ? "stepped forward" : "has nothing to step forward"));
        }

        private void BeginStroke(int button, int target, Vector3 hit)
        {
            _stroking = true;
            _strokeButton = button;
            _strokeTarget = target;
            _lastDabValid = false;
            _strokeDabs = 0;
            lock (ShorePaint.Sync)
                ShorePaint.Mask.BeginStep();
            Dab(hit, Mathf.Max(Time.deltaTime, 1f / 60f));
        }

        /// <summary>One frame of the stroke: dabs from the last one to the pointer, a quarter of the radius apart, sharing this frame's strength.</summary>
        private void Dab(Vector3 hit, float seconds)
        {
            float radius = BrushSize * 0.5f;
            float total = Strength * Mathf.Clamp(seconds, 0f, 0.1f) * 2f;

            int n = 1;
            Vector3 from = hit;
            if (_lastDabValid)
            {
                float step = Mathf.Max(radius * 0.25f, 2f);
                from = _lastDab;
                Vector3 flat = hit - from;
                flat.y = 0f;
                n = Mathf.Clamp(Mathf.CeilToInt(flat.magnitude / step), 1, 64);
            }

            lock (ShorePaint.Sync)
            {
                ShorePaintMask mask = ShorePaint.Mask;
                for (int i = 1; i <= n; i++)
                {
                    Vector3 p = _lastDabValid ? Vector3.Lerp(from, hit, i / (float)n) : hit;
                    mask.Stamp(p.x, p.z, radius, Hardness, total / n, _strokeTarget);
                }
            }

            _strokeDabs += n;
            _lastDab = hit;
            _lastDabValid = true;
        }

        private void EndStroke()
        {
            _stroking = false;
            System.Collections.Generic.Dictionary<long, sbyte[]> step;
            lock (ShorePaint.Sync)
                step = ShorePaint.Mask.EndStep();
            ShorePaint.AddStep(step);
            if (step != null)
                Log.Msg("water: shores painted " + (_strokeTarget > 0 ? "with surf" : _strokeTarget < 0 ? "without surf" : "back to automatic") + ", " +
                        _strokeDabs + " dabs of " + BrushSize.ToString("F0") + " m; now " + ShorePaint.Mask.TileCount + " tiles");
        }

        /// <summary>The brush under the pointer: its full-strength middle and its fading edge, coloured by what a press would paint.</summary>
        public override void RenderOverlay(RenderManager.CameraInfo cameraInfo)
        {
            base.RenderOverlay(cameraInfo);
            Vector3 hit;
            if (!TryHit(out hit))
                return;

            int target = _stroking ? _strokeTarget :
                         Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? -1 :
                         Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ? 0 : 1;
            Color colour = target > 0 ? SurfColour : target < 0 ? NoSurfColour : AutoColour;
            OverlayEffect overlay = Singleton<RenderManager>.instance.OverlayEffect;
            overlay.DrawCircle(cameraInfo, new Color(colour.r, colour.g, colour.b, 0.25f), hit, BrushSize, hit.y - 60f, hit.y + 60f, false, true);
            overlay.DrawCircle(cameraInfo, new Color(colour.r, colour.g, colour.b, 0.35f), hit, BrushSize * Hardness, hit.y - 60f, hit.y + 60f, false, true);
            Singleton<ToolManager>.instance.m_drawCallData.m_overlayCalls += 2;
        }
    }

    /// <summary>Escape puts the shore paint tool away (the game's own Escape would open the pause menu), as the building picker's.</summary>
    [HarmonyPatch(typeof(GameKeyShortcuts), "Escape")]
    public static class ShorePaintEscapePatch
    {
        public static bool Prefix()
        {
            try
            {
                if (!ShorePaintTool.IsActive)
                    return true;
                ShorePaintTool.TurnOff("Escape");
                return false;
            }
            catch (Exception e)
            {
                Log.Warn("water: Escape with the shore paint tool: " + e.Message);
                return true;
            }
        }
    }
}
