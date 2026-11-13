using System.Collections.Generic;

namespace RealGlass.PerAsset
{
    /// <summary>
    /// What each building asset has of its own, from wherever it comes: in a city the PLAYER's own
    /// glass for it (RealGlassAssets.xml, the building window) over the glass its CREATOR shipped
    /// with it (RealGlassDefinition.xml, <see cref="CreatorDefinitions"/>), setting by setting
    /// (<see cref="AssetRecord.Over"/>); in the asset editor only the glass of the building being made
    /// (<see cref="CreatorAsset"/>) -- what a player with no glass of their own for it sees. Whatever
    /// draws an asset's glass reads it here (AssetMap, LodGlassMap, the rooms' switch).
    /// </summary>
    public static class AssetRecords
    {
        private static long _seen = -1;
        private static readonly List<KeyValuePair<BuildingInfo, AssetRecord>> LoadedList = new List<KeyValuePair<BuildingInfo, AssetRecord>>();
        private static bool _anyInteriors, _anyDirt;
        private static int _byPlayer, _byCreator;

        /// <summary>
        /// Goes up whenever any asset's glass changes, from any source. Each source's version only
        /// ever goes up, so a weighted sum of them changes whenever one does.
        /// </summary>
        public static long Stamp
        {
            get { return AssetFile.Version * 1000003L + CreatorDefinitions.Version * 1009L + CreatorAsset.Version; }
        }

        /// <summary>The asset's glass of its own, every source laid together; null when it follows every global setting.</summary>
        public static AssetRecord Of(BuildingInfo info)
        {
            if (info == null)
                return null;
            if (CreatorAsset.InEditor)
                return info == CreatorAsset.Info ? CreatorAsset.Record : null;

            return AssetRecord.Over(AssetFile.Get(info.name), CreatorDefinitions.Of(info));
        }

        /// <summary>As <see cref="Of(BuildingInfo)"/>, by the asset's name (BuildingInfo.name).</summary>
        public static AssetRecord OfName(string name)
        {
            if (name == null)
                return null;
            if (CreatorAsset.InEditor)
                return CreatorAsset.Info != null && CreatorAsset.Info.name == name ? CreatorAsset.Record : null;

            return AssetRecord.Over(AssetFile.Get(name), CreatorDefinitions.OfName(name));
        }

        /// <summary>
        /// The loaded building assets with glass of their own, each once, every source laid together.
        /// Rebuilt when <see cref="Stamp"/> moves; do not change it.
        /// </summary>
        public static List<KeyValuePair<BuildingInfo, AssetRecord>> Loaded
        {
            get
            {
                Refresh();
                return LoadedList;
            }
        }

        /// <summary>Some loaded asset has rooms of its own switched ON (they show even while the Interiors tab's switch is off).</summary>
        public static bool AnyInteriorsOn
        {
            get
            {
                Refresh();
                return _anyInteriors;
            }
        }

        /// <summary>Some loaded asset has dirt of its own above 0 (it shows even while the Glass tab's Dirt is at 0).</summary>
        public static bool AnyDirt
        {
            get
            {
                Refresh();
                return _anyDirt;
            }
        }

        /// <summary>For the log: of <see cref="Loaded"/>, how many have glass the player gave them, how many their creator's.</summary>
        public static string Describe()
        {
            Refresh();
            return LoadedList.Count + " loaded building asset" + (LoadedList.Count == 1 ? "" : "s") + " with glass of their own (" +
                   _byPlayer + " set by you, " + _byCreator + " by their creator)";
        }

        private static void Refresh()
        {
            long stamp = Stamp;
            if (stamp == _seen)
                return;
            _seen = stamp;

            LoadedList.Clear();
            _anyInteriors = false;
            _anyDirt = false;
            _byPlayer = _byCreator = 0;

            if (CreatorAsset.InEditor)
            {
                if (CreatorAsset.Info != null && CreatorAsset.Record != null && !CreatorAsset.Record.IsEmpty)
                    Add(CreatorAsset.Info, CreatorAsset.Record);
                return;
            }

            var done = new HashSet<string>();
            foreach (AssetRecord own in AssetFile.All)
            {
                BuildingInfo info = PrefabCollection<BuildingInfo>.FindLoaded(own.Name);
                if (info == null || !done.Add(info.name))
                    continue;
                _byPlayer++;
                AssetRecord creator = CreatorDefinitions.Of(info);
                if (creator != null)
                    _byCreator++;
                Add(info, AssetRecord.Over(own, creator));
            }

            List<KeyValuePair<BuildingInfo, AssetRecord>> creators = CreatorDefinitions.Loaded;
            for (int i = 0; i < creators.Count; i++)
            {
                if (!done.Add(creators[i].Key.name))
                    continue;
                _byCreator++;
                Add(creators[i].Key, creators[i].Value);
            }
        }

        private static void Add(BuildingInfo info, AssetRecord record)
        {
            if (record == null || record.IsEmpty)
                return;
            LoadedList.Add(new KeyValuePair<BuildingInfo, AssetRecord>(info, record));
            if (record.Interiors.HasValue && record.Interiors.Value)
                _anyInteriors = true;
            if (record.Dirt.HasValue && record.Dirt.Value > 0f)
                _anyDirt = true;
        }
    }
}
