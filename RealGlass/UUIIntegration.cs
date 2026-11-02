using System;
using System.Reflection;
using ColossalFramework.UI;
using UnifiedUI.Helpers;
using UnityEngine;

namespace RealGlass
{
    /// <summary>
    /// Registers our button with Unified UI.
    /// </summary>
    /// <remarks>
    /// UnifiedUILib.dll ships alongside this mod, so the types always resolve. Registering is what
    /// puts a Unified UI panel on screen: RegisterCustomButton goes to MainPanel.Instance of the
    /// newest UnifiedUILib loaded by any mod, and that getter CREATES the library's own floating
    /// panel when the Unified UI mod is not enabled and no mod has made one yet (IL, Volumetric
    /// Weather 1.2.1). So we register only when the panel is there anyway: the Unified UI mod is
    /// enabled, or another mod has already made the library's panel (then we join it, rather than
    /// being the one button outside it). Otherwise the HUD button.
    /// </remarks>
    public static class UUIIntegration
    {
        private static UUICustomButton _button;

        public static bool IsRegistered => _button != null;

        public static bool Register(Texture2D icon, Action<bool> onToggle)
        {
            if (_button != null)
                return true;

            try
            {
                bool modEnabled = UUIHelpers.IsUUIEnabled();
                bool panelShown = !modEnabled && LibraryPanelExists();

                if (!modEnabled && !panelShown)
                {
                    Log.Msg("Unified UI: not registered -- the Unified UI mod is not enabled and no other mod shows its panel, so ours would create one");
                    return false;
                }

                _button = UUIHelpers.RegisterCustomButton(
                    name: "RealGlass",
                    groupName: null,
                    tooltip: Mod.DisplayName,
                    icon: icon,
                    onToggle: onToggle,
                    onToolChanged: null,
                    hotkeys: new UUIHotKeys { ActivationKey = Settings.PanelKey });

                GiveTooltipBox();

                Log.Msg("Unified UI: registered=" + (_button != null) +
                        " (" + (modEnabled ? "Unified UI mod enabled" : "joined another mod's Unified UI panel") +
                        ", icon=" + (icon == null ? "MISSING" : icon.width + "x" + icon.height) + ")");

                return _button != null;
            }
            catch (Exception e)
            {
                Log.Error("Unified UI registration failed; falling back to the HUD button.", e);
                _button = null;
                return false;
            }
        }

        /// <summary>
        /// Gives our button in Unified UI's bar the view's tooltip box. UIComponent.tooltipBox falls
        /// back to the view's default box only when a component's parent is itself a UIComponent
        /// (IL of get_tooltipBox, 2026-11-02), and UnifiedUILib's panel hangs off the view's root
        /// and never sets one (2.2.12): every button in it was left with none, so its tooltip
        /// never showed. Ours only; a box another copy of the library set is kept.
        /// </summary>
        private static void GiveTooltipBox()
        {
            try
            {
                UIComponent button = _button != null ? _button.Button : null;
                if (button != null && button.tooltipBox == null)
                    button.tooltipBox = UIView.GetAView().defaultTooltipBox;
            }
            catch (Exception e)
            {
                Log.Error("Giving the Unified UI button its tooltip box threw.", e);
            }
        }

        /// <summary>
        /// Whether another mod has already put UnifiedUILib's own panel on screen. Every mod ships
        /// its own copy of the library and each copy is a different MainPanel type, so every loaded
        /// copy is asked. A hidden panel counts: hiding a UIComponent leaves its GameObject active,
        /// so FindObjectOfType sees it.
        /// </summary>
        private static bool LibraryPanelExists()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != "UnifiedUILib")
                    continue;

                Type panel = assembly.GetType("UnifiedUI.GUI.MainPanel", false);
                if (panel != null && UnityEngine.Object.FindObjectOfType(panel) != null)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Tells Unified UI whether our panel is open.
        /// </summary>
        /// <remarks>
        /// Read from UnifiedUILib's IL: the hotkey and a click both end in ButtonBase.Toggle, which
        /// is "if (IsActive) Deactivate() else Activate()" on the BUTTON's own flag -- it never asks
        /// us. So when the panel is closed any other way (its own close button), the button is left
        /// active, the next press "deactivates" a panel that is already hidden, and only the press
        /// after that opens it. Setting IsPressed flips the flag and the sprites without invoking
        /// the toggle callback, so this cannot loop.
        /// </remarks>
        public static void SetPressed(bool pressed)
        {
            if (_button == null)
                return;

            try
            {
                if (_button.IsPressed == pressed)
                    return;

                _button.IsPressed = pressed;
                Log.Msg("Unified UI: button state corrected to " + (pressed ? "active" : "inactive") +
                        " to match the panel");
            }
            catch (Exception e)
            {
                Log.Error("Syncing the Unified UI button state threw.", e);
            }
        }

        public static void Unregister()
        {
            if (_button == null)
                return;

            try
            {
                _button.Release();
            }
            catch (Exception e)
            {
                Log.Error("Releasing the Unified UI button threw.", e);
            }

            _button = null;
        }
    }
}
