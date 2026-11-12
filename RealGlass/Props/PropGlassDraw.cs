using System;
using System.Collections.Generic;
using RealGlass.PerAsset;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealGlass.Props
{
    /// <summary>
    /// Glass or water on the props a player chose (1.1.9; PerAsset.PropRecord, the prop window): a
    /// second draw of the prop's own mesh over the game's, RealGlass/PropGlass, in two passes -- what
    /// is seen through the surface dimmed and tinted by its clarity and color, then what it reflects
    /// (the screen, the city, the ground, the sky, the sun) added.
    ///
    /// PropGlassPatches hands over each prop the game draws near (its PropInfo, where, its matrix:
    /// postfixes on PropInstance.RenderInstance, the game's own near test repeated here). Only a chosen
    /// prop goes further than one array read (3,000-22,000 prop draws a frame in a dense city). They
    /// are gathered per model (mesh + material) and drawn once the managers that draw props have drawn
    /// them, every copy of a model in ONE Graphics.DrawMeshInstanced, as the vehicles' glass is. Only
    /// the near tier. Procedural Objects' props, which it draws itself, are handed over by
    /// Glass.ProceduralBuildings.DrawProps (<see cref="NoteProcedural"/>).
    ///
    /// GLASS: over all of the prop, or -- Shininess needed above 0 -- over its parts at least that
    /// shiny (1 - _XYSMap.z). WATER: over the faces that look up, rippled by the water's ripple texture.
    ///
    /// A see-through prop (its material in the transparent queue: the community's trick of a vehicle
    /// shader on a prop) is drawn by the game AFTER the opaque world, so ours goes after it (a material
    /// of ours at the transparent queue + 1); an opaque prop's at the buildings' glass's place
    /// (Geometry+499), before the fog.
    ///
    /// Only ever called on the main thread. Every entry point is in a try: an exception is logged and
    /// the props' glass stays off for the rest of the city (<see cref="Fail"/>); the game draws on.
    /// </summary>
    public static class PropGlassDraw
    {
        /// <summary>Set by GlassController each frame: the glass is on, a prop is chosen, the materials exist.</summary>
        public static bool Active;

        /// <summary>A draw threw: off until the next city (<see cref="Reset"/>).</summary>
        public static bool Failed;

        /// <summary>RealGlass/PropGlass with instancing: one at its own queue (opaque props), one after the transparent queue (see-through props).</summary>
        public static Material Opaque, SeeThrough;

        // Per second, for the detail log line.
        public static int Props, Draws;

        /// <summary>The transparent queue (2500 and up): what the game draws after the opaque world.</summary>
        public const int TransparentQueue = 2500;

        /// <summary>Our see-through material's queue: after Unity's "Transparent" (3000), where the game's see-through props are.</summary>
        public const int SeeThroughQueue = 3001;

        private const int MaxInstances = 500;
        private const int FirstInstances = 8;
        private const int MaxModels = 4096;

        /// <summary>What a prop material gives its glass.</summary>
        private sealed class Source
        {
            public Texture Xys;
            public Vector4 St;
            public bool SeeThrough;

            // The material is a decal's (the game's decal shaders read their box from it): a box the
            // game projects, never a mesh ours goes over -- a batch with one is dropped (Flush).
            public bool Decal;
        }

        private sealed class Batch
        {
            public long Key;
            public Mesh Mesh;
            public Material Material;
            public Source Source;
            public int Layer;
            public int Index;
            public Matrix4x4[] Matrices = new Matrix4x4[FirstInstances];
            public int Count;
        }

        private static readonly Dictionary<Material, Source> Sources = new Dictionary<Material, Source>();
        private static readonly Dictionary<long, Batch> Batches = new Dictionary<long, Batch>();
        private static readonly List<Batch> Pending = new List<Batch>();
        private static readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();
        private static int _frame = -1;
        private static bool _instanced, _loggedMode, _loggedForget;

        // The chosen props, by PrefabInfo.m_prefabDataIndex: 0 none, else PropRecord's surface; and
        // what each draws with -- optics (x reflectivity head-on, y clarity, z strength, w shininess
        // needed), tint (rgb its color, linear; w 1, or 0: the Water tab's color, _RGPropWaterTab), look
        // (x roughness, y sun glint, z unused, w the reflection's gain: water's Strength) and
        // ripples (x strength, y size, z speed).
        private static byte[] _surface = new byte[0];
        private static Vector4[] _optics = new Vector4[0];
        private static Vector4[] _tint = new Vector4[0];
        private static Vector4[] _look = new Vector4[0];
        private static Vector4[] _ripples = new Vector4[0];
        private static int _tableVersion = -1, _tablePrefabs = -1;
        private static int _chosen, _chosenLoaded;
        private static string _loggedTable;

        private static int _idXys, _idSt, _idMode, _idOptics, _idTint, _idLook, _idRipples;
        private static int _idGameDecalSize;
        private static bool _ids;

        private static void Ids()
        {
            if (_ids)
                return;
            _idXys = Shader.PropertyToID("_XYSMap");
            _idSt = Shader.PropertyToID("_RGPropST");
            _idMode = Shader.PropertyToID("_RGPropMode");
            _idOptics = Shader.PropertyToID("_RGPropOptics");
            _idTint = Shader.PropertyToID("_RGPropTint");
            _idLook = Shader.PropertyToID("_RGPropLook");
            _idRipples = Shader.PropertyToID("_RGPropRipples");
            _idGameDecalSize = Shader.PropertyToID("_DecalSize");
            _ids = true;
        }

        /// <summary>A new city: every cached material and model forgotten.</summary>
        public static void Reset()
        {
            Active = false;
            Failed = false;
            Sources.Clear();
            Batches.Clear();
            Pending.Clear();
            _frame = -1;
            _loggedMode = _loggedForget = false;
            _tableVersion = _tablePrefabs = -1;
            _surface = new byte[0];
            _optics = new Vector4[0];
            _tint = new Vector4[0];
            _look = new Vector4[0];
            _ripples = new Vector4[0];
            _chosen = _chosenLoaded = 0;
            _loggedTable = null;
            Props = Draws = 0;
        }

        /// <summary>Props chosen in the file, and how many of them this city has loaded.</summary>
        public static string DescribeChosen()
        {
            return _chosen + " chosen, " + _chosenLoaded + " loaded";
        }

        public static bool AnyChosen
        {
            get { return _chosenLoaded > 0; }
        }

        /// <summary>The material is drawn after the opaque world (a see-through prop).</summary>
        public static bool IsSeeThrough(Material material)
        {
            return material != null && material.renderQueue >= TransparentQueue;
        }

        /// <summary>
        /// The chosen props' table again when the file changed (AssetFile.Version) or the city's props
        /// did. Main thread, every frame (GlassController): an int comparison when nothing changed.
        /// </summary>
        public static void RefreshTable()
        {
            int prefabs = PrefabCollection<PropInfo>.LoadedCount();
            if (_tableVersion == AssetFile.Version && _tablePrefabs == prefabs)
                return;
            _tableVersion = AssetFile.Version;
            _tablePrefabs = prefabs;

            int size = 0;
            var found = new List<KeyValuePair<PropInfo, PropRecord>>();
            string refused = null;
            _chosen = 0;
            foreach (PropRecord record in AssetFile.AllProps)
            {
                if (record == null || record.IsEmpty)
                    continue;
                _chosen++;
                PropInfo info = PrefabCollection<PropInfo>.FindLoaded(record.Name);
                if (info == null || info.m_prefabDataIndex < 0)
                    continue;
                if (!PropSelection.CanDraw(info))
                {
                    // In the file, but nothing ours draws over (a decal, a prop the game bends over
                    // the ground or the water): left as the game draws it, and said.
                    refused = (refused == null ? "" : refused + ", ") + "'" + record.Name + "' (" +
                              PropSelection.ShapeOf(info).ToString().ToLowerInvariant() + ")";
                    continue;
                }
                found.Add(new KeyValuePair<PropInfo, PropRecord>(info, record));
                size = Math.Max(size, info.m_prefabDataIndex + 1);
            }

            var surface = new byte[size];
            var optics = new Vector4[size];
            var tint = new Vector4[size];
            var looks = new Vector4[size];
            var ripples = new Vector4[size];
            int glass = 0, water = 0;
            foreach (KeyValuePair<PropInfo, PropRecord> pair in found)
            {
                int i = pair.Key.m_prefabDataIndex;
                PropRecord.Look look = pair.Value.Resolve();
                int kind = pair.Value.SurfaceOrGame;
                surface[i] = (byte)kind;
                if (kind == PropRecord.Water)
                    water++;
                else
                    glass++;
                optics[i] = new Vector4(look.Reflectivity, look.Clarity, look.Strength, look.Threshold);
                Color linear = ((Color)look.Colour).linear;
                tint[i] = new Vector4(linear.r, linear.g, linear.b, look.WaterTabColour ? 0f : 1f);
                looks[i] = new Vector4(Roughness(kind, look.Polish), look.Glint, 0f,
                                       kind == PropRecord.Water ? look.WaterStrength : 1f);
                ripples[i] = new Vector4(look.RippleStrength, look.RippleSize, look.RippleSpeed, 0f);
            }

            _surface = surface;
            _optics = optics;
            _tint = tint;
            _look = looks;
            _ripples = ripples;
            _chosenLoaded = found.Count;
            string line = _chosen + " prop" + (_chosen == 1 ? "" : "s") + " made glass or water, " + _chosenLoaded +
                          " of them in this city (" + glass + " glass, " + water + " water)" +
                          (refused != null ? "; left as the game draws them, not a shape ours goes over: " + refused : "");
            if (line != _loggedTable)
            {
                _loggedTable = line;
                Log.Msg("props: " + line);
            }
        }

        /// <summary>
        /// PropGlass's mode 3, the finish (a surface ON the mesh, nothing seen through it): drawn only
        /// for other mods (Api.Reflective) since the props' Reflective surface was taken out.
        /// </summary>
        public const int FinishMode = 3;

        /// <summary>Glass at 0.02 when clear (the buildings' panes), a finish the same polished, both to 0.5 at 0% polish; still water's 0.05.</summary>
        public static float Roughness(int surface, float polish)
        {
            if (surface == PropRecord.Water)
                return 0.05f;
            return Mathf.Lerp(0.5f, 0.02f, Mathf.Clamp01(polish));
        }

        private static readonly int IdWaterTab = Shader.PropertyToID("_RGPropWaterTab");

        /// <summary>
        /// The Water tab's color (linear, x its brightness row) for the props' water without a color of
        /// its own: set every frame the props draw (GlassController), so a change there shows at once.
        /// </summary>
        public static void SetWaterTabColour(Color32 colour, float brightness)
        {
            Color linear = ((Color)colour).linear * brightness;
            Shader.SetGlobalVector(IdWaterTab, new Vector4(linear.r, linear.g, linear.b, 1f));
        }

        /// <summary>
        /// A prop the game drew (PropInstance.RenderInstance's postfixes), at its matrix: noted when it
        /// is chosen and the game drew it NEAR -- its mesh at this matrix -- by the game's own test
        /// (initialised, a renderer, its layer in the camera's mask, within its LOD distance).
        /// </summary>
        public static void NoteNear(RenderManager.CameraInfo cameraInfo, PropInfo info, Vector3 position, Matrix4x4 matrix)
        {
            if (!Active || info == null)
                return;
            int index = info.m_prefabDataIndex;
            byte[] table = _surface;
            if ((uint)index >= (uint)table.Length || table[index] == 0)
                return;

            try
            {
                if (!info.m_prefabInitialized || !info.m_hasRenderer)
                    return;
                if (cameraInfo != null)
                {
                    if ((cameraInfo.m_layerMask & (1 << (info.m_prefabDataLayer & 31))) == 0)
                        return;
                    if (!cameraInfo.CheckRenderDistance(position, info.m_lodRenderDistance))
                        return;
                }
                Note(info.m_mesh, matrix, info.m_material, info.m_prefabDataLayer, index);
            }
            catch (Exception e)
            {
                Fail("noting a prop", e);
            }
        }

        /// <summary>
        /// A prop Intersection Marking Tool drew (its MarkingPropData.RenderInstance, after each of its
        /// DrawMesh calls: PropImtPatch): the near draw is the prop's own mesh -- its LOD draw is not --
        /// at the matrix IMT built (position, its angles, slope, tilt, scale). Noted for our glass when
        /// chosen, and for the picker while it is on (IMT's props are none of the city's: no raycast
        /// of the game's finds them).
        /// </summary>
        public static void NoteImt(Mesh mesh, Matrix4x4 matrix, Material material, int layer, PropInfo info)
        {
            if (info == null || mesh == null || mesh != info.m_mesh)
                return;
            try
            {
                if (OtherModsSeen.Recording)
                    OtherModsSeen.Add(info, matrix);
                if (!Active || !IsChosen(info))
                    return;
                Note(mesh, matrix, material, layer, info.m_prefabDataIndex);
            }
            catch (Exception e)
            {
                Fail("noting a prop of Intersection Marking Tool", e);
            }
        }

        /// <summary>
        /// The shapes the props' glass draws over (PropSelection.Shape): a prop's mesh where it stands,
        /// only. Decals (a box the game projects: drawn as a mesh it would show the box) and the props
        /// the game bends over the ground or the water can be picked, and are told they take none.
        /// </summary>
        public static bool CanDrawShape(PropSelection.Shape shape)
        {
            return shape == PropSelection.Shape.Mesh;
        }

        /// <summary>The prop's asset was made glass or water and is in this city's table.</summary>
        public static bool IsChosen(PropInfo info)
        {
            if (info == null)
                return false;
            int index = info.m_prefabDataIndex;
            byte[] table = _surface;
            return (uint)index < (uint)table.Length && table[index] != 0;
        }

        /// <summary>
        /// A prop of Procedural Objects (Glass.ProceduralBuildings.DrawProps): its own mesh, at the
        /// matrix it is drawn with, read through its own copy of the asset's material (the same
        /// specular map, its own queue); on layer 0, as Procedural Objects draws it. Every distance
        /// it is drawn at -- it has one mesh, no LOD.
        /// </summary>
        public static void NoteProcedural(Mesh mesh, Matrix4x4 matrix, Material material, PropInfo info)
        {
            if (!Active || !IsChosen(info))
                return;

            try
            {
                Note(mesh, matrix, material, 0, info.m_prefabDataIndex);
            }
            catch (Exception e)
            {
                Fail("noting a prop of Procedural Objects", e);
            }
        }

        private static void Note(Mesh mesh, Matrix4x4 matrix, Material material, int layer, int index)
        {
            if (mesh == null || material == null || Opaque == null || SeeThrough == null)
                return;
            Ids();
            int frame = Time.frameCount;
            if (frame != _frame)
            {
                for (int i = 0; i < Pending.Count; i++)
                    Pending[i].Count = 0;
                Pending.Clear();
                _frame = frame;
            }

            long key = ((long)mesh.GetInstanceID() << 32) ^ (uint)material.GetInstanceID();
            Batch b;
            if (!Batches.TryGetValue(key, out b) || b.Mesh != mesh || b.Material != material)
            {
                if (Batches.Count >= MaxModels || Sources.Count >= MaxModels)
                    Forget();
                b = new Batch { Key = key, Mesh = mesh, Material = material, Source = SourceFor(material) };
                Batches[key] = b;
            }
            if (b.Count == 0)
            {
                b.Layer = layer;
                b.Index = index;
                Pending.Add(b);
            }
            else if (b.Count == MaxInstances)
            {
                DrawBatch(b);
                b.Index = index;
            }
            else if (b.Count == b.Matrices.Length)
            {
                var grown = new Matrix4x4[Math.Min(b.Matrices.Length * 2, MaxInstances)];
                Array.Copy(b.Matrices, grown, b.Count);
                b.Matrices = grown;
            }

            b.Matrices[b.Count] = matrix;
            b.Count++;
            Props++;
        }

        private static Source SourceFor(Material material)
        {
            Source s;
            if (Sources.TryGetValue(material, out s))
                return s;

            s = new Source { SeeThrough = IsSeeThrough(material) };
            s.Xys = material.HasProperty(_idXys) ? material.GetTexture(_idXys) : null;
            bool main = material.HasProperty("_MainTex");
            Vector2 scale = main ? material.mainTextureScale : Vector2.one;
            Vector2 offset = main ? material.mainTextureOffset : Vector2.zero;
            s.St = new Vector4(scale.x, scale.y, offset.x, offset.y);

            // A decal's material (the game's decal shaders read their box from it), whatever the prop
            // says of itself: never drawn over.
            s.Decal = material.HasProperty(_idGameDecalSize);
            Sources[material] = s;
            return s;
        }

        private static void Forget()
        {
            Sources.Clear();
            Batches.Clear();
            for (int i = 0; i < Pending.Count; i++)
            {
                Batches[Pending[i].Key] = Pending[i];
                Sources[Pending[i].Material] = Pending[i].Source;
            }
            if (_loggedForget)
                return;
            _loggedForget = true;
            Log.Msg("props: over " + MaxModels + " prop models seen in this city: the ones not in view are forgotten and read again when they come back");
        }

        /// <summary>Every model gathered so far this frame drawn (after each manager that draws props).</summary>
        public static void Flush()
        {
            if (Pending.Count == 0)
                return;
            try
            {
                if (Active)
                {
                    for (int i = 0; i < Pending.Count; i++)
                    {
                        if (Pending[i].Count > 0)
                            DrawBatch(Pending[i]);
                    }
                }
                for (int i = 0; i < Pending.Count; i++)
                    Pending[i].Count = 0;
                Pending.Clear();
            }
            catch (Exception e)
            {
                Pending.Clear();
                Fail("drawing the props' glass", e);
            }
        }

        private static void DrawBatch(Batch b)
        {
            if (!_loggedMode)
            {
                _loggedMode = true;
                _instanced = SystemInfo.supportsInstancing && Opaque.enableInstancing && SeeThrough.enableInstancing;
                Log.Msg("props: glass and water drawn " + (_instanced ? "with GPU instancing, one draw per model in view"
                    : "one draw per prop (no GPU instancing here)"));
            }

            int i = b.Index;
            if ((uint)i >= (uint)_surface.Length || _surface[i] == 0)
            {
                b.Count = 0;   // unchosen since it was noted
                return;
            }

            Source s = b.Source;
            if (s.Decal)
            {
                b.Count = 0;   // a decal's material: its mesh is a box the game projects, none of ours
                return;
            }

            Vector4 optics = _optics[i];
            if (s.Xys == null)
                optics.w = 0f;   // no map: nothing to tell shiny from dull, all of it
            Block.Clear();
            if (s.Xys != null)
                Block.SetTexture(_idXys, s.Xys);
            Block.SetVector(_idSt, s.St);
            Block.SetFloat(_idMode, _surface[i]);
            Block.SetVector(_idOptics, optics);
            Block.SetVector(_idTint, _tint[i]);
            Block.SetVector(_idLook, _look[i]);
            Block.SetVector(_idRipples, _ripples[i]);
            Material material = s.SeeThrough ? SeeThrough : Opaque;
            if (_instanced)
            {
                Graphics.DrawMeshInstanced(b.Mesh, 0, material, b.Matrices, b.Count, Block, ShadowCastingMode.Off, false, b.Layer, null);
                Draws++;
            }
            else
            {
                for (int k = 0; k < b.Count; k++)
                {
                    Graphics.DrawMesh(b.Mesh, b.Matrices[k], material, b.Layer, null, 0, Block, false, false);
                    Draws++;
                }
            }
            b.Count = 0;
        }

        public static void Fail(string where, Exception e)
        {
            Active = false;
            if (Failed)
                return;

            Failed = true;
            ErrorNotice.Report(ErrorNotice.Props);
            try
            {
                Log.Error("props: " + where + " failed; the props' glass and water are off until the next city is loaded", e);
            }
            catch
            {
                // the log itself failed: nothing more to do
            }
        }
    }
}
