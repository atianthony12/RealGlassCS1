using System;
using System.Collections.Generic;
using ColossalFramework.UI;

namespace RealGlass
{
    /// <summary>
    /// The one message a player sees when a part of the mod switches itself off after an error (his
    /// call, 2026-10-23: "at most, disable our mod with an error message"). Until then such an error
    /// reached RealGlass.log only, and the player saw the game's own glass or water with no word why.
    /// </summary>
    /// <remarks>
    /// A part reports from wherever it failed -- often inside the game's rendering, where no window
    /// may be opened -- and the message waits for the city controller's next Update
    /// (<see cref="ShowPending"/>). Once per part a launch: a part that fails again in the next city
    /// is logged, not shown again. The three parts are the ones that stop whole: the buildings' glass,
    /// the vehicles' glass, the water. What stops inside them (the LODs' borrowed glass, the room fit,
    /// Procedural Objects' buildings, the foam, the water's sky) leaves the part running and stays a
    /// log line. Main thread only, as every caller.
    /// </remarks>
    public static class ErrorNotice
    {
        // The parts, by their message's language key.
        public const string Glass = "Error.Glass";
        public const string Vehicles = "Error.Vehicles";
        public const string Water = "Error.Water";

        private static readonly List<string> Pending = new List<string>();
        private static readonly List<string> Shown = new List<string>();

        /// <summary>The part stopped after an error: its message, once a launch, at the next chance to show it.</summary>
        public static void Report(string part)
        {
            if (!Shown.Contains(part) && !Pending.Contains(part))
                Pending.Add(part);
        }

        /// <summary>From the city controller's Update, with no modal window up: the waiting message, if any.</summary>
        public static void ShowPending()
        {
            if (Pending.Count == 0)
                return;

            string part = Pending[0];
            Pending.RemoveAt(0);
            Shown.Add(part);
            try
            {
                string message = Localization.Get(part) + "\n\n" + Localization.Get("Error.Report", Log.Path);
                MessageWindow.Show(message);
                Log.Msg("error message shown: " + part);
            }
            catch (Exception e)
            {
                Log.Error("Could not show the error message (" + part + ").", e);
            }
        }
    }
}
