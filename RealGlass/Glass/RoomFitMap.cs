using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using ColossalFramework;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealGlass.Glass
{
    /// <summary>
    /// The game's side of <see cref="RoomFit"/>: the building assets standing in the city (looked
    /// for again every few seconds), each model's shine map read back on the GPU a batch a frame,
    /// its windows measured on worker threads, a row of <see cref="RoomFitTextures"/> each; and every
    /// building's place (its model's origin and turn, its asset's row), kept current a slice of the
    /// building buffer a frame -- so the rooms stand where each asset's own windows are, on every
    /// tier. What was measured is remembered between loads (<see cref="RoomFitCache"/>,
    /// RealGlassRoomFits.txt): an asset measured before is not read back again. Only while there
    /// are rooms; nothing it measured is forgotten while the city lasts.
    /// </summary>
    /// <remarks>
    /// The main thread's share is what only it may do: copying each model's mesh out of the engine
    /// and the read-back, which waits for the GPU -- seven models a wait (2026-10-21: three a wait
    /// cost 7.4 s over the first 43 s of his 891-asset city at 4K). Cutting each model's tile out of
    /// the read-back is the worker's.
    /// </remarks>
    internal sealed class RoomFitMap
    {
        /// <summary>The shine map is read back this size (the survey of the installed assets, 2026-10-21:
        /// 96% of floors where the full map puts them; 94% of storeys at 256).</summary>
        private const int Tile = 512;

        /// <summary>A read-back: a 2048 x 1024 target, its first tile the marker (LodGlass's, 256 wide in its corner), seven models.</summary>
        private const int TilesX = 4, TilesY = 2, PerBatch = TilesX * TilesY - 1;

        /// <summary>A frame's batch stops once its models' vertices copied reach this.</summary>
        private const int MaxVerticesPerFrame = 200000;

        /// <summary>Models read back and not yet measured, at most (three batches: each holds its 8 MB read-back until its last model is cut out).</summary>
        private const int MaxInFlight = 3 * PerBatch;

        /// <summary>How often the building buffer is looked through for assets not seen yet, seconds.</summary>
        private const float ScanEvery = 10f;

        /// <summary>Building slots whose place is looked at a frame: the whole buffer (49,152) in 24 frames.</summary>
        private const int PlacesPerFrame = 2048;

        /// <summary>The places (4 MB) are sent to the GPU at most this often, seconds.</summary>
        private const float UploadEvery = 0.5f;

        /// <summary>How many assets each list of the report names.</summary>
        private const int ReportNames = 30;

        private sealed class Job
        {
            public BuildingInfo Info;
            public string Asset;
            public string Fingerprint;
            public float CellWidth;
            public Texture Xys;
            public Texture Aci;      // the illumination map (_ACIMap.b): the windows the game lights, for the threshold
            public Vector4 St;
            public bool Clamp;
            public float[] Pos, Uv;
            public int[] Tri;

            /// <summary>Its batch's read-back (shared, read only) and where its tile is in it: the worker cuts it out.</summary>
            public Color32[] Pixels;
            public int PixelsWidth, PixelsHeight, TileX, TileY;
            public bool Flipped;

            public RoomFitResult Result;

            /// <summary>Remembered from an earlier load (not measured now); Fresh: measured now by RoomFit (worth remembering).</summary>
            public bool Remembered, Fresh;

            /// <summary>The asset editor's building: neither taken from nor written to the cache (it changes as it is made).</summary>
            public bool Unremembered;
        }

        /// <summary>Where the asset editor draws the building being made (GlassPatches.EditorRenderMeshPatch), as building 0.</summary>
        private static readonly Vector3 EditorOrigin = new Vector3(0f, 60f, 0f);

        /// <summary>How often the building being made is looked at for a new model or textures, seconds.</summary>
        private const float EditorCheckEvery = 1f;

        private readonly RoomFitTextures _textures = new RoomFitTextures();
        private readonly HashSet<BuildingInfo> _seen = new HashSet<BuildingInfo>();
        private readonly Queue<Job> _toRead = new Queue<Job>();
        private readonly Queue<Job> _toMeasure = new Queue<Job>();
        private readonly Dictionary<BuildingInfo, int> _rowOf = new Dictionary<BuildingInfo, int>();
        private readonly Dictionary<string, RoomFitResult> _resultOf = new Dictionary<string, RoomFitResult>(StringComparer.Ordinal);
        private readonly List<Job> _round = new List<Job>();
        private readonly int _workers = Mathf.Clamp(SystemInfo.processorCount / 2, 1, 3);

        // Shared with the workers (under _lock).
        private readonly object _lock = new object();
        private readonly List<Job> _measured = new List<Job>();
        private int _running;

        private RenderTexture _target;
        private Texture2D _read;
        private Mesh _quad;
        private bool _ids;
        private int _idTarget, _idRect, _idModel, _idInfo, _idAtlasXys;

        private int _nextRow = 1, _next = 1;
        private float _nextScan, _nextUpload;

        // The asset editor's building being made: which, and its model and shine map as last measured.
        private BuildingInfo _editorInfo;
        private string _editorKey;
        private float _nextEditorCheck;
        private bool _failed, _reported, _warnedRows, _loggedStart;
        private Stopwatch _roundWatch;
        private long _mainTicks;

        // What earlier loads measured (RoomFitCache), and its file's saving (one write at a time, on a worker).
        private RoomFitCache _cache;
        private readonly object _saveLock = new object();
        private bool _saving, _saveFailed, _loggedSaveFailure;
        private string _saveProblem = "";

        // The thresholds detected (ThresholdDetect), by asset name: 0 none; and the assets with one.
        private readonly Dictionary<string, float> _detectedOf = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly List<BuildingInfo> _detectedInfos = new List<BuildingInfo>();
        private bool _rooms;

        /// <summary>Counts up whenever a verdict came in that changes what an asset's glass needs (AssetMap resolves again).</summary>
        public long DetectionsVersion { get; private set; }

        /// <summary>The shininess this asset's glass needs as read from its textures, 0 when none (or not measured yet).</summary>
        public float DetectedOf(string asset)
        {
            float t;
            return asset != null && _detectedOf.TryGetValue(asset, out t) ? t : 0f;
        }

        /// <summary>The assets with a detected threshold (AssetMap's list; read, not kept).</summary>
        public List<BuildingInfo> DetectedInfos
        {
            get { return _detectedInfos; }
        }

        /// <summary>What was found of an asset's windows; null while it is not measured.</summary>
        public RoomFitResult ResultOf(string asset)
        {
            RoomFitResult r;
            return asset != null && _resultOf.TryGetValue(asset, out r) ? r : null;
        }

        /// <summary>For the 5 s line.</summary>
        public string Describe()
        {
            if (_failed)
                return "rooms fitted: stopped";
            int storeys = 0;
            foreach (RoomFitResult r in _resultOf.Values)
            {
                if (r.Storey > 0f)
                    storeys++;
            }
            return "rooms fitted to " + _resultOf.Count + " assets (" + storeys + " with their storeys)" +
                   (_toRead.Count + _toMeasure.Count > 0 ? ", " + (_toRead.Count + _toMeasure.Count) + " to go" : "");
        }

        /// <summary>
        /// Every frame the glass is on. measure: anything wants the assets measured (the rooms, the
        /// detected thresholds); rooms: there are rooms behind the glass -- their fit is bound and
        /// the places kept (else the shader lays them out as before); util: GlassUtil, for the read-back.
        /// </summary>
        public void Update(bool measure, bool rooms, Material util)
        {
            if (!measure || _failed)
            {
                if (_textures.IsCreated)
                    _textures.Unbind();
                return;
            }

            try
            {
                _rooms = rooms;
                Step(util);
                if (rooms)
                    _textures.Bind();
                else if (_textures.IsCreated)
                    _textures.Unbind();
            }
            catch (Exception e)
            {
                _failed = true;
                _textures.Destroy();
                FreeReadBack();
                Log.Error("rooms: fitting them to the windows failed; the rooms stay on the storey grid in this city", e);
            }
        }

        private void Step(Material util)
        {
            if (!_textures.IsCreated)
            {
                if (!_textures.Create(RenderManager.OBJECT_COLORMAP_WIDTH, RenderManager.OBJECT_COLORMAP_HEIGHT))
                {
                    _failed = true;
                    Log.Warn("rooms: no room for their fit (" + _textures.Problem + "); the rooms stay on the storey grid");
                    return;
                }
            }
            if (_cache == null)
                _cache = LoadCache();
            if (!_loggedSaveFailure)
            {
                string problem;
                lock (_saveLock)
                    problem = _saveFailed ? _saveProblem : "";
                if (problem.Length > 0)
                {
                    _loggedSaveFailure = true;
                    Log.Msg("rooms: " + RoomFitCache.FileName + " could not be written (" + problem + "): the next load measures again");
                }
            }

            long start = Stopwatch.GetTimestamp();
            float now = Time.realtimeSinceStartup;
            if (now >= _nextScan)
            {
                _nextScan = now + ScanEvery;
                Scan();
            }

            EditorAsset(now);
            if (util != null)
                ReadSome(util);
            Dispatch();
            bool collected = Collect();
            if (_rooms)
                Places();
            if (_editorInfo != null)
                Place(0, EditorOrigin, Quaternion.identity, _editorInfo);

            if ((_textures.FitsPending || collected) || (_textures.PlacesPending && now >= _nextUpload))
            {
                _nextUpload = now + UploadEvery;
                if (!_textures.Upload())
                    throw new InvalidOperationException("the upload failed: " + _textures.Problem);
            }
            _mainTicks += Stopwatch.GetTimestamp() - start;

            if (_round.Count > 0 && _toRead.Count == 0 && _toMeasure.Count == 0 && Running() == 0)
            {
                FreeReadBack();
                Report();
            }
        }

        // ---------------------------------------------------------------------------------------
        // Finding the assets, reading them back, measuring them.
        // ---------------------------------------------------------------------------------------

        /// <summary>The building assets standing in the city (main buildings, and Procedural Objects') not seen yet, queued.</summary>
        private void Scan()
        {
            int added = 0;
            Building[] buffer = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            for (int i = 1; i < buffer.Length; i++)
            {
                if ((buffer[i].m_flags & (Building.Flags.Created | Building.Flags.Deleted)) == Building.Flags.Created && buffer[i].m_parentBuilding == 0)
                    added += Add(buffer[i].Info);
            }
            List<ProceduralBuildings.Entry> entries = ProceduralBuildings.Entries;
            for (int i = 0; i < entries.Count; i++)
                added += Add(entries[i].Info);

            if (added == 0)
                return;
            if (_roundWatch == null)
            {
                _roundWatch = Stopwatch.StartNew();
                _mainTicks = 0;
            }
            if (!_loggedStart)
            {
                _loggedStart = true;
                int remembered;
                lock (_lock)
                    remembered = _measured.Count;
                Log.Msg("rooms: fitting the rooms of " + added + " building assets in the city to their windows: " + remembered +
                        " remembered from earlier loads, " + _toRead.Count + " to measure (" + _workers + " worker thread" + (_workers == 1 ? "" : "s") + ")");
            }
        }

        /// <summary>
        /// The asset editor's building being made (PerAsset.CreatorAsset): no building of the buffer,
        /// drawn as building 0 at <see cref="EditorOrigin"/>. Measured when it comes, and again when its
        /// model or shine map is another object (the creator imported a new one); never cached.
        /// </summary>
        private void EditorAsset(float now)
        {
            BuildingInfo info = PerAsset.CreatorAsset.InEditor ? PerAsset.CreatorAsset.Info : null;
            if (info == null)
            {
                if (_editorInfo != null)
                {
                    _textures.ClearPlace(0);
                    _editorInfo = null;
                    _editorKey = null;
                }
                return;
            }

            if (info == _editorInfo && now < _nextEditorCheck)
                return;
            _nextEditorCheck = now + EditorCheckEvery;

            string key = EditorKey(info);
            if (info == _editorInfo && key == _editorKey)
                return;

            bool again = info == _editorInfo;
            _editorInfo = info;
            _editorKey = key;
            _seen.Remove(info);
            if (Add(info, false) > 0)
                Log.Msg("rooms: measuring the windows of the building being made" + (again ? " again (a new model or textures)" : "") + " (" + key + ")");
        }

        /// <summary>What the fit of the building being made depends on: its model, its shine map (the objects), their tiling.</summary>
        private static string EditorKey(BuildingInfo info)
        {
            Material m = info.m_material;
            Texture xys = m != null && m.HasProperty("_XYSMap") ? m.GetTexture("_XYSMap") : null;
            return "mesh " + (info.m_mesh != null ? info.m_mesh.GetInstanceID() + " " + info.m_mesh.vertexCount + "v" : "none") +
                   ", shine " + (xys != null ? xys.GetInstanceID() + " " + xys.width + "x" + xys.height : "none") +
                   (m != null ? ", st " + m.mainTextureScale + m.mainTextureOffset : "");
        }

        /// <summary>
        /// One asset, if it draws with the glass (a building shader with a shine map, a mesh): its fit
        /// from an earlier load when its fingerprint is the same, else queued to be measured. 1 if either.
        /// remember: false for the asset editor's building (no cache either way).
        /// </summary>
        private int Add(BuildingInfo info, bool remember = true)
        {
            if (info == null || !_seen.Add(info))
                return 0;
            Material m = info.m_material;
            if (m == null || info.m_mesh == null || !GlassDraw.IsBuildingShader(m.shader) || !m.HasProperty("_XYSMap"))
                return 0;
            Texture xys = m.GetTexture("_XYSMap");
            if (xys == null)
                return 0;

            Vector2 scale = m.mainTextureScale, offset = m.mainTextureOffset;
            var job = new Job
            {
                Info = info,
                Asset = info.name,
                CellWidth = RoomFit.CellWidth(AssetMap.KindOf(info)),
                Xys = xys,
                Aci = m.HasProperty("_ACIMap") ? m.GetTexture("_ACIMap") : null,
                St = new Vector4(scale.x, scale.y, offset.x, offset.y),
                Clamp = xys.wrapMode == TextureWrapMode.Clamp,
            };
            job.Fingerprint = RoomFitCache.Fingerprint(info.m_mesh, xys, job.St, job.CellWidth, job.Aci);
            job.Unremembered = !remember;

            RoomFitResult remembered;
            if (remember && _cache != null && _cache.TryGet(job.Asset, job.Fingerprint, out remembered))
            {
                job.Result = remembered;
                job.Remembered = true;
                lock (_lock)
                    _measured.Add(job);
                return 1;
            }
            _toRead.Enqueue(job);
            return 1;
        }

        private int Running()
        {
            lock (_lock)
                return _running;
        }

        /// <summary>A batch a frame: each model's mesh copied (the workers touch no engine object) and its shine map read back.</summary>
        private void ReadSome(Material util)
        {
            if (_toRead.Count == 0 || _toMeasure.Count + Running() >= MaxInFlight)
                return;

            var batch = new List<Job>();
            int vertices = 0;
            while (_toRead.Count > 0 && batch.Count < PerBatch && vertices < MaxVerticesPerFrame)
            {
                Job job = _toRead.Dequeue();
                string problem = CopyMesh(job);
                if (problem.Length > 0)
                {
                    job.Result = new RoomFitResult { Problem = problem };
                    lock (_lock)
                        _measured.Add(job);
                    continue;
                }
                vertices += job.Pos.Length / 3;
                batch.Add(job);
            }
            if (batch.Count == 0)
                return;

            ReadBack(batch, util);
            for (int i = 0; i < batch.Count; i++)
                _toMeasure.Enqueue(batch[i]);
        }

        private static string CopyMesh(Job job)
        {
            try
            {
                Mesh mesh = job.Info.m_mesh;
                Vector3[] v = mesh.vertices;
                Vector2[] uv = mesh.uv;
                int[] tri = mesh.triangles;
                if (v == null || v.Length < 3 || tri == null || tri.Length < 3)
                    return "its mesh could not be read";
                if (uv == null || uv.Length < v.Length)
                    return "its mesh has no texture coordinates";
                job.Pos = new float[v.Length * 3];
                for (int i = 0; i < v.Length; i++)
                {
                    job.Pos[3 * i] = v[i].x;
                    job.Pos[3 * i + 1] = v[i].y;
                    job.Pos[3 * i + 2] = v[i].z;
                }
                job.Uv = new float[uv.Length * 2];
                for (int i = 0; i < uv.Length; i++)
                {
                    job.Uv[2 * i] = uv[i].x;
                    job.Uv[2 * i + 1] = uv[i].y;
                }
                job.Tri = tri;
                return "";
            }
            catch (Exception e)
            {
                return "its mesh could not be read (" + e.GetType().Name + ": " + e.Message + ")";
            }
        }

        private void Ids()
        {
            if (_ids)
                return;
            _idTarget = Shader.PropertyToID("_RGLodTarget");
            _idRect = Shader.PropertyToID("_RGLodRect");
            _idModel = Shader.PropertyToID("_RGLodModel");
            _idInfo = Shader.PropertyToID("_RGLodInfo");
            _idAtlasXys = Shader.PropertyToID("_RGLodAtlasXys");
            _ids = true;
        }

        /// <summary>
        /// Each job's shine map into its 512 x 512 tile (GlassUtil pass 6, LodGlass's: r the model's
        /// shine at the mip as wide as the tile), the marker in the target's corner; read back, which
        /// way up the marker says.
        /// </summary>
        private void ReadBack(List<Job> batch, Material util)
        {
            Ids();
            int width = TilesX * Tile, height = TilesY * Tile;
            if (_quad == null)
                _quad = GlassScreen.ClipQuad();
            if (_target == null)
            {
                _target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                _target.name = "RealGlass room fit read-back";
                _target.hideFlags = HideFlags.HideAndDontSave;
                _target.filterMode = FilterMode.Point;
                _target.Create();
            }
            if (_read == null)
            {
                _read = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                _read.hideFlags = HideFlags.HideAndDontSave;
            }

            RenderTexture before = RenderTexture.active;
            var cb = new CommandBuffer();
            cb.name = "RealGlass room fit: read-back";
            try
            {
                cb.SetRenderTarget(_target);
                cb.ClearRenderTarget(false, true, new Color(0f, 0f, 0f, 0f));

                var marker = new MaterialPropertyBlock();
                marker.SetVector(_idTarget, new Vector4(0f, 0f, (float)LodGlass.Tile / width, (float)LodGlass.Tile / height));
                marker.SetVector(_idInfo, new Vector4(1f, 0f, 0f, 0f));
                cb.DrawMesh(_quad, Matrix4x4.identity, util, 0, LodGlass.PassSample, marker);

                for (int i = 0; i < batch.Count; i++)
                {
                    Job job = batch[i];
                    int tile = i + 1;
                    float mip = MipFor(job.Xys);
                    var block = new MaterialPropertyBlock();
                    block.SetVector(_idTarget, new Vector4((float)(tile % TilesX) * Tile / width, (float)(tile / TilesX) * Tile / height,
                                                           (float)Tile / width, (float)Tile / height));
                    block.SetVector(_idRect, new Vector4(0f, 0f, 1f, 1f));
                    block.SetTexture(_idModel, job.Xys);
                    // g: 1 - the illumination map's b, read at its own tile-wide mip (pass 6 reads z
                    // of its second texture; an asset with no illumination map: black, g = 1, none lit).
                    block.SetTexture(_idAtlasXys, job.Aci != null ? job.Aci : Texture2D.blackTexture);
                    block.SetVector(_idInfo, new Vector4(0f, mip, job.Aci != null ? MipFor(job.Aci) : 0f, 0f));
                    cb.DrawMesh(_quad, Matrix4x4.identity, util, 0, LodGlass.PassSample, block);
                }

                Graphics.ExecuteCommandBuffer(cb);
                RenderTexture.active = _target;
                _read.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                Color32[] pixels = _read.GetPixels32();

                bool flipped;
                if (!LodGlass.FindMarker(pixels, width, height, out flipped))
                    throw new InvalidOperationException("the read-back's marker tile was not where it was drawn");
                for (int i = 0; i < batch.Count; i++)
                {
                    // Cut out on the worker (TakeTile): the read-back is shared, read only, until its last model is.
                    int tile = i + 1;
                    Job job = batch[i];
                    job.Pixels = pixels;
                    job.PixelsWidth = width;
                    job.PixelsHeight = height;
                    job.TileX = (tile % TilesX) * Tile;
                    job.TileY = (tile / TilesX) * Tile;
                    job.Flipped = flipped;
                }
            }
            finally
            {
                RenderTexture.active = before;
                cb.Release();
            }
        }

        /// <summary>The mip of a texture as wide as a tile (0 when it is no wider).</summary>
        private static float MipFor(Texture t)
        {
            int size = Mathf.Max(t.width, t.height);
            return size > Tile ? Mathf.Log((float)size / Tile, 2f) : 0f;
        }

        /// <summary>One tile: its r channel (the shine) and 255 - its g (the illumination), rows in texture order.</summary>
        private static byte[] TakeTile(Color32[] pixels, int width, int height, int x0, int y0, bool flipped, out byte[] illumination)
        {
            var shine = new byte[Tile * Tile];
            illumination = new byte[Tile * Tile];
            for (int r = 0; r < Tile; r++)
            {
                int row = flipped ? height - 1 - (y0 + r) : y0 + r;
                for (int c = 0; c < Tile; c++)
                {
                    Color32 p = pixels[row * width + x0 + c];
                    shine[r * Tile + c] = p.r;
                    illumination[r * Tile + c] = (byte)(255 - p.g);
                }
            }
            return shine;
        }

        /// <summary>As many measurements running as there are workers.</summary>
        private void Dispatch()
        {
            while (_toMeasure.Count > 0)
            {
                lock (_lock)
                {
                    if (_running >= _workers)
                        return;
                    _running++;
                }
                ThreadPool.QueueUserWorkItem(Measure, _toMeasure.Dequeue());
            }
        }

        /// <summary>The worker: one asset's tile cut out of its batch's read-back and measured. Never throws (a thread's exception would end the game).</summary>
        private void Measure(object state)
        {
            var job = (Job)state;
            try
            {
                byte[] illumination;
                byte[] shine = TakeTile(job.Pixels, job.PixelsWidth, job.PixelsHeight, job.TileX, job.TileY, job.Flipped, out illumination);
                job.Pixels = null;
                job.Result = RoomFit.Measure(job.Pos, job.Uv, job.Tri, shine, Tile, job.St.x, job.St.y, job.St.z, job.St.w, job.Clamp, job.CellWidth);
                // Its threshold from the same tiles, against the game's own 70% (what the shares
                // are told against; whether it applies is decided against the slider at use).
                job.Result.Threshold = ThresholdDetect.Measure(job.Pos, job.Uv, job.Tri, shine, job.Aci != null ? illumination : null, Tile,
                                                               job.St.x, job.St.y, job.St.z, job.St.w, job.Clamp, Settings.Defaults.GlassThreshold);
                job.Fresh = true;
            }
            catch (Exception e)
            {
                job.Result = new RoomFitResult { Problem = "its tile could not be read (" + e.GetType().Name + ": " + e.Message + ")" };
            }
            job.Pixels = null;
            job.Pos = job.Uv = null;
            job.Tri = null;
            lock (_lock)
            {
                _measured.Add(job);
                _running--;
            }
        }

        /// <summary>What the workers measured: a row each (the ones measured), remembered. True if any.</summary>
        private bool Collect()
        {
            List<Job> done;
            lock (_lock)
            {
                if (_measured.Count == 0)
                    return false;
                done = new List<Job>(_measured);
                _measured.Clear();
            }

            for (int i = 0; i < done.Count; i++)
            {
                Job job = done[i];
                _round.Add(job);
                _resultOf[job.Asset] = job.Result;
                if (job.Fresh && _cache != null && !job.Unremembered)
                    _cache.Put(job.Asset, job.Fingerprint, job.Result);
                NoteThreshold(job);
                if (!job.Result.Measured)
                    continue;
                if (_nextRow >= RoomFitTextures.FitRows)
                {
                    if (!_warnedRows)
                    {
                        _warnedRows = true;
                        Log.Warn("rooms: more than " + (RoomFitTextures.FitRows - 1) + " building assets measured; the rest keep the storey grid");
                    }
                    continue;
                }
                int row = _nextRow++;
                _rowOf[job.Info] = row;
                _textures.SetFit(row, job.Result);
            }
            return true;
        }

        /// <summary>An asset's detected threshold kept by its name; a change counts (AssetMap and LodGlassMap look again).</summary>
        private void NoteThreshold(Job job)
        {
            ThresholdResult t = job.Result.Threshold;
            float detected = t != null && t.Measured ? t.Detected : 0f;
            float before;
            bool had = _detectedOf.TryGetValue(job.Asset, out before);
            if (had && before == detected)
                return;
            _detectedOf[job.Asset] = detected;
            if (detected > 0f && !_detectedInfos.Contains(job.Info))
                _detectedInfos.Add(job.Info);
            else if (detected <= 0f)
                _detectedInfos.Remove(job.Info);
            if (detected > 0f || had)
                DetectionsVersion++;
            if (detected > 0f && Log.Detailed)
                Log.Detail("glass threshold: '" + job.Asset + "' " + Mathf.RoundToInt(detected * 100f) + "% by " + t.SourceName +
                           " (glass " + Pct(t.GlassShareGlobal) + " of its walls at 70%, " + Pct(t.GlassShareDetected) + " at it)");
        }

        private static string Pct(float v)
        {
            return Mathf.RoundToInt(v * 100f) + "%";
        }

        /// <summary>
        /// After a round: the thresholds detected, one line -- how many by which cue, the range, and
        /// the largest changes of glass share each way (the assets to look at with the mask view).
        /// </summary>
        private void ReportThresholds()
        {
            int measured = 0, lit = 0, shine = 0;
            float lo = 1f, hi = 0f;
            var changed = new List<Job>();
            for (int i = 0; i < _round.Count; i++)
            {
                ThresholdResult t = _round[i].Result.Threshold;
                if (t == null || !t.Measured)
                    continue;
                measured++;
                if (t.Detected <= 0f)
                    continue;
                if (t.Source == ThresholdDetect.SourceNightMap) lit++; else shine++;
                lo = Mathf.Min(lo, t.Detected);
                hi = Mathf.Max(hi, t.Detected);
                changed.Add(_round[i]);
            }
            if (measured == 0)
                return;
            changed.Sort((a, b) => Mathf.Abs(b.Result.Threshold.GlassShareDetected - b.Result.Threshold.GlassShareGlobal)
                .CompareTo(Mathf.Abs(a.Result.Threshold.GlassShareDetected - a.Result.Threshold.GlassShareGlobal)));
            var line = new StringBuilder();
            line.Append("glass threshold: ").Append(measured).Append(" assets measured, ").Append(lit).Append(" detected by their lit windows, ")
                .Append(shine).Append(" by shininess, ").Append(measured - lit - shine).Append(" keep the global");
            if (changed.Count > 0)
            {
                line.Append("; detected ").Append(Pct(lo)).Append("..").Append(Pct(hi)).Append("; the largest changes: ");
                for (int i = 0; i < changed.Count && i < ReportNames; i++)
                {
                    ThresholdResult t = changed[i].Result.Threshold;
                    line.Append(i > 0 ? "; " : "").Append('\'').Append(changed[i].Asset).Append("' (70% -> ").Append(Pct(t.Detected))
                        .Append(", glass ").Append(Pct(t.GlassShareGlobal)).Append(" -> ").Append(Pct(t.GlassShareDetected)).Append(')');
                }
                if (changed.Count > ReportNames)
                    line.Append("; and ").Append(changed.Count - ReportNames).Append(" more");
            }
            Log.Msg(line.ToString());
        }

        // ---------------------------------------------------------------------------------------
        // The places.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// A slice of the building buffer: each main building's model placed as the renderer places it
        /// (Building.CalculateMeshPosition, what BuildingAI.RefreshInstance makes m_dataMatrix1 of),
        /// with its asset's row; every other slot cleared. Procedural Objects' buildings every frame.
        /// </summary>
        private void Places()
        {
            Building[] buffer = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            int n = buffer.Length;
            for (int k = 0; k < PlacesPerFrame; k++)
            {
                int i = _next;
                _next = _next + 1 < n ? _next + 1 : 1;
                BuildingInfo info = buffer[i].Info;
                if ((buffer[i].m_flags & (Building.Flags.Created | Building.Flags.Deleted)) != Building.Flags.Created ||
                    buffer[i].m_parentBuilding != 0 || info == null)
                {
                    _textures.ClearPlace(i);
                    continue;
                }
                Vector3 position;
                Quaternion rotation;
                buffer[i].CalculateMeshPosition(out position, out rotation);
                Place(i, position, rotation, info);
            }

            List<ProceduralBuildings.Entry> entries = ProceduralBuildings.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                ProceduralBuildings.Entry e = entries[i];
                Vector3 position;
                Quaternion rotation;
                if (e.Index >= 0 && e.Info != null && ProceduralBuildings.Place(e, out position, out rotation))
                    Place(e.Index, position, rotation, e.Info);
            }
        }

        private void Place(int index, Vector3 position, Quaternion rotation, BuildingInfo info)
        {
            int row;
            if (!_rowOf.TryGetValue(info, out row))
                row = 0;
            Vector3 x = rotation * Vector3.right, z = rotation * Vector3.forward;
            x.y = 0f;
            z.y = 0f;
            _textures.SetPlace(index, position, x.normalized, z.normalized, row);
        }

        // ---------------------------------------------------------------------------------------
        // The report.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// After a round (the city as loaded, then the assets new in it): how many, how long, and what
        /// was found -- the first in full, every later one in a line.
        /// </summary>
        private void Report()
        {
            int storeys = 0, grid = 0, none = 0, failed = 0, sides = 0, narrow = 0;
            var noRooms = new List<Job>();
            var narrowOnes = new List<Job>();
            var failures = new List<Job>();
            var heights = new int[8];
            for (int i = 0; i < _round.Count; i++)
            {
                RoomFitResult r = _round[i].Result;
                if (!r.Measured)
                {
                    failed++;
                    failures.Add(_round[i]);
                    continue;
                }
                if (r.Storey > 0f)
                {
                    storeys++;
                    heights[Mathf.Clamp((int)((r.Storey - 2.5f) / 0.5f) + 1, 0, 7)]++;
                }
                else
                {
                    grid++;
                }
                if (!r.Rooms)
                {
                    none++;
                    noRooms.Add(_round[i]);
                }
                sides += r.SidesFound;
                narrow += r.SidesNarrow;
                if (r.SidesNarrow > 0)
                    narrowOnes.Add(_round[i]);
            }

            int remembered = 0;
            for (int i = 0; i < _round.Count; i++)
            {
                if (_round[i].Remembered)
                    remembered++;
            }

            double mainMs = _mainTicks * 1000.0 / Stopwatch.Frequency;
            string took = (_roundWatch != null ? (_roundWatch.ElapsedMilliseconds / 1000.0).ToString("F1") : "?") + " s (" + mainMs.ToString("F0") +
                          " ms of it on the main thread; " + remembered + " remembered from earlier loads, " + (_round.Count - remembered) + " measured)";
            if (!_reported)
            {
                _reported = true;
                Log.Msg("rooms: fitted " + _round.Count + " building assets to their windows in " + took + ": " + storeys +
                        " with a row of windows a storey (storeys under 2.5 m " + heights[0] + ", 2.5-3 m " + heights[1] + ", 3-3.5 m " + heights[2] +
                        ", 3.5-4 m " + heights[3] + ", 4 m and over " + (heights[4] + heights[5] + heights[6] + heights[7]) + "), " + grid +
                        " with none (the storey grid), " + none + " with no rooms (windows in strips), " + sides + " sides with a window rhythm, " +
                        narrow + " sides too narrow for a room, " + failed + " could not be measured");
                if (noRooms.Count > 0)
                    Log.Msg("rooms: no rooms (windows in strips): " + Names(noRooms, false));
                if (narrowOnes.Count > 0)
                    Log.Msg("rooms: sides too narrow for a room (none there): " + Names(narrowOnes, true));
                if (failures.Count > 0)
                    Log.Msg("rooms: could not be measured: " + Names(failures, false));
                if (Log.Detailed)
                {
                    for (int i = 0; i < _round.Count; i++)
                        Log.Detail("rooms: '" + _round[i].Asset + "' " + Outcome(_round[i].Result));
                }
            }
            else
            {
                var line = new StringBuilder();
                line.Append("rooms: ").Append(_round.Count).Append(" more asset").Append(_round.Count == 1 ? "" : "s").Append(" fitted (")
                    .Append(mainMs.ToString("F0")).Append(" ms on the main thread, ").Append(remembered).Append(" remembered): ");
                for (int i = 0; i < _round.Count && i < ReportNames; i++)
                    line.Append(i > 0 ? "; " : "").Append('\'').Append(_round[i].Asset).Append("' ").Append(Outcome(_round[i].Result));
                if (_round.Count > ReportNames)
                    line.Append("; and ").Append(_round.Count - ReportNames).Append(" more");
                Log.Msg(line.ToString());
            }

            ReportThresholds();
            _round.Clear();
            _roundWatch = null;
            _mainTicks = 0;
            SaveCache();
        }

        // ---------------------------------------------------------------------------------------
        // What earlier loads measured (RealGlassRoomFits.txt, next to RealGlass.xml).
        // ---------------------------------------------------------------------------------------

        private static string CachePath
        {
            get { return System.IO.Path.Combine(ColossalFramework.IO.DataLocation.localApplicationData, RoomFitCache.FileName); }
        }

        /// <summary>The file is read and written one at a time, whichever city's map does it (a city left while its write runs).</summary>
        private static readonly object FileLock = new object();

        /// <summary>The text to a .tmp beside the file, then put in its place. Throws (the callers catch).</summary>
        private static void WriteFile(string path, string text)
        {
            lock (FileLock)
            {
                string temp = path + ".tmp";
                System.IO.File.WriteAllText(temp, text, new UTF8Encoding(false));
                if (System.IO.File.Exists(path))
                    System.IO.File.Delete(path);
                System.IO.File.Move(temp, path);
            }
        }

        /// <summary>The file read once a city (a few hundred kB). Never throws: anything wrong with it means measuring again.</summary>
        private static RoomFitCache LoadCache()
        {
            try
            {
                string path = CachePath;
                if (!System.IO.File.Exists(path))
                {
                    Log.Msg("rooms: no " + RoomFitCache.FileName + " yet: every asset's windows are measured this time and remembered");
                    return new RoomFitCache();
                }
                int skipped;
                string reason;
                string text;
                lock (FileLock)
                    text = System.IO.File.ReadAllText(path, Encoding.UTF8);
                RoomFitCache cache = RoomFitCache.Parse(text, out skipped, out reason);
                if (reason.Length > 0)
                    Log.Msg("rooms: " + RoomFitCache.FileName + " not used (" + reason + "): every asset's windows are measured again");
                else
                    Log.Msg("rooms: " + cache.Count + " assets' windows remembered from " + RoomFitCache.FileName +
                            (skipped > 0 ? " (" + skipped + " lines not read: those are measured again)" : ""));
                return cache;
            }
            catch (Exception e)
            {
                Log.Msg("rooms: " + RoomFitCache.FileName + " could not be read (" + e.GetType().Name + ": " + e.Message + "): every asset's windows are measured again");
                return new RoomFitCache();
            }
        }

        /// <summary>
        /// After a round with something measured: the file written on a worker -- to a .tmp first, then
        /// put in place (as RealGlassAssets.xml is) -- one write at a time; a round ending while one is
        /// still writing is saved by the next round. A failure is said once and costs only remembering.
        /// </summary>
        private void SaveCache()
        {
            if (_cache == null || !_cache.Dirty || _saveFailed)
                return;
            lock (_saveLock)
            {
                if (_saving)
                    return;
                _saving = true;
            }
            string text = _cache.Format();
            string path = CachePath;
            ThreadPool.QueueUserWorkItem(unused =>
            {
                string problem = "";
                try
                {
                    WriteFile(path, text);
                }
                catch (Exception e)
                {
                    problem = e.GetType().Name + ": " + e.Message;
                }
                lock (_saveLock)
                {
                    _saving = false;
                    if (problem.Length > 0)
                    {
                        _saveFailed = true;
                        _saveProblem = problem;
                    }
                }
            });
        }

        /// <summary>One asset's fit in words, with its numbers.</summary>
        private static string Outcome(RoomFitResult r)
        {
            if (!r.Measured)
                return "could not be measured: " + r.Problem;
            var s = new StringBuilder();
            if (r.Storey > 0f)
            {
                s.Append("storeys ").Append(r.Storey.ToString("F3")).Append(" m (rhythm ").Append(r.StoreyRhythm.ToString("F2")).Append("), ")
                 .Append(r.GroundTop > 0f ? "ground storey to " + r.GroundTop.ToString("F2") + " m" : "floors at " + r.FloorPhase.ToString("F2") + " m")
                 .Append(", windows ").Append(r.WindowBand.ToString("F2")).Append(" m");
            }
            else
            {
                s.Append("no row of windows a storey (rhythm ").Append(r.StoreyRhythm.ToString("F2")).Append("): the storey grid");
            }
            s.Append("; ").Append(r.SidesFound).Append(" sides with a window rhythm");
            if (r.SidesNarrow > 0)
                s.Append(", ").Append(r.SidesNarrow).Append(" too narrow");
            if (!r.Rooms)
                s.Append("; NO ROOMS (windows in strips)");
            return s.ToString();
        }

        private static string Names(List<Job> list, bool narrow)
        {
            var line = new StringBuilder();
            for (int i = 0; i < list.Count && i < ReportNames; i++)
            {
                RoomFitResult r = list[i].Result;
                line.Append(i > 0 ? "; " : "").Append('\'').Append(list[i].Asset).Append('\'');
                if (!r.Measured)
                    line.Append(' ').Append(r.Problem);
                else if (narrow)
                    line.Append(' ').Append(r.SidesNarrow).Append(" of ").Append(Sided(r)).Append(" sides");
                else
                    line.Append(" windows ").Append(r.WindowBand.ToString("F2")).Append(" m a ").Append(r.Storey.ToString("F2")).Append(" m storey");
            }
            if (list.Count > ReportNames)
                line.Append("; and ").Append(list.Count - ReportNames).Append(" more");
            return line.ToString();
        }

        /// <summary>The sides big enough to speak of (RoomFit.MinSideArea).</summary>
        private static int Sided(RoomFitResult r)
        {
            int n = 0;
            for (int k = 0; k < r.Sides.Length; k++)
            {
                if (r.Sides[k].Area >= RoomFit.MinSideArea)
                    n++;
            }
            return n;
        }

        private void FreeReadBack()
        {
            if (_target != null)
            {
                _target.Release();
                UnityEngine.Object.DestroyImmediate(_target);
            }
            _target = null;
            if (_read != null)
                UnityEngine.Object.DestroyImmediate(_read);
            _read = null;
        }

        public void Destroy()
        {
            // A city left while assets were still being measured: what was measured is kept for the next load.
            try
            {
                bool saving;
                lock (_saveLock)
                    saving = _saving;
                if (_cache != null && _cache.Dirty && !_saveFailed && !saving)
                    WriteFile(CachePath, _cache.Format());
            }
            catch (Exception e)
            {
                Log.Msg("rooms: " + RoomFitCache.FileName + " could not be written on leaving the city (" + e.GetType().Name + ": " + e.Message + ")");
            }
            _cache = null;

            _textures.Destroy();
            FreeReadBack();
            if (_quad != null)
                UnityEngine.Object.DestroyImmediate(_quad);
            _quad = null;
            _seen.Clear();
            _toRead.Clear();
            _toMeasure.Clear();
            _rowOf.Clear();
            _resultOf.Clear();
            _detectedOf.Clear();
            _detectedInfos.Clear();
            DetectionsVersion++;
            _round.Clear();
            _failed = false;
            _editorInfo = null;
            _editorKey = null;
        }
    }
}
