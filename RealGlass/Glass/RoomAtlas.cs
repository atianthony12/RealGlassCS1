using System;
using UnityEngine;

namespace RealGlass.Glass
{
    /// <summary>
    /// The pictures of the rooms behind the glass (Interiors.cginc): two atlases of the same rooms in
    /// the same places -- by night, each lit by its own lamps (a neutral white the shader colours),
    /// and by day -- shipped as raw DXT1 data with their mips (tools/interiors/build-rooms.ps1 makes
    /// them; Editor/RoomAtlasBaker.cs compresses them) and loaded with Texture2D.LoadRawTextureData
    /// (probed in the Unity 5.6 editor on Direct3D 11 and OpenGL Core, 2026-10-07). Engine types only,
    /// C# 4: the GPU preview runs this same file (tools/preview/sync-sim.ps1).
    /// </summary>
    /// <remarks>
    /// A file: "RGRA", version 2, width, height, mips, Unity's TextureFormat, tiles a row, a tile's
    /// pixels, tiles, the back wall's share of a picture (float), then per kind of room
    /// (<see cref="Pools"/>) its first tile, how many, its box's aspect and depth (floats), then the
    /// data's length and the data (little-endian). Tile k is at column k % row, row k / row, counted
    /// from the BOTTOM of the texture (uv's v up), the picture upright in it. Each picture is a BOX
    /// ROOM seen through its window (tools/interiors/render-rooms.py, box_view): the room x -A..A
    /// across, -1..1 up, 0..D deep, from a camera far enough in front that the back wall fills
    /// <see cref="Far"/> of the picture.
    /// </remarks>
    public sealed class RoomAtlas
    {
        /// <summary>Kinds of room, the atlas's groups: homes, offices, shops, hotels, leisure, public, industry, (spare).</summary>
        public const int PoolCount = 8;

        private const int Version = 2;

        public Texture2D Night { get; private set; }
        public Texture2D Day { get; private set; }
        public int TilesPerRow { get; private set; }
        public int NightTile { get; private set; }
        public int DayTile { get; private set; }
        public int Tiles { get; private set; }

        /// <summary>Rows of tiles: the texture's height over a tile's (8 x 8 until 2026-11-08, 16 x 8 since).</summary>
        public int Rows { get; private set; }

        /// <summary>The share of a picture, across, that is the room's back wall.</summary>
        public float Far { get; private set; }

        /// <summary>
        /// Per kind of room: x its first tile, y how many (0: none -- the shader is given another kind's),
        /// z its box's aspect (width / height), w its depth (in half-heights).
        /// </summary>
        public readonly Vector4[] Pools = new Vector4[PoolCount];

        /// <summary>Why the last Load failed; "" when it did not.</summary>
        public string Problem { get; private set; }

        public bool Loaded
        {
            get { return Night != null && Day != null; }
        }

        public RoomAtlas()
        {
            Problem = "";
        }

        /// <summary>Both pictures from their files' bytes; false (and <see cref="Problem"/>) when either is wrong.</summary>
        public bool Load(byte[] night, byte[] day)
        {
            Destroy();
            try
            {
                int nightRow, nightTiles, dayRow, dayTiles, nightTile, dayTile;
                float nightFar, dayFar;
                var nightPools = new Vector4[PoolCount];
                var dayPools = new Vector4[PoolCount];
                Texture2D n = Read(night, "rooms by night", out nightRow, out nightTile, out nightTiles, out nightFar, nightPools);
                if (n == null)
                    return false;
                Texture2D d = Read(day, "rooms by day", out dayRow, out dayTile, out dayTiles, out dayFar, dayPools);
                if (d == null)
                {
                    UnityEngine.Object.DestroyImmediate(n);
                    return false;
                }

                if (nightRow != dayRow || nightTiles != dayTiles || n.height / nightTile != d.height / dayTile ||
                    nightTiles > nightRow * (n.height / nightTile))
                {
                    Problem = "the day and night pictures do not hold the same rooms (" + nightTiles + " / " + dayTiles + ")";
                    UnityEngine.Object.DestroyImmediate(n);
                    UnityEngine.Object.DestroyImmediate(d);
                    return false;
                }

                Night = n;
                Day = d;
                TilesPerRow = nightRow;
                Rows = Mathf.Max(n.height / nightTile, 1);
                NightTile = nightTile;
                DayTile = dayTile;
                Tiles = nightTiles;
                Far = nightFar;
                for (int k = 0; k < PoolCount; k++)
                    Pools[k] = nightPools[k];
                Problem = "";
                return true;
            }
            catch (Exception e)
            {
                Problem = e.GetType().Name + ": " + e.Message;
                Destroy();
                return false;
            }
        }

        private Texture2D Read(byte[] bytes, string what, out int perRow, out int tilePx, out int tiles, out float far, Vector4[] pools)
        {
            perRow = tilePx = tiles = 0;
            far = 0f;
            if (bytes == null || bytes.Length < 64)
            {
                Problem = what + ": no data";
                return null;
            }

            int at = 0;
            if (bytes[0] != 'R' || bytes[1] != 'G' || bytes[2] != 'R' || bytes[3] != 'A')
            {
                Problem = what + ": not a room atlas";
                return null;
            }
            at = 4;
            int version = Int(bytes, ref at);
            int width = Int(bytes, ref at), height = Int(bytes, ref at), mips = Int(bytes, ref at), format = Int(bytes, ref at);
            perRow = Int(bytes, ref at);
            tilePx = Int(bytes, ref at);
            tiles = Int(bytes, ref at);
            far = Float(bytes, ref at);
            int poolCount = Int(bytes, ref at);
            if (version != Version || format != (int)TextureFormat.DXT1 || width <= 0 || height <= 0 || width > 8192 || height > 8192 ||
                mips < 1 || perRow <= 0 || tilePx <= 0 || tiles <= 0 || poolCount != PoolCount || !(far > 0.05f && far < 0.95f))
            {
                Problem = what + ": version " + version + ", " + width + "x" + height + ", format " + format + ", " + poolCount + " kinds: not ours";
                return null;
            }

            for (int k = 0; k < PoolCount; k++)
            {
                int first = Int(bytes, ref at), count = Int(bytes, ref at);
                float aspect = Float(bytes, ref at), depth = Float(bytes, ref at);
                if (!(aspect > 0.2f && aspect < 8f && depth > 0.1f && depth < 20f))
                {
                    Problem = what + ": kind " + k + "'s box " + aspect + " x " + depth + ": not a room";
                    return null;
                }
                pools[k] = new Vector4(first, count, aspect, depth);
            }

            int length = Int(bytes, ref at);
            if (length <= 0 || at + length > bytes.Length)
            {
                Problem = what + ": " + length + " bytes of pictures, " + (bytes.Length - at) + " in the file";
                return null;
            }

            var data = new byte[length];
            Buffer.BlockCopy(bytes, at, data, 0, length);

            // sRGB (linear = false): the pictures are colours, and the shader reads light.
            var t = new Texture2D(width, height, TextureFormat.DXT1, mips > 1, false);
            if (t.mipmapCount != mips)
            {
                Problem = what + ": " + mips + " mips in the file, " + t.mipmapCount + " in the texture";
                UnityEngine.Object.DestroyImmediate(t);
                return null;
            }

            t.name = "RealGlass " + what;
            t.hideFlags = HideFlags.HideAndDontSave;
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Trilinear;
            t.anisoLevel = 4;
            t.LoadRawTextureData(data);
            t.Apply(false, true);   // no longer readable: the copy in memory is let go
            return t;
        }

        private static int Int(byte[] b, ref int at)
        {
            int v = b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24);
            at += 4;
            return v;
        }

        private static float Float(byte[] b, ref int at)
        {
            float v = BitConverter.ToSingle(b, at);   // little-endian on every platform the game runs on
            at += 4;
            return v;
        }

        /// <summary>The atlases and their layout, for the shader (Interiors.cginc).</summary>
        public void Bind()
        {
            if (!Loaded)
                return;

            Shader.SetGlobalTexture("_RGRoomAtlas", Night);
            Shader.SetGlobalTexture("_RGRoomAtlasDay", Day);
            Shader.SetGlobalVector("_RGRoomAtlasInfo", new Vector4(TilesPerRow, NightTile, DayTile, Far));
            Shader.SetGlobalFloat("_RGRoomAtlasRows", Rows);
        }

        public void Destroy()
        {
            // Immediate, as the other textures': the preview runs in the editor, where Destroy is refused.
            if (Night != null)
                UnityEngine.Object.DestroyImmediate(Night);
            if (Day != null)
                UnityEngine.Object.DestroyImmediate(Day);
            Night = Day = null;
        }
    }
}
