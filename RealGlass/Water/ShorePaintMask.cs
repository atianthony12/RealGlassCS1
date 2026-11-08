using System;
using System.Collections.Generic;
using System.IO;

namespace RealGlass.Water
{
    /// <summary>
    /// Where the player painted the surf in or out over the automatic rule: a value per sample, -1 .. +1
    /// (0 = automatic, as the foam is without any paint), kept in tiles that exist only where something
    /// was painted. The foam's surf takes clamp01(automatic + paint): + brings the surf to water the rule
    /// leaves out (a lake, a reservoir), - takes it away (a harbour, a marina, a lagoon).
    /// Copied from ForestPainter's BrushMask (sparse tiles of sbytes, soft round dabs, run-length coded),
    /// with its marks and seeds left out and its cells moved onto the shore map's own grid: sample c at
    /// x = c x <see cref="Cell"/>, so the map's texel is c + 1080 (<see cref="ShoreDistance"/>'s 8 m
    /// samples from -8640 m) and the paint goes to the GPU unresampled.
    /// Pure (no game or Unity types): tested offline.
    /// </summary>
    public sealed class ShorePaintMask
    {
        /// <summary>Metres between samples: the shore map's (ShoreDistance.Spacing).</summary>
        public const float Cell = 8f;

        /// <summary>Samples a tile's side: a tile is 64 m, 64 bytes.</summary>
        public const int TileCells = 8;

        private const int TileLength = TileCells * TileCells;

        /// <summary>A value as stored: -127 .. 127 for -1 .. +1.</summary>
        public const float Unit = 127f;

        /// <summary>The most tiles a file may hold: the whole map (270 x 270 tiles) and some.</summary>
        private const int MaxTiles = 100000;

        private readonly Dictionary<long, sbyte[]> _tiles = new Dictionary<long, sbyte[]>();

        /// <summary>The tiles a stroke touched, as they were before it (for its undo step); null when not recording.</summary>
        private Dictionary<long, sbyte[]> _before;

        public int TileCount
        {
            get { return _tiles.Count; }
        }

        public bool Any
        {
            get { return _tiles.Count > 0; }
        }

        /// <summary>Goes up at every change.</summary>
        public int Version { get; private set; }

        /// <summary>The samples changed since the last <see cref="TakeDirty"/> (inclusive), or none.</summary>
        private bool _dirty;
        private int _dx0, _dz0, _dx1, _dz1;

        /// <summary>The value of one sample, -1 .. +1.</summary>
        public float CellValue(int cx, int cz)
        {
            return Raw(cx, cz) / Unit;
        }

        /// <summary>The stored value of one sample, -127 .. 127.</summary>
        public int Raw(int cx, int cz)
        {
            if (_tiles.Count == 0)
                return 0;
            int tx = FloorDiv(cx, TileCells), tz = FloorDiv(cz, TileCells);
            sbyte[] tile;
            if (!_tiles.TryGetValue(TileKey(tx, tz), out tile))
                return 0;
            return tile[(cz - tz * TileCells) * TileCells + (cx - tx * TileCells)];
        }

        /// <summary>The painted value at a point, bilinear between the samples.</summary>
        public float Sample(float x, float z)
        {
            if (_tiles.Count == 0)
                return 0f;
            float fx = x / Cell, fz = z / Cell;
            int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
            float tx = fx - ix, tz = fz - iz;
            float a = CellValue(ix, iz), b = CellValue(ix + 1, iz);
            float c = CellValue(ix, iz + 1), d = CellValue(ix + 1, iz + 1);
            float lo = a + (b - a) * tx, hi = c + (d - c) * tx;
            return lo + (hi - lo) * tz;
        }

        /// <summary>
        /// One dab of the brush: every sample within <paramref name="radius"/> of the point moves toward
        /// <paramref name="target"/> (-1 no surf, +1 surf, 0 automatic) by up to <paramref name="amount"/>
        /// (0..1), full out to <paramref name="hardness"/> x the radius and fading to nothing at its rim.
        /// A dab toward a target never overshoots it. Returns whether any sample changed.
        /// </summary>
        public bool Stamp(float x, float z, float radius, float hardness, float amount, int target)
        {
            if (radius <= 0f || amount <= 0f || float.IsNaN(amount) || float.IsNaN(x) || float.IsNaN(z))
                return false;

            int goal = target > 0 ? 127 : target < 0 ? -127 : 0;
            float inner = radius * Clamp01(hardness);
            int x0 = (int)Math.Floor((x - radius) / Cell), x1 = (int)Math.Ceiling((x + radius) / Cell);
            int z0 = (int)Math.Floor((z - radius) / Cell), z1 = (int)Math.Ceiling((z + radius) / Cell);
            bool changed = false;

            for (int cz = z0; cz <= z1; cz++)
            {
                for (int cx = x0; cx <= x1; cx++)
                {
                    float px = cx * Cell - x, pz = cz * Cell - z;
                    float d = (float)Math.Sqrt(px * px + pz * pz);
                    if (d >= radius)
                        continue;

                    float w = d <= inner ? 1f : 1f - Smooth((d - inner) / Math.Max(0.001f, radius - inner));
                    float raw = amount * w * Unit;
                    if (raw < 0.05f)
                        continue;
                    // At least one step: a fast sweep shares a frame's strength between many dabs, each tiny.
                    int step = Math.Max(1, (int)Math.Round(raw));

                    int tx = FloorDiv(cx, TileCells), tz = FloorDiv(cz, TileCells);
                    long key = TileKey(tx, tz);
                    sbyte[] tile;
                    if (!_tiles.TryGetValue(key, out tile))
                    {
                        // Nothing painted here: nothing to bring back to automatic.
                        if (goal == 0)
                            continue;
                        tile = new sbyte[TileLength];
                        Remember(key, null);
                        _tiles[key] = tile;
                    }

                    int i = (cz - tz * TileCells) * TileCells + (cx - tx * TileCells);
                    int old = tile[i];
                    int v = old < goal ? Math.Min(old + step, goal) : Math.Max(old - step, goal);
                    if (v == old)
                        continue;
                    Remember(key, tile);
                    tile[i] = (sbyte)v;
                    changed = true;
                    MarkDirty(cx, cz, cx, cz);
                }
            }

            if (changed)
                Version++;
            return changed;
        }

        /// <summary>Everything painted taken away.</summary>
        public void Clear()
        {
            if (_tiles.Count == 0)
                return;
            foreach (KeyValuePair<long, sbyte[]> t in _tiles)
            {
                int tx, tz;
                FromKey(t.Key, out tx, out tz);
                MarkDirty(tx * TileCells, tz * TileCells, tx * TileCells + TileCells - 1, tz * TileCells + TileCells - 1);
            }
            _tiles.Clear();
            Version++;
        }

        // ---- undo -------------------------------------------------------------------------------

        /// <summary>A stroke begins: the tiles it touches are copied as they were, once each.</summary>
        public void BeginStep()
        {
            _before = new Dictionary<long, sbyte[]>();
        }

        /// <summary>The stroke ends: what it changed, to put back (null if nothing).</summary>
        public Dictionary<long, sbyte[]> EndStep()
        {
            Dictionary<long, sbyte[]> before = _before;
            _before = null;
            return before != null && before.Count > 0 ? before : null;
        }

        /// <summary>The tiles back as a step holds them (null = the tile had nothing); returns the step that puts them back again (for redo).</summary>
        public Dictionary<long, sbyte[]> Restore(Dictionary<long, sbyte[]> step)
        {
            var inverse = new Dictionary<long, sbyte[]>();
            foreach (KeyValuePair<long, sbyte[]> t in step)
            {
                sbyte[] now;
                inverse[t.Key] = _tiles.TryGetValue(t.Key, out now) ? (sbyte[])now.Clone() : null;
                if (t.Value == null)
                    _tiles.Remove(t.Key);
                else
                    _tiles[t.Key] = (sbyte[])t.Value.Clone();
                int tx, tz;
                FromKey(t.Key, out tx, out tz);
                MarkDirty(tx * TileCells, tz * TileCells, tx * TileCells + TileCells - 1, tz * TileCells + TileCells - 1);
            }
            Version++;
            return inverse;
        }

        /// <summary>A copy of every tile, as a step (Clear's undo).</summary>
        public Dictionary<long, sbyte[]> Snapshot()
        {
            var all = new Dictionary<long, sbyte[]>();
            foreach (KeyValuePair<long, sbyte[]> t in _tiles)
                all[t.Key] = (sbyte[])t.Value.Clone();
            return all;
        }

        private void Remember(long key, sbyte[] tile)
        {
            if (_before == null || _before.ContainsKey(key))
                return;
            _before[key] = tile != null ? (sbyte[])tile.Clone() : null;
        }

        // ---- the GPU's map ---------------------------------------------------------------------

        /// <summary>The samples changed since the last call (inclusive, in samples); false if none.</summary>
        public bool TakeDirty(out int x0, out int z0, out int x1, out int z1)
        {
            x0 = _dx0;
            z0 = _dz0;
            x1 = _dx1;
            z1 = _dz1;
            bool any = _dirty;
            _dirty = false;
            return any;
        }

        /// <summary>Everything to be sent again (a new texture).</summary>
        public void MarkAllDirty()
        {
            MarkDirty(int.MinValue / 4, int.MinValue / 4, int.MaxValue / 4, int.MaxValue / 4);
        }

        /// <summary>
        /// The paint into a square map of <paramref name="side"/> bytes a side whose texel (0, 0) is sample
        /// (-<paramref name="centre"/>, -centre): 128 + the value (1..255; 128 automatic), over the given
        /// samples only (clamped to the map).
        /// </summary>
        public void Bake(byte[] map, int side, int centre, int x0, int z0, int x1, int z1)
        {
            int ix0 = Math.Max(x0 + centre, 0), ix1 = Math.Min(x1 + centre, side - 1);
            int iz0 = Math.Max(z0 + centre, 0), iz1 = Math.Min(z1 + centre, side - 1);
            if (ix0 > ix1 || iz0 > iz1)
                return;

            // The range back to automatic, then each tile in it written over (a tile at a time: the whole
            // map at a city's load is the tiles' samples, not 4.7 million look-ups).
            for (int iz = iz0; iz <= iz1; iz++)
            {
                int row = iz * side;
                for (int ix = ix0; ix <= ix1; ix++)
                    map[row + ix] = 128;
            }

            foreach (KeyValuePair<long, sbyte[]> t in _tiles)
            {
                int tx, tz;
                FromKey(t.Key, out tx, out tz);
                sbyte[] tile = t.Value;
                for (int j = 0; j < TileCells; j++)
                {
                    int iz = tz * TileCells + j + centre;
                    if (iz < iz0 || iz > iz1)
                        continue;
                    for (int k = 0; k < TileCells; k++)
                    {
                        int ix = tx * TileCells + k + centre;
                        if (ix < ix0 || ix > ix1)
                            continue;
                        map[iz * side + ix] = (byte)(128 + tile[j * TileCells + k]);
                    }
                }
            }
        }

        private void MarkDirty(int x0, int z0, int x1, int z1)
        {
            if (!_dirty)
            {
                _dx0 = x0;
                _dz0 = z0;
                _dx1 = x1;
                _dz1 = z1;
                _dirty = true;
                return;
            }
            _dx0 = Math.Min(_dx0, x0);
            _dz0 = Math.Min(_dz0, z0);
            _dx1 = Math.Max(_dx1, x1);
            _dz1 = Math.Max(_dz1, z1);
        }

        // ---- the save --------------------------------------------------------------------------

        /// <summary>Tiles left all zero (painted, then brought back to automatic) dropped.</summary>
        public void Compact()
        {
            var empty = new List<long>();
            foreach (KeyValuePair<long, sbyte[]> t in _tiles)
            {
                if (IsEmpty(t.Value))
                    empty.Add(t.Key);
            }
            foreach (long key in empty)
                _tiles.Remove(key);
        }

        /// <summary>Tile count, then per tile its x, z (shorts) and its samples run-length coded (count 1..255, value).</summary>
        public void Write(BinaryWriter writer)
        {
            Compact();
            var keys = new List<long>(_tiles.Keys);
            keys.Sort();
            writer.Write(keys.Count);
            foreach (long key in keys)
            {
                int tx, tz;
                FromKey(key, out tx, out tz);
                writer.Write((short)tx);
                writer.Write((short)tz);
                sbyte[] tile = _tiles[key];
                int i = 0;
                while (i < TileLength)
                {
                    sbyte v = tile[i];
                    int run = 1;
                    while (i + run < TileLength && run < 255 && tile[i + run] == v)
                        run++;
                    writer.Write((byte)run);
                    writer.Write(v);
                    i += run;
                }
            }
        }

        /// <summary>As written by <see cref="Write"/>; throws on data that does not fit.</summary>
        public static ShorePaintMask Read(BinaryReader reader)
        {
            var mask = new ShorePaintMask();
            int count = reader.ReadInt32();
            if (count < 0 || count > MaxTiles)
                throw new InvalidDataException("a shore paint tile count of " + count);

            for (int n = 0; n < count; n++)
            {
                int tx = reader.ReadInt16(), tz = reader.ReadInt16();
                var tile = new sbyte[TileLength];
                int i = 0;
                while (i < TileLength)
                {
                    int run = reader.ReadByte();
                    sbyte v = reader.ReadSByte();
                    if (run == 0 || i + run > TileLength || v == sbyte.MinValue)
                        throw new InvalidDataException("a shore paint tile's runs do not add up");
                    for (int k = 0; k < run; k++)
                        tile[i + k] = v;
                    i += run;
                }
                mask._tiles[TileKey(tx, tz)] = tile;
            }

            mask.MarkAllDirty();
            return mask;
        }

        /// <summary>A hash of the contents (the tests' and the log's).</summary>
        public int ContentHash()
        {
            var keys = new List<long>(_tiles.Keys);
            keys.Sort();
            uint h = 2166136261u;
            unchecked
            {
                foreach (long key in keys)
                {
                    sbyte[] tile = _tiles[key];
                    if (IsEmpty(tile))
                        continue;
                    h = (h ^ (uint)key) * 16777619u;
                    h = (h ^ (uint)(key >> 32)) * 16777619u;
                    foreach (sbyte v in tile)
                        h = (h ^ (byte)v) * 16777619u;
                }
            }
            return (int)h;
        }

        private static bool IsEmpty(sbyte[] tile)
        {
            foreach (sbyte v in tile)
            {
                if (v != 0)
                    return false;
            }
            return true;
        }

        private static long TileKey(int tx, int tz)
        {
            return ((long)tx << 32) | (uint)tz;
        }

        private static void FromKey(long key, out int tx, out int tz)
        {
            tx = (int)(key >> 32);
            tz = (int)(uint)key;
        }

        private static float Smooth(float t)
        {
            t = Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        private static float Clamp01(float t)
        {
            return t < 0f ? 0f : t > 1f ? 1f : t;
        }

        private static int FloorDiv(int a, int b)
        {
            return a >= 0 ? a / b : -((-a + b - 1) / b);
        }
    }
}
