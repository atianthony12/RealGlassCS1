using System;
using System.Collections.Generic;
using ColossalFramework;
using RealGlass.Props;
using UnityEngine;

namespace RealGlass.PerAsset
{
    /// <summary>
    /// The props the prop window (UI.PropPanel) is about (2.0.0): ONE prop asset, when a prop standing
    /// on its own or one of Procedural Objects' was picked with the building picker (Alt+G), or every
    /// prop asset a BUILDING carries,
    /// from the building window's Props button (the author's pick: a building's props are no
    /// instances of the city's, so they are chosen from a list, not pointed at). Either way a choice
    /// is the prop ASSET's -- every copy of it, wherever it stands (on its own, in any building, along
    /// a road), in every city, kept in RealGlassAssets.xml (<see cref="PropRecord"/>).
    /// </summary>
    public static class PropSelection
    {
        /// <summary>The props shown, in the window's order: each listed once.</summary>
        private static readonly List<PropInfo> Shown = new List<PropInfo>();

        /// <summary>Goes up whenever the list changes: the window rebuilds its rows.</summary>
        public static int ListVersion { get; private set; }

        /// <summary>The window's title: the prop's, or "Props of" the building's.</summary>
        public static string Title { get; private set; }

        /// <summary>The building whose props are listed; null for a prop picked on its own.</summary>
        public static BuildingInfo Building { get; private set; }

        public static IList<PropInfo> Props
        {
            get { return Shown; }
        }

        public static bool HasSelection
        {
            get { return Shown.Count > 0; }
        }

        /// <summary>The prop whose settings the window's rows show: the one picked, or the one clicked in a building's list.</summary>
        public static PropInfo Selected { get; private set; }

        /// <summary>A prop of the list chosen for the window's rows.</summary>
        public static void Select(PropInfo info)
        {
            if (info == null || info == Selected)
                return;
            Selected = info;
            PropRecord record = AssetFile.GetProp(info.name);
            Log.Msg("props: '" + info.name + "' (" + TitleOf(info) + ") chosen in the list, " +
                    (record != null ? Describe(record) : "as the game draws it") + (CanDraw(info) ? "" : " -- " + WhyNot(info)));
        }

        /// <summary>A prop standing on its own, picked (PropManager's prop <paramref name="propId"/>).</summary>
        public static void SelectFree(ushort propId)
        {
            if (propId == 0 || !Singleton<PropManager>.exists)
                return;

            SelectAsset(Singleton<PropManager>.instance.m_props.m_buffer[propId].Info, "prop " + propId);
        }

        /// <summary>A prop asset picked through one copy of it (<paramref name="what"/>: which, for the log) -- a prop of Procedural Objects, for one.</summary>
        public static void SelectAsset(PropInfo info, string what)
        {
            if (info == null)
                return;

            Shown.Clear();
            Shown.Add(info);
            Selected = info;
            Building = null;
            Title = TitleOf(info);
            ListVersion++;
            PropRecord record = AssetFile.GetProp(info.name);
            Log.Msg("props: picked " + what + ": asset '" + info.name + "' (" + Title + "), " +
                    (record != null ? Describe(record) : "as the game draws it") + (CanDraw(info) ? "" : " -- " + WhyNot(info)));
        }

        /// <summary>Every prop asset <paramref name="building"/> carries (its sub-buildings' too), for its window's Props button.</summary>
        public static void SelectOf(BuildingInfo building)
        {
            if (building == null)
                return;

            Shown.Clear();
            AddProps(building, new HashSet<PropInfo>(), 0, Shown);
            Shown.Sort(Compare);
            Selected = null;   // nothing chosen until one is clicked: no rows under the list till then (the author's call)
            Building = building;
            Title = Localization.Get("Props.Of", AssetSelection.TitleOf(building));
            ListVersion++;
            Log.Msg("props: the props of '" + building.name + "': " + Shown.Count + " asset" + (Shown.Count == 1 ? "" : "s") +
                    " (" + CountSeeThrough() + " see-through)");
        }

        /// <summary>How many props a building carries (its sub-buildings' too), for the building window's button.</summary>
        public static int CountOf(BuildingInfo building)
        {
            if (building == null)
                return 0;
            var found = new List<PropInfo>();
            AddProps(building, new HashSet<PropInfo>(), 0, found);
            return found.Count;
        }

        private static void AddProps(BuildingInfo building, HashSet<PropInfo> seen, int depth, List<PropInfo> into)
        {
            if (building == null || depth > 4)
                return;

            if (building.m_props != null)
            {
                foreach (BuildingInfo.Prop prop in building.m_props)
                {
                    if (prop == null)
                        continue;
                    PropInfo info = prop.m_finalProp != null ? prop.m_finalProp : prop.m_prop;
                    AddProp(info, seen, into);
                }
            }

            if (building.m_subBuildings != null)
            {
                foreach (BuildingInfo.SubInfo sub in building.m_subBuildings)
                {
                    if (sub != null)
                        AddProps(sub.m_buildingInfo, seen, depth + 1, into);
                }
            }
        }

        /// <summary>A prop, or -- one that is a set of variations -- each variation the game may draw in its place.</summary>
        private static void AddProp(PropInfo info, HashSet<PropInfo> seen, List<PropInfo> into)
        {
            if (info == null || !seen.Add(info))
                return;

            if (info.m_variations != null && info.m_variations.Length > 0)
            {
                foreach (PropInfo.Variation variation in info.m_variations)
                    AddProp(variation.m_finalProp != null ? variation.m_finalProp : variation.m_prop, seen, into);
            }

            if (!info.m_isMarker && info.m_mesh != null && info.m_material != null)
                into.Add(info);
        }

        /// <summary>The window's order: the props with a surface of ours, then the see-through ones (glass and water by intent), then by title.</summary>
        private static int Compare(PropInfo a, PropInfo b)
        {
            int rank = Rank(a).CompareTo(Rank(b));
            return rank != 0 ? rank : string.Compare(TitleOf(a), TitleOf(b), StringComparison.CurrentCultureIgnoreCase);
        }

        private static int Rank(PropInfo info)
        {
            if (AssetFile.GetProp(info.name) != null)
                return 0;
            if (!CanDraw(info))
                return 3;
            return PropGlassDraw.IsSeeThrough(info.m_material) ? 1 : 2;
        }

        private static int CountSeeThrough()
        {
            int n = 0;
            foreach (PropInfo info in Shown)
            {
                if (PropGlassDraw.IsSeeThrough(info.m_material))
                    n++;
            }
            return n;
        }

        /// <summary>The city is going: nothing picked.</summary>
        public static void Clear()
        {
            Shown.Clear();
            Selected = null;
            Building = null;
            Title = null;
            ListVersion++;
        }

        /// <summary>How the game draws a prop, by its material's RenderType (PropInfo.InitializePrefab, IL 2026-11-08).</summary>
        public enum Shape
        {
            /// <summary>Nothing to draw over: a marker (never drawn in a city), no mesh or material.</summary>
            None,

            /// <summary>Its mesh where it stands.</summary>
            Mesh,

            /// <summary>"Decal": its mesh is a box, projected onto what lies in it (the depth), its shape the texture's alpha. None of ours on it.</summary>
            Decal,

            /// <summary>"Fence": its vertices follow the ground's height map (m_requireHeightMap).</summary>
            Fence,

            /// <summary>"Floating": its vertices ride the water's surface (m_requireWaterMap; m_requireHeightMap too).</summary>
            Floating,
        }

        public static Shape ShapeOf(PropInfo info)
        {
            if (info == null || info.m_isMarker || info.m_mesh == null || info.m_material == null)
                return Shape.None;
            if (info.m_isDecal)
                return Shape.Decal;
            if (info.m_requireWaterMap)
                return Shape.Floating;
            if (info.m_requireHeightMap)
                return Shape.Fence;
            return Shape.Mesh;
        }

        /// <summary>Ours can go over the prop: a shape the props' glass draws (PropGlassDraw.CanDrawShape).</summary>
        public static bool CanDraw(PropInfo info)
        {
            return PropGlassDraw.CanDrawShape(ShapeOf(info));
        }

        private static string WhyNot(PropInfo info)
        {
            Shape shape = ShapeOf(info);
            return shape == Shape.None ? "nothing of it to draw over" : "a " + shape.ToString().ToLowerInvariant() + " prop: none of ours on it";
        }

        /// <summary>The prop's surface now: Game, Glass or Water.</summary>
        public static int SurfaceOf(PropInfo info)
        {
            PropRecord record = info != null ? AssetFile.GetProp(info.name) : null;
            return record != null ? record.SurfaceOrGame : PropRecord.Game;
        }

        /// <summary>
        /// The selected prop's settings changed (PropCatalog): stored -- a record with no surface is
        /// removed, its settings with it -- and on every copy from the next frame (the draw's table
        /// follows AssetFile.Version). <paramref name="what"/> goes to the detailed log.
        /// </summary>
        public static void Store(PropRecord record, string what)
        {
            if (Selected == null || record == null)
                return;
            record.Title = TitleOf(Selected);
            if (record.IsEmpty)
                record = new PropRecord(Selected.name);
            AssetFile.PutProp(record);
            if (Log.Detailed)
                Log.Detail("props: '" + Selected.name + "' " + what);
        }

        /// <summary>The prop's title as the game shows it; its name where it has none.</summary>
        public static string TitleOf(PropInfo info)
        {
            if (info == null)
                return "";
            try
            {
                string title = info.GetLocalizedTitle();
                if (!string.IsNullOrEmpty(title))
                    return title;
            }
            catch (Exception)
            {
                // A title is a nicety: the name will do.
            }
            return info.name;
        }

        /// <summary>The prop's surface in one line, for the log.</summary>
        public static string Describe(PropRecord r)
        {
            string s = PropRecord.SurfaceName(r.SurfaceOrGame);
            if (r.Strength.HasValue) s += ", strength " + Pct(r.Strength.Value);
            if (r.Preset.HasValue) s += ", glass " + GlassPresets.LogName(r.Preset.Value);
            if (r.Colour.HasValue) s += ", colour " + ColorText.Format(r.Colour.Value);
            if (r.Reflectivity.HasValue) s += ", reflectivity " + Pct(r.Reflectivity.Value);
            if (r.Clarity.HasValue) s += ", clarity " + Pct(r.Clarity.Value);
            if (r.Threshold.HasValue) s += ", only above " + Pct(r.Threshold.Value) + " shine";
            if (r.Polish.HasValue) s += ", polish " + Pct(r.Polish.Value);
            if (r.WaterStrength.HasValue) s += ", water strength " + Pct(r.WaterStrength.Value);
            if (r.RippleStrength.HasValue || r.RippleSize.HasValue || r.RippleSpeed.HasValue)
                s += ", ripples " + Pct(r.RippleStrength ?? 1f) + " / size " + Pct(r.RippleSize ?? 1f) + " / speed " + Pct(r.RippleSpeed ?? 1f);
            if (r.Glint.HasValue) s += ", sun glint " + Pct(r.Glint.Value);
            return s;
        }

        private static string Pct(float v)
        {
            return Mathf.RoundToInt(v * 100f) + "%";
        }
    }
}
