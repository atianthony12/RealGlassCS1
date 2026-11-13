using System;
using ColossalFramework;
using ICities;
using UnityEngine;

namespace RealGlass.Glass
{
    /// <summary>
    /// Dynamic dirt (the Glass tab's switch, on by default): how far the dirt on the glass has built up
    /// since the last rain, 0 just washed .. 1 all of the Dirt setting. Rain washes it off -- heavy rain
    /// in about a game hour, light rain over a few -- and it builds up again over a game month. The rain
    /// is the game's own (WeatherManager.m_currentRain: random weather, Play It's slider, the storms;
    /// Volumetric Weather reads the same value). Counted in game hours, frame by frame, so it stops while
    /// the game is paused; kept with the city (<see cref="DirtSaveData"/>), and a city that has none yet
    /// starts from the game's own ground wetness (saved by the game: wet ground, freshly washed glass).
    /// </summary>
    public static class DirtWeather
    {
        /// <summary>Game hours for dirt to build up from clean to all of it: a month (the author's call, 2026-11-13).</summary>
        public const double BuildUpHours = 30.0 * 24.0;

        /// <summary>Game hours heavy rain (1) takes to wash it all off; lighter rain takes longer in proportion.</summary>
        public const double WashHours = 1.0;

        /// <summary>Rain below this does not wash (the game's rain is 0, or 0.25 .. 1).</summary>
        public const float RainThreshold = 0.05f;

        /// <summary>The most game time one frame may count (a time jump is not weeks of weather).</summary>
        public const double MostHoursAFrame = 6.0;

        private static double _frame = -1.0;
        private static bool _washing;

        /// <summary>0 just washed .. 1 built up all the way.</summary>
        public static float Level { get; private set; }

        /// <summary>A level is known for this city (loaded with it, or started from its ground wetness).</summary>
        public static bool Known { get; private set; }

        /// <summary>The next level after <paramref name="hours"/> game hours of <paramref name="rain"/> (0..1). Pure.</summary>
        public static float Step(float level, double hours, float rain)
        {
            if (!(hours > 0.0))
                return Mathf.Clamp01(level);

            hours = Math.Min(hours, MostHoursAFrame);
            double next = rain >= RainThreshold ? level - rain * hours / WashHours : level + hours / BuildUpHours;
            return Mathf.Clamp01((float)next);
        }

        /// <summary>The level loaded with a city (null: none saved, start from the ground's wetness).</summary>
        public static void Load(float? level)
        {
            _frame = -1.0;
            _washing = false;
            if (level.HasValue)
            {
                Level = Mathf.Clamp01(level.Value);
                Known = true;
            }
            else
            {
                Level = 1f;
                Known = false;
            }
        }

        /// <summary>Once a frame while the glass draws: the game time since the last frame, and its rain.</summary>
        public static void Update()
        {
            if (!Singleton<SimulationManager>.exists || !Singleton<WeatherManager>.exists)
                return;

            WeatherManager weather = Singleton<WeatherManager>.instance;
            if (!Known)
            {
                // No level saved with this city yet: wet ground is freshly washed glass.
                Level = 1f - Mathf.Clamp01(weather.m_groundWetness);
                Known = true;
                Log.Msg("dirt: built up " + Pct(Level) + " (from the ground's wetness; this city had none saved)");
            }

            SimulationManager sm = Singleton<SimulationManager>.instance;
            double frame = (double)sm.m_referenceFrameIndex + sm.m_referenceTimer;
            double day = Math.Max(SimulationManager.DAYTIME_FRAMES, 1u);
            double hours = _frame < 0.0 || frame < _frame ? 0.0 : (frame - _frame) * 24.0 / day;
            _frame = frame;

            float rain = Mathf.Clamp01(weather.m_currentRain);
            Level = Step(Level, hours, rain);

            bool washing = rain >= RainThreshold;
            if (washing != _washing)
            {
                _washing = washing;
                Log.Msg(washing ? "dirt: rain (" + Pct(rain) + ") is washing the glass, built up " + Pct(Level)
                                : "dirt: the rain stopped, built up " + Pct(Level) + "; building up again over a game month");
            }
        }

        private static string Pct(float v)
        {
            return Mathf.RoundToInt(v * 100f) + "%";
        }
    }

    /// <summary>
    /// Keeps how far the dirt has built up with the city: the game's own save data for mods, one key,
    /// five bytes (a version, the level). A city never drawn with the mod carries no key.
    /// </summary>
    public sealed class DirtSaveData : SerializableDataExtensionBase
    {
        /// <summary>The key in the city's save. PERMANENT.</summary>
        public const string Key = "RealGlass.Dirt";

        private const byte Version = 1;

        public override void OnLoadData()
        {
            base.OnLoadData();
            try
            {
                byte[] data = serializableDataManager.LoadData(Key);
                if (data != null && data.Length >= 5 && data[0] == Version)
                {
                    float level = BitConverter.ToSingle(data, 1);
                    DirtWeather.Load(level >= 0f && level <= 1f ? level : (float?)null);
                    Log.Msg("dirt: built up " + Mathf.RoundToInt(DirtWeather.Level * 100f) + "% (saved with the city)");
                }
                else
                {
                    DirtWeather.Load(null);
                }
            }
            catch (Exception e)
            {
                DirtWeather.Load(null);
                Log.Error("dirt: loading how far it had built up threw; it starts from the ground's wetness", e);
            }
        }

        public override void OnSaveData()
        {
            base.OnSaveData();
            try
            {
                if (!DirtWeather.Known)
                    return;

                var data = new byte[5];
                data[0] = Version;
                Buffer.BlockCopy(BitConverter.GetBytes(DirtWeather.Level), 0, data, 1, 4);
                serializableDataManager.SaveData(Key, data);
            }
            catch (Exception e)
            {
                Log.Error("dirt: saving how far it had built up threw; the save goes on without it", e);
            }
        }
    }
}
