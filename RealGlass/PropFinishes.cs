using System;
using UnityEngine;

namespace RealGlass
{
    /// <summary>One finish of a Reflective prop (1.1.9): what the prop window's "Kind of finish" writes.</summary>
    public struct PropFinish
    {
        /// <summary>The value stored in RealGlassAssets.xml (PropFinish). Append only, never renumbered.</summary>
        public readonly int Value;

        /// <summary>For the log: the finish's English name. The UI's names are in the language files.</summary>
        public readonly string LogName;

        /// <summary>The reflection's color, sRGB: the reflectance head-on per channel over its brightest.</summary>
        public readonly Color32 Colour;

        /// <summary>The brightest channel's reflectance head-on, 0..1 (the Reflectivity row).</summary>
        public readonly float Reflectivity;

        public PropFinish(int value, string logName, double r, double g, double b)
        {
            Value = value;
            LogName = logName;
            double top = Math.Max(r, Math.Max(g, Math.Max(b, 1e-6)));
            Reflectivity = (float)top;
            Colour = new Color32(Srgb(r / top), Srgb(g / top), Srgb(b / top), 255);
        }

        /// <summary>Linear 0..1 to an sRGB byte (IEC 61966-2-1).</summary>
        private static byte Srgb(double linear)
        {
            double c = Math.Max(0.0, Math.Min(1.0, linear));
            double s = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
            return (byte)Math.Round(s * 255.0);
        }
    }

    /// <summary>
    /// The finishes of a Reflective prop: a finish ON the prop, nothing seen through it -- what the
    /// glass is not (a street light's pole, a bench's steel). A clear gloss coat over its paint, or a
    /// bare metal.
    /// </summary>
    /// <remarks>
    /// Pure: no engine calls (Color32 is a plain struct), so tools/test-settings.ps1 can check it.
    ///
    /// Where the numbers come from: the metals' reflectance head-on (F0, linear RGB) as measured and
    /// tabulated by Hoffman, "Physics and Math of Shading" (SIGGRAPH 2013 course; Real-Time Rendering,
    /// 4th ed., table 9.2): chromium for chrome plating, iron for stainless steel (an iron alloy whose
    /// chromium film keeps the iron's shine). The gloss coat: a lacquer or clear coat of refractive
    /// index ~1.5, 4% head-on, untinted (Fresnel; the vehicles' clear coat). A finish only WRITES the
    /// color and the reflectivity; moving either afterwards reads Custom.
    /// </remarks>
    public static class PropFinishes
    {
        /// <summary>The player's own numbers: the finish writes nothing.</summary>
        public const int Custom = 0;

        public const int GlossCoat = 1;
        public const int Aluminium = 3;

        // In value order. APPEND ONLY: a saved finish must keep meaning the same finish.
        private static readonly PropFinish[] Table =
        {
            new PropFinish(1, "Gloss coat", 0.04, 0.04, 0.04),
            new PropFinish(2, "Chrome", 0.549, 0.556, 0.554),
            new PropFinish(3, "Aluminium", 0.913, 0.922, 0.924),
            new PropFinish(4, "Stainless steel", 0.562, 0.565, 0.578),
            new PropFinish(5, "Gold", 1.000, 0.782, 0.344),
            new PropFinish(6, "Copper", 0.955, 0.638, 0.538),
        };

        /// <summary>How many finishes there are (Custom not counted).</summary>
        public static int Count
        {
            get { return Table.Length; }
        }

        public static PropFinish At(int index)
        {
            return Table[index];
        }

        /// <summary>The finish with this stored value; false for Custom and for an unknown value.</summary>
        public static bool TryGet(int value, out PropFinish finish)
        {
            for (int i = 0; i < Table.Length; i++)
            {
                if (Table[i].Value == value)
                {
                    finish = Table[i];
                    return true;
                }
            }

            finish = default(PropFinish);
            return false;
        }

        public static string LogName(int value)
        {
            PropFinish finish;
            if (TryGet(value, out finish))
                return finish.LogName;
            return value == Custom ? "Custom" : "unknown (" + value + ")";
        }
    }
}
