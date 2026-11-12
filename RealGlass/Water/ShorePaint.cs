using System;
using System.Collections.Generic;
using System.IO;
using ICities;

namespace RealGlass.Water
{
    /// <summary>
    /// The city's painted shores (<see cref="ShorePaintMask"/>) and the steps back through them (Ctrl+Z,
    /// Ctrl+Y). Main thread only, but for the save's copy (the simulation thread), under <see cref="Sync"/>.
    /// </summary>
    public static class ShorePaint
    {
        public static readonly object Sync = new object();

        private const int MaxSteps = 50;

        private static ShorePaintMask _mask = new ShorePaintMask();
        private static readonly List<Dictionary<long, sbyte[]>> Undo = new List<Dictionary<long, sbyte[]>>();
        private static readonly List<Dictionary<long, sbyte[]>> Redo = new List<Dictionary<long, sbyte[]>>();

        /// <summary>The city's paint. Main thread.</summary>
        public static ShorePaintMask Mask
        {
            get { return _mask; }
        }

        /// <summary>Goes up when a whole new mask comes (a city loaded): the texture is made again from it.</summary>
        public static int Generation { get; private set; }

        public static bool CanUndo
        {
            get { return Undo.Count > 0; }
        }

        /// <summary>A city's paint in (its load), the steps back forgotten.</summary>
        public static void Load(ShorePaintMask mask)
        {
            lock (Sync)
                _mask = mask ?? new ShorePaintMask();
            Undo.Clear();
            Redo.Clear();
            Generation++;
        }

        /// <summary>A finished stroke's step (from ShorePaintMask.EndStep), to go back through.</summary>
        public static void AddStep(Dictionary<long, sbyte[]> step)
        {
            if (step == null)
                return;
            Undo.Add(step);
            if (Undo.Count > MaxSteps)
                Undo.RemoveAt(0);
            Redo.Clear();
        }

        public static bool StepBack()
        {
            return Move(Undo, Redo);
        }

        public static bool StepForward()
        {
            return Move(Redo, Undo);
        }

        private static bool Move(List<Dictionary<long, sbyte[]>> from, List<Dictionary<long, sbyte[]>> to)
        {
            if (from.Count == 0)
                return false;
            Dictionary<long, sbyte[]> step = from[from.Count - 1];
            from.RemoveAt(from.Count - 1);
            Dictionary<long, sbyte[]> inverse;
            lock (Sync)
                inverse = _mask.Restore(step);
            to.Add(inverse);
            return true;
        }

        /// <summary>Everything painted taken away, as one step back.</summary>
        public static void ClearAll()
        {
            if (!_mask.Any)
                return;
            Dictionary<long, sbyte[]> step;
            lock (Sync)
            {
                step = _mask.Snapshot();
                _mask.Clear();
            }
            AddStep(step);
            Log.Msg("water: the painted shores cleared (Ctrl+Z in the paint tool brings them back)");
        }

        /// <summary>The paint as the save keeps it: byte format (1), then the mask (ShorePaintMask.Write). Null when nothing is painted.</summary>
        public static byte[] Encode()
        {
            lock (Sync)
            {
                _mask.Compact();
                if (!_mask.Any)
                    return null;
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write((byte)1);
                    _mask.Write(writer);
                    writer.Flush();
                    return stream.ToArray();
                }
            }
        }

        /// <summary>As written by <see cref="Encode"/>; null (and why) for data this version cannot read.</summary>
        public static ShorePaintMask Decode(byte[] data, out string problem)
        {
            problem = null;
            if (data == null || data.Length == 0)
                return new ShorePaintMask();
            try
            {
                using (var reader = new BinaryReader(new MemoryStream(data)))
                {
                    int format = reader.ReadByte();
                    if (format != 1)
                    {
                        problem = "format " + format + " is newer than this version reads";
                        return null;
                    }
                    ShorePaintMask mask = ShorePaintMask.Read(reader);
                    if (reader.BaseStream.Position != data.Length)
                    {
                        problem = (data.Length - reader.BaseStream.Position) + " bytes left over";
                        return null;
                    }
                    return mask;
                }
            }
            catch (Exception e)
            {
                problem = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }
    }

    /// <summary>
    /// Keeps the painted shores with the city: the game's own save data for mods (found by the game like
    /// the mod's other extensions), one key. ForestPainter's AreaSaveData arrangement: data this version
    /// cannot read (a newer version's) is KEPT and written back exactly as loaded, and nothing can be
    /// painted in that city, so going back a version loses nothing. A city never painted carries no key.
    /// </summary>
    public sealed class ShorePaintSaveData : SerializableDataExtensionBase
    {
        /// <summary>The key in the city's save. PERMANENT.</summary>
        public const string Key = "RealGlass.ShorePaint";

        private static volatile byte[] _kept;

        /// <summary>This city's paint could not be read: it is kept as it was, and cannot be painted.</summary>
        public static bool Keeping
        {
            get { return _kept != null; }
        }

        public override void OnLoadData()
        {
            base.OnLoadData();
            _kept = null;
            byte[] data = null;
            try
            {
                data = serializableDataManager.LoadData(Key);
                string problem;
                ShorePaintMask mask = ShorePaint.Decode(data, out problem);
                if (mask == null)
                {
                    ShorePaint.Load(null);
                    _kept = data;
                    Log.Warn("water: this city's painted shores are kept exactly as they were loaded, at every save, and cannot be painted (" + problem + ")");
                    return;
                }

                ShorePaint.Load(mask);
                if (data != null)
                    Log.Msg("water: painted shores loaded with the city (" + mask.TileCount + " tiles of 64 m, " + data.Length + " bytes)");
            }
            catch (Exception e)
            {
                ShorePaint.Load(null);
                if (data != null && data.Length > 0)
                    _kept = data;
                Log.Error("water: loading the painted shores threw; they are kept as they were", e);
            }
        }

        public override void OnSaveData()
        {
            base.OnSaveData();
            try
            {
                byte[] kept = _kept;
                if (kept != null)
                {
                    serializableDataManager.SaveData(Key, kept);
                    Log.Msg("water: painted shores saved back exactly as they were loaded (" + kept.Length + " bytes)");
                    return;
                }

                byte[] data = ShorePaint.Encode();
                if (data == null)
                {
                    foreach (string key in serializableDataManager.EnumerateData())
                    {
                        if (key == Key)
                        {
                            serializableDataManager.EraseData(Key);
                            break;
                        }
                    }
                    return;
                }

                serializableDataManager.SaveData(Key, data);
                Log.Msg("water: painted shores saved with the city (" + data.Length + " bytes)");
            }
            catch (Exception e)
            {
                Log.Error("water: saving the painted shores threw; the save goes on without them", e);
            }
        }
    }
}
