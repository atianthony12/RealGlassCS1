using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Pictures of the Water tab's city reflection (RealGlass/CityReflection) on this machine's GPU, for
/// the look -- tools\preview\water-render.ps1 writes water-preview-input.txt (the mod's numbers: the
/// reflectance table, the waves' rows, the streaks' sigma, the shots) and runs
///   Unity.exe -batchmode -quit -projectPath . -executeMethod WaterPreview.Render
/// A harbour: a plane of water at y = 0 and towers of window bays standing past it; the camera in
/// legacy deferred HDR like the game's, the reflection drawn by one command buffer at
/// AfterImageEffectsOpaque exactly as WaterCityReflection.Record records it. By day the towers are
/// lit by a sun, by night only their lit windows glow. Each shot is one PNG (tonemapped, sRGB).
/// </summary>
public static class WaterPreview
{
    private sealed class Shot
    {
        public string Name;
        public string View;
        public float Ripples;
        public float Seconds;
        public float Strength;

        /// <summary>With "cards 1": 0 the reflection as today, 1 with the facade cards, 2 where they answered.</summary>
        public int Card;
    }

    /// <summary>
    /// A run of frames for the flicker: the camera's projection jittered as temporal anti-aliasing
    /// jitters it, the game's clocks running (or frozen), each frame resolved by PreviewTAA.shader.
    /// CityGain / Footprint: the reflection's _RGWaterWave. FadeStart / FadeEnd above 0: the ripple
    /// texture's mip levels drawn one by one, each holding only the waves of FadeStart..FadeEnd texels
    /// or more; 0: the base level drawn, the GPU averaging its mips (as the mod does today).
    /// </summary>
    private sealed class Seq
    {
        public string Name;
        public string View;
        public float Ripples;
        public float Seconds;
        public float Strength;
        public bool Frozen;
        public float CityGain;
        public float Footprint;
        public float FadeStart;
        public float FadeEnd;
        public float Jitter;
        public float Cone;
        public float Distort;
        public int Subframes;
        public float History;
        public int Card;
    }

    public static void Render()
    {
        string dir = Directory.GetCurrentDirectory();
        var log = new List<string>();
        try
        {
            PlayerSettings.colorSpace = ColorSpace.Linear;
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/RealGlass/CityReflection.shader");
            log.Add("shader " + (shader != null ? shader.name + " supported=" + shader.isSupported : "MISSING"));
            var material = new Material(shader);

            var table = new Vector4[33];
            var rows = new Vector4[64];
            var shots = new List<Shot>();
            var seqs = new List<Seq>();
            string outDir = dir;
            int width = 960, height = 540;
            // The camera's viewport (the game's own with its toolbar shown: CameraController.
            // kFullScreenWithoutMenuBarRect); the buffers then sized from the camera, as the mod sizes them.
            Rect viewport = new Rect(0f, 0f, 1f, 1f);
            float streak = 0f, length = 1f, exposureDay = 1f, exposureNight = 1f, rippleRms = 0.3f, cityGain = 0f;
            // frames, warm-up, frames a second, jitter, blending, sharpen, raw frames written at the end
            var taa = new float[] { 64f, 32f, 60f, 1f, 0.94f, 0.15f, 0f };
            bool cardScene = false;
            foreach (string raw in File.ReadAllLines(Path.Combine(dir, "water-preview-input.txt")))
            {
                string[] p = raw.Split(' ');
                if (p[0] == "out")
                    outDir = raw.Substring(4);
                else if (p[0] == "size")
                {
                    width = int.Parse(p[1], CultureInfo.InvariantCulture);
                    height = int.Parse(p[2], CultureInfo.InvariantCulture);
                }
                else if (p[0] == "rect")
                    viewport = new Rect(F(p[1]), F(p[2]), F(p[3]), F(p[4]));
                else if (p[0] == "table")
                    table[int.Parse(p[1], CultureInfo.InvariantCulture)] = new Vector4(F(p[2]), 0f, 0f, 0f);
                else if (p[0] == "ripple")
                    rows[int.Parse(p[1], CultureInfo.InvariantCulture)] = new Vector4(F(p[2]), F(p[3]), F(p[4]), F(p[5]));
                else if (p[0] == "ripplerms")
                    rippleRms = F(p[1]);
                else if (p[0] == "citygain")
                    cityGain = F(p[1]);
                else if (p[0] == "streak")
                    streak = F(p[1]);
                else if (p[0] == "length")
                    length = F(p[1]);
                else if (p[0] == "exposure")
                {
                    exposureDay = F(p[1]);
                    exposureNight = F(p[2]);
                }
                else if (p[0] == "shot")
                    shots.Add(new Shot { Name = p[1], View = p[2], Ripples = F(p[3]), Seconds = F(p[4]), Strength = F(p[5]), Card = p.Length > 6 ? (int)F(p[6]) : 0 });
                else if (p[0] == "cards")
                    cardScene = p[1] == "1";
                else if (p[0] == "taa")
                {
                    // frames, warm-up frames, frames a second, jitter spread, stationary blending, sharpen[,
                    // raw frames written at the end]
                    for (int k = 0; k < taa.Length && k + 1 < p.Length; k++)
                        taa[k] = F(p[k + 1]);
                }
                else if (p[0] == "seq")
                    seqs.Add(new Seq
                    {
                        Name = p[1], View = p[2], Ripples = F(p[3]), Seconds = F(p[4]), Strength = F(p[5]), Frozen = p[6] == "1",
                        CityGain = F(p[7]), Footprint = F(p[8]), FadeStart = F(p[9]), FadeEnd = F(p[10]), Jitter = F(p[11]), Cone = p.Length > 12 ? F(p[12]) : 0f,
                        Distort = p.Length > 13 ? F(p[13]) : 0f, Subframes = p.Length > 14 ? (int)F(p[14]) : 1,
                        History = p.Length > 15 ? F(p[15]) : 0f, Card = p.Length > 16 ? (int)F(p[16]) : 0,
                    });
            }

            Shader.SetGlobalVectorArray("_RGSeaTable", table);
            // RG_SKYSHARE (0, the default: none): a sky's share for the city reflection to take the place
            // of, against a plain blue sky -- the stand-in water has no sky layer of ours, so the picture
            // is not the game's; it runs the composite's sky code and the covered share's history (2.0.0).
            s_skyShare = F(Environment.GetEnvironmentVariable("RG_SKYSHARE") ?? "0");
            material.SetTexture("_RGWaterTheme", Cube(s_skyShare > 0f ? new Color(0.25f, 0.45f, 0.9f) : Color.black));
            log.Add("sky share for the city reflection: " + s_skyShare);

            // The game's water, stood in for (PreviewWater.shader), with our ripples on it.
            var water = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/Editor/PreviewWater.shader"));
            water.SetColor("_BodyColour", new Color(0.03f, 0.06f, 0.08f, 1f));
            CardCity cards = cardScene ? new CardCity() : null;
            var city = BuildCity(water, cards, log);
            // Round 2b: the cards' level from the frame, measured every frame from a copy of the frame the
            // water reads (its buffer first at the water's event); applied with RG_CARDGAIN=1.
            if (cards != null)
            {
                _gain = new RealGlass.Glass.FacadeCardGain(AssetDatabase.LoadAssetAtPath<Shader>("Assets/RealGlass/FacadeCardGain.shader"));
                _gain.Keep = F(Environment.GetEnvironmentVariable("RG_GAINKEEP") ?? "0");
                RealGlass.Glass.FacadeCardGain.Apply(Environment.GetEnvironmentVariable("RG_CARDGAIN") == "1", 20f, 0.25f, 4f);
                log.Add("card gain: shader supported=" + _gain.Material.shader.isSupported + ", applied " + (Environment.GetEnvironmentVariable("RG_CARDGAIN") == "1") + ", keep " + _gain.Keep);
            }

            var skybox = new Material(Shader.Find("Skybox/Procedural"));
            RenderSettings.skybox = skybox;
            var sunObject = new GameObject("preview sun");
            Light sun = sunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.None;
            RenderSettings.sun = sun;

            // The sky the water reflects: the skybox in a cube, as the mod's own cube holds it.
            var skyCube = new RenderTexture(128, 128, 16, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                dimension = TextureDimension.Cube,
                useMipMap = true,
                autoGenerateMips = true,
            };
            skyCube.Create();
            var captureObject = new GameObject("preview sky capture");
            Camera capture = captureObject.AddComponent<Camera>();
            capture.enabled = false;
            capture.clearFlags = CameraClearFlags.Skybox;
            capture.cullingMask = 0;
            capture.allowHDR = true;
            capture.renderingPath = RenderingPath.Forward;
            water.SetTexture("_Sky", skyCube);
            RenderTexture ripples = null;

            var cameraObject = new GameObject("preview camera");
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.renderingPath = RenderingPath.DeferredLighting;
            camera.allowHDR = true;
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.depthTextureMode = DepthTextureMode.Depth;
            camera.fieldOfView = 45f;
            camera.nearClipPlane = 1f;
            camera.farClipPlane = 20000f;
            // With a viewport, an LDR target as the game's screen: Unity then renders the HDR picture
            // into a buffer of its own the camera's size and copies it into the viewport at the end, as
            // in the game (rendering straight into a part of an HDR target, legacy deferred's light
            // pass reads the wrong place: black buildings).
            bool part = viewport.x != 0f || viewport.y != 0f || viewport.width != 1f || viewport.height != 1f;
            var target = new RenderTexture(width, height, 24, part ? RenderTextureFormat.ARGB32 : RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            camera.targetTexture = target;
            camera.rect = viewport;
            log.Add("camera " + camera.pixelWidth + " x " + camera.pixelHeight + " at " + camera.pixelRect + " of " + width + " x " + height);

            // As WaterCityReflection.Record.
            var mesh = UnitCube();
            int w = Mathf.Max(1, camera.pixelWidth / 2), h = Mathf.Max(1, camera.pixelHeight / 2);
            RenderTexture frame = Target(w, h, true), trace = Target(w, h, false), streaked = Target(w, h, true);
            RenderTexture accum = Target(w, h, true), history = Target(w, h, false);
            material.SetTexture("_RGWaterFrame", frame);
            material.SetTexture("_RGWaterTrace", trace);
            material.SetTexture("_RGWaterTraced", trace);
            material.SetTexture("_RGWaterHistory", history);
            var buffer = new CommandBuffer { name = "RealGlass water reflection (preview)" };
            PreviewWaterSurface.Record(buffer, material, GameObject.Find("preview water surface"), camera.pixelWidth, camera.pixelHeight);
            buffer.Blit(BuiltinRenderTextureType.CameraTarget, frame);
            buffer.SetRenderTarget(trace);
            buffer.ClearRenderTarget(false, true, Color.clear);
            buffer.DrawMesh(mesh, Matrix4x4.identity, material, 0, 0);
            buffer.SetRenderTarget(accum);
            buffer.DrawMesh(mesh, Matrix4x4.identity, material, 0, 3);
            buffer.Blit(accum, history);
            buffer.Blit(accum, streaked, material, 1);
            buffer.SetGlobalTexture("_RGWaterStreaked", streaked);
            // The share of each texel the city covers, the same way (pass 4, then the streaks).
            RenderTexture accum2 = Target(w, h, true), history2 = Target(w, h, false), streaked2 = Target(w, h, true);
            material.SetTexture("_RGWaterHistory2", history2);
            buffer.SetRenderTarget(accum2);
            buffer.DrawMesh(mesh, Matrix4x4.identity, material, 0, 4);
            buffer.Blit(accum2, history2);
            buffer.Blit(accum2, streaked2, material, 1);
            buffer.SetGlobalTexture("_RGWaterStreaked2", streaked2);
            buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            buffer.DrawMesh(mesh, Matrix4x4.identity, material, 0, 2);
            if (_gain != null)
                camera.AddCommandBuffer(CameraEvent.AfterImageEffectsOpaque, _gain.Buffer);
            camera.AddCommandBuffer(CameraEvent.AfterImageEffectsOpaque, buffer);

            material.SetVector("_RGWaterMarch", new Vector4(8000f, 30f, 0.05f, Mathf.Max(1f, length)));
            material.SetVector("_RGWaterStreak", new Vector4(streak, 1f / h, w, h));

            var stage = new Stage
            {
                City = city, SunObject = sunObject, Sun = sun, Skybox = skybox, Capture = capture, SkyCube = skyCube, Water = water,
                CameraObject = cameraObject, Cards = cards, Camera = camera, Material = material,
            };
            if (seqs.Count > 0)
                Sequences(seqs, stage, camera, target, trace, material, rows, rippleRms, taa, exposureDay, exposureNight, outDir, log);

            foreach (Shot shot in shots)
            {
                bool night = stage.Set(shot.View);
                stage.SetCards(night, shot.Card);

                // The game's clocks at this shot's time, then the ripples drawn for it (as WaterRipples:
                // root-mean-square slope = half the game texture's decoded rms x the strength).
                Shader.SetGlobalVector("_SimulationTime", new Vector4(shot.Seconds % 60f, (shot.Seconds % 60f) / 60f, 0f, 0f));
                Shader.SetGlobalVector("_SimulationTime2", new Vector4(shot.Seconds, shot.Seconds / 3600f, 0f, 0f));
                if (ripples != null)
                    ripples.Release();
                ripples = CityReflectionTest.GenerateRipples(rows, 0.5f * rippleRms * shot.Ripples, 1024);
                water.SetTexture("_WaterNormal", ripples);
                material.SetTexture("_RGRipples", ripples);

                material.SetVector("_RGWaterGeom", new Vector4(0f, shot.Strength, s_skyShare, 0f));
                material.SetVector("_RGWaterWave", new Vector4(cityGain, 0f, 0f, 0f));
                material.SetVector("_RGWaterLight", new Vector4(1f, 1f, 1f, 0f));

                if (_gain != null)
                {
                    _gain.Reset();
                    _gain.Frame(camera, camera.pixelWidth, camera.pixelHeight, true);
                }
                camera.Render();
                if (_gain != null)
                    log.Add("shot " + shot.Name + " " + GainLine());
                RenderTexture.active = target;
                var read = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
                read.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                RenderTexture.active = null;

                float exposure = night ? exposureNight : exposureDay;
                Color[] px = read.GetPixels();
                var png = new Texture2D(width, height, TextureFormat.RGB24, false);
                var o = new Color[px.Length];
                for (int i = 0; i < px.Length; i++)
                    o[i] = new Color(Tone(px[i].r * exposure), Tone(px[i].g * exposure), Tone(px[i].b * exposure), 1f);
                png.SetPixels(o);
                png.Apply(false);
                File.WriteAllBytes(Path.Combine(outDir, shot.Name + ".png"), png.EncodeToPNG());
                log.Add("shot " + shot.Name + " ok; the frame's copy " + Coverage(frame, 1) + ", the trace " + Coverage(trace, 3) +
                        " (coloured " + Coverage(trace, 1) + "), the streaks " + Coverage(streaked, 3));
            }
        }
        catch (Exception e)
        {
            log.Add("EXCEPTION " + e);
        }

        File.WriteAllLines(Path.Combine(dir, "water-preview-output.txt"), log.ToArray());
    }

    private static RealGlass.Glass.FacadeCardGain _gain;
    private static float s_skyShare;   // RG_SKYSHARE: the sky's share the city reflection takes the place of (0: none)

    /// <summary>The gain's newest sums, for the log.</summary>
    private static string GainLine()
    {
        return "card gain: " + RealGlass.Glass.FacadeCardGain.Describe(_gain.Read());
    }

    private sealed class CityTower
    {
        public Renderer Renderer;
        public Material Day, Night;
    }

    /// <summary>The harbour's light, sky and camera for a view.</summary>
    private sealed class Stage
    {
        public List<CityTower> City;
        public GameObject SunObject;
        public Light Sun;
        public Material Skybox;
        public Camera Capture;
        public RenderTexture SkyCube;
        public Material Water;
        public GameObject CameraObject;
        public CardCity Cards;
        public Camera Camera;
        public Material Material;
        public Vector3 ToSun;

        /// <summary>
        /// "cards 1": what the reflection's card lookup reads, as the mod would set it -- the glass's own
        /// uniforms (GlassUniforms.Apply: the sun, the ambient, the glass's look, the walk's budget, the
        /// colour map), the city map, the cards, their light, the stand-ins' lights by the game's rule --
        /// and the keyword for mode 1 and 2 (2: where the cards answered).
        /// </summary>
        public void SetCards(bool night, int mode)
        {
            if (Cards == null)
                return;
            Shader.SetGlobalFloat("_PreviewNight", night ? 1f : 0f);
            Shader.SetGlobalFloat("_PreviewGameLights", 1f);
            // The stand-ins shade as the game's final pass does (round 2; by day too: its glow and its
            // windows by the game's rule are the game's, day and night).
            Shader.SetGlobalFloat("_PreviewGameShading", 1f);
            Shader.SetGlobalColor("_PreviewPalette", GlassPreview.Palette);
            RealGlass.Glass.FacadeCards.SetNight(Environment.GetEnvironmentVariable("RG_CARDNIGHT") == "filtered");
            Shader.SetGlobalFloat("_PreviewLitShare", 0.25f);
            Shader.SetGlobalFloat("_GiantMarshmallow", night ? -3f : 6f);
            Shader.SetGlobalTexture("_RGSky", SkyCube);

            var f = new RealGlass.Glass.GlassFrame();
            f.SunX = ToSun.x; f.SunY = ToSun.y; f.SunZ = ToSun.z;
            Color sunLin = Sun.color.linear * Sun.intensity;
            f.SunR = sunLin.r; f.SunG = sunLin.g; f.SunB = sunLin.b;
            Color sky = RenderSettings.ambientSkyColor.linear, ground = RenderSettings.ambientGroundColor.linear;
            f.AmbSkyR = sky.r; f.AmbSkyG = sky.g; f.AmbSkyB = sky.b;
            f.AmbGroundR = ground.r; f.AmbGroundG = ground.g; f.AmbGroundB = ground.b;
            f.ScreenHeight = Camera.pixelHeight;
            f.ColorMapWidth = 512;
            f.ColorMapHeight = 256;
            f.Hour = night ? 21f : 13f;
            f.Clock = 240f + f.Hour;
            f.CubeMips = SkyCube.width > 0 ? Mathf.RoundToInt(Mathf.Log(SkyCube.width, 2f)) + 1 : 1;
            RealGlass.Glass.GlassUniforms.Apply(RealGlass.Glass.GlassParams.Default(), f,
                RealGlass.Glass.GlassQuality.ForTier(RealGlass.Glass.GlassQuality.Ultra));
            Cards.Map.Bind(true);
            Cards.Cards.Bind();
            GlassPreview.CardLight(Sun);
            RealGlass.Glass.FacadeCards.SetMode(true, false, mode == 2 ? 1f : 0f);
            // RG_CARDPART=n: the card's light part by part (FacadeCards.cginc's _RGCardMode.w).
            string part = Environment.GetEnvironmentVariable("RG_CARDPART");
            if (!string.IsNullOrEmpty(part))
            {
                Vector4 m = Shader.GetGlobalVector("_RGCardMode");
                Shader.SetGlobalVector("_RGCardMode", new Vector4(m.x, m.y, m.z, F(part)));
            }
            // The game's static environment cube (the cards' sheen): the preview's sky cube stands in.
            Shader.SetGlobalTexture("_EnvironmentCubemap", SkyCube);
            // RG_CARDSMOD=1 (the game round): the keyword on the reflection's own material, the mod's way.
            if (Environment.GetEnvironmentVariable("RG_CARDSMOD") == "1")
            {
                Shader.DisableKeyword(RealGlass.Glass.FacadeCards.Keyword);
                RealGlass.Glass.FacadeCards.SetReader(Material, mode >= 1);
            }
            else if (mode >= 1)
                Shader.EnableKeyword(RealGlass.Glass.FacadeCards.Keyword);
            else
                Shader.DisableKeyword(RealGlass.Glass.FacadeCards.Keyword);
        }

        /// <summary>True at night.</summary>
        public bool Set(string view)
        {
            bool night = view.StartsWith("night", StringComparison.Ordinal);
            bool sunset = view.StartsWith("sunset", StringComparison.Ordinal);
            foreach (CityTower t in City)
                t.Renderer.sharedMaterial = night ? t.Night : t.Day;

            // Day: the sun high behind and to the left; sunset: low, ahead of the camera (its glitter
            // on the water between camera and towers); night: none.
            Vector3 toSun = sunset ? new Vector3(0.979f, 0.105f, -0.173f).normalized : new Vector3(-0.55f, 0.45f, 0.7f).normalized;
            ToSun = night ? new Vector3(0f, -0.5f, 0.866f) : toSun;
            SunObject.transform.rotation = Quaternion.LookRotation(-toSun);
            Sun.intensity = night ? 0f : sunset ? 1.6f : 1.3f;
            Sun.color = sunset ? new Color(1f, 0.62f, 0.38f) : new Color(1f, 0.95f, 0.88f);
            Skybox.SetFloat("_Exposure", night ? 0.04f : 1.1f);
            Skybox.SetFloat("_AtmosphereThickness", 1f);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            Color ambient = night ? new Color(0.010f, 0.012f, 0.020f) : sunset ? new Color(0.30f, 0.30f, 0.38f) : new Color(0.45f, 0.55f, 0.70f);
            RenderSettings.ambientSkyColor = ambient;
            RenderSettings.ambientEquatorColor = night ? new Color(0.008f, 0.009f, 0.012f) : new Color(0.40f, 0.42f, 0.45f);
            RenderSettings.ambientGroundColor = night ? new Color(0.004f, 0.004f, 0.005f) : new Color(0.20f, 0.20f, 0.20f);
            Capture.RenderToCubemap(SkyCube);
            Water.SetVector("_PreviewSun", new Vector4(toSun.x, toSun.y, toSun.z, Sun.intensity));
            Water.SetColor("_PreviewSunColour", Sun.color.linear);
            Water.SetColor("_PreviewAmbient", ambient.linear);

            // The views: a camera low over the harbour, one close over the water, one higher up;
            // sunset and night from the low one (nightclose: night from the close one).
            Vector3 eye;
            float pitch;
            float yaw = 90f;
            if (view.EndsWith("steep", StringComparison.Ordinal))
            {
                // (The facade cards' views.) The towers' tops above the screen's top edge: their
                // reflections in the water run up to it, where the screen has nothing more.
                eye = new Vector3(250f, 80f, 0f);
                pitch = 20f;
            }
            else if (view.EndsWith("off", StringComparison.Ordinal))
            {
                // The towers wholly off the screen (above its top edge), their reflections on it.
                eye = new Vector3(300f, 60f, -300f);
                pitch = 35f;
                yaw = 40f;
            }
            else if (view.EndsWith("high", StringComparison.Ordinal))
            {
                eye = new Vector3(-200f, 260f, 60f);
                pitch = 18f;
            }
            else if (view.EndsWith("close", StringComparison.Ordinal))
            {
                eye = new Vector3(0f, 14f, 40f);
                pitch = 22f;
            }
            else
            {
                eye = new Vector3(0f, 45f, 40f);
                pitch = 4f;
            }
            CameraObject.transform.position = eye;
            CameraObject.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
            return night;
        }
    }

    /// <summary>
    /// The flicker: each run's frames rendered with the projection jittered (Halton 2, 3 over 8
    /// frames, as Post-processing Stack v1's temporal anti-aliasing) and the clocks running, resolved
    /// by PreviewTAA.shader; after the warm-up, each water pixel's mean |second difference in time|
    /// of its displayed brightness (0..1) -- steady motion has none, a pixel that jumps back and forth
    /// a lot. Logged per run as the share of the water over 2/255 and over 8/255, with TAA and without
    /// (the raw frames); the last resolved frame and the flicker map written as PNGs.
    /// </summary>
    private static void Sequences(List<Seq> seqs, Stage stage, Camera camera, RenderTexture target, RenderTexture trace, Material material,
                                  Vector4[] rows, float rippleRms, float[] taa, float exposureDay, float exposureNight, string outDir, List<string> log)
    {
        int frames = (int)taa[0], warm = (int)taa[1], dump = taa.Length > 6 ? (int)taa[6] : 0;
        float fps = taa[2], spread = taa[3], blend = taa[4], sharpen = taa[5];
        int width = target.width, height = target.height, n = width * height;
        var resolve = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/Editor/PreviewTAA.shader"));
        var generator = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/RealGlass/WaterRipples.shader"));
        generator.SetVectorArray("_RGRippleWaves", rows);
        RenderTexture boxRipples = RippleTarget(1024, true), bandRipples = RippleTarget(1024, false);
        RenderTexture resolved = Target(width, height, false), history = Target(width, height, false);
        var read = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
        var lum = new float[3][];
        var raw = new float[3][];
        for (int k = 0; k < 3; k++)
        {
            lum[k] = new float[n];
            raw[k] = new float[n];
        }
        var flicker = new double[n];
        var flickerRaw = new double[n];
        var exposed = new Color[n];
        log.Add("taa: " + frames + " frames (" + warm + " to warm up) at " + fps + " a second; jitter " + spread + ", blending " + blend + ", sharpen " + sharpen);

        foreach (Seq s in seqs)
        {
            bool night = stage.Set(s.View);
            stage.SetCards(night, s.Card);
            float exposure = night ? exposureNight : exposureDay;
            material.SetVector("_RGWaterGeom", new Vector4(0f, s.Strength, s_skyShare, 0f));
            material.SetVector("_RGWaterWave", new Vector4(s.CityGain, s.Footprint, s.Cone, s.Distort));
            material.SetVector("_RGWaterLight", new Vector4(1f, 1f, 1f, 0f));
            RenderTexture ripples = s.FadeEnd > 0f ? bandRipples : boxRipples;
            stage.Water.SetTexture("_WaterNormal", ripples);
            material.SetTexture("_RGRipples", ripples);
            Array.Clear(flicker, 0, n);
            Array.Clear(flickerRaw, 0, n);
            bool[] mask = null;
            int measured = 0;

            // "cards 1": which pixels a card answers and which the screen does, in these frames (the cards'
            // check view, drawn once with the keyword on whatever the run's mode), for R7's fair comparison.
            bool[] byCard = null, byScreen = null;
            if (stage.Cards != null)
            {
                stage.SetCards(night, 2);
                Shader.SetGlobalVector("_SimulationTime", new Vector4(s.Seconds % 60f, (s.Seconds % 60f) / 60f, 0f, 0f));
                DrawRipples(generator, ripples, 0.5f * rippleRms * s.Ripples, s.FadeStart, s.FadeEnd);
                material.SetVector("_RGWaterHistoryInfo", Vector4.zero);
                camera.ResetProjectionMatrix();
                if (_gain != null)
                {
                    _gain.Reset();
                    _gain.Frame(camera, camera.pixelWidth, camera.pixelHeight, true);
                }
                camera.Render();
                RenderTexture.active = target;
                read.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                RenderTexture.active = null;
                Color[] dbg = read.GetPixels();
                byCard = new bool[n];
                byScreen = new bool[n];
                for (int p = 0; p < n; p++)
                {
                    byCard[p] = dbg[p].g - dbg[p].r > 0.3f;
                    byScreen[p] = dbg[p].r - dbg[p].g > 0.3f;
                }
                stage.SetCards(night, s.Card);
            }

            for (int f = 0; f < frames; f++)
            {
                int i = f % 8;
                float jx = (Halton(i + 1, 2) - 0.5f) * spread * s.Jitter, jy = (Halton(i + 1, 3) - 0.5f) * spread * s.Jitter;
                camera.ResetProjectionMatrix();
                Matrix4x4 projection = camera.projectionMatrix;
                projection.m02 += 2f * jx / width;
                projection.m12 += 2f * jy / height;
                camera.projectionMatrix = projection;

                // Sub-frames (measured frames only): the frame as a camera whose shutter is open the whole
                // frame sees it -- the mean of its light over the frame's time (the raw frame's flicker).
                int subs = f >= warm - 2 && !s.Frozen ? Math.Max(1, s.Subframes) : 1;
                for (int j = 0; j < subs; j++)
                {
                    float t = s.Seconds + (s.Frozen ? 0f : (f + (float)j / subs) / fps);
                    Shader.SetGlobalVector("_SimulationTime", new Vector4(t % 60f, (t % 60f) / 60f, 0f, 0f));
                    Shader.SetGlobalVector("_SimulationTime2", new Vector4(t, t / 3600f, 0f, 0f));
                    DrawRipples(generator, ripples, 0.5f * rippleRms * s.Ripples, s.FadeStart, s.FadeEnd);
                    // As WaterCityReflection.History (the camera stands still; no history on a run's first frame).
                    material.SetMatrix("_RGWaterPrevVP", camera.projectionMatrix * camera.worldToCameraMatrix);
                    material.SetVector("_RGWaterHistoryInfo", new Vector4(f == 0 || s.History <= 0f ? 0f : s.History, 2f / width, 2f / height, 0f));
                    // The cards' level, measured every frame (kept by RG_GAINKEEP as the mod would keep it).
                    if (_gain != null)
                        _gain.Frame(camera, camera.pixelWidth, camera.pixelHeight, true);
                    camera.Render();
                    if (subs > 1)
                        AddFrame(target, read, exposed, j == 0, 1f / subs);
                }

                resolve.SetTexture("_HistoryTex", history);
                resolve.SetVector("_Jitter", new Vector4(jx / width, jy / height, 0f, 0f));
                resolve.SetVector("_TAA", new Vector4(sharpen, blend, f == 0 ? 1f : 0f, 0f));
                Graphics.Blit(target, resolved, resolve, 0);
                Graphics.Blit(resolved, history);

                if (mask == null)
                    mask = WaterMask(trace, width, height);
                if (dump > 0 && f >= frames - dump)
                {
                    RenderTexture.active = target;
                    read.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    RenderTexture.active = null;
                    Color[] shot = subs > 1 ? exposed : read.GetPixels();
                    var toned = new Color[n];
                    for (int p = 0; p < n; p++)
                        toned[p] = new Color(Tone(shot[p].r * exposure), Tone(shot[p].g * exposure), Tone(shot[p].b * exposure), 1f);
                    WritePng(Path.Combine(outDir, s.Name + "-f" + (f - (frames - dump)) + ".png"), toned, width, height);
                }
                if (f >= warm - 2)
                {
                    Brightness(resolved, read, lum[f % 3], exposure);
                    if (subs > 1)
                        for (int p = 0; p < n; p++)
                            raw[f % 3][p] = Tone((0.2126f * exposed[p].r + 0.7152f * exposed[p].g + 0.0722f * exposed[p].b) * exposure);
                    else
                        Brightness(target, read, raw[f % 3], exposure);
                    if (f >= warm)
                    {
                        float[] a = lum[f % 3], b = lum[(f + 2) % 3], c = lum[(f + 1) % 3];
                        float[] ra = raw[f % 3], rb = raw[(f + 2) % 3], rc = raw[(f + 1) % 3];
                        for (int p = 0; p < n; p++)
                        {
                            flicker[p] += Math.Abs(a[p] - 2f * b[p] + c[p]);
                            flickerRaw[p] += Math.Abs(ra[p] - 2f * rb[p] + rc[p]);
                        }
                        measured++;
                    }
                }
            }
            camera.ResetProjectionMatrix();

            int water = 0, over2 = 0, over8 = 0, rawOver2 = 0, rawOver8 = 0;
            double sum = 0.0, rawSum = 0.0, bright = 0.0;
            float[] last = lum[(frames - 1) % 3];
            for (int p = 0; p < n; p++)
            {
                if (!mask[p])
                    continue;
                water++;
                double v = flicker[p] / Math.Max(1, measured), rv = flickerRaw[p] / Math.Max(1, measured);
                sum += v;
                rawSum += rv;
                bright += last[p];
                if (v > 2.0 / 255.0) over2++;
                if (v > 8.0 / 255.0) over8++;
                if (rv > 2.0 / 255.0) rawOver2++;
                if (rv > 8.0 / 255.0) rawOver8++;
            }
            double w = Math.Max(1, water);
            log.Add(string.Format(CultureInfo.InvariantCulture,
                "flicker {0}: water {1:F1}% of the frame, brightness {2:F3} | TAA: mean {3:F2}/255, over 2/255 {4:F2}%, over 8/255 {5:F2}% | no TAA: mean {6:F2}/255, over 2/255 {7:F2}%, over 8/255 {8:F2}%",
                s.Name, 100.0 * water / n, bright / w, 255.0 * sum / w, 100.0 * over2 / w, 100.0 * over8 / w, 255.0 * rawSum / w, 100.0 * rawOver2 / w, 100.0 * rawOver8 / w));
            if (byCard != null)
            {
                // The same frames, the water split by who answered it (R7).
                foreach (bool card in new[] { true, false })
                {
                    bool[] cls = card ? byCard : byScreen;
                    int cn = 0, c2 = 0, c8 = 0;
                    double cs = 0.0, cb = 0.0;
                    for (int p = 0; p < n; p++)
                    {
                        if (!mask[p] || !cls[p])
                            continue;
                        cn++;
                        double v = flicker[p] / Math.Max(1, measured);
                        cs += v;
                        cb += last[p];
                        if (v > 2.0 / 255.0) c2++;
                        if (v > 8.0 / 255.0) c8++;
                    }
                    double cw = Math.Max(1, cn);
                    log.Add(string.Format(CultureInfo.InvariantCulture,
                        "flicker {0} {1}: {2:F1}% of the water, brightness {3:F3} | TAA: mean {4:F2}/255, over 2/255 {5:F2}%, over 8/255 {6:F2}%, mean / brightness {7:F4}",
                        s.Name, card ? "where a card answers" : "where the screen answers", 100.0 * cn / Math.Max(1, water), cb / cw, 255.0 * cs / cw,
                        100.0 * c2 / cw, 100.0 * c8 / cw, cs / Math.Max(cb, 1e-6)));
                }
            }

            // The last resolved frame, and the flicker map: grey = the mean second difference x 25 on the
            // water (red over 8/255), the frame at a third elsewhere. With no jitter, the raw frame and
            // its own flicker (no TAA).
            bool noJitter = s.Jitter <= 0f;
            double[] shownFlicker = noJitter ? flickerRaw : flicker;
            RenderTexture.active = noJitter ? target : resolved;
            read.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            RenderTexture.active = null;
            Color[] px = noJitter && s.Subframes > 1 && !s.Frozen ? exposed : read.GetPixels();
            var picture = new Color[n];
            var map = new Color[n];
            for (int p = 0; p < n; p++)
            {
                Color c = new Color(Tone(px[p].r * exposure), Tone(px[p].g * exposure), Tone(px[p].b * exposure), 1f);
                picture[p] = c;
                float v = (float)(shownFlicker[p] / Math.Max(1, measured));
                map[p] = !mask[p] ? c * 0.33f : v > 8f / 255f ? new Color(1f, 0.15f, 0.1f, 1f) : new Color(Mathf.Min(1f, 25f * v), Mathf.Min(1f, 25f * v), Mathf.Min(1f, 25f * v), 1f);
                map[p].a = 1f;
            }
            WritePng(Path.Combine(outDir, s.Name + ".png"), picture, width, height);
            WritePng(Path.Combine(outDir, s.Name + "-flicker.png"), map, width, height);
        }
    }

    /// <summary>The frame's linear colour x weight, written into sum (first) or added to it.</summary>
    private static void AddFrame(RenderTexture rt, Texture2D read, Color[] sum, bool first, float weight)
    {
        RenderTexture.active = rt;
        read.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        RenderTexture.active = null;
        Color[] px = read.GetPixels();
        for (int p = 0; p < px.Length; p++)
            sum[p] = first ? px[p] * weight : sum[p] + px[p] * weight;
    }

    internal static void WritePng(string path, Color[] px, int width, int height)
    {
        var png = new Texture2D(width, height, TextureFormat.RGB24, false);
        png.SetPixels(px);
        png.Apply(false);
        File.WriteAllBytes(path, png.EncodeToPNG());
    }

    /// <summary>Each pixel's displayed brightness (0..1): its luminance x the exposure, tonemapped as the PNGs.</summary>
    internal static void Brightness(RenderTexture rt, Texture2D read, float[] into, float exposure)
    {
        RenderTexture.active = rt;
        read.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        RenderTexture.active = null;
        Color[] px = read.GetPixels();
        for (int p = 0; p < px.Length; p++)
            into[p] = Tone((0.2126f * px[p].r + 0.7152f * px[p].g + 0.0722f * px[p].b) * exposure);
    }

    /// <summary>The water: the trace's alpha (1 on the map's water), at the full size.</summary>
    private static bool[] WaterMask(RenderTexture trace, int width, int height)
    {
        int w = trace.width, h = trace.height;
        var read = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
        RenderTexture.active = trace;
        read.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        RenderTexture.active = null;
        Color[] px = read.GetPixels();
        var mask = new bool[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                mask[y * width + x] = px[Mathf.Min(h - 1, y / 2) * w + Mathf.Min(w - 1, x / 2)].a > 0.5f;
        return mask;
    }

    /// <summary>The share of a buffer's texels whose channel is above 0, and the rows they span (0 = bottom).</summary>
    private static string Coverage(RenderTexture rt, int channel)
    {
        if (rt == null)
            return "(none)";
        var read = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false, true);
        RenderTexture.active = rt;
        read.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        RenderTexture.active = null;
        Color[] px = read.GetPixels();
        int count = 0, low = -1, high = -1;
        for (int i = 0; i < px.Length; i++)
        {
            if (px[i][channel] <= 0f)
                continue;
            count++;
            int row = i / rt.width;
            if (low < 0)
                low = row;
            high = row;
        }
        UnityEngine.Object.DestroyImmediate(read);
        return (100.0 * count / px.Length).ToString("F1", CultureInfo.InvariantCulture) + "% of " + rt.width + " x " + rt.height +
               (count > 0 ? " (rows " + low + ".." + high + ")" : "");
    }

    private static RenderTexture RippleTarget(int size, bool autoMips)
    {
        var rt = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
        {
            useMipMap = true,
            autoGenerateMips = autoMips,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 4,
        };
        rt.Create();
        return rt;
    }

    /// <summary>
    /// The ripples for this frame: fadeEnd 0, the base level (Blit; the GPU averages the mips, as the
    /// mod today); else every mip level drawn by the generator (Graphics.SetRenderTarget(rt, level) and
    /// a quad as Blit draws it), each holding only the waves of fadeStart..fadeEnd texels or more there.
    /// </summary>
    public static void DrawRipples(Material generator, RenderTexture rt, float slope, float fadeStart, float fadeEnd)
    {
        if (fadeEnd <= 0f)
        {
            generator.SetVector("_RGRipple", new Vector4(slope, 0f, 0f, 0f));
            Graphics.Blit(Texture2D.blackTexture, rt, generator, 0);
            return;
        }

        // The base level holds every wave: the GPU reads it where the ripples are magnified, every
        // wave several pixels long.
        RenderTexture before = RenderTexture.active;
        int level = 0;
        for (int size = rt.width; size >= 1; size >>= 1, level++)
        {
            generator.SetVector("_RGRipple", new Vector4(slope, level == 0 ? 0f : size, fadeStart, fadeEnd));
            Graphics.SetRenderTarget(rt, level);
            GL.PushMatrix();
            GL.LoadOrtho();
            generator.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.TexCoord2(0f, 0f);
            GL.Vertex3(0f, 0f, 0f);
            GL.TexCoord2(1f, 0f);
            GL.Vertex3(1f, 0f, 0f);
            GL.TexCoord2(1f, 1f);
            GL.Vertex3(1f, 1f, 0f);
            GL.TexCoord2(0f, 1f);
            GL.Vertex3(0f, 1f, 0f);
            GL.End();
            GL.PopMatrix();
        }
        RenderTexture.active = before;
    }

    internal static float Halton(int index, int radix)
    {
        float result = 0f, fraction = 1f / radix;
        while (index > 0)
        {
            result += (index % radix) * fraction;
            index /= radix;
            fraction /= radix;
        }
        return result;
    }

    /// <summary>
    /// Towers of 4 x 4 m window bays across the water, x 520..1150 m, z -480..520 m; each its own
    /// facade colour by day and its own pattern of lit windows by night (Self-Illumin: the window's
    /// colour x its illumination).
    /// </summary>
    /// <summary>
    /// "cards 1": the harbour's towers built as the glass preview's stand-ins (a box, game-style maps,
    /// RealGlass/Preview/Building with the game's lit-window rule), and with them the mod's city map and
    /// each tower's facade card (CityMap, FacadeCards: the same files the mod has).
    /// </summary>
    private sealed class CardCity
    {
        public RealGlass.Glass.CityMap Map = new RealGlass.Glass.CityMap();
        public RealGlass.Glass.FacadeCards Cards;
    }

    private static readonly string[] CardKinds = { "curtain", "punched", "glassbox", "fins" };

    private static List<CityTower> BuildCity(Material waterMaterial, CardCity cards, List<string> log)
    {
        var towers = new List<CityTower>();
        var random = new System.Random(7);
        Texture2D day = Facade(false, random), night = Facade(true, random);
        Texture2D dayIllum = Illum(day), nightIllum = Illum(night);
        Color[] facades = { new Color(0.62f, 0.58f, 0.52f), new Color(0.45f, 0.50f, 0.56f), new Color(0.70f, 0.66f, 0.60f), new Color(0.38f, 0.36f, 0.34f), new Color(0.55f, 0.60f, 0.62f) };

        // The front row at the water's edge (x 520..), taller ones behind.
        float[,] spec =
        {
            // x, z, width, height
            { 540f, -420f, 44f, 90f }, { 560f, -300f, 36f, 140f }, { 545f, -180f, 50f, 70f }, { 570f, -60f, 40f, 180f },
            { 550f, 60f, 48f, 110f }, { 565f, 180f, 38f, 160f }, { 545f, 300f, 46f, 95f }, { 560f, 420f, 40f, 130f },
            { 700f, -360f, 52f, 230f }, { 720f, -120f, 46f, 260f }, { 690f, 120f, 56f, 210f }, { 730f, 340f, 44f, 250f },
            { 900f, -250f, 60f, 300f }, { 880f, 30f, 50f, 280f }, { 920f, 260f, 58f, 240f }, { 1100f, -80f, 70f, 330f },
        };
        if (cards != null)
        {
            Shader standIn = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Editor/PreviewBuilding.shader");
            Shader bake = AssetDatabase.LoadAssetAtPath<Shader>("Assets/RealGlass/FacadeCardBake.shader");
            cards.Cards = new RealGlass.Glass.FacadeCards(bake);
            cards.Cards.Log = line => log.Add(line);
            if (!cards.Cards.Create(2048, 32, 64))
                throw new Exception("FacadeCards.Create: " + cards.Cards.Problem);
            cards.Map.Begin(600f, 0f);
            cards.Cards.BeginBuildings(600f, 0f);
            Color[] glassColours = { new Color(0.25f, 0.36f, 0.45f), new Color(0.30f, 0.40f, 0.38f), new Color(0.36f, 0.32f, 0.27f), new Color(0.34f, 0.43f, 0.52f) };
            cards.Cards.FilteredIllumination = Environment.GetEnvironmentVariable("RG_CARDBAKE") == "filtered";
            // RG_BUILDINGS (";"-separated Workshop .crp files, read only): real building assets in the
            // towers' places, in turn (the preview's .crp reader, PreviewVehicles.cs).
            var real = new List<PreviewVehicleAsset>();
            string list = Environment.GetEnvironmentVariable("RG_BUILDINGS");
            if (!string.IsNullOrEmpty(list))
            {
                foreach (string path in list.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    try { real.Add(PreviewVehicleAsset.Load(path.Trim())); }
                    catch (Exception e) { log.Add("building not loaded: " + e.Message); }
                }
                log.Add("cards: " + real.Count + " real buildings in the harbour");
            }
            for (int i = 0; i < spec.GetLength(0); i++)
            {
                float x = spec[i, 0], z = spec[i, 1], width = spec[i, 2], height = spec[i, 3];
                int id = i + 1;
                Texture2D main, xys, aci;
                Mesh mesh;
                if (real.Count > 0)
                {
                    PreviewVehicleAsset a = real[i % real.Count];
                    main = a.Main; xys = a.Xys; aci = a.Aci; mesh = a.Mesh;
                }
                else
                {
                    GlassPreview.MakeFacade(CardKinds[i % CardKinds.Length], width, height, glassColours[i % glassColours.Length], 1000 + i, out main, out xys, out aci);
                    mesh = GlassPreview.BoxMesh(width, height, width);
                }
                var go = new GameObject("preview card tower " + id);
                go.transform.position = new Vector3(x, 0f, z);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var material = new Material(standIn);
                material.SetTexture("_MainTex", main);
                material.SetTexture("_XYSMap", xys);
                material.SetTexture("_ACIMap", aci);
                Renderer r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = material;
                var block = new MaterialPropertyBlock();
                block.SetVector("_ObjectIndex", GlassPreview.ColorIndex(id));
                r.SetPropertyBlock(block);
                towers.Add(new CityTower { Renderer = r, Day = material, Night = material });

                Bounds b = mesh.bounds;
                cards.Map.AddBuilding(go.transform.position, Quaternion.identity, b.min, b.max, null, id);
                int slot = cards.Cards.Bake(mesh, main, xys, aci, new Vector4(1f, 1f, 0f, 0f), b.min, b.max,
                                            RealGlass.Glass.GlassParams.Default().Threshold);
                cards.Cards.SetBuilding(id, go.transform.position, 0f, slot);
            }
            cards.Map.Finish();
            cards.Cards.Upload();
            log.Add("cards: " + cards.Cards.Slots + " towers baked, city map " + cards.Map.LiveCount + " buildings, highest roof " + cards.Map.LiveTop.ToString("F0") + " m");
            spec = new float[0, 4];
        }
        for (int i = 0; i < spec.GetLength(0); i++)
        {
            float x = spec[i, 0], z = spec[i, 1], width = spec[i, 2], height = spec[i, 3];
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.transform.localScale = new Vector3(width, height, width);
            box.transform.position = new Vector3(x, height / 2f, z);
            // One texture repeat = 8 x 8 bays = 32 m.
            var scale = new Vector2(width / 32f, height / 32f);
            var offset = new Vector2((float)random.NextDouble(), (float)random.NextDouble());
            var dayMaterial = new Material(Shader.Find("Legacy Shaders/Self-Illumin/Diffuse"))
            {
                color = facades[i % facades.Length], mainTexture = day, mainTextureScale = scale, mainTextureOffset = offset,
            };
            dayMaterial.SetTexture("_Illum", dayIllum);
            dayMaterial.SetTextureScale("_Illum", scale);
            dayMaterial.SetTextureOffset("_Illum", offset);
            var nightMaterial = new Material(Shader.Find("Legacy Shaders/Self-Illumin/Diffuse"))
            {
                color = Color.white, mainTexture = night, mainTextureScale = scale, mainTextureOffset = offset,
            };
            nightMaterial.SetTexture("_Illum", nightIllum);
            nightMaterial.SetTextureScale("_Illum", scale);
            nightMaterial.SetTextureOffset("_Illum", offset);
            Renderer renderer = box.GetComponent<Renderer>();
            renderer.sharedMaterial = dayMaterial;
            towers.Add(new CityTower { Renderer = renderer, Day = dayMaterial, Night = nightMaterial });
        }

        GameObject water = GameObject.CreatePrimitive(PrimitiveType.Plane);
        water.name = "preview water surface";
        water.GetComponent<Renderer>().sharedMaterial = waterMaterial;
        water.transform.localScale = new Vector3(110f, 1f, 300f);
        water.transform.position = new Vector3(0f, 0f, 0f);

        // The stand-in is forward-only, so it is not in the camera's depth texture, where the game's
        // water is (the city reflection finds the water there). A deferred plane 5 cm under it puts the
        // water's depth in; the stand-in, drawn later and nearer, covers it.
        GameObject depth = GameObject.CreatePrimitive(PrimitiveType.Plane);
        depth.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Legacy Shaders/Diffuse")) { color = new Color(0.035f, 0.07f, 0.09f) };
        depth.transform.localScale = new Vector3(110f, 1f, 300f);
        depth.transform.position = new Vector3(0f, -0.05f, 0f);

        // The quay the towers stand on, a little above the water.
        GameObject quay = GameObject.CreatePrimitive(PrimitiveType.Cube);
        quay.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Legacy Shaders/Diffuse")) { color = new Color(0.30f, 0.29f, 0.27f) };
        quay.transform.localScale = new Vector3(2000f, 4f, 3000f);
        quay.transform.position = new Vector3(520f + 1000f, 1f, 0f);
        return towers;
    }

    /// <summary>8 x 8 bays of 16 px (4 m): a window 3 x 2.6 m in each. By day dark glass in light walls (alpha 0: nothing glows); by night dark walls, about half the windows lit warm or cool (alpha 1 where lit).</summary>
    internal static Texture2D Facade(bool night, System.Random random)
    {
        const int bay = 16, bays = 8, size = bay * bays;
        var t = new Texture2D(size, size, TextureFormat.RGBA32, true, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8 };
        var px = new Color[size * size];
        for (int by = 0; by < bays; by++)
            for (int bx = 0; bx < bays; bx++)
            {
                bool lit = random.NextDouble() < 0.5;
                Color window = !night ? new Color(0.10f, 0.12f, 0.15f, 0f)
                    : lit ? (random.NextDouble() < 0.7 ? new Color(1f, 0.78f, 0.45f, 1f) : new Color(0.75f, 0.85f, 1f, 1f)) * (0.6f + 0.4f * (float)random.NextDouble())
                    : new Color(0.02f, 0.02f, 0.025f, 0f);
                if (night)
                    window.a = lit ? 1f : 0f;
                Color wall = night ? new Color(0.05f, 0.05f, 0.05f, 0f) : new Color(1f, 1f, 1f, 0f);
                for (int y = 0; y < bay; y++)
                    for (int x = 0; x < bay; x++)
                    {
                        bool glass = x >= 2 && x < 14 && y >= 3 && y < 13;
                        px[(by * bay + y) * size + bx * bay + x] = glass ? window : wall;
                    }
            }
        t.SetPixels(px);
        t.Apply(true);
        return t;
    }

    /// <summary>The illumination map Self-Illumin reads (its alpha): the facade's own alpha, x 8 for a lit window's glow.</summary>
    internal static Texture2D Illum(Texture2D facade)
    {
        var t = new Texture2D(facade.width, facade.height, TextureFormat.RGBA32, true, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8 };
        Color[] px = facade.GetPixels();
        for (int i = 0; i < px.Length; i++)
            px[i] = new Color(1f, 1f, 1f, px[i].a);
        t.SetPixels(px);
        t.Apply(true);
        return t;
    }

    internal static float Tone(float x)
    {
        float c = 1f - Mathf.Exp(-Mathf.Max(0f, x));
        return Mathf.Pow(c, 1f / 2.2f);
    }

    internal static Mesh UnitCube()
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

    internal static Cubemap Cube(Color colour)
    {
        var cube = new Cubemap(8, TextureFormat.RGBAFloat, false);
        var face = new Color[64];
        for (int i = 0; i < face.Length; i++)
            face[i] = colour;
        foreach (CubemapFace f in new[] { CubemapFace.PositiveX, CubemapFace.NegativeX, CubemapFace.PositiveY, CubemapFace.NegativeY, CubemapFace.PositiveZ, CubemapFace.NegativeZ })
            cube.SetPixels(face, f);
        cube.Apply(false);
        return cube;
    }

    internal static RenderTexture Target(int width, int height, bool mips)
    {
        var rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
        {
            useMipMap = mips,
            autoGenerateMips = mips,
            filterMode = mips ? FilterMode.Trilinear : FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        rt.Create();
        return rt;
    }

    internal static float F(string s)
    {
        return float.Parse(s, CultureInfo.InvariantCulture);
    }
}
