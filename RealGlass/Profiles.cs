using System;
using System.Collections.Generic;
using System.IO;
using ColossalFramework.IO;
using RealGlass.UI;

namespace RealGlass
{
    /// <summary>
    /// PROFILES (1.1.9, the author's ask: "exactly like how we do them on Volumetric Weather"):
    /// whole looks saved under a name and picked at the top of the in-game (F8) panel -- every
    /// setting of the panel's tabs, the switches included, in files of their own. Volumetric
    /// Weather's Profiles (its 1.3.0, as its 1.5.0 left it), with this mod's names.
    /// </summary>
    /// <remarks>
    /// THE MODEL: a profile is a SAVED look. Picking one puts its values in; what is changed after
    /// that belongs to the look (RealGlass.xml, as always) until "Save" writes it into the
    /// picked profile. "+" saves the look as it is as a new profile and picks it; "-" deletes the
    /// picked one's file, the look staying as it is; "Reset all" lets go -- which is where every
    /// player was before profiles (there is no "(no profile)" entry). While the look differs from
    /// the picked profile the bar says so, and picking another profile then asks first: it would
    /// throw those changes away. (Volumetric Weather's first build wrote every change into the
    /// picked profile too, with no Save button; it failed the author's first test there.)
    ///
    /// Picking one puts ALL its values in (a value it lacks goes back to its default: a profile is
    /// a complete look), the switches included: the player's own click on a look they saved or
    /// chose, like "Reset all". The glass preset is taken as the profile holds it, beside the
    /// colour, reflectivity and clarity it holds: neither writes the other
    /// (<see cref="SettingsCatalog.TakeChanged"/>).
    ///
    /// What a profile does NOT carry (Row.Profiled false): the computer's rows (language, keys,
    /// the button, the panels' look, what the glass may cost, logging), the panel's housekeeping,
    /// the glass mask view (a diagnostic), the shore-paint brush, and what lives elsewhere -- each
    /// building asset's own glass (RealGlassAssets.xml) and the painted shores (the city's save).
    ///
    /// "(mod defaults)": a fixed entry that is the mod's own defaults -- it cannot be saved over or
    /// deleted. Picking it puts every default in; changing anything afterwards shows "(unsaved
    /// profile)" (Save names it as a new profile); picking it again throws those changes away. It is
    /// never a file nor a name in &lt;Profile&gt;: the bar shows it whenever no profile is picked and
    /// the look is the defaults.
    ///
    /// Each profile is RealGlassProfiles\&lt;name&gt;.xml beside the settings file, in the
    /// settings file's own format: a player can send one to a friend, write one by hand, or drop
    /// one in the folder with the game running (the list is read again each time it opens). The
    /// name IS the file's name (<see cref="ProfileName"/>).
    ///
    /// The settings file stays the whole truth of the look, unsaved changes included, across a
    /// restart; its &lt;Profile&gt; only names the pick. At startup the picked profile's file is
    /// READ, never applied: only to know whether the look still matches it. While the game runs it
    /// is watched once a second: a hand edit of it is taken as if it were picked again, and a file
    /// deleted on disk is let go, never written back. A file that cannot be read is never written
    /// over, except by the player's own Save.
    ///
    /// ERROR-PROOF (the author: "if someone messes up the xml file manually, the mod shouldn't
    /// crash"): every file operation is caught, logged with the file and what was being done,
    /// and leaves the look working.
    /// </remarks>
    public static class Profiles
    {
        public const string FolderName = "RealGlassProfiles";
        private const string Extension = ".xml";

        public enum Status
        {
            /// <summary>No profile picked: the settings file alone.</summary>
            None,

            /// <summary>Picked, and its file read.</summary>
            Selected,

            /// <summary>Picked, but its file cannot be read: left alone until it can, or until Save.</summary>
            Unreadable,
        }

        private static string _folder;

        /// <summary>The picked profile's file; null while none is picked.</summary>
        private static WatchedFile _file;
        private static Status _state;
        private static string _problem;

        /// <summary>
        /// The picked profile's file could not even be opened at startup (held by another program):
        /// when it opens, it is only read, as startup would have -- that is not an edit of it.
        /// </summary>
        private static bool _neverRead;

        /// <summary>
        /// The look as it was last known to be kept -- in the picked profile's file (what picking it
        /// puts in), or the defaults right after "Reset all" -- as each profiled row's value in the
        /// file's words; null when it is kept nowhere (a profile gone, or unreadable). What
        /// "unsaved changes" is measured against. Kept when "(no profile)" lets go: the look is
        /// still the one that profile holds until something changes.
        /// </summary>
        private static Dictionary<string, string> _kept;

        /// <summary>
        /// The mod's defaults in <see cref="SettingsXml.ProfileValues"/>'s words, worked out once
        /// (they are constants): what "(Mod defaults)" is (<see cref="LookIsDefaults"/>).
        /// </summary>
        private static Dictionary<string, string> _defaults;

        private static string _lastListError;
        private static bool _loggedOddFile;

        /// <summary>Beside RealGlass.xml and the log, on every system (DataLocation.localApplicationData).</summary>
        public static string Folder
        {
            get
            {
                if (_folder == null)
                    _folder = Path.Combine(DataLocation.localApplicationData, FolderName);

                return _folder;
            }
        }

        /// <summary>The picked profile's name; empty for none.</summary>
        public static string Selected
        {
            get { return Settings.CurrentProfile != null ? Settings.CurrentProfile.value : string.Empty; }
        }

        public static Status State
        {
            get { return _state; }
        }

        /// <summary>
        /// The look is not what the picked profile holds -- or, with none picked, not what the last
        /// one held: what lights up Save, and what picking a profile would throw away.
        /// </summary>
        public static bool HasUnsavedChanges
        {
            get
            {
                Dictionary<string, string> now = SettingsXml.ProfileValues();
                if (_kept != null && SameValues(now, _kept))
                    return false;

                // With none picked, a look at the mod's defaults is kept too: "(Mod defaults)" brings
                // it back.
                return Selected.Length > 0 || !SameValues(now, DefaultValues);
            }
        }

        /// <summary>
        /// The built-in "(Mod defaults)" entry: no profile is picked and every value a profile
        /// carries is the mod's default. Not stored anywhere -- &lt;Profile&gt; stays empty, so an older
        /// version reads the file as "no profile" -- but seen: change anything and the bar says
        /// "(unsaved profile)"; pick it again (<see cref="SelectDefaults"/>) and the changes go.
        /// </summary>
        public static bool LookIsDefaults
        {
            get { return Selected.Length == 0 && SameValues(SettingsXml.ProfileValues(), DefaultValues); }
        }

        private static Dictionary<string, string> DefaultValues
        {
            get
            {
                if (_defaults == null)
                    _defaults = SettingsXml.DefaultProfileValues();

                return _defaults;
            }
        }

        /// <summary>
        /// What a row's reset button puts back: the value the picked profile holds for it,
        /// in the file's words -- what picking the profile again would put in, a value its file
        /// lacks included. The author: "The Reset button ... should reset the setting back to how it
        /// is in the user's profile. It should only reset back to mod default if the user doesn't
        /// have a profile". Null -- the mod's default, then -- with no profile picked ("(Mod
        /// defaults)" too), its file unreadable, or a row no profile carries.
        /// </summary>
        public static string KeptValueOf(Row row)
        {
            if (_state != Status.Selected || Selected.Length == 0 || _kept == null || !SettingsXml.IsProfiled(row))
                return null;

            string value;
            return _kept.TryGetValue(row.Name, out value) ? value : null;
        }

        private static bool SameValues(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            if (a.Count != b.Count)
                return false;

            foreach (KeyValuePair<string, string> pair in a)
            {
                string other;
                if (!b.TryGetValue(pair.Key, out other) || other != pair.Value)
                    return false;
            }

            return true;
        }

        public static string PathOf(string name)
        {
            return Path.Combine(Folder, name + Extension);
        }

        /// <summary>For the log's features line: which profile, and whether its file is well.</summary>
        public static string Describe()
        {
            switch (_state)
            {
                case Status.Selected:
                    return "'" + Selected + "'" + (HasUnsavedChanges ? " (the look has changes not saved in it)" : "");
                case Status.Unreadable:
                    return "'" + Selected + "' (its file cannot be read: " + _problem + ")";
                default:
                    return LookIsDefaults ? "none (the look is the mod's defaults)" : "none";
            }
        }

        // ---- the folder ------------------------------------------------------------------------

        /// <summary>
        /// Every profile in the folder, by name, sorted without regard to case; empty when there is
        /// no folder yet. Never throws: a folder that cannot be read is logged (once per reason)
        /// and lists nothing.
        /// </summary>
        public static List<string> List()
        {
            var names = new List<string>();

            try
            {
                if (!Directory.Exists(Folder))
                    return names;

                foreach (string path in Directory.GetFiles(Folder, "*" + Extension))
                {
                    // "*.xml" also matches "a.xmlx" on Windows (its rule for three-letter extensions).
                    if (!string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string name = Path.GetFileNameWithoutExtension(path);
                    if (!ProfileName.IsListable(name))
                    {
                        if (!_loggedOddFile)
                        {
                            _loggedOddFile = true;
                            Log.Msg("profiles: '" + Path.GetFileName(path) + "' in " + Folder + " is not listed: its name is " +
                                    "empty, starts or ends with a space, or holds a control character, and could not be kept " +
                                    "as the picked profile (rename the file)");
                        }

                        continue;
                    }

                    names.Add(name);
                }

                _lastListError = null;
            }
            catch (Exception e)
            {
                if (e.Message != _lastListError)
                {
                    _lastListError = e.Message;
                    Log.Warn("profiles: could not list " + Folder + ": " + e.Message);
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>The name as the folder spells it, when a profile of that name is there; else null.</summary>
        private static string Listed(string name)
        {
            foreach (string listed in List())
            {
                if (ProfileName.Same(listed, name))
                    return listed;
            }

            return null;
        }

        // ---- the player's clicks ---------------------------------------------------------------

        /// <summary>
        /// Picks a profile from the list: every value it holds is put in (a value it lacks goes
        /// back to its default), and the settings file records the pick. False when its file cannot
        /// be read, and then nothing has changed; the log says why. Never throws. Asking first
        /// about unsaved changes is the bar's business.
        /// </summary>
        public static bool Select(string name)
        {
            try
            {
                return SelectFrom(name, "picked in the panel");
            }
            catch (Exception e)
            {
                Log.Error("profile: picking '" + name + "' failed; the look is left as it is", e);
                return false;
            }
        }

        /// <summary>
        /// "(Mod defaults)": the picked profile is let go (its file kept) and every value a
        /// profile carries goes back to the mod's default -- a profile pick's rows only: the
        /// computer's rows stay. The player's own click, as a profile pick is. Nothing records it: the bar shows
        /// the entry while the look is still the defaults (<see cref="LookIsDefaults"/>). Asking first
        /// about unsaved changes is the bar's business. Never throws.
        /// </summary>
        public static bool SelectDefaults()
        {
            try
            {
                string previous = Selected;
                if (previous.Length > 0 || _file != null)
                    LetGo();

                var changed = new List<Row>();
                List<string> changes = SettingsXml.ResetProfiled(changed);

                Log.Msg("profile: picked the mod's defaults (picked in the panel" +
                        (previous.Length > 0 ? "; '" + previous + "' let go, its file kept" : "") + ") -- " +
                        (changes.Count == 0 ? "no value changed" : changes.Count + " changed: " + Joined(changes)) +
                        "; what a profile does not carry (the options page, the panel's housekeeping) is left as it is");

                SettingsCatalog.TakeChanged(changed, "the mod's defaults");
                _kept = SettingsXml.ProfileValues();

                SettingsXml.SaveNow();
                SettingsCatalog.RefreshAllUIs();
                return true;
            }
            catch (Exception e)
            {
                Log.Error("profile: putting the mod's defaults in failed", e);
                return false;
            }
        }

        /// <summary>
        /// Lets the picked profile go ("Reset all"). No value changes and its file stays: the
        /// settings file alone runs the look again. The caller saves.
        /// </summary>
        public static void Detach(string why)
        {
            string previous = Selected;
            if (previous.Length == 0 && _file == null)
                return;

            LetGo();
            Log.Msg("profile: no profile picked (" + why + ")" +
                    (previous.Length > 0 ? "; '" + previous + "' keeps its file" : "") + "; the look stays as it is");
        }

        /// <summary>
        /// "+": a new profile of the look as it is now, which is then the picked one. False when
        /// the name cannot be used or the file cannot be written -- <paramref name="error"/> is
        /// then a sentence for the player -- and nothing has changed. Never throws.
        /// </summary>
        public static bool Add(string typed, out string error)
        {
            try
            {
                return AddAs(typed, "made with +", out error);
            }
            catch (Exception e)
            {
                error = Localization.Get("Profiles.Failed");
                Log.Error("profile: making '" + typed + "' failed", e);
                return false;
            }
        }

        /// <summary>
        /// "Save": the look as it is now into the picked profile's file, over what it held -- over a
        /// file that cannot be read, too: the player's own click. False when none is picked or the
        /// file cannot be written; the log says why. Never throws.
        /// </summary>
        public static bool SaveSelected()
        {
            string name = Selected;
            if (name.Length == 0 || _file == null)
                return false;

            try
            {
                List<string> changes = ChangesSinceKept();
                bool wasUnreadable = _state == Status.Unreadable;

                // Gone with the folder a moment ago (the watcher lets a deleted file go within a
                // second): the click still means "keep this look under this name".
                if (!Directory.Exists(Folder))
                    Directory.CreateDirectory(Folder);

                string text = SettingsXml.ComposeProfile();
                SettingsXml.WriteReplacing(_file.Path, text);
                _file.Remember(text);
                _file.Broken = false;
                _state = Status.Selected;
                _problem = null;
                _neverRead = false;
                _kept = SettingsXml.ProfileValues();

                Log.Msg("profile: saved '" + name + "' (" + SettingsXml.ProfiledCount + " values, the look as it is) -> " + _file.Path +
                        " -- " + (wasUnreadable ? "over a file that could not be read"
                                  : changes.Count == 0 ? "no value changed"
                                  : changes.Count + " changed since it was saved: " + Joined(changes)));
                return true;
            }
            catch (Exception e)
            {
                Log.Warn("profile: could not save '" + name + "' (" + _file.Path + "): " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// "Revert": the picked profile's saved look put back, the
        /// changes made since thrown away -- picking it again, as the dropdown would. False when none
        /// is picked or its file cannot be read, and then nothing has changed; the log says why. The
        /// button is the player's own "throw them away", so the bar asks nothing. Never throws.
        /// </summary>
        public static bool Revert()
        {
            string name = Selected;
            if (name.Length == 0 || _file == null || _state != Status.Selected)
                return false;

            try
            {
                return SelectFrom(name, "Revert: its saved look put back");
            }
            catch (Exception e)
            {
                Log.Error("profile: reverting to '" + name + "' failed; the look is left as it is", e);
                return false;
            }
        }

        /// <summary>
        /// "-": deletes a profile's file. When it is the picked one it is let go, and the look stays
        /// as it is (kept nowhere now). False when the file cannot be deleted; the log says why.
        /// Never throws.
        /// </summary>
        public static bool Remove(string name)
        {
            try
            {
                if (!ProfileName.IsFileName(name))
                    return false;

                string path = PathOf(name);
                bool had = File.Exists(path);
                if (had)
                    File.Delete(path);

                bool wasPicked = ProfileName.Same(Selected, name);
                if (wasPicked)
                {
                    LetGo();
                    _kept = null;
                }

                Log.Msg("profile: " + (had ? "removed '" + name + "' (" + path + ")" : "'" + name + "' had no file left to remove (" + path + ")") +
                        (wasPicked ? "; no profile picked, the look stays as it is" : ""));

                SettingsXml.SaveNow();
                return true;
            }
            catch (Exception e)
            {
                Log.Warn("profile: could not remove '" + name + "' (" + PathOf(name) + "): " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// After "Reset all" (which lets the pick go first): the defaults count as kept, since a
        /// reset brings them back -- picking a profile right after one throws nothing away.
        /// </summary>
        public static void AfterReset()
        {
            _kept = SettingsXml.ProfileValues();
        }

        // ---- the settings file's side ----------------------------------------------------------

        /// <summary>
        /// Once, when the settings file has been read (SettingsXml.Load): the picked profile's
        /// file is READ -- never applied -- to know whether the look still matches it. Gone: let go.
        /// Cannot be read: kept picked and left alone. Never throws.
        /// </summary>
        internal static void Startup()
        {
            try
            {
                ReadAtStartup();
            }
            catch (Exception e)
            {
                Log.Error("profiles: could not read the picked profile at startup; running on the settings file", e);
            }
        }

        /// <summary>
        /// The settings file could not be read at startup and can be now (SettingsXml.Reload): its
        /// &lt;Profile&gt; is the pick it held then, and is read the way startup reads it -- never put
        /// in over the file's values. Whatever was picked in the panel meanwhile is let go first:
        /// the file names the pick now. Never throws.
        /// </summary>
        internal static void Reread()
        {
            _file = null;
            _state = Status.None;
            _problem = null;
            _neverRead = false;
            _kept = null;
            Startup();
        }

        private static void ReadAtStartup()
        {
            string name = Selected;
            string where = Directory.Exists(Folder)
                ? List().Count + " in " + Folder
                : "no folder yet (" + Folder + ")";

            if (name.Length == 0)
            {
                Log.Msg("profiles: " + where + "; none picked");
                return;
            }

            if (!ProfileName.IsListable(name))
            {
                LetGo();
                Log.Warn("profiles: " + where + "; " + SettingsXml.FileName + " names '" + name +
                         "', which cannot be a profile's file name; no profile picked");
                SettingsXml.SaveNow();
                return;
            }

            string listed = Listed(name);
            if (listed == null)
            {
                LetGo();
                Log.Msg("profiles: " + where + "; '" + name + "' was picked, but its file is gone (" + PathOf(name) +
                        "); no profile picked");
                SettingsXml.SaveNow();
                return;
            }

            if (listed != name)
                Settings.CurrentProfile.value = listed;

            _file = new WatchedFile(PathOf(listed));
            _state = Status.Unreadable; // until it has been read

            string text;
            try
            {
                text = File.ReadAllText(_file.Path);
            }
            catch (Exception e)
            {
                // Not remembered, so the watcher tries again every second, quietly, and then only
                // reads it (_neverRead).
                _problem = e.Message;
                _neverRead = true;
                Log.Warn("profiles: " + where + "; '" + listed + "' is picked, but its file cannot be opened (" + e.Message +
                         "); the look is the settings file's, and the profile's file is left alone until it can be read");
                return;
            }

            if (!TakeKept(text))
            {
                Log.Warn("profiles: " + where + "; '" + listed + "' is picked, but its file cannot be read (" + _problem +
                         "); the look is the settings file's, and the profile's file is left alone until it can be read. " +
                         "Fix it and it is read within a second.");
                return;
            }

            List<string> changes = ChangesSinceKept();
            Log.Msg("profiles: " + where + "; '" + listed + "' picked -- " +
                    (changes.Count == 0 ? "the look is the one it holds"
                     : "the look has " + changes.Count + " change(s) not saved in it: " + Joined(changes)));
        }

        /// <summary>Once a second (SettingsXml.Tick): has the picked profile's file been edited, or deleted?</summary>
        internal static void Check()
        {
            if (_file == null)
                return;

            try
            {
                var info = new FileInfo(_file.Path);
                if (!info.Exists)
                {
                    Deleted();
                    return;
                }

                if (!_file.Moved(info))
                    return;
            }
            catch (Exception e)
            {
                if (Log.Detailed)
                    Log.Detail("profile: could not look at " + _file.Path + ": " + e.Message);
                return;
            }

            try
            {
                Reload();
            }
            catch (Exception e)
            {
                Log.Error("profile: taking the edit of '" + Selected + "' failed", e);
            }
        }

        /// <summary>
        /// The Profile row's AfterChange: &lt;Profile&gt; was edited by hand in the settings file
        /// (the panel's bar sets it directly and never comes here). A name whose file is there is
        /// picked, as the dropdown would; a new name makes a profile of the look as it is -- a
        /// typed name is a wish for one; empty lets go. Never throws.
        /// </summary>
        internal static void OnSelectionEdited()
        {
            string current = _file != null ? Path.GetFileNameWithoutExtension(_file.Path) : string.Empty;

            try
            {
                string wanted = Selected;
                if (wanted == current)
                    return;

                if (wanted.Length == 0)
                {
                    LetGo();
                    Log.Msg("profile: <Profile> was emptied in " + SettingsXml.FileName + "; no profile picked, the look stays as it is");
                    return;
                }

                if (!ProfileName.IsListable(wanted))
                {
                    Log.Warn("profile: <Profile> in " + SettingsXml.FileName + " names '" + wanted +
                             "', which cannot be a profile's file name; left as it was");
                    Restore(current);
                    return;
                }

                string listed = Listed(wanted);
                if (listed != null)
                {
                    if (!SelectFrom(listed, "named in " + SettingsXml.FileName))
                        Restore(current);
                    return;
                }

                string error;
                if (!AddAs(wanted, "named in " + SettingsXml.FileName + ", which had no file of that name", out error))
                {
                    Log.Warn("profile: <Profile> in " + SettingsXml.FileName + " names '" + wanted +
                             "', which has no file, and none could be made (" + error + "); left as it was");
                    Restore(current);
                }
            }
            catch (Exception e)
            {
                Log.Error("profile: taking <Profile> from " + SettingsXml.FileName + " failed", e);
                Restore(current);
            }
        }

        // ---- inside --------------------------------------------------------------------------

        private static bool SelectFrom(string name, string how)
        {
            if (Settings.CurrentProfile == null || !ProfileName.IsListable(name))
                return false;

            string path = PathOf(name);
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception e)
            {
                Log.Warn("profile: '" + name + "' cannot be opened (" + path + ": " + e.Message + "); nothing was changed");
                return false;
            }

            var changed = new List<Row>();
            string error;
            SettingsXml.ReadResult result = SettingsXml.ReadProfile(text, out error, changed);
            if (result == null)
            {
                Log.Warn("profile: '" + name + "' cannot be read (" + error + ", in " + path +
                         "); nothing was changed, and the file is left as it is");
                return false;
            }

            string previous = Selected;
            Settings.CurrentProfile.value = name;
            _file = new WatchedFile(path);
            _file.Remember(text);
            _state = Status.Selected;
            _problem = null;
            _neverRead = false;

            Log.Msg("profile: picked '" + name + "' (" + how +
                    (previous.Length > 0 && previous != name ? "; was '" + previous + "'" : "") + ") -- " +
                    (changed.Count == 0 ? "no value changed" : changed.Count + " changed: " + result.Changes()) +
                    result.Describe());
            result.LogProblems("profile");

            SettingsCatalog.TakeChanged(changed, "the profile");
            _kept = SettingsXml.ProfileValues();

            // Records the pick. The profile's own file is left as it is: picking never writes it.
            SettingsXml.SaveNow();
            SettingsCatalog.RefreshAllUIs();
            return true;
        }

        private static bool AddAs(string typed, string how, out string error)
        {
            error = null;
            if (Settings.CurrentProfile == null)
            {
                error = Localization.Get("Profiles.Failed");
                return false;
            }

            string why = ProfileName.Why(typed);
            if (why != null)
            {
                error = Localization.Get(why);
                return false;
            }

            string name = ProfileName.Clean(typed);
            if (Listed(name) != null)
            {
                error = Localization.Get("Profiles.Invalid.Exists");
                return false;
            }

            string path = PathOf(name);
            string text;
            try
            {
                if (!Directory.Exists(Folder))
                {
                    Directory.CreateDirectory(Folder);
                    Log.Msg("profiles: made the folder " + Folder);
                }

                // Made by someone else since the list was read: never write over a profile.
                if (File.Exists(path))
                {
                    error = Localization.Get("Profiles.Invalid.Exists");
                    return false;
                }

                text = SettingsXml.ComposeProfile();
                SettingsXml.WriteReplacing(path, text);
            }
            catch (Exception e)
            {
                error = Localization.Get("Profiles.Failed");
                Log.Warn("profile: could not make '" + name + "' (" + path + "): " + e.Message);
                return false;
            }

            string previous = Selected;
            Settings.CurrentProfile.value = name;
            _file = new WatchedFile(path);
            _file.Remember(text);
            _state = Status.Selected;
            _problem = null;
            _neverRead = false;
            _kept = SettingsXml.ProfileValues();

            Log.Msg("profile: added '" + name + "' (" + how + "; " + SettingsXml.ProfiledCount + " values, the look as it is) -> " +
                    path + (previous.Length > 0 && previous != name ? "; '" + previous + "' is no longer picked" : "") +
                    "; it is now the picked profile");

            SettingsXml.SaveNow();
            return true;
        }

        /// <summary>
        /// The file changed on disk and it was not us: taken as if it were picked again (a value it
        /// lacks goes back to its default). A file that could not even be opened at startup is only
        /// read, as startup would have.
        /// </summary>
        private static void Reload()
        {
            string text;
            try
            {
                text = File.ReadAllText(_file.Path);
            }
            catch (Exception)
            {
                return; // an editor is still writing it or holds it; look again in a second
            }

            string name = Selected;

            if (_neverRead)
            {
                if (TakeKept(text))
                {
                    List<string> changes = ChangesSinceKept();
                    Log.Msg("profile: '" + name + "' can be opened now -- " +
                            (changes.Count == 0 ? "the look is the one it holds"
                             : "the look has " + changes.Count + " change(s) not saved in it: " + Joined(changes)));
                }
                else
                {
                    Log.Warn("profile: '" + name + "' can be opened now, but cannot be read (" + _problem + "); it is left alone until it can be read");
                }

                SettingsCatalog.RefreshAllUIs();
                return;
            }

            // Saved again unchanged, or only its date moved: nothing to take.
            bool same = text == _file.DiskText;
            _file.Remember(text);
            if (same)
                return;

            var changed = new List<Row>();
            string error;
            SettingsXml.ReadResult result = SettingsXml.ReadProfile(text, out error, changed);
            if (result == null)
            {
                MarkUnreadable(error);
                Log.Warn("profile: '" + name + "' was edited and cannot be read (" + error + "). Nothing was taken from it, " +
                         "and it will not be saved over until it can be read again, or until you press Save.");
                SettingsCatalog.RefreshAllUIs();
                return;
            }

            bool wasUnreadable = _state == Status.Unreadable;
            _file.Broken = false;
            _state = Status.Selected;
            _problem = null;

            Log.Msg("profile: '" + name + "' was edited" + (wasUnreadable ? " and can be read again" : "") + ", and is put in as if picked -- " +
                    (changed.Count == 0 ? "no value changed" : changed.Count + " changed: " + result.Changes()) +
                    result.Describe());
            result.LogProblems("profile");

            SettingsCatalog.TakeChanged(changed, "the profile's file");
            _kept = SettingsXml.ProfileValues();

            // The settings file follows at once; the profile's file stays as the player wrote it.
            SettingsXml.SaveNow();
            SettingsCatalog.RefreshAllUIs();
        }

        /// <summary>
        /// Reads a profile's text into <see cref="_kept"/> without putting it in (startup, and a file
        /// that could not be opened then). False when it cannot be read: then it is left alone.
        /// </summary>
        private static bool TakeKept(string text)
        {
            string error;
            Dictionary<string, string> kept = SettingsXml.ProfileValuesOf(text, out error);
            _neverRead = false;

            if (kept == null)
            {
                MarkUnreadable(error);
                return false;
            }

            _file.Remember(text);
            _file.Broken = false;
            _state = Status.Selected;
            _problem = null;
            _kept = kept;
            return true;
        }

        /// <summary>The picked profile's file is gone from disk: let it go, never write it back.</summary>
        private static void Deleted()
        {
            string name = Selected;
            LetGo();
            _kept = null;
            Log.Msg("profile: '" + name + "' was deleted on disk; no profile picked, the look stays as it is");

            SettingsXml.SaveNow();
            SettingsCatalog.RefreshAllUIs();
        }

        /// <summary>
        /// Kept picked, but not read and never written until it reads again (or Save). Its write
        /// time and length are remembered with no text, so the next look reads it only once it
        /// changes. What it held is not known any more: the look counts as kept nowhere.
        /// </summary>
        private static void MarkUnreadable(string error)
        {
            _file.Remember(null);
            _file.Broken = true;
            _state = Status.Unreadable;
            _problem = error;
            _kept = null;
        }

        /// <summary>No profile picked. What the look was kept as stays known (<see cref="_kept"/>).</summary>
        private static void LetGo()
        {
            if (Settings.CurrentProfile != null)
                Settings.CurrentProfile.value = string.Empty;

            _file = null;
            _state = Status.None;
            _problem = null;
            _neverRead = false;
        }

        /// <summary>What differs from the kept look, for the log: "GlassStrength 70 -> 55".</summary>
        private static List<string> ChangesSinceKept()
        {
            var changes = new List<string>();
            if (_kept == null)
                return changes;

            foreach (KeyValuePair<string, string> pair in SettingsXml.ProfileValues())
            {
                string kept;
                if (!_kept.TryGetValue(pair.Key, out kept))
                    changes.Add(pair.Key + " " + pair.Value);
                else if (kept != pair.Value)
                    changes.Add(pair.Key + " " + kept + " -> " + pair.Value);
            }

            return changes;
        }

        private static string Joined(List<string> items)
        {
            const int Shown = 12;
            if (items.Count <= Shown)
                return string.Join(", ", items.ToArray());

            return string.Join(", ", items.GetRange(0, Shown).ToArray()) + " and " + (items.Count - Shown) + " more";
        }

        /// <summary>Puts &lt;Profile&gt; back to what the watcher follows, after a hand edit that could not be taken.</summary>
        private static void Restore(string current)
        {
            try
            {
                if (Settings.CurrentProfile != null)
                    Settings.CurrentProfile.value = current;

                SettingsXml.SaveNow();
            }
            catch (Exception e)
            {
                Log.Error("profile: could not put <Profile> back to '" + current + "'", e);
            }
        }
    }
}
