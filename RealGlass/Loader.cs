using ColossalFramework.UI;
using ICities;
using UnityEngine;

namespace RealGlass
{
    /// <summary>
    /// Discovered automatically by the game alongside <see cref="Mod"/>. Owns the controller
    /// GameObject for the lifetime of a loaded city.
    /// </summary>
    public class Loader : LoadingExtensionBase
    {
        private static GameObject _root;
        private static bool _unsupportedShown;

        /// <summary>
        /// True once a city has stood down in this launch. A static: the machine does not change
        /// between cities.
        /// </summary>
        public static bool HasStoodDown { get; private set; }

        /// <summary>A city or an editor of ours is loaded (its prefabs ready): what the creators' files are read against.</summary>
        public static bool InLevel { get; private set; }

        public override void OnLevelLoaded(LoadMode mode)
        {
            base.OnLevelLoaded(mode);

            // A second copy of the mod, off since it was enabled: it says so once, and does nothing.
            if (ModCopies.StoodDown)
            {
                ModCopies.ShowOnce();
                return;
            }

            // Wherever there are buildings to see: a city, the asset editor (a creator checking the
            // glass), the scenario editor (a city). Not the map or theme editors: no buildings.
            string where = Where(mode);
            if (where == null)
                return;

            if (_root != null)
                return;

            Log.Msg("loaded: " + where);

            // The asset editor is the creators' (PerAsset.CreatorAsset): the creator window, the glass
            // of the building being made. A city reads its assets' creators' glass afresh.
            PerAsset.CreatorAsset.Enter(mode == LoadMode.NewAsset || mode == LoadMode.LoadAsset);
            PerAsset.CreatorDefinitions.Clear();
            InLevel = true;

            if (!CanDrawGlass())
            {
                StandDown();
                return;
            }

            _root = new GameObject("RealGlassController");
            Object.DontDestroyOnLoad(_root);
            _root.AddComponent<ModController>();

            Debug.Log("[RealGlass] Controller created.");
        }

        public override void OnLevelUnloading()
        {
            base.OnLevelUnloading();
            Shutdown();
        }

        /// <summary>
        /// Takes the controller down with everything it holds (the panel, the button, the picker,
        /// the camera's buffers, the water's materials given back). Called when a city unloads, and
        /// by the mod's OnDisabled: a mod disabled in the Content Manager while a city is loaded
        /// gets no OnLevelUnloading any more, and without this its controller would live on into
        /// the next city with the old city's references (audit, 2026-10-11).
        /// </summary>
        public static void Shutdown()
        {
            InLevel = false;
            PerAsset.CreatorAsset.Leave();
            PerAsset.CreatorDefinitions.Clear();

            if (_root == null)
                return;

            Object.Destroy(_root);
            _root = null;
        }

        /// <summary>Where the glass runs, in words for the log; null where it does not.</summary>
        private static string Where(LoadMode mode)
        {
            switch (mode)
            {
                case LoadMode.NewGame:
                case LoadMode.LoadGame:
                case LoadMode.NewGameFromScenario:
                    return "a city";
                case LoadMode.NewAsset:
                case LoadMode.LoadAsset:
                    return "the asset editor";
                case LoadMode.NewScenarioFromGame:
                case LoadMode.NewScenarioFromMap:
                case LoadMode.LoadScenario:
                case LoadMode.UpdateScenarioFromGame:
                case LoadMode.UpdateScenarioFromMap:
                    return "the scenario editor";
                default:
                    return null;
            }
        }

        /// <summary>
        /// The glass is drawn by shaders from the bundle built for this platform: Direct3D 11 or
        /// OpenGL Core on Windows, Metal or OpenGL Core on a Mac, OpenGL Core on Linux
        /// (<see cref="ShaderBundle"/>). Where the game runs on anything else, or the card cannot
        /// run shader model 3.5 / OpenGL 3.3, the glass shader is unsupported and ShaderBundle
        /// returns null: then nothing starts at all.
        /// </summary>
        private static bool CanDrawGlass()
        {
            return ShaderBundle.Get(ShaderBundle.Glass) != null;
        }

        /// <summary>
        /// No controller means nothing of ours runs in this city: no glass, no panel or button;
        /// the buildings are the game's. The Harmony patches applied in OnEnabled are inert
        /// without it.
        /// </summary>
        private static void StandDown()
        {
            HasStoodDown = true;

            // Always on: SystemReport, which would have named the machine, belongs to the
            // controller that is not being created.
            Log.Msg("NOT SUPPORTED here: the glass shader cannot run (" + SystemInfo.operatingSystem +
                    ", graphics " + SystemInfo.graphicsDeviceType + ", GPU '" + SystemInfo.graphicsDeviceName +
                    "'); the mod needs Direct3D 11 or OpenGL 3.3 on Windows, Metal or OpenGL 3.3 on a Mac, " +
                    "OpenGL 3.3 on Linux, and stays off in this city -- nothing of it runs");

            // Once per launch: every city load would be nagging.
            if (_unsupportedShown)
                return;

            _unsupportedShown = true;

            // Three texts, as Volumetric Weather's: Windows names the log to send (a real bug is
            // the one case worth a report); Linux says what the mod needs and the way out through
            // Proton; a Mac, or anything else, says what the mod needs.
            try
            {
                string message;
                switch (Application.platform)
                {
                    case RuntimePlatform.WindowsPlayer:
                    case RuntimePlatform.WindowsEditor:
                        message = Localization.Get("Mod.UnsupportedWindows", Log.Path);
                        break;
                    case RuntimePlatform.LinuxPlayer:
                    case RuntimePlatform.LinuxEditor:
                        message = Localization.Get("Mod.UnsupportedLinux");
                        break;
                    default:
                        message = Localization.Get("Mod.Unsupported");
                        break;
                }

                MessageWindow.Show(message);
            }
            catch (System.Exception e)
            {
                Log.Error("Could not show the 'not supported' message.", e);
            }
        }
    }
}
