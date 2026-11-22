using UnityEngine;

namespace RealGlass.Water
{
    /// <summary>
    /// THE ONE MIRROR (2.0.2): the water's reflection with nothing subtracted. Until now the city
    /// reflection's composite took an estimate of the sky the sky layer (RealGlass/WaterSky) had drawn
    /// out of the pixel before adding the city; under a bright low cloud bank its one sharp read sat in
    /// the bank while the layer's rippled reads reached the darker sky above, so it took out up to 2.5
    /// times the sky that was there: near-black reflections and pixels below zero (coloured specks). Now
    /// the sky layer leaves out the share of the sky the city stands in for, and the composite only adds
    /// the city's light (RealGlass/CityReflection's and WaterSky's _RGWaterOneMirror; variant H0, pick (i)
    /// of the preview round of 2026-11-21).
    /// </summary>
    /// <remarks>
    /// - WHICH SHARE: the layer draws before the reflection pass, so it reads LAST frame's: the city
    ///   reflection's streaked light and the share of each texel the city covers (its _RGWaterStreaked /
    ///   _RGWaterStreaked2), copied here once a frame before the frame renders, at last frame's
    ///   view-projection. The composite adds that same light at that same place: what one leaves out the
    ///   other puts in, from the same two textures and the same matrix. One frame late while the camera
    ///   moves; exact from the first still frame.
    /// - ONCE A FRAME: a dynamic resolution mod renders the game's camera twice a frame (and at another
    ///   size): both renders read the same last frame. The copies are the streaked targets' own size
    ///   (half the screen's), read by uv: the render's size does not matter to them.
    /// - THE STRENGTH the layer leaves the sky out by is the one last frame's light was traced with (at
    ///   most 1), so the pair stays whole when the strength jumps (the eye going under the water: 0).
    /// - NEW TARGETS are black (coverage 0: the layer as before, nothing added) until a frame has been
    ///   drawn into the streaked targets.
    /// Kept C# 4, UnityEngine only: the editor's preview runs the same code (tools\preview\sync-sim.ps1).
    /// </remarks>
    public sealed class WaterOneMirror
    {
        public static readonly int IdSwitch = Shader.PropertyToID("_RGWaterOneMirror");
        public static readonly int IdLight = Shader.PropertyToID("_RGWaterPrevLight");
        public static readonly int IdCover = Shader.PropertyToID("_RGWaterPrevCover");
        public static readonly int IdLayerVP = Shader.PropertyToID("_RGWaterOneMirrorVP");

        /// <summary>The composite's switch: the one mirror (x 1), the trace's own rho (y 0), last frame's light at the reprojected point (z 1, pick (i)).</summary>
        public static readonly Vector4 Composite = new Vector4(1f, 0f, 1f, 0f);

        /// <summary>The sky layer's switch: the one mirror's leaving out (x 1, not 2: the layer never draws the city itself), by the strength (y, at most 1).</summary>
        public static Vector4 Layer(float strength)
        {
            return new Vector4(1f, Mathf.Clamp01(strength), 0f, 0f);
        }

        /// <summary>The frame's number: Time.frameCount (the editor's preview counts its own frames).</summary>
        public static System.Func<int> Frame = () => Time.frameCount;

        private RenderTexture _light, _cover;
        private int _copied = int.MinValue;
        private float _strengthNow, _strengthCopied;
        private Matrix4x4 _vp = Matrix4x4.identity;

        /// <summary>Last frame's streaked light as copied this frame (black before the first copy; null when not made).</summary>
        public RenderTexture Light { get { return _light; } }

        /// <summary>Last frame's share covered as copied this frame.</summary>
        public RenderTexture Cover { get { return _cover; } }

        /// <summary>Last frame's view-projection, as the copies were made under (OpenGL convention).</summary>
        public Matrix4x4 PrevVP { get { return _vp; } }

        /// <summary>The strength the copied light was traced with (at most what the layer leaves out by).</summary>
        public float Strength { get { return _strengthCopied; } }

        /// <summary>The copies made (at the streaked targets' size) and black; false when they could not be.</summary>
        public bool Make(int width, int height)
        {
            Free();
            _light = Target("RealGlassWaterOneMirrorLight", width, height);
            _cover = Target("RealGlassWaterOneMirrorCover", width, height);
            _copied = int.MinValue;
            _strengthNow = 0f;
            _strengthCopied = 0f;
            if (!_light.Create() || !_cover.Create())
            {
                Free();
                return false;
            }
            RenderTexture active = RenderTexture.active;
            Graphics.Blit(Texture2D.blackTexture, _light);
            Graphics.Blit(Texture2D.blackTexture, _cover);
            RenderTexture.active = active;
            return true;
        }

        private static RenderTexture Target(string name, int width, int height)
        {
            return new RenderTexture(Mathf.Max(1, width), Mathf.Max(1, height), 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                useMipMap = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
        }

        /// <summary>
        /// Before the frame renders (the city reflection's LateUpdate): last frame's streaked light and
        /// coverage copied, once a frame (<see cref="Frame"/>), with the view-projection the composite
        /// reads them by and this frame's strength. <paramref name="drawn"/>: the streaked targets hold a
        /// drawn frame (false right after they were made: the copies stay black).
        /// </summary>
        public void FrameStart(RenderTexture streaked, RenderTexture streaked2, Matrix4x4 prevVP, float strength, bool drawn)
        {
            int frame = Frame();
            if (frame == _copied || _light == null)
                return;
            _copied = frame;
            _vp = prevVP;
            // The light being copied was traced with the strength set the frame before (0 before any).
            _strengthCopied = _strengthNow;
            _strengthNow = strength;
            if (!drawn || streaked == null || streaked2 == null)
                return;
            RenderTexture active = RenderTexture.active;
            Graphics.Blit(streaked, _light);
            Graphics.Blit(streaked2, _cover);
            RenderTexture.active = active;
        }

        /// <summary>The composite's switch on (the one mirror) or off (0: its estimate of the sky, as before 2.0.2).</summary>
        public void BindComposite(Material composite, bool on)
        {
            composite.SetVector(IdSwitch, on ? Composite : Vector4.zero);
            composite.SetTexture(IdLight, _light != null ? (Texture)_light : Texture2D.blackTexture);
            composite.SetTexture(IdCover, _cover != null ? (Texture)_cover : Texture2D.blackTexture);
        }

        /// <summary>The sky layer's switch at <paramref name="value"/> (<see cref="Layer"/>, or zero), with the copies and the matrix.</summary>
        public void BindLayer(Material layer, Vector4 value)
        {
            layer.SetVector(IdSwitch, value);
            layer.SetMatrix(IdLayerVP, _vp);
            layer.SetTexture(IdLight, _light != null ? (Texture)_light : Texture2D.blackTexture);
            layer.SetTexture(IdCover, _cover != null ? (Texture)_cover : Texture2D.blackTexture);
        }

        /// <summary>No one mirror on a material (the layer when it is not wired): switch 0, black textures.</summary>
        public static void Unbind(Material material)
        {
            material.SetVector(IdSwitch, Vector4.zero);
            material.SetTexture(IdLight, Texture2D.blackTexture);
            material.SetTexture(IdCover, Texture2D.blackTexture);
        }

        public void Free()
        {
            Release(ref _light);
            Release(ref _cover);
            _copied = int.MinValue;
        }

        private static void Release(ref RenderTexture rt)
        {
            if (rt == null)
                return;
            rt.Release();
            if (Application.isPlaying) Object.Destroy(rt);
            else Object.DestroyImmediate(rt);
            rt = null;
        }
    }
}
