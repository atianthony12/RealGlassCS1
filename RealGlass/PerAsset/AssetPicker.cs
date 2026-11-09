using System;
using System.Collections.Generic;
using ColossalFramework;
using ColossalFramework.Math;
using ColossalFramework.UI;
using HarmonyLib;
using RealGlass.Glass;
using RealGlass.Props;
using UnityEngine;

namespace RealGlass.PerAsset
{
    /// <summary>
    /// The building picker (Alt+G by default, as BOB's Alt+B): while it is on, the building under
    /// the cursor is outlined, and a click on it opens the building window (UI.AssetPanel) on its
    /// asset and turns the picker off, giving the player's cursor back (the author, 2026-10-04). The
    /// key again, a right-click or Escape turns it off without a pick; the window's "Pick another
    /// building" turns it on again.
    /// </summary>
    /// <remarks>
    /// A DefaultTool, as BOB's BOBTool is (IL of BOB 2197863850 and of the game, 2026-10-01): the
    /// base keeps the mouse ray (DefaultTool.OnToolLateUpdate: m_mouseRay, m_mouseRayLength,
    /// m_mouseRayValid = not over the UI and the cursor shown); SimulationStep casts it at buildings
    /// only, on the simulation thread as DefaultTool's own does; OnToolGUI takes the clicks without
    /// calling the base (no info panel opens); m_hoverInstance stays empty (the base highlights
    /// nothing). Added to the ToolController's own GameObject (ToolBase.Awake finds the controller
    /// there); a tool added at run time is in neither ToolController.m_tools nor ToolsModifierControl's
    /// list, so it is switched on by setting CurrentTool, and off by SetTool&lt;DefaultTool&gt;().
    ///
    /// Procedural Objects' buildings are no buildings of the city's: they are found on the main thread
    /// (their meshes are Unity's) by their SHAPE -- the ray against each one's box, then the triangles
    /// of the nearest (Glass.ProceduralBuildings.Raycast) -- not by the small dot Procedural Objects
    /// itself selects them by. Whichever is nearer under the cursor, one of them or the city's
    /// building, is the one pointed at: a copy stacked on a building to make it taller, or the
    /// building under it, whichever the cursor is on. Its props the same way (1.1.9): one picked opens
    /// the prop window on its asset, as a prop of the city's does.
    /// </remarks>
    public sealed class AssetPicker : DefaultTool
    {
        public static AssetPicker Instance { get; private set; }

        private static readonly Color HoverColour = new Color(0.40f, 0.85f, 1.00f, 0.75f);

        /// <summary>The main building under the cursor, from the simulation thread, and how far along the ray.</summary>
        private volatile int _hover;
        private volatile float _hoverDistance;
        private Vector3 _hoverPosition;
        private int _shownHover = -1;
        private string _shownText = "";

        /// <summary>A prop standing on its own under the cursor, nearer than any building there (simulation thread); 0 none (1.1.9).</summary>
        private volatile int _hoverProp;
        private int _shownProp = -1;

        /// <summary>A building or prop of Procedural Objects nearer under the cursor than the city's (main thread), and where.</summary>
        private ProceduralBuildings.Item _procedural;
        private ProceduralBuildings.Item _shownProcedural;
        private Vector3 _proceduralPoint;

        /// <summary>A prop another mod draws (Intersection Marking Tool's) nearer under the cursor than the rest (main thread).</summary>
        private PickTarget _other;
        private PickTarget _shownOther;
        private readonly List<KeyValuePair<OtherModsSeen.Seen, float>> _otherHits = new List<KeyValuePair<OtherModsSeen.Seen, float>>();

        /// <summary>
        /// Everything under the cursor, nearest first, while the player steps through it with the next
        /// key (Ctrl+Space; PickStack): the one shown and clicked is _stack[_stackAt]. Moving the
        /// cursor ends it.
        /// </summary>
        private List<PickTarget> _stack;
        private int _stackAt;
        private Vector3 _stackMouse;
        private bool _nextWasDown;
        private string _stackText;
        private int _stackShown = -1;

        private const float StackMouseSlack = 4f;

        /// <summary>The picker is the game's current tool.</summary>
        public static bool IsActive
        {
            get
            {
                ToolController tools = ToolsModifierControl.toolController;
                return Instance != null && tools != null && tools.CurrentTool == Instance;
            }
        }

        /// <summary>Puts the picker on the game's ToolController (one city), leaving the tool that was current.</summary>
        public static void Create()
        {
            if (Instance != null)
                return;

            ToolController tools = ToolsModifierControl.toolController;
            if (tools == null)
            {
                Log.Warn("assets: no ToolController, so no building picker");
                return;
            }

            try
            {
                // Adding a tool makes it current at once (ToolBase.OnEnable): the one before is put back.
                ToolBase previous = tools.CurrentTool;
                Instance = tools.gameObject.AddComponent<AssetPicker>();
                if (previous != null && previous != Instance)
                    tools.CurrentTool = previous;
                else
                    ToolsModifierControl.SetTool<DefaultTool>();
                Log.Msg("assets: building picker ready (" + DescribeKey() + ")");
            }
            catch (Exception e)
            {
                Log.Error("assets: could not create the building picker", e);
                Instance = null;
            }
        }

        public static void DestroyPicker()
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
                Log.Warn("assets: removing the building picker: " + e.Message);
            }

            Instance = null;
        }

        /// <summary>On, or off if it is on: the key and the building window's "Pick another building".</summary>
        public static void Toggle()
        {
            if (Instance == null)
                return;

            if (IsActive)
            {
                TurnOff("its key");
                return;
            }

            ToolsModifierControl.toolController.CurrentTool = Instance;
            Log.Msg("assets: building picker on -- click a building to choose its asset");
        }

        public static void TurnOff(string why)
        {
            if (!IsActive)
                return;

            ToolsModifierControl.SetTool<DefaultTool>();
            Log.Msg("assets: building picker off (" + why + ")");
        }

        public static string DescribeKey()
        {
            return Settings.PickerKey != null ? SettingsXmlFormat.FormatKey(Settings.PickerKey.value) : "no key";
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            _hover = 0;
            _hoverProp = 0;
            _shownHover = -1;
            _shownProp = -1;
            _procedural = _shownProcedural = null;
            _other = _shownOther = null;
            _stack = null;
            _stackShown = -1;
            ProceduralBuildings.ForgetShapes();
            OtherModsSeen.Stop();
            ShowToolInfo(false, null, Vector3.zero);
        }

        protected override void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            base.OnDestroy();
        }

        /// <summary>
        /// The building or the prop standing on its own under the cursor, whichever is nearer
        /// (simulation thread, as DefaultTool casts its own ray; ToolBase.RayCast keeps the nearest hit
        /// of the kinds asked for). A building's own props are no props of the city's: they are chosen
        /// in its window (the Props button), not here.
        /// </summary>
        public override void SimulationStep()
        {
            if (!m_mouseRayValid)
            {
                _hover = 0;
                _hoverProp = 0;
                return;
            }

            Ray ray = m_mouseRay;
            RaycastInput input = new RaycastInput(ray, m_mouseRayLength);
            input.m_ignoreTerrain = true;
            input.m_ignoreBuildingFlags = Building.Flags.None;
            input.m_buildingService = new RaycastService(ItemClass.Service.None, ItemClass.SubService.None, ItemClass.Layer.None);
            input.m_ignorePropFlags = PropInstance.Flags.None;
            input.m_propService = new RaycastService(ItemClass.Service.None, ItemClass.SubService.None, ItemClass.Layer.None);

            RaycastOutput output;
            int hit = 0;
            int prop = 0;
            bool any = RayCast(input, out output);
            if (any && output.m_propInstance != 0)
            {
                PropInstance[] props = Singleton<PropManager>.instance.m_props.m_buffer;
                if (((PropInstance.Flags)props[output.m_propInstance].m_flags & (PropInstance.Flags.Created | PropInstance.Flags.Deleted)) == PropInstance.Flags.Created)
                {
                    prop = output.m_propInstance;
                    _hoverPosition = output.m_hitPos;
                    _hoverDistance = Vector3.Distance(ray.origin, output.m_hitPos);
                }
            }
            else if (any && output.m_building != 0)
            {
                ushort building = output.m_building;
                Building[] buffer = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                if (buffer[building].m_parentBuilding != 0)
                {
                    ushort main = Building.FindParentBuilding(building);
                    if (main != 0)
                        building = main;
                }

                if ((buffer[building].m_flags & (Building.Flags.Created | Building.Flags.Deleted)) == Building.Flags.Created)
                {
                    hit = building;
                    _hoverPosition = output.m_hitPos;
                    _hoverDistance = Vector3.Distance(ray.origin, output.m_hitPos);
                }
            }

            _hover = hit;
            _hoverProp = prop;
        }

        /// <summary>The asset's name by the cursor; no info panel, no cursor of the base's.</summary>
        protected override void OnToolUpdate()
        {
            OtherModsSeen.Recording = true;
            if (UpdateStack())
                return;

            FindProcedural();
            FindOther();
            if (_other != null)
            {
                if (_shownOther == null || _shownOther.Info != _other.Info)
                {
                    _shownOther = _other;
                    _shownProcedural = null;
                    _shownHover = -1;
                    _shownProp = -1;
                    _shownText = Localization.Get("Picker.Prop", _other.Title);
                }
                ShowToolInfo(true, _shownText, m_mouseRay.origin + m_mouseRay.direction.normalized * _other.Distance);
                return;
            }
            _shownOther = null;

            if (_procedural != null)
            {
                if (_procedural != _shownProcedural)
                {
                    _shownProcedural = _procedural;
                    _shownHover = -1;
                    _shownProp = -1;
                    var poProp = _procedural as ProceduralBuildings.PropEntry;
                    var poBuilding = _procedural as ProceduralBuildings.Entry;
                    _shownText = poProp != null ? Localization.Get("Picker.Prop", PropSelection.TitleOf(poProp.Info))
                        : Localization.Get("Picker.Building", AssetSelection.TitleOf(poBuilding != null ? poBuilding.Info : null));
                }

                ShowToolInfo(true, _shownText, _proceduralPoint);
                return;
            }

            _shownProcedural = null;
            int prop = _hoverProp;
            if (prop != 0)
            {
                if (prop != _shownProp)
                {
                    _shownProp = prop;
                    _shownHover = -1;
                    PropInfo info = Singleton<PropManager>.instance.m_props.m_buffer[prop].Info;
                    _shownText = info != null ? Localization.Get("Picker.Prop", PropSelection.TitleOf(info)) : "";
                }

                ShowToolInfo(true, _shownText, _hoverPosition);
                return;
            }

            _shownProp = -1;
            int hover = _hover;
            if (hover == 0)
            {
                if (_shownHover != 0)
                {
                    _shownHover = 0;
                    ShowToolInfo(false, null, Vector3.zero);
                }
                return;
            }

            if (hover != _shownHover)
            {
                _shownHover = hover;
                BuildingInfo info = Singleton<BuildingManager>.instance.m_buildings.m_buffer[hover].Info;
                _shownText = info != null ? Localization.Get("Picker.Building", AssetSelection.TitleOf(info)) : "";
            }

            ShowToolInfo(true, _shownText, _hoverPosition);
        }

        /// <summary>
        /// The next key steps through everything under the cursor (PickStack), nearest first: the
        /// first press lists it and shows the one behind the nearest, each press after the next one,
        /// round again after the last. Moving the cursor ends it. True while it shows one (the tooltip
        /// and the click are its).
        /// </summary>
        private bool UpdateStack()
        {
            if (_stack != null && (!m_mouseRayValid || (Input.mousePosition - _stackMouse).sqrMagnitude > StackMouseSlack * StackMouseSlack))
            {
                _stack = null;
                _stackShown = -1;
                _shownHover = _shownProp = -1;
                _shownProcedural = null;
                _shownOther = null;
            }

            bool down = NextKeyDown();
            bool pressed = down && !_nextWasDown;
            _nextWasDown = down;
            if (pressed && m_mouseRayValid)
            {
                if (_stack == null)
                {
                    _stack = PickStack.Along(m_mouseRay, m_mouseRayLength);
                    _stackMouse = Input.mousePosition;
                    _stackAt = _stack.Count > 1 ? 1 : 0;
                    Log.Msg("assets: " + _stack.Count + " thing" + (_stack.Count == 1 ? "" : "s") + " under the cursor; showing number " + (_stackAt + 1));
                }
                else if (_stack.Count > 0)
                {
                    _stackAt = (_stackAt + 1) % _stack.Count;
                }
                _stackShown = -1;
            }

            if (_stack == null || _stack.Count == 0)
            {
                if (_stack != null && pressed)
                    _stack = null;   // nothing at all there
                return false;
            }

            PickTarget target = _stack[_stackAt];
            if (_stackShown != _stackAt)
            {
                _stackShown = _stackAt;
                string key = Settings.PickerNextKey != null ? Settings.PickerNextKey.ToLocalizedString("KEYNAME") : "Ctrl+Space";
                _stackText = Localization.Get(target.IsProp ? "Picker.Prop" : "Picker.Building", target.Title) + "\n" +
                             Localization.Get("Picker.Next", (_stackAt + 1).ToString(), _stack.Count.ToString(), key);
            }
            ShowToolInfo(true, _stackText, m_mouseRay.origin + m_mouseRay.direction.normalized * target.Distance);
            return true;
        }

        private static bool NextKeyDown()
        {
            SavedInputKey key = Settings.PickerNextKey;
            if (key == null || key.Key == KeyCode.None)
                return false;
            bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            return Input.GetKey(key.Key) && control == key.Control && shift == key.Shift && alt == key.Alt;
        }

        /// <summary>The nearest prop another mod draws under the cursor, if nearer than anything else found there.</summary>
        private void FindOther()
        {
            _other = null;
            if (!m_mouseRayValid)
                return;

            _otherHits.Clear();
            Ray ray = new Ray(m_mouseRay.origin, m_mouseRay.direction.normalized);
            OtherModsSeen.RaycastAll(ray, m_mouseRayLength, _otherHits);
            float best = float.MaxValue;
            int at = -1;
            for (int i = 0; i < _otherHits.Count; i++)
            {
                if (_otherHits[i].Value < best)
                {
                    best = _otherHits[i].Value;
                    at = i;
                }
            }
            if (at < 0)
                return;

            float nearest = _procedural != null ? Vector3.Distance(ray.origin, _proceduralPoint)
                : ((_hover != 0 || _hoverProp != 0) ? _hoverDistance : float.MaxValue);
            if (best >= nearest)
                return;

            OtherModsSeen.Seen seen = _otherHits[at].Key;
            _other = new PickTarget { What = PickTarget.Kind.Other, Info = seen.Info, Matrix = seen.Matrix, Distance = best };
            _otherHits.Clear();
        }

        /// <summary>The building or prop of Procedural Objects under the cursor, if it is nearer than the city's building or prop there.</summary>
        private void FindProcedural()
        {
            _procedural = null;
            if (!m_mouseRayValid)
                return;

            ProceduralBuildings.Refresh();
            Ray ray = m_mouseRay;
            float distance;
            ProceduralBuildings.Item found = ProceduralBuildings.Raycast(ray, m_mouseRayLength, out distance);
            if (found == null || !HasAsset(found) || ((_hover != 0 || _hoverProp != 0) && distance >= _hoverDistance))
                return;

            _procedural = found;
            _proceduralPoint = ray.origin + ray.direction * distance;
        }

        private static bool HasAsset(ProceduralBuildings.Item item)
        {
            var prop = item as ProceduralBuildings.PropEntry;
            if (prop != null)
                return prop.Info != null;
            var building = item as ProceduralBuildings.Entry;
            return building != null && building.Info != null;
        }

        protected override void OnToolGUI(Event e)
        {
            if (e.type != EventType.MouseDown || m_toolController.IsInsideUI)
                return;

            if (e.button == 1)
            {
                UIInput.MouseUsed();
                TurnOff("right-click");
                return;
            }

            if (e.button != 0)
                return;

            // The one stepped to (Ctrl+Space), else a prop another mod draws nearer than the rest.
            PickTarget chosen = _stack != null && _stack.Count > 0 ? _stack[_stackAt] : _other;
            if (chosen != null)
            {
                UIInput.MouseUsed();
                e.Use();
                Pick(chosen);
                return;
            }

            ProceduralBuildings.Item procedural = _procedural;
            ushort building = (ushort)_hover;
            ushort prop = (ushort)_hoverProp;
            if (procedural == null && building == 0 && prop == 0)
                return;

            // The click is ours: the default tool, current again from here on, opens a building's info
            // panel on a mouse-down (DefaultTool.OnToolGUI, IL 2026-10-04) and must not see this one.
            UIInput.MouseUsed();
            e.Use();
            if (procedural == null && prop != 0)
            {
                PropSelection.SelectFree(prop);
                TurnOff("a prop was picked");
                ModController.ShowPropWindow();
                return;
            }

            var proceduralProp = procedural as ProceduralBuildings.PropEntry;
            if (proceduralProp != null)
            {
                PropSelection.SelectAsset(proceduralProp.Info, "a prop of Procedural Objects (its object " + ProceduralBuildings.IdOf(proceduralProp) + ")");
                TurnOff("a prop was picked");
                ModController.ShowPropWindow();
                return;
            }

            var proceduralBuilding = procedural as ProceduralBuildings.Entry;
            if (proceduralBuilding != null)
                AssetSelection.SelectAsset(proceduralBuilding.Info, "a building of Procedural Objects (its object " + ProceduralBuildings.IdOf(proceduralBuilding) + ")");
            else
                AssetSelection.Select(building);
            TurnOff("a building was picked");
            ModController.ShowAssetWindow();
        }

        /// <summary>A target picked: the prop window on a prop's asset, the building window on a building's.</summary>
        private static void Pick(PickTarget t)
        {
            switch (t.What)
            {
                case PickTarget.Kind.Prop:
                    PropSelection.SelectFree(t.Id);
                    break;
                case PickTarget.Kind.Other:
                    PropSelection.SelectAsset(t.Info, "a prop of Intersection Marking Tool");
                    break;
                case PickTarget.Kind.Procedural:
                    var poProp = t.Item as ProceduralBuildings.PropEntry;
                    var poBuilding = t.Item as ProceduralBuildings.Entry;
                    if (poProp != null)
                        PropSelection.SelectAsset(poProp.Info, "a prop of Procedural Objects (its object " + ProceduralBuildings.IdOf(poProp) + ")");
                    else if (poBuilding != null)
                        AssetSelection.SelectAsset(poBuilding.Info, "a building of Procedural Objects (its object " + ProceduralBuildings.IdOf(poBuilding) + ")");
                    break;
                default:
                    AssetSelection.Select(t.Id);
                    break;
            }

            if (t.IsProp)
            {
                TurnOff("a prop was picked");
                ModController.ShowPropWindow();
            }
            else
            {
                TurnOff("a building was picked");
                ModController.ShowAssetWindow();
            }
        }

        /// <summary>A target outlined as the game outlines its kind.</summary>
        private static void Outline(RenderManager.CameraInfo cameraInfo, PickTarget t)
        {
            switch (t.What)
            {
                case PickTarget.Kind.Building:
                    Building[] buffer = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                    BuildingTool.RenderOverlay(cameraInfo, ref buffer[t.Id], HoverColour, HoverColour);
                    break;
                case PickTarget.Kind.Prop:
                    OutlineProp(cameraInfo, t.Id);
                    break;
                case PickTarget.Kind.Procedural:
                    Quad3 quad;
                    float minY, maxY;
                    if (ProceduralBuildings.Footprint(t.Item, out quad, out minY, out maxY))
                        Singleton<RenderManager>.instance.OverlayEffect.DrawQuad(cameraInfo, HoverColour, quad, minY - 100f, maxY + 100f, false, false);
                    break;
                default:
                    if (t.Info == null)
                        break;
                    // Its matrix: TRS(position, AngleAxis(angle, down) x ..., scale) as Intersection Marking Tool builds it.
                    Vector3 position = t.Matrix.GetColumn(3);
                    Vector3 forward = t.Matrix.GetColumn(2);
                    float scale = ((Vector3)t.Matrix.GetColumn(0)).magnitude;
                    float angle = Mathf.Atan2(-forward.x, forward.z);
                    PropTool.RenderOverlay(cameraInfo, t.Info, position, scale, angle, HoverColour);
                    break;
            }
        }

        /// <summary>A city's prop outlined as the game's prop tool outlines one, at its own scale.</summary>
        private static void OutlineProp(RenderManager.CameraInfo cameraInfo, ushort prop)
        {
            // PropInstance.RenderInstance's scale: m_minScale + Randomizer(id).Int32(10000) x (max - min) x 0.0001.
            PropInstance instance = Singleton<PropManager>.instance.m_props.m_buffer[prop];
            PropInfo info = instance.Info;
            if (info == null)
                return;
            var random = new Randomizer(prop);
            float scale = info.m_minScale + random.Int32(10000u) * (info.m_maxScale - info.m_minScale) * 0.0001f;
            PropTool.RenderOverlay(cameraInfo, info, instance.Position, scale, instance.Angle, HoverColour);
        }

        public override void RenderOverlay(RenderManager.CameraInfo cameraInfo)
        {
            List<PickTarget> stack = _stack;
            if (stack != null && stack.Count > 0 && _stackAt < stack.Count)
            {
                Outline(cameraInfo, stack[_stackAt]);
                return;
            }
            PickTarget other = _other;
            if (other != null)
            {
                Outline(cameraInfo, other);
                return;
            }

            ProceduralBuildings.Item procedural = _procedural;
            if (procedural != null)
            {
                // Its footprint, outlined as the game outlines a building (BuildingTool.RenderOverlay:
                // OverlayEffect.DrawQuad over its height and 100 m either way); a prop's the same way.
                Quad3 quad;
                float minY, maxY;
                if (ProceduralBuildings.Footprint(procedural, out quad, out minY, out maxY))
                    Singleton<RenderManager>.instance.OverlayEffect.DrawQuad(cameraInfo, HoverColour, quad, minY - 100f, maxY + 100f, false, false);
                return;
            }

            int prop = _hoverProp;
            if (prop != 0)
            {
                // Outlined as the game's prop tool outlines one, at its own scale (PropInstance.RenderInstance:
                // m_minScale + Randomizer(id).Int32(10000) x (max - min) x 0.0001).
                PropInstance instance = Singleton<PropManager>.instance.m_props.m_buffer[prop];
                PropInfo info = instance.Info;
                if (info != null)
                {
                    var random = new Randomizer(prop);
                    float scale = info.m_minScale + random.Int32(10000u) * (info.m_maxScale - info.m_minScale) * 0.0001f;
                    PropTool.RenderOverlay(cameraInfo, info, instance.Position, scale, instance.Angle, HoverColour);
                }
                return;
            }

            int hover = _hover;
            if (hover == 0)
                return;

            Building[] buffer = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            BuildingTool.RenderOverlay(cameraInfo, ref buffer[hover], HoverColour, HoverColour);
        }
    }

    /// <summary>
    /// Escape turns the picker off, as BOB's prefix on the same method does: the game's own Escape
    /// (GameKeyShortcuts.Escape) knows no tool of a mod's and would open the pause menu.
    /// </summary>
    [HarmonyPatch(typeof(GameKeyShortcuts), "Escape")]
    public static class PickerEscapePatch
    {
        public static bool Prefix()
        {
            // Whatever goes wrong here, the game's Escape must still work.
            try
            {
                if (!AssetPicker.IsActive)
                    return true;

                AssetPicker.TurnOff("Escape");
                return false;
            }
            catch (Exception e)
            {
                Log.Warn("assets: Escape with the building picker: " + e.Message);
                return true;
            }
        }
    }
}
