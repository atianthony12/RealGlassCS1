using UnityEngine;

namespace RealGlass.Glass
{
    /// <summary>
    /// The rooms behind the glass (UnityProject/Assets/RealGlass/Interiors.cginc): what the settings,
    /// the hour and the clock make of them, as the shader's uniforms. Engine types only, C# 4: the GPU
    /// preview runs this same file (tools/preview/sync-sim.ps1).
    /// </summary>
    /// <remarks>
    /// Which rooms are lit: each room draws a number of its own for every stretch of
    /// <see cref="HoursPerChange"/> / switching game hours (its own phase), and is lit while that number
    /// is under its kind's share of the hour -- so no two rooms change together, a room keeps its state
    /// for hours, and the share follows the day: homes busiest in the evening and nearly dark before
    /// dawn (the shape of Richardson et al. 2009's domestic lighting demand), offices by working hours
    /// with a floor of lights left on (EnergyPlus's office lighting schedule, raised at night: Dobler et
    /// al. 2015 found commercial windows far steadier than homes'), shops by opening hours.
    /// </remarks>
    public static class Interiors
    {
        // A building's kind, in the a channel of its _RGAssets texel (+ 8 x its asset's own switch,
        // + 24 x its asset's own curtains switch, + 72 x its asset's own illumination-map switch: each
        // 0 follows the global one, 1 off, 2 on; 255 not looked at yet). Interiors.cginc decodes it.
        public const int KindHome = 0;
        public const int KindOffice = 1;
        public const int KindShopLow = 2;     // ground floor a shop, homes above
        public const int KindHotel = 3;       // ground floor a lobby / restaurant, rooms above
        public const int KindLeisure = 4;
        public const int KindPublic = 5;
        public const int KindIndustry = 6;
        public const int KindShopHigh = 7;    // ground floor a shop, offices above

        public const int FollowGlobal = 0;
        public const int AssetOff = 1;
        public const int AssetOn = 2;

        // The atlas's groups of pictures (RoomAtlas.Pools; tools/interiors/rooms.txt's kinds).
        public const int PoolHome = 0;
        public const int PoolOffice = 1;
        public const int PoolShop = 2;
        public const int PoolHotel = 3;
        public const int PoolLeisure = 4;
        public const int PoolPublic = 5;
        public const int PoolIndustry = 6;

        /// <summary>Game hours a room keeps its light on or off at 100% switching (it may change at the turn of each).</summary>
        public const float HoursPerChange = 4f;

        /// <summary>
        /// A lit room's average light at 100% brightness, linear: about the game's own lit window, whose
        /// building shader adds 3 x its light-colour texture where a window is lit (the disassembly of
        /// Custom/Buildings/Building/Default, 2026-10-07) -- a little under, for the lamp-lit room has
        /// bright and dark parts where the game's window is one flat glow.
        /// </summary>
        public const float LitRoomLuminance = 1.6f;

        /// <summary>The clock wraps after this many game hours (256 days), so a float keeps every minute.</summary>
        public const double ClockWrap = 24.0 * 256.0;

        // The share of rooms lit at each hour (0..23), as a share of the busiest hour's. Interpolated.
        private static readonly float[] Homes =
        {
            0.42f, 0.28f, 0.18f, 0.13f, 0.11f, 0.15f, 0.32f, 0.45f, 0.30f, 0.20f, 0.18f, 0.18f,
            0.20f, 0.20f, 0.20f, 0.22f, 0.30f, 0.50f, 0.72f, 0.90f, 1.00f, 1.00f, 0.86f, 0.64f,
        };

        private static readonly float[] Offices =
        {
            0.22f, 0.22f, 0.22f, 0.22f, 0.22f, 0.24f, 0.32f, 0.55f, 0.88f, 1.00f, 1.00f, 1.00f,
            1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 0.85f, 0.68f, 0.55f, 0.46f, 0.38f, 0.30f, 0.25f,
        };

        private static readonly float[] Shops =
        {
            0.12f, 0.12f, 0.12f, 0.12f, 0.12f, 0.12f, 0.12f, 0.30f, 0.70f, 1.00f, 1.00f, 1.00f,
            1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 0.90f, 0.60f, 0.35f, 0.18f,
        };

        // Hotels, restaurants and bars, hospitals and civic buildings: lit late, some all night.
        private static readonly float[] Others =
        {
            0.55f, 0.45f, 0.38f, 0.35f, 0.35f, 0.35f, 0.45f, 0.60f, 0.70f, 0.75f, 0.75f, 0.75f,
            0.75f, 0.75f, 0.75f, 0.75f, 0.75f, 0.82f, 0.90f, 0.97f, 1.00f, 1.00f, 0.90f, 0.72f,
        };

        private static int _rooms, _rooms2, _rooms3, _share, _pools;
        private static bool _ids;
        private static readonly Vector4[] PoolsOut = new Vector4[RoomAtlas.PoolCount];

        private static void Ids()
        {
            if (_ids)
                return;
            _rooms = Shader.PropertyToID("_RGRooms");
            _rooms2 = Shader.PropertyToID("_RGRooms2");
            _rooms3 = Shader.PropertyToID("_RGRooms3");
            _share = Shader.PropertyToID("_RGRoomShare");
            _pools = Shader.PropertyToID("_RGRoomPools");
            _ids = true;
        }

        /// <summary>One hour curve at a time of day (0..24), between its hours.</summary>
        public static float Curve(float[] curve, float hour)
        {
            float h = hour - 24f * Mathf.Floor(hour / 24f);
            int i = Mathf.Clamp((int)h, 0, 23);
            float t = h - i;
            return Mathf.Lerp(curve[i], curve[(i + 1) % 24], t);
        }

        /// <summary>The share of rooms lit now: x homes, y offices and industry, z shops, w hotels, leisure, public.</summary>
        public static Vector4 Shares(GlassParams p, float hour)
        {
            float lit = Mathf.Clamp(p.InteriorsLit, 0f, 0.95f);
            if (!p.InteriorsLateNight)
                return new Vector4(lit, lit, lit, lit);

            return new Vector4(lit * Curve(Homes, hour), lit * Curve(Offices, hour), lit * Curve(Shops, hour), lit * Curve(Others, hour));
        }

        /// <summary>Game hours a room keeps its state for, at this switching (0: it never changes).</summary>
        public static float HoursPerState(float switching)
        {
            return switching > 0.001f ? HoursPerChange / switching : 0f;
        }

        /// <summary>
        /// The uniforms. show: rooms are drawn anywhere (the global switch, or an asset of its own);
        /// atlas: the pictures (null or not loaded: no rooms at all).
        /// </summary>
        public static void Apply(GlassParams p, GlassFrame f, RoomAtlas atlas, bool show)
        {
            Ids();
            bool loaded = atlas != null && atlas.Loaded;
            // y: how much of the rooms shows by day. Glass.shader fades from the dark inside to the
            // room (lerp(inside, roomDay, rooms x y)) and the window's cover from the glass's strength
            // to the whole window with it: 1 is the rooms in full, 0 the same as no rooms by day.
            float byDay = p.InteriorsByDay ? Mathf.Clamp01(p.InteriorsDayOpacity) : 0f;
            Shader.SetGlobalVector(_rooms, new Vector4(p.Interiors ? 1f : 0f, byDay, f.Clock,
                                                       HoursPerState(p.InteriorsSwitching)));
            Shader.SetGlobalVector(_rooms2, new Vector4(Mathf.Max(p.InteriorsBrightness, 0f) * LitRoomLuminance,
                                                        Mathf.Clamp(p.InteriorsColoured, 0f, 1f), Mathf.Clamp01(p.InteriorsCurtains),
                                                        show && loaded ? 1f : 0f));
            // w: the coloured lights' share in workplaces (offices, shops, industry, public; Interiors.cginc).
            Shader.SetGlobalVector(_rooms3, new Vector4(Mathf.Clamp01(p.InteriorsDepth), p.InteriorsMatchLods ? 1f : 0f,
                                                        p.InteriorsFollowIllumination ? 1f : 0f, Mathf.Clamp(p.InteriorsColouredWork, 0f, 1f)));
            Shader.SetGlobalVector(_share, Shares(p, f.Hour));
            if (!loaded)
                return;

            // A kind the atlas has no pictures of borrows: offices for the working kinds, homes else.
            for (int k = 0; k < RoomAtlas.PoolCount; k++)
            {
                Vector4 pool = atlas.Pools[k];
                if (pool.y < 0.5f)
                {
                    Vector4 office = atlas.Pools[PoolOffice], home = atlas.Pools[PoolHome];
                    bool working = k == PoolShop || k == PoolIndustry || k == PoolPublic;
                    pool = working && office.y > 0.5f ? office : (home.y > 0.5f ? home : office);
                    if (pool.y < 0.5f)
                        pool = new Vector4(0f, Mathf.Max(atlas.Tiles, 1), atlas.Pools[k].z, atlas.Pools[k].w);
                }
                PoolsOut[k] = pool;
            }
            Shader.SetGlobalVectorArray(_pools, PoolsOut);
            atlas.Bind();
        }

        /// <summary>A building texel's a channel: its kind and its asset's own switch, its curtains following the global share.</summary>
        public static byte Texel(int kind, int assetSwitch)
        {
            return Texel(kind, assetSwitch, FollowGlobal, FollowGlobal);
        }

        /// <summary>A building texel's a channel: its kind, its asset's own switch and its asset's own curtains switch.</summary>
        public static byte Texel(int kind, int assetSwitch, int curtains)
        {
            return Texel(kind, assetSwitch, curtains, FollowGlobal);
        }

        /// <summary>
        /// A building texel's a channel: its kind (0..7), its asset's own switch, curtains switch and
        /// illumination-map switch (at most 215).
        /// </summary>
        public static byte Texel(int kind, int assetSwitch, int curtains, int illumination)
        {
            return (byte)(Mathf.Clamp(kind, 0, KindShopHigh) + 8 * Mathf.Clamp(assetSwitch, 0, 2) + 24 * Mathf.Clamp(curtains, 0, 2) +
                          72 * Mathf.Clamp(illumination, 0, 2));
        }
    }
}
