using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RealGlass.Water;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// tools/issue-probe: the depth half of GitHub issue #2 (thin lines round trees and land against the
/// void, and at a shore, with Dynamic Resolution) on the MOD'S OWN WaterSurfaceMask, the same probe run
/// on every version (run.ps1 copies it into a worktree of each). Each case is drawn twice: as the old
/// Dynamic Resolution mod draws it (our per-frame code runs with no target on the camera, so it sees the
/// screen's size; the camera is then rendered into a target of screen x factor from that mod's camera's
/// OnRenderImage, IssueProbeDrCamera) and as the same picture with no mismatch (the target already on the
/// camera when our code runs). Any difference between the two is ours.
/// Readers of the depth texture after the water, stand-ins (IssueProbeEffects.shader): the game's fog,
/// Volumetric Weather's clouds, ambient occlusion, depth of field; then tonemap / bloom / sharpen.
/// </summary>
public static class IssueProbe
{
    private static int _frame;
    private static readonly List<string> Lines = new List<string>();
    private static string _out, _version;
    private static Material _fx, _readDepth;
    private static Mesh _cube, _lakeMesh;
    private static Camera _camera, _dummyCamera;
    private static IssueProbeDrCamera _dr;
    private static Shader _maskShader;
    private static int _screenW, _screenH;
    private static Matrix4x4 _lake;
    private static MaterialPropertyBlock _block;
    private static readonly Dictionary<string, Texture2D> Solids = new Dictionary<string, Texture2D>();
    private static bool _nested;

    // The probes on the game's camera.
    private static CommandBuffer _afterProbe, _fogBuffer, _aoBuffer, _dofBuffer;
    private static RenderTexture _after;   // the depth texture as the passes after the water read it, per render

    private sealed class Shot
    {
        public int W, H;           // the render
        public Color[] Depth;      // the depth texture after the water (render's size)
        public Color[] Display;    // what the screen shows (screen's size, tonemapped)
        public int CopyW, CopyH;   // our copies' size at that render
    }

    public static void Run()
    {
        _out = Environment.GetEnvironmentVariable("ISSUE_OUT");
        _version = Environment.GetEnvironmentVariable("ISSUE_VERSION") ?? "unknown";
        if (string.IsNullOrEmpty(_out))
            _out = Path.Combine("..", "issue-probe-out");
        Directory.CreateDirectory(_out);
        WaterSurfaceMask.Frame = () => _frame;
        bool ok = false;
        try
        {
            Probe();
            ok = true;
        }
        catch (Exception e)
        {
            Lines.Add("EXCEPTION " + e);
        }
        Lines.Add(ok ? "ISSUE PROBE OK" : "ISSUE PROBE FAILED");
        File.WriteAllLines(Path.Combine(_out, "depth-" + _version + ".txt"), Lines.ToArray());
    }

    private static void Probe()
    {
        PlayerSettings.colorSpace = ColorSpace.Linear;
        _maskShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/RealGlass/WaterSurfaceMask.shader");
        _fx = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/Editor/IssueProbeEffects.shader"));
        _readDepth = new Material(_maskShader);
        _cube = Cube();
        _lakeMesh = Grid();
        _block = new MaterialPropertyBlock();
        BuildScene();

        _camera.targetTexture = null;
        _screenW = _camera.pixelWidth;
        _screenH = _camera.pixelHeight;
        Lines.Add("version " + _version + "; the screen (the camera with no target) is " + _screenW + " x " + _screenH +
                  "; " + SystemInfo.graphicsDeviceType + "; reversed z " + SystemInfo.usesReversedZBuffer);

        _afterProbe = new CommandBuffer { name = "probe: the depth texture after the water" };
        _fogBuffer = new CommandBuffer { name = "stand-in: the game's fog and VW's clouds" };
        _fogBuffer.DrawMesh(_cube, Matrix4x4.identity, _fx, 0, 0);
        _fogBuffer.DrawMesh(_cube, Matrix4x4.identity, _fx, 0, 1);
        _aoBuffer = FullScreen("stand-in: ambient occlusion", 2);
        _dofBuffer = FullScreen("stand-in: depth of field", 3);
        _camera.AddCommandBuffer(CameraEvent.AfterImageEffectsOpaque, _afterProbe);
        _camera.AddCommandBuffer(CameraEvent.AfterImageEffectsOpaque, _fogBuffer);
        Shader.SetGlobalColor("_ProbeFogColour", new Color(0.55f, 0.58f, 0.62f, 1f));
        Shader.SetGlobalColor("_ProbeCloudColour", new Color(0.62f, 0.58f, 0.55f, 1f));
        Shader.SetGlobalVector("_ProbeAo", new Vector4(6f, 0.7f, 0f, 0f));

        // The old mod's camera: depth -3, culling mask 0, its OnRenderImage renders the game's.
        _dummyCamera = new GameObject("probe: Dynamic Resolution's camera").AddComponent<Camera>();
        _dummyCamera.enabled = false;
        _dummyCamera.cullingMask = 0;
        _dummyCamera.clearFlags = CameraClearFlags.SolidColor;
        _dummyCamera.depth = -3f;
        _dr = _dummyCamera.gameObject.AddComponent<IssueProbeDrCamera>();
        _dr.Game = _camera;

        string[] views = { "edge", "shore", "aerial" };
        float[] factors = { 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f, 2.11f, 3f };
        string[] effects = { "fog", "fog+ao", "fog+dof", "fog+post", "all" };

        Lines.Add("ROW\tversion\tview\tfactor\tmode\teffects\tmask\tframe\trender\tcopies\tdepthDiffer\tvoidAsNear\tnearAsVoid\tshownOver2\tshownOver16\tbluer\tinBand");
        foreach (string view in views)
            foreach (float factor in factors)
                foreach (string fx in effects)
                    Case(view, factor, "single", fx, true, false);

        // Without our mask (vanilla water, or Realistic water off): Dynamic Resolution alone.
        foreach (string view in views)
            foreach (float factor in factors)
                Case(view, factor, "single", "all", false, false);

        // Realistic depth on (the seabed's frame copied too).
        foreach (string view in views)
            foreach (float factor in new[] { 1f, 2.11f })
                Case(view, factor, "single", "all", true, true);

        // Two renders a frame (Lumina's and the old mod's both on), the old mod's Apply, a change of factor.
        foreach (string view in views)
            foreach (float factor in new[] { 0.5f, 1f, 2f, 2.11f })
            {
                Case(view, factor, "twice", "all", true, false);
                Case(view, factor, "twice-mixed", "all", true, false);
                Case(view, factor, "apply", "all", true, false);
                Case(view, factor, "change", "all", true, false);
            }

        Lines.Add("nested in the other camera's OnRenderImage: " + (_nested ? "yes" : "NO (rendered directly)") + " (" + _dr.Ran + " calls)");
    }

    // ---------------------------------------------------------------------------------------------

    private static void Case(string view, float factor, string mode, string effects, bool useMask, bool bed)
    {
        SetView(view);
        SetEffects(effects);
        int w = Mathf.Max(16, Mathf.RoundToInt(_screenW * factor)), h = Mathf.Max(16, Mathf.RoundToInt(_screenH * factor));
        // The second size: Lumina's render before the old mod's (twice-mixed), or the size after the change.
        float factor2 = mode == "change" ? factor * 1.5f : factor * 0.7f;
        int w2 = Mathf.Max(16, Mathf.RoundToInt(_screenW * factor2)), h2 = Mathf.Max(16, Mathf.RoundToInt(_screenH * factor2));

        var cache = new Dictionary<string, Shot>();
        Shot reference = Reference(w, h, useMask, bed);
        cache[w + "x" + h] = reference;
        if (mode == "twice-mixed" || mode == "change")
            cache[w2 + "x" + h2] = Reference(w2, h2, useMask, bed);

        WaterSurfaceMask mask = useMask ? NewMask(bed) : null;
        RenderTexture target = Target(w, h), target2 = (mode == "twice-mixed" || mode == "change") ? Target(w2, h2) : null;
        int frames = mode == "change" ? 6 : 4;
        try
        {
            for (int frame = 1; frame <= frames; frame++)
            {
                RenderTexture[] renders;
                if (mode == "twice")
                    renders = new[] { target, target };
                else if (mode == "twice-mixed")
                    renders = new[] { target2, target };
                else if (mode == "change" && frame > 3)
                    renders = new[] { target2 };
                else
                    renders = new[] { target };

                // Our LateUpdate. The old mod's Apply leaves its new target on the camera until its next
                // render: one LateUpdate sees it.
                _camera.targetTexture = (mode == "apply" && frame == 2) ? renders[renders.Length - 1] : null;
                if (mask != null)
                    mask.Attach(_camera);
                _camera.targetTexture = null;
                // The game's water draws after our LateUpdate (the other order loses a frame's water at
                // a change of size: reported apart, frame 1 of a size).
                Record(mask);

                List<Shot> shots = Draw(renders, mask);
                for (int r = 0; r < shots.Count; r++)
                {
                    Shot s = shots[r];
                    Shot truth;
                    if (!cache.TryGetValue(s.W + "x" + s.H, out truth))
                        continue;
                    // Only the last render reaches the screen.
                    bool shown = r == shots.Count - 1;
                    Compare(view, factor, mode, effects, useMask, bed, frame, r, s, truth, shown);
                    if (shown && frame == 3 && mode == "single" && effects == "all" && useMask && !bed &&
                        (Mathf.Approximately(factor, 2.11f) || Mathf.Approximately(factor, 0.5f)))
                        SavePictures(view, factor, s, truth);
                }
            }
        }
        finally
        {
            if (mask != null) mask.Destroy();
            target.Release();
            if (target2 != null) target2.Release();
        }
    }

    // The same picture with no mismatch: the target on the camera when our per-frame code runs.
    private static Shot Reference(int w, int h, bool useMask, bool bed)
    {
        WaterSurfaceMask mask = useMask ? NewMask(bed) : null;
        RenderTexture target = Target(w, h);
        Shot shot = null;
        try
        {
            for (int frame = 1; frame <= 3; frame++)
            {
                _camera.targetTexture = target;
                if (mask != null)
                    mask.Attach(_camera);
                Record(mask);
                shot = Render(target, mask);
                _camera.targetTexture = null;
            }
        }
        finally
        {
            if (mask != null) mask.Destroy();
            target.Release();
        }
        return shot;
    }

    private static WaterSurfaceMask NewMask(bool bed)
    {
        var mask = new WaterSurfaceMask(_maskShader);
        mask.BedWanted = bed;
        return mask;
    }

    // One frame of the old mod: its camera's OnRenderImage renders ours into each target in turn.
    private static List<Shot> Draw(RenderTexture[] targets, WaterSurfaceMask mask)
    {
        var shots = new List<Shot>();
        var screen = Target(_screenW, _screenH);
        _dr.Targets = targets;
        _dr.AfterRender = i =>
        {
            Shot s = Capture(targets[i], mask);
            shots.Add(s);
            if (i + 1 < targets.Length)
                ProbeFor(targets[i + 1].width, targets[i + 1].height);
        };
        ProbeFor(targets[0].width, targets[0].height);
        int before = _dr.Ran;
        _dummyCamera.targetTexture = screen;
        _dummyCamera.Render();
        _dummyCamera.targetTexture = null;
        if (_dr.Ran > before)
            _nested = true;
        else
        {
            // OnRenderImage did not run in this editor: the same sequence by hand.
            for (int i = 0; i < targets.Length; i++)
            {
                ProbeFor(targets[i].width, targets[i].height);
                _camera.targetTexture = targets[i];
                _camera.Render();
                _camera.targetTexture = null;
                shots.Add(Capture(targets[i], mask));
            }
        }
        _dr.AfterRender = null;
        screen.Release();
        return shots;
    }

    private static Shot Render(RenderTexture target, WaterSurfaceMask mask)
    {
        ProbeFor(target.width, target.height);
        _camera.targetTexture = target;
        _camera.Render();
        _camera.targetTexture = null;
        return Capture(target, mask);
    }

    // What this render left: the depth texture after the water, and the picture as the screen shows it
    // (downsampled to the screen as the old mod does, then the post chain).
    private static Shot Capture(RenderTexture target, WaterSurfaceMask mask)
    {
        var s = new Shot { W = target.width, H = target.height };
        s.Depth = Read(_after);
        if (mask != null && mask.SceneDepth != null)
        {
            s.CopyW = mask.SceneDepth.width;
            s.CopyH = mask.SceneDepth.height;
        }
        RenderTexture screen = RenderTexture.GetTemporary(_screenW, _screenH, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        screen.filterMode = FilterMode.Bilinear;
        Graphics.Blit(target, screen);
        RenderTexture shown = RenderTexture.GetTemporary(_screenW, _screenH, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        RenderTexture bright = RenderTexture.GetTemporary(_screenW / 2, _screenH / 2, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        RenderTexture blur = RenderTexture.GetTemporary(_screenW / 2, _screenH / 2, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        Graphics.Blit(screen, bright, _fx, 5);
        Graphics.Blit(bright, blur, _fx, 6);
        _fx.SetTexture("_BloomTex", blur);
        Graphics.Blit(screen, shown, _fx, 4);
        s.Display = Read(shown);
        RenderTexture.ReleaseTemporary(screen);
        RenderTexture.ReleaseTemporary(shown);
        RenderTexture.ReleaseTemporary(bright);
        RenderTexture.ReleaseTemporary(blur);
        return s;
    }

    private static void Compare(string view, float factor, string mode, string effects, bool useMask, bool bed, int frame, int render,
                                Shot s, Shot truth, bool shown)
    {
        int differ = 0, voidAsNear = 0, nearAsVoid = 0;
        for (int i = 0; i < s.Depth.Length; i++)
        {
            float a = s.Depth[i].r, b = truth.Depth[i].r;
            if (Mathf.Abs(a - b) > 1e-6f)
            {
                differ++;
                bool aFar = Far(a), bFar = Far(b);
                if (bFar && !aFar) voidAsNear++;
                else if (!bFar && aFar) nearAsVoid++;
            }
        }

        int over2 = -1, over16 = -1, bluer = -1, inBand = -1;
        if (shown)
        {
            bool[] band = Band(truth);
            over2 = 0; over16 = 0; bluer = 0; inBand = 0;
            for (int y = 0; y < _screenH; y++)
                for (int x = 0; x < _screenW; x++)
                {
                    int i = y * _screenW + x;
                    Color p = s.Display[i], q = truth.Display[i];
                    float d = Mathf.Max(Mathf.Abs(p.r - q.r), Mathf.Max(Mathf.Abs(p.g - q.g), Mathf.Abs(p.b - q.b)));
                    if (d > 2f / 255f) over2++;
                    if (d > 16f / 255f)
                    {
                        over16++;
                        int bx = Mathf.Min(truth.W - 1, (int)((x + 0.5f) * truth.W / _screenW));
                        int by = Mathf.Min(truth.H - 1, (int)((y + 0.5f) * truth.H / _screenH));
                        if (band[by * truth.W + bx]) inBand++;
                    }
                    if (p.b - q.b > 8f / 255f && p.b - q.b > p.r - q.r) bluer++;
                }
        }
        Lines.Add(string.Join("\t", new[]
        {
            "ROW", _version, view, factor.ToString("0.00", CultureInfo.InvariantCulture), mode, effects,
            useMask ? (bed ? "mask+bed" : "mask") : "none", frame.ToString(), render + ":" + s.W + "x" + s.H,
            s.CopyW + "x" + s.CopyH, differ.ToString(), voidAsNear.ToString(), nearAsVoid.ToString(),
            over2.ToString(), over16.ToString(), bluer.ToString(), inBand.ToString()
        }));
    }

    // Pixels within 2 px of an outline in the depth (a 5% step in eye depth to a neighbour).
    private static bool[] Band(Shot t)
    {
        int w = t.W, h = t.H;
        var edge = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float e = Eye(t.Depth[y * w + x].r);
                if ((x + 1 < w && Step(e, Eye(t.Depth[y * w + x + 1].r))) || (y + 1 < h && Step(e, Eye(t.Depth[(y + 1) * w + x].r))))
                    edge[y * w + x] = true;
            }
        var band = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!edge[y * w + x]) continue;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx >= 0 && yy >= 0 && xx < w && yy < h) band[yy * w + xx] = true;
                    }
            }
        return band;
    }

    private static bool Step(float a, float b)
    {
        return Mathf.Abs(a - b) > 0.05f * Mathf.Min(a, b);
    }

    // ---------------------------------------------------------------------------------------------

    private static void BuildScene()
    {
        _camera = new GameObject("probe: the game's camera").AddComponent<Camera>();
        _camera.enabled = false;
        _camera.renderingPath = RenderingPath.DeferredLighting;
        _camera.allowHDR = true;
        _camera.allowMSAA = false;
        _camera.depthTextureMode = DepthTextureMode.Depth;
        _camera.clearFlags = CameraClearFlags.SolidColor;
        _camera.backgroundColor = new Color(0.30f, 0.55f, 0.95f, 1f);   // the raw background: where the fog misses the void, blue
        _camera.nearClipPlane = 1f;
        _camera.farClipPlane = 4000f;
        _camera.fieldOfView = 45f;

        var sun = new GameObject("probe: sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.1f;
        sun.transform.rotation = Quaternion.Euler(40f, 150f, 0f);
        RenderSettings.ambientLight = new Color(0.35f, 0.38f, 0.42f);

        Material ground = Lit(new Color(0.42f, 0.40f, 0.33f));
        Material grass = Lit(new Color(0.25f, 0.38f, 0.18f));
        Material leaves = Lit(new Color(0.12f, 0.30f, 0.10f));
        Material bark = Lit(new Color(0.30f, 0.22f, 0.15f));
        Material wall = Lit(new Color(0.55f, 0.55f, 0.58f));

        // The map: ground to z = 300, then nothing (the void past the map's edge).
        Box(new Vector3(0f, -0.5f, -100f), new Vector3(1600f, 1f, 800f), Quaternion.identity, ground);
        // A hill and trees along the map's edge.
        Prim(PrimitiveType.Sphere, new Vector3(150f, -40f, 255f), new Vector3(170f, 120f, 90f), Quaternion.identity, grass);
        var random = new System.Random(7);
        for (int i = 0; i < 46; i++)
        {
            float x = -320f + 14f * i + (float)random.NextDouble() * 6f, z = 284f + (i % 3) * 5f;
            float height = 7f + (float)random.NextDouble() * 9f, crown = 3f + (float)random.NextDouble() * 3.5f;
            Prim(PrimitiveType.Cylinder, new Vector3(x, height * 0.5f, z), new Vector3(0.8f, height * 0.5f, 0.8f), Quaternion.Euler(0f, 0f, (float)random.NextDouble() * 6f - 3f), bark);
            Prim(PrimitiveType.Sphere, new Vector3(x, height + crown * 0.4f, z), new Vector3(crown * 2f, crown * 2.4f, crown * 2f), Quaternion.identity, leaves);
        }
        // A few buildings.
        for (int i = 0; i < 6; i++)
            Box(new Vector3(260f + 40f * i, 30f + 10f * (i % 3), 120f), new Vector3(22f, 60f + 20f * (i % 3), 22f), Quaternion.identity, wall);

        // The lake (the water only in our mask, at 3 m over the ground, as the game's draws record it),
        // its far bank sloping up out of the water, posts standing at the waterline.
        _lake = Matrix4x4.TRS(new Vector3(-250f, 0f, 100f), Quaternion.identity, Vector3.one);
        Box(new Vector3(-250f, 2f, 252f), new Vector3(120f, 14f, 30f), Quaternion.Euler(-18f, 0f, 0f), grass);
        for (int i = 0; i < 7; i++)
            Prim(PrimitiveType.Cylinder, new Vector3(-280f + 10f * i, 5f, 237f), new Vector3(0.6f, 5f, 0.6f), Quaternion.identity, bark);
        // Its side banks: the ground higher than the water on both sides.
        Box(new Vector3(-310f, 2f, 100f), new Vector3(40f, 8f, 300f), Quaternion.identity, grass);
        Box(new Vector3(-190f, 2f, 100f), new Vector3(40f, 8f, 300f), Quaternion.identity, grass);
    }

    private static void SetView(string view)
    {
        Vector3 at, from;
        switch (view)
        {
            case "edge": from = new Vector3(0f, 18f, 140f); at = new Vector3(0f, 10f, 400f); break;
            case "shore": from = new Vector3(-250f, 5.5f, 0f); at = new Vector3(-250f, 4f, 240f); break;
            default: from = new Vector3(-120f, 260f, -160f); at = new Vector3(-60f, 0f, 220f); break;
        }
        _camera.transform.position = from;
        _camera.transform.LookAt(at);
        float focus = Vector3.Distance(from, at);
        Shader.SetGlobalVector("_ProbeDof", new Vector4(focus * 0.6f, focus * 0.5f, 5f, 0f));
    }

    private static void SetEffects(string effects)
    {
        bool ao = effects == "fog+ao" || effects == "all", dof = effects == "fog+dof" || effects == "all";
        bool post = effects == "fog+post" || effects == "all";
        _camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, _aoBuffer);
        _camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, _dofBuffer);
        if (ao) _camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, _aoBuffer);
        if (dof) _camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, _dofBuffer);
        // Exposure, sharpen, bloom threshold, bloom amount (without the chain: the tonemap alone, to see).
        Shader.SetGlobalVector("_ProbePost", post ? new Vector4(1.6f, 0.6f, 0.8f, 0.35f) : new Vector4(1.6f, 0f, 100f, 0f));
    }

    private static CommandBuffer FullScreen(string name, int pass)
    {
        var cb = new CommandBuffer { name = name };
        int id = Shader.PropertyToID("_ProbeTemp" + pass);
        cb.GetTemporaryRT(id, -1, -1, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        cb.Blit(BuiltinRenderTextureType.CameraTarget, id);
        cb.Blit(id, BuiltinRenderTextureType.CameraTarget, _fx, pass);
        cb.ReleaseTemporaryRT(id);
        return cb;
    }

    private static RenderTexture Target(int w, int h)
    {
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        rt.filterMode = FilterMode.Bilinear;
        rt.Create();
        return rt;
    }

    // The probe blits into a target of the render's own size: re-made per render size.
    private static void ProbeFor(int w, int h)
    {
        if (_after == null || _after.width != w || _after.height != h)
        {
            if (_after != null) _after.Release();
            _after = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point };
            _after.Create();
        }
        _afterProbe.Clear();
        _afterProbe.Blit(BuiltinRenderTextureType.CameraTarget, _after, _readDepth, 1);
        _afterProbe.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
    }

    // A new frame's water draws (none without the mask), as the game's water patches record them.
    private static void Record(WaterSurfaceMask mask)
    {
        _frame++;
        if (mask == null) return;
        Bind(3f, true);
        mask.Draw(_lakeMesh, _lake, 0, null, 0, _block);
        Bind(900f, false);
    }

    private static void Bind(float height, bool wet)
    {
        _block.SetTexture("_MainTex", Solid(new Color(1f, 0f, 1f, 0f)));
        _block.SetVector("_HeightMapping", new Vector4(0.25f, height / 0.996094f, 0.25f, 0f));
        _block.SetTexture("_TerrainHeight", Solid(Color.clear));
        _block.SetVector("_TerrainMapping", new Vector4(0.25f, 1024f, 0.25f, 0f));
        _block.SetVector("_SurfaceMapping", new Vector4(0.5f, 0.5f, 0.001f, 0f));
        _block.SetVector("_TerrainStatus", new Vector4(0f, 0.5f, 0f, 0f));
        Texture2D a = Solid(new Color(0.5f, 0f, 0.5f, 1f));
        Texture2D b = Solid(new Color(0.5f, wet ? 1f : 0f, 0.5f, 1f));
        _block.SetTexture("_SurfaceAOld", a);
        _block.SetTexture("_SurfaceANew", a);
        _block.SetTexture("_SurfaceBOld", b);
        _block.SetTexture("_SurfaceBNew", b);
    }

    private static Texture2D Solid(Color c)
    {
        string key = c.ToString("F4");
        Texture2D t;
        if (Solids.TryGetValue(key, out t)) return t;
        t = new Texture2D(4, 4, TextureFormat.RGBAFloat, false, true);
        var pixels = new Color[16];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = c;
        t.SetPixels(pixels);
        t.Apply();
        t.wrapMode = TextureWrapMode.Clamp;
        Solids[key] = t;
        return t;
    }

    private static Mesh Grid()
    {
        const int nx = 16, nz = 56;
        var vertices = new Vector3[(nx + 1) * (nz + 1)];
        var indices = new int[nx * nz * 6];
        for (int z = 0; z <= nz; z++)
            for (int x = 0; x <= nx; x++)
                vertices[z * (nx + 1) + x] = new Vector3(-40f + 80f * x / nx, 0f, -140f + 280f * z / nz);
        int k = 0;
        for (int z = 0; z < nz; z++)
            for (int x = 0; x < nx; x++)
            {
                int a = z * (nx + 1) + x, b = a + nx + 1;
                indices[k++] = a; indices[k++] = b; indices[k++] = a + 1;
                indices[k++] = a + 1; indices[k++] = b; indices[k++] = b + 1;
            }
        return new Mesh { vertices = vertices, triangles = indices };
    }

    private static Mesh Cube()
    {
        var mesh = new Mesh();
        mesh.vertices = new[]
        {
            new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(1, 1, -1), new Vector3(-1, 1, -1),
            new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(1, 1, 1), new Vector3(-1, 1, 1),
        };
        mesh.triangles = new[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5 };
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
        return mesh;
    }

    private static Material Lit(Color c)
    {
        var m = new Material(Shader.Find("Legacy Shaders/Diffuse"));
        m.color = c;
        return m;
    }

    private static void Box(Vector3 at, Vector3 size, Quaternion turn, Material m)
    {
        Prim(PrimitiveType.Cube, at, size, turn, m);
    }

    private static void Prim(PrimitiveType type, Vector3 at, Vector3 size, Quaternion turn, Material m)
    {
        GameObject g = GameObject.CreatePrimitive(type);
        g.transform.position = at;
        g.transform.localScale = size;
        g.transform.rotation = turn;
        g.GetComponent<Renderer>().sharedMaterial = m;
    }

    private static Color[] Read(RenderTexture texture)
    {
        var copy = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        Graphics.Blit(texture, copy);
        RenderTexture.active = copy;
        var read = new Texture2D(texture.width, texture.height, TextureFormat.RGBAFloat, false, true);
        read.ReadPixels(new Rect(0f, 0f, texture.width, texture.height), 0, 0);
        Color[] pixels = read.GetPixels();
        UnityEngine.Object.DestroyImmediate(read);
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(copy);
        return pixels;
    }

    private static float Eye(float raw)
    {
        float n = _camera.nearClipPlane, f = _camera.farClipPlane;
        float x = SystemInfo.usesReversedZBuffer ? -1f + f / n : 1f - f / n;
        float y = SystemInfo.usesReversedZBuffer ? 1f : f / n;
        return 1f / (x / f * raw + y / f);
    }

    private static bool Far(float raw)
    {
        return Eye(raw) > 0.98f * _camera.farClipPlane;
    }

    // The screen's picture of one case: the reference, ours, and their difference x4, side by side.
    private static void SavePictures(string view, float factor, Shot s, Shot truth)
    {
        int w = _screenW, h = _screenH;
        var tex = new Texture2D(w * 3, h, TextureFormat.RGB24, false);
        var px = new Color[w * 3 * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Color q = truth.Display[y * w + x], p = s.Display[y * w + x];
                px[y * w * 3 + x] = q;
                px[y * w * 3 + w + x] = p;
                Color d = new Color(Mathf.Abs(p.r - q.r) * 4f, Mathf.Abs(p.g - q.g) * 4f, Mathf.Abs(p.b - q.b) * 4f);
                px[y * w * 3 + 2 * w + x] = d;
            }
        tex.SetPixels(px);
        tex.Apply();
        string name = "depth-" + _version + "-" + view + "-x" + factor.ToString("0.00", CultureInfo.InvariantCulture) + ".png";
        File.WriteAllBytes(Path.Combine(_out, name), tex.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(tex);
    }
}
