using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using ColossalFramework;
using RealGlass.PerAsset;
using UnityEngine;

namespace RealGlass.Glass
{
    /// <summary>
    /// The game's side of <see cref="LodGlass"/>: the LODs of the building assets standing in the city
    /// (and their sub-meshes'), found in the building buffer (again every few seconds, for newly
    /// placed assets), read back on the GPU a batch a frame, compared with their models on worker
    /// threads, decided whenever a setting they depend on changes -- the switch, the shininess needed
    /// (an asset's own, else the global), an asset's own say -- and the mask made on a worker and
    /// uploaded when the LODs that take their model's glass are no longer the same.
    /// </summary>
    internal sealed class LodGlassMap
    {
        /// <summary>LODs read back and not yet compared, at most: each holds both meshes until it is.</summary>
        private const int MaxInFlight = 24;

        /// <summary>A frame's batch stops once its models' vertices copied reach this (a frame's hitch at load).</summary>
        private const int MaxVerticesPerFrame = 200000;

        /// <summary>How often the building buffer is looked through for assets not seen yet, seconds.</summary>
        private const float ScanEvery = 10f;

        /// <summary>A change of setting is decided on once it has rested this long (a slider being dragged), seconds.</summary>
        private const float DecideAfter = 0.3f;

        /// <summary>How many LODs each list of the report names.</summary>
        private const int ReportNames = 30;

        private sealed class Pending
        {
            public LodGlassEntry Entry;
            public BuildingInfoBase Info;
        }

        private readonly LodGlass _glass = new LodGlass();
        private readonly List<LodGlassEntry> _entries = new List<LodGlassEntry>();
        private readonly HashSet<BuildingInfoBase> _known = new HashSet<BuildingInfoBase>();
        private readonly HashSet<BuildingInfo> _seenInfos = new HashSet<BuildingInfo>();
        private readonly HashSet<string> _assets = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<Pending> _toRead = new Queue<Pending>();
        private readonly Queue<LodGlassEntry> _toCompare = new Queue<LodGlassEntry>();
        private readonly Dictionary<string, int> _verdictOf = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly int _workers = Mathf.Clamp(SystemInfo.processorCount / 2, 1, 3);

        // Shared with the workers (under _lock).
        private readonly object _lock = new object();
        private readonly List<LodGlassEntry> _compared = new List<LodGlassEntry>();
        private int _running;
        private byte[] _maskMade;
        private long _maskMs;
        private int _maskMadeFor;

        /// <summary>Which atlas's LODs a mask is for: one made for an atlas the game has since made anew is dropped.</summary>
        private int _generation;

        private Texture _atlas;
        private int _maskW, _maskH;
        private bool _failed;
        private float _nextScan;
        private bool _loggedStart;

        // A round of comparisons: from the first LOD found to the last compared.
        private int _roundFound, _roundDone;
        private Stopwatch _roundWatch;
        private long _mainTicks;

        /// <summary>The LODs compared in this round, for its report.</summary>
        private readonly List<LodGlassEntry> _roundEntries = new List<LodGlassEntry>();

        /// <summary>The first round (the city as loaded) is reported in full; every later one in one line.</summary>
        private bool _reported;

        // Deciding.
        private long _seenVersion = -1;
        private bool _seenOn;
        private float _seenThreshold = -1f;
        private long _seenDetections = -1;
        private float _decideAt = -1f;
        private bool _newlyCompared;

        // The mask.
        private bool _maskBuilding, _maskAgain;
        private int _maskBorrowing, _maskAssets;
        private string _maskReason = "";

        /// <summary>What the asset's LODs show (LodGlass.Verdict*); NotMeasured when nothing is known yet.</summary>
        public int VerdictOf(string asset)
        {
            int verdict;
            return asset != null && _verdictOf.TryGetValue(asset, out verdict) ? verdict : LodGlass.VerdictNotMeasured;
        }

        /// <summary>For the 5 s line: how far the comparison is, and how many LODs take their model's glass.</summary>
        public string Describe()
        {
            if (_failed)
                return "lod glass stopped";
            int done = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Transferred || _entries[i].Problem.Length > 0)
                    done++;
            }
            return "lod glass " + done + "/" + _entries.Count + " compared, " + _maskBorrowing + " showing the model's" +
                   (_glass.MaskWidth > 0 ? " (" + _glass.MaskWidth + "x" + _glass.MaskHeight + ")" : "");
        }

        /// <summary>Every frame the glass is on, after the atlases are bound.</summary>
        public void Update(GlassParams p, Texture atlasXys, Material util)
        {
            if (!_failed)
            {
                try
                {
                    Step(p, atlasXys, util);
                }
                catch (Exception e)
                {
                    _failed = true;
                    _glass.Destroy();
                    Log.Error("lod glass: failed; the LODs keep their own glass in this city", e);
                }
            }
            _glass.Bind();
        }

        private void Step(GlassParams p, Texture atlasXys, Material util)
        {
            if (atlasXys != _atlas)
            {
                // A new atlas (once a load): every rectangle may have moved. Start over.
                if (_atlas != null)
                {
                    Log.Msg("lod glass: the game made its LOD atlas anew: comparing again");
                    Forget();
                }
                _atlas = atlasXys;
                if (_atlas != null)
                    LodGlass.MaskSize(_atlas.width, _atlas.height, out _maskW, out _maskH);
            }

            // Nothing on the middle or far tiers, or nothing to read them with: nothing to do yet.
            if (GlassDraw.Tiers < 1 || _atlas == null || util == null)
                return;

            long start = Stopwatch.GetTimestamp();
            float now = Time.realtimeSinceStartup;
            if (now >= _nextScan)
            {
                _nextScan = now + ScanEvery;
                Scan();
            }

            ReadSome(util);
            Dispatch();
            Collect();

            bool idle = _toRead.Count == 0 && _toCompare.Count == 0 && Running() == 0;
            if (idle && _newlyCompared)
            {
                // A round done: decided, the mask made if it changed, and reported.
                _newlyCompared = false;
                _glass.FreeReadBack();
                Decide(p);
                Report();
            }

            long version = AssetRecords.Stamp;
            long detections = p.ThresholdAuto ? GlassController.DetectionsVersion() : -2;
            if (version != _seenVersion || p.LodGlass != _seenOn || p.Threshold != _seenThreshold || detections != _seenDetections)
            {
                bool first = _seenVersion < 0;
                _seenVersion = version;
                _seenOn = p.LodGlass;
                _seenThreshold = p.Threshold;
                _seenDetections = detections;
                if (!first)
                {
                    _decideAt = now + DecideAfter;
                    _maskReason = "after a change of setting";
                }
            }
            if (_decideAt >= 0f && now >= _decideAt && idle)
            {
                _decideAt = -1f;
                Decide(p);
            }

            FinishMask();
            _mainTicks += Stopwatch.GetTimestamp() - start;
        }

        // ---------------------------------------------------------------------------------------
        // Finding the LODs.
        // ---------------------------------------------------------------------------------------

        /// <summary>The building assets standing in the city that were not seen yet: their LODs, queued.</summary>
        private void Scan()
        {
            Building[] buffer = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            int added = 0;
            for (int i = 1; i < buffer.Length; i++)
            {
                if ((buffer[i].m_flags & (Building.Flags.Created | Building.Flags.Deleted)) != Building.Flags.Created)
                    continue;
                BuildingInfo info = buffer[i].Info;
                if (info == null || !_seenInfos.Add(info))
                    continue;

                added += Add(info.name, info, info.name);
                if (info.m_subMeshes == null)
                    continue;
                for (int k = 0; k < info.m_subMeshes.Length; k++)
                {
                    BuildingInfoBase sub = info.m_subMeshes[k].m_subInfo;
                    if (sub != null)
                        added += Add(info.name, sub, info.name + " / " + sub.name);
                }
            }

            if (added == 0)
                return;
            if (_roundFound == 0)
            {
                _roundWatch = Stopwatch.StartNew();
                _mainTicks = 0;
            }
            _roundFound += added;
            if (!_loggedStart)
            {
                _loggedStart = true;
                Log.Msg("lod glass: comparing " + added + " LODs of " + _assets.Count + " building assets in the city with their models (" +
                        _workers + " worker thread" + (_workers == 1 ? "" : "s") + "; a " + _maskW + " x " + _maskH + " mask for the " +
                        _atlas.width + " x " + _atlas.height + " atlas)");
            }
            else if (Log.Detailed)
            {
                Log.Detail("lod glass: " + added + " more LODs to compare (assets new in the city)");
            }
        }

        /// <summary>
        /// One LOD, queued, if the glass is drawn on it and its model: a building shader on both, and its
        /// rectangle of the atlas in its mesh data (uv3, uv4: BuildingInfoBase.InitMeshData). 1 if added.
        /// </summary>
        private int Add(string asset, BuildingInfoBase b, string name)
        {
            if (!_known.Add(b))
                return 0;

            RenderGroup.MeshData data = b.m_lodMeshData;
            Material model = b.m_material;
            if (data == null || data.m_vertices == null || data.m_uvs == null || data.m_triangles == null || data.m_uvs3 == null ||
                data.m_uvs4 == null || data.m_uvs3.Length == 0 || data.m_uvs4.Length == 0 || model == null || b.m_mesh == null ||
                b.m_lodMaterialCombined == null)
            {
                return 0;
            }
            if (!GlassDraw.IsBuildingShader(model.shader) || !GlassDraw.IsBuildingShader(b.m_lodMaterialCombined.shader))
                return 0;

            Vector2 corner = data.m_uvs3[0];
            Vector4 size = data.m_uvs4[0];
            var rect = new Vector4(Mathf.Abs(corner.x), Mathf.Abs(corner.y), Mathf.Abs(size.x), Mathf.Abs(size.y));
            if (!(rect.z > 0f) || !(rect.w > 0f))
                return 0;

            Texture xys = model.HasProperty("_XYSMap") ? model.GetTexture("_XYSMap") : null;
            Vector2 scale = model.mainTextureScale, offset = model.mainTextureOffset;
            var entry = new LodGlassEntry
            {
                Asset = asset,
                Name = name,
                Rect = rect,
                ModelXys = xys,
                ModelSt = new Vector4(scale.x, scale.y, offset.x, offset.y),
                ModelClamp = xys != null && xys.wrapMode == TextureWrapMode.Clamp,
            };
            LodGlass.SideFor(entry, _maskW, _maskH);
            _entries.Add(entry);
            _assets.Add(asset);
            _toRead.Enqueue(new Pending { Entry = entry, Info = b });
            return 1;
        }

        // ---------------------------------------------------------------------------------------
        // Reading back and comparing.
        // ---------------------------------------------------------------------------------------

        private int Running()
        {
            lock (_lock)
                return _running;
        }

        /// <summary>A batch a frame: both meshes copied (the workers touch no engine object), both pictures read back.</summary>
        private void ReadSome(Material util)
        {
            if (_toRead.Count == 0 || _toCompare.Count + Running() >= MaxInFlight)
                return;

            var batch = new List<LodGlassEntry>();
            int vertices = 0;
            while (_toRead.Count > 0 && batch.Count < LodGlass.PerBatch && vertices < MaxVerticesPerFrame)
            {
                Pending next = _toRead.Dequeue();
                string problem = CopyGeometry(next.Entry, next.Info);
                if (problem.Length > 0)
                {
                    next.Entry.Problem = problem;
                    lock (_lock)
                        _compared.Add(next.Entry);
                    continue;
                }
                vertices += next.Entry.ModelPos.Length / 3;
                batch.Add(next.Entry);
            }
            if (batch.Count == 0)
                return;

            if (!_glass.ReadBack(batch, 0, batch.Count, _atlas, util))
            {
                _failed = true;
                _glass.Destroy();
                Log.Warn("lod glass: the pictures could not be read back (" + _glass.Problem + "); the LODs keep their own glass in this city");
                return;
            }
            for (int i = 0; i < batch.Count; i++)
                _toCompare.Enqueue(batch[i]);
        }

        /// <summary>The meshes as plain arrays: positions, uv (the LOD's taken back out of its atlas rectangle), triangles.</summary>
        private static string CopyGeometry(LodGlassEntry e, BuildingInfoBase b)
        {
            try
            {
                Mesh mesh = b.m_mesh;
                Vector3[] v = mesh.vertices;
                Vector2[] uv = mesh.uv;
                int[] tri = mesh.triangles;
                if (v == null || v.Length < 3 || tri == null || tri.Length < 3)
                    return "the model's mesh could not be read";
                e.ModelPos = Flat(v);
                e.ModelUv = Flat(uv);
                e.ModelTri = tri;

                RenderGroup.MeshData d = b.m_lodMeshData;
                e.LodPos = Flat(d.m_vertices);
                var own = new float[d.m_uvs.Length * 2];
                for (int k = 0; k < d.m_uvs.Length; k++)
                {
                    own[2 * k] = (d.m_uvs[k].x - e.Rect.x) / e.Rect.z;
                    own[2 * k + 1] = (d.m_uvs[k].y - e.Rect.y) / e.Rect.w;
                }
                e.LodUv = own;
                e.LodTri = (int[])d.m_triangles.Clone();
                return "";
            }
            catch (Exception ex)
            {
                return "its meshes could not be read (" + ex.GetType().Name + ": " + ex.Message + ")";
            }
        }

        private static float[] Flat(Vector3[] v)
        {
            if (v == null)
                return null;
            var f = new float[v.Length * 3];
            for (int i = 0; i < v.Length; i++)
            {
                f[3 * i] = v[i].x;
                f[3 * i + 1] = v[i].y;
                f[3 * i + 2] = v[i].z;
            }
            return f;
        }

        private static float[] Flat(Vector2[] v)
        {
            if (v == null)
                return null;
            var f = new float[v.Length * 2];
            for (int i = 0; i < v.Length; i++)
            {
                f[2 * i] = v[i].x;
                f[2 * i + 1] = v[i].y;
            }
            return f;
        }

        /// <summary>As many comparisons running as there are workers.</summary>
        private void Dispatch()
        {
            while (_toCompare.Count > 0)
            {
                lock (_lock)
                {
                    if (_running >= _workers)
                        return;
                    _running++;
                }
                LodGlassEntry e = _toCompare.Dequeue();
                ThreadPool.QueueUserWorkItem(Compare, e);
            }
        }

        /// <summary>The worker: one LOD compared. Never throws (a thread's exception would end the game).</summary>
        private void Compare(object state)
        {
            var e = (LodGlassEntry)state;
            LodGlass.Transfer(e);
            lock (_lock)
            {
                _compared.Add(e);
                _running--;
            }
        }

        private void Collect()
        {
            lock (_lock)
            {
                if (_compared.Count == 0)
                    return;
                _roundDone += _compared.Count;
                _roundEntries.AddRange(_compared);
                _compared.Clear();
            }
            _newlyCompared = true;
        }

        // ---------------------------------------------------------------------------------------
        // Deciding, and the mask.
        // ---------------------------------------------------------------------------------------

        /// <summary>Every LOD's verdict at its asset's settings; the mask made anew when the LODs that take their model's glass changed.</summary>
        private void Decide(GlassParams p)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                LodGlassEntry e = _entries[i];
                AssetRecord record = AssetRecords.OfName(e.Asset);
                e.Mode = record == null || !record.LodGlass.HasValue ? LodGlass.ModeAuto : (record.LodGlass.Value ? LodGlass.ModeModel : LodGlass.ModeOwn);
                // Its own, else the one read from its textures ("Detect per asset"), else the global: AssetMap's rule.
                float detected = p.ThresholdAuto ? GlassController.DetectedThresholdOf(e.Asset) : 0f;
                e.Threshold = record != null && record.Threshold.HasValue ? record.Threshold.Value
                    : (ThresholdDetect.Applies(detected, p.Threshold) ? detected : p.Threshold);
            }

            bool changed = LodGlass.DecideAll(_entries, p.LodGlass);

            _verdictOf.Clear();
            for (int i = 0; i < _entries.Count; i++)
            {
                LodGlassEntry e = _entries[i];
                int had;
                if (!_verdictOf.TryGetValue(e.Asset, out had) || Rank(e.Verdict) > Rank(had))
                    _verdictOf[e.Asset] = e.Verdict;
            }

            if (changed)
                MakeMask();
        }

        /// <summary>The mask for every LOD that takes its model's glass, made on a worker (one at a time).</summary>
        private void MakeMask()
        {
            if (_maskBuilding)
            {
                _maskAgain = true;
                return;
            }

            var borrowing = new List<LodGlassEntry>();
            var assets = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < _entries.Count; i++)
            {
                LodGlassEntry e = _entries[i];
                if (LodGlass.Borrows(e.Verdict) && e.Grid != null)
                {
                    borrowing.Add(e);
                    assets.Add(e.Asset);
                }
            }

            bool had = _maskBorrowing > 0;
            _maskBorrowing = borrowing.Count;
            _maskAssets = assets.Count;
            if (borrowing.Count == 0)
            {
                _glass.Upload(null, 0, 0);
                if (had)
                    Log.Msg("lod glass: no LOD shows its model's glass now (" + _maskReason + ")");
                return;
            }

            _maskBuilding = true;
            int w = _maskW, h = _maskH, generation = _generation;
            ThreadPool.QueueUserWorkItem(unused =>
            {
                byte[] data = null;
                Stopwatch watch = Stopwatch.StartNew();
                try
                {
                    data = LodGlass.BuildMask(borrowing, w, h);
                }
                catch (Exception)
                {
                    data = null;
                }
                lock (_lock)
                {
                    _maskMade = data ?? new byte[0];
                    _maskMs = watch.ElapsedMilliseconds;
                    _maskMadeFor = generation;
                }
            });
        }

        /// <summary>A mask the worker made, uploaded (the main thread's part).</summary>
        private void FinishMask()
        {
            byte[] data;
            long ms;
            int generation;
            lock (_lock)
            {
                data = _maskMade;
                ms = _maskMs;
                generation = _maskMadeFor;
                _maskMade = null;
            }
            if (data == null)
                return;

            _maskBuilding = false;
            if (generation != _generation)
            {
                // Made for an atlas the game has since made anew: dropped, and the one asked for since made.
                if (_maskAgain && !_failed)
                {
                    _maskAgain = false;
                    MakeMask();
                }
                return;
            }
            Stopwatch watch = Stopwatch.StartNew();
            if (data.Length == 0)
            {
                Log.Warn("lod glass: the mask could not be made; the LODs keep their own glass");
                _glass.Upload(null, 0, 0);
            }
            else if (!_glass.Upload(data, _maskW, _maskH))
            {
                _failed = true;
                Log.Warn("lod glass: the mask could not be uploaded (" + _glass.Problem + "); the LODs keep their own glass in this city");
            }
            else
            {
                Log.Msg("lod glass: " + _maskBorrowing + " LODs of " + _maskAssets + " assets show their model's glass (a " + _maskW + " x " +
                        _maskH + " mask, made in " + ms + " ms on a worker, uploaded in " + watch.ElapsedMilliseconds + " ms" +
                        (_maskReason.Length > 0 ? "; " + _maskReason : "") + ")");
            }
            _maskReason = "";

            if (_maskAgain && !_failed)
            {
                _maskAgain = false;
                MakeMask();
            }
        }

        /// <summary>Which of an asset's LODs speaks for it in its building window: the one with the most to say.</summary>
        private static int Rank(int verdict)
        {
            switch (verdict)
            {
                case LodGlass.VerdictBorrowed:
                case LodGlass.VerdictForced:
                    return 8;
                case LodGlass.VerdictShapeDiffers:
                    return 7;
                case LodGlass.VerdictOff:
                    return 6;
                case LodGlass.VerdictKeptOwn:
                    return 5;
                case LodGlass.VerdictFailed:
                    return 4;
                case LodGlass.VerdictAgrees:
                    return 3;
                case LodGlass.VerdictNoGlass:
                    return 2;
                default:
                    return 0;
            }
        }

        // ---------------------------------------------------------------------------------------
        // The report.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// After a round of comparisons (and its decisions): how many, how long, and what each LOD was
        /// found to be -- with the numbers the verdicts' limits are judged by.
        /// </summary>
        private void Report()
        {
            // The first round (the city as loaded) in full; every later one -- a few LODs of assets new in
            // the city, as often as every ten seconds -- in one line naming only what it compared: the
            // full lists again each time would fill a growing city's log.
            if (_reported)
            {
                ReportRound();
                return;
            }
            _reported = true;

            var counts = new int[9];
            var borrowed = new List<LodGlassEntry>();
            var shape = new List<LodGlassEntry>();
            var failed = new List<LodGlassEntry>();
            int hadNone = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                LodGlassEntry e = _entries[i];
                counts[Mathf.Clamp(e.Verdict, 0, 8)]++;
                if (LodGlass.Borrows(e.Verdict))
                {
                    borrowed.Add(e);
                    if (LodGlass.HadNone(e))
                        hadNone++;
                }
                else if (e.Verdict == LodGlass.VerdictShapeDiffers)
                {
                    shape.Add(e);
                }
                else if (e.Verdict == LodGlass.VerdictFailed)
                {
                    failed.Add(e);
                }
            }

            double mainMs = _mainTicks * 1000.0 / Stopwatch.Frequency;
            Log.Msg("lod glass: compared " + _roundDone + " LODs in " + (_roundWatch != null ? (_roundWatch.ElapsedMilliseconds / 1000.0).ToString("F1") : "?") +
                    " s (" + mainMs.ToString("F0") + " ms of it on the main thread" + (_glass.Flipped ? ", read back bottom up" : "") + "); of " +
                    _entries.Count + ": " + counts[LodGlass.VerdictAgrees] + " agree with their model, " + counts[LodGlass.VerdictNoGlass] +
                    " neither has glass, " + borrowed.Count + " take the model's glass (" + hadNone + " had none of their own, " +
                    (borrowed.Count - hadNone) + " had it elsewhere" + (counts[LodGlass.VerdictForced] > 0 ? ", " + counts[LodGlass.VerdictForced] +
                    " asked for by their building window" : "") + "), " + counts[LodGlass.VerdictShapeDiffers] + " shaped unlike their model (left as they are), " +
                    counts[LodGlass.VerdictKeptOwn] + " kept as they are by their building window, " + counts[LodGlass.VerdictOff] +
                    " left as they are (switched off), " + failed.Count + " could not be compared");
            _roundFound = _roundDone = 0;
            _roundWatch = null;
            _roundEntries.Clear();

            if (borrowed.Count > 0)
            {
                borrowed.Sort((a, b) => b.ModelShare.CompareTo(a.ModelShare));
                Log.Msg("lod glass: taking their model's glass: " + Names(borrowed));
            }
            if (shape.Count > 0)
            {
                shape.Sort((a, b) => a.Reach.CompareTo(b.Reach));
                Log.Msg("lod glass: shaped unlike their model: " + Names(shape));
            }
            if (failed.Count > 0)
            {
                var line = new StringBuilder();
                for (int i = 0; i < failed.Count && i < ReportNames; i++)
                    line.Append(i > 0 ? "; " : "").Append('\'').Append(failed[i].Name).Append("' ").Append(failed[i].Problem);
                if (failed.Count > ReportNames)
                    line.Append("; and ").Append(failed.Count - ReportNames).Append(" more");
                Log.Msg("lod glass: could not compare: " + line);
            }
        }

        /// <summary>A later round, in one line: each LOD it compared with its verdict, then how many take the model's glass now.</summary>
        private void ReportRound()
        {
            double mainMs = _mainTicks * 1000.0 / Stopwatch.Frequency;
            var line = new StringBuilder();
            line.Append("lod glass: ").Append(_roundEntries.Count).Append(" more LOD").Append(_roundEntries.Count == 1 ? "" : "s")
                .Append(" compared (").Append(mainMs.ToString("F0")).Append(" ms on the main thread): ");
            for (int i = 0; i < _roundEntries.Count; i++)
            {
                if (i == ReportNames)
                {
                    line.Append("; and ").Append(_roundEntries.Count - i).Append(" more");
                    break;
                }
                LodGlassEntry e = _roundEntries[i];
                line.Append(i > 0 ? "; " : "").Append('\'').Append(e.Name).Append("' ").Append(Outcome(e));
            }
            int borrowing = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (LodGlass.Borrows(_entries[i].Verdict))
                    borrowing++;
            }
            line.Append(" -- of ").Append(_entries.Count).Append(" LODs, ").Append(borrowing).Append(" take the model's glass");
            Log.Msg(line.ToString());

            _roundFound = _roundDone = 0;
            _roundWatch = null;
            _roundEntries.Clear();
        }

        /// <summary>One LOD's verdict in words, with the numbers that decided it.</summary>
        private static string Outcome(LodGlassEntry e)
        {
            switch (e.Verdict)
            {
                case LodGlass.VerdictAgrees:
                    return "agrees with its model (alike " + e.Agreement.ToString("F2") + ")";
                case LodGlass.VerdictNoGlass:
                    return "neither has glass";
                case LodGlass.VerdictBorrowed:
                    return "takes the model's glass (met " + Pct(e.Reach) + ", model " + Pct(e.ModelShare) + " glass, LOD " +
                           Pct(e.LodShare) + ", alike " + e.Agreement.ToString("F2") + ")";
                case LodGlass.VerdictShapeDiffers:
                    return "shaped unlike its model (met " + Pct(e.Reach) + "), left as it is";
                case LodGlass.VerdictForced:
                    return "takes the model's glass (its building window's ask)";
                case LodGlass.VerdictKeptOwn:
                    return "kept as it is by its building window";
                case LodGlass.VerdictOff:
                    return "left as it is (switched off)";
                case LodGlass.VerdictFailed:
                    return "could not be compared: " + e.Problem;
                default:
                    return "not measured";
            }
        }

        /// <summary>Up to <see cref="ReportNames"/> LODs with their numbers: reach, gap, the glass of each, how alike.</summary>
        private static string Names(List<LodGlassEntry> list)
        {
            var line = new StringBuilder();
            for (int i = 0; i < list.Count && i < ReportNames; i++)
            {
                LodGlassEntry e = list[i];
                line.Append(i > 0 ? "; " : "").Append('\'').Append(e.Name).Append("' met ").Append(Pct(e.Reach))
                    .Append(" (gap ").Append(e.Gap.ToString("F2")).Append(" m), model ").Append(Pct(e.ModelShare))
                    .Append(" glass, LOD ").Append(Pct(e.LodShare)).Append(", alike ").Append(e.Agreement.ToString("F2"));
            }
            if (list.Count > ReportNames)
                line.Append("; and ").Append(list.Count - ReportNames).Append(" more");
            return line.ToString();
        }

        private static string Pct(float v)
        {
            return (v * 100f).ToString(v < 0.1f ? "F1" : "F0") + "%";
        }

        /// <summary>Everything known forgotten (a new atlas): found and compared again.</summary>
        private void Forget()
        {
            _generation++;
            _maskAgain = false;
            _entries.Clear();
            _known.Clear();
            _seenInfos.Clear();
            _assets.Clear();
            _toRead.Clear();
            _toCompare.Clear();
            _verdictOf.Clear();
            _nextScan = 0f;
            _roundFound = _roundDone = 0;
            _roundEntries.Clear();
            _reported = false;
            _maskBorrowing = 0;
            _glass.Upload(null, 0, 0);
        }

        public void Destroy()
        {
            _glass.Destroy();
            _entries.Clear();
            _known.Clear();
            _seenInfos.Clear();
            _assets.Clear();
            _toRead.Clear();
            _toCompare.Clear();
            _verdictOf.Clear();
            _atlas = null;
            _failed = false;
            _seenVersion = -1;
        }
    }
}
