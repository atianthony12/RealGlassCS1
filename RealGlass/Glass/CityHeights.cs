using System.Diagnostics;
using ColossalFramework;
using UnityEngine;

namespace RealGlass.Glass
{
    /// <summary>
    /// Fills the city height map (CityMap) from the game: every building near the view, placed as
    /// the renderer places it (Building.CalculateMeshPosition: its mesh position and rotation), with
    /// its prefab's own height grid (BuildingInfoGen.m_heights: 17 x 17 points over the mesh's
    /// bounds, the highest point of the mesh over each, filled by the game in
    /// BuildingInfoBase.CalculateGeneratedInfo), under the id its colour index names (a sub-building
    /// its root parent's: BuildingAI.GetObjectColorIndex -> Building.FindParentBuilding). A slice of
    /// the building buffer a frame; a new map when the view has moved a quarter of the map from its
    /// centre, or every few seconds while the glass is on (buildings go up, burn, level up). The
    /// old map stays on the GPU until the new one is complete. Procedural Objects' buildings go in
    /// last (ProceduralBuildings.AddToMap).
    /// </summary>
    internal sealed class CityHeights
    {
        /// <summary>Building slots looked at a frame: the whole buffer (49,152) in a dozen frames.</summary>
        private const int PerFrame = 4096;

        /// <summary>Seconds between two maps of the same place.</summary>
        private const float Refresh = 6f;

        /// <summary>Metres round the map a building's position may be and still reach into it.</summary>
        private const float Margin = 160f;

        private readonly CityMap _map = new CityMap();
        private readonly Stopwatch _watch = new Stopwatch();
        private bool _building;
        private int _next;
        private int _frames;
        private float _started = -1000f;
        private Vector2 _centre;
        private bool _logged;

        public CityMap Map
        {
            get { return _map; }
        }

        /// <summary>A main building standing in the map, as the facade cards need it.</summary>
        public struct Standing
        {
            public int Id;              // its id in the map (the colour index's root)
            public Vector3 Position;    // its mesh's position (CalculateMeshPosition)
            public float Heading;       // its mesh's rotation about y (degrees)
            public BuildingInfo Info;
        }

        private System.Collections.Generic.List<Standing> _building2 = new System.Collections.Generic.List<Standing>();
        private System.Collections.Generic.List<Standing> _live = new System.Collections.Generic.List<Standing>();

        /// <summary>The facade cards want the list of main buildings with each map.</summary>
        public bool CollectStanding;

        /// <summary>The live map's main buildings (filled while <see cref="CollectStanding"/>).</summary>
        public System.Collections.Generic.List<Standing> LiveStanding
        {
            get { return _live; }
        }

        /// <summary>Counts up with every finished map.</summary>
        public int MapVersion { get; private set; }

        /// <summary>The live map's list was collected from its start (a map begun before the cards asked has none).</summary>
        public bool LiveCollected { get; private set; }

        private bool _collecting;

        /// <summary>A fresh map at the next update (the cards want one with its list).</summary>
        public void Renew()
        {
            if (!_building)
                _started = -1000f;
        }

        /// <summary>Milliseconds the last complete map took, over how many frames.</summary>
        public float LastMs { get; private set; }

        public int LastFrames { get; private set; }

        /// <summary>One slice of the work; focus is the ground point the camera looks at.</summary>
        public void Update(Vector3 focus, float now)
        {
            if (!_building)
            {
                float quarter = CityMap.Size * CityMap.Texel * 0.25f;
                bool moved = Mathf.Abs(focus.x - _centre.x) > quarter || Mathf.Abs(focus.z - _centre.y) > quarter;
                if (_map.Live && !moved && now - _started < Refresh)
                    return;

                _map.Begin(focus.x, focus.z);
                _building2.Clear();
                _collecting = CollectStanding;
                _centre = new Vector2(focus.x, focus.z);
                _building = true;
                _next = 1;
                _frames = 0;
                _started = now;
                _watch.Reset();
            }

            _watch.Start();
            Building[] buffer = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            int end = Mathf.Min(_next + PerFrame, buffer.Length);
            for (int i = _next; i < end; i++)
            {
                // Standing, not burned down (a collapsed building is rubble, not its mesh).
                Building.Flags flags = buffer[i].m_flags;
                if ((flags & (Building.Flags.Created | Building.Flags.Deleted | Building.Flags.Collapsed)) != Building.Flags.Created)
                    continue;

                Vector3 at = buffer[i].m_position;
                if (!_map.Near(at.x, at.z, Margin))
                    continue;

                BuildingInfo info = buffer[i].Info;
                if (info == null)
                    continue;
                BuildingInfoGen gen = info.m_generatedInfo;
                if (gen == null)
                    continue;

                Vector3 position;
                Quaternion rotation;
                buffer[i].CalculateMeshPosition(out position, out rotation);
                int id = buffer[i].m_parentBuilding != 0 ? Building.FindParentBuilding((ushort)i) : i;
                _map.AddBuilding(position, rotation, gen.m_min, gen.m_max, gen.m_heights, id);

                // The facade cards (a trial): the main buildings that show their finished asset -- not a
                // sub-building (its root's card stands for the id; the ray then misses the root's box and
                // keeps the guess), not one under construction, abandoned or burned down (their look is
                // not the card's).
                if (CollectStanding && buffer[i].m_parentBuilding == 0 && id == i &&
                    (flags & (Building.Flags.Completed | Building.Flags.Abandoned | Building.Flags.BurnedDown)) == Building.Flags.Completed)
                {
                    _building2.Add(new Standing { Id = id, Position = position, Heading = rotation.eulerAngles.y, Info = info });
                }
            }

            _next = end;
            _frames++;
            _watch.Stop();

            if (_next < buffer.Length)
                return;

            // And Procedural Objects' buildings, which are in no building buffer.
            _watch.Start();
            ProceduralBuildings.AddToMap(_map, Margin);
            _map.Finish();
            _watch.Stop();
            _building = false;
            System.Collections.Generic.List<Standing> swap = _live;
            _live = _building2;
            _building2 = swap;
            _building2.Clear();
            LiveCollected = _collecting;
            MapVersion++;
            LastMs = (float)_watch.Elapsed.TotalMilliseconds;
            LastFrames = _frames;
            if (!_logged)
            {
                _logged = true;
                Log.Msg("glass: city heights " + CityMap.Size + "x" + CityMap.Size + " cells of " + CityMap.Texel.ToString("F0") +
                        " m round (" + _map.LiveCentre.x.ToString("F0") + ", " + _map.LiveCentre.y.ToString("F0") + "): " +
                        _map.LiveCount + " buildings, highest roof " + _map.LiveTop.ToString("F0") + " m; " +
                        LastMs.ToString("F1") + " ms over " + LastFrames + " frames");
            }
        }

        public void Destroy()
        {
            _map.Destroy();
        }
    }
}
