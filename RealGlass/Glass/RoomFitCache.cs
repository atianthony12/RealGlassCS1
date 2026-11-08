using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace RealGlass.Glass
{
    /// <summary>
    /// What <see cref="RoomFit"/> found of each building asset, remembered between loads
    /// (RealGlassRoomFits.txt, next to RealGlass.xml): an asset already measured is not read back
    /// or measured again, so after a city's first load only assets new to it (or changed) cost
    /// anything. Each asset by its name and a fingerprint of what was measured -- its model's
    /// vertex and index counts, its shine map's size, its texture's scale and offset, the room of
    /// its kind, and <see cref="RoomFit.Version"/> -- so an asset updated on the Workshop with
    /// another model or map, or a newer way of measuring, is measured again. (Not the mesh's bounds:
    /// the game moves their bottom while it draws, BuildingAI.RenderMesh.) Its text is pure (Format,
    /// Parse: never throws; a line it cannot read is skipped and measured again); C# 4, engine types
    /// only in <see cref="Fingerprint"/>: the GPU preview runs this same file.
    /// </summary>
    public sealed class RoomFitCache
    {
        public const string FileName = "RealGlassRoomFits.txt";

        /// <summary>The file's first word, and its format.</summary>
        private const string Magic = "RealGlassRoomFits";
        private const int Format1 = 1;

        /// <summary>Format 2 (RoomFit.Version 2): a sixth part per line, the detected threshold (ThresholdDetect). Format 1 files still read.</summary>
        private const int Format2 = 2;

        /// <summary>At most this many assets are kept (every city's, every load's): the oldest go first.</summary>
        public const int MaxEntries = 10000;

        private sealed class Entry
        {
            public string Fingerprint;
            public RoomFitResult Result;
            public long Order;
        }

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private long _order;

        public int Count
        {
            get { return _entries.Count; }
        }

        /// <summary>Something was put in since the last Format.</summary>
        public bool Dirty { get; private set; }

        /// <summary>
        /// The fingerprint of what an asset's fit is measured from (its model, its shine map's size, its
        /// texture's scale and offset, the room of its kind). Reads counts only: nothing is copied.
        /// </summary>
        public static string Fingerprint(Mesh mesh, Texture shineMap, Vector4 st, float cellWidth, Texture illumination = null)
        {
            var s = new StringBuilder();
            s.Append('v').Append(RoomFit.Version).Append(' ').Append(mesh != null ? mesh.vertexCount : 0);
            int subMeshes = mesh != null ? mesh.subMeshCount : 0;
            s.Append(" s").Append(subMeshes);
            for (int k = 0; k < subMeshes; k++)
                s.Append(k == 0 ? " i" : ",").Append(mesh.GetIndexCount(k));
            s.Append(" t").Append(shineMap != null ? shineMap.width : 0).Append('x').Append(shineMap != null ? shineMap.height : 0);
            s.Append(" st").Append(Num(st.x)).Append(',').Append(Num(st.y)).Append(',').Append(Num(st.z)).Append(',').Append(Num(st.w));
            s.Append(" w").Append(Num(cellWidth));
            if (illumination != null)
                s.Append(" a").Append(illumination.width).Append('x').Append(illumination.height);
            return s.ToString();
        }

        /// <summary>The asset's remembered fit, when its fingerprint is the same.</summary>
        public bool TryGet(string asset, string fingerprint, out RoomFitResult result)
        {
            result = null;
            Entry e;
            if (asset == null || !_entries.TryGetValue(asset, out e) || e.Fingerprint != fingerprint)
                return false;
            e.Order = ++_order;
            result = e.Result;
            return true;
        }

        /// <summary>An asset's fit, measured now (only what RoomFit measured: a mesh the game would not give is tried again).</summary>
        public void Put(string asset, string fingerprint, RoomFitResult result)
        {
            if (asset == null || fingerprint == null || result == null)
                return;
            _entries[asset] = new Entry { Fingerprint = fingerprint, Result = result, Order = ++_order };
            Dirty = true;
        }

        // ---------------------------------------------------------------------------------------
        // The text.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// The file's text: two comment lines, the header, then a line per asset (the most recently
        /// used first, at most MaxEntries): name, fingerprint, problem ("" when measured), the
        /// storey's numbers, then each side's -- tabs between the parts, spaces between numbers.
        /// </summary>
        public string Format()
        {
            var list = new List<KeyValuePair<string, Entry>>(_entries);
            list.Sort((a, b) => b.Value.Order.CompareTo(a.Value.Order));
            var s = new StringBuilder();
            s.Append("# Real Glass & Water: each building asset's rooms fitted to its own windows, remembered between loads\n");
            s.Append("# so only new or changed assets are measured. Safe to delete: it is made again.\n");
            s.Append(Magic).Append(' ').Append(Format2).Append('\n');
            for (int i = 0; i < list.Count && i < MaxEntries; i++)
            {
                RoomFitResult r = list[i].Value.Result;
                s.Append(Clean(list[i].Key)).Append('\t').Append(Clean(list[i].Value.Fingerprint)).Append('\t')
                 .Append(r.Measured ? "" : Clean(r.Problem.Length > 0 ? r.Problem : "not measured")).Append('\t');
                s.Append(Num(r.Storey)).Append(' ').Append(Num(r.StoreyRhythm)).Append(' ').Append(Num(r.FloorPhase)).Append(' ')
                 .Append(Num(r.GroundTop)).Append(' ').Append(Num(r.WindowBand)).Append(' ').Append(r.Rooms ? 1 : 0).Append(' ')
                 .Append(Num(r.UprightArea)).Append(' ').Append(r.Samples).Append('\t');
                for (int k = 0; k < RoomFit.Sides; k++)
                {
                    RoomFitSide f = r.Sides[k];
                    s.Append(k > 0 ? ";" : "").Append(Num(f.Area)).Append(' ').Append(f.Found ? 1 : 0).Append(' ').Append(Num(f.Rhythm)).Append(' ')
                     .Append(Num(f.Module)).Append(' ').Append(Num(f.Width)).Append(' ').Append(Num(f.Phase)).Append(' ').Append(Num(f.NarrowShare));
                }
                // The threshold: detected (0 none), its source, the lit share, the glass share at the game's 70% and at it; "-" not measured.
                ThresholdResult t = r.Threshold;
                s.Append('\t');
                if (t != null && t.Measured)
                    s.Append(Num(t.Detected)).Append(' ').Append(t.Source).Append(' ').Append(Num(t.LitShare)).Append(' ')
                     .Append(Num(t.GlassShareGlobal)).Append(' ').Append(Num(t.GlassShareDetected));
                else
                    s.Append('-');
                s.Append('\n');
            }
            Dirty = false;
            return s.ToString();
        }

        /// <summary>
        /// A cache read from text. Never throws: a file of another format (or not ours) gives an empty
        /// cache and a reason; a line that cannot be read is skipped (counted). reason: "" when read.
        /// </summary>
        public static RoomFitCache Parse(string text, out int skipped, out string reason)
        {
            var cache = new RoomFitCache();
            skipped = 0;
            reason = "";
            if (string.IsNullOrEmpty(text))
            {
                reason = "empty";
                return cache;
            }

            string[] lines = text.Split('\n');
            bool header = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#')
                    continue;
                if (!header)
                {
                    string[] h = line.Split(' ');
                    int format;
                    if (h.Length < 2 || h[0] != Magic || !int.TryParse(h[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out format))
                    {
                        reason = "not a file of fits";
                        return cache;
                    }
                    if (format != Format1 && format != Format2)
                    {
                        reason = "format " + format + ", not " + Format1 + " or " + Format2;
                        return cache;
                    }
                    header = true;
                    continue;
                }

                string name, fingerprint;
                RoomFitResult r;
                if (TryLine(line, out name, out fingerprint, out r))
                    cache._entries[name] = new Entry { Fingerprint = fingerprint, Result = r, Order = -i };
                else
                    skipped++;
            }
            if (!header)
                reason = "no header";
            cache._order = 0;
            return cache;
        }

        private static bool TryLine(string line, out string name, out string fingerprint, out RoomFitResult r)
        {
            name = fingerprint = null;
            r = null;
            try
            {
                string[] parts = line.Split('\t');
                if ((parts.Length != 5 && parts.Length != 6) || parts[0].Length == 0 || parts[1].Length == 0)
                    return false;
                string[] own = parts[3].Split(' ');
                string[] sides = parts[4].Split(';');
                if (own.Length != 8 || sides.Length != RoomFit.Sides)
                    return false;

                var result = new RoomFitResult();
                result.Problem = parts[2];
                result.Measured = parts[2].Length == 0;
                result.Storey = Float(own[0]);
                result.StoreyRhythm = Float(own[1]);
                result.FloorPhase = Float(own[2]);
                result.GroundTop = Float(own[3]);
                result.WindowBand = Float(own[4]);
                result.Rooms = own[5] != "0";
                result.UprightArea = Float(own[6]);
                result.Samples = int.Parse(own[7], NumberStyles.Integer, CultureInfo.InvariantCulture);
                for (int k = 0; k < RoomFit.Sides; k++)
                {
                    string[] f = sides[k].Split(' ');
                    if (f.Length != 7)
                        return false;
                    RoomFitSide s = result.Sides[k];
                    s.Area = Float(f[0]);
                    s.Found = f[1] != "0";
                    s.Rhythm = Float(f[2]);
                    s.Module = Float(f[3]);
                    s.Width = Float(f[4]);
                    s.Phase = Float(f[5]);
                    s.NarrowShare = Float(f[6]);
                    if (s.Found && !(s.Width > 0f))
                        return false;   // a found side of no width would lay out no rooms
                    if (s.Found)
                        result.SidesFound++;
                    if (s.Narrow && s.Area >= RoomFit.MinSideArea)
                        result.SidesNarrow++;
                }
                if (result.Storey < 0f || result.Storey > RoomFit.MaxStorey * 1.1f)
                    return false;

                if (parts.Length == 6 && parts[5] != "-")
                {
                    string[] t = parts[5].Split(' ');
                    if (t.Length != 5)
                        return false;
                    var th = new ThresholdResult { Measured = true };
                    th.Detected = Float(t[0]);
                    th.Source = int.Parse(t[1], NumberStyles.Integer, CultureInfo.InvariantCulture);
                    th.LitShare = Float(t[2]);
                    th.GlassShareGlobal = Float(t[3]);
                    th.GlassShareDetected = Float(t[4]);
                    if (th.Detected != 0f && (th.Detected < ThresholdDetect.Lowest - 1e-3f || th.Detected > ThresholdDetect.Highest + 1e-3f))
                        return false;   // no verdict the measure could give
                    if (th.Source < ThresholdDetect.SourceNone || th.Source > ThresholdDetect.SourceShininess)
                        return false;
                    result.Threshold = th;
                }

                name = parts[0];
                fingerprint = parts[1];
                r = result;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>A number as text that reads back the same, in any culture; NaN and infinities refused on reading.</summary>
        private static string Num(float v)
        {
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        private static float Float(string s)
        {
            float v = float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (float.IsNaN(v) || float.IsInfinity(v))
                throw new FormatException("not a finite number");
            return v;
        }

        /// <summary>No tab or line break inside a part.</summary>
        private static string Clean(string s)
        {
            return s == null ? "" : s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
