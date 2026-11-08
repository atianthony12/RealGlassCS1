using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ColossalFramework.IO;
using UnityEngine;

namespace RealGlass.PerAsset
{
    /// <summary>
    /// RealGlassAssets.xml, next to RealGlass.xml: the glass each building asset has of its own,
    /// for every copy of it in every city (<see cref="AssetFileFormat"/>). Nothing goes into a save.
    /// </summary>
    /// <remarks>
    /// Kept as RealGlass.xml is (<see cref="SettingsXml"/>): read at startup and again within a
    /// second of an edit on disk; written a second after the last change (a slider drag is one
    /// write), through a temporary file, and not at all when the text would be the same; a file
    /// that cannot be read AT ALL is never saved over. It is only made once some asset has glass of
    /// its own.
    /// </remarks>
    public static class AssetFile
    {
        public const string FileName = "RealGlassAssets.xml";

        private const float SaveDelay = 1f;
        private const float FileCheckInterval = 1f;

        private static readonly Dictionary<string, AssetRecord> Records = new Dictionary<string, AssetRecord>(StringComparer.Ordinal);

        /// <summary>The props with a surface of ours (glass or water), by PropInfo name (1.1.9).</summary>
        private static readonly Dictionary<string, PropRecord> Props = new Dictionary<string, PropRecord>(StringComparer.Ordinal);

        /// <summary>The assets changed in the game since the last save: one log line each when it is saved.</summary>
        private static readonly List<string> Changed = new List<string>();

        /// <summary>The props changed in the game since the last save, as <see cref="Changed"/>.</summary>
        private static readonly List<string> ChangedProps = new List<string>();

        private static string _path;
        private static bool _loaded;
        private static int _version;

        private static bool _dirty;
        private static int _changeStamp, _seenStamp;
        private static float _quietSince;
        private static float _nextFileCheck;

        private static bool _broken, _warnedBrokenSave;
        private static string _diskText;
        private static DateTime _seenTime;
        private static long _seenLength = -1;
        private static bool _replaceFailed;
        private static string _lastSaveError;

        public static string Path
        {
            get
            {
                if (_path == null)
                    _path = System.IO.Path.Combine(DataLocation.localApplicationData, FileName);
                return _path;
            }
        }

        private static string TempPath
        {
            get { return Path + ".tmp"; }
        }

        /// <summary>Goes up whenever any asset's glass changes -- by the panel or by an edit of the file.</summary>
        public static int Version
        {
            get { return _version; }
        }

        /// <summary>How many assets have glass of their own.</summary>
        public static int Count
        {
            get { return Records.Count; }
        }

        private static int _anyInteriorsVersion = -1;
        private static bool _anyInteriors;

        /// <summary>Some asset has rooms of its own switched ON (they show even while the Interiors tab's switch is off).</summary>
        public static bool AnyInteriorsOn
        {
            get
            {
                if (_anyInteriorsVersion != _version)
                {
                    _anyInteriorsVersion = _version;
                    _anyInteriors = false;
                    foreach (AssetRecord record in Records.Values)
                    {
                        if (record.Interiors.HasValue && record.Interiors.Value)
                        {
                            _anyInteriors = true;
                            break;
                        }
                    }
                }
                return _anyInteriors;
            }
        }

        /// <summary>The asset's own glass, or null when it follows every global setting. Do not change it: <see cref="Put"/> a copy.</summary>
        public static AssetRecord Get(string name)
        {
            AssetRecord record;
            return name != null && Records.TryGetValue(name, out record) ? record : null;
        }

        /// <summary>Every asset with glass of its own.</summary>
        public static ICollection<AssetRecord> All
        {
            get { return Records.Values; }
        }

        /// <summary>
        /// Stores an asset's glass (a record with nothing of its own removes it), saved a second
        /// after the last change.
        /// </summary>
        public static void Put(AssetRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.Name))
                return;

            AssetRecord old = Get(record.Name);
            if (record.IsEmpty)
            {
                if (old == null)
                    return;
                Records.Remove(record.Name);
            }
            else
            {
                if (old != null && old.SameSettings(record) && old.Title == record.Title)
                    return;
                Records[record.Name] = record.Clone();
            }

            _version++;
            if (!Changed.Contains(record.Name))
                Changed.Add(record.Name);
            MarkDirty();
        }

        /// <summary>The prop's surface, or null when it is drawn as the game draws it. Do not change it: <see cref="PutProp"/> a copy.</summary>
        public static PropRecord GetProp(string name)
        {
            PropRecord record;
            return name != null && Props.TryGetValue(name, out record) ? record : null;
        }

        /// <summary>Every prop with a surface of ours.</summary>
        public static ICollection<PropRecord> AllProps
        {
            get { return Props.Values; }
        }

        public static int PropCount
        {
            get { return Props.Count; }
        }

        /// <summary>Stores a prop's surface (one with none removes it), saved a second after the last change.</summary>
        public static void PutProp(PropRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.Name))
                return;

            PropRecord old = GetProp(record.Name);
            if (record.IsEmpty)
            {
                if (old == null)
                    return;
                Props.Remove(record.Name);
            }
            else
            {
                if (old != null && old.SameSettings(record) && old.Title == record.Title)
                    return;
                Props[record.Name] = record.Clone();
            }

            _version++;
            if (!ChangedProps.Contains(record.Name))
                ChangedProps.Add(record.Name);
            MarkDirty();
        }

        /// <summary>What the changed assets now have, one line (written with the save that keeps them).</summary>
        private static void LogChanges()
        {
            if (Changed.Count == 0 && ChangedProps.Count == 0)
                return;

            var parts = new List<string>();
            foreach (string name in Changed)
            {
                AssetRecord record = Get(name);
                parts.Add("'" + name + "' " + (record == null ? "the global glass" : AssetSelection.Describe(record)));
            }
            foreach (string name in ChangedProps)
            {
                PropRecord prop = GetProp(name);
                parts.Add("prop '" + name + "' " + (prop == null ? "as the game draws it" : PropSelection.Describe(prop)));
            }

            Changed.Clear();
            ChangedProps.Clear();
            Log.Msg("assets: " + string.Join("; ", parts.ToArray()));
        }

        private static void MarkDirty()
        {
            _dirty = true;
            _changeStamp++;
        }

        // ---- startup --------------------------------------------------------------------------

        /// <summary>Once, from Settings.Init.</summary>
        public static void Load()
        {
            if (_loaded)
                return;
            _loaded = true;

            try
            {
                string file = File.Exists(Path) ? Path : (File.Exists(TempPath) ? TempPath : null);
                if (file == null)
                {
                    Log.Msg("assets: no " + FileName + " yet: every building asset follows the global glass");
                    return;
                }

                if (file == TempPath)
                    Log.Warn("assets: " + FileName + " is missing but an unfinished save of it is there; using that");

                string text = File.ReadAllText(file);
                if (file == Path)
                    Remember(text);

                var problems = new List<string>();
                string error = Apply(text, problems);
                if (error != null)
                {
                    _broken = true;
                    WarnUnreadable(file, error, "");
                }
                else
                {
                    Log.Msg("assets: " + Records.Count + " building asset" + (Records.Count == 1 ? "" : "s") + " with glass of their own" +
                            (Props.Count > 0 ? ", " + Props.Count + " prop" + (Props.Count == 1 ? "" : "s") + " made glass or water" : "") + ", from " + file);
                }

                LogProblems(problems);
            }
            catch (Exception e)
            {
                Log.Error("assets: could not load " + Path + "; every asset follows the global glass", e);
            }
        }

        /// <summary>
        /// Takes every record the text holds, replacing the ones there were -- all of them, or, from
        /// a file that is not well-formed, every one that could be salvaged (AssetFileFormat.Parse
        /// never throws). Returns null when the whole file was read, else why not.
        /// </summary>
        private static string Apply(string text, List<string> problems)
        {
            int version;
            string error;
            var props = new List<PropRecord>();
            List<AssetRecord> records = AssetFileFormat.Parse(text, out version, problems, out error, props);

            if (version > AssetFileFormat.Version)
                problems.Add("the file is from a NEWER version of the mod (format " + version + ")");

            Records.Clear();
            foreach (AssetRecord record in records)
            {
                if (!record.IsEmpty)
                    Records[record.Name] = record;
            }

            Props.Clear();
            foreach (PropRecord prop in props)
            {
                if (!prop.IsEmpty)
                    Props[prop.Name] = prop;
            }

            _version++;
            return error;
        }

        private const string UnreadableName = "RealGlassAssets.unreadable.xml";

        private static string UnreadablePath
        {
            get { return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path), UnreadableName); }
        }

        private static void WarnUnreadable(string file, string error, string when)
        {
            Log.Warn("assets: " + file + when + " could not be read in full (" + error + "): " + Records.Count +
                     " asset" + (Records.Count == 1 ? "" : "s") + " taken from it all the same, one by one. It is NOT saved " +
                     "over while it stays so: fix it and it is read again within a second. Change an asset's glass in the " +
                     "game first, and the file is kept as " + UnreadableName + " before the mod writes it again.");
        }

        private static void LogProblems(List<string> problems)
        {
            if (problems.Count > 0)
                Log.Warn("assets: not taken as written -- " + string.Join("; ", problems.ToArray()));
        }

        // ---- the running game -----------------------------------------------------------------

        /// <summary>Every frame (SettingsSaver), at the main menu and in a city.</summary>
        public static void Tick()
        {
            if (!_loaded)
                return;

            float now = Time.realtimeSinceStartup;
            if (_dirty)
            {
                if (_changeStamp != _seenStamp)
                {
                    _seenStamp = _changeStamp;
                    _quietSince = now;
                }
                else if (now - _quietSince >= SaveDelay)
                {
                    SaveNow();
                }
            }

            if (now < _nextFileCheck)
                return;
            _nextFileCheck = now + FileCheckInterval;
            CheckFile();
        }

        /// <summary>Writes a pending change now: quitting, the mod being disabled.</summary>
        public static void Flush()
        {
            if (_dirty)
                SaveNow();
        }

        private static void CheckFile()
        {
            try
            {
                var info = new FileInfo(Path);
                if (!info.Exists)
                {
                    // Deleted while the game runs: written again from what is in use, if anything.
                    if (_diskText != null || _broken)
                    {
                        Log.Msg("assets: " + FileName + " was deleted while the game runs; writing it again from the assets' glass in use " +
                                "(delete it with the game closed to give every asset the global glass)");
                        _diskText = null;
                        _broken = false;
                        SaveNow();
                    }
                    return;
                }

                if (info.LastWriteTimeUtc == _seenTime && info.Length == _seenLength)
                    return;
            }
            catch (Exception e)
            {
                Log.Detail("assets: could not look at " + Path + ": " + e.Message);
                return;
            }

            Reload();
        }

        /// <summary>The file changed on disk and it was not us: take what it says.</summary>
        private static void Reload()
        {
            string text;
            try
            {
                text = File.ReadAllText(Path);
            }
            catch (Exception)
            {
                return; // an editor is still writing it; look again in a second
            }

            bool same = text == _diskText;
            Remember(text);
            if (same)
                return;

            var problems = new List<string>();
            string error = Apply(text, problems);
            if (error != null)
            {
                _broken = true;
                WarnUnreadable(FileName, error, " was edited and");
                LogProblems(problems);
                return;
            }

            bool wasBroken = _broken;
            _broken = false;
            _warnedBrokenSave = false;
            Log.Msg("assets: " + FileName + " was edited" + (wasBroken ? " and can be read again" : "") + " -- " + Records.Count +
                    " building asset" + (Records.Count == 1 ? "" : "s") + " with glass of their own, " + Props.Count +
                    " prop" + (Props.Count == 1 ? "" : "s") + " made glass or water");
            LogProblems(problems);
        }

        // ---- saving ---------------------------------------------------------------------------

        public static void SaveNow()
        {
            _dirty = false;
            _seenStamp = _changeStamp;
            LogChanges();

            if (_broken)
            {
                // A glass was changed in the game while the file on disk cannot be read in full:
                // that file is kept aside first, so neither the change nor the hand edit is lost.
                if (!KeepUnreadable())
                {
                    if (!_warnedBrokenSave)
                    {
                        _warnedBrokenSave = true;
                        Log.Warn("assets: NOT saved -- " + FileName + " cannot be read in full and could not be kept as " +
                                 UnreadableName + "; saving would throw away whatever is in it. Fix it or delete it.");
                    }
                    return;
                }

                _broken = false;
                _warnedBrokenSave = false;
            }

            try
            {
                // No file until some asset has glass of its own: nothing to keep, nothing to show.
                if (Records.Count == 0 && Props.Count == 0 && !File.Exists(Path))
                    return;

                string text = AssetFileFormat.Write(new List<AssetRecord>(Records.Values), new List<PropRecord>(Props.Values),
                                                    Localization.Get("AssetFile.Header"));
                if (text == _diskText && File.Exists(Path))
                    return;

                WriteReplacing(text);
                Remember(text);
                _lastSaveError = null;
                if (Log.Detailed)
                    Log.Detail("assets: saved " + Path + " (" + Records.Count + " assets)");
            }
            catch (Exception e)
            {
                if (e.Message != _lastSaveError)
                {
                    _lastSaveError = e.Message;
                    Log.Warn("assets: could not save " + Path + ": " + e.Message);
                }
            }
        }

        /// <summary>The unreadable file, copied to RealGlassAssets.unreadable.xml (over an older copy). True when it is safe now.</summary>
        private static bool KeepUnreadable()
        {
            try
            {
                if (File.Exists(Path))
                {
                    File.Copy(Path, UnreadablePath, true);
                    Log.Warn("assets: " + FileName + " could not be read in full; kept as " + UnreadablePath +
                             " before writing it again with the glass changed in the game");
                }
                return true;
            }
            catch (Exception e)
            {
                Log.Warn("assets: could not keep the unreadable " + FileName + " as " + UnreadableName + ": " + e.Message);
                return false;
            }
        }

        private static void WriteReplacing(string text)
        {
            File.WriteAllText(TempPath, text, new UTF8Encoding(false));

            if (!File.Exists(Path))
            {
                File.Move(TempPath, Path);
                return;
            }

            if (!_replaceFailed)
            {
                try
                {
                    File.Replace(TempPath, Path, null);
                    return;
                }
                catch (Exception e)
                {
                    _replaceFailed = true;
                    Log.Msg("assets: File.Replace failed (" + e.Message + "); copying over the file from now on");
                }
            }

            File.Copy(TempPath, Path, true);
            File.Delete(TempPath);
        }

        /// <summary>What is now on disk, so our own write is not mistaken for an edit.</summary>
        private static void Remember(string text)
        {
            _diskText = text;
            try
            {
                var info = new FileInfo(Path);
                _seenTime = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
                _seenLength = info.Exists ? info.Length : -1;
            }
            catch (Exception)
            {
                _seenLength = -1;
            }
        }
    }
}
