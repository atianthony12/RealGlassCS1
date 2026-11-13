using System;
using ColossalFramework;
using RealGlass.Vehicles;
using UnityEngine;

namespace RealGlass.Glass
{
    /// <summary>
    /// The glass renderer for one city: created by ModController, destroyed with it. Every frame
    /// it reads the settings and the scene (the sun, the sky's light, the ground, the weather),
    /// keeps the screen copy (GlassScreen) and the sky cube (GlassSky) up, sets the shader's
    /// uniforms (GlassUniforms) and tells the draws (GlassDraw, called from GlassPatches inside the
    /// game's rendering) whether to run. Off means nothing runs: no copy, no cube, no draw.
    /// </summary>
    public sealed class GlassController : MonoBehaviour
    {
        /// <summary>The live controller, or null outside a city.</summary>
        public static GlassController Instance { get; private set; }

        private static bool _dirty = true;
        private static string _status = "";

        private readonly GlassScreen _screen = new GlassScreen();
        private readonly GlassSky _sky = new GlassSky();
        private readonly CityHeights _heights = new CityHeights();
        private readonly AssetMap _assets = new AssetMap();
        private readonly LodGlassMap _lodGlass = new LodGlassMap();
        private readonly RoomAtlas _rooms = new RoomAtlas();
        private readonly RoomFitMap _roomFit = new RoomFitMap();
        private bool _roomsTried;
        private readonly DirtTextures _dirt = new DirtTextures();
        private bool _dirtTried;
        private string _loggedRooms = "";
        private int _loggedHiZ = -1;

        private const string RoomsNightResource = "RealGlass.Resources.rooms-night.bytes";
        private const string RoomsDayResource = "RealGlass.Resources.rooms-day.bytes";
        private const string DirtGrimeResource = "RealGlass.Resources.dirt-grime.bytes";
        private const string DirtRunsResource = "RealGlass.Resources.dirt-runs.bytes";

        /// <summary>
        /// What the costly parts may spend (GlassQuality): Ultra until players get a quality choice;
        /// that choice will set this and nothing else.
        /// </summary>
        private GlassQuality _quality = GlassQuality.ForTier(GlassQuality.Ultra);
        private Vector3 _look;
        private Material _near, _lod, _group, _util;
        private GlassPrePass _prePass;
        private FacadeCardsGame _cards;
        private string _cardsOffFor;   // the glass's off state the cards were last stopped for (once a state)

        /// <summary>RealGlass/VehicleGlass (Vehicles/VehicleGlassDraw.cs), instancing enabled; null where the shader is missing.</summary>
        private Material _vehicle;

        /// <summary>The vehicles' glass as last logged: on, off, or why not.</summary>
        private string _loggedVehicles;

        /// <summary>RealGlass/PropGlass (Props/PropGlassDraw.cs), instancing enabled: opaque props' and see-through props' (null where the shader is missing).</summary>
        private Material _propOpaque, _propSeeThrough;

        /// <summary>The props' glass and water as last logged.</summary>
        private string _loggedProps;

        private static readonly int IdPropRipple = Shader.PropertyToID("_RGPropRipple");
        private static readonly int IdGameRipples = Shader.PropertyToID("_WaterNormal");
        private Texture _atlasRgb, _atlasXys, _atlasAci;
        private int _buildingLayers;
        private bool _layersKnown;
        private bool _wasOn;
        private bool _loggedClouds;
        private bool _loggedDistant;
        private string _lastState = "";
        private float _nextDetail;
        private int _frames;
        private float _frameTime;
        private int _lastNear, _lastLod, _lastFar;
        private Camera _watched;
        private DepthTextureMode _originalDepthMode;
        private GlassParams _params;

        // The rooms' clock (Clock): game hours, and the frame it was counted to (-1 = not yet).
        private double _clockHours;
        private double _clockFrame = -1.0;

        /// <summary>
        /// Called by the settings catalog after any glass setting changed (every glass row's
        /// AfterChange), and once after a Reset. Cheap: the controller re-reads next frame.
        /// </summary>
        public static void SettingsChanged()
        {
            _dirty = true;
        }

        /// <summary>One line for the panel's status row; "" when there is nothing to say.</summary>
        public static string Status
        {
            get { return _status; }
        }

        /// <summary>
        /// The panel changed this asset's own glass: on every copy of it now, not over the next
        /// dozen frames.
        /// </summary>
        public static void RefreshAsset(BuildingInfo info)
        {
            GlassController c = Instance;
            if (c != null)
                c._assets.RefreshAsset(info, _dirty ? GlassSettings.Read() : c._params);
        }

        /// <summary>What this asset's LODs show (LodGlass.Verdict*), for its building window; NotMeasured outside a city or before they are compared.</summary>
        public static int LodVerdictOf(BuildingInfo info)
        {
            GlassController c = Instance;
            return c != null && info != null ? c._lodGlass.VerdictOf(info.name) : LodGlass.VerdictNotMeasured;
        }

        /// <summary>What was found of this asset's windows (RoomFit), for its building window; null outside a city or before it is measured.</summary>
        public static RoomFitResult RoomFitOf(BuildingInfo info)
        {
            GlassController c = Instance;
            return c != null && info != null ? c._roomFit.ResultOf(info.name) : null;
        }

        /// <summary>The shininess this asset's glass needs as read from its textures (ThresholdDetect); 0 when none, outside a city or before it is measured.</summary>
        public static float DetectedThresholdOf(string asset)
        {
            GlassController c = Instance;
            return c != null ? c._roomFit.DetectedOf(asset) : 0f;
        }

        /// <summary>How many verdicts the detection has given so far this city (for re-resolving).</summary>
        public static long DetectionsVersion()
        {
            GlassController c = Instance;
            return c != null ? c._roomFit.DetectionsVersion : 0;
        }

        /// <summary>A layer any building asset draws on (its m_prefabDataLayer): where group meshes get glass.</summary>
        public static bool IsBuildingLayer(int layer)
        {
            GlassController c = Instance;
            if (c == null || layer < 0 || layer > 31)
                return false;
            if (!c._layersKnown)
                c.FindBuildingLayers();
            return (c._buildingLayers & (1 << layer)) != 0;
        }

        private void Awake()
        {
            Instance = this;

            // A new city's controller has read nothing yet: the flag is static (the catalog's hooks
            // set it with no controller in hand), so the first city's reading left it clear, and
            // the second city of a session would otherwise run on an empty GlassParams -- glass off
            // with its checkbox on, until a setting moved (audit, 2026-10-11).
            _dirty = true;
        }

        private void Start()
        {
            try
            {
                Shader glass = ShaderBundle.Get(ShaderBundle.Glass);
                Shader util = ShaderBundle.Get(ShaderBundle.GlassUtil);
                if (glass == null || !glass.isSupported)
                {
                    Log.Warn("glass: the glass shader is " + (glass == null ? "missing" : "not supported here") + ": no glass");
                    enabled = false;
                    return;
                }

                _near = MakeMaterial(glass, "RG_NEAR");
                _near.SetVector("_RGNearBase", Vector4.zero);   // a building's ground is its origin, unless a draw says otherwise
                _lod = MakeMaterial(glass, "RG_LOD");
                _group = MakeMaterial(glass, "RG_GROUP");
                if (util != null && util.isSupported)
                    _util = new Material(util) { name = "RealGlass util", hideFlags = HideFlags.HideAndDontSave };
                else
                    Log.Warn("glass: the GlassUtil shader is " + (util == null ? "missing" : "not supported") + ": reflections of the sky only");

                GlassDraw.Reset();
                GlassDraw.Near = _near;
                GlassDraw.Lod = _lod;
                GlassDraw.Group = _group;

                // What the glass mirrors, in a pass of its own (GlassPrePass): without it the glass
                // mirrors the sky alone.
                Shader prePass = ShaderBundle.Get(ShaderBundle.GlassPrePass);
                Shader reflection = ShaderBundle.Get(ShaderBundle.GlassReflection);
                if (_util != null && prePass != null && prePass.isSupported && reflection != null && reflection.isSupported)
                {
                    _prePass = new GlassPrePass(prePass, reflection);
                    _prePass.Material(GlassPrePass.TierNear).SetVector("_RGNearBase", Vector4.zero);
                    _screen.PrePass = _prePass;
                    GlassDraw.PrePass = _prePass;

                    // The facade cards (a trial, 2026-11-13): off unless this platform's bundle has them.
                    _cards = new FacadeCardsGame(_prePass);
                }
                else
                {
                    Log.Warn("glass: the reflection pass's shaders are " + (prePass == null || reflection == null ? "missing" : "not supported here") +
                             ": the glass mirrors the sky alone");
                }

                // The vehicles' glass: its own shader, drawn instanced (one draw per model in view).
                VehicleGlassDraw.Reset();
                Shader vehicle = ShaderBundle.Get(ShaderBundle.VehicleGlass);
                if (vehicle != null && vehicle.isSupported)
                {
                    _vehicle = new Material(vehicle) { name = "RealGlass vehicles", hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
                    VehicleGlassDraw.Glass = _vehicle;
                }
                else
                {
                    Log.Warn("vehicles: the vehicle glass shader is " + (vehicle == null ? "missing" : "not supported here") + ": no glass on vehicles");
                }

                // The props' glass and water (the prop window): its own shader, two materials -- an
                // opaque prop's at the shader's queue, a see-through prop's after the transparent queue.
                Props.PropGlassDraw.Reset();
                Shader prop = ShaderBundle.Get(ShaderBundle.PropGlass);
                if (prop != null && prop.isSupported)
                {
                    _propOpaque = new Material(prop) { name = "RealGlass props", hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
                    _propSeeThrough = new Material(prop)
                    {
                        name = "RealGlass see-through props", hideFlags = HideFlags.HideAndDontSave, enableInstancing = true,
                        renderQueue = Props.PropGlassDraw.SeeThroughQueue,
                    };
                    Props.PropGlassDraw.Opaque = _propOpaque;
                    Props.PropGlassDraw.SeeThrough = _propSeeThrough;
                }
                else
                {
                    Log.Warn("props: the prop glass shader is " + (prop == null ? "missing" : "not supported here") + ": no glass or water on props");
                }
                FindBuildingLayers();

                // Each asset's threshold read from its textures (RoomFitMap measures every asset, rooms or not).
                _assets.DetectedOf = info => info != null ? _roomFit.DetectedOf(info.name) : 0f;
                _assets.DetectionsVersion = () => _roomFit.DetectionsVersion;
                _assets.Detected = () => _roomFit.DetectedInfos;
                Log.Msg("glass: renderer ready (shader '" + glass.name + "', util " + (_util != null ? "yes" : "no") +
                        "); building layers " + DescribeLayers() + "; " + DescribeSettings(GlassSettings.Read()) +
                        "; quality " + _quality);
            }
            catch (Exception e)
            {
                Log.Error("glass: could not start the renderer", e);
                enabled = false;
            }
        }

        private static Material MakeMaterial(Shader shader, string keyword)
        {
            var m = new Material(shader) { name = "RealGlass " + keyword, hideFlags = HideFlags.HideAndDontSave };
            m.EnableKeyword(keyword);
            return m;
        }

        private void FindBuildingLayers()
        {
            _buildingLayers = 0;
            int count = PrefabCollection<BuildingInfo>.LoadedCount();
            for (uint i = 0; i < count; i++)
            {
                BuildingInfo info = PrefabCollection<BuildingInfo>.GetLoaded(i);
                if (info != null && info.m_prefabDataLayer >= 0 && info.m_prefabDataLayer < 32)
                    _buildingLayers |= 1 << info.m_prefabDataLayer;
            }

            _layersKnown = count > 0;
        }

        private string DescribeLayers()
        {
            string s = "";
            for (int i = 0; i < 32; i++)
            {
                if ((_buildingLayers & (1 << i)) != 0)
                    s += (s.Length > 0 ? ", " : "") + i + " " + LayerMask.LayerToName(i);
            }
            return s.Length > 0 ? s : "none";
        }

        private void LateUpdate()
        {
            try
            {
                Frame();
            }
            catch (Exception e)
            {
                // Off for the rest of this city, and everything the glass holds given back, as when
                // it is switched off: nothing of ours left on the camera.
                GlassDraw.Fail("the frame", e);
                enabled = false;
                _status = Localization.Get("Status.Failed");
                try
                {
                    StopCards("the glass stopped after an error");
                    _screen.Detach();
                    _sky.Release();
                    RestoreCamera();
                }
                catch (Exception cleanup)
                {
                    Log.Warn("glass: cleaning up after the error: " + cleanup.Message);
                }
                _wasOn = false;
            }
        }

        private void Frame()
        {
            _frames++;
            _frameTime += Time.unscaledDeltaTime;
            if (_dirty)
            {
                _params = GlassSettings.Read();
                _dirty = false;

                // A fresh 5 s for the detail line after a change of setting too, as after a change
                // of state below: every frame time it gives is then all one setting's, and two of
                // them compare (tools/benchmark.ps1 reads them so).
                ResetCounters();
                _nextDetail = Time.realtimeSinceStartup + 5f;
            }

            GlassParams p = _params;
            Camera camera = GameCamera.Get();
            bool infoView = Singleton<InfoManager>.exists && Singleton<InfoManager>.instance.CurrentMode != InfoManager.InfoMode.None;
            bool snapshot = IsTakingSnapshots();
            bool failed = GlassDraw.Failed;
            bool on = p.Enabled && camera != null && _near != null && !infoView && !snapshot && !failed;

            string state = failed ? "stopped after an error (above)" : !p.Enabled ? "off" : (camera == null ? "no camera" :
                (infoView ? "an info view is open" : (snapshot ? "the Snapshot tool is in use" : "on")));
            if (state != _lastState)
            {
                Log.Msg("glass: " + state);
                _lastState = state;

                // A fresh 5 s for the detail line: its frame time is then all one state's.
                ResetCounters();
                _nextDetail = Time.realtimeSinceStartup + 5f;
            }

            if (!on)
            {
                GlassDraw.Active = false;
                VehicleGlassDraw.Active = false;
                Props.PropGlassDraw.Active = false;
                // The cards walk the glass's map and read its sky: none without the glass, the water's either
                // (kept for a passing state -- an info view, the Snapshot tool -- freed when switched off).
                if (!ReferenceEquals(state, _cardsOffFor))
                {
                    _cardsOffFor = state;
                    StopCards("the glass is " + state, !p.Enabled || failed);
                }
                if (_wasOn)
                {
                    // Off means nothing runs: the copy leaves the camera, the cube is freed, the
                    // camera's depth texture is as the game had it.
                    _screen.Detach();
                    _sky.Release();
                    RestoreCamera();
                    _wasOn = false;
                }
                _status = Localization.Get(failed ? "Status.Failed" : p.Enabled ? (infoView ? "Status.InfoView" :
                    (snapshot ? "Status.Snapshot" : "Status.Waiting")) : "Status.Off");

                // The frame time without the glass, for the same line with it: what the glass costs.
                if (Log.Detailed && Time.realtimeSinceStartup >= _nextDetail)
                {
                    _nextDetail = Time.realtimeSinceStartup + 5f;
                    float offMs = _frames > 0 ? 1000f * _frameTime / _frames : 0f;
                    Log.Detail("glass: 5 s with the glass " + state + ": frame " + offMs.ToString("F1") + " ms");
                    ResetCounters();
                }
                else if (!Log.Detailed && _frames >= 300)
                {
                    ResetCounters();
                }
                return;
            }

            _wasOn = true;
            GlassDraw.Tiers = Settings.IsInitialised ? Settings.GlassDistance.value : 2;
            ProceduralBuildings.Refresh();
            BindAtlases();
            _lodGlass.Update(p, _atlasXys, _util);
            WatchCamera(camera);

            GlassFrame f = ReadFrame(camera);
            int size = Settings.IsInitialised ? Settings.SkyCubePixels : 128;
            int rate = Settings.IsInitialised ? Settings.SkyCubeRate.value : 1;
            float dusk = GlassUniforms.Dusk(f.SunY, p.SkyDusk);
            _sky.Update(p.SkySource == 0, size, rate, Shader.GetGlobalTexture("_EnvironmentCubemap"), camera.transform.position, p.SkyClouds,
                        true, _util, dusk);
            if (_sky.DrawsClouds != _loggedClouds)
            {
                _loggedClouds = _sky.DrawsClouds;
                Log.Msg(_loggedClouds
                    ? "glass: the live sky carries Volumetric Weather's clouds ('" + GlassSky.CloudVolumeName + "' drawn into it)"
                    : "glass: the live sky has no clouds (" + (p.SkySource != 0 ? "the game's map is the sky" :
                        (!p.SkyClouds ? "switched off" : "no '" + GlassSky.CloudVolumeName + "' drawing")) + ")");
            }
            if (_sky.DrawsDistantClouds != _loggedDistant)
            {
                _loggedDistant = _sky.DrawsDistantClouds;
                Log.Msg(_loggedDistant
                    ? "glass: the live sky carries Volumetric Weather's distant clouds ('" + GlassSky.DistantCloudsName + "' drawn into it)"
                    : "glass: the live sky has no distant clouds (" + (p.SkySource != 0 ? "the game's map is the sky" :
                        (!p.SkyClouds ? "switched off" : "no '" + GlassSky.DistantCloudsName + "' drawing")) + ")");
            }
            f.CubeMips = _sky.Mips;
            f.CloudAlpha = _sky.CloudAlpha;

            // The buildings round the view, from above: what a reflection meets off screen.
            if (p.Neighbours)
                _heights.Update(_look, Time.realtimeSinceStartup);
            _heights.Map.Bind(p.Neighbours);

            // The facade cards (a trial): the buildings the walk meets show their own facade -- in the
            // glass, and in the water (whose controller asks FacadeCardsGame.WaterLive). Before the
            // screen's update: the reflection's buffer is recorded there, the cards' level in it.
            _cardsOffFor = null;
            if (_cards != null)
            {
                _cards.Frame(p, _heights, camera);
                _heights.CollectStanding = _cards.WantsStanding;
            }

            // Each building asset's own glass, on every copy of it (RealGlassAssets.xml over its
            // creator's RealGlassDefinition.xml), and every building's kind of rooms while there are
            // rooms anywhere.
            bool roomsWanted = p.Interiors || PerAsset.AssetRecords.AnyInteriorsOn;
            bool rooms = RoomsReady(roomsWanted) && roomsWanted;
            _assets.Update(p, rooms);
            // Each asset's windows measured: for its rooms, and for its own threshold (rooms or not).
            _roomFit.Update(rooms || p.ThresholdAuto, rooms, _util);

            // Dirt on the panes, the Glass tab's or an asset's own: its textures read the first time
            // any is wanted, kept for the city.
            f.DirtReady = DirtReady(p.Dirt > 0f || PerAsset.AssetRecords.AnyDirt);
            if (f.DirtReady)
                _dirt.Bind();
            // Dynamic dirt: rain washes it off, it builds up again (counted whenever it is on, so the
            // level is right the moment dirt is turned up).
            if (p.DirtDynamic)
                DirtWeather.Update();
            f.DirtBuiltUp = DirtWeather.Level;

            GlassUniforms.Apply(p, f, _quality);
            Interiors.Apply(p, f, _rooms, rooms);
            string roomsState = !roomsWanted ? "off" : (rooms ? "on" + (p.Interiors ? "" : " (only the assets that have them)") +
                (p.InteriorsByDay ? ", day and night" : ", at night") : "wanted, but no pictures");
            if (roomsState != _loggedRooms)
            {
                _loggedRooms = roomsState;
                Log.Msg("interiors: " + roomsState);
            }
            bool copied = _screen.Update(camera, _util, p.Finished, _quality.HiZLevels);
            if (!copied)
            {
                // Without the copy nothing on screen can be reflected: the trace and the mirrored
                // fill fall back to the city colour / sky (the shader reads what is bound).
                Shader.SetGlobalVector("_RGTrace", new Vector4(0f, 0f, 1f, 1f));
            }
            else if (_screen.HiZLevels != _loggedHiZ)
            {
                _loggedHiZ = _screen.HiZLevels;
                Log.Msg(_loggedHiZ > 0
                    ? "glass: the reflections trace over a hierarchical depth of " + _loggedHiZ + " levels"
                    : "glass: no hierarchical depth (" + (_quality.HiZLevels <= 0 ? "not in the budget" : "no float render target here") +
                      "); the plain trace");
            }

            GlassDraw.Active = true;
            ProceduralBuildings.Draw();
            UpdateVehicles();
            UpdateProps();
            ProceduralBuildings.DrawProps();
            _status = "";
            _lastNear = GlassDraw.NearDraws;
            _lastLod = GlassDraw.LodDraws;
            _lastFar = GlassDraw.GroupDraws;

            if (Log.Detailed && Time.realtimeSinceStartup >= _nextDetail)
            {
                _nextDetail = Time.realtimeSinceStartup + 5f;
                float ms = _frames > 0 ? 1000f * _frameTime / _frames : 0f;
                Log.Detail("glass: 5 s: " + GlassDraw.NearDraws / 5 + " near, " + GlassDraw.LodDraws / 5 + " middle, " +
                           GlassDraw.GroupDraws / 5 + " far" + (ProceduralBuildings.Entries.Count > 0 ? ", " + GlassDraw.ProceduralDraws / 5 +
                               " Procedural Objects" : "") + " draws a second (" + GlassDraw.NearSkipped / 5 + " near skipped: not glass-capable); " +
                           "frame " + ms.ToString("F1") + " ms; copy " + (copied ? _screen.Width + "x" + _screen.Height : "OFF " + _screen.Problem) +
                           (copied ? (_screen.Finished ? " + the finished picture" : " (no finished picture)") : "") +
                           "; sky " + (p.SkySource == 0 ? _sky.FacesDone + "/6 faces " + _sky.Mips + " mips" + (_sky.DrawsClouds ? " with clouds" : "") +
                               (_sky.DrawsDistantClouds ? " + distant clouds" : "") + (_sky.CloudAlpha ? ", x" + dusk.ToString("F2") + " at dusk" : "") +
                               (_sky.Problem.Length > 0 ? " " + _sky.Problem : "") : "the game's map") +
                           "; daylight " + GlassUniforms.Daylight(f.SunY).ToString("F2") + ", sun " +
                           (Mathf.Asin(Mathf.Clamp(f.SunY, -1f, 1f)) * Mathf.Rad2Deg).ToString("F1") + " deg (" + f.SunR.ToString("F2") + " " + f.SunG.ToString("F2") + " " + f.SunB.ToString("F2") +
                           "), ground " + f.GroundY.ToString("F0") + " m, " + f.Temperature.ToString("F0") + " C; " +
                           (p.Neighbours ? (_heights.Map.Live ? "city heights " + _heights.Map.LiveCount + " buildings, highest roof " +
                               _heights.Map.LiveTop.ToString("F0") + " m, " + _heights.LastMs.ToString("F1") + " ms over " + _heights.LastFrames + " frames"
                               : "city heights being drawn") : "no city heights") +
                           "; hi-z " + (copied ? _screen.HiZLevels + " levels" : "-") +
                           "; reflection pass " + (_prePass == null ? "none" : (_prePass.Attached ? _prePass.Recorded / 5 + " glass draws a second" : "OFF " + _prePass.Problem)) +
                           (_cards != null ? "; " + _cards.Describe() : "") +
                           "; rooms " + (rooms ? DescribeRooms(p, f) + "; " + _roomFit.Describe() : "off") +
                           "; " + _lodGlass.Describe() +
                           "; vehicles " + (VehicleGlassDraw.Active ? VehicleGlassDraw.Vehicles / 5 + " a second in " + VehicleGlassDraw.Draws / 5 +
                               " draws (materials seen: " + VehicleGlassDraw.DescribeShaders() + ")" : "off") +
                           "; props " + (Props.PropGlassDraw.Active ? Props.PropGlassDraw.Props / 5 + " a second in " + Props.PropGlassDraw.Draws / 5 +
                               " draws (" + Props.PropGlassDraw.DescribeChosen() + ")" : "off") +
                           "; shaders seen: " + GlassDraw.DescribeShaders());
                ResetCounters();
            }
            else if (!Log.Detailed && _frames >= 300)
            {
                ResetCounters();
            }
        }

        /// <summary>
        /// Every picture an editor takes of its asset goes through the Snapshot tool: the
        /// snapshots, thumbnails and tooltips a creator takes, and the ones the asset editor takes
        /// itself on save (SaveAssetPanel.TakeCompulsoryShot switches to this tool first; IL,
        /// 2026-09-29). They ship with the asset and every player sees them, most without this
        /// mod, so they show the asset as the game draws it.
        /// </summary>
        private static bool IsTakingSnapshots()
        {
            ToolController tools = ToolsModifierControl.toolController;
            return tools != null && tools.CurrentTool is SnapshotTool;
        }

        // The cards off (never throws: it runs on the glass's way out, the error path included).
        private void StopCards(string why, bool free = true)
        {
            if (_cards == null)
                return;
            try
            {
                if (free)
                {
                    _cards.Stop(why);
                    _heights.CollectStanding = false;
                }
                else
                {
                    _cards.Pause(why);
                }
            }
            catch (Exception e)
            {
                FacadeCardsGame.WaterLive = false;
                Log.Warn("facade cards: stopping them: " + e.Message);
            }
        }

        private void ResetCounters()
        {
            GlassDraw.NearDraws = GlassDraw.LodDraws = GlassDraw.GroupDraws = GlassDraw.NearSkipped = GlassDraw.ProceduralDraws = 0;
            if (_prePass != null)
                _prePass.Recorded = 0;
            VehicleGlassDraw.Vehicles = VehicleGlassDraw.Draws = 0;
            Props.PropGlassDraw.Props = Props.PropGlassDraw.Draws = 0;
            _lastNear = _lastLod = _lastFar = 0;
            _frames = 0;
            _frameTime = 0f;
        }

        /// <summary>
        /// The trace reads _CameraDepthTexture: the camera must make one (it usually does; if not,
        /// ask for it, and give the camera back as it was on the way out). Logged once per camera:
        /// the copy is taken after the DEFERRED final pass, so another path is worth knowing about.
        /// </summary>
        private void WatchCamera(Camera camera)
        {
            if (camera == _watched)
                return;

            RestoreCamera();
            _watched = camera;
            _originalDepthMode = camera.depthTextureMode;
            if ((camera.depthTextureMode & DepthTextureMode.Depth) == 0)
                camera.depthTextureMode |= DepthTextureMode.Depth;
            Log.Msg("glass: camera '" + camera.name + "' " + camera.actualRenderingPath + ", HDR " + camera.allowHDR + ", depth texture " +
                    ((_originalDepthMode & DepthTextureMode.Depth) != 0 ? "already made" : "ASKED FOR (was " + _originalDepthMode + ")") +
                    ", " + camera.pixelWidth + "x" + camera.pixelHeight + ", fov " + camera.fieldOfView.ToString("F0") +
                    (camera.actualRenderingPath != RenderingPath.DeferredLighting ? " -- NOT the legacy deferred path the copy is timed for" : ""));
        }

        private void RestoreCamera()
        {
            if (_watched != null && (_originalDepthMode & DepthTextureMode.Depth) == 0)
                _watched.depthTextureMode = _originalDepthMode;
            _watched = null;
        }

        /// <summary>The middle and far tiers read the game's shared LOD atlases; they are made once per load.</summary>
        private void BindAtlases()
        {
            BuildingManager bm = Singleton<BuildingManager>.instance;
            if (bm.m_lodRgbAtlas == _atlasRgb && bm.m_lodXysAtlas == _atlasXys && bm.m_lodAciAtlas == _atlasAci)
                return;

            _atlasRgb = bm.m_lodRgbAtlas;
            _atlasXys = bm.m_lodXysAtlas;
            _atlasAci = bm.m_lodAciAtlas;
            // The reflection pass's own materials of the two tiers too: its draws are these tiers' draws
            // (the game's block of arrays, or none), so the atlases reach it from its materials alone.
            // Without them it read blank textures: no reflection at all above a threshold of ~78%.
            Material preLod = _prePass != null ? _prePass.Material(GlassPrePass.TierLod) : null;
            Material preGroup = _prePass != null ? _prePass.Material(GlassPrePass.TierGroup) : null;
            foreach (Material m in new[] { _lod, _group, preLod, preGroup })
            {
                if (m == null)
                    continue;
                m.SetTexture("_MainTex", _atlasRgb);
                m.SetTexture("_XYSMap", _atlasXys);
                m.SetTexture("_ACIMap", _atlasAci);
            }

            Log.Msg("glass: LOD atlases " + Describe(_atlasRgb) + " / " + Describe(_atlasXys) + " / " + Describe(_atlasAci));
        }

        private static string Describe(Texture t)
        {
            var t2 = t as Texture2D;
            return t == null ? "none" : t.name + " " + t.width + "x" + t.height + (t2 != null ? " " + t2.format + " " + t2.mipmapCount + " mips" : "");
        }

        /// <summary>The scene as the glass needs it: the game's sun, its ambient, the ground, the weather.</summary>
        private GlassFrame ReadFrame(Camera camera)
        {
            GlassFrame f = new GlassFrame();
            Vector3 toSun = new Vector3(0f, -1f, 0f);
            Color sun = Color.black;
            DayNightProperties dn = DayNightProperties.instance;
            if (dn != null && dn.m_SunLight != null)
            {
                toSun = -dn.m_SunLight.forward;
                Light light = dn.m_SunLight.GetComponent<Light>();
                if (light != null && light.enabled)
                    sun = light.color.linear * light.intensity;
            }

            f.SunX = toSun.x;
            f.SunY = toSun.y;
            f.SunZ = toSun.z;
            f.SunR = sun.r;
            f.SunG = sun.g;
            f.SunB = sun.b;
            Color sky = RenderSettings.ambientSkyColor.linear, ground = RenderSettings.ambientGroundColor.linear;
            f.AmbSkyR = sky.r;
            f.AmbSkyG = sky.g;
            f.AmbSkyB = sky.b;
            f.AmbGroundR = ground.r;
            f.AmbGroundG = ground.g;
            f.AmbGroundB = ground.b;

            // The ground where the camera looks: what a far block's floors and the reflected
            // street are measured from.
            Vector3 look = camera.transform.position;
            CameraController cc = camera.GetComponent<CameraController>();
            if (cc != null)
                look = cc.m_currentPosition;
            _look = look;
            f.GroundY =Singleton<TerrainManager>.exists ? Singleton<TerrainManager>.instance.SampleDetailHeight(look) : 0f;
            f.Temperature = Singleton<WeatherManager>.exists ? Singleton<WeatherManager>.instance.m_currentTemperature : 15f;
            f.ScreenHeight = camera.pixelHeight;
            f.ColorMapWidth = RenderManager.OBJECT_COLORMAP_WIDTH;
            f.ColorMapHeight = RenderManager.OBJECT_COLORMAP_HEIGHT;

            // The game's clock, for the rooms' lights: the hour (as the sky has it), and the hours
            // since day 0, wrapped at 256 days. It runs with the game's speed and stops when the game
            // is paused.
            if (Singleton<SimulationManager>.exists)
            {
                SimulationManager sm = Singleton<SimulationManager>.instance;
                f.Hour = sm.m_currentDayTimeHour;
                f.Clock = (float)Clock(sm);
            }
            return f;
        }

        /// <summary>
        /// The rooms' clock in game hours. It starts as SimulationManager.Update's own sum (frame +
        /// day-time offset, a day being DAYTIME_FRAMES frames; IL 2026-10-07) and is then counted
        /// up frame by frame at the day's CURRENT length, never recomputed from the total.
        /// DAYTIME_FRAMES is a static field, not a constant: Real Time rewrites it, and
        /// m_dayTimeOffsetFrames with it, whenever its day or night speed changes (IL of Real Time,
        /// 2026-10-14). The whole sum over a new day length would jump by days and switch every
        /// room's light at once; counted up, a new speed only changes how fast the rooms change.
        /// </summary>
        private double Clock(SimulationManager sm)
        {
            double frame = (double)sm.m_referenceFrameIndex + sm.m_referenceTimer;
            double day = Math.Max(SimulationManager.DAYTIME_FRAMES, 1u);
            double step = frame - _clockFrame;
            if (_clockFrame < 0.0 || step < 0.0)
                _clockHours = ((double)(sm.m_referenceFrameIndex + sm.m_dayTimeOffsetFrames) + sm.m_referenceTimer) * 24.0 / day;
            else
                _clockHours += step * 24.0 / day;

            _clockFrame = frame;
            _clockHours -= Interiors.ClockWrap * Math.Floor(_clockHours / Interiors.ClockWrap);
            return _clockHours;
        }

        /// <summary>
        /// The rooms' pictures, read from the DLL the first time rooms are wanted (the switch, or an
        /// asset of their own), kept for the city. False while there are none (and why, once).
        /// </summary>
        private bool RoomsReady(bool wanted)
        {
            if (!wanted || _roomsTried)
                return _rooms.Loaded;

            _roomsTried = true;
            byte[] night = ShaderBundle.ReadResource(RoomsNightResource), day = ShaderBundle.ReadResource(RoomsDayResource);
            if (_rooms.Load(night, day))
            {
                Log.Msg("interiors: " + _rooms.Tiles + " rooms loaded (" + _rooms.Night.width + "x" + _rooms.Night.height + " by night, " +
                        _rooms.Day.width + "x" + _rooms.Day.height + " by day; " + DescribePools() + ")");
            }
            else
            {
                Log.Warn("interiors: the rooms' pictures could not be loaded (" + _rooms.Problem + "): no rooms behind the glass");
            }
            return _rooms.Loaded;
        }

        /// <summary>
        /// The dirt's textures, read from the DLL the first time dirt is wanted (the Glass tab's Dirt, or
        /// an asset's own), kept for the city. False while there are none (and why, once).
        /// </summary>
        private bool DirtReady(bool wanted)
        {
            if (!wanted || _dirtTried)
                return _dirt.Loaded;

            _dirtTried = true;
            if (_dirt.Load(ShaderBundle.ReadResource(DirtGrimeResource), ShaderBundle.ReadResource(DirtRunsResource)))
            {
                Log.Msg("dirt: textures loaded (" + _dirt.Grime.width + "x" + _dirt.Grime.height + " " + _dirt.Grime.format + ", " +
                        _dirt.Runs.width + "x" + _dirt.Runs.height + " " + _dirt.Runs.format + ")");
            }
            else
            {
                Log.Warn("dirt: its textures could not be loaded (" + _dirt.Problem + "): no dirt on the glass");
            }
            return _dirt.Loaded;
        }

        private string DescribePools()
        {
            string[] names = { "homes", "offices", "shops", "hotels", "leisure", "public", "industry", "spare" };
            string s = "";
            for (int k = 0; k < RoomAtlas.PoolCount; k++)
            {
                if (_rooms.Pools[k].y > 0.5f)
                    s += (s.Length > 0 ? ", " : "") + (int)_rooms.Pools[k].y + " " + names[k];
            }
            return s;
        }

        /// <summary>The settings in one line: what the log's "glass:" lines are read against.</summary>
        public static string DescribeSettings(GlassParams p)
        {
            return "glass " + (p.Enabled ? "on" : "off") + ", reflect " + Pct(p.Reflectivity) + "..." + Pct(p.Glancing) + " (silvery edge-on " + Pct(p.EdgeSilver) +
                   "), clarity " + Pct(p.Clarity) +
                   ", tint #" + Hex(p.TintR) + Hex(p.TintG) + Hex(p.TintB) + " own " + Pct(p.OwnColour) + ", painted detail " + Pct(p.PaintedDetail) +
                   ", strength " + Pct(p.Strength) +
                   (p.Dirt > 0f ? ", dirt on " + Pct(p.Dirt) + " of buildings" + (p.DirtDynamic ? " (washed by rain)" : "") : "") +
                   ", glass above " + Pct(p.Threshold) + (p.ThresholdAuto ? " (detected per asset)" : "") + (p.NightWindows ? " + night windows" : "") +
                   (p.LodGlass ? ", LODs take their model's glass where theirs differs" : ", LODs keep their own glass") + (p.ShowMask ? ", SHOWING THE MASK" : "") +
                   "; trace " + (p.Trace ? "q" + p.Quality : "off") + (p.Finished ? " of the finished picture" : " of the copy") +
                   (p.Neighbours ? " + city heights" : "") + (p.SteadyGlints ? ", steady glints" : "") +
                   ", brightness " + Pct(p.ReflectionBrightness) + ", sky " + (p.SkySource == 0 ? (p.SkyClouds ? "live with clouds" : "live") +
                       ", dims at dusk " + Pct(p.SkyDusk) : "game map") +
                   ", fill " + p.OffscreenFill + ", glint " + Pct(p.SunGlint) + ", smooth " + Pct(p.Smoothness) +
                   "; panes " + p.PaneWidth.ToString("F1") + " x " + p.FloorHeight.ToString("F1") + " m, pillow " + Pct(p.Pillow) + ", tilt " + Pct(p.Tilt) +
                   ", roller " + Pct(p.RollerWave) + (p.PaneWeather ? " (weather)" : "") + (p.PanesFollowWindows ? ", following the windows" : "") +
                   "; behind the glass: " + (p.Interiors
                       ? "ROOMS " + (p.InteriorsByDay ? "day (" + Pct(p.InteriorsDayOpacity) + ") and night" : "at night") + ", depth " + Pct(p.InteriorsDepth) +
                         (p.InteriorsFollowIllumination ? ", lit by the assets' illumination maps (an asset saying no" : "") + ", lit " + Pct(p.InteriorsLit) +
                         (p.InteriorsLateNight ? " following the hour" : " all night") + ", switching " + Pct(p.InteriorsSwitching) +
                         (p.InteriorsFollowIllumination ? ")" : "") +
                         ", brightness " + Pct(p.InteriorsBrightness) + ", coloured " + Pct(p.InteriorsColoured) + ", curtains " + Pct(p.InteriorsCurtains)
                       : "a dark inside by day, the game's own windows by night");
        }

        /// <summary>The rooms now, for the 5 s line: the hour, the shares lit, how long a room keeps its light.</summary>
        private static string DescribeRooms(GlassParams p, GlassFrame f)
        {
            Vector4 s = Interiors.Shares(p, f.Hour);
            float hours = Interiors.HoursPerState(p.InteriorsSwitching);
            return "at " + Mathf.FloorToInt(f.Hour) + ":" + Mathf.FloorToInt((f.Hour % 1f) * 60f).ToString("00") +
                   ", lit: homes " + Pct(s.x) + ", offices " + Pct(s.y) + ", shops " + Pct(s.z) + ", others " + Pct(s.w) +
                   (hours > 0f ? ", a room's light changes every " + hours.ToString("F1") + " game hours" : ", the pattern stands still") +
                   " (clock " + f.Clock.ToString("F2") + " h)";
        }

        private static string Pct(float v)
        {
            return Mathf.RoundToInt(v * 100f) + "%";
        }

        private static string Hex(float c)
        {
            return Mathf.Clamp(Mathf.RoundToInt(c * 255f), 0, 255).ToString("X2");
        }

        /// <summary>
        /// The vehicles' glass this frame (the buildings' glass is on): its switch, and its look's
        /// numbers for VehicleGlass.shader (VehicleGlassLook: the Vehicles tab's tinted share, reflections and
        /// clear coat; glint, edge-on and smoothness as physics has them). One line when it changes.
        /// </summary>
        private void UpdateVehicles()
        {
            bool wanted = Settings.IsInitialised && Settings.VehicleGlass.value;
            bool on = wanted && _vehicle != null && !VehicleGlassDraw.Failed;
            if (on)
            {
                VehicleGlassLook.Apply(Settings.VehicleTinted.value, 1f, Settings.VehicleReflections.value, 1f, 1f, VehicleGlassLook.Roughness,
                    true, Settings.VehicleCoat.value);
            }
            VehicleGlassDraw.Active = on;

            string state = on ? "on (tinted windows on " + (Settings.VehicleTinted.value * 100f).ToString("F0") + "% of them, reflections " +
                                (Settings.VehicleReflections.value * 100f).ToString("F0") + "%, clear coat " +
                                (Settings.VehicleCoat.value * 100f).ToString("F0") + "%)"
                : !wanted ? "off" : (VehicleGlassDraw.Failed ? "stopped after an error (above)" : "no shader");
            if (state != _loggedVehicles)
            {
                _loggedVehicles = state;
                Log.Msg("vehicles: glass " + state);
            }
        }

        /// <summary>
        /// The props' glass and water this frame (the buildings' glass is on): on while a prop this
        /// city has loaded is chosen (RealGlassAssets.xml's props), with the water's ripple texture
        /// (the Water tab's moving one while it holds the water, else the game's). One line when it
        /// changes.
        /// </summary>
        private void UpdateProps()
        {
            bool can = _propOpaque != null && _propSeeThrough != null && !Props.PropGlassDraw.Failed;
            if (can)
                Props.PropGlassDraw.RefreshTable();
            // A draw hook that could not be applied: nothing half drawn, and the player is told once --
            // only if a prop was chosen (nobody else misses anything).
            bool hooks = !Patcher.PropsFailed;
            if (can && !hooks && Props.PropGlassDraw.AnyChosen)
                ErrorNotice.Report(ErrorNotice.Props);
            bool on = can && hooks && Props.PropGlassDraw.AnyChosen;
            if (on)
            {
                Texture ripples = Water.WaterController.ShadedRipplesNow ?? Shader.GetGlobalTexture(IdGameRipples);
                if (ripples != null)
                {
                    _propOpaque.SetTexture(IdPropRipple, ripples);
                    _propSeeThrough.SetTexture(IdPropRipple, ripples);
                }
                Props.PropGlassDraw.SetWaterTabColour(Water.WaterController.TabColour(), Water.WaterController.TabBrightness());
            }
            Props.PropGlassDraw.Active = on;

            string state = on ? "on (" + Props.PropGlassDraw.DescribeChosen() + ")"
                : _propOpaque == null ? "no shader"
                : Props.PropGlassDraw.Failed ? "stopped after an error (above)"
                : !hooks ? "off: their draw hooks could not be applied (" + Patcher.Failed + ")"
                : "none chosen in this city";
            if (state != _loggedProps)
            {
                _loggedProps = state;
                Log.Msg("props: glass and water " + state);
            }
        }

        private void OnDestroy()
        {
            GlassDraw.Active = false;
            GlassDraw.Near = GlassDraw.Lod = GlassDraw.Group = null;
            GlassDraw.PrePass = null;
            GlassDraw.Reset();
            VehicleGlassDraw.Reset();
            VehicleGlassDraw.Glass = null;
            Props.PropGlassDraw.Reset();
            Props.PropGlassDraw.Opaque = Props.PropGlassDraw.SeeThrough = null;
            StopCards("the city closed");
            _cards = null;
            _screen.Detach();
            _screen.PrePass = null;
            if (_prePass != null)
                _prePass.Destroy();
            _prePass = null;
            _sky.Destroy();
            _heights.Destroy();
            _assets.Destroy();
            _lodGlass.Destroy();
            _rooms.Destroy();
            _dirt.Destroy();
            _roomFit.Destroy();
            ProceduralBuildings.Reset();
            RestoreCamera();
            foreach (Material m in new[] { _near, _lod, _group, _util, _vehicle, _propOpaque, _propSeeThrough })
            {
                if (m != null)
                    Destroy(m);
            }

            _status = "";
            if (Instance == this)
                Instance = null;
            Log.Msg("glass: renderer stopped");
        }
    }
}
