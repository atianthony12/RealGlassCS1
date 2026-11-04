using System;
using System.Reflection;
using ColossalFramework;
using ColossalFramework.Plugins;
using ColossalFramework.UI;
using UnityEngine;

namespace RealGlass
{
    /// <summary>
    /// One copy of the mod at a time. A player can have two -- the Workshop item and a copy in
    /// Addons\Mods (the GitHub zip, or a build of their own) -- and the game enables both: two sets of
    /// draw hooks under one Harmony ID, two savers on one RealGlass.xml, one log started twice, the
    /// glass drawn twice and the water's hook failed (2026-10-23, the Workshop copy beside a dev
    /// build: the one conflict that has really happened). The first copy enabled holds the claim, an
    /// AppDomain value every copy can read (the copies share no type, only the domain); a copy
    /// enabled while another holds it stays off: no patches, no settings, no controller -- one log
    /// line, one line on its options page, and a window at its first city.
    /// </summary>
    /// <remarks>
    /// The game enables mods in the order it found them -- its own, then Addons\Mods, then the
    /// Workshop's (PluginManager.LoadPlugins -> AddPlugins, IL 2026-10-23) -- so a local copy wins over
    /// a subscribed one: a dev build runs, the Workshop copy beside it is the one off. A rebuilt DLL
    /// replaces its copy by unloading the old (OnDisabled, which gives the claim back) before the new
    /// one is enabled (AddPlugins), so the hot-reload loop is untouched. A copy from before 1.1.4
    /// claims nothing and so cannot be stopped: a 1.1.3 beside a newer copy still runs.
    /// </remarks>
    public static class ModCopies
    {
        /// <summary>The AppDomain value: the running copy's Assembly. Permanent (every later copy reads it).</summary>
        private const string Key = "RealGlass.RunningCopy";

        private static bool _shown;

        /// <summary>This copy stayed off when it was enabled: another copy of the mod was running.</summary>
        public static bool StoodDown { get; private set; }

        /// <summary>The running copy's folder, while this one is off ("?" when the game cannot say).</summary>
        public static string Running { get; private set; }

        /// <summary>This copy's folder ("?" when the game cannot say).</summary>
        public static string Here
        {
            get { return FolderOf(typeof(ModCopies).Assembly); }
        }

        /// <summary>From OnEnabled, first: takes the claim, or keeps this copy off. True to run.</summary>
        public static bool Claim()
        {
            try
            {
                Assembly ours = typeof(ModCopies).Assembly;
                Assembly holder = AppDomain.CurrentDomain.GetData(Key) as Assembly;
                if (holder != null && holder != ours && Singleton<PluginManager>.instance.FindPluginInfo(holder) != null)
                {
                    StoodDown = true;
                    Running = FolderOf(holder);
                    return false;
                }

                // Free, ours already, or held by a copy no longer enabled (it was unloaded without
                // its OnDisabled running to the end): ours now.
                AppDomain.CurrentDomain.SetData(Key, ours);
                StoodDown = false;
                return true;
            }
            catch (Exception e)
            {
                // Cannot tell: run, as every copy did before this check.
                Debug.LogException(e);
                StoodDown = false;
                return true;
            }
        }

        /// <summary>From OnDisabled, last: the claim given back, if this copy holds it.</summary>
        public static void Release()
        {
            try
            {
                if (AppDomain.CurrentDomain.GetData(Key) as Assembly == typeof(ModCopies).Assembly)
                    AppDomain.CurrentDomain.SetData(Key, null);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        /// <summary>The window, at this copy's first city of the launch (the UI is up by then).</summary>
        public static void ShowOnce()
        {
            if (_shown)
                return;

            _shown = true;
            try
            {
                MessageWindow.Show(Localization.Get("Mod.SecondCopy", Running, Here));
            }
            catch (Exception e)
            {
                Log.Error("Could not show the 'installed twice' message.", e);
            }
        }

        private static string FolderOf(Assembly assembly)
        {
            try
            {
                PluginManager.PluginInfo plugin = Singleton<PluginManager>.instance.FindPluginInfo(assembly);
                return plugin != null && !string.IsNullOrEmpty(plugin.modPath) ? plugin.modPath : "?";
            }
            catch (Exception)
            {
                return "?";
            }
        }
    }
}
