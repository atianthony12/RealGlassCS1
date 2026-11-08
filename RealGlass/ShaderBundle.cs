using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace RealGlass
{
    /// <summary>
    /// The shaders compiled in Unity 5.6 and embedded in this assembly as AssetBundles: one per
    /// platform, because a bundle only holds code for the graphics APIs its build target allows
    /// (Direct3D 11 and OpenGL Core for Windows, Metal and OpenGL Core for the Mac, OpenGL Core for
    /// Linux; build-bundle.ps1). The one for <see cref="Application.platform"/> is loaded; the
    /// others stay unread inside the DLL.
    /// </summary>
    /// <remarks>
    /// Loaded once and the bundle released immediately (keeping the shaders alive): a bundle left
    /// loaded cannot be loaded a second time, which would break the mod's hot-reload loop.
    /// A bundle built for another standalone platform still LOADS (seen on a Mac with Volumetric
    /// Weather's Windows-only 1.1.0 bundle): every shader comes out with isSupported false, and
    /// Unity warns "Shader Unsupported ... Setting to default shader" for each. So a wrong pick is
    /// a stand-down (Loader), never a crash.
    /// </remarks>
    public static class ShaderBundle
    {
        /// <summary>The glass itself: drawn over every building, at every distance.</summary>
        public const string Glass = "RealGlass/Glass";

        /// <summary>The glass's helpers: full-screen copies, the city colour, the hierarchical depth.</summary>
        public const string GlassUtil = "RealGlass/GlassUtil";

        /// <summary>The Water tab: the theme's sky x real water's reflectance, drawn into the water's cube.</summary>
        public const string WaterCube = "RealGlass/WaterCube";

        /// <summary>The Water tab: the city in the map's water.</summary>
        public const string CityReflection = "RealGlass/CityReflection";

        /// <summary>The Water tab: the moving ripples, laid in place of the game's ripple texture.</summary>
        public const string WaterRipples = "RealGlass/WaterRipples";

        /// <summary>The Water tab: foam, drawn on the game's own water meshes after the game's water.</summary>
        public const string WaterFoam = "RealGlass/WaterFoam";

        public const string WaterSurfaceMask = "RealGlass/WaterSurfaceMask";

        /// <summary>The Water tab: the sky in the water, per pixel, drawn on the game's own water meshes.</summary>
        public const string WaterSky = "RealGlass/WaterSky";

        /// <summary>The Water tab: the ripples as an ocean's waves, by an inverse FFT of a wind-sea spectrum.</summary>
        public const string WaterWaves = "RealGlass/WaterWaves";

        /// <summary>The Vehicles tab: the glass of every vehicle near enough to be drawn in full, instanced per model.</summary>
        public const string VehicleGlass = "RealGlass/VehicleGlass";

        /// <summary>The Water tab: the view from under the water, full screen while the camera is below its level.</summary>
        public const string WaterUnder = "RealGlass/WaterUnder";

        /// <summary>The prop window: glass or water over the props a player chose, instanced per model.</summary>
        public const string PropGlass = "RealGlass/PropGlass";

        /// <summary>
        /// "RealGlass.Resources.realglass-win.bundle" and so on: the csproj's LogicalName for each
        /// bundle, with the name from <see cref="BundleFor"/> in between.
        /// </summary>
        private const string ResourceRoot = "RealGlass.Resources.";
        private const string ResourcePrefix = ResourceRoot + "realglass-";
        private const string ResourceSuffix = ".bundle";

        private static Dictionary<string, Shader> _shaders;

        /// <summary>
        /// Which bundle a platform gets: "win", "mac" or "linux"; null where none is built (the mod
        /// then stands down). The editor variants are for anyone running the mod inside an editor.
        /// </summary>
        public static string BundleFor(RuntimePlatform platform)
        {
            switch (platform)
            {
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor:
                    return "win";
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor:
                    return "mac";
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.LinuxEditor:
                    return "linux";
                default:
                    return null;
            }
        }

        /// <summary>Returns the named shader, or null if it is missing or unsupported here.</summary>
        public static Shader Get(string shaderName)
        {
            if (_shaders == null)
                _shaders = Load();

            Shader shader;
            if (!_shaders.TryGetValue(shaderName, out shader))
            {
                Log.Warn("Shader '" + shaderName + "' is not in the bundle.");
                return null;
            }

            if (!shader.isSupported)
            {
                Log.Error("Shader '" + shaderName + "' is not supported on this GPU/graphics API (" +
                          SystemInfo.graphicsDeviceType + ", '" + SystemInfo.graphicsDeviceName + "').");
                return null;
            }

            return shader;
        }

        private static Dictionary<string, Shader> Load()
        {
            Dictionary<string, Shader> result = new Dictionary<string, Shader>();

            try
            {
                string name = BundleFor(Application.platform);
                if (name == null)
                {
                    Log.Warn("No shader bundle is built for the platform '" + Application.platform +
                             "'; the mod cannot draw and stands down.");
                    return result;
                }

                string resource = ResourcePrefix + name + ResourceSuffix;
                byte[] data = ReadResource(resource);
                if (data == null)
                {
                    Log.Warn("Shader bundle '" + resource + "' is not embedded in this build (embedded: " +
                             string.Join(", ", Assembly.GetExecutingAssembly().GetManifestResourceNames()) +
                             "); the mod cannot draw and stands down. Run build-bundle.ps1, then rebuild the mod.");
                    return result;
                }

                AssetBundle bundle = AssetBundle.LoadFromMemory(data);
                if (bundle == null)
                {
                    Log.Error("Shader bundle '" + name + "' failed to load (built with a mismatched Unity version?).");
                    return result;
                }

                foreach (string asset in bundle.GetAllAssetNames())
                {
                    if (!asset.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
                        continue;

                    Shader shader = bundle.LoadAsset<Shader>(asset);
                    if (shader != null)
                        result[shader.name] = shader;
                }

                bundle.Unload(false);

                string[] names = new string[result.Count];
                result.Keys.CopyTo(names, 0);
                Log.Msg("shader bundle loaded: " + name + " (" + data.Length + " bytes) for " + Application.platform +
                        " on " + SystemInfo.graphicsDeviceType + ": " + string.Join(", ", names));
            }
            catch (Exception e)
            {
                Log.Error("Loading the shader bundle threw.", e);
            }

            return result;
        }

        /// <summary>An embedded resource's bytes (the bundles, the rooms' pictures); null when the DLL has none by that name.</summary>
        internal static byte[] ReadResource(string resource)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (stream == null)
                    return null;

                byte[] data = new byte[stream.Length];
                int offset = 0;

                while (offset < data.Length)
                {
                    int read = stream.Read(data, offset, data.Length - offset);
                    if (read <= 0)
                        break;
                    offset += read;
                }

                return data;
            }
        }
    }
}
