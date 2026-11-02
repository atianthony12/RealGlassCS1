using ColossalFramework.UI;
using UnityEngine;

namespace RealGlass.UI
{
    /// <summary>
    /// The mod's own button ("Toolbar" on the options page), used when the player picks it there or
    /// Unified UI isn't available. Drag it to move it; where it was left is saved. (Volumetric Weather's
    /// button, with this mod's icon.)
    /// </summary>
    /// <remarks>
    /// The button draws the game's background sprites itself and the icon is a CHILD sprite, so
    /// it is on top (children are drawn over their parent); the child is not interactive, so every
    /// click still reaches the button.
    /// </remarks>
    public class ModButton : UIButton
    {
        /// <summary>
        /// Play It!'s button size (36 x 36, read from its IL: ModManager.CreateUI), so the sister
        /// mods' buttons sit side by side as one set.
        /// </summary>
        public const float ButtonSize = 36f;

        /// <summary>How far (UI units) a press must travel before it is a drag, not a click.</summary>
        private const float DragThreshold = 4f;

        private const string IconSpriteName = "RealGlassIcon";

        /// <summary>
        /// Background sprites, normal / hovered / pressed / disabled -- blue only on hover, as in
        /// Play It!. The first family whose normal sprite is in the live atlas wins. A state a family
        /// lacks falls back to its normal sprite.
        /// </summary>
        private static readonly string[][] Backgrounds =
        {
            new[] { "OptionBase", "OptionBaseHovered", "OptionBasePressed", "OptionBaseDisabled" },
            new[] { "RoundBackBig", "RoundBackBigHovered", "RoundBackBigPressed", "RoundBackBigDisabled" },
            new[] { "ButtonMenu", "ButtonMenuHovered", "ButtonMenuPressed", "ButtonMenuDisabled" },
        };

        private static UITextureAtlas _iconAtlas;

        public System.Action OnClicked;

        private bool _pressed;
        private bool _dragged;
        private Vector2 _downAt;
        private Vector2 _grab;

        public override void Start()
        {
            base.Start();

            name = "RealGlassButton";
            size = new Vector2(ButtonSize, ButtonSize);
            tooltip = Mod.DisplayName;

            // UIComponent.tooltipBox falls back to the view's default box only when the parent is
            // itself a UIComponent (IL of get_tooltipBox, 2026-11-02); this button hangs off the
            // view's root, so without this its tooltip never showed.
            if (tooltipBox == null)
                tooltipBox = UIView.GetAView().defaultTooltipBox;

            // A focused UIButton keeps its focused look after the click, until something else
            // takes the focus: the button would stay lit. Blue means the mouse is on it, only.
            canFocus = false;

            // The game's round sprites in every panel look, as Play It!'s and Render It's buttons are
            // (the looks' own "hud" sprite was a rounded square: the author's call, 2026-10-31).
            atlas = UIView.GetAView().defaultAtlas;
            string[] sprites = PickBackground(atlas);
            if (sprites != null)
            {
                normalBgSprite = sprites[0];
                hoveredBgSprite = sprites[1];
                pressedBgSprite = sprites[2];
                disabledBgSprite = sprites[3];
                focusedBgSprite = sprites[0];
            }

            Texture2D icon = IconLoader.Load();
            UITextureAtlas iconAtlas = icon != null ? IconAtlas(icon) : null;
            if (iconAtlas != null)
            {
                UISprite sprite = AddUIComponent<UISprite>();
                sprite.atlas = iconAtlas;
                sprite.spriteName = IconSpriteName;
                sprite.size = size;
                sprite.relativePosition = Vector3.zero;
                sprite.isInteractive = false;
            }
            else
            {
                // Without an icon the button would be an empty square, so fall back to text.
                text = "RG";
                textScale = 0.8f;
            }

            Place();

            eventClick += (component, e) =>
            {
                if (OnClicked != null)
                    OnClicked();
            };

            Log.Msg("panel button: HUD button at (" + relativePosition.x.ToString("F0") + ", " +
                    relativePosition.y.ToString("F0") + "), " + ButtonSize.ToString("F0") + " px, background '" +
                    (sprites != null ? sprites[0] : "none") + "', icon " +
                    (iconAtlas != null ? icon.width + "x" + icon.height : "MISSING (text instead)"));
        }

        /// <summary>Moves the button to the saved spot, kept on screen.</summary>
        public void Place()
        {
            float x = Settings.HudButtonX != null ? Settings.HudButtonX.value : Settings.Defaults.HudButtonX;
            float y = Settings.HudButtonY != null ? Settings.HudButtonY.value : Settings.Defaults.HudButtonY;
            relativePosition = OnScreen(new Vector2(x, y));
        }

        protected override void OnMouseDown(UIMouseEventParameter p)
        {
            if ((p.buttons & UIMouseButton.Left) != 0)
            {
                _pressed = true;
                _dragged = false;
                _downAt = MouseInGui();
                _grab = _downAt - new Vector2(relativePosition.x, relativePosition.y);
            }

            base.OnMouseDown(p);
        }

        /// <summary>A press that moved is a drag: it toggles nothing.</summary>
        protected override void OnClick(UIMouseEventParameter p)
        {
            if (_dragged)
            {
                p.Use();
                return;
            }

            base.OnClick(p);
        }

        /// <summary>
        /// The drag follows the mouse from here rather than from mouse-move events, which stop the
        /// moment the pointer outruns the button.
        /// </summary>
        public override void Update()
        {
            base.Update();

            if (!_pressed)
                return;

            if (!Input.GetMouseButton(0))
            {
                _pressed = false;
                if (_dragged)
                    SavePosition();
                return;
            }

            Vector2 mouse = MouseInGui();
            if (!_dragged && (mouse - _downAt).magnitude < DragThreshold)
                return;

            _dragged = true;
            relativePosition = OnScreen(mouse - _grab);
        }

        private void SavePosition()
        {
            if (Settings.HudButtonX == null || Settings.HudButtonY == null)
                return;

            Settings.HudButtonX.value = Mathf.Round(relativePosition.x);
            Settings.HudButtonY.value = Mathf.Round(relativePosition.y);
            Log.Msg("panel button: moved to (" + Settings.HudButtonX.value.ToString("F0") + ", " +
                    Settings.HudButtonY.value.ToString("F0") + ")");
        }

        /// <summary>
        /// The mouse in UI units, from the top left. UIView.ScreenPointToGUI only flips y against a
        /// resolution that is already in UI units (read from its IL), so the pixels are scaled here
        /// first. The ratio is read every time, never assumed: it depends on the screen, the game's
        /// UI scale and mods such as UI Resolution.
        /// </summary>
        private Vector2 MouseInGui()
        {
            UIView view = GetUIView();
            Vector2 resolution = view.GetScreenResolution();
            float unitsPerPixel = resolution.x / view.uiCamera.pixelWidth;

            Vector3 mouse = Input.mousePosition;
            return new Vector2(mouse.x * unitsPerPixel, resolution.y - mouse.y * unitsPerPixel);
        }

        private Vector3 OnScreen(Vector2 position)
        {
            Vector2 resolution = GetUIView().GetScreenResolution();
            float x = Mathf.Clamp(position.x, 0f, Mathf.Max(0f, resolution.x - width));
            float y = Mathf.Clamp(position.y, 0f, Mathf.Max(0f, resolution.y - height));
            return new Vector3(x, y);
        }

        /// <summary>The first family whose normal sprite exists; a missing state falls back to it.</summary>
        private static string[] PickBackground(UITextureAtlas atlas)
        {
            if (atlas == null)
                return null;

            foreach (string[] family in Backgrounds)
            {
                if (atlas[family[0]] == null)
                    continue;

                string[] picked = new string[family.Length];
                for (int i = 0; i < family.Length; i++)
                    picked[i] = atlas[family[i]] != null ? family[i] : family[0];

                return picked;
            }

            return null;
        }

        /// <summary>
        /// The icon in a one-sprite atlas of its own, made once: a UISprite can only show a sprite
        /// from an atlas. Unified UI's lib builds its single-texture atlases the same way.
        /// </summary>
        private static UITextureAtlas IconAtlas(Texture2D texture)
        {
            if (_iconAtlas != null)
                return _iconAtlas;

            UITextureAtlas defaultAtlas = UIView.GetAView().defaultAtlas;
            if (defaultAtlas == null || defaultAtlas.material == null)
                return null;

            UITextureAtlas atlas = ScriptableObject.CreateInstance<UITextureAtlas>();
            atlas.name = "RealGlassAtlas";

            Material material = Object.Instantiate(defaultAtlas.material);
            material.mainTexture = texture;
            atlas.material = material;

            atlas.AddSprite(new UITextureAtlas.SpriteInfo
            {
                name = IconSpriteName,
                texture = texture,
                region = new Rect(0f, 0f, 1f, 1f),
            });

            _iconAtlas = atlas;
            return atlas;
        }
    }
}
