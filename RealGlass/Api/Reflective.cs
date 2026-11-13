using System;
using RealGlass.Props;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealGlass.Api
{
    /// <summary>
    /// For other mods (2.0.0): Real Glass's Reflective finish -- the props' shader's finish pass,
    /// RealGlass/PropGlass with _RGPropMode 3 (the city's reflection where the screen holds it, a
    /// building of the city walk, the ground, the sky, the sun's glint) -- drawn over a mesh another mod
    /// draws itself. Nothing here runs unless another mod calls it: a city without such a mod is as it
    /// was.
    /// </summary>
    /// <remarks>
    /// FOR OTHER MODS (called by REFLECTION, so a mod never needs RealGlass.dll to build; these names,
    /// signatures and meanings stay, <see cref="ApiVersion"/> says what is here; main thread only):
    /// - <see cref="Available"/>: Real Glass would draw this frame (its glass on: not off, not in an
    ///   info view or the Snapshot tool, no error since the city loaded).
    /// - <see cref="Draw"/>(mesh, matrix, layer, camera, colour, reflectivity, polish, glint): the
    ///   finish over the mesh this frame (call it each frame, as Graphics.DrawMesh). TRUE when Real
    ///   Glass draws it -- then leave out your own reflection of that surface (its sky and its
    ///   1 - F dimming: ours does both); FALSE: draw yours as without Real Glass.
    ///
    /// The mesh: POSITION and NORMAL as drawn (no vertex shader of the caller's runs: a mesh whose
    /// shader moves its vertices needs a plain copy with them in place), submesh 0. Its origin's world
    /// height is taken as the ground under it (the reflection's ground below the horizon). Drawn with
    /// ZTest LEqual and a small depth offset over the caller's own draw of the same surface: where
    /// the caller's draw lies in front (made wider far away), ours is simply hidden.
    ///
    /// The look: <c>colour</c> the reflection's colour, sRGB
    /// (its brightest channel is taken as 1: white for a gloss coat); <c>reflectivity</c> the
    /// brightest channel's reflectance head-on, 0..1 (a gloss coat or lacquer 0.04, aluminium ~0.91);
    /// <c>polish</c> 0..1 (0 roughness 0.5, 1 a mirror's 0.02); <c>glint</c> the sun's glint, 0..2
    /// (1 as real). Out-of-range or non-finite numbers are clamped or replaced, never thrown on.
    ///
    /// Never throws. At most <see cref="MaxPerFrame"/> draws a frame (the rest return false). If a
    /// draw fails, the API stands down until the next city and logs it once. The props' own glass is
    /// not affected.
    /// </remarks>
    public static class Reflective
    {
        public const int ApiVersion = 1;

        /// <summary>Draws a frame through the API at most: the rest are told false (their own look).</summary>
        public const int MaxPerFrame = 4096;

        private static readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();
        private static int _idMode, _idOptics, _idTint, _idLook, _idSt;
        private static bool _ids;

        private static bool _failed;
        private static Material _failedOn;   // the material the failure was on: a new city's is a new one
        private static bool _loggedFirst;
        private static Material _loggedOn;
        private static int _frame = -1, _thisFrame;
        private static int _draws;
        private static float _nextDetail;

        /// <summary>Real Glass would draw a surface this frame.</summary>
        public static bool Available
        {
            get
            {
                try
                {
                    Material m = PropGlassDraw.Opaque;
                    if (_failed && m != _failedOn)
                        _failed = false;   // a new city: a new material
                    return !_failed && m != null && Glass.GlassDraw.Active && !PropGlassDraw.Failed;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// The Reflective finish over <paramref name="mesh"/> at <paramref name="matrix"/>, this frame
        /// (see the class's remarks). <paramref name="camera"/> null: every camera, as Graphics.DrawMesh.
        /// True when drawn: leave out your own reflection of it.
        /// </summary>
        public static bool Draw(Mesh mesh, Matrix4x4 matrix, int layer, Camera camera, Color colour,
                                float reflectivity, float polish, float glint)
        {
            if (mesh == null || !Available)
                return false;
            if (!Finite(matrix.m03) || !Finite(matrix.m13) || !Finite(matrix.m23) || !Finite(matrix.m00) || !Finite(matrix.m11) || !Finite(matrix.m22))
                return false;

            int frame = Time.frameCount;
            if (frame != _frame)
            {
                _frame = frame;
                _thisFrame = 0;
            }
            if (_thisFrame >= MaxPerFrame)
                return false;

            Material material = PropGlassDraw.Opaque;
            try
            {
                Ids();
                Color linear = colour.linear;
                float r = Clean(linear.r, 1f), g = Clean(linear.g, 1f), b = Clean(linear.b, 1f);
                if (Math.Max(r, Math.Max(g, b)) < 1e-3f)
                    r = g = b = 1f;   // black has no brightest channel: untinted

                Block.Clear();
                Block.SetFloat(_idMode, PropGlassDraw.FinishMode);
                Block.SetVector(_idSt, new Vector4(1f, 1f, 0f, 0f));
                // x reflectivity head-on, y clarity (unused by a finish), z strength, w shininess needed (0: all of it).
                Block.SetVector(_idOptics, new Vector4(Mathf.Clamp01(Clean(reflectivity, 0.04f)), 1f, 1f, 0f));
                Block.SetVector(_idTint, new Vector4(r, g, b, 1f));
                Block.SetVector(_idLook, new Vector4(PropGlassDraw.Roughness(PropGlassDraw.FinishMode, Mathf.Clamp01(Clean(polish, 0.5f))),
                                                     Mathf.Clamp(Clean(glint, 1f), 0f, 2f), 0f, 1f));
                Graphics.DrawMesh(mesh, matrix, material, layer & 31, camera, 0, Block, false, false);
            }
            catch (Exception e)
            {
                Stand(material, e);
                return false;
            }

            _thisFrame++;
            _draws++;
            if (!_loggedFirst || _loggedOn != material)
            {
                _loggedFirst = true;
                _loggedOn = material;
                Log.Msg("api: another mod draws surfaces with Real Glass's Reflective finish (Api.Reflective)");
            }
            if (Log.Detailed && Time.realtimeSinceStartup >= _nextDetail)
            {
                Log.Detail("api: " + _draws + " Reflective surfaces of another mod drawn in the last 5 s");
                _draws = 0;
                _nextDetail = Time.realtimeSinceStartup + 5f;
            }
            return true;
        }

        private static void Ids()
        {
            if (_ids)
                return;
            _idMode = Shader.PropertyToID("_RGPropMode");
            _idOptics = Shader.PropertyToID("_RGPropOptics");
            _idTint = Shader.PropertyToID("_RGPropTint");
            _idLook = Shader.PropertyToID("_RGPropLook");
            _idSt = Shader.PropertyToID("_RGPropST");
            _ids = true;
        }

        private static bool Finite(float v)
        {
            return !float.IsNaN(v) && !float.IsInfinity(v);
        }

        private static float Clean(float v, float fallback)
        {
            return Finite(v) ? Math.Max(v, 0f) : fallback;
        }

        private static void Stand(Material material, Exception e)
        {
            _failed = true;
            _failedOn = material;
            try
            {
                Log.Error("api: drawing another mod's Reflective surface failed; the API is off until the next city is loaded (Real Glass's own glass goes on)", e);
            }
            catch
            {
                // the log itself failed: nothing more to do
            }
        }
    }
}
