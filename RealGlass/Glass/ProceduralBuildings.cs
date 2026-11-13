using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;

namespace RealGlass.Glass
{
    /// <summary>
    /// The glass on Procedural Objects' buildings (Workshop 1094334744). A building converted there
    /// keeps a COPY of its asset's material (Object.Instantiate(info.m_material): the specular map, our
    /// glass mask, with it) and of its main mesh, and Procedural Objects draws it itself every frame
    /// (ProceduralObjectsLogic.Update: Graphics.DrawMesh on layer 0, no property block, the one mesh
    /// at every distance) -- by none of the game's building paths our patches follow, so it kept the
    /// game's own glass beside ours. Each is drawn again here, with the glass, where and when
    /// Procedural Objects draws it (<see cref="ProceduralAccess"/>): its asset's own glass and rooms,
    /// through a texel of _RGAssets of its own past the buildings' (an id no building has, under
    /// 65536 for the city map); in the city map; and found by the building picker by its shape.
    ///
    /// Its PROPS too (2.0.0): a prop converted there is drawn the same way (its own mesh and a copy of
    /// its asset's material), so the props' glass and water (Props.PropGlassDraw, by its asset's
    /// choice in RealGlassAssets.xml) is drawn over each one whose asset was made glass or water, and
    /// the picker finds them by their shape as it finds the buildings.
    /// </summary>
    /// <remarks>
    /// Nothing of Procedural Objects is patched or changed: without it nothing runs; not as expected,
    /// one line and its buildings keep the game's glass. Not drawn: a building given a texture of its
    /// own or a text -- the glass mask is the asset's painting, which the new texture is not (most are
    /// converted only to be reshaped: taller, slimmer; the author's call). A prop with a texture of its
    /// own takes its asset's choice all the same (the author's call: over all of a prop, the painting
    /// does not matter). Main thread only.
    /// </remarks>
    public static class ProceduralBuildings
    {
        /// <summary>One object of Procedural Objects: a building (<see cref="Entry"/>) or a prop (<see cref="PropEntry"/>).</summary>
        public abstract class Item
        {
            public readonly object Object;

            internal int Stamp;

            protected Item(object o)
            {
                Object = o;
            }
        }

        /// <summary>One prop of Procedural Objects: any with its asset can be picked.</summary>
        public sealed class PropEntry : Item
        {
            public PropInfo Info;

            /// <summary>How its asset is drawn: a mesh, a projected decal, a fence or floating prop.</summary>
            public PerAsset.PropSelection.Shape Shape;

            /// <summary>One of its ground surfaces (asphalt, gravel, cliff grass): a prop of its own.</summary>
            public bool Surface;

            /// <summary>The props' glass draws over it when its asset is made glass or water (PropSelection.CanDraw).</summary>
            public bool Glass;

            public PropEntry(object o) : base(o)
            {
            }
        }

        /// <summary>One building of Procedural Objects.</summary>
        public sealed class Entry : Item
        {
            /// <summary>Its texel of _RGAssets and its id in the city map; -1 when there was no place left.</summary>
            public readonly int Index;

            /// <summary>Its _ObjectIndex: the texel at Index, as the game's colour index names a building's.</summary>
            public readonly Vector4 ColourIndex;

            public BuildingInfo Info;

            /// <summary>Drawn with the glass: the asset's own texture on a building shader the glass follows.</summary>
            public bool Glass;

            /// <summary>Its material's _BuildingState: what the game's shader reads for it, with no block.</summary>
            public Vector4 State;

            public Entry(object o, int index) : base(o)
            {
                Index = index;
                int w = RenderManager.OBJECT_COLORMAP_WIDTH, h = RenderManager.OBJECT_COLORMAP_HEIGHT;
                ColourIndex = index >= 0 ? new Vector4((index % w + 0.5f) / w, (index / w + 0.5f) / h, 1f, 0f) : Vector4.zero;
            }
        }

        /// <summary>Seconds between two reads of the whole list while its length stays the same.</summary>
        private const float ScanEvery = 2f;

        /// <summary>The building shader's own default _BuildingState (resources.assets, 2026-10-21): nothing squashed or stretched.</summary>
        private static readonly Vector4 DefaultState = new Vector4(0f, 1000f, 0f, 1f);

        private static readonly List<Entry> List = new List<Entry>();
        private static readonly List<PropEntry> PropList = new List<PropEntry>();
        private static readonly Dictionary<object, Item> ByObject = new Dictionary<object, Item>(new SameObject());
        private static readonly List<object> Gone = new List<object>();
        private static readonly List<Candidate> Candidates = new List<Candidate>();
        private static readonly Comparison<Candidate> ByEnter = (a, b) => a.Enter.CompareTo(b.Enter);
        private static readonly Dictionary<Mesh, Shape> Shapes = new Dictionary<Mesh, Shape>();

        private static ProceduralAccess _access;
        private static bool _looked;
        private static string _problem;
        private static bool _failed, _pickFailed;
        private static int _frame = -1;
        private static int _seenCount = -1;
        private static float _nextScan;
        private static int _stamp;
        private static int _first, _places;
        private static bool[] _taken;
        private static bool _loggedPresence, _loggedFirst;
        private static string _loggedSummary = "";
        private static int _stateId = -1;

        // The last pick, kept while neither the ray nor the list moved.
        private static Ray _lastRay;
        private static int _lastStamp = -1;
        private static Item _lastHit;
        private static float _lastDistance;

        /// <summary>The buildings found at the last read of Procedural Objects' list (the same objects stay the same entries).</summary>
        public static List<Entry> Entries
        {
            get { return List; }
        }

        /// <summary>The props found at the last read of Procedural Objects' list.</summary>
        public static List<PropEntry> PropEntries
        {
            get { return PropList; }
        }

        /// <summary>
        /// Keeps the list current: read whole when its length changes (an object made, deleted,
        /// pasted, undone) and every couple of seconds (a texture or a text given). Once a frame at
        /// most; called by the glass each frame and by the building picker while it is on.
        /// </summary>
        public static void Refresh()
        {
            if (_failed || Time.frameCount == _frame)
                return;

            _frame = Time.frameCount;
            try
            {
                if (!Find())
                    return;

                object logic = _access.Logic();
                IList objects = logic != null ? _access.Objects(logic) : null;
                if (objects == null)
                {
                    if (List.Count > 0 || PropList.Count > 0)
                        Forget();
                    return;
                }

                int count = objects.Count;
                float now = Time.realtimeSinceStartup;
                if (count == _seenCount && now < _nextScan)
                    return;

                _seenCount = count;
                _nextScan = now + ScanEvery;
                Scan(objects);
            }
            catch (Exception e)
            {
                Fail("reading its objects", e);
            }
        }

        /// <summary>Procedural Objects among the loaded assemblies, read the first time (one line either way).</summary>
        private static bool Find()
        {
            if (!_looked)
            {
                _looked = true;
                Type logic = ProceduralAccess.FindLogicType();
                if (logic != null)
                    _access = ProceduralAccess.Create(logic, out _problem);
                else
                    _problem = "not installed";
            }

            if (!_loggedPresence)
            {
                _loggedPresence = true;
                if (_access != null)
                {
                    Log.Msg("procedural objects: " + (_access.Version ?? "a version without a number") +
                            " is here: its buildings get the glass" +
                            (_access.PropAsset != null ? ", its props their asset's glass or water" : "") +
                            ", read from it without changing it");
                    if (_access.PropAsset == null)
                        Log.Warn("procedural objects: its props are not as this mod knows them (" + _access.PropsProblem +
                                 "): they keep the game's look");
                }
                else if (_problem == "not installed")
                {
                    Log.Msg("procedural objects: not installed");
                }
                else
                {
                    Log.Warn("procedural objects: installed, but not as this mod knows it (" + _problem +
                             "): its buildings keep the game's glass");
                }
            }

            return _access != null;
        }

        private static void Scan(IList objects)
        {
            Places();
            if (_stateId < 0)
                _stateId = Shader.PropertyToID("_BuildingState");

            _stamp++;
            List.Clear();
            PropList.Clear();
            int all = objects.Count, glass = 0, own = 0, otherShader = 0, noAsset = 0, noPlace = 0;
            int propsAble = 0, propsOwn = 0, propsDecals = 0, propsSurfaces = 0, propsBent = 0, propsNoAsset = 0;
            for (int i = 0; i < all; i++)
            {
                object o = objects[i];
                if (o == null)
                    continue;

                string kind = _access.Kind(o);
                if (kind == ProceduralAccess.PropKind)
                {
                    if (_access.PropAsset == null)
                        continue;
                    bool ownTexture;
                    PropEntry p = ScanProp(o, out ownTexture);
                    if (p.Info == null)
                        propsNoAsset++;
                    else if (p.Surface)
                        propsSurfaces++;
                    else if (p.Shape == PerAsset.PropSelection.Shape.Decal)
                        propsDecals++;
                    else if (p.Shape == PerAsset.PropSelection.Shape.Fence || p.Shape == PerAsset.PropSelection.Shape.Floating)
                        propsBent++;
                    if (ownTexture)
                        propsOwn++;
                    if (p.Glass)
                        propsAble++;
                    continue;
                }

                if (kind != ProceduralAccess.BuildingKind)
                    continue;

                Item known;
                Entry e = ByObject.TryGetValue(o, out known) ? known as Entry : null;
                if (e == null)
                {
                    Drop(o);  // known as another kind before (never seen so far): a new entry for it
                    e = new Entry(o, TakePlace(_access.Id(o)));
                    ByObject[o] = e;
                }

                e.Stamp = _stamp;
                e.Info = _access.Asset(o);
                Material material = _access.Material(o);
                e.State = material != null && material.HasProperty(_stateId) ? material.GetVector(_stateId) : DefaultState;
                e.Glass = false;
                if (e.Info == null || e.Info.m_material == null || material == null)
                    noAsset++;
                else if (!GlassDraw.IsBuildingShader(material.shader))
                    otherShader++;
                else if (material.mainTexture != e.Info.m_material.mainTexture)
                    own++;
                else if (e.Index < 0)
                    noPlace++;
                else
                {
                    e.Glass = true;
                    glass++;
                }

                List.Add(e);
            }

            Gone.Clear();
            foreach (KeyValuePair<object, Item> kv in ByObject)
            {
                if (kv.Value.Stamp != _stamp)
                    Gone.Add(kv.Key);
            }
            for (int i = 0; i < Gone.Count; i++)
                Drop(Gone[i]);
            Gone.Clear();

            string summary = List.Count + " buildings among its " + all + " objects, " + glass + " with the glass" +
                             (own > 0 ? "; " + own + " with a texture or text of their own (no glass)" : "") +
                             (otherShader > 0 ? "; " + otherShader + " on a shader the glass does not follow" : "") +
                             (noAsset > 0 ? "; " + noAsset + " without their asset" : "") +
                             (noPlace > 0 ? "; " + noPlace + " past the " + _places + " places for them (no glass)" : "") +
                             (PropList.Count > 0 ? "; " + PropList.Count + " props (" + propsDecals + " decals, " + propsSurfaces + " surfaces, " +
                                 propsBent + " fence or floating, " + propsOwn + " with a texture or text of their own" +
                                 (propsNoAsset > 0 ? ", " + propsNoAsset + " without their asset" : "") + "), " +
                                 propsAble + " drawn over when their asset is made glass or water" : "");
            if (summary == _loggedSummary)
                return;

            _loggedSummary = summary;
            if (!_loggedFirst)
            {
                _loggedFirst = true;
                Log.Msg("procedural objects: " + summary);
            }
            else if (Log.Detailed)
            {
                Log.Detail("procedural objects: " + summary);
            }
        }

        /// <summary>
        /// One of its props, read: its asset, how its asset is drawn (PropSelection.Shape: its decals
        /// are boxes projected as the game's are, its surfaces props of one shape or another), whether
        /// one of its ground surfaces, whether its texture is its own or a text (ownTexture), and
        /// whether the props' glass draws over it now. Every one with its asset can be picked; one with
        /// a texture of its own takes its asset's choice too (the author's call for the props: a
        /// surface over all of the prop does not need the asset's painting).
        /// </summary>
        private static PropEntry ScanProp(object o, out bool ownTexture)
        {
            Item known;
            PropEntry p = ByObject.TryGetValue(o, out known) ? known as PropEntry : null;
            if (p == null)
            {
                Drop(o);
                p = new PropEntry(o);
                ByObject[o] = p;
            }

            p.Stamp = _stamp;
            p.Info = _access.PropAsset(o);
            p.Shape = PerAsset.PropSelection.ShapeOf(p.Info);
            p.Surface = _access.Surface(o);
            Material material = _access.Material(o);
            ownTexture = p.Info != null && material != null && p.Info.m_material != null && material.mainTexture != p.Info.m_material.mainTexture;
            p.Glass = material != null && PerAsset.PropSelection.CanDraw(p.Info);

            PropList.Add(p);
            return p;
        }

        /// <summary>An object no longer listed (or listed as another kind): forgotten, a building's place freed.</summary>
        private static void Drop(object o)
        {
            Item known;
            if (!ByObject.TryGetValue(o, out known))
                return;
            Entry e = known as Entry;
            if (e != null)
                FreePlace(e.Index);
            ByObject.Remove(o);
        }

        /// <summary>
        /// Every prop Procedural Objects draws this frame whose asset was made glass or water, handed
        /// to the props' glass (Props.PropGlassDraw) at the place Procedural Objects draws it, and
        /// drawn. After PropGlassDraw.Active is set for the frame.
        /// </summary>
        public static void DrawProps()
        {
            if (_failed || PropList.Count == 0 || !Props.PropGlassDraw.Active)
                return;

            try
            {
                object options = _access.Options();
                if (options == null)
                    return;

                bool night = Singleton<SimulationManager>.instance.m_isNightTime;
                int noted = 0;
                for (int i = 0; i < PropList.Count; i++)
                {
                    PropEntry p = PropList[i];
                    if (!p.Glass || !Props.PropGlassDraw.IsChosen(p.Info) || !_access.Drawn(p.Object, options, night))
                        continue;

                    Mesh mesh = _access.Mesh(p.Object);
                    Material material = _access.Material(p.Object);
                    if (mesh == null || material == null)
                        continue;

                    // As Procedural Objects draws it: Graphics.DrawMesh(m_mesh, m_position, m_rotation, ...),
                    // its scale in the mesh itself; layer 0.
                    Matrix4x4 matrix = Matrix4x4.TRS(_access.Position(p.Object), _access.Rotation(p.Object), Vector3.one);
                    Props.PropGlassDraw.NoteProcedural(mesh, matrix, material, p.Info);
                    noted++;
                }

                if (noted > 0)
                    Props.PropGlassDraw.Flush();
            }
            catch (Exception ex)
            {
                Fail("drawing its props' glass", ex);
            }
        }

        /// <summary>
        /// Every building with the glass that Procedural Objects draws this frame, drawn again with the
        /// glass (GlassDraw.DrawProcedural). After GlassDraw.Active is set for the frame.
        /// </summary>
        public static void Draw()
        {
            if (_failed || List.Count == 0 || !GlassDraw.Active)
                return;

            try
            {
                object options = _access.Options();
                if (options == null)
                    return;

                bool night = Singleton<SimulationManager>.instance.m_isNightTime;
                TerrainManager terrain = Singleton<TerrainManager>.instance;
                for (int i = 0; i < List.Count; i++)
                {
                    Entry e = List[i];
                    if (!e.Glass || !_access.Drawn(e.Object, options, night))
                        continue;

                    Mesh mesh = _access.Mesh(e.Object);
                    if (mesh == null)
                        continue;

                    Vector3 at = _access.Position(e.Object);
                    Matrix4x4 matrix = Matrix4x4.TRS(at, _access.Rotation(e.Object), Vector3.one);
                    GlassDraw.DrawProcedural(mesh, matrix, e.Info.m_material, e.State, e.ColourIndex, terrain.SampleDetailHeight(at));
                }
            }
            catch (Exception ex)
            {
                Fail("drawing its buildings", ex);
            }
        }

        /// <summary>Where Procedural Objects draws this building now: its mesh's origin and turn (RoomFitMap). False when that cannot be read.</summary>
        public static bool Place(Entry e, out Vector3 at, out Quaternion rotation)
        {
            at = Vector3.zero;
            rotation = Quaternion.identity;
            if (_failed || _access == null || e == null || e.Object == null)
                return false;
            try
            {
                at = _access.Position(e.Object);
                rotation = _access.Rotation(e.Object);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Every building Procedural Objects draws now, into the city map being built (CityHeights,
        /// before it finishes a map): with its asset's height grid while its mesh keeps the asset's
        /// size, else as its mesh's box. margin: metres round the map a building may stand and still
        /// reach in, besides its own size.
        /// </summary>
        public static void AddToMap(CityMap map, float margin)
        {
            if (_failed || List.Count == 0)
                return;

            try
            {
                object options = _access.Options();
                if (options == null)
                    return;

                bool night = Singleton<SimulationManager>.instance.m_isNightTime;
                for (int i = 0; i < List.Count; i++)
                {
                    Entry e = List[i];
                    if (e.Index < 0 || e.Info == null || !_access.Drawn(e.Object, options, night))
                        continue;

                    Mesh mesh = _access.Mesh(e.Object);
                    if (mesh == null)
                        continue;

                    Vector3 at = _access.Position(e.Object);
                    Bounds bounds = mesh.bounds;
                    Vector3 min = bounds.min, max = bounds.max;
                    float reach = Mathf.Sqrt(Mathf.Max(min.x * min.x, max.x * max.x) + Mathf.Max(min.z * min.z, max.z * max.z));
                    if (!map.Near(at.x, at.z, margin + reach))
                        continue;

                    BuildingInfoGen gen = e.Info.m_generatedInfo;
                    float[] heights = gen != null && Close(min, gen.m_min) && Close(max, gen.m_max) ? gen.m_heights : null;
                    map.AddBuilding(at, _access.Rotation(e.Object), min, max, heights, e.Index);
                }
            }
            catch (Exception ex)
            {
                Fail("putting its buildings in the city map", ex);
            }
        }

        private static bool Close(Vector3 a, Vector3 b)
        {
            return Mathf.Abs(a.x - b.x) < 0.05f && Mathf.Abs(a.y - b.y) < 0.05f && Mathf.Abs(a.z - b.z) < 0.05f;
        }

        /// <summary>
        /// The nearest of its objects under the ray that our glass can go over -- a building with the
        /// glass (<see cref="Entry"/>) or any prop with its asset, decals and surfaces too
        /// (<see cref="PropEntry"/>) -- by its shape (its box, then its triangles); null when none
        /// within length. distance: along the ray, metres.
        /// </summary>
        public static Item Raycast(Ray ray, float length, out float distance)
        {
            distance = length;
            if (_failed || _pickFailed || (List.Count == 0 && PropList.Count == 0))
                return null;

            if (_lastStamp == _stamp && ray.origin == _lastRay.origin && ray.direction == _lastRay.direction)
            {
                distance = _lastHit != null ? _lastDistance : length;
                return _lastHit;
            }

            Item best = null;
            try
            {
                object options = _access.Options();
                if (options == null)
                    return null;

                bool night = Singleton<SimulationManager>.instance.m_isNightTime;
                Candidates.Clear();
                for (int i = 0; i < List.Count; i++)
                {
                    if (List[i].Glass)
                        AddCandidate(List[i], ray, length, options, night);
                }
                for (int i = 0; i < PropList.Count; i++)
                {
                    if (PropList[i].Info != null)
                        AddCandidate(PropList[i], ray, length, options, night);   // every kind: its decal's box, its surface's plate
                }

                Candidates.Sort(ByEnter);
                for (int i = 0; i < Candidates.Count; i++)
                {
                    Candidate c = Candidates[i];
                    if (c.Enter >= distance)
                        break;  // every box further on starts behind what was already met

                    Shape shape = ShapeOf(c.Mesh);
                    float t = ProceduralPick.RayTriangles(c.Origin, c.Direction, shape.Vertices, shape.Triangles, distance);
                    if (t >= 0f)
                    {
                        distance = t;
                        best = c.Entry;
                    }
                }
                Candidates.Clear();
            }
            catch (Exception ex)
            {
                // The picker goes on with the city's own buildings and props; the glass is not touched.
                _pickFailed = true;
                Log.Error("procedural objects: picking its objects failed; the picker finds the game's buildings and props only", ex);
                return null;
            }

            _lastRay = ray;
            _lastStamp = _stamp;
            _lastHit = best;
            _lastDistance = distance;
            return best;
        }

        /// <summary>
        /// Every one of its objects under the ray (the picker's Ctrl+Space, 2.0.0): what
        /// <see cref="Raycast"/> considers, each with where the ray meets its triangles, added to
        /// <paramref name="hits"/> (unsorted). Nothing added when Procedural Objects is not here.
        /// </summary>
        public static void RaycastAll(Ray ray, float length, List<KeyValuePair<Item, float>> hits)
        {
            if (_failed || _pickFailed || (List.Count == 0 && PropList.Count == 0))
                return;

            try
            {
                object options = _access.Options();
                if (options == null)
                    return;

                bool night = Singleton<SimulationManager>.instance.m_isNightTime;
                Candidates.Clear();
                for (int i = 0; i < List.Count; i++)
                {
                    if (List[i].Glass)
                        AddCandidate(List[i], ray, length, options, night);
                }
                for (int i = 0; i < PropList.Count; i++)
                {
                    if (PropList[i].Info != null)
                        AddCandidate(PropList[i], ray, length, options, night);
                }

                for (int i = 0; i < Candidates.Count; i++)
                {
                    Candidate c = Candidates[i];
                    Shape shape = ShapeOf(c.Mesh);
                    float t = ProceduralPick.RayTriangles(c.Origin, c.Direction, shape.Vertices, shape.Triangles, length);
                    if (t >= 0f)
                        hits.Add(new KeyValuePair<Item, float>(c.Entry, t));
                }
                Candidates.Clear();
            }
            catch (Exception ex)
            {
                _pickFailed = true;
                Log.Error("procedural objects: picking its objects failed; the picker finds the game's buildings and props only", ex);
            }
        }

        /// <summary>The object as a candidate for the ray when Procedural Objects draws it and the ray meets its box.</summary>
        private static void AddCandidate(Item item, Ray ray, float length, object options, bool night)
        {
            if (!_access.Drawn(item.Object, options, night))
                return;

            Mesh mesh = _access.Mesh(item.Object);
            if (mesh == null)
                return;

            // The ray in the mesh's own space (a rotation keeps the metres).
            Quaternion back = Quaternion.Inverse(_access.Rotation(item.Object));
            Vector3 o = back * (ray.origin - _access.Position(item.Object));
            Vector3 d = back * ray.direction;
            Bounds bounds = mesh.bounds;
            float enter = ProceduralPick.RayBox(o, d, bounds.min, bounds.max);
            if (enter >= 0f && enter < length)
                Candidates.Add(new Candidate { Entry = item, Mesh = mesh, Origin = o, Direction = d, Enter = enter });
        }

        /// <summary>The footprint of an object, as the game outlines a building (OverlayEffect.DrawQuad), and its height span.</summary>
        public static bool Footprint(Item e, out Quad3 quad, out float minY, out float maxY)
        {
            quad = default(Quad3);
            minY = maxY = 0f;
            Mesh mesh = e != null && _access != null ? _access.Mesh(e.Object) : null;
            if (mesh == null)
                return false;

            Vector3 at = _access.Position(e.Object);
            Quaternion turn = _access.Rotation(e.Object);
            Bounds b = mesh.bounds;
            quad.a = at + turn * new Vector3(b.min.x, b.min.y, b.min.z);
            quad.b = at + turn * new Vector3(b.min.x, b.min.y, b.max.z);
            quad.c = at + turn * new Vector3(b.max.x, b.min.y, b.max.z);
            quad.d = at + turn * new Vector3(b.max.x, b.min.y, b.min.z);
            minY = at.y + b.min.y;
            maxY = at.y + b.max.y;
            return true;
        }

        /// <summary>Procedural Objects' number for the object (for the log).</summary>
        public static int IdOf(Item e)
        {
            return e != null && _access != null ? _access.Id(e.Object) : -1;
        }

        /// <summary>Its buildings of this asset that have the glass (the building window's count).</summary>
        public static int CountCopies(BuildingInfo info)
        {
            int count = 0;
            for (int i = 0; i < List.Count; i++)
            {
                if (List[i].Glass && List[i].Info == info)
                    count++;
            }
            return count;
        }

        /// <summary>The meshes' triangles read for picking: let go when the picker is.</summary>
        public static void ForgetShapes()
        {
            Shapes.Clear();
            _lastStamp = -1;
            _lastHit = null;
        }

        private static Shape ShapeOf(Mesh mesh)
        {
            Shape shape;
            if (Shapes.TryGetValue(mesh, out shape))
                return shape;

            if (Shapes.Count >= 32)
                Shapes.Clear();
            shape = new Shape { Vertices = mesh.vertices, Triangles = mesh.triangles };
            Shapes[mesh] = shape;
            return shape;
        }

        /// <summary>The places past the buildings' ids: from the building buffer's end to 65535 (the city map's ids are 16 bits).</summary>
        private static void Places()
        {
            if (_taken != null)
                return;

            _first = Singleton<BuildingManager>.instance.m_buildings.m_buffer.Length;
            int end = Mathf.Min(65536, RenderManager.OBJECT_COLORMAP_WIDTH * RenderManager.OBJECT_COLORMAP_HEIGHT);
            _places = Mathf.Max(0, end - _first);
            _taken = new bool[_places];
        }

        /// <summary>A place for an object: its own number's where free (the same place every time the city loads), else the next free.</summary>
        private static int TakePlace(int id)
        {
            if (_places <= 0)
                return -1;

            int start = (id % _places + _places) % _places;
            for (int k = 0; k < _places; k++)
            {
                int place = (start + k) % _places;
                if (!_taken[place])
                {
                    _taken[place] = true;
                    return _first + place;
                }
            }
            return -1;
        }

        private static void FreePlace(int index)
        {
            int place = index - _first;
            if (_taken != null && place >= 0 && place < _places)
                _taken[place] = false;
        }

        private static void Forget()
        {
            List.Clear();
            PropList.Clear();
            ByObject.Clear();
            if (_taken != null)
                Array.Clear(_taken, 0, _taken.Length);
            _seenCount = -1;
            ForgetShapes();
        }

        private static void Fail(string what, Exception e)
        {
            _failed = true;
            Forget();
            Log.Error("procedural objects: " + what + " failed; its buildings and props keep the game's look in this city", e);
        }

        /// <summary>The city is going: nothing kept of its objects (what was read of the mod stays).</summary>
        public static void Reset()
        {
            Forget();
            _taken = null;
            _failed = _pickFailed = false;
            _frame = -1;
            _loggedPresence = _loggedFirst = false;
            _loggedSummary = "";
        }

        private struct Candidate
        {
            public Item Entry;
            public Mesh Mesh;
            public Vector3 Origin, Direction;
            public float Enter;
        }

        private sealed class Shape
        {
            public Vector3[] Vertices;
            public int[] Triangles;
        }

        /// <summary>Procedural Objects' objects by reference: whatever Equals they may have.</summary>
        private sealed class SameObject : IEqualityComparer<object>
        {
            bool IEqualityComparer<object>.Equals(object a, object b)
            {
                return ReferenceEquals(a, b);
            }

            public int GetHashCode(object o)
            {
                return RuntimeHelpers.GetHashCode(o);
            }
        }
    }
}
