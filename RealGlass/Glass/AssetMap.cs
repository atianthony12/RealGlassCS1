using System.Collections.Generic;
using ColossalFramework;
using RealGlass.PerAsset;
using UnityEngine;

namespace RealGlass.Glass
{
    /// <summary>
    /// Puts each building asset's own glass (RealGlassAssets.xml, <see cref="AssetFile"/>) on every
    /// copy of it in the city: the building's texel of <see cref="AssetTextures"/>, at the id its
    /// colour index names -- a building complex is its main building, as the game's colour index
    /// has it (BuildingAI.GetObjectColorIndex), so a complex follows its main building's asset.
    /// </summary>
    /// <remarks>
    /// After any change of an asset's glass (or of a global setting an asset's glass falls back on)
    /// a pass over the whole building buffer, a slice a frame; then a slow watch for buildings that
    /// went up or levelled up into another asset. Nothing at all while no asset has glass of its
    /// own and there are no rooms. The asset being edited in the panel is put on its buildings at
    /// once (<see cref="RefreshAsset"/>).
    ///
    /// While there are rooms behind the glass (Interiors.cs) EVERY building's texel says what kind of
    /// building it is (its a channel: <see cref="Interiors.Texel"/>; 255 while there are none), so the
    /// watch never stops then.
    ///
    /// Procedural Objects' buildings have texels past the buildings' (ProceduralBuildings), set from
    /// their assets every frame.
    ///
    /// An asset's glass is everything it has of its own (PerAsset.AssetRecords): the player's, over
    /// its creator's (RealGlassDefinition.xml). In the asset editor, the building being made
    /// (PerAsset.CreatorAsset) is no building of the buffer: the editor draws it with the colour index
    /// of building 0 (GlassPatches.EditorRenderMeshPatch), so its glass goes on texel 0, sent at once,
    /// and on every building there (its sub-buildings): all of them are the asset being made.
    /// </remarks>
    internal sealed class AssetMap
    {
        /// <summary>Building slots looked at a frame during a pass: the whole buffer (49,152) in a dozen frames.</summary>
        private const int PassPerFrame = 4096;

        /// <summary>Afterwards: a new or levelled-up building takes its asset's glass within a couple of seconds.</summary>
        private const int WatchPerFrame = 512;

        /// <summary>Frames between two uploads while a pass runs.</summary>
        private const int UploadEvery = 10;

        private static readonly Color32 AllGlobal = new Color32(AssetTextures.Global, AssetTextures.Global, AssetTextures.Global, AssetTextures.Global);

        private readonly AssetTextures _textures = new AssetTextures();
        private readonly Dictionary<BuildingInfo, Color32> _texelOf = new Dictionary<BuildingInfo, Color32>();
        private readonly Dictionary<BuildingInfo, Color32> _fullTexelOf = new Dictionary<BuildingInfo, Color32>();
        private bool _rooms;
        private readonly Dictionary<ulong, int> _slotOf = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, int> _keep = new Dictionary<ulong, int>();
        private readonly List<ResolvedAsset> _resolved = new List<ResolvedAsset>();
        private readonly bool[] _taken = new bool[AssetTextures.GlassSlots];
        private int _nextSlot;
        private long _seenVersion = -1;
        private BuildingInfo _seenEdited;
        private ulong _seenGlobal = ulong.MaxValue;

        /// <summary>The kinds of building the glass is off on (GlassKinds bits), as the texels have them.</summary>
        private int _offKinds;
        private int _next = 1;
        private int _passLeft;
        private int _sinceUpload;
        private bool _warnedSlots;

        /// <summary>For the log: the assets with glass of their own that are loaded, and the glasses they make.</summary>
        public int LoadedAssets { get; private set; }

        /// <summary>
        /// Each asset's shininess needed read from its textures (RoomFitMap's detection: 0 none),
        /// and its count of verdicts so far: set by GlassController. Used where the asset has no
        /// threshold of its own and the Glass tab's "Detect per asset" is on.
        /// </summary>
        public System.Func<BuildingInfo, float> DetectedOf;
        public System.Func<long> DetectionsVersion;
        public System.Func<List<BuildingInfo>> Detected;

        /// <summary>Verdicts arrive a few at a time while a city loads: the glass is resolved again at most this often, seconds.</summary>
        private const float DetectionsEvery = 1f;
        private long _seenDetections = -1;
        private bool _seenAuto;
        private float _nextDetections;

        /// <summary>For the log: the assets whose glass takes its detected threshold.</summary>
        public int DetectedAssets { get; private set; }

        public int GlassCount
        {
            get { return _slotOf.Count; }
        }

        /// <summary>
        /// Every frame while the glass is on: keeps the textures current and bound. rooms: there are
        /// rooms behind the glass, so every building's kind is wanted.
        /// </summary>
        public void Update(GlassParams global, bool rooms)
        {
            if (!_textures.IsCreated)
                _textures.Create(RenderManager.OBJECT_COLORMAP_WIDTH, RenderManager.OBJECT_COLORMAP_HEIGHT);

            if (rooms != _rooms)
            {
                // Every texel's a channel changes: a pass over the whole city.
                _rooms = rooms;
                _fullTexelOf.Clear();
                _passLeft = Singleton<BuildingManager>.instance.m_buildings.m_buffer.Length;
            }

            if (global.GlassOffKinds != _offKinds)
            {
                // A kind of building switched on or off: its strength (r) changes, a pass over the city.
                _offKinds = global.GlassOffKinds;
                _fullTexelOf.Clear();
                _passLeft = Singleton<BuildingManager>.instance.m_buildings.m_buffer.Length;
                Log.Msg("glass: off on " + GlassKinds.Describe(_offKinds));
            }

            ulong globalKey = GlassKey(GlassUniforms.Tint(global.TintR, global.TintG, global.TintB), global.OwnColour,
                                       global.Reflectivity, global.Clarity, global.Threshold, global.NightWindows, null);
            long version = AssetRecords.Stamp;
            bool editor = CreatorAsset.InEditor;
            long detections = DetectionsVersion != null ? DetectionsVersion() : 0;
            bool newDetections = detections != _seenDetections && Time.realtimeSinceStartup >= _nextDetections;
            if (version != _seenVersion || globalKey != _seenGlobal || (editor && CreatorAsset.Info != _seenEdited) ||
                global.ThresholdAuto != _seenAuto || (global.ThresholdAuto && newDetections))
            {
                bool first = _seenVersion < 0;
                _seenVersion = version;
                _seenGlobal = globalKey;
                _seenEdited = CreatorAsset.Info;
                _seenAuto = global.ThresholdAuto;
                _seenDetections = detections;
                _nextDetections = Time.realtimeSinceStartup + DetectionsEvery;
                Resolve(global);
                first = first && DetectedAssets == 0;
                if (LoadedAssets > 0 || !first)
                    _passLeft = Singleton<BuildingManager>.instance.m_buildings.m_buffer.Length;
                if (first && LoadedAssets > 0 && !editor)
                    Log.Msg("assets: " + AssetRecords.Describe() + "; " + _slotOf.Count + " glasses of their own");
            }

            int budget = _passLeft > 0 ? PassPerFrame : (_texelOf.Count > 0 || _rooms || _offKinds != 0 || editor ? WatchPerFrame : 0);
            if (budget > 0)
                Scan(budget);
            SetProcedural(null);

            // The asset editor's building: its texel now, and sent now (a slider being dragged).
            bool now = false;
            if (editor)
                now = _textures.Set(0, CreatorAsset.Info != null ? TexelOf(CreatorAsset.Info) : AllGlobal);

            if (_textures.Pending && (now || _passLeft <= 0 || ++_sinceUpload >= UploadEvery))
            {
                _textures.Upload();
                _sinceUpload = 0;
            }

            _textures.Bind();
        }

        /// <summary>
        /// Puts this asset's glass on every copy of it now, in one go: the panel is changing it and
        /// the player is looking at it.
        /// </summary>
        public void RefreshAsset(BuildingInfo info, GlassParams global)
        {
            if (info == null || !_textures.IsCreated)
                return;

            long version = AssetRecords.Stamp;
            if (version != _seenVersion)
            {
                _seenVersion = version;
                Resolve(global);
                _passLeft = Singleton<BuildingManager>.instance.m_buildings.m_buffer.Length;
            }

            Color32 texel = TexelOf(info);

            Building[] buffer = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            for (int i = 1; i < buffer.Length; i++)
            {
                if ((buffer[i].m_flags & (Building.Flags.Created | Building.Flags.Deleted)) == Building.Flags.Created &&
                    buffer[i].m_parentBuilding == 0 && buffer[i].Info == info)
                {
                    _textures.Set(i, texel);
                }
            }

            SetProcedural(info);
            _textures.Upload();
            _sinceUpload = 0;
        }

        /// <summary>
        /// The buildings of Procedural Objects (ProceduralBuildings): each its own texel past the
        /// buildings', its asset's like any copy's. A few hundred at most, every frame (only what
        /// changed is marked for the upload). only: just this asset's, or null for all.
        /// </summary>
        private void SetProcedural(BuildingInfo only)
        {
            List<ProceduralBuildings.Entry> entries = ProceduralBuildings.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                ProceduralBuildings.Entry e = entries[i];
                if (e.Index >= 0 && (only == null || e.Info == only))
                    _textures.Set(e.Index, TexelOf(e.Info));
            }
        }

        private void Scan(int budget)
        {
            Building[] buffer = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            int n = buffer.Length;
            for (int k = 0; k < budget; k++)
            {
                int i = _next;
                _next = _next + 1 < n ? _next + 1 : 1;

                // Only main buildings: a sub-building's draws read its main building's texel. In the
                // asset editor every building there is part of the asset being made.
                Color32 texel = AllGlobal;
                if ((buffer[i].m_flags & (Building.Flags.Created | Building.Flags.Deleted)) == Building.Flags.Created &&
                    buffer[i].m_parentBuilding == 0)
                {
                    texel = TexelOf(CreatorAsset.InEditor && CreatorAsset.Info != null ? CreatorAsset.Info : buffer[i].Info);
                }

                _textures.Set(i, texel);
            }

            if (_passLeft > 0)
                _passLeft -= budget;
        }

        /// <summary>
        /// A building's texel: its asset's own glass (or the global settings), and while there are
        /// rooms its kind and its asset's own say about them. Cached per asset.
        /// </summary>
        private Color32 TexelOf(BuildingInfo info)
        {
            if (info == null)
                return AllGlobal;

            Color32 texel;
            if (_fullTexelOf.TryGetValue(info, out texel))
                return texel;

            if (!_texelOf.TryGetValue(info, out texel))
                texel = AllGlobal;
            if (_rooms)
            {
                AssetRecord record = AssetRecords.Of(info);
                int assetSwitch = record == null || !record.Interiors.HasValue ? Interiors.FollowGlobal
                    : (record.Interiors.Value ? Interiors.AssetOn : Interiors.AssetOff);
                int curtains = record == null || !record.Curtains.HasValue ? Interiors.FollowGlobal
                    : (record.Curtains.Value ? Interiors.AssetOn : Interiors.AssetOff);
                int illumination = record == null || !record.FollowIllumination.HasValue ? Interiors.FollowGlobal
                    : (record.FollowIllumination.Value ? Interiors.AssetOn : Interiors.AssetOff);
                texel.a = Interiors.Texel(KindOf(info), assetSwitch, curtains, illumination);
            }
            if (_offKinds != 0 && (_offKinds & GlassKinds.Of(info)) != 0)
            {
                // Its kind is switched off: strength 0, nothing of ours on it (Glass.shader clips) --
                // unless the asset has a strength of its own: the player or its creator meant it.
                AssetRecord own = AssetRecords.Of(info);
                if (own == null || !own.Strength.HasValue)
                    texel.r = 0;
            }
            _fullTexelOf[info] = texel;
            return texel;
        }

        /// <summary>
        /// What rooms a building has, by what the game says it is (its ItemClass): homes, offices, shops
        /// (homes above the low ones, offices above the high), hotels, leisure, public buildings, industry.
        /// </summary>
        public static int KindOf(BuildingInfo info)
        {
            ItemClass c = info != null ? info.m_class : null;
            if (c == null)
                return Interiors.KindOffice;

            switch (c.m_service)
            {
                case ItemClass.Service.Residential:
                    return Interiors.KindHome;
                case ItemClass.Service.Office:
                    return Interiors.KindOffice;
                case ItemClass.Service.Commercial:
                    switch (c.m_subService)
                    {
                        case ItemClass.SubService.CommercialLeisure:
                            return Interiors.KindLeisure;
                        case ItemClass.SubService.CommercialTourist:
                            return Interiors.KindHotel;
                        case ItemClass.SubService.CommercialHigh:
                        case ItemClass.SubService.CommercialWallToWall:
                            return Interiors.KindShopHigh;
                        default:
                            return Interiors.KindShopLow;
                    }
                case ItemClass.Service.Hotel:
                    return Interiors.KindHotel;
                case ItemClass.Service.Tourism:
                case ItemClass.Service.Beautification:
                    return Interiors.KindLeisure;
                case ItemClass.Service.Industrial:
                case ItemClass.Service.PlayerIndustry:
                case ItemClass.Service.Fishing:
                    return Interiors.KindIndustry;
                case ItemClass.Service.HealthCare:
                case ItemClass.Service.Education:
                case ItemClass.Service.PlayerEducation:
                case ItemClass.Service.Museums:
                case ItemClass.Service.Monument:
                case ItemClass.Service.PoliceDepartment:
                case ItemClass.Service.FireDepartment:
                case ItemClass.Service.Disaster:
                case ItemClass.Service.PublicTransport:
                case ItemClass.Service.VarsitySports:
                case ItemClass.Service.ServicePoint:
                    return Interiors.KindPublic;
                default:
                    return Interiors.KindOffice;
            }
        }

        /// <summary>
        /// Each asset's texel, from its own settings and, where it has none, the global ones.
        /// </summary>
        /// <remarks>
        /// SLOTS STAY PUT: a glass that is still in use keeps its slot, a new one takes the free slot
        /// least recently given out. The texels of buildings the pass has not reached yet still name
        /// the old slots: renumbering the table from 0 (as before 2026-10-03) showed the glass being
        /// edited on other assets' buildings for a moment, every step of a slider.
        /// </remarks>
        private void Resolve(GlassParams global)
        {
            _texelOf.Clear();
            _fullTexelOf.Clear();
            _resolved.Clear();
            LoadedAssets = 0;
            DetectedAssets = 0;

            // The assets with a record, then those with only a detected threshold: each, where it has
            // no threshold of its own, takes its detected one (ThresholdDetect: none within a step of
            // the global) as if it were its own.
            _withRecord.Clear();
            List<KeyValuePair<BuildingInfo, AssetRecord>> loaded = AssetRecords.Loaded;
            for (int k = 0; k < loaded.Count; k++)
            {
                _withRecord.Add(loaded[k].Key);
                _candidates.Add(new KeyValuePair<BuildingInfo, AssetRecord>(loaded[k].Key, loaded[k].Value));
            }
            List<BuildingInfo> detected = global.ThresholdAuto && Detected != null ? Detected() : null;
            if (detected != null)
            {
                for (int k = 0; k < detected.Count; k++)
                {
                    if (detected[k] != null && !_withRecord.Contains(detected[k]))
                        _candidates.Add(new KeyValuePair<BuildingInfo, AssetRecord>(detected[k], null));
                }
            }

            for (int k = 0; k < _candidates.Count; k++)
            {
                BuildingInfo info = _candidates[k].Key;
                AssetRecord record = _candidates[k].Value;
                if (record != null)
                    LoadedAssets++;
                float found = ThresholdFor(record, info, global, DetectedOf);
                if ((record == null || !record.Threshold.HasValue) && found != global.Threshold)
                {
                    record = AssetRecord.Over(record, new AssetRecord(info.name) { Threshold = found });
                    DetectedAssets++;
                }
                if (record == null)
                    continue;

                var r = new ResolvedAsset { Info = info, Record = record };
                if (record.HasGlass)
                {
                    Glass(record, global, out r.Tint, out r.Own, out r.Reflectivity, out r.Clarity, out r.Threshold, out r.NightWindows);
                    r.Dirt = record.Dirt;   // null: the Glass tab's (the shader reads that live)
                    r.Key = GlassKey(r.Tint, r.Own, r.Reflectivity, r.Clarity, r.Threshold, r.NightWindows, r.Dirt);
                }
                _resolved.Add(r);
            }
            _candidates.Clear();

            // The glasses still in use keep their slots.
            _keep.Clear();
            for (int i = 0; i < _resolved.Count; i++)
            {
                int slot;
                if (_resolved[i].Record.HasGlass && _slotOf.TryGetValue(_resolved[i].Key, out slot))
                    _keep[_resolved[i].Key] = slot;
            }
            _slotOf.Clear();
            for (int i = 0; i < _taken.Length; i++)
                _taken[i] = false;
            foreach (KeyValuePair<ulong, int> kept in _keep)
            {
                _slotOf[kept.Key] = kept.Value;
                _taken[kept.Value] = true;
            }

            for (int i = 0; i < _resolved.Count; i++)
            {
                ResolvedAsset r = _resolved[i];
                byte slot = AssetTextures.Global;
                if (r.Record.HasGlass)
                {
                    int found;
                    if (!_slotOf.TryGetValue(r.Key, out found))
                    {
                        found = FreeSlot();
                        if (found >= 0)
                        {
                            _slotOf[r.Key] = found;
                            _taken[found] = true;
                            _textures.SetGlass(found, r.Tint, r.Own, r.Reflectivity, r.Clarity, r.Threshold, r.NightWindows, r.Dirt);
                        }
                        else
                        {
                            found = AssetTextures.Global;
                            if (!_warnedSlots)
                            {
                                _warnedSlots = true;
                                Log.Warn("assets: more than " + AssetTextures.GlassSlots + " different glasses of their own; the rest show the global glass");
                            }
                        }
                    }
                    slot = (byte)found;
                }

                _texelOf[r.Info] = new Color32(
                    r.Record.Strength.HasValue ? AssetTextures.Encode(r.Record.Strength.Value) : AssetTextures.Global,
                    r.Record.PaintedDetail.HasValue ? AssetTextures.Encode(r.Record.PaintedDetail.Value) : AssetTextures.Global,
                    slot, AssetTextures.Global);
            }

            _resolved.Clear();
        }

        /// <summary>
        /// The next slot no glass holds now, going round the table: a slot just let go is the last
        /// to be written over, so buildings still naming it keep their glass until the pass reaches
        /// them. -1 when every slot is taken.
        /// </summary>
        private int FreeSlot()
        {
            for (int k = 0; k < _taken.Length; k++)
            {
                int slot = _nextSlot;
                _nextSlot = _nextSlot + 1 < _taken.Length ? _nextSlot + 1 : 0;
                if (!_taken[slot])
                    return slot;
            }

            return -1;
        }

        private readonly HashSet<BuildingInfo> _withRecord = new HashSet<BuildingInfo>();
        private readonly List<KeyValuePair<BuildingInfo, AssetRecord>> _candidates = new List<KeyValuePair<BuildingInfo, AssetRecord>>();

        /// <summary>
        /// The shininess an asset's glass needs: its own (the player's, else its creator's), else --
        /// with "Detect per asset" on -- the one read from its textures where that is no step from
        /// the global, else the global. detectedOf: the detection (0 none); null for none at all.
        /// </summary>
        public static float ThresholdFor(AssetRecord record, BuildingInfo info, GlassParams global, System.Func<BuildingInfo, float> detectedOf)
        {
            if (record != null && record.Threshold.HasValue)
                return record.Threshold.Value;
            float detected = global.ThresholdAuto && detectedOf != null && info != null ? detectedOf(info) : 0f;
            return ThresholdDetect.Applies(detected, global.Threshold) ? detected : global.Threshold;
        }

        private struct ResolvedAsset
        {
            public BuildingInfo Info;
            public AssetRecord Record;
            public ulong Key;
            public Vector3 Tint;
            public float Own, Reflectivity, Clarity, Threshold;
            public float? Dirt;
            public bool NightWindows;
        }

        /// <summary>
        /// The asset's glass in full: each number its own, else its kind of glass's (when it chose
        /// one), else the global one; what counts as its glass, its own else the global.
        /// </summary>
        public static void Glass(AssetRecord record, GlassParams global, out Vector3 tint, out float own, out float reflectivity,
                                 out float clarity, out float threshold, out bool nightWindows)
        {
            threshold = record.Threshold ?? global.Threshold;
            nightWindows = record.NightWindows ?? global.NightWindows;

            GlassPreset preset = default(GlassPreset);
            bool hasPreset = record.Preset.HasValue && GlassPresets.TryGet(record.Preset.Value, out preset);

            float r, g, b;
            if (record.Colour.HasValue)
            {
                r = record.Colour.Value.r / 255f;
                g = record.Colour.Value.g / 255f;
                b = record.Colour.Value.b / 255f;
            }
            else if (hasPreset)
            {
                r = preset.Colour.r / 255f;
                g = preset.Colour.g / 255f;
                b = preset.Colour.b / 255f;
            }
            else
            {
                r = global.TintR;
                g = global.TintG;
                b = global.TintB;
            }

            tint = GlassUniforms.Tint(r, g, b);
            own = record.OwnColour ?? global.OwnColour;
            reflectivity = record.Reflectivity ?? (hasPreset ? preset.Reflectivity : global.Reflectivity);
            clarity = record.Clarity ?? (hasPreset ? preset.Clarity : global.Clarity);
        }

        /// <summary>
        /// A glass as the texture holds it (8 bits a number, the last byte the lit windows' switch in its
        /// top bit and the dirt in 7: the asset's own at 0..126 -- a percent row's 1% steps all stay apart
        /// at 1/126 -- or 127, the Glass tab's): equal keys, one slot.
        /// </summary>
        private static ulong GlassKey(Vector3 tint, float own, float reflectivity, float clarity, float threshold, bool nightWindows,
                                      float? dirt)
        {
            uint dirt7 = dirt.HasValue ? (uint)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(dirt.Value) * 126f), 0, 126) : 127u;
            ulong last = (nightWindows ? 128UL : 0UL) | dirt7;
            return Byte(tint.x) | Byte(tint.y) << 8 | Byte(tint.z) << 16 | Byte(own) << 24 |
                   Byte(reflectivity) << 32 | Byte(clarity) << 40 | Byte(threshold) << 48 | last << 56;
        }

        private static ulong Byte(float value)
        {
            return (ulong)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(value) * 255f), 0, 255);
        }

        /// <summary>The copies of an asset standing in the city (main buildings only).</summary>
        public static int CountCopies(BuildingInfo info)
        {
            if (info == null || !Singleton<BuildingManager>.exists)
                return 0;

            int count = 0;
            Building[] buffer = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            for (int i = 1; i < buffer.Length; i++)
            {
                if ((buffer[i].m_flags & (Building.Flags.Created | Building.Flags.Deleted)) == Building.Flags.Created &&
                    buffer[i].m_parentBuilding == 0 && buffer[i].Info == info)
                {
                    count++;
                }
            }

            return count;
        }

        public void Destroy()
        {
            _textures.Destroy();
            _texelOf.Clear();
            _fullTexelOf.Clear();
            _rooms = false;
            _slotOf.Clear();
            _nextSlot = 0;
            _seenVersion = -1;
            _seenEdited = null;
        }
    }
}
