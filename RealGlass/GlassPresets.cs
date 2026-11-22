using UnityEngine;

namespace RealGlass
{
    /// <summary>One kind of real architectural glass: what the Glass preset row writes.</summary>
    public struct GlassPreset
    {
        /// <summary>The value stored in RealGlass.xml (GlassPreset). Append only, never renumbered.</summary>
        public readonly int Value;

        /// <summary>For the log: the preset's English name. The UI's names are in the language files.</summary>
        public readonly string LogName;

        /// <summary>The tint of the light that comes through the glass (GlassColor), sRGB.</summary>
        public readonly Color32 Colour;

        /// <summary>Visible light reflected looking straight at it, 0..1 (GlassReflectivity).</summary>
        public readonly float Reflectivity;

        /// <summary>Visible light transmitted, 0..1 (GlassClarity).</summary>
        public readonly float Clarity;

        public GlassPreset(int value, string logName, Color32 colour, float reflectivity, float clarity)
        {
            Value = value;
            LogName = logName;
            Colour = colour;
            Reflectivity = reflectivity;
            Clarity = clarity;
        }
    }

    /// <summary>
    /// The glass presets: kinds of glass real towers are clad in, with the numbers their makers
    /// publish -- visible light transmittance (how much light gets through: Clarity) and exterior
    /// visible light reflectance (how much of the sky and the city it mirrors head-on:
    /// Reflectivity) -- and the colour the light takes going through.
    /// </summary>
    /// <remarks>
    /// Pure: no engine calls (Color32 is a plain struct), so tools/test-settings.ps1 checks the
    /// table against the catalog's rows on the built DLL.
    ///
    /// Where the numbers come from: Vitro Architectural Glass (formerly PPG), "Vistacool Subtly
    /// Reflective and Solarcool Reflective Glasses" product data sheet (02/2017), table of
    /// performance values, 1-inch insulating units (two 6 mm lites, 13 mm air space) with a clear
    /// inner lite, centre of glass, LBNL Window 7.3: visible light transmittance / exterior
    /// visible light reflectance. Rows marked "typical" are not in that sheet and are the usual
    /// values of their family. A preset only WRITES the three rows (colour, reflectivity,
    /// clarity); the player can move any of them afterwards, and the preset then reads Custom.
    /// The colour is the tint the glass gives (Solarcool on the second surface "deepens the hue
    /// of the tinted glass substrate", the sheet says), picked by eye.
    /// </remarks>
    public static class GlassPresets
    {
        /// <summary>The player's own numbers: the preset writes nothing.</summary>
        public const int Custom = 0;

        public const int OceanBlue = 3;

        /// <summary>
        /// A silver reflective curtain wall, the brightest mirror of the city a glass tower can
        /// be (the default from 2026-09-29 to 2026-10-04: "I love the silver look"; again from
        /// 2026-11-13 to 2026-11-21).
        /// </summary>
        public const int SilverReflective = 8;

        /// <summary>
        /// The default: a reflective blue curtain wall in the deep blue the author settled on in
        /// the first glass experiment (2026-09-24); the default until 2026-09-29, from 2026-10-04 to
        /// 2026-11-13, and again since 2026-11-21 (the author's profile "New Defaults").
        /// </summary>
        public const int ReflectiveBlue = 10;

        // In value order. APPEND ONLY: a saved preset must keep meaning the same glass.
        private static readonly GlassPreset[] Table =
        {
            // Typical: clear glass with a neutral solar-control low-E coating (Solarban 70 class):
            // high transmittance, a faint green-grey cast.
            new GlassPreset(1, "Clear low-E", new Color32(0xD9, 0xE4, 0xE6, 255), 0.11f, 0.64f),

            // Vistacool (2) Azuria + clear: VLT 47%, exterior reflectance 21%.
            new GlassPreset(2, "Aqua blue", new Color32(0x6F, 0xA3, 0xA8, 255), 0.21f, 0.47f),

            // Vistacool (2) Pacifica + clear: VLT 29%, exterior reflectance 11% -- rich blue.
            new GlassPreset(3, "Ocean blue", new Color32(0x2A, 0x5C, 0x78, 255), 0.11f, 0.29f),

            // Solarcool (2) Solarblue + clear: VLT 20%, exterior reflectance 15%.
            new GlassPreset(4, "Sky blue", new Color32(0x5E, 0x86, 0xA8, 255), 0.15f, 0.20f),

            // Solarcool (2) Solexia + clear: VLT 27%, exterior reflectance 24% -- green.
            new GlassPreset(5, "Green", new Color32(0x6E, 0x9C, 0x7C, 255), 0.24f, 0.27f),

            // Solarcool (2) Solarbronze + clear: VLT 19%, exterior reflectance 14%.
            new GlassPreset(6, "Bronze", new Color32(0x8A, 0x6E, 0x55, 255), 0.14f, 0.19f),

            // Solarcool (2) Solargray + clear: VLT 16%, exterior reflectance 11%.
            new GlassPreset(7, "Grey", new Color32(0x5F, 0x64, 0x68, 255), 0.11f, 0.16f),

            // Typical: a first-surface reflective coating ("a bright metallic appearance", the
            // sheet says; monolithic Solarcool Azuria reflects 36%): the city's mirror.
            new GlassPreset(8, "Silver reflective", new Color32(0xA9, 0xB4, 0xBA, 255), 0.35f, 0.20f),

            // Typical: a gold reflective coating, the gold mirror towers.
            new GlassPreset(9, "Gold reflective", new Color32(0xB8, 0x96, 0x3E, 255), 0.30f, 0.18f),

            // Solarcool (2) Azuria + clear: VLT 24%, exterior reflectance 20%.
            new GlassPreset(10, "Reflective blue", new Color32(0x2E, 0x6A, 0x80, 255), 0.20f, 0.24f),
        };

        /// <summary>How many real-glass presets there are (Custom not counted).</summary>
        public static int Count
        {
            get { return Table.Length; }
        }

        /// <summary>The preset at <paramref name="index"/>, 0-based, in value order.</summary>
        public static GlassPreset At(int index)
        {
            return Table[index];
        }

        /// <summary>The preset with this stored value; false for Custom and for an unknown value.</summary>
        public static bool TryGet(int value, out GlassPreset preset)
        {
            for (int i = 0; i < Table.Length; i++)
            {
                if (Table[i].Value == value)
                {
                    preset = Table[i];
                    return true;
                }
            }

            preset = default(GlassPreset);
            return false;
        }

        /// <summary>The preset's English name for the log.</summary>
        public static string LogName(int value)
        {
            GlassPreset preset;
            if (TryGet(value, out preset))
                return preset.LogName;

            return value == Custom ? "Custom" : "unknown (" + value + ")";
        }
    }
}
