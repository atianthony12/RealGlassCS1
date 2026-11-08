using System.Collections.Generic;
using UnityEngine;

namespace RealGlass.Glass
{
    /// <summary>
    /// The second draw of every building, right after the game's own (GlassPatches calls in from
    /// the game's three building paths), with the glass material and the same mesh, matrix and
    /// data. Only ever called on the main thread, from inside the game's rendering.
    /// </summary>
    public static class GlassDraw
    {
        /// <summary>0 near buildings only, 1 and the middle distance, 2 the whole city (GlassDistance).</summary>
        public static int Tiers = 2;

        /// <summary>Set by GlassController each frame: glass is on, the materials exist, no info view is open.</summary>
        public static bool Active;

        /// <summary>
        /// A patch threw (<see cref="Fail"/>): the glass stays off for the rest of this city, so the
        /// game's own drawing never meets the same error again. Cleared with the next city (Reset).
        /// </summary>
        public static bool Failed;

        public static Material Near, Lod, Group;

        /// <summary>The reflection pass (GlassController's): every glass draw is recorded into its pre-pass beside it; null: none.</summary>
        public static GlassPrePass PrePass;

        // Per second, for the detail log line.
        public static int NearDraws, LodDraws, GroupDraws, NearSkipped, ProceduralDraws;

        private static readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();
        private static readonly Dictionary<Material, Maps> ByMaterial = new Dictionary<Material, Maps>();
        private static readonly Dictionary<BuildingInfoBase, bool> LodOk = new Dictionary<BuildingInfoBase, bool>();
        private static readonly Dictionary<int, bool> GroupLayerOk = new Dictionary<int, bool>();
        private static readonly Dictionary<string, int> ShaderCounts = new Dictionary<string, int>();

        private static int _idMain, _idXys, _idAci, _idState, _idMainSt, _idObjectIndex, _idNearBase;
        private static bool _ids;
        private static bool _loggedLodSkipped;

        /// <summary>What a building material gives the glass: its three maps, or nothing (not glass-capable).</summary>
        private sealed class Maps
        {
            public bool Ok;
            public Texture Main, Xys, Aci;
            public Vector4 MainSt;
        }

        private static void Ids()
        {
            if (_ids)
                return;
            _idMain = Shader.PropertyToID("_MainTex");
            _idXys = Shader.PropertyToID("_XYSMap");
            _idAci = Shader.PropertyToID("_ACIMap");
            _idState = Shader.PropertyToID("_BuildingState");
            _idMainSt = Shader.PropertyToID("_MainTex_ST");
            _idObjectIndex = Shader.PropertyToID("_ObjectIndex");
            _idNearBase = Shader.PropertyToID("_RGNearBase");
            _ids = true;
        }

        /// <summary>
        /// The game's building shaders the glass follows: Default and the variants that draw a
        /// building the same way. Everything else (fences, wind turbines, animated signs, the
        /// Rotors transparency...) moves or cuts its vertices in ways this draw does not repeat.
        /// </summary>
        public static bool IsBuildingShader(Shader shader)
        {
            if (shader == null)
                return false;
            string name = shader.name;
            return name == "Custom/Buildings/Building/Default" || name == "Custom/Buildings/Building/NoBase"
                || name == "Custom/Buildings/Building/Floating" || name == "Custom/Buildings/Building/Basement";
        }

        private static Maps MapsFor(Material material)
        {
            Maps maps;
            if (ByMaterial.TryGetValue(material, out maps))
                return maps;

            Ids();
            maps = new Maps();
            string shaderName = material.shader != null ? material.shader.name : "(none)";
            int count;
            ShaderCounts.TryGetValue(shaderName, out count);
            ShaderCounts[shaderName] = count + 1;
            if (IsBuildingShader(material.shader) && material.HasProperty(_idXys) && material.HasProperty(_idAci))
            {
                maps.Main = material.GetTexture(_idMain);
                maps.Xys = material.GetTexture(_idXys);
                maps.Aci = material.GetTexture(_idAci);
                Vector2 scale = material.mainTextureScale, offset = material.mainTextureOffset;
                maps.MainSt = new Vector4(scale.x, scale.y, offset.x, offset.y);
                maps.Ok = maps.Main != null && maps.Xys != null && maps.Aci != null;
            }

            ByMaterial[material] = maps;
            return maps;
        }

        /// <summary>
        /// One near building or sub-mesh: the game just drew mesh with material at matrix. state is
        /// its _BuildingState (m_dataVector0), objectIndex its _ObjectIndex (m_dataVector3: where it
        /// is in the game's object colour map -- which building it is, for its kind of glass and
        /// its lamps).
        /// </summary>
        public static void DrawNear(Mesh mesh, Matrix4x4 matrix, Material material, int layer, Vector4 state, Vector4 objectIndex)
        {
            if (!Active || mesh == null || material == null || Near == null)
                return;

            Maps maps = MapsFor(material);
            if (!maps.Ok)
            {
                NearSkipped++;
                return;
            }

            FillBlock(maps, state, objectIndex);
            Graphics.DrawMesh(mesh, matrix, Near, layer, null, 0, Block, false, false);
            if (PrePass != null)
                PrePass.Record(mesh, matrix, GlassPrePass.TierNear, Block, layer);
            NearDraws++;
        }

        /// <summary>
        /// A building of Procedural Objects (ProceduralBuildings): the copy of the asset's mesh it
        /// keeps, where it draws it (layer 0, its position and rotation), the asset's maps -- its
        /// material is a copy of the asset's -- and an object index of its own past the buildings'.
        /// ground: the terrain under it, its glass's ground in place of its origin (_RGNearBase).
        /// </summary>
        public static bool DrawProcedural(Mesh mesh, Matrix4x4 matrix, Material assetMaterial, Vector4 state, Vector4 objectIndex, float ground)
        {
            if (!Active || mesh == null || assetMaterial == null || Near == null)
                return false;

            Maps maps = MapsFor(assetMaterial);
            if (!maps.Ok)
                return false;

            FillBlock(maps, state, objectIndex);
            Block.SetVector(_idNearBase, new Vector4(ground, 0f, 0f, 1f));
            Graphics.DrawMesh(mesh, matrix, Near, 0, null, 0, Block, false, false);
            if (PrePass != null)
                PrePass.Record(mesh, matrix, GlassPrePass.TierNear, Block, 0);
            ProceduralDraws++;
            return true;
        }

        private static void FillBlock(Maps maps, Vector4 state, Vector4 objectIndex)
        {
            Block.Clear();
            Block.SetTexture(_idMain, maps.Main);
            Block.SetTexture(_idXys, maps.Xys);
            Block.SetTexture(_idAci, maps.Aci);
            Block.SetVector(_idMainSt, maps.MainSt);
            Block.SetVector(_idState, state);
            Block.SetVector(_idObjectIndex, objectIndex);
        }

        /// <summary>
        /// The middle distance: Building.RenderLod just drew up to 8 instances of info's combined
        /// LOD mesh, placed by the arrays it left in BuildingManager's property block. The same
        /// block (copied by DrawMesh at the call) draws them again with the glass.
        /// </summary>
        public static void DrawLod(BuildingInfoBase info, int count, MaterialPropertyBlock gameBlock)
        {
            if (!Active || Tiers < 1 || count <= 0 || Lod == null || info == null)
                return;

            Mesh mesh = count <= 1 ? info.m_lodMeshCombined1 : (count <= 4 ? info.m_lodMeshCombined4 : info.m_lodMeshCombined8);
            if (mesh == null || info.m_lodMaterialCombined == null)
                return;

            bool ok;
            if (!LodOk.TryGetValue(info, out ok))
            {
                ok = IsBuildingShader(info.m_lodMaterialCombined.shader);
                LodOk[info] = ok;
            }

            if (!ok)
                return;

            Graphics.DrawMesh(mesh, Matrix4x4.identity, Lod, info.m_prefabDataLayer, null, 0, gameBlock, false, false);
            if (PrePass != null)
                PrePass.Record(mesh, Matrix4x4.identity, GlassPrePass.TierLod, gameBlock, info.m_prefabDataLayer);
            LodDraws++;
        }

        /// <summary>The far city: one baked block (a group mesh) of a building layer.</summary>
        public static void DrawGroup(Mesh mesh, Matrix4x4 matrix, int layer, Material renderMaterial)
        {
            if (!Active || Tiers < 2 || mesh == null || Group == null)
                return;

            bool ok;
            if (!GroupLayerOk.TryGetValue(layer, out ok))
            {
                ok = GlassController.IsBuildingLayer(layer) && renderMaterial != null && renderMaterial.HasProperty("_XYSMap");
                GroupLayerOk[layer] = ok;
                Log.Msg("glass: group layer " + layer + " (" + LayerMask.LayerToName(layer) + ") material '" +
                        (renderMaterial != null ? renderMaterial.name : "none") + "' shader '" +
                        (renderMaterial != null && renderMaterial.shader != null ? renderMaterial.shader.name : "none") +
                        "': " + (ok ? "glass drawn" : "not a building layer"));
            }

            if (!ok)
                return;

            Graphics.DrawMesh(mesh, matrix, Group, layer, null, 0, null, false, false);
            if (PrePass != null)
                PrePass.Record(mesh, matrix, GlassPrePass.TierGroup, null, layer);
            GroupDraws++;
        }

        /// <summary>The building materials seen so far, by shader: which ones the glass follows.</summary>
        public static string DescribeShaders()
        {
            string s = "";
            foreach (KeyValuePair<string, int> kv in ShaderCounts)
                s += (s.Length > 0 ? ", " : "") + kv.Key + " x" + kv.Value;
            return s.Length > 0 ? s : "none yet";
        }

        /// <summary>
        /// A patch caught an exception inside the game's rendering: the glass stops at once (one
        /// log line, not one per building per frame) and stays off for this city -- whatever it
        /// was, the game's buildings must go on being drawn.
        /// </summary>
        public static void Fail(string where, System.Exception e)
        {
            Active = false;
            if (Failed)
                return;

            Failed = true;
            ErrorNotice.Report(ErrorNotice.Glass);
            try
            {
                Log.Error("glass: " + where + " failed; the glass is off until the next city is loaded", e);
            }
            catch
            {
                // the log itself failed: nothing more to do, the glass is off
            }
        }

        /// <summary>
        /// The game's Building.RenderLod did not run for info (another mod's prefix skipped it; see
        /// GlassPatches.RenderLodPatch): no middle-distance glass there. One line a city, naming the
        /// first asset: a report can then say which mod hides what.
        /// </summary>
        public static void NoteLodSkipped(BuildingInfoBase info)
        {
            if (_loggedLodSkipped)
                return;

            _loggedLodSkipped = true;
            Log.Msg("glass: the game's middle-distance draw did not run for '" + (info != null ? info.name : "?") +
                    "' (another mod skipped Building.RenderLod): no glass is drawn where the game drew nothing");
        }

        /// <summary>Forget every cached material and layer (a city unloads, or the atlas changed).</summary>
        public static void Reset()
        {
            ByMaterial.Clear();
            LodOk.Clear();
            GroupLayerOk.Clear();
            ShaderCounts.Clear();
            NearDraws = LodDraws = GroupDraws = NearSkipped = ProceduralDraws = 0;
            Active = false;
            Failed = false;
            _loggedLodSkipped = false;
        }
    }
}
