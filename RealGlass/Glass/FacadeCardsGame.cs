using System;
using System.Collections.Generic;
using System.Diagnostics;
using ColossalFramework;
using UnityEngine;

namespace RealGlass.Glass
{
    /// <summary>
    /// The facade cards in the game (a trial, 2026-11-13; FacadeCards.cs has the cards, CardSchedule.cs
    /// which asset gets one when). Owned by GlassController: the cards walk ITS city map (CityHeights) and
    /// read ITS uniforms and sky, so they run only while the glass is on -- the water's cards too (the
    /// water's own controller asks <see cref="WaterLive"/> each frame).
    /// </summary>
    /// <remarks>
    /// <para>Built only when the loaded bundle holds the bake and the gain shaders (the Windows bundle;
    /// the Mac and Linux bundles are rebuilt at a release: until then the cards stay off there, one log
    /// line), with three render targets at once and float targets.</para>
    /// <para>Each frame: the assets standing in the newest city map (main buildings only: a sub-building's
    /// cells name its root, whose box the ray then misses -- the guess stays; Procedural Objects' buildings
    /// keep the guess; a building under construction, abandoned or burned down has no record), nearest
    /// first, baked within a budget (BakesPerFrame, BakeMs of CPU); the records written again when a map
    /// or a bake lands; the level from the frame measured in the reflection's buffer (FacadeCardGain);
    /// the readers' keyword on their own materials. A building without its card yet keeps today's guess.</para>
    /// <para>Left out of a card (reported): the building's own colour variation (the asset's _MainTex
    /// alone), the palette of its lit windows (one warm lamp colour), its sub-meshes' other materials (the
    /// main mesh's material is used for every sub-mesh), its state (no record: the guess).</para>
    /// </remarks>
    internal sealed class FacadeCardsGame
    {
        /// <summary>The water's cards may draw this frame (read by WaterController.CityFrame).</summary>
        public static bool WaterLive;

        public const int BakesPerFrame = 2;
        public const int BakesWhenBehind = 6;     // when many wait, within the same CPU cap
        public const int Behind = 50;
        public const float BakeMs = 1f;

        private readonly GlassPrePass _prePass;
        private readonly Shader _bakeShader, _gainShader;
        private readonly string _unavailable;
        private FacadeCards _cards;
        private FacadeCardGain _gain;
        private CardSchedule _schedule;
        private int _mapVersion = -1;
        private bool _recordsDirty;
        private bool _failed;
        private string _state = "";
        private readonly List<string> _keys = new List<string>();
        private readonly List<float> _distances = new List<float>();
        private readonly Dictionary<string, BuildingInfo> _infos = new Dictionary<string, BuildingInfo>();
        private readonly Stopwatch _watch = new Stopwatch();
        private System.Action<UnityEngine.Rendering.CommandBuffer, Camera, int, int> _measure;

        // The 5 s line's counters.
        private int _bakes, _bakeFailures, _gcCount;
        private double _bakeMs, _gcMs;
        private float _firstWaitingSince = -1f, _allBakedAfter = -1f;

        public FacadeCardsGame(GlassPrePass prePass)
        {
            _prePass = prePass;
            if (prePass == null)
                _unavailable = "no reflection pass";
            else if (!ShaderBundle.Has(ShaderBundle.FacadeCardBake) || !ShaderBundle.Has(ShaderBundle.FacadeCardGain))
                _unavailable = "this platform's shader bundle has no cards yet (" + ShaderBundle.BundleFor(Application.platform) + ": built at the next release)";
            else if (SystemInfo.supportedRenderTargetCount < 3)
                _unavailable = "fewer than three render targets at once";
            else if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
                _unavailable = "no float render targets";
            if (_unavailable == null)
            {
                _bakeShader = ShaderBundle.Get(ShaderBundle.FacadeCardBake);
                _gainShader = ShaderBundle.Get(ShaderBundle.FacadeCardGain);
            }
            else
            {
                Log.Msg("facade cards: unavailable (" + _unavailable + "): the rows do nothing");
            }
            WaterLive = false;
        }

        public bool Live
        {
            get { return _cards != null; }
        }

        /// <summary>The city map should carry the list of main buildings (while the cards are wanted).</summary>
        public bool WantsStanding { get; private set; }

        /// <summary>
        /// One frame with the glass on, after GlassUniforms.Apply and before GlassScreen.Update (which
        /// records the reflection's buffer this frame).
        /// </summary>
        public void Frame(GlassParams p, CityHeights heights, Camera camera)
        {
            // The water's cards have no row of their own (the author's call, 2026-11-13: the row "City
            // beyond the screen" taken out): on wherever the water reflects the city. With "Neighbors'
            // facades" off they still run for the water alone (the atlas is made for them).
            bool waterRow = Settings.IsInitialised && Settings.WaterEnabled.value && Settings.WaterCity.value;
            bool wantGlass = p.CityCards && p.Neighbours;
            bool wantWater = waterRow && p.Neighbours;
            string why = _unavailable ?? (_failed ? "stopped after an error (above)" :
                (!wantGlass && !wantWater ? (p.Neighbours ? "Neighbors' facades off and no water reflections" : "the buildings round are off (Reflect the buildings around)") : null));
            WantsStanding = why == null;
            if (why != null)
            {
                // Once per reason (this runs every frame: no string built while nothing changes).
                if (_cards != null || !ReferenceEquals(why, _stoppedFor))
                    SafeStop(why);
                _stoppedFor = why;
                return;
            }
            _stoppedFor = null;

            try
            {
                if (_cards == null && !Start())
                    return;

                FacadeCards.SetReader(_prePass.Reflection, wantGlass);
                WaterLive = wantWater;
                if (_prePass.BeforeReflection == null)
                    _prePass.BeforeReflection = _measure;

                if (heights.MapVersion != _mapVersion && heights.Map.Live)
                {
                    _mapVersion = heights.MapVersion;
                    // A map begun before the cards asked carries no list: ask for a fresh one at once.
                    if (heights.LiveCollected)
                        TakeMap(heights.LiveStanding, camera.transform.position);
                    else
                        heights.Renew();
                }

                Bake(p);

                if (_recordsDirty && heights.Map.Live)
                {
                    WriteRecords(heights);
                    _recordsDirty = false;
                }

                _cards.Bind();
                Light();
                FacadeCards.SetMode(true, false);
                FacadeCards.SetNight(false);
                FacadeCardGain.Apply(true, 20f, 0.25f, 4f);

                int code = (wantGlass ? 1 : 0) + (wantWater ? 2 : 0) + (waterRow ? 4 : 0) + (p.Finished ? 8 : 0);
                if (code != _liveCode)
                {
                    _liveCode = code;
                    SetState("live: " + (wantGlass ? "the glass" : "") + (wantGlass && wantWater ? " and " : "") + (wantWater ? "the water" : "") +
                             (waterRow && !wantWater ? " (the water's cards need the buildings round)" : "") +
                             (p.Finished ? "" : "; no finished picture (Reflect fog, clouds and lights off): the level stays the formula's"));
                }
            }
            catch (Exception e)
            {
                _failed = true;
                Log.Error("facade cards: stopped after an error; the reflections keep today's guess", e);
                SafeStop("stopped after an error (above)");
            }
        }

        // Stop that never throws (it runs on error paths): the cards' fault must not take the glass.
        private void SafeStop(string why)
        {
            try
            {
                Stop(why);
            }
            catch (Exception e)
            {
                WaterLive = false;
                _cards = null;
                _gain = null;
                Log.Warn("facade cards: stopping them: " + e.Message);
            }
        }

        private bool Start()
        {
            _cards = new FacadeCards(_bakeShader);
            // One 2048^2 atlas (with its two siblings and a depth: ~80 MB, as before): its top half in 32 x 64
            // tiles for the nearest assets (204 sharp cards), its bottom half in 16 x 32 tiles for the rest (819):
            // 1,023 cards for the memory that held 409 (his city: ~988 building assets).
            if (!_cards.Create(2048, 32, 64, 16, 32, 1024))
            {
                string problem = _cards.Problem;
                _cards.Destroy();
                _cards = null;
                _failed = true;
                SafeStop("the atlases could not be made: " + problem);
                return false;
            }
            _gain = new FacadeCardGain(_gainShader) { Keep = 0.9f, MatrixAtRender = true };
            _measure = (cb, cam, w, h) =>
            {
                _camera = cam;
                _gain.Record(cb, cam, w, h);
            };
            if (_preRender == null)
            {
                _preRender = PreRender;
                Camera.onPreRender += _preRender;
            }
            _schedule = new CardSchedule(_cards.FullCapacity, _cards.SmallCapacity);
            _mapVersion = -1;
            _recordsDirty = true;
            _firstWaitingSince = Time.realtimeSinceStartup;
            _allBakedAfter = -1f;
            Log.Msg("facade cards: atlases " + _cards.Size + "x" + _cards.Size + " (" + _cards.FullCapacity + " sharp + " + _cards.SmallCapacity +
                    " small slots, ~80 MB); baking " + BakesPerFrame + " a frame (up to " + BakesWhenBehind + " with " + Behind +
                    " or more waiting) within " + BakeMs.ToString("F1") + " ms CPU, nearest first");
            return true;
        }

        private Camera _camera;
        private Camera.CameraCallback _preRender;

        // The gain's camera, as it renders (the buffer was recorded in LateUpdate: a later move would
        // put every sample's world point elsewhere).
        private void PreRender(Camera camera)
        {
            if (camera != null && camera == _camera && _gain != null)
                Shader.SetGlobalMatrix("_RGGainInvVP", FacadeCardGain.InverseOf(camera));
        }

        /// <summary>Off for now (an info view, the Snapshot tool): the readers' keyword off and the measurement out; the cards kept.</summary>
        public void Pause(string why)
        {
            WaterLive = false;
            if (_prePass != null)
            {
                FacadeCards.SetReader(_prePass.Reflection, false);
                if (_prePass.BeforeReflection == _measure)
                    _prePass.BeforeReflection = null;
            }
            FacadeCardGain.Apply(false, 20f, 0.25f, 4f);
            Shader.SetGlobalVector("_RGCardInfo2", Vector4.zero);
            if (_gain != null)
                _gain.Reset();
            SetState("paused (" + why + ")" + (_cards != null ? ", " + _schedule.Used + " cards kept" : ""));
        }

        /// <summary>Off: the readers' keyword off, the measurement out of the buffer, the atlases freed.</summary>
        public void Stop(string why)
        {
            WaterLive = false;
            if (_prePass != null)
            {
                FacadeCards.SetReader(_prePass.Reflection, false);
                if (_prePass.BeforeReflection == _measure)
                    _prePass.BeforeReflection = null;
            }
            FacadeCardGain.Apply(false, 20f, 0.25f, 4f);
            Shader.SetGlobalVector("_RGCardInfo2", Vector4.zero);
            if (_cards != null)
            {
                _cards.Destroy();
                _cards = null;
            }
            if (_gain != null)
            {
                _gain.Destroy();
                _gain = null;
            }
            if (_preRender != null)
            {
                Camera.onPreRender -= _preRender;
                _preRender = null;
            }
            _schedule = null;
            _mapVersion = -1;
            SetState("off (" + why + ")");
        }

        private string _stoppedFor;
        private int _liveCode = -1;

        private void SetState(string state)
        {
            if (!state.StartsWith("live", StringComparison.Ordinal))
                _liveCode = -1;
            if (state == _state)
                return;
            _state = state;
            Log.Msg("facade cards: " + state);
        }

        // A new city map: its assets, each with its nearest building's distance to the camera.
        private void TakeMap(List<CityHeights.Standing> standing, Vector3 eye)
        {
            var nearest = new Dictionary<string, float>();
            _infos.Clear();
            foreach (CityHeights.Standing s in standing)
            {
                if (s.Info == null || s.Info.m_mesh == null || s.Info.m_material == null)
                    continue;
                string key = s.Info.name;
                float d = (s.Position - eye).sqrMagnitude;
                float had;
                if (!nearest.TryGetValue(key, out had) || d < had)
                    nearest[key] = d;
                _infos[key] = s.Info;
            }
            _keys.Clear();
            _distances.Clear();
            foreach (KeyValuePair<string, float> kv in nearest)
            {
                _keys.Add(kv.Key);
                _distances.Add(kv.Value);
            }
            _schedule.SetStanding(_keys, _distances);
            _recordsDirty = true;
            if (_schedule.Waiting > 0 && _allBakedAfter >= 0f)
            {
                _firstWaitingSince = Time.realtimeSinceStartup;
                _allBakedAfter = -1f;
            }
        }

        // This frame's bakes, nearest first, within the budget.
        private void Bake(GlassParams p)
        {
            int waiting = _schedule.Waiting;
            int budget = waiting >= Behind ? BakesWhenBehind : BakesPerFrame;
            // A garbage collection the game (or anything) sets off inside this window stops the clock too:
            // counted apart, so the 5 s line tells the cards' own time from the collector's (2026-11-13: two
            // ~0.5 s periods with nothing baked, each after a settings change).
            int collections = GC.CollectionCount(0);
            _watch.Reset();
            _watch.Start();
            int done = 0;
            _cards.BeginBatch();
            while (done < budget && _watch.Elapsed.TotalMilliseconds < BakeMs)
            {
                List<CardSchedule.Job> jobs = _schedule.Next(1);
                if (jobs.Count == 0)
                    break;
                CardSchedule.Job job = jobs[0];
                BuildingInfo info;
                string why = _infos.TryGetValue(job.Key, out info) ? BakeOne(info, job.Slot, p) : "not in the map any more";
                _schedule.Done(job, why == null);
                if (why == null)
                    _bakes++;
                else
                    NoCard(job.Key, why);
                done++;
                _recordsDirty = true;
            }
            _cards.EndBatch();
            _watch.Stop();
            double ms = _watch.Elapsed.TotalMilliseconds;
            int collected = GC.CollectionCount(0) - collections;
            if (collected > 0)
            {
                _gcCount += collected;
                _gcMs += ms;
            }
            else
            {
                _bakeMs += ms;
            }
            if (_allBakedAfter < 0f && waiting == 0 && _keys.Count > 0)
            {
                _allBakedAfter = Time.realtimeSinceStartup - _firstWaitingSince;
                Log.Msg("facade cards: every asset in view has its card (" + _schedule.Used + " of " + _schedule.Capacity + " slots), " +
                        _allBakedAfter.ToString("F1") + " s after the first map");
            }
        }

        // Null when baked; else why not (the reasons are counted and each logged once with its first asset).
        private string BakeOne(BuildingInfo info, int slot, GlassParams p)
        {
            Mesh mesh = info.m_mesh;
            Material m = info.m_material;
            if (mesh == null)
                return "no mesh";
            if (m == null)
                return "no material";
            string shader = m.shader != null ? m.shader.name : "no shader";
            if (!m.HasProperty("_MainTex") || m.GetTexture("_MainTex") == null)
                return "no _MainTex (shader " + shader + ")";
            if (!m.HasProperty("_XYSMap") || m.GetTexture("_XYSMap") == null)
                return "no _XYSMap (shader " + shader + ")";
            if (mesh.vertexCount == 0)
                return "an empty mesh";
            Texture main = m.GetTexture("_MainTex");
            Texture xys = m.GetTexture("_XYSMap");
            Texture aci = m.HasProperty("_ACIMap") ? m.GetTexture("_ACIMap") : null;
            Vector2 scale = m.mainTextureScale, offset = m.mainTextureOffset;
            float threshold = AssetMap.ThresholdFor(PerAsset.AssetRecords.Of(info), info, p, _detected);
            Bounds b = mesh.bounds;
            return _cards.BakeInto(slot, mesh, main, xys, aci, new Vector4(scale.x, scale.y, offset.x, offset.y), b.min, b.max, threshold)
                ? null : "the bake refused it";
        }

        private static readonly Func<BuildingInfo, float> _detected = i => i != null ? GlassController.DetectedThresholdOf(i.name) : 0f;
        private readonly Dictionary<string, int> _noCard = new Dictionary<string, int>();

        private void NoCard(string asset, string why)
        {
            int n;
            _noCard.TryGetValue(why, out n);
            _noCard[why] = n + 1;
            _bakeFailures++;
            if (n == 0)
                Log.Msg("facade cards: no card for '" + asset + "': " + why + " (the first of its kind; the rest are counted in the 5 s line)");
        }

        private string DescribeNoCards()
        {
            if (_noCard.Count == 0)
                return "";
            var parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in _noCard)
                parts.Add(kv.Value + " " + kv.Key);
            return " (never: " + string.Join(", ", parts.ToArray()) + ")";
        }

        private void WriteRecords(CityHeights heights)
        {
            Vector2 centre = heights.Map.LiveCentre;
            _cards.BeginBuildings(centre.x, centre.y);
            foreach (CityHeights.Standing s in heights.LiveStanding)
            {
                if (s.Info == null)
                    continue;
                int slot = _schedule.SlotOf(s.Info.name);
                if (slot > 0)
                    _cards.SetBuilding(s.Id, s.Position, s.Heading, slot);
            }
            _cards.Upload();
        }

        // The light the game's building shader receives: the main light (the moon at night) on a face
        // square to it, the ambient's equator on walls and its sky on roofs (measured, round 2), a lit
        // window's lamp (the game's palette varies by building: one warm colour here).
        private static void Light()
        {
            Light main = Singleton<RenderManager>.exists ? Singleton<RenderManager>.instance.MainLight : null;
            Vector3 toLight = Vector3.up;
            Color light = Color.black;
            if (main != null && main.enabled)
            {
                toLight = -main.transform.forward;
                light = main.color.linear * main.intensity;
            }
            Color lamp = new Color(1f, 0.8f, 0.56f) * 1.5f;
            FacadeCards.SetLight(toLight, light, RenderSettings.ambientEquatorColor.linear, RenderSettings.ambientSkyColor.linear, lamp);
        }

        /// <summary>The 5 s line's part; the counters restart. Reads the gain back (only while detailed logging is on).</summary>
        public string Describe()
        {
            try
            {
                if (_cards == null)
                    return "facade cards " + (_state.Length > 0 ? _state : "off");
                string s = "facade cards " + _schedule.Used + "/" + _schedule.Capacity + " slots (" + _schedule.FullCount + " sharp), " + _bakes + " baked in " +
                           _bakeMs.ToString("F1") + " ms CPU" + (_bakeFailures > 0 ? " (" + _bakeFailures + " could not be)" : "") +
                           (_gcCount > 0 ? " + " + _gcMs.ToString("F0") + " ms in " + _gcCount + " garbage collection(s) that fell in the cards' window" : "") +
                           ", " + _schedule.Waiting + " waiting, " + _schedule.Failed + " never" + DescribeNoCards() +
                           "; " + FacadeCardGain.Describe(_gain.Read()) + " of " + (_gain.SamplesX * _gain.SamplesY) + " points";
                _bakes = _bakeFailures = 0;
                _bakeMs = 0.0;
                _gcCount = 0;
                _gcMs = 0.0;
                return s;
            }
            catch (Exception e)
            {
                return "facade cards: (not described: " + e.Message + ")";
            }
        }

        public void Destroy()
        {
            SafeStop("the city closed");
        }
    }
}
