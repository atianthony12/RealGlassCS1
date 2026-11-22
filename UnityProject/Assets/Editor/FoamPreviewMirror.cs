using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// THE MIRROR MEASUREMENT (FoamPreview "mirrorprobe 1", 2026-11-21; preview only). The author's report at
// the mod's defaults: seen from low over the water, a cliff's reflection under clouds is much darker; with
// the game's own sky in the water it looks paler. Two causes would need different fixes: the city
// reflection's composite taking away the wrong amount of sky (its estimate: one sharp read of the sky
// cube along the flat mirror ray x the rough sea's reflectance), or a correct but dim mirror (the
// reflectance at low angles). Each shot is drawn again with parts switched off, every setting twice (the
// second read), and the composite's terms read through Editor/PreviewMirrorProbe.shader:
//   R     the frame as the mod draws it;
//   Radd  the same with the composite taking nothing away (_RGWaterGeom.z = 0): Radd - W = the light added,
//         Radd - R = the light taken away;
//   W     no city reflection (strength 0): the frame the composite works on;
//   B     no city reflection and the sky layer at 0% (its bed and glitter stay);
//   W0,W1 no city reflection, the water's sky cube bound to a black / a white cube: a = W1 - W0 is the sky
//         layer's alpha (its reflectance x the share), W - W0 the sky light the layer put in.
public static partial class FoamPreview
{
    /// <summary>
    /// THE ONE MIRROR's lag (2026-11-21): the camera turned shot.Pan degrees a frame (or its field of view x
    /// shot.Zoom a frame) for 8 frames, then at rest for 4. Each frame is drawn as it comes (its layer and
    /// composite read the frame before's coverage: the lag) and again at the same pose (settled: the frame
    /// before is this pose); logged per frame, the pixels whose luma code differs by more than 2 between the two,
    /// their mean difference and the worst. Today's water, H0 with either composite pick, H1, P. Pictures at
    /// frame 3: the lagged frame and |lagged - settled| x 10 (<shot>_pan_<variant>.png / _pandiff_).
    /// </summary>
    private static void PanLag(Scene scene, Shot shot, float exposure, string outDir, List<string> log)
    {
        Transform t = scene.CameraObject.transform;
        Quaternion q0 = t.rotation;
        float fov0 = scene.Camera.fieldOfView, pick0 = scene.OneMirrorPick;
        RenderTexture target = scene.Target;
        int w = target.width, h = target.height, n = w * h;
        const int move = 8, rest = 4;
        var combos = new[] { new[] { 0f, 1f }, new[] { 1f, 1f }, new[] { 1f, 0f }, new[] { 2f, 1f }, new[] { 3f, 1f } };
        string[] names = { "today", "H0 (i)", "H0 (ii)", "H1 (i)", "P" };
        Action<int> pose = g =>
        {
            t.rotation = Quaternion.AngleAxis(shot.Pan * g, Vector3.up) * q0;
            scene.Camera.fieldOfView = fov0 * Mathf.Pow(shot.Zoom, g);
        };
        for (int c = 0; c < combos.Length; c++)
        {
            scene.OneMirrorPick = combos[c][1];
            scene.OneMirrorFor((int)combos[c][0], shot.Strength);
            pose(0);
            scene.Frame(1);
            scene.Frame(1);
            var s = new StringBuilder(string.Format(CultureInfo.InvariantCulture, "pan {0} {1} (turn {2} deg, zoom x{3} a frame):", shot.Name, names[c], shot.Pan, shot.Zoom));
            int twiceTotal = 0;
            for (int f = 1; f <= move + rest; f++)
            {
                pose(Mathf.Min(f, move));
                // A dynamic resolution's two renders a frame: the frame starts once, the camera drawn twice; both
                // renders must read the same last frame (0 px apart).
                scene.Frame(1);
                Color[] lag = ReadTarget(target);
                scene.Camera.Render();
                Color[] lagAgain = ReadTarget(target);
                int twiceApart = 0;
                for (int i = 0; i < n; i++)
                {
                    if (lag[i] != lagAgain[i])
                        twiceApart++;
                }
                twiceTotal += twiceApart;
                scene.FrameStart();
                scene.Camera.Render();
                Color[] settled = ReadTarget(target);
                int over = 0;
                double sum = 0.0;
                float most = 0f;
                var diff = f == 3 ? new Color[n] : null;
                var shownLag = f == 3 ? new Color[n] : null;
                for (int i = 0; i < n; i++)
                {
                    Color a = Shown(lag[i], exposure);
                    float d = Mathf.Abs(Code(a) - Code(Shown(settled[i], exposure)));
                    if (d > 2f)
                    {
                        over++;
                        sum += d;
                    }
                    most = Mathf.Max(most, d);
                    if (diff != null)
                    {
                        float e = Mathf.Clamp01(d * 10f / 255f);
                        diff[i] = new Color(e, e, e);
                        shownLag[i] = a;
                    }
                }
                s.Append(string.Format(CultureInfo.InvariantCulture, " f{0}{1}: {2} px, mean {3:F1}, worst {4:F0};", f, f > move ? " (still)" : "", over, over > 0 ? sum / over : 0.0, most));
                if (diff != null)
                {
                    string tag = names[c].Replace(" ", "").Replace("(", "").Replace(")", "");
                    WaterPreview.WritePng(Path.Combine(outDir, shot.Name + "_pan_" + tag + ".png"), shownLag, w, h);
                    WaterPreview.WritePng(Path.Combine(outDir, shot.Name + "_pandiff_" + tag + ".png"), diff, w, h);
                }
            }
            s.Append(" two renders a frame: " + twiceTotal + " px apart over all frames");
            log.Add(s.ToString());
        }
        pose(0);
        scene.OneMirrorPick = pick0;
        scene.OneMirrorFor(0, shot.Strength);
    }

    private static Color[] ReadTarget(RenderTexture rt)
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = rt;
        var texture = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false, true);
        texture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        RenderTexture.active = previous;
        Color[] px = texture.GetPixels();
        UnityEngine.Object.DestroyImmediate(texture);
        return px;
    }

    private static float Code(Color shown)
    {
        return 255f * (0.2126f * shown.r + 0.7152f * shown.g + 0.0722f * shown.b);
    }

    private sealed class MirrorMasks
    {
        public int W, H;
        public bool[] Covered, Bright, Dark, Open, Sky;
        public float Threshold;
    }

    private sealed class MirrorProbe
    {
        public readonly Material Material;
        public readonly CommandBuffer Buffer;
        public readonly RenderTexture Terms, Added;
        public RenderTexture[] Resizable;
        private readonly Cubemap _black, _white;
        private readonly RenderTexture _cubeRead;
        private readonly int _screenW, _screenH;
        private readonly Dictionary<string, MirrorMasks> _masks = new Dictionary<string, MirrorMasks>();
        private readonly Dictionary<string, float> _thresholds = new Dictionary<string, float>();

        public MirrorProbe(int width, int height, Mesh cube, RenderTexture frame, RenderTexture surfaceDepth)
        {
            _screenW = width;
            _screenH = height;
            Material = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/Editor/PreviewMirrorProbe.shader"));
            Material.SetTexture("_RGWaterFrame", frame);
            Material.SetTexture("_RGWaterSurfaceDepth", surfaceDepth);
            Terms = FloatTarget(width, height);
            Added = FloatTarget(width, height);
            Buffer = new CommandBuffer { name = "RealGlass the mirror measurement's probe (preview)" };
            Buffer.SetRenderTarget(Terms);
            Buffer.ClearRenderTarget(false, true, new Color(-1f, 0f, 0f, 0f));
            Buffer.DrawMesh(cube, Matrix4x4.identity, Material, 0, 0);
            Buffer.SetRenderTarget(Added);
            Buffer.ClearRenderTarget(false, true, new Color(0f, 0f, 0f, -1f));
            Buffer.DrawMesh(cube, Matrix4x4.identity, Material, 0, 1);
            Buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            _black = WaterPreview.Cube(new Color(0f, 0f, 0f, 1f));
            _white = WaterPreview.Cube(new Color(1f, 1f, 1f, 1f));
            _cubeRead = FloatTarget(8, 1);
        }

        private static RenderTexture FloatTarget(int width, int height)
        {
            var rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            return rt;
        }

        private void Resize(int width, int height)
        {
            var all = new List<RenderTexture>(Resizable) { Terms, Added };
            foreach (RenderTexture rt in all)
            {
                if (rt.width == width && rt.height == height)
                    continue;
                rt.Release();
                rt.width = width;
                rt.height = height;
                rt.Create();
            }
        }

        private Texture _bound;

        private void Sync(Material reflection)
        {
            Material.SetVector("_RGWaterGeom", reflection.GetVector("_RGWaterGeom"));
            Material.SetVector("_RGWaterLight", reflection.GetVector("_RGWaterLight"));
            Material.SetTexture("_RGWaterTheme", _bound);
        }

        private static Color[] Read(RenderTexture rt)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            var texture = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false, true);
            texture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            RenderTexture.active = previous;
            Color[] px = texture.GetPixels();
            UnityEngine.Object.DestroyImmediate(texture);
            return px;
        }

        /// <summary>
        /// The camera drawn for some frames (the history of a setting is the setting's own; the one mirror's
        /// variants read the frame before's coverage), the last read. Each frame starts (Scene.FrameStart) and
        /// renders _perFrame times (a dynamic resolution's two renders a frame read the same last frame).
        /// </summary>
        private Color[] Draw(Scene scene, RenderTexture target, int times)
        {
            Sync(scene.Reflection);
            for (int i = 0; i < times; i++)
                scene.Frame(_perFrame);
            return Read(target);
        }

        private int _perFrame = 1;

        /// <summary>The one mirror's variants measured after today's picture (Shot.OneMirror's numbers; "onemirrorvariants 1,2,3").</summary>
        public int[] Variants = new int[0];

        /// <summary>The surface's eye depth target (the first of Resizable), for the far-water region.</summary>
        private RenderTexture SurfaceDepth { get { return Resizable[0]; } }

        private static float Lum(Color c)
        {
            return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        }

        private static float Code(Color shown)
        {
            return 255f * (0.2126f * shown.r + 0.7152f * shown.g + 0.0722f * shown.b);
        }

        private static string Q(List<float> v)
        {
            if (v.Count == 0)
                return "-";
            v.Sort();
            Func<float, float> at = f => v[Mathf.Clamp(Mathf.RoundToInt(f * (v.Count - 1)), 0, v.Count - 1)];
            return string.Format(CultureInfo.InvariantCulture, "{0:G4}/{1:G4}/{2:G4}", at(0.5f), at(0.1f), at(0.9f));
        }

        private static float Median(List<float> v)
        {
            if (v.Count == 0)
                return float.NaN;
            v.Sort();
            return v[v.Count / 2];
        }

        private sealed class Region
        {
            public readonly Dictionary<string, List<float>> Values = new Dictionary<string, List<float>>();
            public int Count, Capped;

            public void Add(string key, float value)
            {
                if (float.IsNaN(value) || float.IsInfinity(value))
                    return;
                List<float> list;
                if (!Values.TryGetValue(key, out list))
                {
                    list = new List<float>();
                    Values[key] = list;
                }
                list.Add(value);
            }

            public List<float> Get(string key)
            {
                List<float> list;
                return Values.TryGetValue(key, out list) ? list : new List<float>();
            }
        }

        public void Measure(Scene scene, Shot shot, int rw, int rh, bool sizeChange, float exposure, string outDir, List<string> log, int perFrame = 1)
        {
            _perFrame = perFrame;
            _camera = scene.Camera;
            int variantBefore = scene.OneMirror;
            Camera camera = scene.Camera;
            Material reflection = scene.Reflection;
            Vector4 geom = reflection.GetVector("_RGWaterGeom");
            Vector4 sky = Shader.GetGlobalVector("_RGWaterSky");
            Texture bound = scene.Bound;
            _bound = bound;
            Vector4 light = reflection.GetVector("_RGWaterLight");
            log.Add(string.Format(CultureInfo.InvariantCulture, "mirror {0}: the reflection's sky {1} ({2} px), light {3}, geom {4}", shot.Name, bound != null ? bound.name : "NONE",
                bound != null ? bound.width : 0, light, geom));
            bool screen = rw == _screenW && rh == _screenH && perFrame == 1;
            string size = (rw == _screenW && rh == _screenH ? "screen" : (sizeChange ? "x0.5 then x" : "x") + ((float)rw / _screenW).ToString("0.##", CultureInfo.InvariantCulture)) +
                          (perFrame > 1 ? " twice-a-frame" : "");
            RenderTexture target = scene.Target;
            RenderTexture small = null;
            if (!screen)
            {
                target = new RenderTexture(rw, rh, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                target.Create();
            }

            Color[] r1, rr, terms, added, radd, w, b, w0, w1;
            var variantFrames = new List<Color[]>();
            var variantBlack = new List<Color[]>();
            float[] surfaceEye;
            try
            {
                // Today's water for every reference below; the variants after.
                scene.OneMirrorFor(0, geom.y);
                if (sizeChange)
                {
                    // A frame at a quarter of this size (x0.5 of the screen) first, the copies at its size.
                    small = new RenderTexture(rw / 4, rh / 4, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                    small.Create();
                    Resize(rw / 4, rh / 4);
                    camera.targetTexture = small;
                    Sync(reflection);
                    camera.Render();
                }
                Resize(rw, rh);
                camera.targetTexture = target;

                // R: the first frame after the change of size is the one read (sizeChange); else twice.
                r1 = Draw(scene, target, 1);
                rr = sizeChange ? r1 : Draw(scene, target, 1);
                terms = Read(Terms);
                added = Read(Added);
                reflection.SetVector("_RGWaterGeom", new Vector4(geom.x, geom.y, 0f, geom.w));
                radd = Draw(scene, target, 2);
                reflection.SetVector("_RGWaterGeom", new Vector4(geom.x, 0f, geom.z, geom.w));
                w = Draw(scene, target, 2);
                Shader.SetGlobalVector("_RGWaterSky", new Vector4(0f, sky.y, sky.z, sky.w));
                b = Draw(scene, target, 2);
                Shader.SetGlobalVector("_RGWaterSky", sky);
                Shader.SetGlobalTexture("_RGWaterSkyCube", _black);
                w0 = Draw(scene, target, 2);
                Shader.SetGlobalTexture("_RGWaterSkyCube", _white);
                w1 = Draw(scene, target, 2);
                Shader.SetGlobalTexture("_RGWaterSkyCube", bound);
                Color[] depthPx = Read(SurfaceDepth);
                surfaceEye = new float[depthPx.Length];
                for (int i = 0; i < depthPx.Length; i++)
                    surfaceEye[i] = depthPx[i].r;
                // THE ONE MIRROR's variants: each drawn as the shot (two frames: the second reads the first's
                // coverage), then with the water's sky cube black (what is left is all but the sky).
                reflection.SetVector("_RGWaterGeom", geom);
                Shader.SetGlobalVector("_RGWaterSky", sky);
                foreach (int v in Variants)
                {
                    scene.OneMirrorFor(v, geom.y);
                    for (int pass = 0; pass < 2; pass++)
                    {
                        Shader.SetGlobalTexture("_RGWaterSkyCube", pass == 0 ? bound : (Texture)_black);
                        Color[] got;
                        if (sizeChange)
                        {
                            // The first frame after a change of size is the one read: a frame at a quarter of this
                            // size (its copies at that size) first, as today's R above.
                            Draw(scene, target, 1);
                            Resize(rw / 4, rh / 4);
                            camera.targetTexture = small;
                            scene.Frame(1);
                            Resize(rw, rh);
                            camera.targetTexture = target;
                            got = Draw(scene, target, 1);
                        }
                        else
                            got = Draw(scene, target, 2);
                        if (pass == 0)
                            variantFrames.Add(got);
                        else
                            variantBlack.Add(got);
                    }
                    Shader.SetGlobalTexture("_RGWaterSkyCube", bound);
                }
            }
            finally
            {
                scene.OneMirrorFor(variantBefore, geom.y);
                Shader.SetGlobalTexture("_RGWaterSkyCube", bound);
                Shader.SetGlobalVector("_RGWaterSky", sky);
                reflection.SetVector("_RGWaterGeom", geom);
                Sync(reflection);
                camera.targetTexture = scene.Target;
                Resize(_screenW, _screenH);
                if (!screen)
                    UnityEngine.Object.DestroyImmediate(target);
                if (small != null)
                    UnityEngine.Object.DestroyImmediate(small);
            }

            int n = rw * rh;
            float strength = Mathf.Min(geom.y, 1f);
            string label = shot.Name + " " + size;

            // The sky cube along the view's azimuth, at the mip the composite reads it (screen only).
            if (screen)
            {
                Vector3 f = camera.transform.forward;
                f.y = 0f;
                f.Normalize();
                float[] up = { 0.3f, 1f, 3f, 5f, 10f, 20f, 35f, 60f };
                var dirs = new Vector4[8];
                for (int k = 0; k < 8; k++)
                {
                    float e = up[k] * Mathf.Deg2Rad;
                    Vector3 d = f * Mathf.Cos(e) + Vector3.up * Mathf.Sin(e);
                    dirs[k] = new Vector4(d.x, d.y, d.z, geom.w);
                }
                Material.SetVectorArray("_ProbeDirs", dirs);
                Material.SetTexture("_RGWaterTheme", bound);
                Graphics.Blit(Texture2D.blackTexture, _cubeRead, Material, 2);
                Color[] c = Read(_cubeRead);
                var s = new StringBuilder("mirror sky " + shot.Name + ": the water's sky cube along the view (lum at mip " + geom.w.ToString(CultureInfo.InvariantCulture) + "):");
                for (int k = 0; k < 8; k++)
                    s.Append(string.Format(CultureInfo.InvariantCulture, " {0} deg {1:G4};", up[k], Lum(c[k])));
                log.Add(s.ToString());
            }

            // Per pixel: what the composite saw and did.
            var rho = new float[n];
            var cov = new float[n];
            var cl = new float[n];
            var water = new bool[n];
            for (int i = 0; i < n; i++)
            {
                water[i] = terms[i].r >= 0f && added[i].a >= 0f;
                rho[i] = terms[i].r;
                cov[i] = terms[i].a;
                cl[i] = water[i] && rho[i] > 1e-5f && cov[i] > 1e-3f ? Lum(radd[i]) - Lum(w[i]) : 0f;
                cl[i] = water[i] && rho[i] > 1e-5f && cov[i] > 1e-3f ? cl[i] / (rho[i] * cov[i]) : 0f;
            }

            // The masks: from the first measurement of the view at the screen's size (the clear 100% shot of a
            // run), reused for every later shot of that view; at another size made anew with that threshold.
            string key = shot.View + "|" + rw + "x" + rh;
            MirrorMasks m;
            if (!_masks.TryGetValue(key, out m))
            {
                m = new MirrorMasks { W = rw, H = rh, Covered = new bool[n], Bright = new bool[n], Dark = new bool[n], Open = new bool[n] };
                var cs = new List<float>();
                int yLo = rh, yHi = -1, xLo = rw, xHi = -1;
                for (int i = 0; i < n; i++)
                {
                    m.Covered[i] = water[i] && cov[i] > 0.9f;
                    if (m.Covered[i])
                    {
                        cs.Add(cl[i]);
                        yLo = Mathf.Min(yLo, i / rw);
                        yHi = Mathf.Max(yHi, i / rw);
                        xLo = Mathf.Min(xLo, i % rw);
                        xHi = Mathf.Max(xHi, i % rw);
                    }
                }
                // The sky just over the horizon beside the hill: rows 1.5-10% of the frame's height over the
                // reflection's top, columns clear of it, off the water.
                m.Sky = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    int y = i / rw, x = i % rw;
                    m.Sky[i] = !water[i] && y >= yHi + rh * 0.015f && y <= yHi + rh * 0.10f && (x < xLo - rw / 50 || x > xHi + rw / 50);
                }
                float threshold;
                if (!_thresholds.TryGetValue(shot.View, out threshold))
                {
                    cs.Sort();
                    float p10 = cs.Count > 0 ? cs[cs.Count / 10] : 0f, p90 = cs.Count > 0 ? cs[cs.Count * 9 / 10] : 0f;
                    threshold = Mathf.Sqrt(Mathf.Max(p10, 1e-6f) * Mathf.Max(p90, 1e-6f));
                    _thresholds[shot.View] = threshold;
                }
                m.Threshold = threshold;
                for (int i = 0; i < n; i++)
                {
                    m.Bright[i] = m.Covered[i] && cl[i] > threshold;
                    m.Dark[i] = m.Covered[i] && cl[i] <= threshold;
                    int y = i / rw;
                    m.Open[i] = water[i] && cov[i] < 0.05f && y >= yLo && y <= yHi;
                }
                _masks[key] = m;
                log.Add(string.Format(CultureInfo.InvariantCulture, "mirror masks {0} ({1}): from {2}; covered > 0.9 on {3} px, bright / dark split at C = {4:G4}, open water beside it {5} px",
                    shot.View, size, shot.Name, Count(m.Covered), threshold, Count(m.Open)));
                if (screen)
                {
                    var mask = new Color[n];
                    for (int i = 0; i < n; i++)
                        mask[i] = m.Bright[i] ? new Color(1f, 0.3f, 0.2f) : m.Dark[i] ? new Color(0.2f, 0.4f, 1f) : m.Open[i] ? new Color(0.2f, 0.8f, 0.3f) : Shown(rr[i], exposure) * 0.4f;
                    WaterPreview.WritePng(Path.Combine(outDir, "mask-" + shot.View.Replace('@', '_') + ".png"), mask, rw, rh);
                }
            }

            var bright = new Region();
            var dark = new Region();
            var all = new Region();
            var open = new List<float>();
            var skyBand = new List<float>();
            var check = new Region();
            int below = 0, belowCovered = 0, noisy = 0;
            float noise = 0f, lowest = 0f;
            var intended = screen ? new Color[n] : null;
            var estMap = screen ? new Color[n] : null;
            float[] bins = { 1f, 3f, 5f, 10f, 20f, 35f };
            var byAngle = new Region[bins.Length];
            for (int k = 0; k < bins.Length; k++)
                byAngle[k] = new Region();

            for (int i = 0; i < n; i++)
            {
                float minR = Mathf.Min(rr[i].r, Mathf.Min(rr[i].g, rr[i].b));
                if (minR < -1e-4f)
                {
                    below++;
                    if (m.Covered[i])
                        belowCovered++;
                }
                lowest = Mathf.Min(lowest, minR);
                float dn = Mathf.Abs(Lum(rr[i]) - Lum(r1[i]));
                noise = Mathf.Max(noise, dn);
                if (dn > 1e-4f)
                    noisy++;

                Color a3 = w1[i] - w0[i];
                float a = Lum(a3);
                Color skyIn = w[i] - w0[i];
                Color addedLight = radd[i] - w[i];
                Color iPx = rr[i];
                if (water[i] && rho[i] > 1e-5f)
                    iPx = w[i] - skyIn * cov[i] + addedLight * (a / rho[i]);
                if (screen)
                    intended[i] = Shown(iPx, exposure);
                if (m.Open[i])
                    open.Add(Code(Shown(rr[i], exposure)));
                if (m.Sky[i])
                    skyBand.Add(Code(Shown(rr[i], exposure)));
                if (screen)
                    estMap[i] = Shown(rr[i], exposure) * 0.35f;
                if (!m.Covered[i])
                    continue;

                float lumSky = terms[i].g, schlick = terms[i].b, lumBase = added[i].a;
                float share = Mathf.Min(geom.z, 1f / Mathf.Max(rho[i], 1e-4f));
                float taken = cov[i] * strength * share * rho[i] * lumSky;
                float pre = taken / Mathf.Max(lumBase, 1e-6f);
                float truth = cov[i] * Lum(skyIn);
                float c = cl[i];
                float mu = 1f - Mathf.Pow(Mathf.Clamp01((schlick - 0.0204f) / 0.9796f), 0.2f);
                float angle = Mathf.Asin(Mathf.Clamp01(mu)) * Mathf.Rad2Deg;
                Color cColour = addedLight * (1f / Mathf.Max(rho[i] * cov[i], 1e-6f));
                float codeR = Code(Shown(rr[i], exposure)), codeC = Code(Shown(cColour, exposure)), codeI = Code(Shown(iPx, exposure));

                Region region = m.Bright[i] ? bright : dark;
                foreach (Region g in new[] { region, all })
                {
                    g.Count++;
                    if (pre >= 0.98f)
                        g.Capped++;
                    g.Add("R", Lum(rr[i]));
                    g.Add("C", c);
                    g.Add("rho", rho[i]);
                    g.Add("B", Lum(b[i]));
                    g.Add("W", Lum(w[i]));
                    g.Add("W0", Lum(w0[i]));
                    g.Add("a", a);
                    g.Add("schlick", schlick);
                    g.Add("b1=R/(rho C)", Lum(rr[i]) / (rho[i] * c));
                    g.Add("b2=(R-B)/(rho C)", (Lum(rr[i]) - Lum(b[i])) / (rho[i] * c));
                    g.Add("b3=(R-W0)/(rho C)", (Lum(rr[i]) - Lum(w0[i])) / (rho[i] * c));
                    g.Add("taken/lumBase", pre);
                    g.Add("est/truth", taken / Mathf.Max(truth, 1e-7f));
                    // What the frame shows was taken (the cap included) against the sky the layer put in, and the
                    // sky light left in the final pixel as a share of it.
                    g.Add("removed/skyIn", (Lum(radd[i]) - Lum(rr[i])) / Mathf.Max(truth, 1e-7f));
                    g.Add("skyLeft/R", (truth - (Lum(radd[i]) - Lum(rr[i]))) / Mathf.Max(Lum(rr[i]), 1e-7f));
                    g.Add("a/rho", a / rho[i]);
                    g.Add("skyLayer/skyEst", (Lum(skyIn) / Mathf.Max(a, 1e-6f)) / Mathf.Max(lumSky, 1e-7f));
                    g.Add("sharp/blurred", lumSky / Mathf.Max(Lum(skyIn) / Mathf.Max(a, 1e-6f), 1e-7f));
                    g.Add("R/I", Lum(rr[i]) / Mathf.Max(Lum(iPx), 1e-7f));
                    g.Add("I", Lum(iPx));
                    g.Add("codeR", codeR);
                    g.Add("codeC", codeC);
                    g.Add("codeI", codeI);
                    g.Add("codeR/codeC", codeR / Mathf.Max(codeC, 1e-3f));
                    g.Add("angle", angle);
                }
                for (int k = 0; k < bins.Length; k++)
                {
                    if (Mathf.Abs(angle - bins[k]) <= Mathf.Max(0.5f, 0.1f * bins[k]))
                    {
                        byAngle[k].Count++;
                        byAngle[k].Add("rho", rho[i]);
                        byAngle[k].Add("a", a);
                        byAngle[k].Add("schlick", schlick);
                        byAngle[k].Add("est/truth", taken / Mathf.Max(truth, 1e-7f));
                        byAngle[k].Add("b1", Lum(rr[i]) / (rho[i] * c));
                        byAngle[k].Add("R/I", Lum(rr[i]) / Mathf.Max(Lum(iPx), 1e-7f));
                    }
                }
                // The probe's removal against the frame's own (uncapped pixels): the probe reads what the composite read.
                if (pre < 0.95f)
                {
                    float removedFrame = Lum(radd[i]) - Lum(rr[i]);
                    check.Add("removed frame/probe", removedFrame / Mathf.Max(pre * lumBase, 1e-7f));
                    check.Add("added frame/probe", (Lum(radd[i]) - Lum(w[i])) / Mathf.Max(Lum(added[i]), 1e-7f));
                    check.Count++;
                }
                if (screen)
                {
                    // The estimate against the truth: red where the composite takes more sky than the layer put in, blue less.
                    float e = Mathf.Log(Mathf.Max(taken / Mathf.Max(truth, 1e-7f), 1e-3f), 2f);
                    float t = Mathf.Clamp01(Mathf.Abs(e) / 2f);
                    estMap[i] = e > 0f ? new Color(1f, 1f - t, 1f - t) : new Color(1f - t, 1f - t, 1f);
                }
            }

            string head = "mirror " + label;
            log.Add(string.Format(CultureInfo.InvariantCulture, "{0}: below zero {1} of {2} px ({3} in the reflection), lowest {4:G4}; noise floor (R drawn twice{5}) max {6:G3}, {7} px over 1e-4; sky share {8}, strength {9}",
                head, below, n, belowCovered, lowest, sizeChange ? ": the frame after the change against its own" : "", noise, noisy, geom.z, geom.y));
            log.Add(head + " check (uncapped, n " + check.Count + "): removed frame/probe " + Q(check.Get("removed frame/probe")) + "; added frame/probe " + Q(check.Get("added frame/probe")));
            foreach (KeyValuePair<string, Region> pair in new[] { new KeyValuePair<string, Region>("bright", bright), new KeyValuePair<string, Region>("dark", dark), new KeyValuePair<string, Region>("all", all) })
            {
                Region g = pair.Value;
                var s = new StringBuilder(string.Format(CultureInfo.InvariantCulture, "{0} {1}: n {2}, capped {3:P1}", head, pair.Key, g.Count, g.Count > 0 ? g.Capped / (double)g.Count : 0.0));
                foreach (string k in new[] { "R", "C", "rho", "B", "W", "W0", "a", "schlick", "b1=R/(rho C)", "b2=(R-B)/(rho C)", "b3=(R-W0)/(rho C)", "taken/lumBase", "est/truth", "removed/skyIn", "skyLeft/R", "a/rho",
                                              "skyLayer/skyEst", "sharp/blurred", "R/I", "I", "codeR", "codeC", "codeI", "codeR/codeC", "angle" })
                    s.Append("; " + k + " " + Q(g.Get(k)));
                if (pair.Key != "all")
                    s.Append(string.Format(CultureInfo.InvariantCulture, "; codeR/codeOpen {0:G4}", Median(g.Get("codeR")) / Median(new List<float>(open))));
                log.Add(s.ToString());
            }
            log.Add(head + " open water: n " + open.Count + ", code " + Q(open) + "; the sky over the horizon beside the hill: n " + skyBand.Count + ", code " + Q(skyBand));
            var sa = new StringBuilder(head + " by angle below the level:");
            for (int k = 0; k < bins.Length; k++)
                sa.Append(string.Format(CultureInfo.InvariantCulture, " {0} deg (n {1}): rho {2}, a {3}, schlick {4}, est/truth {5}, b1 {6}, R/I {7};", bins[k], byAngle[k].Count,
                    Q(byAngle[k].Get("rho")), Q(byAngle[k].Get("a")), Q(byAngle[k].Get("schlick")), Q(byAngle[k].Get("est/truth")), Q(byAngle[k].Get("b1")), Q(byAngle[k].Get("R/I"))));
            log.Add(sa.ToString());

            for (int vi = 0; vi < Variants.Length; vi++)
                VariantStats(Variants[vi], variantFrames[vi], variantBlack[vi], rr, w, w0, w1, radd, rho, cov, water, m, surfaceEye, sky.x, geom.y,
                             exposure, true, rw, rh, head, Path.Combine(outDir, shot.Name + (screen ? "" : "_" + size.Replace(' ', '_'))), log);
            if (!screen && Variants.Length > 0)
            {
                // Today's picture at this size, beside the variants' (the dynamic resolution sheet).
                var shownToday = new Color[n];
                for (int i = 0; i < n; i++)
                    shownToday[i] = Shown(rr[i], exposure);
                WaterPreview.WritePng(Path.Combine(outDir, shot.Name + "_" + size.Replace(' ', '_') + "_v0.png"), shownToday, rw, rh);
            }

            if (screen)
            {
                WaterPreview.WritePng(Path.Combine(outDir, shot.Name + "_I.png"), intended, rw, rh);
                WaterPreview.WritePng(Path.Combine(outDir, shot.Name + "_est.png"), estMap, rw, rh);
            }
        }

        private static readonly string[] VariantNames = { "today", "H0", "H1", "P" };

        /// <summary>
        /// One variant of the one mirror against today's picture and against its intended picture, built from
        /// today's references (W the water without the city's reflection, W0 the same with the sky black, a the
        /// layer's alpha, Radd - W the city's light today's trace adds, k the share covered):
        ///   H0: W - (W - W0) k min(s, 1) + (Radd - W)                (the sky out where the city stands in for it)
        ///   H1: W - (W - W0) k min(s, 1) + (Radd - W) (a / share) / rho   (the city weighted as the sky)
        ///   P:  W - (W - W0) k min(s, 1) + (Radd - W) a / rho        (the layer's a, share included)
        /// Logged: pixels below zero; against today over the whole frame (codes through the shots' curve: px over 2,
        /// mean, worst); where covered > 0.9 (bright / dark / all, and the far water 1-4 km by eye depth): R / I, the
        /// sky left in R ((R - R with the sky cube black) / R), R's codes against the water round it; R / I by angle.
        /// Pictures: the variant (_v<n>) and |its code - the intended picture's| x 10 (_v<n>_err).
        /// </summary>
        private void VariantStats(int v, Color[] rv, Color[] rvb, Color[] today, Color[] w, Color[] w0, Color[] w1, Color[] radd, float[] rho, float[] cov,
                                  bool[] water, MirrorMasks m, float[] eye, float share, float strength, float exposure, bool screen, int rw, int rh,
                                  string head, string pathBase, List<string> log)
        {
            int n = rw * rh;
            float sMin = Mathf.Min(Mathf.Max(strength, 0f), 1f);
            int below = 0, belowCovered = 0, over2 = 0;
            float lowest = 0f, worst = 0f;
            double diffSum = 0.0;
            var bright = new Region();
            var dark = new Region();
            var all = new Region();
            var far = new Region();
            var open = new List<float>();
            float[] bins = { 1f, 3f, 5f, 10f, 20f, 35f };
            var byAngle = new Region[bins.Length];
            for (int k = 0; k < bins.Length; k++)
                byAngle[k] = new Region();
            var picture = screen ? new Color[n] : null;
            var error = screen ? new Color[n] : null;
            for (int i = 0; i < n; i++)
            {
                Color r = rv[i];
                float minR = Mathf.Min(r.r, Mathf.Min(r.g, r.b));
                if (minR < -1e-4f)
                {
                    below++;
                    if (m.Covered[i])
                        belowCovered++;
                }
                lowest = Mathf.Min(lowest, minR);
                Color shown = Shown(r, exposure), shownToday = Shown(today[i], exposure);
                float most = Mathf.Max(Mathf.Abs(shown.r - shownToday.r), Mathf.Max(Mathf.Abs(shown.g - shownToday.g), Mathf.Abs(shown.b - shownToday.b))) * 255f;
                if (most > 2f)
                    over2++;
                worst = Mathf.Max(worst, most);
                diffSum += Mathf.Abs(Code(shown) - Code(shownToday));
                if (screen)
                    picture[i] = shown;

                Color intended = r;
                float a = Lum(w1[i] - w0[i]);
                if (water[i] && rho[i] > 1e-5f)
                {
                    Color skyIn = w[i] - w0[i], cityIn = radd[i] - w[i];
                    float weight = v == 1 ? 1f : v == 2 ? (a / Mathf.Max(share, 1e-4f)) / rho[i] : a / rho[i];
                    intended = w[i] - skyIn * (cov[i] * sMin) + cityIn * weight;
                }
                float codeR = Code(shown), codeI = Code(Shown(intended, exposure));
                if (screen)
                {
                    float e = Mathf.Clamp01(Mathf.Abs(codeR - codeI) * 10f / 255f);
                    error[i] = water[i] ? new Color(e, e, e) : shown * 0.3f;
                }
                if (m.Open[i])
                    open.Add(codeR);
                if (!m.Covered[i])
                    continue;
                float ratio = Lum(r) / Mathf.Max(Lum(intended), 1e-7f);
                float left = Lum(r - rvb[i]) / Mathf.Max(Lum(r), 1e-7f);
                Region region = m.Bright[i] ? bright : dark;
                // The water point's distance along the eye's ray (the flat sea at the preview's level).
                float distance = DistanceOf(i, rw, rh);
                bool isFar = distance >= 1000f && distance <= 4000f;
                foreach (Region g in isFar ? new[] { region, all, far } : new[] { region, all })
                {
                    g.Count++;
                    g.Add("R/I", ratio);
                    g.Add("skyLeft/R", left);
                    g.Add("|codeR-codeI|", Mathf.Abs(codeR - codeI));
                    g.Add("codeR", codeR);
                    g.Add("codeToday", Code(shownToday));
                    g.Add("distance", distance);
                    g.Add("R/today", Lum(r) / Mathf.Max(Lum(today[i]), 1e-7f));
                }
                // The angle below the level from the trace's reflectance would need the table; the eye's ray: from the camera.
                float angle = AngleOf(i, rw, rh);
                for (int k = 0; k < bins.Length; k++)
                {
                    if (Mathf.Abs(angle - bins[k]) <= Mathf.Max(0.5f, 0.1f * bins[k]))
                    {
                        byAngle[k].Count++;
                        byAngle[k].Add("R/I", ratio);
                        byAngle[k].Add("skyLeft/R", left);
                    }
                }
            }

            string h = head + " " + VariantNames[v];
            log.Add(string.Format(CultureInfo.InvariantCulture, "{0}: below zero {1} of {2} px ({3} in the reflection), lowest {4:G4}; against today: {5} px over 2 codes, mean {6:F3} codes, worst {7:F1}",
                h, below, n, belowCovered, lowest, over2, diffSum / n, worst));
            foreach (KeyValuePair<string, Region> pair in new[] { new KeyValuePair<string, Region>("bright", bright), new KeyValuePair<string, Region>("dark", dark),
                                                                   new KeyValuePair<string, Region>("all", all), new KeyValuePair<string, Region>("far", far) })
            {
                Region g = pair.Value;
                var s = new StringBuilder(string.Format(CultureInfo.InvariantCulture, "{0} {1}: n {2}", h, pair.Key, g.Count));
                foreach (string k in new[] { "R/I", "skyLeft/R", "|codeR-codeI|", "codeR", "codeToday", "distance", "R/today" })
                    s.Append("; " + k + " " + Q(g.Get(k)));
                if (pair.Key == "bright" || pair.Key == "dark")
                    s.Append(string.Format(CultureInfo.InvariantCulture, "; codeR/codeOpen {0:G4}", Median(g.Get("codeR")) / Median(new List<float>(open))));
                log.Add(s.ToString());
            }
            var sa = new StringBuilder(h + " by angle below the level:");
            for (int k = 0; k < bins.Length; k++)
                sa.Append(string.Format(CultureInfo.InvariantCulture, " {0} deg (n {1}): R/I {2}, skyLeft/R {3};", bins[k], byAngle[k].Count, Q(byAngle[k].Get("R/I")), Q(byAngle[k].Get("skyLeft/R"))));
            log.Add(sa.ToString());
            log.Add(h + " open water: code " + Q(open));
            if (screen)
            {
                WaterPreview.WritePng(pathBase + "_v" + v + ".png", picture, rw, rh);
                WaterPreview.WritePng(pathBase + "_v" + v + "_err.png", error, rw, rh);
            }
        }

        /// <summary>How far below the level the eye's ray through a pixel looks (degrees), the camera as set for the measurement.</summary>
        private float AngleOf(int i, int rw, int rh)
        {
            Vector3 d = _camera.ViewportPointToRay(new Vector3((i % rw + 0.5f) / rw, (i / rw + 0.5f) / rh, 0f)).direction;
            return Mathf.Asin(Mathf.Clamp01(-d.y)) * Mathf.Rad2Deg;
        }

        private Camera _camera;

        /// <summary>How far along the eye's ray a pixel meets the flat sea at the preview's level (m).</summary>
        private float DistanceOf(int i, int rw, int rh)
        {
            Ray ray = _camera.ViewportPointToRay(new Vector3((i % rw + 0.5f) / rw, (i / rw + 0.5f) / rh, 0f));
            return (ray.origin.y - SeaLevel) / Mathf.Max(-ray.direction.y, 1e-4f);
        }

        private static int Count(bool[] v)
        {
            int c = 0;
            foreach (bool b in v)
                if (b)
                    c++;
            return c;
        }
    }
}
