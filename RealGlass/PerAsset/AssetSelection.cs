using ColossalFramework;
using ColossalFramework.Globalization;
using RealGlass.Glass;
using UnityEngine;

namespace RealGlass.PerAsset
{
    /// <summary>
    /// The building asset the building window (UI.AssetPanel) is about: picked by clicking one of its copies
    /// (<see cref="AssetPicker"/>). A building of a complex picks its main building's asset -- what
    /// the glass of the whole complex follows (AssetMap). A building of Procedural Objects picks its
    /// own asset (ProceduralBuildings).
    /// </summary>
    public static class AssetSelection
    {
        /// <summary>The main building that was clicked; 0 for none, or a building of Procedural Objects.</summary>
        public static ushort Building { get; private set; }

        public static BuildingInfo Info { get; private set; }

        /// <summary>The title the game shows for the asset.</summary>
        public static string Title { get; private set; }

        /// <summary>Its copies in this city (main buildings), counted every couple of seconds while shown.</summary>
        public static int Copies { get; private set; }

        public static bool HasSelection
        {
            get { return Info != null; }
        }

        public static string Name
        {
            get { return Info != null ? Info.name : null; }
        }

        /// <summary>The asset's own glass as the player stored it, or null while it follows its creator's and the global settings.</summary>
        public static AssetRecord Record
        {
            get { return Info != null ? AssetFile.Get(Info.name) : null; }
        }

        /// <summary>The glass its creator shipped with it (RealGlassDefinition.xml), beneath the player's; null for none.</summary>
        public static AssetRecord CreatorRecord
        {
            get { return Info != null ? CreatorDefinitions.Of(Info) : null; }
        }

        /// <summary>A copy of the asset's glass to change and <see cref="Store"/>.</summary>
        public static AssetRecord Editable()
        {
            AssetRecord stored = Record;
            AssetRecord record = stored != null ? stored.Clone() : new AssetRecord(Info.name);
            record.Title = Title;
            return record;
        }

        /// <summary>Picks the asset of this building (a sub-building: its main building's).</summary>
        public static void Select(ushort building)
        {
            if (building == 0 || !Singleton<BuildingManager>.exists)
                return;

            Building[] buffer = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            ushort main = buffer[building].m_parentBuilding != 0 ? global::Building.FindParentBuilding(building) : building;
            if (main == 0)
                main = building;

            BuildingInfo info = buffer[main].Info;
            if (info == null)
                return;

            Choose(info, main, "building " + main + (main != building ? " (the main building of " + building + ")" : ""));
        }

        /// <summary>
        /// Picks the asset of a building of Procedural Objects (ProceduralBuildings): no building of
        /// the city's, but a copy of the asset all the same, its glass the asset's. what: for the log.
        /// </summary>
        public static void SelectAsset(BuildingInfo info, string what)
        {
            if (info != null)
                Choose(info, 0, what);
        }

        private static void Choose(BuildingInfo info, ushort main, string what)
        {
            Building = main;
            Info = info;
            Title = TitleOf(info);
            Copies = CountCopies(info);

            AssetRecord record = Record, creator = CreatorRecord;
            Log.Msg("assets: picked " + what + ": asset '" + info.name + "' (" + Title + "), " + Copies + " in this city, " +
                    (record != null ? "with glass of its own: " + Describe(record) : "on the global glass") +
                    (creator != null ? "; its creator's glass beneath: " + Describe(creator) : ""));
        }

        /// <summary>The asset's copies with glass: the city's buildings and Procedural Objects' own.</summary>
        private static int CountCopies(BuildingInfo info)
        {
            return AssetMap.CountCopies(info) + ProceduralBuildings.CountCopies(info);
        }

        /// <summary>The city is going: nothing picked.</summary>
        public static void Clear()
        {
            Building = 0;
            Info = null;
            Title = null;
            Copies = 0;
        }

        /// <summary>
        /// Stores the asset's glass (a record with nothing of its own removes it) and shows it on every
        /// copy now. Logged when the file is saved, a second after the last change (AssetFile): a
        /// slider drag is one line, not sixty. <paramref name="what"/> goes to the detailed log.
        /// </summary>
        public static void Store(AssetRecord record, string what)
        {
            if (Info == null || record == null)
                return;

            AssetFile.Put(record);
            GlassController.RefreshAsset(Info);
            if (Log.Detailed)
                Log.Detail("assets: '" + Info.name + "' " + what);
        }

        /// <summary>Every setting of the asset back to the global one.</summary>
        public static void UseGlobal()
        {
            if (Info == null || Record == null)
                return;

            Store(new AssetRecord(Info.name), "back to the global settings");
        }

        // The building window's status lines (copies in the city, the LOD's verdict, the rooms' fit, the
        // detected shininess) were taken out on the author's call (2026-11-13). The pick's log line still
        // says how many copies stand in the city.

        /// <summary>What was found of the asset's windows for its rooms (RoomFit): the creator window's status line; "" while nothing is known.</summary>
        internal static string RoomsText(RoomFitResult r)
        {
            if (r == null)
                return "";
            if (!r.Measured)
                return Localization.Get("Asset.Rooms.Failed");
            if (!r.Rooms)
                return Localization.Get("Asset.Rooms.None");
            if (r.Storey > 0f)
                return Localization.Get(r.SidesNarrow > 0 ? "Asset.Rooms.FittedNarrow" : "Asset.Rooms.Fitted", r.Storey.ToString("0.0#"));
            return Localization.Get("Asset.Rooms.Grid");
        }

        /// <summary>The asset's title as the game shows it; its name where it has none.</summary>
        public static string TitleOf(BuildingInfo info)
        {
            try
            {
                if (Locale.Exists("BUILDING_TITLE", info.name))
                    return Locale.Get("BUILDING_TITLE", info.name);
            }
            catch (System.Exception)
            {
                // A title is a nicety: the name will do.
            }

            return info.name;
        }

        /// <summary>The asset's own settings in one line, for the log.</summary>
        public static string Describe(AssetRecord r)
        {
            string s = "";
            if (r.Strength.HasValue) s += "strength " + Pct(r.Strength.Value) + ", ";
            if (r.PaintedDetail.HasValue) s += "painted detail " + Pct(r.PaintedDetail.Value) + ", ";
            if (r.Preset.HasValue) s += "glass " + GlassPresets.LogName(r.Preset.Value) + ", ";
            if (r.Colour.HasValue) s += "colour " + ColorText.Format(r.Colour.Value) + ", ";
            if (r.Reflectivity.HasValue) s += "reflectivity " + Pct(r.Reflectivity.Value) + ", ";
            if (r.Clarity.HasValue) s += "clarity " + Pct(r.Clarity.Value) + ", ";
            if (r.OwnColour.HasValue) s += "own colour " + Pct(r.OwnColour.Value) + ", ";
            if (r.Threshold.HasValue) s += "glass above " + Pct(r.Threshold.Value) + ", ";
            if (r.NightWindows.HasValue) s += "lit windows " + (r.NightWindows.Value ? "are glass" : "are not glass") + ", ";
            if (r.Dirt.HasValue) s += "dirt " + Pct(r.Dirt.Value) + ", ";
            if (r.Interiors.HasValue) s += (r.Interiors.Value ? "rooms" : "no rooms") + " behind the windows, ";
            if (r.Curtains.HasValue) s += (r.Curtains.Value ? "curtains or blinds" : "no curtains or blinds") + " in its rooms, ";
            if (r.FollowIllumination.HasValue) s += "rooms lit " + (r.FollowIllumination.Value ? "by its illumination map" : "by ours") + ", ";
            if (r.LodGlass.HasValue) s += "its LODs " + (r.LodGlass.Value ? "show the model's glass" : "keep their own glass") + ", ";
            return s.Length > 0 ? s.Substring(0, s.Length - 2) : "nothing of its own";
        }

        private static string Pct(float v)
        {
            return Mathf.RoundToInt(v * 100f) + "%";
        }
    }
}
