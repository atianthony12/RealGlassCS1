using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The water's sky cube (RealGlass/WaterCube) drawn the way WaterSurface does it -- the skybox of a
/// hidden forward camera with no layers, Camera.RenderToCubemap into a 64 px ARGBHalf cube render
/// texture -- and the centre of every face read back (Graphics.CopyTexture to a 2D render texture).
/// (RealLight's test, ported 2026-10-03.) tools\test-reflection-shader.ps1 writes cube-test-input.txt:
/// the table, the cube's uniforms, a theme's cube and a published real sky (one colour per face
/// each, so a face read back names its direction) and that sky's weight; it checks each face against
/// rho x gain x lerp(theme x light, real sky, weight) / light.
///   Unity.exe -batchmode -quit -projectPath . -executeMethod WaterCubeTest.Run
/// </summary>
public static class WaterCubeTest
{
    private static readonly CubemapFace[] Faces =
    {
        CubemapFace.PositiveX, CubemapFace.NegativeX, CubemapFace.PositiveY, CubemapFace.NegativeY, CubemapFace.PositiveZ, CubemapFace.NegativeZ,
    };

    public static void Run()
    {
        var output = new StringBuilder();
        string dir = Directory.GetCurrentDirectory();
        try
        {
            PlayerSettings.colorSpace = ColorSpace.Linear;
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/RealGlass/WaterCube.shader");
            output.AppendLine("shader " + (shader != null ? shader.name + " supported=" + shader.isSupported : "MISSING"));
            var material = new Material(shader);
            var table = new Vector4[33];
            var theme = new Color[6];
            var real = new Color[6];
            float weight = 0f;
            bool publish = false;
            foreach (string raw in File.ReadAllLines(Path.Combine(dir, "cube-test-input.txt")))
            {
                string[] p = raw.Split(' ');
                if (p[0] == "table")
                    table[int.Parse(p[1], CultureInfo.InvariantCulture)] = new Vector4(F(p[2]), 0f, 0f, 0f);
                else if (p[0] == "theme")
                    theme[int.Parse(p[1], CultureInfo.InvariantCulture)] = new Color(F(p[2]), F(p[3]), F(p[4]), 1f);
                else if (p[0] == "real")
                {
                    real[int.Parse(p[1], CultureInfo.InvariantCulture)] = new Color(F(p[2]), F(p[3]), F(p[4]), 1f);
                    publish = true;
                }
                else if (p[0] == "weight")
                    weight = F(p[1]);
                else if (p.Length == 5 && p[0].StartsWith("_RGWater"))
                    material.SetVector(p[0], new Vector4(F(p[1]), F(p[2]), F(p[3]), F(p[4])));
            }

            Shader.SetGlobalVectorArray("_RGSeaTable", table);
            material.SetTexture("_RGWaterTheme", Cube(theme));
            if (publish)
            {
                Shader.SetGlobalTexture("_RLSkyLightCube", Cube(real));
                Shader.SetGlobalFloat("_RLSkyLightCubeWeight", weight);
            }

            var cube = new RenderTexture(64, 64, 16, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                dimension = TextureDimension.Cube,
                useMipMap = true,
                autoGenerateMips = true,
            };
            cube.Create();

            var cameraObject = new GameObject("water sky camera");
            Camera capture = cameraObject.AddComponent<Camera>();
            capture.enabled = false;
            capture.clearFlags = CameraClearFlags.Skybox;
            capture.cullingMask = 0;
            capture.allowHDR = true;
            capture.allowMSAA = false;
            capture.renderingPath = RenderingPath.Forward;
            capture.nearClipPlane = 0.3f;
            capture.farClipPlane = 1000f;
            cameraObject.AddComponent<Skybox>().material = material;
            bool rendered = capture.RenderToCubemap(cube);
            output.AppendLine("rendered " + rendered);

            var face = new RenderTexture(64, 64, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            face.Create();
            var read = new Texture2D(64, 64, TextureFormat.RGBAFloat, false, true);
            foreach (CubemapFace f in Faces)
            {
                Graphics.CopyTexture(cube, (int)f, 0, face, 0, 0);
                RenderTexture.active = face;
                read.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
                RenderTexture.active = null;
                // The four centre texels' mean: the face's centre lies between them.
                Color c = (read.GetPixel(31, 31) + read.GetPixel(32, 31) + read.GetPixel(31, 32) + read.GetPixel(32, 32)) * 0.25f;
                output.AppendLine("face " + (int)f + " " + S(c.r) + " " + S(c.g) + " " + S(c.b));
            }
        }
        catch (Exception e)
        {
            output.AppendLine("EXCEPTION " + e);
        }

        File.WriteAllText(Path.Combine(dir, "cube-test-output.txt"), output.ToString());
    }

    /// <summary>
    /// THE MATCHED MAP (2026-11-20), through the mod's own GlassSky (synced into Assets/Sim): a live sky of one
    /// colour per face (Skybox/Cubemap over a float cube), a map of other colours (8 px with mips, as the
    /// mods' cubes have), GlassSky.Update with Match / MatchMap set. Checks, face by face, read back: the cube
    /// bound = the map x (the live cube's mean luminance over the nine upper-sky directions / the map's),
    /// both means from the colours read back; a map swapped in = all six faces at once; MatchMap null = the
    /// live cube bound again, Matching false. Writes PASS / FAIL lines to cube-match-output.txt.
    ///   Unity.exe -batchmode -quit -projectPath . -executeMethod WaterCubeTest.RunMatched
    /// </summary>
    public static void RunMatched()
    {
        var output = new StringBuilder();
        string dir = Directory.GetCurrentDirectory();
        int fails = 0;
        try
        {
            PlayerSettings.colorSpace = ColorSpace.Linear;
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/RealGlass/WaterCube.shader");
            output.AppendLine("shader " + (shader != null ? shader.name + " supported=" + shader.isSupported : "MISSING"));
            var match = new Material(shader);
            var sky = new Material(Shader.Find("Skybox/Cubemap"));
            Color[] live = { new Color(0.9f, 0.6f, 0.3f), new Color(0.2f, 0.3f, 0.4f), new Color(1.6f, 1.8f, 2.2f), new Color(0.05f, 0.05f, 0.05f), new Color(0.7f, 0.8f, 1.0f), new Color(0.4f, 0.45f, 0.5f) };
            Color[] mapA = { new Color(0.5f, 0.6f, 0.8f), new Color(0.3f, 0.4f, 0.6f), new Color(0.4f, 0.55f, 0.9f), new Color(0.1f, 0.1f, 0.1f), new Color(0.6f, 0.65f, 0.7f), new Color(0.35f, 0.4f, 0.5f) };
            Color[] mapB = { new Color(0.9f, 0.5f, 0.2f), new Color(0.8f, 0.4f, 0.2f), new Color(0.7f, 0.5f, 0.4f), new Color(0.2f, 0.1f, 0.1f), new Color(0.9f, 0.6f, 0.3f), new Color(0.6f, 0.4f, 0.3f) };
            sky.SetTexture("_Tex", Cube(live));
            RenderSettings.skybox = sky;

            var glassSky = new RealGlass.Glass.GlassSky { GlobalName = "_RGWaterSkyCube", Match = match, MatchMap = Cube(mapA, true) };
            glassSky.Update(true, 16, 2, null, Vector3.zero, false, false, null, 1f);
            output.AppendLine("first update: problem '" + glassSky.Problem + "', matching " + glassSky.Matching);
            fails += Expect(output, "matching after the first update", glassSky.Matching && glassSky.Problem.Length == 0);

            RenderTexture liveCube = ReadField(glassSky, "_cube");
            Color[] liveRead = Faces6(liveCube);
            Color[] liveCoarse = Faces6(liveCube, 2);
            for (int f = 0; f < 6; f++)
                output.AppendLine(string.Format(CultureInfo.InvariantCulture, "live face {0}: mip 0 {1:F4} {2:F4} {3:F4}, mip 2 {4:F4} {5:F4} {6:F4}", f,
                                                liveRead[f].r, liveRead[f].g, liveRead[f].b, liveCoarse[f].r, liveCoarse[f].g, liveCoarse[f].b));
            Color[] got = Faces6((RenderTexture)glassSky.Cube);
            fails += Expect(output, "the cube bound is the matched one, not the live", (object)glassSky.Cube != (object)liveCube && Shader.GetGlobalTexture("_RGWaterSkyCube") == glassSky.Cube);
            fails += CheckMatched(output, "map A", liveRead, mapA, got);

            glassSky.MatchMap = Cube(mapB, true);
            glassSky.Update(true, 16, 1, null, Vector3.zero, false, false, null, 1f);
            fails += CheckMatched(output, "map B swapped in (all six faces in one update)", Faces6(liveCube), mapB, Faces6((RenderTexture)glassSky.Cube));

            // Every face one colour, both cubes: the gain is the two colours' luminance ratio, whatever the directions.
            Color[] flatLive = new Color[6], flatMap = new Color[6];
            for (int f = 0; f < 6; f++)
            {
                flatLive[f] = new Color(1.2f, 1.0f, 0.8f);
                flatMap[f] = new Color(0.3f, 0.4f, 0.5f);
            }
            var flatSky = new RealGlass.Glass.GlassSky { GlobalName = "_RGTestFlat", Match = match, MatchMap = Cube(flatMap, true) };
            sky.SetTexture("_Tex", Cube(flatLive));
            flatSky.Update(true, 16, 6, null, Vector3.zero, false, false, null, 1f);
            fails += CheckMatched(output, "one colour everywhere", Faces6(ReadField(flatSky, "_cube")), flatMap, Faces6((RenderTexture)flatSky.Cube));
            flatSky.Destroy();
            sky.SetTexture("_Tex", Cube(live));

            glassSky.MatchMap = null;
            glassSky.Update(true, 16, 1, null, Vector3.zero, false, false, null, 1f);
            fails += Expect(output, "map off: the live cube bound again", !glassSky.Matching && (object)glassSky.Cube == (object)liveCube &&
                                                                          Shader.GetGlobalTexture("_RGWaterSkyCube") == liveCube);
            glassSky.Destroy();
        }
        catch (Exception e)
        {
            output.AppendLine("FAIL EXCEPTION " + e);
            fails++;
        }

        output.AppendLine(fails == 0 ? "MATCHED MAP TEST OK" : "MATCHED MAP TEST FAILED " + fails);
        File.WriteAllText(Path.Combine(dir, "cube-match-output.txt"), output.ToString());
    }

    private static int Expect(StringBuilder output, string what, bool ok)
    {
        output.AppendLine((ok ? "PASS " : "FAIL ") + what);
        return ok ? 0 : 1;
    }

    // The shader's nine directions: the zenith (+Y), four at 12 degrees (+-X, +-Z faces), four at 45 degrees (+Y).
    private static float UpperLuminance(Color[] faces)
    {
        Func<Color, float> lum = c => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        return (5f * lum(faces[2]) + lum(faces[0]) + lum(faces[1]) + lum(faces[4]) + lum(faces[5])) / 9f;
    }

    /// <summary>
    /// One colour everywhere: the gain exactly (0.5%). A colour per face: the model's gain (each face flat, the
    /// 45-degree samples all on +Y) is only near the shader's -- at the ~4 px mip the hardware filters those
    /// samples across into the side faces (measured: 0.95 of the model) -- so there the faces must share ONE
    /// gain (the map's colours, x one number, 0.5%), and that gain lie within 10% of the model's.
    /// </summary>
    private static int CheckMatched(StringBuilder output, string what, Color[] liveRead, Color[] map, Color[] got)
    {
        float model = UpperLuminance(liveRead) / UpperLuminance(map);
        bool flat = true;
        for (int f = 1; f < 6; f++)
            flat &= liveRead[f] == liveRead[0] && map[f] == map[0];
        Func<Color, float> lum = c => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        float gain = lum(got[2]) / lum(map[2]);   // the shader's own, read from the +Y face
        output.AppendLine(what + ": live mean " + UpperLuminance(liveRead).ToString("F4", CultureInfo.InvariantCulture) + ", map mean " +
                          UpperLuminance(map).ToString("F4", CultureInfo.InvariantCulture) + ", gain: model " + model.ToString("F4", CultureInfo.InvariantCulture) +
                          ", shader " + gain.ToString("F4", CultureInfo.InvariantCulture) + " (" + (gain / model).ToString("F4", CultureInfo.InvariantCulture) + " of it)");
        int fails = 0;
        if (flat)
            gain = model;
        else
            fails += Expect(output, "  " + what + ": the shader's gain within 10% of the flat-face model", Mathf.Abs(gain / model - 1f) < 0.10f);
        for (int f = 0; f < 6; f++)
        {
            Color want = map[f] * gain;
            float err = Mathf.Max(Mathf.Abs(got[f].r - want.r), Mathf.Max(Mathf.Abs(got[f].g - want.g), Mathf.Abs(got[f].b - want.b))) /
                        Mathf.Max(0.01f, Mathf.Max(want.r, Mathf.Max(want.g, want.b)));
            fails += Expect(output, string.Format(CultureInfo.InvariantCulture, "  {0} face {1}: got {2:F4} {3:F4} {4:F4}, want {5:F4} {6:F4} {7:F4} ({8:P2})",
                                                  what, f, got[f].r, got[f].g, got[f].b, want.r, want.g, want.b, err), err < 0.005f);
        }
        return fails;
    }

    private static RenderTexture ReadField(object o, string name)
    {
        return (RenderTexture)o.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(o);
    }

    /// <summary>The centre of each face of a cube render texture, read back.</summary>
    private static Color[] Faces6(RenderTexture cube)
    {
        return Faces6(cube, 0);
    }

    private static Color[] Faces6(RenderTexture cube, int mip)
    {
        int size = Mathf.Max(2, cube.width >> mip);
        var face = new RenderTexture(size, size, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        face.Create();
        var read = new Texture2D(size, size, TextureFormat.RGBAFloat, false, true);
        var result = new Color[6];
        for (int f = 0; f < 6; f++)
        {
            Graphics.CopyTexture(cube, (int)Faces[f], mip, face, 0, 0);
            RenderTexture.active = face;
            read.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            RenderTexture.active = null;
            int h = size / 2;
            result[f] = (read.GetPixel(h - 1, h - 1) + read.GetPixel(h, h - 1) + read.GetPixel(h - 1, h) + read.GetPixel(h, h)) * 0.25f;
        }
        face.Release();
        return result;
    }

    /// <summary>A float cube, one colour per face (linear: read back exactly); with mips, as a cubemap mod's.</summary>
    private static Cubemap Cube(Color[] colours, bool mips)
    {
        var cube = new Cubemap(8, TextureFormat.RGBAFloat, mips);
        var pixels = new Color[64];
        for (int f = 0; f < 6; f++)
        {
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = colours[f];
            cube.SetPixels(pixels, Faces[f]);
        }

        cube.Apply(mips);
        return cube;
    }

    /// <summary>A float cube, one colour per face (linear: read back exactly).</summary>
    private static Cubemap Cube(Color[] colours)
    {
        var cube = new Cubemap(8, TextureFormat.RGBAFloat, false);
        var pixels = new Color[64];
        for (int f = 0; f < 6; f++)
        {
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = colours[f];
            cube.SetPixels(pixels, Faces[f]);
        }

        cube.Apply(false);
        return cube;
    }

    private static float F(string s)
    {
        return float.Parse(s, CultureInfo.InvariantCulture);
    }

    private static string S(float v)
    {
        return v.ToString("R", CultureInfo.InvariantCulture);
    }
}
