using System;
using System.Collections.Generic;
using ColossalFramework;
using ColossalFramework.Math;
using RealGlass.Glass;
using RealGlass.Props;
using UnityEngine;

namespace RealGlass.PerAsset
{
    /// <summary>One thing the building picker can pick: a city's building or prop, one of Procedural Objects', or a prop another mod draws.</summary>
    public sealed class PickTarget
    {
        public enum Kind
        {
            Building,
            Prop,
            Procedural,
            Other,
        }

        public Kind What;

        /// <summary>The city's building (its main building) or prop.</summary>
        public ushort Id;

        public ProceduralBuildings.Item Item;

        /// <summary>A prop another mod draws (Intersection Marking Tool's): its asset and matrix.</summary>
        public PropInfo Info;
        public Matrix4x4 Matrix;

        /// <summary>Along the ray, metres.</summary>
        public float Distance;

        /// <summary>Its asset's name as the game shows it.</summary>
        public string Title
        {
            get
            {
                switch (What)
                {
                    case Kind.Building:
                        return AssetSelection.TitleOf(Singleton<BuildingManager>.instance.m_buildings.m_buffer[Id].Info);
                    case Kind.Prop:
                        return PropSelection.TitleOf(Singleton<PropManager>.instance.m_props.m_buffer[Id].Info);
                    case Kind.Procedural:
                        var poProp = Item as ProceduralBuildings.PropEntry;
                        var poBuilding = Item as ProceduralBuildings.Entry;
                        return poProp != null ? PropSelection.TitleOf(poProp.Info) : AssetSelection.TitleOf(poBuilding != null ? poBuilding.Info : null);
                    default:
                        return PropSelection.TitleOf(Info);
                }
            }
        }

        /// <summary>A prop (opens the prop window), not a building.</summary>
        public bool IsProp
        {
            get { return What == Kind.Prop || What == Kind.Other || (What == Kind.Procedural && Item is ProceduralBuildings.PropEntry); }
        }
    }

    /// <summary>
    /// Everything under the cursor, nearest first (1.1.9: the picker's Ctrl+Space, as Node Controller
    /// steps through what is under its cursor): the city's buildings and props (each one's own ray test,
    /// Building.RayCast / PropInstance.RayCast, over the managers' 270 x 270 grids of 64 m -- the
    /// game's RayCast keeps only the nearest), Procedural Objects' objects and the props another mod
    /// draws. The ray stops a little past the ground (nothing is picked through the terrain). Main
    /// thread, on the key only.
    /// </summary>
    public static class PickStack
    {
        private const int GridSize = 270;
        private const float CellSize = 64f;
        private const float HalfMap = GridSize * CellSize * 0.5f;
        private const int Reach = 2;            // cells round each sample: a building or prop stands in its position's cell, and reaches ~100 m
        private const float Step = 32f;
        private const float PastGround = 16f;
        private const int MostTargets = 64;

        private static readonly HashSet<int> Cells = new HashSet<int>();
        private static readonly Dictionary<ushort, float> Buildings = new Dictionary<ushort, float>();
        private static readonly List<KeyValuePair<ProceduralBuildings.Item, float>> ProceduralHits = new List<KeyValuePair<ProceduralBuildings.Item, float>>();
        private static readonly List<KeyValuePair<OtherModsSeen.Seen, float>> OtherHits = new List<KeyValuePair<OtherModsSeen.Seen, float>>();

        public static List<PickTarget> Along(Ray ray, float length)
        {
            var targets = new List<PickTarget>();
            try
            {
                Vector3 dir = ray.direction.normalized;
                length = Mathf.Min(length, GroundAlong(ray.origin, dir, length) + PastGround);
                Segment3 segment = new Segment3(ray.origin, ray.origin + dir * length);

                CellsAlong(ray.origin, dir, length);
                CityHits(segment, length, targets);

                ProceduralHits.Clear();
                ProceduralBuildings.Refresh();
                ProceduralBuildings.RaycastAll(new Ray(ray.origin, dir), length, ProceduralHits);
                foreach (KeyValuePair<ProceduralBuildings.Item, float> hit in ProceduralHits)
                    targets.Add(new PickTarget { What = PickTarget.Kind.Procedural, Item = hit.Key, Distance = hit.Value });
                ProceduralHits.Clear();

                OtherHits.Clear();
                OtherModsSeen.RaycastAll(new Ray(ray.origin, dir), length, OtherHits);
                foreach (KeyValuePair<OtherModsSeen.Seen, float> hit in OtherHits)
                    targets.Add(new PickTarget { What = PickTarget.Kind.Other, Info = hit.Key.Info, Matrix = hit.Key.Matrix, Distance = hit.Value });
                OtherHits.Clear();
            }
            catch (Exception e)
            {
                Log.Warn("assets: listing what is under the cursor: " + e.Message);
            }

            targets.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            if (targets.Count > MostTargets)
                targets.RemoveRange(MostTargets, targets.Count - MostTargets);
            return targets;
        }

        /// <summary>Where the ray first goes under the ground, in 8 m steps (its length when it never does).</summary>
        private static float GroundAlong(Vector3 origin, Vector3 dir, float length)
        {
            TerrainManager terrain = Singleton<TerrainManager>.instance;
            for (float t = 0f; t < length; t += 8f)
            {
                Vector3 p = origin + dir * t;
                if (Mathf.Abs(p.x) > HalfMap || Mathf.Abs(p.z) > HalfMap)
                {
                    if (t > 0f)
                        return t;   // left the map
                    continue;
                }
                if (p.y < terrain.SampleDetailHeight(p))
                    return t;
            }
            return length;
        }

        private static void CellsAlong(Vector3 origin, Vector3 dir, float length)
        {
            Cells.Clear();
            for (float t = 0f; ; t += Step)
            {
                float tt = Mathf.Min(t, length);
                Vector3 p = origin + dir * tt;
                int cx = (int)(p.x / CellSize + GridSize * 0.5f), cz = (int)(p.z / CellSize + GridSize * 0.5f);
                for (int z = Math.Max(cz - Reach, 0); z <= Math.Min(cz + Reach, GridSize - 1); z++)
                {
                    for (int x = Math.Max(cx - Reach, 0); x <= Math.Min(cx + Reach, GridSize - 1); x++)
                        Cells.Add(z * GridSize + x);
                }
                if (tt >= length)
                    break;
            }
        }

        private static void CityHits(Segment3 segment, float length, List<PickTarget> targets)
        {
            BuildingManager bm = Singleton<BuildingManager>.instance;
            PropManager pm = Singleton<PropManager>.instance;
            Building[] buildings = bm.m_buildings.m_buffer;
            PropInstance[] props = pm.m_props.m_buffer;
            Buildings.Clear();

            foreach (int cell in Cells)
            {
                int guard = 0;
                ushort id = bm.m_buildingGrid[cell];
                while (id != 0 && guard++ < 49152)
                {
                    if ((buildings[id].m_flags & (Building.Flags.Created | Building.Flags.Deleted)) == Building.Flags.Created)
                    {
                        float t;
                        if (buildings[id].RayCast(id, segment, out t))
                        {
                            ushort main = buildings[id].m_parentBuilding != 0 ? Building.FindParentBuilding(id) : id;
                            if (main == 0)
                                main = id;
                            float d = t * length, had;
                            if (!Buildings.TryGetValue(main, out had) || d < had)
                                Buildings[main] = d;
                        }
                    }
                    id = buildings[id].m_nextGridBuilding;
                }

                guard = 0;
                ushort p = pm.m_propGrid[cell];
                while (p != 0 && guard++ < 65536)
                {
                    if (((PropInstance.Flags)props[p].m_flags & (PropInstance.Flags.Created | PropInstance.Flags.Deleted)) == PropInstance.Flags.Created &&
                        props[p].Info != null)
                    {
                        float t, targetSqr;
                        if (props[p].RayCast(p, segment, out t, out targetSqr))
                            targets.Add(new PickTarget { What = PickTarget.Kind.Prop, Id = p, Distance = t * length });
                    }
                    p = props[p].m_nextGridProp;
                }
            }

            foreach (KeyValuePair<ushort, float> b in Buildings)
                targets.Add(new PickTarget { What = PickTarget.Kind.Building, Id = b.Key, Distance = b.Value });
            Buildings.Clear();
        }
    }
}
