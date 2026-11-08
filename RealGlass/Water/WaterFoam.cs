using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace RealGlass.Water
{
    /// <summary>
    /// Foam on the map's water (the author, 2026-10-06: "One thing I'd actually like is the foam, that
    /// would be for sure a keeper"; docs: the water plan, foam first). RealGlass/WaterFoam is drawn on
    /// the game's own water meshes, right after each of the game's water draws: the game's draw is
    /// untouched (<see cref="WaterFoamDrawPatch"/> calls it first, exactly as the game called it), then
    /// ours on the same mesh, matrix, layer and per-patch property block, so the foam finds the game's
    /// simulation (heights, flow, depth) under the game's own names, and lies exactly on its water.
    /// It only adds white; everywhere else it draws nothing. The model and its sources:
    /// <see cref="FoamOptics"/> and the shader's header.
    /// </summary>
    /// <remarks>
    /// - Only on the OPAQUE water material: the transparent one is the game's info views' and the water
    ///   tool's (InfoManager.SetActualMode, WaterTool), where the foam is off anyway.
    /// - Off in info views and under the snapshot tool (an asset's pictures ship to players without the
    ///   mod), as the city reflection.
    /// - IN LAYERS (2026-10-24, the author: "In real life, the foam is often layered"): fresh foam,
    ///   the residual sheet and the bubbles under them, by age (the shader's header); the surf as broken
    ///   waves running in, in a band measured from the waterline -- kept near the real distance from it
    ///   (<see cref="ShoreMap"/>), so a bay's flat floor does not close the band into rings.
    /// - With Detailed logging, every 5 s: the foam's draws a frame, and the simulation's numbers round
    ///   the camera the thresholds are set against (the rise a water frame, the flow's speed) -- the
    ///   front thresholds wait on a real tsunami's.
    /// </remarks>
    public sealed class WaterFoam
    {
        private static readonly int IdPattern = Shader.PropertyToID("_RGFoamPattern");
        private static readonly int IdMainTex = Shader.PropertyToID("_MainTex");
        private static readonly int IdDebug = Shader.PropertyToID("_RGFoamDebug");
        private float _view;

        private static readonly int[] IdVectors =
        {
            Shader.PropertyToID("_RGFoamScale"), Shader.PropertyToID("_RGFoamFeature"), Shader.PropertyToID("_RGFoamShore"),
            Shader.PropertyToID("_RGFoamFast"), Shader.PropertyToID("_RGFoamFront"), Shader.PropertyToID("_RGFoamLook"),
        };

        // The layers (FoamOptics.LayerVectors) and the water's colour (the bubbles take it).
        private static readonly int[] IdLayers =
        {
            Shader.PropertyToID("_RGFoamLayers"), Shader.PropertyToID("_RGFoamSheet"), Shader.PropertyToID("_RGFoamBubbles"),
            Shader.PropertyToID("_RGFoamSurf"), Shader.PropertyToID("_RGFoamScale2"), Shader.PropertyToID("_RGFoamExtra"),
            Shader.PropertyToID("_RGFoamCrest"), Shader.PropertyToID("_RGFoamAbsorb"), Shader.PropertyToID("_RGFoamOuter"),
            Shader.PropertyToID("_RGFoamSteep"),
        };
        private static readonly int IdWater = Shader.PropertyToID("_RGFoamWater");
        private static readonly int IdClean = Shader.PropertyToID("_WaterColorClean");
        // The sea's distance from the waterline (ShoreMap), the surf's corridor.
        private static readonly int IdShore = Shader.PropertyToID("_RGShoreDistance");
        private static readonly int IdShoreMapping = Shader.PropertyToID("_RGShoreMapping");
        // Where the player painted the surf in or out (ShorePaint).
        private static readonly int IdPaint = Shader.PropertyToID("_RGShorePaint");
        private static readonly int IdPaintMapping = Shader.PropertyToID("_RGShorePaintMapping");

        // Read by the draw hook on every one of the game's terrain draws: kept to plain fields.
        private static Material s_material;
        private static Material s_gameWater;
        private static bool s_active;
        private static int s_draws;
        private static bool s_failed;
        private static readonly Dictionary<string, int> s_hooks = new Dictionary<string, int>();

        private readonly Material _material;
        private readonly Texture2D _pattern;
        private readonly ShoreMap _shore = new ShoreMap();
        private Texture _shoreBound;
        private readonly ShorePaintTexture _paint = new ShorePaintTexture();
        private Texture _paintBound;
        private bool _shown;
        private float _statsAt = -1f;
        private int _framesSinceStats;
        private int _drawsSinceStats;

        private WaterFoam(Shader shader)
        {
            _pattern = BuildPattern();
            _material = new Material(shader) { name = "RealGlass water foam", hideFlags = HideFlags.HideAndDontSave };
            _material.SetTexture(IdPattern, _pattern);
        }

        /// <summary>Null when the shader is missing or unsupported here (logged by ShaderBundle): no foam.</summary>
        public static WaterFoam Create()
        {
            Shader shader = ShaderBundle.Get(ShaderBundle.WaterFoam);
            return shader != null && shader.isSupported ? new WaterFoam(shader) : null;
        }

        /// <summary>
        /// The game's DrawMesh, in place of the call in TerrainPatch.Render and RenderSubPatch: the game's
        /// draw exactly as it was, then -- for the water -- the foam on the same mesh and property block.
        /// </summary>
        public static void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int layer, Camera camera, int submeshIndex,
                                    MaterialPropertyBlock properties, bool castShadows, bool receiveShadows)
        {
            Graphics.DrawMesh(mesh, matrix, material, layer, camera, submeshIndex, properties, castShadows, receiveShadows);
            // Reflection coverage and the sky are independent of whether foam is enabled.
            WaterCityReflection.DrawSurface(mesh, matrix, material, layer, camera, submeshIndex, properties);
            WaterSkyLayer.Draw(mesh, matrix, material, layer, camera, submeshIndex, properties);
            if (!s_active || (object)material != (object)s_gameWater || (object)material == null)
                return;

            try
            {
                Graphics.DrawMesh(mesh, matrix, s_material, layer, camera, submeshIndex, properties, false, receiveShadows);
                s_draws++;
            }
            catch (Exception e)
            {
                s_active = false;
                if (!s_failed)
                    Log.Error("water: the foam could not be drawn; it stays off until the city is reloaded", e);
                s_failed = true;
            }
        }

        /// <summary>The transpiler's count of the game's draws it routed through <see cref="DrawMesh"/>, per method.</summary>
        public static void NoteHooks(string method, int calls)
        {
            lock (s_hooks)
                s_hooks[method] = calls;
        }

        /// <summary>"Render 2, RenderSubPatch 3" (the draws routed through the hook); "none" before the patch ran.</summary>
        public static string Hooks()
        {
            lock (s_hooks)
            {
                if (s_hooks.Count == 0)
                    return "none";
                string text = "";
                foreach (KeyValuePair<string, int> pair in s_hooks)
                    text += (text.Length > 0 ? ", " : "") + pair.Key + " " + pair.Value;
                return text;
            }
        }

        /// <summary>Whether the game's water draws go through the hook (both methods, each with a draw).</summary>
        public static bool Hooked
        {
            get
            {
                lock (s_hooks)
                {
                    int render, sub;
                    return s_hooks.TryGetValue("Render", out render) && render > 0 && s_hooks.TryGetValue("RenderSubPatch", out sub) && sub > 0;
                }
            }
        }

        /// <summary>
        /// Foam on the water this frame: <paramref name="amount"/> the strength of it all (1 = the model's),
        /// each source's share (1 = the model's), the sea's level (m).
        /// </summary>
        public void Show(float amount, float shore, float rapids, float fronts, float seaLevel, float view)
        {
            _view = view;
            // y: the paint view, while the shore paint tool is out (its layers on the water).
            _material.SetVector(IdDebug, new Vector4(view, ShorePaintTool.IsActive ? 1f : 0f, 0f, 0f));
            if (s_failed || !Singleton<TerrainManager>.exists)
            {
                Hide();
                return;
            }

            TerrainManager terrain = Singleton<TerrainManager>.instance;
            double[] v = FoamOptics.Vectors(amount, shore, rapids, fronts, seaLevel);
            for (int i = 0; i < IdVectors.Length; i++)
                _material.SetVector(IdVectors[i], new Vector4((float)v[4 * i], (float)v[4 * i + 1], (float)v[4 * i + 2], (float)v[4 * i + 3]));

            double[] layers = FoamOptics.LayerVectors();
            for (int i = 0; i < IdLayers.Length; i++)
                _material.SetVector(IdLayers[i], new Vector4((float)layers[4 * i], (float)layers[4 * i + 1], (float)layers[4 * i + 2], (float)layers[4 * i + 3]));

            // The water's heights texture is read through _MainTex_ST as the game's own material has it.
            Material water = terrain.m_waterMaterial;
            if (water != null && water.HasProperty(IdMainTex))
            {
                _material.SetTextureScale(IdMainTex, water.GetTextureScale(IdMainTex));
                _material.SetTextureOffset(IdMainTex, water.GetTextureOffset(IdMainTex));
            }

            // The bubbles under the foam are the water's own colour made paler: the colour it is drawn
            // with (the Water color row's, or the theme's), linear as the shader takes it.
            Color clean = water != null && water.HasProperty(IdClean) ? water.GetColor(IdClean).linear : ((Color)Settings.Defaults.WaterColor).linear;
            _material.SetVector(IdWater, new Vector4(clean.r, clean.g, clean.b, 0f));

            // The surf's distance from the waterline kept near the real one (the bays' rings).
            _shore.Update(terrain, seaLevel);
            if ((object)_shore.Texture != (object)_shoreBound && _shore.Texture != null)
            {
                _material.SetTexture(IdShore, _shore.Texture);
                _shoreBound = _shore.Texture;
            }
            _material.SetVector(IdShoreMapping, _shoreBound != null ? _shore.Mapping : Vector4.zero);

            // The painted shores, over the rule.
            _paint.Update();
            if ((object)_paint.Texture != (object)_paintBound && _paint.Texture != null)
            {
                _material.SetTexture(IdPaint, _paint.Texture);
                _paintBound = _paint.Texture;
            }
            _material.SetVector(IdPaintMapping, _paintBound != null ? _paint.Mapping : Vector4.zero);

            s_material = _material;
            s_gameWater = water;
            s_active = water != null;
            if (!_shown)
                Log.Msg("water: foam ON (the game's water draws hooked: " + Hooks() + "; pattern " + FoamPattern.Size + " px, lace " +
                        FoamPattern.LaceTile.ToString("F0") + " m, patches " + FoamPattern.PatchTile.ToString("F0") + " m)" +
                        (Hooked ? "" : " -- NOT every water draw is hooked: the foam may be missing in places"));
            _shown = true;
            Stats(terrain, seaLevel);
        }

        /// <summary>No foam (nothing drawn: the hook passes the game's draws straight through).</summary>
        public void Hide()
        {
            s_active = false;
            if (_shown)
                Log.Msg("water: foam off");
            _shown = false;
            _statsAt = -1f;
        }

        public void Destroy()
        {
            Hide();
            s_material = null;
            s_gameWater = null;
            UnityEngine.Object.Destroy(_material);
            UnityEngine.Object.Destroy(_pattern);
            _shore.Destroy();
            _shoreBound = null;
            _paint.Destroy();
            _paintBound = null;
        }

        /// <summary>The pattern texture (FoamPattern: r lace, g patches, b bubbles' holes, a bubble clouds), mipmapped and tiling.</summary>
        private static Texture2D BuildPattern()
        {
            byte[] rgba = FoamPattern.Generate();
            var t = new Texture2D(FoamPattern.Size, FoamPattern.Size, TextureFormat.RGBA32, true, true)
            {
                name = "RealGlassWaterFoam",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
            };
            var px = new Color32[FoamPattern.Size * FoamPattern.Size];
            for (int i = 0; i < px.Length; i++)
                px[i] = new Color32(rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2], rgba[4 * i + 3]);
            t.SetPixels32(px);
            t.Apply(true, true);
            return t;
        }

        /// <summary>
        /// With Detailed logging, every 5 s: the foam's draws a frame, and round the camera (its terrain
        /// patch, 1920 m) the simulation's numbers the thresholds are set against -- the water's rise from
        /// one water frame to the next (m), the flow's speed (the game's units), the slope, how much of the
        /// patch is shallow.
        /// </summary>
        private void Stats(TerrainManager terrain, float seaLevel)
        {
            _framesSinceStats++;
            _drawsSinceStats += s_draws;
            s_draws = 0;
            float now = Time.realtimeSinceStartup;
            if (_statsAt < 0f)
            {
                _statsAt = now;
                _framesSinceStats = 0;
                _drawsSinceStats = 0;
                return;
            }

            if (now - _statsAt < 5f)
                return;

            float draws = _framesSinceStats > 0 ? (float)_drawsSinceStats / _framesSinceStats : 0f;
            _statsAt = now;
            _framesSinceStats = 0;
            _drawsSinceStats = 0;
            if (!Log.Detailed && _view <= 0f)
                return;

            string numbers;
            try
            {
                numbers = Simulation(terrain, seaLevel);
            }
            catch (Exception e)
            {
                numbers = "the simulation's numbers unreadable (" + e.Message + ")";
            }

            string line = "water: foam " + draws.ToString("F0") + " draws a frame (view " + _view.ToString("F0") + "); " + numbers;
            if (_view > 0f)
                Log.Msg(line);
            else
                Log.Detail(line);
        }

        private static string Simulation(TerrainManager terrain, float seaLevel)
        {
            CameraController controller = GameCamera.Controller;
            Camera camera = GameCamera.Get();
            Vector3 focus = controller != null ? controller.m_currentPosition : (camera != null ? camera.transform.position : Vector3.zero);
            TerrainPatch patch = null;
            int px = Mathf.Clamp(Mathf.FloorToInt((focus.x + 8640f) / 1920f), 0, 8), pz = Mathf.Clamp(Mathf.FloorToInt((focus.z + 8640f) / 1920f), 0, 8);
            foreach (TerrainPatch p in terrain.m_patches)
            {
                if (p != null && p.m_x == px && p.m_z == pz)
                {
                    patch = p;
                    break;
                }
            }

            if (patch == null || patch.m_waterHeight == null)
                return "no terrain patch at the camera";

            // The frames the game's water draws with now (TerrainPatch.SetWaterMaterialProperties).
            uint f = Singleton<SimulationManager>.instance.m_referenceFrameIndex + 32u;
            int heightIndex = (int)(((f - 64u) >> 6) & 1u);
            int newer = (int)(((f - 64u) >> 6) % 3u);
            Texture2D heights = patch.m_waterHeight[heightIndex].m_waterHeight;
            Texture2D surfaceA = patch.m_waterSurfaceA != null ? patch.m_waterSurfaceA[newer] : null;
            Texture2D surfaceB = patch.m_waterSurfaceB != null ? patch.m_waterSurfaceB[newer] : null;
            if (heights == null || surfaceA == null || surfaceB == null)
                return "the patch has no water textures";

            float scale = patch.m_heightMappingRaw.y;
            Color32[] h = heights.GetPixels32();
            var rises = new List<float>();
            for (int i = 0; i < h.Length; i++)
            {
                float older = ((h[i].r << 8) | h[i].g) / 65536f * scale, later = ((h[i].b << 8) | h[i].a) / 65536f * scale;
                if (later != older)
                    rises.Add(later - older);
            }

            Color32[] a = surfaceA.GetPixels32(), b = surfaceB.GetPixels32();
            var speeds = new List<float>();
            var slopes = new List<float>();
            int wet = 0, shallow = 0;
            for (int i = 0; i < b.Length && i < a.Length; i++)
            {
                if (b[i].g == 0)
                    continue;
                wet++;
                float depth = b[i].g / 255f * 16f;
                if (depth < 2f)
                    shallow++;
                float fx = b[i].r / 255f * 2f - 1f, fz = b[i].b / 255f * 2f - 1f;
                speeds.Add(Mathf.Sqrt(fx * fx + fz * fz));
                float nx = a[i].r / 255f * 2f - 1f, nz = a[i].b / 255f * 2f - 1f;
                slopes.Add(Mathf.Sqrt(nx * nx + nz * nz));
            }

            return "patch (" + px + ", " + pz + ") mappings surface " + patch.m_surfaceMappingRaw + " height " + patch.m_heightMappingRaw +
                   " detail " + patch.m_heightMappingDetail + " (detail " + patch.m_rndDetailIndex + "), heights " + heights.width + "x" + heights.height +
                   " " + heights.format + ", surface " + surfaceB.width + "x" + surfaceB.height + " " + surfaceB.format + "; focus " + focus + ", sea level " + seaLevel.ToString("F1") + " m: rise a water frame " + Spread(rises, "m") +
                   "; flow speed " + Spread(speeds, "") + "; slope " + Spread(slopes, "") + "; " + wet + " wet texels of " + b.Length +
                   ", " + shallow + " under 2 m deep";
        }

        /// <summary>"median a, 95% b, max c" of the values (by size; signed values by their size too).</summary>
        private static string Spread(List<float> values, string unit)
        {
            if (values.Count == 0)
                return "none";
            values.Sort((x, y) => Math.Abs(x).CompareTo(Math.Abs(y)));
            float median = values[values.Count / 2], p95 = values[Math.Min(values.Count - 1, (int)(values.Count * 0.95))], max = values[values.Count - 1];
            return "median " + median.ToString("F2") + unit + ", 95% " + p95.ToString("F2") + unit + ", max " + max.ToString("F2") + unit + " (" + values.Count + ")";
        }
    }
}
