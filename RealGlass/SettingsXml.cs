using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ColossalFramework.IO;
using RealGlass.UI;
using UnityEngine;

namespace RealGlass
{
    /// <summary>
    /// RealGlass.xml, next to the log: every setting, in a file a player can open and edit --
    /// with the game running, too. (Volumetric Weather's settings file, with this mod's rows.)
    /// </summary>
    /// <remarks>
    /// The catalog is the list: one element per row that holds a value, in catalog order, under a
    /// heading for the tab it is on. Numbers are written the way the slider shows them, so a
    /// percentage is 47, not 0.47 -- the number someone reads off the screen is the number they
    /// look for in the file.
    ///
    /// When it is read:
    /// - at startup;
    /// - within a second of it changing on disk (<see cref="Tick"/>), applied exactly as if each
    ///   changed row had been moved in the UI (AfterChange runs, both UIs refresh).
    /// When it is written:
    /// - a switch, a key or a reset: at once (<see cref="SaveNow"/>). A switch is a decision; "I
    ///   ticked it and it was off next time" must never happen;
    /// - a slider: a second after the last change, so a drag is one write, not sixty;
    /// - on quitting, and when the mod is disabled.
    /// It is written to a temporary file that then replaces it, so a crash never leaves half a
    /// file, and not at all when the text would be the same.
    ///
    /// A value that is out of range is pulled into it, one that cannot be read is ignored, an
    /// unknown name is ignored, a missing one keeps its current value -- each said once in the
    /// log. A file that cannot be read AT ALL is never saved over: it may hold an afternoon of
    /// tuning with one bracket missing.
    ///
    /// PROFILES (2.0.0, Volumetric Weather's): a profile's file is written only by the panel's "+"
    /// and "Save", from the same walk of the catalog, with only the rows a profile carries
    /// (<see cref="Profiles"/>). This file stays the whole truth of the look, unsaved changes
    /// included: it gains one element, &lt;Profile&gt;, the pick.
    /// </remarks>
    public static class SettingsXml
    {
        public const string FileName = "RealGlass.xml";

        private const float SaveDelay = 1f;
        private const float FileCheckInterval = 1f;

        private static string _path;

        // Set from the setters, which only ever run on the main thread; volatile anyway, since
        // nothing here is worth a torn read.
        private static volatile bool _dirty;
        private static volatile int _changeStamp;
        private static int _seenStamp;
        private static float _quietSince;
        private static float _nextFileCheck;

        /// <summary>True while values are being taken FROM a file: those are not changes to save.</summary>
        private static bool _loading;

        /// <summary>This file on disk: its text, and whether it can be read (nothing is written until it can).</summary>
        private static WatchedFile _live;
        private static bool _warnedBrokenSave;

        /// <summary>
        /// This session has read this file, or written it. Until then (it could not be read at
        /// startup) the next read is the one startup could not make, and its &lt;Profile&gt; is the
        /// pick it held, not an edit (<see cref="Reload"/>).
        /// </summary>
        private static bool _readOnce;

        private static int _savedKey;
        private static bool _replaceFailed;
        private static string _lastSaveError;
        private static GameObject _saver;

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
            get { return TempOf(Path); }
        }

        private static string TempOf(string path)
        {
            return path + ".tmp";
        }

        private static WatchedFile Live
        {
            get
            {
                if (_live == null)
                    _live = new WatchedFile(Path);

                return _live;
            }
        }

        /// <summary>A value changed. Saved a second after the last one; see <see cref="Tick"/>.</summary>
        public static void MarkDirty()
        {
            if (_loading)
                return;

            _dirty = true;
            _changeStamp++;
        }

        // ---- startup --------------------------------------------------------------------------

        /// <summary>Called once, at the end of Settings.Init, with every setting constructed.</summary>
        public static void Load()
        {
            StartSaver();
            WarnAboutRowlessSettings();

            try
            {
                if (File.Exists(Path))
                {
                    LoadAtStartup(Path, "loaded");
                }
                else if (File.Exists(TempPath))
                {
                    // Only possible if the game died between writing the new file and moving it
                    // into place -- and then the temporary file IS the latest save.
                    Log.Warn("settings: " + FileName + " is missing but an unfinished save of it is there; using that");
                    LoadAtStartup(TempPath, "recovered");
                }
                else
                {
                    SaveNow();
                    Log.Msg("settings: no settings file yet; created " + Path + " with every default");
                }
            }
            catch (Exception e)
            {
                Log.Error("settings: could not load " + Path + "; running on the defaults", e);
            }

            // The picked profile's file, READ (never applied): does the look still match it?
            Profiles.Startup();

            _savedKey = CurrentKey();
        }

        private static void LoadAtStartup(string file, string verb)
        {
            string text = File.ReadAllText(file);
            if (file == Path)
                Live.Remember(text);

            string error;
            ReadResult result = Read(text, out error, null, false, true, null);
            if (result == null)
            {
                Live.Broken = true;
                Log.Warn("settings: " + file + " cannot be read (" + error + "). Running on the defaults, " +
                         "and NOT saving over it: fix it or delete it, and it is read again within a second.");
                return;
            }

            _readOnce = true;
            Log.Msg("settings: " + verb + " " + result.Applied + " values from " + file + result.Describe());
            result.LogProblems("settings");

            // Lists every setting, with its comment: a file from an older version gains the new
            // ones, and a clamped value is written as it now is. Skipped when nothing differs.
            SaveNow();
        }

        // ---- the running game -----------------------------------------------------------------

        /// <summary>Every frame, from <see cref="SettingsSaver"/>, at the main menu and in a city.</summary>
        public static void Tick()
        {
            if (!Settings.IsInitialised)
                return;

            float now = Time.realtimeSinceStartup;

            // The files first, then the pending save: a hand edit is read before anything is
            // written (and the saves themselves never write over one they have not read).
            if (now >= _nextFileCheck)
            {
                _nextFileCheck = now + FileCheckInterval;

                // Unified UI holds our key too; anything that sets it bypasses every UI path.
                if (CurrentKey() != _savedKey)
                    MarkDirty();

                CheckFile();
                Profiles.Check();
            }

            if (_dirty)
            {
                int stamp = _changeStamp;
                if (stamp != _seenStamp)
                {
                    _seenStamp = stamp;
                    _quietSince = now;
                }
                else if (now - _quietSince >= SaveDelay)
                {
                    SaveNow();
                }
            }
        }

        /// <summary>Writes a pending change now: quitting, the mod being disabled.</summary>
        public static void Flush()
        {
            if (_dirty)
                SaveNow();
        }

        /// <summary>From Mod.OnDisabled.</summary>
        public static void Shutdown()
        {
            Flush();

            if (_saver != null)
                UnityEngine.Object.Destroy(_saver);

            _saver = null;
        }

        /// <summary>Starts <see cref="SettingsSaver"/> unless it is running. Safe to call again.</summary>
        public static void StartSaver()
        {
            if (_saver != null)
                return;

            _saver = new GameObject("RealGlassSettings");
            UnityEngine.Object.DontDestroyOnLoad(_saver);
            _saver.AddComponent<SettingsSaver>();
        }

        private static void CheckFile()
        {
            FileInfo info;
            try
            {
                info = new FileInfo(Path);
                if (!info.Exists)
                {
                    if (Live.DiskText != null || Live.Broken)
                    {
                        Log.Msg("settings: " + FileName + " was deleted while the game runs; writing it again from " +
                                "the settings in use (delete it with the game closed to go back to the defaults)");
                    }

                    Live.DiskText = null;
                    Live.Broken = false;
                    SaveNow();
                    return;
                }

                if (!Live.Moved(info))
                    return;
            }
            catch (Exception e)
            {
                Log.Detail("settings: could not look at " + Path + ": " + e.Message);
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
                return; // an editor is still writing it or holds it; look again in a second
            }

            // Saved again unchanged, or only its date moved: nothing to take.
            bool same = text == Live.DiskText;
            Live.Remember(text);
            if (same)
                return;

            string error;
            var changed = new List<Row>();
            ReadResult result = Read(text, out error, null, false, false, changed);
            if (result == null)
            {
                Live.Broken = true;
                Live.DiskText = null;
                Log.Warn("settings: " + FileName + " was edited and cannot be read (" + error + "). Nothing was " +
                         "taken from it, and it will not be saved over until it can be read again.");
                return;
            }

            bool wasBroken = Live.Broken;
            bool firstRead = !_readOnce;
            _readOnce = true;
            Live.Broken = false;
            _warnedBrokenSave = false;

            Log.Msg("settings: " + FileName + " was edited" + (wasBroken ? " and can be read again" : "") +
                    (firstRead ? " (the first read this session: its <Profile> is only read, as at startup)" : "") + " -- " +
                    (changed.Count == 0 ? "no value changed" : changed.Count + " changed: " + result.Changes()) +
                    result.Describe());
            result.LogProblems("settings");

            // The read startup could not make: the file's <Profile> is the pick it held then, not an
            // edit. Put in, that profile's values would replace the ones just taken -- which are
            // the whole truth of the look, the changes never saved in the profile included. So it
            // is only READ, the way startup reads it.
            if (firstRead)
                changed.RemoveAll(IsProfileRow);

            // As if each row had been moved in the UI, in catalog order -- which puts the glass
            // preset before the three rows it writes, and the picked profile (a hidden row, after
            // every panel row) after every value it might then replace.
            RunAfterChange(changed, "the file");

            if (firstRead)
                Profiles.Reread();

            _savedKey = CurrentKey();
            SettingsCatalog.RefreshAllUIs();
        }

        private static bool IsProfileRow(Row row)
        {
            return row.Text != null && row.Text == Settings.CurrentProfile;
        }

        /// <summary>
        /// Each changed row's live-apply hook, as if it had been moved in the UI, in catalog order.
        /// One that throws is logged and the rest still run.
        /// </summary>
        internal static void RunAfterChange(List<Row> changed, string source)
        {
            foreach (Row row in changed)
            {
                if (row.AfterChange == null)
                    continue;

                try
                {
                    row.AfterChange();
                }
                catch (Exception e)
                {
                    Log.Error("settings: applying '" + row.Name + "' from " + source + " failed", e);
                }
            }
        }

        // ---- saving ---------------------------------------------------------------------------

        /// <summary>
        /// Writes the file now, if what it would hold differs from what is there. Never a
        /// profile's file: those are written by "+" and "Save" alone (<see cref="Profiles"/>).
        /// </summary>
        public static void SaveNow()
        {
            _dirty = false;
            _seenStamp = _changeStamp;

            if (Settings.IsInitialised)
                SaveLive();
        }

        private static void SaveLive()
        {
            if (Live.Broken)
            {
                if (!_warnedBrokenSave)
                {
                    _warnedBrokenSave = true;
                    Log.Warn("settings: NOT saved -- " + FileName + " cannot be read, and saving would throw away " +
                             "whatever is in it. Fix it or delete it; the mod reads it again within a second.");
                }

                return;
            }

            try
            {
                // Edited on disk since we last read or wrote it: that edit is read first
                // (CheckFile, within a second) and this save comes after it -- never write over
                // an edit unread.
                if (Live.DiskText != null && File.Exists(Path) && Live.Moved(new FileInfo(Path)))
                {
                    MarkDirty();
                    return;
                }

                string text = Compose(null, Localization.Get("File.Header"));
                _savedKey = CurrentKey();

                if (text == Live.DiskText && File.Exists(Path))
                    return;

                WriteReplacing(Path, text);
                Live.Remember(text);
                _readOnce = true;
                _lastSaveError = null;

                if (Log.Detailed)
                    Log.Detail("settings: saved " + Path);
            }
            catch (Exception e)
            {
                // Once per distinct failure: this runs every second while the file is missing.
                if (e.Message != _lastSaveError)
                {
                    _lastSaveError = e.Message;
                    Log.Warn("settings: could not save " + Path + ": " + e.Message);
                }
            }
        }

        /// <summary>
        /// Writes a temporary file, then moves it into place: a crash never leaves half a file.
        /// UTF-8 without a byte-order mark. Throws what the file system throws.
        /// </summary>
        internal static void WriteReplacing(string path, string text)
        {
            string temp = TempOf(path);
            File.WriteAllText(temp, text, new UTF8Encoding(false));

            if (!File.Exists(path))
            {
                File.Move(temp, path);
                return;
            }

            if (!_replaceFailed)
            {
                try
                {
                    File.Replace(temp, path, null);
                    return;
                }
                catch (Exception e)
                {
                    _replaceFailed = true;
                    Log.Msg("settings: File.Replace failed (" + e.Message + "); copying over the file from now on");
                }
            }

            File.Copy(temp, path, true);
            File.Delete(temp);
        }

        /// <summary>
        /// The file's text: one commented element per row that <paramref name="filter"/> takes
        /// (null: every row), in catalog order.
        /// </summary>
        private static string Compose(Predicate<Row> filter, string header)
        {
            var entries = new List<SettingsXmlFormat.Entry>();
            var seen = new HashSet<object>();

            foreach (Row row in SettingsCatalog.Rows)
            {
                object setting = SettingOf(row);
                if (setting == null || !seen.Add(setting))
                    continue;

                if (filter != null && !filter(row))
                    continue;

                entries.Add(new SettingsXmlFormat.Entry
                {
                    Section = SectionOf(row),
                    Group = row.GroupTitle,
                    Name = row.Name,
                    Value = ValueText(row),
                    Comment = CommentFor(row),
                });
            }

            // The comments are in the player's language (Localization); the names are not.
            return SettingsXmlFormat.Write(entries, header);
        }

        // ---- profiles -------------------------------------------------------------------------

        /// <summary>A row a profile carries: one with a value, and Row.Profiled.</summary>
        internal static bool IsProfiled(Row row)
        {
            return row.Profiled && SettingOf(row) != null;
        }

        /// <summary>How many values a profile's file holds.</summary>
        internal static int ProfiledCount
        {
            get
            {
                int count = 0;
                var seen = new HashSet<object>();
                foreach (Row row in SettingsCatalog.Rows)
                {
                    object setting = SettingOf(row);
                    if (setting != null && seen.Add(setting) && row.Profiled)
                        count++;
                }

                return count;
            }
        }

        /// <summary>A profile's file as the look is now: the rows a profile carries, commented like this file.</summary>
        internal static string ComposeProfile()
        {
            return Compose(IsProfiled, Localization.Get("File.ProfileHeader"));
        }

        /// <summary>
        /// The look as a profile holds it: each row a profile carries, by name, with its value as
        /// the file writes it. What "unsaved changes" compares (<see cref="Profiles"/>): values,
        /// never the text, whose comments follow the language and the version.
        /// </summary>
        internal static Dictionary<string, string> ProfileValues()
        {
            var values = new Dictionary<string, string>();
            var seen = new HashSet<object>();

            foreach (Row row in SettingsCatalog.Rows)
            {
                object setting = SettingOf(row);
                if (setting == null || !seen.Add(setting) || !row.Profiled)
                    continue;

                values[row.Name] = ValueText(row);
            }

            return values;
        }

        /// <summary>
        /// What picking a profile of this text would put in, as <see cref="ProfileValues"/> gives
        /// it, worked out without changing the look: the values are taken the way a pick takes them,
        /// read back, and put back in the same call. Nothing sees them in between: the settings are
        /// read on the main thread (the mod's worker threads -- LOD glass, room fit, shore map,
        /// ripples -- take what they need from it, never a setting). Null (and why) if the text is
        /// not a settings file.
        /// </summary>
        internal static Dictionary<string, string> ProfileValuesOf(string text, out string error)
        {
            var held = new List<KeyValuePair<Row, object>>();
            var seen = new HashSet<object>();
            foreach (Row row in SettingsCatalog.Rows)
            {
                object setting = SettingOf(row);
                if (setting != null && seen.Add(setting) && row.Profiled)
                    held.Add(new KeyValuePair<Row, object>(row, RawValue(row)));
            }

            try
            {
                if (Read(text, out error, IsProfiled, true, false, null) == null)
                    return null;

                return ProfileValues();
            }
            finally
            {
                // Read clears _loading when it is done: set again, so putting the values back
                // is no change to save.
                _loading = true;
                try
                {
                    foreach (KeyValuePair<Row, object> pair in held)
                        SetRawValue(pair.Key, pair.Value);
                }
                finally
                {
                    _loading = false;
                }
            }
        }

        /// <summary>
        /// Takes a profile's values: only the rows a profile carries, and a name that belongs to
        /// any other row is named in the log and left alone. A profile is a complete look, so a
        /// value it lacks goes back to its default. Null (and why) if the text is not a settings
        /// file.
        /// </summary>
        internal static ReadResult ReadProfile(string text, out string error, List<Row> changedRows)
        {
            return Read(text, out error, IsProfiled, true, false, changedRows);
        }

        /// <summary>
        /// The mod's defaults as <see cref="ProfileValues"/> gives a look (the built-in "(Mod
        /// defaults)" entry): each row a profile carries at its default, worked out without changing
        /// the look, as <see cref="ProfileValuesOf"/> does.
        /// </summary>
        internal static Dictionary<string, string> DefaultProfileValues()
        {
            var held = new List<KeyValuePair<Row, object>>();
            var seen = new HashSet<object>();
            foreach (Row row in SettingsCatalog.Rows)
            {
                object setting = SettingOf(row);
                if (setting != null && seen.Add(setting) && row.Profiled)
                    held.Add(new KeyValuePair<Row, object>(row, RawValue(row)));
            }

            _loading = true;
            try
            {
                foreach (KeyValuePair<Row, object> pair in held)
                    pair.Key.ResetToDefault();

                return ProfileValues();
            }
            finally
            {
                try
                {
                    foreach (KeyValuePair<Row, object> pair in held)
                        SetRawValue(pair.Key, pair.Value);
                }
                finally
                {
                    _loading = false;
                }
            }
        }

        /// <summary>
        /// Puts every row a profile carries back to its default (picking "(Mod defaults)"): what an
        /// empty profile would put in, but the default itself even for a row with a
        /// <see cref="Row.OlderFileInt"/>. The rows a profile does not carry -- the computer's, the
        /// panel's housekeeping -- are left alone. What changed, as "Name was -> now".
        /// </summary>
        internal static List<string> ResetProfiled(List<Row> changedRows)
        {
            var changes = new List<string>();
            var seen = new HashSet<object>();
            foreach (Row row in SettingsCatalog.Rows)
            {
                object setting = SettingOf(row);
                if (setting == null || !seen.Add(setting) || !row.Profiled)
                    continue;

                string was = ValueText(row);
                row.ResetToDefault();
                string now = ValueText(row);
                if (now != was)
                {
                    changes.Add(row.Name + " " + was + " -> " + now);
                    changedRows.Add(row);
                }
            }

            return changes;
        }

        // ---- applying values ------------------------------------------------------------------

        /// <summary>What one read of a file did, for the log.</summary>
        internal sealed class ReadResult
        {
            public int Applied;
            public readonly List<string> Missing = new List<string>();
            public readonly List<string> Defaulted = new List<string>();
            public readonly List<string> OlderFile = new List<string>();
            public readonly List<string> Unknown = new List<string>();
            public readonly List<string> NotProfiled = new List<string>();
            public readonly List<string> Retired = new List<string>();
            public readonly List<string> Problems = new List<string>();
            public readonly List<string> ChangeList = new List<string>();
            public List<string> Duplicates = new List<string>();
            public int Version;

            public string Describe()
            {
                string text = "";
                if (Missing.Count > 0)
                    text += "; " + Missing.Count + " not in it, left as they were: " + List(Missing);
                if (Defaulted.Count > 0)
                    text += "; " + Defaulted.Count + " not in it, set to their defaults: " + List(Defaulted);
                if (OlderFile.Count > 0)
                    text += "; written before these existed, so set to what an older file means: " + List(OlderFile);
                if (Unknown.Count > 0)
                    text += "; ignored names the mod does not know: " + List(Unknown);
                if (NotProfiled.Count > 0)
                    text += "; ignored, not part of a profile: " + List(NotProfiled);
                if (Retired.Count > 0)
                    text += "; retired, read only for what an older file meant: " + List(Retired);
                if (Duplicates.Count > 0)
                    text += "; written twice, the last one taken: " + List(Duplicates);
                if (Version > SettingsXmlFormat.Version)
                    text += "; the file is from a NEWER version of the mod (format " + Version + ")";

                return text;
            }

            public string Changes()
            {
                return List(ChangeList);
            }

            /// <summary>One warning for every value not taken as written. <paramref name="prefix"/> starts the line ("settings", "profile").</summary>
            public void LogProblems(string prefix)
            {
                if (Problems.Count > 0)
                    Log.Warn(prefix + ": not taken as written -- " + string.Join("; ", Problems.ToArray()));
            }

            private static string List(List<string> items)
            {
                const int Shown = 12;
                if (items.Count <= Shown)
                    return string.Join(", ", items.ToArray());

                return string.Join(", ", items.GetRange(0, Shown).ToArray()) + " and " + (items.Count - Shown) + " more";
            }
        }

        /// <summary>
        /// Takes every value the text holds for the rows <paramref name="filter"/> takes (null:
        /// every row). Null (and why) if it is not a settings file.
        /// <paramref name="startup"/>: the settings file as the game starts, where a row with an
        /// <see cref="Row.OlderFileInt"/> that the file lacks was added after it was written.
        /// <paramref name="missingMeansDefault"/>: a row the text lacks goes back to its default
        /// (or to what an older file means, where the row says) instead of staying as it is.
        /// </summary>
        private static ReadResult Read(string text, out string error, Predicate<Row> filter, bool missingMeansDefault,
            bool startup, List<Row> changedRows)
        {
            error = null;
            var result = new ReadResult();

            Dictionary<string, string> values;
            try
            {
                values = SettingsXmlFormat.Parse(text, out result.Version, result.Duplicates);
            }
            catch (FormatException e)
            {
                error = e.Message;
                return null;
            }

            var seen = new HashSet<object>();
            var heldBack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Every element as written, for the rows whose missing value is worked out from the rest
            // of the file (Row.OlderFile); `values` loses each one as its row takes it.
            var written = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
            _loading = true;
            try
            {
                foreach (Row row in SettingsCatalog.Rows)
                {
                    object setting = SettingOf(row);
                    if (setting == null || !seen.Add(setting))
                        continue;

                    if (filter != null && !filter(row))
                    {
                        heldBack.Add(row.Name);
                        continue;
                    }

                    string value;
                    if (!values.TryGetValue(row.Name, out value))
                    {
                        // Written before the row existed, and what the file meant by it is in the
                        // file itself (a retired element): take that.
                        string derived = (startup || missingMeansDefault) && row.OlderFile != null ? row.OlderFile(written) : null;
                        if (derived != null)
                        {
                            string was = ValueText(row);
                            string derivedProblem = ApplyText(row, derived);
                            if (derivedProblem != null)
                                result.Problems.Add(row.Name + " '" + derived + "' " + derivedProblem);

                            string now = ValueText(row);
                            result.OlderFile.Add(row.Name + " " + now);
                            if (now != was)
                            {
                                result.ChangeList.Add(row.Name + " " + was + " -> " + now);
                                if (changedRows != null)
                                    changedRows.Add(row);
                            }
                            continue;
                        }

                        // A row whose default is not what an existing player had: a file without
                        // it is older than the row and means that value, not the default. (A hand
                        // edit that deletes the element later is read as "unchanged", like any
                        // other.)
                        if (startup && row.OlderFileInt.HasValue && row.Int != null)
                        {
                            row.Int.value = row.OlderFileInt.Value;
                            result.OlderFile.Add(row.Name + " " + ValueText(row));
                        }
                        else if (missingMeansDefault)
                        {
                            string was = ValueText(row);
                            if (row.OlderFileInt.HasValue && row.Int != null)
                                row.Int.value = row.OlderFileInt.Value;
                            else
                                row.ResetToDefault();

                            result.Defaulted.Add(row.Name);
                            string now = ValueText(row);
                            if (now != was)
                            {
                                result.ChangeList.Add(row.Name + " " + was + " -> " + now);
                                if (changedRows != null)
                                    changedRows.Add(row);
                            }
                        }
                        else
                        {
                            result.Missing.Add(row.Name);
                        }
                        continue;
                    }

                    values.Remove(row.Name);
                    result.Applied++;

                    string before = ValueText(row);
                    string problem = ApplyText(row, value);
                    if (problem != null)
                        result.Problems.Add(row.Name + " '" + value + "' " + problem);

                    string after = ValueText(row);
                    if (after != before)
                    {
                        result.ChangeList.Add(row.Name + " " + before + " -> " + after);
                        if (changedRows != null)
                            changedRows.Add(row);
                    }
                }
            }
            finally
            {
                _loading = false;
            }

            foreach (string name in values.Keys)
            {
                if (Retired.Contains(name))
                    result.Retired.Add(name);
                else if (heldBack.Contains(name))
                    result.NotProfiled.Add(name);
                else
                    result.Unknown.Add(name);
            }

            return result;
        }

        /// <summary>
        /// Names no row has any more, read only for what an older file meant by them (Row.OlderFile),
        /// and never used again for anything. 1.1.7: "UseUnifiedUI", the switch IconPlace replaced.
        /// 2.0.0: "WaterCityCards" ("City beyond the screen", a dev builds' trial row): the water's
        /// facade cards run whenever the cards do (the author's call, 2026-11-13). The first save
        /// leaves them out.
        /// </summary>
        internal static readonly HashSet<string> Retired = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "UseUnifiedUI",
            "WaterCityCards",
        };

        /// <summary>Stores one value written as text. Returns what went wrong, or null.</summary>
        private static string ApplyText(Row row, string text)
        {
            if (row.Float != null)
            {
                double number;
                if (!SettingsXmlFormat.TryParseNumber(text, out number))
                    return "ignored: not a number (decimals take a dot: 0.5)";

                return ApplyDisplay(row, number);
            }

            if (row.Bool != null)
            {
                bool flag;
                if (!SettingsXmlFormat.TryParseBool(text, out flag))
                    return "ignored: not true or false";

                row.Bool.value = flag;
                return null;
            }

            if (row.Int != null)
                return ApplyInt(row, text);

            if (row.Key != null)
            {
                int encoded;
                if (!SettingsXmlFormat.TryParseKey(text, out encoded))
                    return "ignored: not a key (F8, Ctrl+Shift+G, None)";

                row.Key.value = encoded;
                return null;
            }

            if (row.Colour != null)
            {
                Color32 colour;
                if (!ColorText.TryParse(text, out colour))
                    return "ignored: not a colour (#RRGGBB, or r, g, b)";

                row.Colour.value = colour;
                return null;
            }

            if (row.Text != null)
            {
                row.Text.value = text;
                return null;
            }

            return null;
        }

        /// <summary>A number in slider units, pulled into the slider's range.</summary>
        private static string ApplyDisplay(Row row, double display)
        {
            string problem = null;

            if (row.Max > row.Min && (display < row.Min || display > row.Max))
            {
                double clamped = Math.Max(row.Min, Math.Min(row.Max, display));
                problem = "pulled into its range " + SettingsXmlFormat.FormatNumber(row.Min) + " to " +
                          SettingsXmlFormat.FormatNumber(row.Max) + ": " + SettingsXmlFormat.FormatNumber(clamped);
                display = clamped;
            }

            row.Store((float)display);
            return problem;
        }

        private static string ApplyInt(Row row, string text)
        {
            string trimmed = text.Trim();

            if (row.ChoiceValues != null)
            {
                int number;
                bool isNumber = int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);

                for (int i = 0; i < row.ChoiceValues.Length; i++)
                {
                    bool byName = row.Choices != null && i < row.Choices.Length &&
                                  string.Equals(row.Choices[i], trimmed, StringComparison.OrdinalIgnoreCase);

                    if (byName || (isNumber && row.ChoiceValues[i] == number))
                    {
                        row.Int.value = row.ChoiceValues[i];
                        return null;
                    }
                }

                return "ignored: not one of " + ChoiceList(row);
            }

            double value;
            if (!SettingsXmlFormat.TryParseNumber(trimmed, out value))
                return "ignored: not a number";

            string problem = null;
            if (row.Max > row.Min && (value < row.Min || value > row.Max))
            {
                value = Math.Max(row.Min, Math.Min(row.Max, value));
                problem = "pulled into its range: " + SettingsXmlFormat.FormatNumber(value);
            }

            row.Int.value = (int)Math.Round(value);
            return problem;
        }

        // ---- rows -----------------------------------------------------------------------------

        private static object SettingOf(Row row)
        {
            if (row.Float != null) return row.Float;
            if (row.Bool != null) return row.Bool;
            if (row.Int != null) return row.Int;
            if (row.Key != null) return row.Key;
            if (row.Colour != null) return row.Colour;
            if (row.Text != null) return row.Text;
            return null;
        }

        /// <summary>A row's setting as it is held, to be put back with <see cref="SetRawValue"/>. Null for a key: never in a profile.</summary>
        private static object RawValue(Row row)
        {
            if (row.Float != null) return row.Float.value;
            if (row.Bool != null) return row.Bool.value;
            if (row.Int != null) return row.Int.value;
            if (row.Key != null) return null;
            if (row.Colour != null) return row.Colour.value;
            if (row.Text != null) return row.Text.value;
            return null;
        }

        private static void SetRawValue(Row row, object value)
        {
            if (value == null)
                return;

            if (row.Float != null) row.Float.value = (float)value;
            else if (row.Bool != null) row.Bool.value = (bool)value;
            else if (row.Int != null) row.Int.value = (int)value;
            else if (row.Colour != null) row.Colour.value = (Color32)value;
            else if (row.Text != null) row.Text.value = (string)value;
        }

        private static int CurrentKey()
        {
            return Settings.PanelKey != null ? (int)Settings.PanelKey.value : 0;
        }

        /// <summary>A row's value as the file writes it: what the picked profile's (Profiles.KeptValueOf) is compared with.</summary>
        internal static string ValueOf(Row row)
        {
            return ValueText(row);
        }

        /// <summary>Stores a value written as the file writes it (a row's reset to its profile's value). Returns what went wrong, or null.</summary>
        internal static string ApplyValue(Row row, string text)
        {
            return ApplyText(row, text);
        }

        /// <summary>The value exactly as the file holds it.</summary>
        private static string ValueText(Row row)
        {
            if (row.Float != null)
                return SettingsXmlFormat.FormatNumber(row.Display);
            if (row.Bool != null)
                return SettingsXmlFormat.FormatBool(row.Bool.value);
            if (row.Int != null)
                return row.Int.value.ToString(CultureInfo.InvariantCulture);
            if (row.Key != null)
                return SettingsXmlFormat.FormatKey(row.Key.value);
            if (row.Colour != null)
                return ColorText.Format(row.Colour.value);
            if (row.Text != null)
                return row.Text.value;

            return string.Empty;
        }

        private static string SectionOf(Row row)
        {
            if (row.Panel != PanelPage.None)
                return Localization.Get("File.Section.Panel", SettingsCatalog.TabName(row.Panel));
            if (row.Options == OptionsPage.General)
                return Localization.Get("File.Section.Options");

            return Localization.Get("File.Section.None");
        }

        /// <summary>"Floor height. 2.8 to 6 (slider: 2.8 m to 6.0 m), default 3.8."</summary>
        private static string CommentFor(Row row)
        {
            string label = row.Label;
            string text;

            if (row.Float != null)
            {
                string fallback = SettingsXmlFormat.FormatNumber(row.Kind == RowKind.Percent ? row.DefaultFloat * 100f : row.DefaultFloat);
                string min = SettingsXmlFormat.FormatNumber(row.Min);
                string max = SettingsXmlFormat.FormatNumber(row.Max);

                if (!(row.Max > row.Min))
                    text = Localization.Get("File.AnyNumber", label, fallback);
                else if (row.Kind == RowKind.Percent)
                    text = Localization.Get("File.Percent", label, min, max, fallback);
                else
                    text = Localization.Get("File.Number", label, min, max, Readout(row), fallback);
            }
            else if (row.Bool != null)
            {
                text = Localization.Get("File.Toggle", label, SettingsXmlFormat.FormatBool(row.DefaultBool));
            }
            else if (row.Int != null)
            {
                string fallback = row.DefaultInt.ToString(CultureInfo.InvariantCulture);
                text = row.ChoiceValues != null
                    ? Localization.Get("File.Choice", label, ChoiceList(row), fallback)
                    : Localization.Get("File.AnyNumber", label, fallback);
            }
            else if (row.Colour != null)
            {
                text = Localization.Get("File.Colour", label, ColorText.Format(row.DefaultColour));
            }
            else if (row.Text != null)
            {
                text = Localization.Get("File.Text", label);
            }
            else
            {
                text = Localization.Get("File.Key", label, SettingsXmlFormat.FormatKey(row.DefaultKey));
            }

            if (row.Confirm)
                text += " " + Localization.Get("File.Confirm");

            if (row.HasNote)
                text += " " + row.Note;

            return text;
        }

        /// <summary>
        /// What the slider shows at its ends, where that is not just the number, with the space in
        /// front of it (a language file cannot hold a leading space: its texts are trimmed).
        /// </summary>
        private static string Readout(Row row)
        {
            string low = row.FormatDisplay(row.Min);
            string high = row.FormatDisplay(row.Max);

            if (low == SettingsXmlFormat.FormatNumber(row.Min) && high == SettingsXmlFormat.FormatNumber(row.Max))
                return string.Empty;

            return " " + Localization.Get("File.Readout", low, high);
        }

        private static string ChoiceList(Row row)
        {
            var parts = new List<string>();
            for (int i = 0; i < row.ChoiceValues.Length; i++)
            {
                string value = row.ChoiceValues[i].ToString(CultureInfo.InvariantCulture);
                string name = row.Choices != null && i < row.Choices.Length ? row.Choices[i] : value;
                parts.Add(name == value ? value : value + " (" + name + ")");
            }

            return string.Join(", ", parts.ToArray());
        }

        /// <summary>
        /// A setting with no catalog row is never saved. Said once, always, so it is found the
        /// first time anyone reads a log.
        /// </summary>
        private static void WarnAboutRowlessSettings()
        {
            var inCatalog = new HashSet<object>();
            foreach (Row row in SettingsCatalog.Rows)
            {
                object setting = SettingOf(row);
                if (setting != null)
                    inCatalog.Add(setting);
            }

            var missing = new List<string>();
            foreach (Setting setting in Setting.All)
            {
                if (!inCatalog.Contains(setting))
                    missing.Add(setting.name);
            }

            if (missing.Count > 0)
            {
                Log.Warn("settings: no row in SettingsCatalog, so NEVER saved: " +
                         string.Join(", ", missing.ToArray()));
            }
        }
    }

    /// <summary>
    /// Drives <see cref="SettingsXml.Tick"/>. Lives for the whole session, main menu included: the
    /// options page changes settings there too.
    /// </summary>
    internal sealed class SettingsSaver : MonoBehaviour
    {
        private bool _failed;

        private void Update()
        {
            try
            {
                SettingsXml.Tick();
                PerAsset.AssetFile.Tick();
            }
            catch (Exception e)
            {
                // Once: this runs every frame, and a repeating error would bury the log.
                if (!_failed)
                    Log.Error("settings: the saver failed; changes may not reach " + SettingsXml.FileName + " or " + PerAsset.AssetFile.FileName, e);

                _failed = true;
            }
        }

        private void OnApplicationQuit()
        {
            SettingsXml.Flush();
            PerAsset.AssetFile.Flush();
        }

        private void OnDestroy()
        {
            SettingsXml.Flush();
            PerAsset.AssetFile.Flush();
        }
    }
}
