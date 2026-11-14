namespace RealGlass
{
    /// <summary>
    /// Text cut or shown without ever leaving half a character. A character outside the Basic
    /// Multilingual Plane (an emoji, many CJK ideographs) is TWO UTF-16 units, a surrogate pair; a
    /// string holding only one of them makes the game's font throw ("invalid utf-16 sequence") when
    /// a label asks for its glyphs, and in this game one such label stops every new text from
    /// drawing until a restart (probed in the Unity 5.6 editor). Every cut of text from outside the
    /// mod -- a profile's file name, what the player typed or pasted -- goes through here.
    /// Pure (System only), so tools/test-profiles.ps1 checks it on the built DLL. C# 5 on purpose.
    /// </summary>
    internal static class TextCut
    {
        /// <summary>
        /// The first <paramref name="count"/> UTF-16 units of the text (all of it when it is
        /// shorter; "" for null or a count of 0 or less), one fewer when the cut would fall
        /// between the two halves of a pair.
        /// </summary>
        public static string First(string text, int count)
        {
            if (text == null || count <= 0)
                return string.Empty;

            if (text.Length <= count)
                return text;

            int n = count;
            if (char.IsHighSurrogate(text[n - 1]))
                n--;

            return text.Substring(0, n);
        }

        /// <summary>
        /// True when the text holds half a pair: a high surrogate not followed by a low one, or a
        /// low one not preceded by a high one. False for null or empty.
        /// </summary>
        public static bool HasLoneSurrogate(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                        i++; // a whole pair
                    else
                        return true;
                }
                else if (char.IsLowSurrogate(c))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The text with every half pair replaced by '?' (the same instance when there is none;
        /// "" for null): safe to give a label.
        /// </summary>
        public static string Whole(string text)
        {
            if (text == null)
                return string.Empty;

            if (!HasLoneSurrogate(text))
                return text;

            char[] chars = text.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1]))
                        i++;
                    else
                        chars[i] = '?';
                }
                else if (char.IsLowSurrogate(c))
                {
                    chars[i] = '?';
                }
            }

            return new string(chars);
        }
    }
}
