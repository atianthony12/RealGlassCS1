using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ColossalFramework.Packaging;

namespace RealGlass.PerAsset
{
    /// <summary>
    /// RealGlassDefinition.xml: the glass building assets' CREATORS gave them, in the folder of their
    /// .crp -- the pattern creators know from Ploppable RICO's PloppableRICODefinition.xml (143 of the
    /// author's Workshop items ship one) and the Effects mods' EffectsDefinition.xml. One file a
    /// folder, an Asset element per asset by its own name (<see cref="AssetFileFormat.DefinitionRoot"/>):
    /// a pack of several .crp in one Workshop item has one file, and so has Addons\Assets, where the
    /// asset editor saves every asset (SaveAssetPanel.GetSavePathName, no subfolder).
    /// </summary>
    /// <remarks>
    /// Written by the asset editor when an asset is saved (<see cref="CreatorAsset.Saved"/>), merged
    /// with the other assets' entries; put into an upload's Content folder when the asset is shared
    /// (<see cref="CopyFor"/>), the one thing the game uploads besides the preview
    /// (WorkshopAssetUploadPanel.UpdateItem: every file in m_ContentPath). Read in players' games
    /// (<see cref="CreatorDefinitions"/>). Never throws.
    /// </remarks>
    public static class CreatorFile
    {
        public const string FileName = "RealGlassDefinition.xml";
        private const string UnreadableName = "RealGlassDefinition.unreadable.xml";

        public static string PathIn(string folder)
        {
            return Path.Combine(folder, FileName);
        }

        /// <summary>Every entry of the file in this folder; none when it has no file. error: why it could not be read in full.</summary>
        public static List<AssetRecord> Read(string folder, List<string> problems, out string error)
        {
            error = null;
            try
            {
                string path = PathIn(folder);
                if (!File.Exists(path))
                    return new List<AssetRecord>();

                int version;
                return AssetFileFormat.Parse(File.ReadAllText(path), AssetFileFormat.DefinitionRoot, out version, problems, out error);
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return new List<AssetRecord>();
            }
        }

        /// <summary>
        /// One asset's entry in this folder's file, under its own name, written now: put in (or taken
        /// out, for a record with nothing of its own), the other assets' entries kept. A file that could
        /// not be read in full is kept as RealGlassDefinition.unreadable.xml first. False, with
        /// <paramref name="problem"/>, when nothing could be written.
        /// </summary>
        public static bool Put(string folder, string assetName, AssetRecord record, out string problem)
        {
            problem = null;
            if (SettingsXml.ReadOnly)   // off after a game update (GameUpdate): nothing written
                return true;

            try
            {
                string path = PathIn(folder);
                string existing = File.Exists(path) ? File.ReadAllText(path) : null;
                if (existing == null && (record == null || record.IsEmpty))
                    return true;

                var problems = new List<string>();
                string error;
                string text = AssetFileFormat.MergeDefinition(existing, assetName, record, Header(), problems, out error);
                if (text == existing)
                    return true;

                if (error != null)
                {
                    File.Copy(path, Path.Combine(folder, UnreadableName), true);
                    Log.Warn("creator: " + path + " could not be read in full (" + error + "); kept as " + UnreadableName +
                             " before writing it again");
                }
                if (problems.Count > 0)
                    Log.Warn("creator: " + path + ": not taken as written -- " + string.Join("; ", problems.ToArray()));

                WriteReplacing(path, text);
                return true;
            }
            catch (Exception e)
            {
                problem = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// The entries of this package's assets, from the file beside its .crp, written as a
        /// RealGlassDefinition.xml of their own into <paramref name="target"/> (an upload's Content
        /// folder). How many; 0 writes nothing.
        /// </summary>
        public static int CopyFor(Package package, string target, out string problem)
        {
            problem = null;
            if (SettingsXml.ReadOnly)   // off after a game update (GameUpdate): nothing written
                return 0;

            try
            {
                string crp = package.packagePath;
                if (string.IsNullOrEmpty(crp) || string.IsNullOrEmpty(target))
                    return 0;

                string error;
                List<AssetRecord> entries = Read(Path.GetDirectoryName(crp), null, out error);
                var mine = new List<AssetRecord>();
                for (int i = 0; i < entries.Count; i++)
                {
                    if (InPackage(package, entries[i].Name))
                        mine.Add(entries[i]);
                }
                if (mine.Count == 0)
                    return 0;

                File.WriteAllText(PathIn(target), AssetFileFormat.Write(mine, Header(), AssetFileFormat.DefinitionRoot), new UTF8Encoding(false));
                return mine.Count;
            }
            catch (Exception e)
            {
                problem = e.GetType().Name + ": " + e.Message;
                return 0;
            }
        }

        /// <summary>The package holds the asset an entry names: its prefab ("Tower_Data") or an asset of that name.</summary>
        private static bool InPackage(Package package, string entryName)
        {
            string own = AssetFileFormat.Stem(entryName);
            return own.Length > 0 && (package.Find(own + AssetFileFormat.DataSuffix) != null || package.Find(own) != null);
        }

        private static string Header()
        {
            return Localization.Get("CreatorFile.Header");
        }

        private static void WriteReplacing(string path, string text)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (!File.Exists(path))
            {
                File.Move(temp, path);
                return;
            }

            try
            {
                File.Replace(temp, path, null);
            }
            catch (Exception)
            {
                File.Copy(temp, path, true);
                File.Delete(temp);
            }
        }
    }
}
