using System;
using System.Collections.Generic;
using System.IO;

namespace RealGlass
{
    /// <summary>
    /// The rules for a profile's name. The name IS its file's name (&lt;name&gt;.xml) and nothing
    /// else: renaming the file renames the profile, and no name inside a file can disagree with it.
    /// Pure -- no engine call -- so tools/test-profiles.ps1 checks it on the built DLL.
    /// (Volumetric Weather's, as it is.)
    /// </summary>
    /// <remarks>
    /// Windows' rules on every system, so a profile made on a Mac can be sent to a Windows
    /// player: none of &lt; &gt; : " / \ | ? *, no control characters, no dot at either end, and
    /// not a name Windows keeps for a device (CON, NUL, COM1... with any extension, too).
    /// Names are compared without regard to case: Windows and the Mac's file system would call
    /// "Storm" and "storm" the same file, and Linux follows the rule rather than its file system.
    /// Any other character is fine: a name is what the player typed.
    ///
    /// These rules are for MAKING a profile. A file that is already in the folder is listed
    /// under its file name whatever it is (<see cref="IsFileName"/> only keeps a name from
    /// reaching outside the folder).
    /// </remarks>
    public static class ProfileName
    {
        public const int MaxLength = 40;

        private const string Extension = ".xml";
        private const string Forbidden = "<>:\"/\\|?*";

        private static readonly string[] Devices =
        {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
            "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
        };

        /// <summary>
        /// The name as it will be used: trimmed, and without a ".xml" typed at the end (the file
        /// gets one anyway). Null when it cannot be one; <see cref="Why"/> says why.
        /// </summary>
        public static string Clean(string typed)
        {
            return Why(typed) == null ? Tidy(typed) : null;
        }

        /// <summary>
        /// Null when the name can be used; otherwise the language key of the reason, one of
        /// "Profiles.Invalid.Empty", ".Long", ".Character", ".Dot", ".Reserved", ".Parentheses".
        /// Whether the name is taken is the caller's to ask (<see cref="Contains"/>): it needs the
        /// folder.
        /// </summary>
        public static string Why(string typed)
        {
            string name = Tidy(typed);

            if (name.Length == 0)
                return "Profiles.Invalid.Empty";

            if (name.Length > MaxLength)
                return "Profiles.Invalid.Long";

            foreach (char c in name)
            {
                if (char.IsControl(c) || Forbidden.IndexOf(c) >= 0)
                    return "Profiles.Invalid.Character";
            }

            if (name[0] == '.' || name[name.Length - 1] == '.')
                return "Profiles.Invalid.Dot";

            if (IsDevice(name))
                return "Profiles.Invalid.Reserved";

            if (IsInParentheses(name))
                return "Profiles.Invalid.Parentheses";

            return null;
        }

        /// <summary>
        /// A whole name in parentheses, "(unsaved profile)" or "(mod defaults)", is the list's own entries'
        /// (Volumetric Weather 1.5.0), in every language -- the Chinese ones are in full-width parentheses. A file
        /// already named so is still listed (these rules are for making one), as its own entry.
        /// </summary>
        private static bool IsInParentheses(string name)
        {
            char first = name[0];
            char last = name[name.Length - 1];
            return (first == '(' || first == '（') && (last == ')' || last == '）');
        }

        /// <summary>Two names are one profile when they differ only by case.</summary>
        public static bool Same(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        public static bool Contains(IList<string> names, string name)
        {
            if (names == null)
                return false;

            foreach (string existing in names)
            {
                if (Same(existing, name))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The first of "Profile 1", "Profile 2"... that is not taken: what a new profile's field
        /// starts with. <paramref name="pattern"/> is the language's, with {0} for the number.
        /// </summary>
        public static string NextFree(IList<string> existing, string pattern)
        {
            for (int n = 1; n < 100000; n++)
            {
                string name = string.Format(pattern, n);
                if (!Contains(existing, name))
                    return name;
            }

            return string.Format(pattern, 100000);
        }

        /// <summary>
        /// A name that can only ever mean a file IN the profiles folder: not empty, not "." or
        /// "..", and nothing that separates or starts a path. What a name from anywhere but the
        /// folder's own listing -- a hand edit of &lt;Profile&gt; -- has to pass before it is used
        /// to make a path, or "..\RealGlass" would be the settings file itself.
        /// </summary>
        public static bool IsFileName(string name)
        {
            if (string.IsNullOrEmpty(name) || name == "." || name == "..")
                return false;

            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return false;

            // Not every system's list holds these three; every system's paths can.
            return name.IndexOf('/') < 0 && name.IndexOf('\\') < 0 && name.IndexOf(':') < 0;
        }

        /// <summary>
        /// A file in the folder that can be listed and picked: a name <see cref="IsFileName"/>
        /// takes, with no space at either end -- the pick is kept trimmed, so " Storm" would come
        /// back as "Storm" and miss its file -- and no control character: the pick is written
        /// into RealGlass.xml, where most of them make the whole file unreadable (Linux
        /// and the Mac allow them in file names). Anything else goes: this is for READING.
        /// </summary>
        public static bool IsListable(string name)
        {
            if (!IsFileName(name) || name != name.Trim())
                return false;

            foreach (char c in name)
            {
                if (char.IsControl(c))
                    return false;
            }

            return true;
        }

        private static string Tidy(string typed)
        {
            string name = typed == null ? string.Empty : typed.Trim();

            if (name.Length > Extension.Length && name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - Extension.Length).TrimEnd();

            return name;
        }

        /// <summary>Windows keeps the device names with ANY extension: "con.storm" is CON too.</summary>
        private static bool IsDevice(string name)
        {
            int dot = name.IndexOf('.');
            string stem = (dot >= 0 ? name.Substring(0, dot) : name).TrimEnd(' ');

            foreach (string device in Devices)
            {
                if (string.Equals(stem, device, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
