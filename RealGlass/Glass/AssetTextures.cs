using UnityEngine;

namespace RealGlass.Glass
{
    /// <summary>
    /// Each building's own glass, where the shader finds it at every distance (Glass.shader,
    /// _RGAssets / _RGAssetGlass): a texture laid out like the game's object colour map -- one texel
    /// per building id, the id a building's colour index names, which every distance tier carries
    /// (near _ObjectIndex, middle _ObjectIndices[], far the baked second uv) and which the game points
    /// at the MAIN building of a complex -- and a small table of the glasses those texels name.
    /// Engine types only, C# 4: the GPU preview runs this same file (tools/preview/sync-sim.ps1).
    /// </summary>
    /// <remarks>
    /// A building texel: r strength, g painted detail (0..254 = 0..100%), b the glass slot
    /// (0..254), a - ; <see cref="Global"/> (255) in a channel: the global setting. A glass slot is three
    /// texels of the table, every value resolved (the asset's own, else its kind of glass's, else the
    /// global): row 0 the tint (linear, brightest channel 1) and the own-colour mix, row 1 the
    /// reflectivity, the clarity, the shininess needed to count as glass (0..255 = 0..1) and whether
    /// the windows lit at night count too (0 or 255), row 2 the dirt on its panes (r: the asset's own
    /// 0..254 = 0..1, or 255 the Glass tab's; gba spare).
    /// </remarks>
    /// <summary>A slot's rooms' lights, as the table's rows 3..6 hold them (AssetTextures.Lights).</summary>
    public struct LightRows
    {
        public Color32 Main, Second, Numbers, Day;

        /// <summary>The 14 bytes that tell two apart, in two numbers.</summary>
        public ulong KeyA
        {
            get { return Pack(Main) | (ulong)Pack(Second) << 32; }
        }

        public ulong KeyB
        {
            get { return Pack(Numbers) | (ulong)(Day.r | Day.g << 8) << 32; }
        }

        private static uint Pack(Color32 c)
        {
            return (uint)(c.r | c.g << 8 | c.b << 16 | c.a << 24);
        }
    }

    public sealed class AssetTextures
    {
        /// <summary>In any channel of a building's texel: that setting is the global one.</summary>
        public const byte Global = 255;

        /// <summary>How many different glasses the table holds (slot 255 is "the global glass").</summary>
        public const int GlassSlots = 255;

        private const int TableWidth = 256;

        /// <summary>The table's rows (the shaders read row k at v = (k + 0.5) / RG_ASSET_ROWS, Interiors.cginc).</summary>
        private const int TableRows = 7;

        private static readonly Color32 AllGlobal = new Color32(Global, Global, Global, Global);

        private Texture2D _map, _table;
        private Color32[] _mapPixels, _tablePixels;
        private int _width, _height;
        private bool _mapDirty, _tableDirty;

        public bool IsCreated
        {
            get { return _map != null; }
        }

        public int Width
        {
            get { return _width; }
        }

        public int Height
        {
            get { return _height; }
        }

        /// <summary>
        /// Makes both textures, every building at the global settings. width x height: the game's
        /// object colour map (RenderManager.OBJECT_COLORMAP_WIDTH x HEIGHT, 512 x 256).
        /// </summary>
        public void Create(int width, int height)
        {
            if (_map != null && width == _width && height == _height)
                return;

            Destroy();
            _width = width;
            _height = height;
            _map = NewTexture("RealGlass assets", width, height);
            _mapPixels = new Color32[width * height];
            for (int i = 0; i < _mapPixels.Length; i++)
                _mapPixels[i] = AllGlobal;

            _table = NewTexture("RealGlass asset glass", TableWidth, TableRows);
            _tablePixels = new Color32[TableWidth * TableRows];
            for (int i = 0; i < _tablePixels.Length; i++)
                _tablePixels[i] = i < 2 * TableWidth ? new Color32(255, 255, 255, 0)
                                : (i < 3 * TableWidth ? new Color32(Global, 0, 0, 0) : AllGlobal);   // rows 3..6: the Interiors tab's

            _mapDirty = _tableDirty = true;
            Upload();
        }

        private static Texture2D NewTexture(string name, int width, int height)
        {
            var t = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            t.name = name;
            t.filterMode = FilterMode.Point;
            t.wrapMode = TextureWrapMode.Clamp;
            t.anisoLevel = 0;
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        /// <summary>A building's texel; true when it changed.</summary>
        public bool Set(int building, Color32 texel)
        {
            if (_mapPixels == null || building < 0 || building >= _mapPixels.Length)
                return false;

            Color32 old = _mapPixels[building];
            if (old.r == texel.r && old.g == texel.g && old.b == texel.b && old.a == texel.a)
                return false;

            _mapPixels[building] = texel;
            _mapDirty = true;
            return true;
        }

        /// <summary>Every building back to the global settings.</summary>
        public void ClearAll()
        {
            if (_mapPixels == null)
                return;

            for (int i = 0; i < _mapPixels.Length; i++)
                _mapPixels[i] = AllGlobal;
            _mapDirty = true;
        }

        /// <summary>
        /// One glass of the table. tint: the shader's tint (linear, brightest channel 1, as
        /// GlassUniforms.Tint makes it); the rest 0..1.
        /// </summary>
        public void SetGlass(int slot, Vector3 tint, float ownColour, float reflectivity, float clarity, float threshold,
                             bool nightWindows, float? dirt)
        {
            if (_tablePixels == null || slot < 0 || slot >= GlassSlots)
                return;

            _tablePixels[slot] = new Color32(Byte(tint.x), Byte(tint.y), Byte(tint.z), Byte(ownColour));
            _tablePixels[TableWidth + slot] = new Color32(Byte(reflectivity), Byte(clarity), Byte(threshold), (byte)(nightWindows ? 255 : 0));
            // The dirt: the asset's own (0..254), or Global (255): the Glass tab's, read live by the shader.
            _tablePixels[2 * TableWidth + slot] = new Color32(dirt.HasValue ? Encode(dirt.Value) : Global, 0, 0, 0);
            _tableDirty = true;
        }

        /// <summary>
        /// The rooms' lights of one slot (rows 3..6; Interiors.cginc, BuildingLightsOf), each the
        /// asset's own or <see cref="Global"/> (255): the Interiors tab's, read live by the shader.
        /// mainByte: Interiors.MiredByte of its white, 254 a colour of its own (mainColour), 255 none;
        /// colours linear, brightest channel 1. secondShare 0 or null: no second colour.
        /// </summary>
        public static LightRows Lights(byte mainByte, Vector3 mainColour, Vector3 secondColour, float? secondShare,
                                       float? variety, float? other, float? lit, float? brightness, bool? byDay, float? dayOpacity)
        {
            LightRows rows;
            rows.Main = new Color32(Byte(mainColour.x), Byte(mainColour.y), Byte(mainColour.z), mainByte);
            rows.Second = new Color32(Byte(secondColour.x), Byte(secondColour.y), Byte(secondColour.z),
                                      secondShare.HasValue && secondShare.Value > 0f ? Encode(secondShare.Value) : Global);
            // Brightness 0..3 in 0..254.
            rows.Numbers = new Color32(variety.HasValue ? Encode(variety.Value) : Global, other.HasValue ? Encode(other.Value) : Global,
                                       lit.HasValue ? Encode(lit.Value) : Global, brightness.HasValue ? Encode(brightness.Value / 3f) : Global);
            rows.Day = new Color32(byDay.HasValue ? (byte)(byDay.Value ? 254 : 0) : Global, dayOpacity.HasValue ? Encode(dayOpacity.Value) : Global, 0, 0);
            return rows;
        }

        /// <summary>Every light the Interiors tab's: a slot that only has glass of its own.</summary>
        public static readonly LightRows NoLights = new LightRows
        {
            Main = new Color32(Global, Global, Global, Global), Second = new Color32(Global, Global, Global, Global),
            Numbers = new Color32(Global, Global, Global, Global), Day = new Color32(Global, Global, 0, 0),
        };

        public void SetLights(int slot, LightRows rows)
        {
            if (_tablePixels == null || slot < 0 || slot >= GlassSlots)
                return;

            _tablePixels[3 * TableWidth + slot] = rows.Main;
            _tablePixels[4 * TableWidth + slot] = rows.Second;
            _tablePixels[5 * TableWidth + slot] = rows.Numbers;
            _tablePixels[6 * TableWidth + slot] = rows.Day;
            _tableDirty = true;
        }

        /// <summary>Sends what changed to the GPU; true when anything was sent.</summary>
        public bool Upload()
        {
            bool sent = false;
            if (_mapDirty && _map != null)
            {
                _map.SetPixels32(_mapPixels);
                _map.Apply(false);
                _mapDirty = false;
                sent = true;
            }

            if (_tableDirty && _table != null)
            {
                _table.SetPixels32(_tablePixels);
                _table.Apply(false);
                _tableDirty = false;
                sent = true;
            }

            return sent;
        }

        public bool Pending
        {
            get { return _mapDirty || _tableDirty; }
        }

        public void Bind()
        {
            if (_map == null)
                return;

            Shader.SetGlobalTexture("_RGAssets", _map);
            Shader.SetGlobalTexture("_RGAssetGlass", _table);
        }

        /// <summary>0..1 as a building texel's 0..254 (255 is "global").</summary>
        public static byte Encode(float value)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(value) * 254f), 0, 254);
        }

        private static byte Byte(float value)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(value) * 255f), 0, 255);
        }

        public void Destroy()
        {
            // Immediate, as CityMap's: the preview runs in the editor, where Destroy is refused.
            if (_map != null)
                Object.DestroyImmediate(_map);
            if (_table != null)
                Object.DestroyImmediate(_table);
            _map = _table = null;
            _mapPixels = _tablePixels = null;
        }
    }
}
