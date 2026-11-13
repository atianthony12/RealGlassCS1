using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds the shader bundles the mod embeds: one per platform, because a bundle only holds
/// code for the graphics APIs of its build target (and the Mac's Metal can only come out of
/// a Mac-target build). Run headless:
///   Unity.exe -batchmode -nographics -quit -projectPath . -executeMethod BundleBuilder.Build
/// Needs the Mac and Linux Build Support modules of this exact editor version installed.
/// (Volumetric Weather's builder, with this mod's names.)
/// </summary>
public static class BundleBuilder
{
    private const string OutputDir = "Bundles";
    private const string BundleName = "realglass";

    /// <summary>
    /// Every shader the mod ships. Add new ones here and nowhere else: build-bundle.ps1 reads
    /// this list out of this file and makes tools/bundle-apis.ps1 check every name in it.
    /// </summary>
    private static readonly string[] Shaders =
    {
        "Assets/RealGlass/Glass.shader",
        "Assets/RealGlass/GlassUtil.shader",
        "Assets/RealGlass/WaterCube.shader",
        "Assets/RealGlass/CityReflection.shader",
        "Assets/RealGlass/WaterRipples.shader",
        "Assets/RealGlass/WaterFoam.shader",
        "Assets/RealGlass/WaterSurfaceMask.shader",
        "Assets/RealGlass/WaterSky.shader",
        "Assets/RealGlass/WaterWaves.shader",
        "Assets/RealGlass/VehicleGlass.shader",
        "Assets/RealGlass/WaterUnder.shader",
        "Assets/RealGlass/PropGlass.shader",
        "Assets/RealGlass/GlassPrePass.shader",
        "Assets/RealGlass/GlassReflection.shader",
        "Assets/RealGlass/FacadeCardBake.shader",
        "Assets/RealGlass/FacadeCardGain.shader",
    };

    /// <summary>
    /// Textures the mod ships in the bundles: none now (the pictured rooms that were here went in
    /// round 7). One listed here must exist, and goes into every bundle.
    /// </summary>
    private static readonly string[] Textures = { };

    private struct Target
    {
        public string Name;
        public BuildTarget BuildTarget;
        public GraphicsDeviceType[] Apis;
    }

    /// <summary>
    /// The three bundles and the graphics APIs each carries. The names are the resource names
    /// ("realglass-win.bundle"...) ShaderBundle picks from by Application.platform.
    /// </summary>
    private static readonly Target[] Targets =
    {
        new Target
        {
            Name = "win",
            BuildTarget = BuildTarget.StandaloneWindows64,
            Apis = new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.OpenGLCore },
        },
        new Target
        {
            Name = "linux",
            BuildTarget = BuildTarget.StandaloneLinux64,
            Apis = new[] { GraphicsDeviceType.OpenGLCore },
        },
        new Target
        {
            Name = "mac",
            BuildTarget = BuildTarget.StandaloneOSXUniversal,
            Apis = new[] { GraphicsDeviceType.Metal, GraphicsDeviceType.OpenGLCore },
        },
    };

    /// <summary>
    /// One UNCOMPRESSED bundle per target in Bundles/[name]/realglass, and that exact file is what
    /// ships: tools/bundle-apis.ps1 can only read an uncompressed bundle, and what it verifies
    /// must be the bytes that are embedded.
    /// </summary>
    public static void Build()
    {
        // "-bundleTargets win,linux" (build-bundle.ps1 -Only) builds just those; default all.
        string only = null;
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "-bundleTargets")
                only = "," + args[i + 1] + ",";
        }

        foreach (Target target in Targets)
        {
            if (only != null && only.IndexOf("," + target.Name + ",", System.StringComparison.Ordinal) < 0)
                continue;
            if (!BuildOne(target))
            {
                EditorApplication.Exit(1);
                return;
            }
        }

        Debug.Log("BUNDLE BUILD ALL OK");
    }

    /// <summary>
    /// The vehicles' glass is drawn with GPU instancing (Graphics.DrawMeshInstanced), so its shader's
    /// INSTANCING_ON variant must be in the bundles. Unity 5.6 strips instancing variants that no
    /// material in the build asks for ("Instancing Variants: Strip Unused", Graphics settings), and
    /// these bundles hold shaders, no materials: the first build of VehicleGlass had 2 programs an API,
    /// the instanced pair gone, silently. So the setting is put to keep them all, read by its own
    /// option names (the serialized enum), and logged.
    /// </summary>
    private static bool KeepInstancingVariants()
    {
        Object[] settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
        SerializedProperty p = settings.Length > 0 ? new SerializedObject(settings[0]).FindProperty("m_InstancingStripping") : null;
        if (p == null)
        {
            Debug.LogError("BUNDLE BUILD FAILED: no m_InstancingStripping in the Graphics settings");
            return false;
        }

        string[] names = p.propertyType == SerializedPropertyType.Enum ? p.enumNames : new string[0];
        int keep = System.Array.FindIndex(names, n => n.Replace(" ", "").ToLowerInvariant() == "keepall");
        if (keep < 0)
        {
            Debug.LogError("BUNDLE BUILD FAILED: m_InstancingStripping has no \"Keep All\" (" + p.propertyType + ": " + string.Join(", ", names) + ")");
            return false;
        }

        if (p.enumValueIndex != keep)
        {
            Debug.Log("BUNDLE BUILD: instancing variants were \"" + names[p.enumValueIndex] + "\"; now \"" + names[keep] + "\"");
            p.enumValueIndex = keep;
            p.serializedObject.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
        }
        else
        {
            Debug.Log("BUNDLE BUILD: instancing variants \"" + names[keep] + "\"");
        }
        return true;
    }

    private static bool BuildOne(Target target)
    {
        if (!KeepInstancingVariants())
            return false;
        PlayerSettings.SetUseDefaultGraphicsAPIs(target.BuildTarget, false);
        PlayerSettings.SetGraphicsAPIs(target.BuildTarget, target.Apis);

        GraphicsDeviceType[] readBack = PlayerSettings.GetGraphicsAPIs(target.BuildTarget);
        string list = "";
        for (int i = 0; i < readBack.Length; i++)
            list += (i > 0 ? ", " : "") + readBack[i];

        string dir = Path.Combine(OutputDir, target.Name);
        Directory.CreateDirectory(dir);

        foreach (string texture in Textures)
        {
            if (AssetDatabase.LoadAssetAtPath<Texture2D>(texture) == null)
            {
                Debug.LogError("BUNDLE BUILD " + target.Name + " FAILED: " + texture + " is missing");
                return false;
            }
        }

        string[] assets = new string[Shaders.Length + Textures.Length];
        Shaders.CopyTo(assets, 0);
        Textures.CopyTo(assets, Shaders.Length);
        AssetBundleBuild build = new AssetBundleBuild
        {
            assetBundleName = BundleName,
            assetNames = assets,
        };

        // Always rebuild. The incremental check only hashes the .shader files themselves,
        // so an edit confined to GlassCommon.cginc would otherwise yield a byte-identical,
        // stale bundle -- with no error anywhere (Volumetric Weather lost a day to that).
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
            dir,
            new[] { build },
            BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.UncompressedAssetBundle,
            target.BuildTarget);

        string what = "BUNDLE BUILD " + target.Name + " (" + target.BuildTarget + ", apis=[" + list + "])";

        if (manifest == null)
        {
            Debug.LogError(what + " FAILED");
            return false;
        }

        Debug.Log("BUNDLE BUILD OK: " + target.Name + " -> " + Path.Combine(dir, BundleName) + "  [" + what + "]");
        return true;
    }
}
