using System;
using System.Reflection;
using ColossalFramework.DataBinding;
using ColossalFramework.UI;

namespace RealGlass
{
    /// <summary>
    /// The game's message window (ExceptionPanel) with the mod's name and a text, as a warning.
    /// </summary>
    /// <remarks>
    /// ExceptionPanel.SetMessage packs title, message and icon into ONE string for
    /// BindPropertyByKey.SetProperties, which splits it again on '&amp;' between entries and '|' between key
    /// and value (BindPropertyByKey.entrySeparator / keyValueSeparator, read 2026-11-04). The mod's own
    /// name has an '&amp;': every window it showed read "Real Glass " and nothing else. So the three
    /// bindings are set one by one, as SetProperties ends up doing (FindBinding(key).property.value);
    /// if the panel is not laid out so, SetMessage with the separators taken out of the text.
    /// </remarks>
    public static class MessageWindow
    {
        private static readonly FieldInfo BindingField =
            typeof(ExceptionPanel).GetField("m_Binding", BindingFlags.Instance | BindingFlags.NonPublic);

        public static void Show(string message)
        {
            ExceptionPanel panel = UIView.library.ShowModal<ExceptionPanel>("ExceptionPanel");
            if (!SetBound(panel, Mod.DisplayName, message))
                panel.SetMessage(Plain(Mod.DisplayName), Plain(message), false);
        }

        private static bool SetBound(ExceptionPanel panel, string title, string message)
        {
            try
            {
                var binding = BindingField != null ? BindingField.GetValue(panel) as BindPropertyByKey : null;
                if (binding == null)
                    return false;

                BindPropertyByKey.BindingInfo titleBinding = binding.FindBinding("title");
                BindPropertyByKey.BindingInfo messageBinding = binding.FindBinding("message");
                if (titleBinding == null || messageBinding == null || titleBinding.property == null || messageBinding.property == null)
                    return false;

                titleBinding.property.value = title;
                messageBinding.property.value = message;
                BindPropertyByKey.BindingInfo icon = binding.FindBinding("img");
                if (icon != null && icon.property != null)
                    icon.property.value = "IconWarning";
                return true;
            }
            catch (Exception e)
            {
                Log.Warn("message window: its fields could not be set one by one (" + e.Message + "); the plain text instead");
                return false;
            }
        }

        /// <summary>The text without the window's two separators.</summary>
        private static string Plain(string text)
        {
            return (text ?? "").Replace("&", "+").Replace("|", "/");
        }
    }
}
