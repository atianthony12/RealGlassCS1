using System;
using System.IO;
using RealGlass.Glass;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Bakes the dirt on the glass (Glass/DirtTextures.cs, Dirt.cginc) from three of ambientCG's CC0 maps
/// (tools/dirt/build-dirt.ps1 fetches them): RG_DIRT_FILM (Surface Imperfections 003's opacity),
/// RG_DIRT_SPOTS (Surface Imperfections 001's), RG_DIRT_RUNS (Leaking 004's) -> RG_DIRT_OUT\
/// dirt-grime.bytes (1024 x 1024 DXT5: a film, g spots) and dirt-runs.bytes (1024 x 512 Alpha8).
/// Mips box-filtered, linear. Then CHECKS what it wrote: each file loaded by the mod's own loader,
/// drawn by the GPU into a render texture, read back and compared with the source map (DXT5's alpha
/// and Alpha8 must come back near the source; a DXT5 load that came back wrong would show as a large
/// error). Run with graphics (no -nographics):
///   Unity.exe -batchmode -quit -projectPath UnityProject -executeMethod DirtBaker.Bake
/// </summary>
public static class DirtBaker
{
    private const string Folder = "Assets/DirtBake";
    private const int Version = 1;

    public static void Bake()
    {
        try
        {
            string film = Environment.GetEnvironmentVariable("RG_DIRT_FILM");
            string spots = Environment.GetEnvironmentVariable("RG_DIRT_SPOTS");
            string runs = Environment.GetEnvironmentVariable("RG_DIRT_RUNS");
            string output = Environment.GetEnvironmentVariable("RG_DIRT_OUT");
            if (string.IsNullOrEmpty(film) || string.IsNullOrEmpty(spots) || string.IsNullOrEmpty(runs) || string.IsNullOrEmpty(output))
                throw new Exception("RG_DIRT_FILM, RG_DIRT_SPOTS, RG_DIRT_RUNS and RG_DIRT_OUT must be set");

            byte[] f = Grey(film, 1024, 1024), s = Grey(spots, 1024, 1024), r = Grey(runs, 1024, 512);
            string grimeFile = Path.Combine(output, "dirt-grime.bytes");
            string runsFile = Path.Combine(output, "dirt-runs.bytes");
            if (Environment.GetEnvironmentVariable("RG_DIRT_CHECK_ONLY") != "1")
                Make(f, s, r, grimeFile, runsFile);

            // The check: through the mod's loader and the GPU.

            var dirt = new DirtTextures();
            if (!dirt.Load(File.ReadAllBytes(grimeFile), File.ReadAllBytes(runsFile)))
                throw new Exception("the mod's loader refused the files: " + dirt.Problem);
            Color32[] g = ReadBack(dirt.Grime);
            Color32[] rr = ReadBack(dirt.Runs);
            double filmDb = Psnr(f, g, 3), spotsDb = Psnr(s, g, 1), runsDb = Psnr(r, rr, 3);
            Debug.Log(string.Format("DIRT CHECK (GPU read-back, {0}): film {1:F1} dB, spots {2:F1} dB, runs {3:F1} dB; means {4:F4} {5:F4} {6:F4}",
                                    SystemInfo.graphicsDeviceType, filmDb, spotsDb, runsDb, dirt.Means.x, dirt.Means.y, dirt.Means.z));
            dirt.Destroy();
            if (filmDb < 45.0 || spotsDb < 38.0 || runsDb < 48.0)
                throw new Exception("the textures came back from the GPU too far from their sources");
            Debug.Log("DIRT BAKE OK");
        }
        catch (Exception e)
        {
            Debug.LogError("DIRT BAKE FAILED: " + e);
            EditorApplication.Exit(1);
        }
    }

    private static void Make(byte[] f, byte[] s, byte[] r, string grimeFile, string runsFile)
    {
        Directory.CreateDirectory(Folder);

        // GRIME: g the spots, a the film, r = b = 0 (each of the two a block of its own in DXT5).
        var grime = new Color32[1024 * 1024];
        for (int i = 0; i < grime.Length; i++)
            grime[i] = new Color32(0, s[i], 0, f[i]);
        Write(grime, 1024, 1024, "dirt-grime", TextureImporterFormat.DXT5, TextureFormat.DXT5, Mean(f), Mean(s), grimeFile);

        // RUNS: the value in a (Alpha8 keeps a alone).
        var run = new Color32[1024 * 512];
        for (int i = 0; i < run.Length; i++)
            run[i] = new Color32(255, 255, 255, r[i]);
        Write(run, 1024, 512, "dirt-runs", TextureImporterFormat.Alpha8, TextureFormat.Alpha8, Mean(r), 0f, runsFile);
        AssetDatabase.DeleteAsset(Folder);
    }

    /// <summary>A map as grey bytes at this size (the source's average of r, g, b), row 0 the bottom (Unity's).</summary>
    private static byte[] Grey(string png, int w, int h)
    {
        var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
        if (!t.LoadImage(File.ReadAllBytes(png)))
            throw new Exception(png + ": not a picture");
        if (t.width != w || t.height != h)
            throw new Exception(png + ": " + t.width + "x" + t.height + ", expected " + w + "x" + h);
        Color32[] p = t.GetPixels32();
        UnityEngine.Object.DestroyImmediate(t);
        var o = new byte[w * h];
        for (int i = 0; i < o.Length; i++)
            o[i] = (byte)((p[i].r + p[i].g + p[i].b + 1) / 3);
        return o;
    }

    private static float Mean(byte[] v)
    {
        double sum = 0;
        for (int i = 0; i < v.Length; i++)
            sum += v[i];
        return (float)(sum / v.Length / 255.0);
    }

    private static void Write(Color32[] pixels, int w, int h, string name, TextureImporterFormat importAs, TextureFormat expect,
                              float mean0, float mean1, string bytes)
    {
        var src = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
        src.SetPixels32(pixels);
        src.Apply(false, false);
        string asset = Folder + "/" + name + ".png";
        File.WriteAllBytes(asset, src.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(src);

        AssetDatabase.ImportAsset(asset, ImportAssetOptions.ForceSynchronousImport);
        var importer = (TextureImporter)AssetImporter.GetAtPath(asset);
        importer.textureType = TextureImporterType.Default;
        importer.sRGBTexture = false;
        importer.alphaSource = TextureImporterAlphaSource.FromInput;
        importer.alphaIsTransparency = false;
        importer.mipmapEnabled = true;
        importer.mipmapFilter = TextureImporterMipFilter.BoxFilter;
        importer.wrapMode = TextureWrapMode.Repeat;
        importer.isReadable = false;
        importer.npotScale = TextureImporterNPOTScale.None;
        importer.maxTextureSize = 4096;
        importer.textureCompression = TextureImporterCompression.CompressedHQ;
        importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings
        {
            name = "Standalone",
            overridden = true,
            maxTextureSize = 4096,
            format = importAs,
            compressionQuality = 100,
        });
        importer.SaveAndReimport();

        var t = AssetDatabase.LoadAssetAtPath<Texture2D>(asset);
        if (t == null || t.format != expect || t.width != w || t.height != h)
            throw new Exception(name + ": imported as " + (t == null ? "nothing" : t.format + " " + t.width + "x" + t.height) + ", not " + expect);
        byte[] data = t.GetRawTextureData();

        using (var o = new BinaryWriter(File.Create(bytes)))
        {
            o.Write(new[] { (byte)'R', (byte)'G', (byte)'D', (byte)'T' });
            o.Write(Version);
            o.Write(t.width);
            o.Write(t.height);
            o.Write(t.mipmapCount);
            o.Write((int)expect);
            o.Write(mean0);
            o.Write(mean1);
            o.Write(data.Length);
            o.Write(data);
        }
        Debug.Log("DIRT " + Path.GetFileName(bytes) + ": " + t.width + "x" + t.height + " " + expect + ", " + t.mipmapCount + " mips, " +
                  data.Length + " bytes, means " + mean0.ToString("F4") + " / " + mean1.ToString("F4"));
    }

    /// <summary>The texture's top mip as the GPU samples it: drawn into a linear render texture and read back.</summary>
    private static Color32[] ReadBack(Texture2D t)
    {
        var rt = new RenderTexture(t.width, t.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        rt.Create();
        t.filterMode = FilterMode.Point;   // texel for texel, top mip
        Graphics.Blit(t, rt);
        t.filterMode = FilterMode.Trilinear;
        var read = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false, true);
        RenderTexture.active = rt;
        read.ReadPixels(new Rect(0, 0, t.width, t.height), 0, 0, false);
        read.Apply(false, false);
        RenderTexture.active = null;
        Color32[] p = read.GetPixels32();
        UnityEngine.Object.DestroyImmediate(read);
        rt.Release();
        UnityEngine.Object.DestroyImmediate(rt);
        return p;
    }

    private static double Psnr(byte[] src, Color32[] got, int channel)
    {
        double se = 0;
        for (int i = 0; i < src.Length; i++)
        {
            Color32 c = got[i];
            int v = channel == 0 ? c.r : channel == 1 ? c.g : channel == 2 ? c.b : c.a;
            double d = v - src[i];
            se += d * d;
        }
        double mse = se / src.Length;
        return mse <= 1e-9 ? 99.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }
}
