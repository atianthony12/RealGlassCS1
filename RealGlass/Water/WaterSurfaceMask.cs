using UnityEngine;
using UnityEngine.Rendering;

namespace RealGlass.Water
{
    /// <summary>
    /// Eye depth of the actual water meshes, independently of the scene depth (which contains the
    /// seabed). Recorded with each patch's property block, before the game reuses that block.
    /// Cleared before a frame's first draw so missing patches never leave old water behind; every
    /// render of the camera in that frame keeps the frame's draws, as the game's own draws do.
    /// </summary>
    /// <remarks>
    /// The scene depth it tests against -- and the city reflection traces through -- is its own copy of
    /// the camera's depth texture, taken at AfterFinalPass, before the forward water is drawn, and
    /// bound in place of _CameraDepthTexture. On Metal the depth texture read later in the frame holds
    /// the game's water itself, pulled toward the camera by its Offset, and every water pixel failed
    /// the test (the author's M1, 2026-10-26: no city in the water); on Direct3D 11 the depth texture is
    /// made once, so the copy holds exactly what it holds.
    ///
    /// THE WATER IN THE DEPTH TEXTURE (2026-10-28): the game's water writes the depth buffer, never the
    /// depth texture, so every pass after it took a water pixel for whatever lies behind the water --
    /// the game's fog filled a wave standing against the sky with its colour, and Volumetric Weather's
    /// clouds drew over it; the fog over the water followed the hidden seabed and piers. Right after the
    /// forward water (BeforeImageEffectsOpaque) the surface is recorded and merged into a copy of the
    /// scene depth, and that copy is bound as _CameraDepthTexture for the rest of the frame: the fog,
    /// the transparents, the image effects. Unity binds its own again at the camera's next render
    /// (probed in the 5.6 editor, Direct3D 11 and OpenGL Core: the replacement reached an
    /// [ImageEffectOpaque] OnRenderImage and every later event; the next frame's AfterFinalPass, and
    /// another camera, read their own). Not on Metal, whose depth texture holds the water already.
    /// </remarks>
    public sealed class WaterSurfaceMask
    {
        private const int PassSceneDepth = 1, PassMerged = 2;
        private static readonly int IdDepth = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int IdSurface = Shader.PropertyToID("_RGWaterSurfaceDepth");

        /// <summary>The water's surface goes into the depth texture the later passes read (not on Metal: there it is in it).</summary>
        public static readonly bool MergesDepth = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal;

        /// <summary>When the surface is recorded: right after the forward water where it is merged, else before the reflection.</summary>
        private static readonly CameraEvent SurfaceWhen = MergesDepth ? CameraEvent.BeforeImageEffectsOpaque : CameraEvent.AfterImageEffectsOpaque;

        private readonly Material _material;
        private readonly Material _copy;
        private readonly Material _merge;
        private readonly CommandBuffer _buffer;
        private readonly CommandBuffer _sceneBuffer;
        private readonly CommandBuffer _mergeBuffer;
        private Camera _camera;
        private RenderTexture _texture;
        private RenderTexture _scene;
        private RenderTexture _merged;
        // The frame itself as it stood before the forward water (the realistic depth's seabed), only
        // while asked for (BedWanted).
        private RenderTexture _bed;
        private bool _bedWanted;
        private bool _consumed;
        private int _frame = -1;

        public WaterSurfaceMask(Shader shader)
        {
            _material = new Material(shader) { name = "RealGlass water surface depth", hideFlags = HideFlags.HideAndDontSave };
            // The copy reads the camera's own depth texture: a material of its own, not given the copy.
            _copy = new Material(shader) { name = "RealGlass scene depth before the water", hideFlags = HideFlags.HideAndDontSave };
            _merge = new Material(shader) { name = "RealGlass scene depth with the water", hideFlags = HideFlags.HideAndDontSave };
            _buffer = new CommandBuffer { name = "RealGlass water surface depth" };
            _sceneBuffer = new CommandBuffer { name = "RealGlass scene depth before the water" };
            _mergeBuffer = new CommandBuffer { name = "RealGlass water surface in the depth texture" };
        }

        public RenderTexture Texture { get { return _texture; } }

        /// <summary>The camera's raw depth before the forward water; bind it in place of _CameraDepthTexture.</summary>
        public RenderTexture SceneDepth { get { return _scene; } }

        /// <summary>The scene depth with the water's surface in it, bound as _CameraDepthTexture after the water (null on Metal).</summary>
        public RenderTexture MergedDepth { get { return _merged; } }

        /// <summary>
        /// A copy of the frame before the forward water, taken with <see cref="SceneDepth"/> at the final
        /// pass (ARGBHalf, the camera's size), or null: only while <see cref="BedWanted"/> (the realistic
        /// depth's seabed: RealGlass/WaterSky reads it).
        /// </summary>
        public RenderTexture BedFrame { get { return _bed; } }

        /// <summary>Whether <see cref="BedFrame"/> is made; takes effect at the next <see cref="Attach"/>.</summary>
        public bool BedWanted
        {
            get { return _bedWanted; }
            set { _bedWanted = value; }
        }

        // Attach before the reflection buffer: the scene depth is ready and the surface depth is
        // produced before tracing. Its own depth attachment selects the nearest water patch without
        // writing into the camera's depth buffer. The merge is added after the surface, at the same
        // event: buffers run in the order added.
        public void Attach(Camera camera)
        {
            if (_camera != camera)
            {
                Detach();
                _camera = camera;
                _camera.AddCommandBuffer(CameraEvent.AfterFinalPass, _sceneBuffer);
                _camera.AddCommandBuffer(SurfaceWhen, _buffer);
                if (MergesDepth)
                    _camera.AddCommandBuffer(SurfaceWhen, _mergeBuffer);
                Camera.onPreCull += BeforeRender;
                Camera.onPostRender += AfterRender;
            }
            if (_texture != null && _texture.width == camera.pixelWidth && _texture.height == camera.pixelHeight && (_bed != null) == _bedWanted)
                return;

            _buffer.Clear();
            _sceneBuffer.Clear();
            _mergeBuffer.Clear();
            Free();
            int width = Mathf.Max(1, camera.pixelWidth), height = Mathf.Max(1, camera.pixelHeight);
            _texture = Target("RealGlassWaterSurfaceDepth", width, height, 24);
            _scene = Target("RealGlassWaterSceneDepth", width, height, 0);
            _merged = MergesDepth ? Target("RealGlassWaterMergedDepth", width, height, 0) : null;
            _bed = _bedWanted ? new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = "RealGlassWaterBedFrame",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            } : null;
            if (!_texture.Create() || !_scene.Create() || (_merged != null && !_merged.Create()) || (_bed != null && !_bed.Create()))
                throw new System.InvalidOperationException("Could not allocate the water surface depth textures.");
            // The seabed's frame, then the scene's depth: one moment of the frame, both at the camera's
            // size and taken again at every render of it (a camera rendered twice a frame included).
            if (_bed != null)
                _sceneBuffer.Blit(BuiltinRenderTextureType.CameraTarget, _bed);
            _sceneBuffer.Blit(BuiltinRenderTextureType.CameraTarget, _scene, _copy, PassSceneDepth);
            _material.SetTexture(IdDepth, _scene);
            if (_merged != null)
            {
                _merge.SetTexture(IdDepth, _scene);
                _merge.SetTexture(IdSurface, _texture);
                _mergeBuffer.Blit(BuiltinRenderTextureType.CameraTarget, _merged, _merge, PassMerged);
                _mergeBuffer.SetGlobalTexture(IdDepth, _merged);
                _mergeBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            }
            Clear();
        }

        private static RenderTexture Target(string name, int width, int height, int depth)
        {
            return new RenderTexture(width, height, depth, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
        }

        public void Draw(Mesh mesh, Matrix4x4 matrix, int layer, Camera camera, int submesh, MaterialPropertyBlock properties)
        {
            if (_camera == null || (camera != null && camera != _camera) || (_camera.cullingMask & (1 << layer)) == 0)
                return;
            if (_consumed || _frame != Frame())
                Clear();
            _buffer.DrawMesh(mesh, matrix, _material, submesh, 0, properties);
        }

        /// <summary>The frame's number: Time.frameCount (the editor's tests count their own: there every render is one frame).</summary>
        public static System.Func<int> Frame = () => Time.frameCount;

        public void SetMapping(Material water)
        {
            if (water == null || !water.HasProperty("_MainTex"))
                return;
            _material.SetTextureScale("_MainTex", water.GetTextureScale("_MainTex"));
            _material.SetTextureOffset("_MainTex", water.GetTextureOffset("_MainTex"));
        }

        private void AfterRender(Camera camera)
        {
            if (camera == _camera)
                _consumed = true;
        }

        private void BeforeRender(Camera camera)
        {
            // Not after a render earlier this frame: a mod that renders the game's camera by hand can
            // render it twice a frame (Lumina's dynamic resolution beside the old Dynamic Resolution
            // mod), and the game's water, drawn for every camera, is in both renders.
            if (camera == _camera && _frame != Frame())
                Clear();
        }

        private void Clear()
        {
            _buffer.Clear();
            _consumed = false;
            _frame = Frame();
            if (_texture == null)
                return;
            _buffer.SetRenderTarget(_texture);
            _buffer.ClearRenderTarget(true, true, Color.clear);
        }

        public void Detach()
        {
            Camera.onPostRender -= AfterRender;
            Camera.onPreCull -= BeforeRender;
            if (_camera != null)
            {
                _camera.RemoveCommandBuffer(SurfaceWhen, _buffer);
                if (MergesDepth)
                    _camera.RemoveCommandBuffer(SurfaceWhen, _mergeBuffer);
                _camera.RemoveCommandBuffer(CameraEvent.AfterFinalPass, _sceneBuffer);
            }
            _camera = null;
            Clear();
        }

        private void Free()
        {
            if (_texture != null)
            {
                _texture.Release();
                Dispose(_texture);
                _texture = null;
            }
            if (_scene != null)
            {
                _scene.Release();
                Dispose(_scene);
                _scene = null;
            }
            if (_merged != null)
            {
                _merged.Release();
                Dispose(_merged);
                _merged = null;
            }
            if (_bed != null)
            {
                _bed.Release();
                Dispose(_bed);
                _bed = null;
            }
        }

        public void Destroy()
        {
            Detach();
            _buffer.Release();
            _sceneBuffer.Release();
            _mergeBuffer.Release();
            Free();
            Dispose(_material);
            Dispose(_copy);
            Dispose(_merge);
        }

        private static void Dispose(Object value)
        {
            if (Application.isPlaying) Object.Destroy(value);
            else Object.DestroyImmediate(value);
        }
    }
}
