using UnityEngine;
using UnityEngine.Rendering;

namespace RealGlass.Glass
{
    /// <summary>
    /// The pictures the glass reads. A command buffer on the camera, right after the deferred final
    /// pass (before any glass is drawn, before the fog), copies the lit opaque picture into _RGScene
    /// (mipmapped: rough glass reads a blurrier level) -- the pixel UNDER each pane -- and averages
    /// its lower half into the 1x1 _RGCity. A second one, at the end of the frame (after the fog, the
    /// transparent things -- clouds, the lamps' glow -- and the glass itself), copies the FINISHED
    /// picture into _RGSceneFinished: what the next frame's glass reflects, read where each
    /// reflected point was last frame (_RGPrevVP, the camera's matrices as it rendered) -- the
    /// previous frame reprojected, as Killzone Shadow Fall's reflections (GDC 2014) did.
    /// All hold finite light only (GlassUtil says why), and the city colour is blended by the
    /// shader from a copy of last frame's (_cityPrev), so nothing bad can stay in it.
    /// With a budget for it (GlassQuality.HiZLevels), the first buffer also builds _RGHiZ, the
    /// scene's depth as a min pyramid, which the reflections' trace strides over.
    /// Engine types only: the GPU preview runs this same file.
    /// </summary>
    public sealed class GlassScreen
    {
        /// <summary>After the deferred final pass: lit, opaque, not yet fogged.</summary>
        public const CameraEvent When = CameraEvent.AfterFinalPass;

        /// <summary>The end of the frame, before the image effects (tone mapping): still HDR, fogged, everything drawn.</summary>
        public const CameraEvent FinishedWhen = CameraEvent.BeforeImageEffects;

        /// <summary>This frame's share of the city colour (the rest is last frame's): ~0.3 s to settle.</summary>
        public const float CityBlend = 0.1f;

        // GlassUtil's passes.
        private const int PassCity = 0, PassCopy = 1, PassHiZ0 = 2, PassHiZMin = 3, PassHiZCopy = 4;

        private Camera _camera;
        private Material _util;
        private CommandBuffer _buffer;
        private RenderTexture _scene;
        private RenderTexture _city, _cityPrev;
        private int _width, _height;
        private bool _fresh;

        // The hierarchical depth (Uludag, "Hi-Z Screen-Space Cone-Traced Reflections", GPU Pro 5):
        // level 0 the scene's linear eye depth, each further level the nearest (r) and the farthest
        // (g) depth of the 2 x 2 below it (the farthest: Tevs et al.'s maximum mipmaps, I3D 2008),
        // built in the same buffer (GlassUtil pass 3 into a temporary target, pass 4 from there into
        // the level's mip: a mip cannot be read while another of the same texture is drawn to).
        private RenderTexture _hiz;
        private Mesh _quad;
        private int _hizLevels, _hizWanted;
        private static int _tempId, _levelId, _sizesId, _sourceId;
        private static bool _ids;

        /// <summary>Levels of the hierarchical depth built (0: none -- the plain trace).</summary>
        public int HiZLevels { get { return _hiz != null ? _hizLevels : 0; } }

        private CommandBuffer _finishedBuffer;
        private RenderTexture _finished;
        private Matrix4x4 _lastViewProjection = Matrix4x4.identity;
        private int _renders;
        private Camera.CameraCallback _onPreRender;

        /// <summary>
        /// The glass's reflection pass (GlassPrePass), or null: its buffers go on the camera right
        /// after this one's, at the same event (it reads the copies and the depth pyramid).
        /// </summary>
        public GlassPrePass PrePass
        {
            get { return _prePass; }
            set
            {
                if (_prePass == value)
                    return;
                if (_prePass != null)
                    _prePass.Detach();
                _prePass = value;
                if (_prePass != null && _camera != null && _buffer != null)
                    _prePass.Attach(_camera, When, _width, _height);
            }
        }

        private GlassPrePass _prePass;

        public RenderTexture Scene { get { return _scene; } }
        public RenderTexture City { get { return _city; } }
        public int Width { get { return _width; } }
        public int Height { get { return _height; } }

        /// <summary>True while the finished picture is copied and read (it exists after the camera's first render).</summary>
        public bool Finished { get { return _finished != null && _renders > 0; } }

        /// <summary>Why the last Update failed ("" when it did not).</summary>
        public string Problem = "";

        /// <summary>
        /// Keeps the copies the camera's size and the buffers attached to it. finished: also copy
        /// the finished picture for the reflections. hizLevels: levels of the hierarchical depth to
        /// build (GlassQuality; 0 none). Returns false when there is nothing to copy with (no camera,
        /// no util shader): the glass then reflects the sky only.
        /// </summary>
        public bool Update(Camera camera, Material util, bool finished, int hizLevels)
        {
            if (camera == null || util == null)
            {
                Problem = camera == null ? "no camera" : "no GlassUtil shader";
                Detach();
                Shader.SetGlobalVector("_RGHiZInfo", Vector4.zero);
                return false;
            }

            int w = Mathf.Max(camera.pixelWidth, 16);
            int h = Mathf.Max(camera.pixelHeight, 16);
            if (camera != _camera || util != _util || w != _width || h != _height || _scene == null || !_scene.IsCreated() ||
                hizLevels != _hizWanted)
            {
                Detach();
                _camera = camera;
                _util = util;
                _width = w;
                _height = h;
                _hizWanted = hizLevels;
                Build();
            }

            if (finished)
                AttachFinished();
            else
                DropFinished();

            Shader.SetGlobalTexture("_RGScene", _scene);
            Shader.SetGlobalTexture("_RGCity", _city);
            // A new target holds whatever its memory held last: its first frame takes this
            // frame's average alone.
            Shader.SetGlobalFloat("_RGCityBlend", _fresh ? 1f : CityBlend);
            _fresh = false;

            // Last frame's picture and the matrices it was drawn with; until the camera has rendered
            // once with the copy on, there is none and the shader reads _RGScene as before.
            Shader.SetGlobalTexture("_RGSceneFinished", Finished ? _finished : _scene);
            Shader.SetGlobalMatrix("_RGPrevVP", _lastViewProjection);
            Shader.SetGlobalVector("_RGFinished", new Vector4(Finished ? 1f : 0f, 0f, 0f, 0f));
            if (_hiz != null)
                Shader.SetGlobalTexture("_RGHiZ", _hiz);
            Shader.SetGlobalVector("_RGHiZInfo", new Vector4(HiZLevels, _width, _height, _hiz != null ? 1f : 0f));
            if (_prePass != null)
                _prePass.Update();
            Problem = "";
            return true;
        }

        private void Build()
        {
            _scene = NewPicture("RealGlass scene");

            _city = NewCity("RealGlass city colour");
            _cityPrev = NewCity("RealGlass city colour, last frame");
            _util.SetTexture("_RGCityPrev", _cityPrev);

            _buffer = new CommandBuffer();
            _buffer.name = "RealGlass screen copy";
            _buffer.Blit(BuiltinRenderTextureType.CameraTarget, _scene, _util, PassCopy);
            _buffer.Blit(_city, _cityPrev);
            _buffer.Blit(_scene, _city, _util, PassCity);
            BuildHiZ();
            _camera.AddCommandBuffer(When, _buffer);
            if (_prePass != null)
                _prePass.Attach(_camera, When, _width, _height);
            _fresh = true;

            // The camera's matrices as it renders, for the next frame's reprojection. The plain
            // (OpenGL-convention) projection: ComputeScreenPos undoes Unity's render-texture flip,
            // so screen uv from it is what the copies are read with.
            _onPreRender = OnPreRender;
            Camera.onPreRender += _onPreRender;
        }

        /// <summary>
        /// The hierarchical depth, appended to the copy's buffer: only where a float target exists;
        /// else the glass keeps the plain trace.
        /// </summary>
        private void BuildHiZ()
        {
            _hizLevels = 0;
            RenderTextureFormat format;
            if (_hizWanted <= 0 || !HiZFormat(out format))
                return;

            _hiz = NewPyramid(_width, _height, format);
            if (_quad == null)
                _quad = ClipQuad();
            _buffer.Blit(_scene, _hiz, _util, PassHiZ0);
            _hizLevels = AppendLevels(_buffer, _hiz, Mathf.Min(_hizWanted, MipCount(_width, _height)), format, _util, _quad);
        }

        /// <summary>
        /// The pyramid's format: nearest and farthest depth, half floats (a 4K screen's pyramid is
        /// 44 MB; half a float's 0.05% is far inside the trace's 1% thickness), else full floats;
        /// false when there is neither.
        /// </summary>
        public static bool HiZFormat(out RenderTextureFormat format)
        {
            format = RenderTextureFormat.RGHalf;
            if (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGHalf))
                return true;
            format = RenderTextureFormat.RGFloat;
            return SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGFloat);
        }

        /// <summary>Mips of a width x height texture: mip k is max(1, size >> k), as the GPU lays them out.</summary>
        public static int MipCount(int width, int height)
        {
            int mips = 1;
            for (int size = Mathf.Max(width, height); size > 1; size >>= 1)
                mips++;
            return mips;
        }

        public static RenderTexture NewPyramid(int width, int height, RenderTextureFormat format)
        {
            var rt = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear);
            rt.name = "RealGlass hierarchical depth";
            rt.useMipMap = true;
            rt.autoGenerateMips = false;
            rt.filterMode = FilterMode.Point;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.Create();
            return rt;
        }

        /// <summary>
        /// Levels 1 .. levels - 1 of a pyramid whose level 0 the buffer has already drawn: each the
        /// nearest of the 2 x 2 below (GlassUtil pass 3) into a temporary target, then drawn into its
        /// mip (SetRenderTarget(rt, mip), pass 4). Returns the levels made. Public for the editor's
        /// probe, which runs this very routine.
        /// </summary>
        public static int AppendLevels(CommandBuffer cb, RenderTexture hiz, int levels, RenderTextureFormat format, Material util, Mesh quad)
        {
            if (!_ids)
            {
                _tempId = Shader.PropertyToID("_RGHiZTemp");
                _levelId = Shader.PropertyToID("_RGHiZLevel");
                _sizesId = Shader.PropertyToID("_RGHiZSizes");
                _sourceId = Shader.PropertyToID("_RGHiZSource");
                _ids = true;
            }

            int width = hiz.width, height = hiz.height;
            int sw = width, sh = height;
            for (int k = 1; k < levels; k++)
            {
                int dw = Mathf.Max(1, width >> k), dh = Mathf.Max(1, height >> k);
                cb.GetTemporaryRT(_tempId, dw, dh, 0, FilterMode.Point, format, RenderTextureReadWrite.Linear);
                cb.SetGlobalFloat(_levelId, k - 1);
                cb.SetGlobalVector(_sizesId, new Vector4(sw, sh, dw, dh));
                cb.Blit(hiz, _tempId, util, PassHiZMin);
                cb.SetRenderTarget(hiz, k);
                cb.SetGlobalTexture(_sourceId, _tempId);
                cb.DrawMesh(quad, Matrix4x4.identity, util, 0, PassHiZCopy);
                cb.ReleaseTemporaryRT(_tempId);
                sw = dw;
                sh = dh;
            }
            return Mathf.Max(levels, 1);
        }

        /// <summary>Two triangles covering clip space (x, y in -1..1), uv 0..1: GlassUtil pass 4's quad.</summary>
        public static Mesh ClipQuad()
        {
            var quad = new Mesh();
            quad.name = "RealGlass clip-space quad";
            quad.hideFlags = HideFlags.HideAndDontSave;
            quad.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(-1f, 1f, 0f) };
            quad.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            quad.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            return quad;
        }

        private void OnPreRender(Camera camera)
        {
            if (camera != _camera)
                return;

            _lastViewProjection = camera.projectionMatrix * camera.worldToCameraMatrix;
            if (_finished != null)
                _renders++;
        }

        private void AttachFinished()
        {
            if (_finishedBuffer != null)
                return;

            _finished = NewPicture("RealGlass finished picture");
            _renders = 0;
            _finishedBuffer = new CommandBuffer();
            _finishedBuffer.name = "RealGlass finished picture";
            _finishedBuffer.Blit(BuiltinRenderTextureType.CameraTarget, _finished, _util, PassCopy);
            _camera.AddCommandBuffer(FinishedWhen, _finishedBuffer);
        }

        private void DropFinished()
        {
            if (_finishedBuffer != null)
            {
                if (_camera != null)
                    _camera.RemoveCommandBuffer(FinishedWhen, _finishedBuffer);
                _finishedBuffer.Release();
                _finishedBuffer = null;
            }

            Free(ref _finished);
            _renders = 0;
        }

        private RenderTexture NewPicture(string name)
        {
            var rt = new RenderTexture(_width, _height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            rt.name = name;
            rt.useMipMap = true;
            rt.autoGenerateMips = true;
            rt.filterMode = FilterMode.Trilinear;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.Create();
            return rt;
        }

        private static RenderTexture NewCity(string name)
        {
            var rt = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            rt.name = name;
            rt.Create();
            return rt;
        }

        /// <summary>Takes the buffers off the camera and frees the targets (unload, resize, off).</summary>
        public void Detach()
        {
            if (_prePass != null)
                _prePass.Detach();
            DropFinished();
            if (_buffer != null)
            {
                if (_camera != null)
                    _camera.RemoveCommandBuffer(When, _buffer);
                _buffer.Release();
                _buffer = null;
            }

            if (_onPreRender != null)
            {
                Camera.onPreRender -= _onPreRender;
                _onPreRender = null;
            }

            Free(ref _scene);
            Free(ref _city);
            Free(ref _cityPrev);
            Free(ref _hiz);
            _hizLevels = 0;
            if (_quad != null)
            {
                Object.DestroyImmediate(_quad);
                _quad = null;
            }
            _camera = null;
            _width = _height = 0;
        }

        private static void Free(ref RenderTexture rt)
        {
            if (rt == null)
                return;
            rt.Release();
            Object.DestroyImmediate(rt);
            rt = null;
        }
    }
}
