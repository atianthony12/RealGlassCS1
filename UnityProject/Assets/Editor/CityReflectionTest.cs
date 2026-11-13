using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The Water tab's city reflection (RealGlass/CityReflection) against the mod's model, in numbers,
/// not pictures (RealLight's test, ported 2026-10-03). tools\test-reflection-shader.ps1 writes
/// reflection-test-input.txt -- the reflection's uniforms (material), the reflectance table, the
/// theme's sky (one colour), the scene -- and runs
///   Unity.exe -batchmode -quit -projectPath . -executeMethod CityReflectionTest.Run
/// which draws the reflection the way the mod does (a half-size copy of the frame, the trace, the
/// streaks, then the cube, from a command buffer at AfterImageEffectsOpaque on a legacy-deferred HDR
/// camera; the ripple texture -- one value everywhere, or RealGlass/WaterRipples' -- and the game's
/// clocks from the file too) over a plane of
/// water at the sea's level and a lit wall standing past it. Each listed world point is rendered
/// with and without the reflection; reflection-test-output.txt has the pixel's own ray and both
/// colours.
/// </summary>
public static class CityReflectionTest
{
    private const int Size = 257;

    public static void Run()
    {
        var output = new StringBuilder();
        string dir = Directory.GetCurrentDirectory();
        try
        {
            PlayerSettings.colorSpace = ColorSpace.Linear;
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/RealGlass/CityReflection.shader");
            output.AppendLine("shader " + (shader != null ? shader.name + " supported=" + shader.isSupported : "MISSING"));
            var material = new Material(shader);

            var table = new Vector4[33];
            var ripples = new Vector4[64];
            bool generate = false;
            float rippleSlope = 0f;
            Color uniformRipple = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            var points = new List<string[]>();
            Vector3 eye = Vector3.zero, look = Vector3.forward, wallCentre = Vector3.zero, wallSize = Vector3.one;
            float level = 0f;
            Color waterColour = new Color(0.05f, 0.12f, 0.15f);
            Color wallColour = new Color(0.8f, 0.5f, 0.3f);   // "wallcolour r g b" for another (a dark tree's)
            Color theme = Color.black;
            foreach (string raw in File.ReadAllLines(Path.Combine(dir, "reflection-test-input.txt")))
            {
                string[] p = raw.Split(' ');
                if (p.Length == 5 && p[0].StartsWith("_RGWater"))
                    material.SetVector(p[0], new Vector4(F(p[1]), F(p[2]), F(p[3]), F(p[4])));
                else if (p[0] == "table")
                    table[int.Parse(p[1], CultureInfo.InvariantCulture)] = new Vector4(F(p[2]), 0f, 0f, 0f);
                else if (p[0] == "ripple")
                    ripples[int.Parse(p[1], CultureInfo.InvariantCulture)] = new Vector4(F(p[2]), F(p[3]), F(p[4]), F(p[5]));
                else if (p[0] == "generate")
                {
                    // The ripple texture made by RealGlass/WaterRipples at this root-mean-square slope.
                    generate = true;
                    rippleSlope = F(p[1]);
                }
                else if (p[0] == "uniform")
                    // One ripple everywhere: the texture's (x, y, z, w).
                    uniformRipple = new Color(F(p[1]), F(p[2]), F(p[3]), F(p[4]));
                else if (p[0] == "simtime")
                    Shader.SetGlobalVector("_SimulationTime", new Vector4(F(p[1]), F(p[2]), F(p[3]), F(p[4])));
                else if (p[0] == "simtime2")
                    Shader.SetGlobalVector("_SimulationTime2", new Vector4(F(p[1]), F(p[2]), F(p[3]), F(p[4])));
                else if (p[0] == "theme")
                    theme = new Color(F(p[1]), F(p[2]), F(p[3]), 1f);
                else if (p[0] == "realsky")
                {
                    // A published real sky (one colour) and its weight.
                    Shader.SetGlobalTexture("_RLSkyLightCube", Cube(new Color(F(p[1]), F(p[2]), F(p[3]), 1f)));
                    Shader.SetGlobalFloat("_RLSkyLightCubeWeight", F(p[4]));
                }
                else if (p[0] == "eye")
                    eye = new Vector3(F(p[1]), F(p[2]), F(p[3]));
                else if (p[0] == "look")
                    look = new Vector3(F(p[1]), F(p[2]), F(p[3]));
                else if (p[0] == "level")
                    level = F(p[1]);
                else if (p[0] == "water")
                    waterColour = new Color(F(p[1]), F(p[2]), F(p[3]));
                else if (p[0] == "wallcolour")
                    wallColour = new Color(F(p[1]), F(p[2]), F(p[3]));
                else if (p[0] == "wall")
                {
                    wallCentre = new Vector3(F(p[1]), F(p[2]), F(p[3]));
                    wallSize = new Vector3(F(p[4]), F(p[5]), F(p[6]));
                }
                else if (p[0] == "point")
                    points.Add(p);
            }

            Shader.SetGlobalVectorArray("_RGSeaTable", table);
            Texture rippleTexture = generate ? (Texture)GenerateRipples(ripples, rippleSlope, 256) : Uniform(uniformRipple);
            material.SetTexture("_RGRipples", rippleTexture);

            // The theme's sky: one colour every way.
            material.SetTexture("_RGWaterTheme", Cube(theme));

            RenderSettings.skybox = new Material(Shader.Find("Skybox/Procedural"));
            var lightObject = new GameObject("test sun");
            Light sun = lightObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.None;
            lightObject.transform.rotation = Quaternion.LookRotation(new Vector3(1f, -0.4f, 0.2f).normalized);

            var cameraObject = new GameObject("test camera");
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.renderingPath = RenderingPath.DeferredLighting;
            camera.allowHDR = true;
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.depthTextureMode = DepthTextureMode.Depth;
            camera.fieldOfView = 60f;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 20000f;
            var target = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            camera.targetTexture = target;
            cameraObject.transform.position = eye;
            cameraObject.transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);

            // The map's water: a plane at the sea's level up to the wall's face.
            float front = wallCentre.x - wallSize.x / 2f;
            GameObject water = GameObject.CreatePrimitive(PrimitiveType.Plane);
            water.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Legacy Shaders/Diffuse")) { color = waterColour };
            water.transform.localScale = new Vector3((front + 1000f) / 10f, 1f, 200f);
            water.transform.position = new Vector3((front - 1000f) / 2f, level, 0f);

            // The city: a lit wall standing in the water, its face toward the camera.
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Legacy Shaders/Diffuse")) { color = wallColour };
            wall.transform.localScale = wallSize;
            wall.transform.position = wallCentre;

            var mesh = new Mesh();
            mesh.vertices = new[]
            {
                new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(1, 1, -1), new Vector3(-1, 1, -1),
                new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(1, 1, 1), new Vector3(-1, 1, 1),
            };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5 };
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);

            // As WaterCityReflection.Record.
            RenderTexture frame = Target(Size / 2, false), trace = Target(Size / 2, true), streaked = Target(Size / 2, false);
            material.SetTexture("_RGWaterFrame", frame);
            var buffer = new CommandBuffer { name = "RealGlass water reflection (test)" };
            PreviewWaterSurface.Record(buffer, material, water, Size, Size);
            buffer.Blit(BuiltinRenderTextureType.CameraTarget, frame);
            buffer.SetRenderTarget(trace);
            buffer.ClearRenderTarget(false, true, Color.clear);
            buffer.DrawMesh(mesh, Matrix4x4.identity, material, 0, 0);
            // The history passes with no history (_RGWaterHistoryInfo stays 0): they read the trace's
            // packed alpha and write the light and the share covered, each to its own buffer.
            RenderTexture accum = Target(Size / 2, true), accum2 = Target(Size / 2, true), streaked2 = Target(Size / 2, false);
            material.SetTexture("_RGWaterTraced", trace);
            buffer.SetRenderTarget(accum);
            buffer.DrawMesh(mesh, Matrix4x4.identity, material, 0, 3);
            buffer.Blit(accum, streaked, material, 1);
            buffer.SetGlobalTexture("_RGWaterStreaked", streaked);
            buffer.SetRenderTarget(accum2);
            buffer.DrawMesh(mesh, Matrix4x4.identity, material, 0, 4);
            buffer.Blit(accum2, streaked2, material, 1);
            buffer.SetGlobalTexture("_RGWaterStreaked2", streaked2);
            buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            buffer.DrawMesh(mesh, Matrix4x4.identity, material, 0, 2);

            Color[] plain = GrabAll(camera, target);
            camera.AddCommandBuffer(CameraEvent.AfterImageEffectsOpaque, buffer);
            Color[] got = GrabAll(camera, target);
            camera.RemoveCommandBuffer(CameraEvent.AfterImageEffectsOpaque, buffer);

            foreach (string[] p in points)
            {
                Vector3 world = new Vector3(F(p[2]), F(p[3]), F(p[4]));
                Vector3 v = camera.WorldToViewportPoint(world);
                int px = Mathf.Clamp(Mathf.FloorToInt(v.x * Size), 0, Size - 1), py = Mathf.Clamp(Mathf.FloorToInt(v.y * Size), 0, Size - 1);
                Vector3 ray = camera.ViewportPointToRay(new Vector3((px + 0.5f) / Size, (py + 0.5f) / Size, 0f)).direction.normalized;
                int at = py * Size + px;
                output.AppendLine("point " + p[1] + " px " + px + " " + py + " ray " + S(ray.x) + " " + S(ray.y) + " " + S(ray.z) +
                                  " plain " + S(plain[at]) + " got " + S(got[at]));
            }
        }
        catch (Exception e)
        {
            output.AppendLine("EXCEPTION " + e);
        }

        File.WriteAllText(Path.Combine(dir, "reflection-test-output.txt"), output.ToString());
    }

    /// <summary>A float texture of one value everywhere (read back exactly: no 8-bit rounding).</summary>
    private static Texture2D Uniform(Color value)
    {
        var t = new Texture2D(4, 4, TextureFormat.RGBAFloat, true, true) { wrapMode = TextureWrapMode.Repeat };
        var px = new Color[16];
        for (int i = 0; i < px.Length; i++)
            px[i] = value;
        t.SetPixels(px);
        t.Apply(true);
        return t;
    }

    /// <summary>The ripple texture as WaterRipples draws it: RealGlass/WaterRipples into an ARGB32 linear target with mips.</summary>
    public static RenderTexture GenerateRipples(Vector4[] rows, float slope, int size)
    {
        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/RealGlass/WaterRipples.shader");
        var generator = new Material(shader);
        generator.SetVectorArray("_RGRippleWaves", rows);
        generator.SetVector("_RGRipple", new Vector4(slope, 0f, 0f, 0f));
        var rt = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
        {
            useMipMap = true,
            autoGenerateMips = true,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,
        };
        rt.Create();
        Graphics.Blit(Texture2D.blackTexture, rt, generator, 0);
        return rt;
    }

    /// <summary>A float cube of one colour every way (linear: read back exactly).</summary>
    private static Cubemap Cube(Color colour)
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

    private static RenderTexture Target(int size, bool mips)
    {
        var rt = new RenderTexture(size, size, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
        {
            useMipMap = mips,
            autoGenerateMips = mips,
            filterMode = mips ? FilterMode.Trilinear : FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        rt.Create();
        return rt;
    }

    private static Color[] GrabAll(Camera camera, RenderTexture target)
    {
        camera.Render();
        RenderTexture.active = target;
        var texture = new Texture2D(Size, Size, TextureFormat.RGBAFloat, false, true);
        texture.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
        RenderTexture.active = null;
        return texture.GetPixels();
    }

    private static float F(string s)
    {
        return float.Parse(s, CultureInfo.InvariantCulture);
    }

    private static string S(float v)
    {
        return v.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string S(Color c)
    {
        return S(c.r) + " " + S(c.g) + " " + S(c.b);
    }
}
