using ICities;
using RealGlass.UI;

namespace RealGlass
{
    public class Mod : IUserMod
    {
        /// <summary>
        /// The mod's name wherever a player reads it: the mods list, the options page, the
        /// panel's title, button tooltips, dialog titles, the log's first line. Files, the
        /// assembly, the namespace and the button IDs stay "RealGlass".
        /// </summary>
        public const string DisplayName = "Real Glass & Water";

        /// <summary>
        /// The release a player has: raised once per Workshop upload (the third number for a
        /// fix or a compatibility change, the second for new features), with its own section in
        /// CHANGELOG.md. Also the first three parts of the assembly version
        /// (Properties/AssemblyInfo.cs). 1.0.0 is the first Workshop release.
        /// </summary>
        public const string Version = "2.0.0";

        /// <summary>
        /// The mods list and the options page show the version after the name, so a bug report
        /// can say which release it was. The game reads Name for display only (mods list,
        /// options category, telemetry); whether a mod is enabled is keyed on its folder, so a
        /// new number each release changes nothing a player has saved.
        /// </summary>
        public string Name => DisplayName + " " + Version;
        public string Description => Localization.Get("Mod.Description");

        /// <summary>Called by the game via reflection when the mod is enabled.</summary>
        public void OnEnabled()
        {
            // Another copy of the mod already running: this one stays off, touching nothing of it --
            // not its log, its settings file or its hooks (ModCopies).
            if (!ModCopies.Claim())
            {
                Log.Continue();
                Log.Warn("A second copy of the mod (" + ModCopies.Here + ", build " + typeof(Mod).Assembly.GetName().Version +
                         ") stays off: the copy in " + ModCopies.Running + " is running. Keep one of them.");
                return;
            }

            Log.Start();
            Log.Msg("Mod enabled. Log file: " + Log.Path);

            // Reads RealGlass.xml and starts the saver that writes changes back and picks up
            // edits made to the file by hand.
            Settings.Init();
            SettingsXml.StartSaver(); // again after a disable/enable, when Init has nothing left to do

            Patcher.EnsureHarmony();
            Patcher.PatchOnReady();
        }

        /// <summary>Called by the game via reflection when the mod is disabled.</summary>
        public void OnDisabled()
        {
            // A copy that stayed off started nothing; the running copy's Harmony ID is the same as
            // ours, so above all no Unpatch from here.
            if (ModCopies.StoodDown)
                return;

            try
            {
                // First: the controller gives the camera and the water back while the patches are
                // still in place, and nothing of ours runs on in the city after the mod is off.
                Loader.Shutdown();
                Patcher.Unpatch();
                SettingsXml.Shutdown();
            }
            finally
            {
                ModCopies.Release();
            }
        }

        /// <summary>
        /// Called by the game via reflection when the options page is (re)built -- at least
        /// once per scene, and again whenever any mod is enabled or disabled. The mod itself is
        /// configured here (language, key, button, diagnostics, reset); every setting of the
        /// glass is in the in-game panel (F8), where it can be judged against the city.
        /// </summary>
        public void OnSettingsUI(UIHelperBase helper)
        {
            // A copy that stayed off: one line (a group title: UIHelper has no plain label), and no
            // settings -- they are the running copy's.
            if (ModCopies.StoodDown)
            {
                helper.AddGroup(Localization.Get("Mod.SecondCopyShort"));
                return;
            }

            OptionsUI.Build(helper);
        }
    }
}
