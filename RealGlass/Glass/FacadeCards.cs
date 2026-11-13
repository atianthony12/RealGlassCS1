using UnityEngine;
using UnityEngine.Rendering;

namespace RealGlass.Glass
{
    /// <summary>
    /// Facade cards: each building asset's four sides and its roof, drawn once into tiles of an atlas,
    /// and per building in the city map where its card is -- so that a reflected ray the screen cannot
    /// answer, walking the city from above (CityMap, CityHit), meets that building's real facade
    /// instead of a guess (FacadeCards.cginc). The idea is Unreal's Lumen's: a coarse scene to trace
    /// and a cache of cards that hold each mesh's surface "from multiple angles" to shade what the trace
    /// meets (Epic Games, "Lumen Technical Details").
    /// </summary>
    /// <remarks>
    /// <para>A PREVIEW ROUND (2026-11-12): the GPU preview runs this file (tools/preview/sync-sim.ps1);
    /// the mod does not use it yet. Engine types only, C# 4.</para>
    /// <para>THE ATLASES: three RenderTextures a side, ARGB32 with mips. _RGCardAtlas (sRGB): rgb the
    /// albedo, a the share of the texel that is glass. _RGCardLight (linear): r the windows that glow
    /// day and night (illumination b up to 0.48, as b / 0.48), g and b the shares the game can light at
    /// night with b in 0.48..0.6 and 0.6..0.75, a the share the mesh covers. _RGCardSpec (linear): r the
    /// specular, g and b the shares with b in 0.75..0.88 and 0.88..1. All premultiplied by the share
    /// covered, so their mips are true averages: the game's lit-window rule is a step on b, so b itself
    /// is never averaged (the bake reads each map texel by texel, the lookup lights each bin at its
    /// centre). A slot is five tiles of TileW x TileH texels in a row of tiles (sides -z, +x, +z, -x,
    /// then the roof); each tile on its own size's grid, so no mip up to the tile's size mixes two.</para>
    /// <para>THE RECORDS: one RGBA32 texture, point-read, exact integers in 16-bit pairs (r high, g low;
    /// b high, a low): per building id (the colour index's root, the city map's) three texels -- its
    /// position x, z (1/8 m from <see cref="Origin"/>), its base y (1/16 m from -512) and heading (1/65536
    /// of a turn), its slot (0: no card); then from row <see cref="BuildingRows"/>, per slot three texels:
    /// its box (mesh space, 1/32 m from -1024).</para>
    /// <para>THE BAKE: per side a clear quad and the asset's mesh through FacadeCardBake.shader, into
    /// both atlases at once with the albedo atlas's depth (a command buffer run at once,
    /// Graphics.ExecuteCommandBuffer, as LodGlass's and RoomFitMap's read-backs run theirs). The clip
    /// positions are written by the shader itself, not through Unity's matrices.</para>
    /// </remarks>
    public sealed class FacadeCards
    {
        /// <summary>The shader keyword the readers are compiled with (the switch: the mod never sets it this round).</summary>
        public const string Keyword = "RG_FACADE_CARDS";

        public const int Faces = 5;
        public const int RecordWidth = 3 * 256;
        public const int BuildingRows = 256;
        public const int SlotRows = 16;

        private const int PassClear = 0, PassBake = 1, PassShow = 2;

        private static bool _ids;
        private static int _idAtlas, _idLight, _idSpec, _idRecords, _idInfo, _idInfo2, _idOrigin, _idSun, _idAmbient, _idAmbientUp, _idLamp,
                           _idFrame, _idTile, _idBakeInfo, _idMainTex, _idMainST, _idXys, _idAci;

        private readonly Material _bake;
        private Mesh _quad;
        private RenderTexture _atlas, _light, _spec;
        private Texture2D _records;
        private readonly Color32[] _recordPixels = new Color32[RecordWidth * (BuildingRows + SlotRows)];
        private int _slots;
        private bool _loggedBake;

        public int Size { get; private set; }
        public int TileW { get; private set; }
        public int TileH { get; private set; }

        /// <summary>Where the records' positions count from (world x, z).</summary>
        public Vector2 Origin { get; private set; }

        /// <summary>Slots baked.</summary>
        public int Slots
        {
            get { return _slots; }
        }

        /// <summary>
        /// The small tiles (the game's far buildings; 0: none): from row <see cref="SplitRow"/> down the atlas
        /// is tiled SmallW x SmallH, above it TileW x TileH. Slots 1..<see cref="FullCapacity"/> are full
        /// size, the rest small.
        /// </summary>
        public int SmallW { get; private set; }
        public int SmallH { get; private set; }
        public int SplitRow { get; private set; }

        /// <summary>Full-size slots (all of them without small tiles).</summary>
        public int FullCapacity
        {
            get { return Size <= 0 ? 0 : Mathf.Min((Size / TileW) * ((SmallW > 0 ? SplitRow : Size) / TileH) / Faces, SlotRows * 256); }
        }

        /// <summary>Small slots, after the full-size ones.</summary>
        public int SmallCapacity
        {
            get { return Size <= 0 || SmallW <= 0 ? 0 : Mathf.Min((Size / SmallW) * ((Size - SplitRow) / SmallH) / Faces, SlotRows * 256 - FullCapacity); }
        }

        public int Capacity
        {
            get { return FullCapacity + SmallCapacity; }
        }

        /// <summary>Why it is not working ("" when it is).</summary>
        public string Problem = "";

        /// <summary>
        /// The bake reads the illumination map bilinearly (as the game's own pixel reads it up close: a
        /// window's edges in between) instead of at its texels' centres. The preview's choice (round 2, R5).
        /// </summary>
        public bool FilteredIllumination;

        /// <summary>Lines for the log: each engine call made for the first time is announced before it is made.</summary>
        public System.Action<string> Log;

        public RenderTexture Atlas
        {
            get { return _atlas; }
        }

        public RenderTexture Light
        {
            get { return _light; }
        }

        public RenderTexture Specular
        {
            get { return _spec; }
        }

        public FacadeCards(Shader bake)
        {
            Ids();
            _bake = new Material(bake) { name = "RealGlass facade card bake", hideFlags = HideFlags.HideAndDontSave };
        }

        private static void Ids()
        {
            if (_ids)
                return;
            _idAtlas = Shader.PropertyToID("_RGCardAtlas");
            _idLight = Shader.PropertyToID("_RGCardLight");
            _idSpec = Shader.PropertyToID("_RGCardSpec");
            _idRecords = Shader.PropertyToID("_RGCardRecords");
            _idInfo = Shader.PropertyToID("_RGCardInfo");
            _idInfo2 = Shader.PropertyToID("_RGCardInfo2");
            _idOrigin = Shader.PropertyToID("_RGCardOrigin");
            _idSun = Shader.PropertyToID("_RGCardSun");
            _idAmbient = Shader.PropertyToID("_RGCardAmbient");
            _idAmbientUp = Shader.PropertyToID("_RGCardAmbientUp");
            _idLamp = Shader.PropertyToID("_RGCardLamp");
            _idFrame = Shader.PropertyToID("_RGCardFrame");
            _idTile = Shader.PropertyToID("_RGCardTile");
            _idBakeInfo = Shader.PropertyToID("_RGCardBakeInfo");
            _idMainTex = Shader.PropertyToID("_MainTex");
            _idMainST = Shader.PropertyToID("_MainTex_ST");
            _idXys = Shader.PropertyToID("_XYSMap");
            _idAci = Shader.PropertyToID("_ACIMap");
            _ids = true;
        }

        private void Say(string line)
        {
            if (Log != null)
                Log(line);
        }

        /// <summary>The atlases (size a side, power of two) and the records, empty. False when they cannot be made.</summary>
        public bool Create(int size, int tileW, int tileH)
        {
            return Create(size, tileW, tileH, 0, 0, size);
        }

        /// <summary>
        /// The same with two kinds of tile: tileW x tileH above splitRow, smallW x smallH from it down (the
        /// mod's: near buildings sharp, far ones in a quarter of the room). smallW 0: one kind.
        /// </summary>
        public bool Create(int size, int tileW, int tileH, int smallW, int smallH, int splitRow)
        {
            Destroy();
            Size = size;
            TileW = tileW;
            TileH = tileH;
            SmallW = smallW;
            SmallH = smallH;
            SplitRow = smallW > 0 ? splitRow : size;
            if (!Mathf.IsPowerOfTwo(size) || !Mathf.IsPowerOfTwo(tileW) || !Mathf.IsPowerOfTwo(tileH) || tileW > size || tileH > size ||
                (smallW > 0 && (!Mathf.IsPowerOfTwo(smallW) || !Mathf.IsPowerOfTwo(smallH) || splitRow % tileH != 0 || splitRow % smallH != 0 || splitRow <= 0 || splitRow >= size)))
            {
                Problem = "sizes must be powers of two within the atlas, the split on both tiles' grids";
                return false;
            }

            Say("facade cards: making the atlases " + size + "x" + size + " with mips (ARGB32 sRGB with depth, two ARGB32 linear)");
            _atlas = NewAtlas("RealGlass facade cards", size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            _light = NewAtlas("RealGlass facade cards' light", size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            _spec = NewAtlas("RealGlass facade cards' specular", size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            _records = new Texture2D(RecordWidth, BuildingRows + SlotRows, TextureFormat.RGBA32, false, true)
            {
                name = "RealGlass facade card records",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            System.Array.Clear(_recordPixels, 0, _recordPixels.Length);
            _quad = GlassScreen.ClipQuad();
            _slots = 0;
            Problem = "";

            // Every tile empty: one clear of both, with the depth.
            var cb = new CommandBuffer { name = "RealGlass facade cards: clear" };
            cb.SetRenderTarget(Targets(), _atlas);
            cb.ClearRenderTarget(true, true, new Color(0f, 0f, 0f, 0f));
            Say("facade cards: clearing the atlases at once (three targets, the first's depth)");
            Graphics.ExecuteCommandBuffer(cb);
            cb.Release();
            return true;
        }

        private RenderTargetIdentifier[] Targets()
        {
            return new RenderTargetIdentifier[] { _atlas, _light, _spec };
        }

        private static RenderTexture NewAtlas(string name, int size, int depth, RenderTextureFormat format, RenderTextureReadWrite readWrite)
        {
            var rt = new RenderTexture(size, size, depth, format, readWrite)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                useMipMap = true,
                autoGenerateMips = true,
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            return rt;
        }

        /// <summary>
        /// Where a mesh point lands on face f of the box (min, max), as a matrix: (u along the side seen
        /// from outside, v up -- or across the roof, z -- and the depth from the side, 0..1). The same
        /// layout as FacadeCards.cginc's CardLookup.
        /// </summary>
        public static Matrix4x4 Frame(int face, Vector3 min, Vector3 max)
        {
            Vector3 e = max - min;
            float ex = Mathf.Max(e.x, 0.01f), ey = Mathf.Max(e.y, 0.01f), ez = Mathf.Max(e.z, 0.01f);
            var m = Matrix4x4.zero;
            m.m33 = 1f;
            switch (face)
            {
                case 0:   // -z, seen from -z: u along +x
                    m.SetRow(0, new Vector4(1f / ex, 0f, 0f, -min.x / ex));
                    m.SetRow(1, new Vector4(0f, 1f / ey, 0f, -min.y / ey));
                    m.SetRow(2, new Vector4(0f, 0f, 1f / ez, -min.z / ez));
                    break;
                case 1:   // +x: u along +z
                    m.SetRow(0, new Vector4(0f, 0f, 1f / ez, -min.z / ez));
                    m.SetRow(1, new Vector4(0f, 1f / ey, 0f, -min.y / ey));
                    m.SetRow(2, new Vector4(-1f / ex, 0f, 0f, max.x / ex));
                    break;
                case 2:   // +z: u along -x
                    m.SetRow(0, new Vector4(-1f / ex, 0f, 0f, max.x / ex));
                    m.SetRow(1, new Vector4(0f, 1f / ey, 0f, -min.y / ey));
                    m.SetRow(2, new Vector4(0f, 0f, -1f / ez, max.z / ez));
                    break;
                case 3:   // -x: u along -z
                    m.SetRow(0, new Vector4(0f, 0f, -1f / ez, max.z / ez));
                    m.SetRow(1, new Vector4(0f, 1f / ey, 0f, -min.y / ey));
                    m.SetRow(2, new Vector4(1f / ex, 0f, 0f, -min.x / ex));
                    break;
                default:  // the roof, seen from above: u along +x, v along +z
                    m.SetRow(0, new Vector4(1f / ex, 0f, 0f, -min.x / ex));
                    m.SetRow(1, new Vector4(0f, 0f, 1f / ez, -min.z / ez));
                    m.SetRow(2, new Vector4(0f, -1f / ey, 0f, max.y / ey));
                    break;
            }
            m.m33 = 1f;
            return m;
        }

        /// <summary>A tile's place in the atlas (0..1: corner, size).</summary>
        public Vector4 TileRect(int slot, int face)
        {
            // As FacadeCards.cginc's CardTile: full-size slots first, then the small region's.
            int s0 = slot - 1;
            if (SmallW > 0 && s0 >= FullCapacity)
            {
                int ks = (s0 - FullCapacity) * Faces + face;
                int perRowS = Size / SmallW;
                int sx = ks % perRowS, sy = ks / perRowS;
                return new Vector4((float)(sx * SmallW) / Size, (float)(SplitRow + sy * SmallH) / Size, (float)SmallW / Size, (float)SmallH / Size);
            }
            int k = s0 * Faces + face;
            int perRow = Size / TileW;
            int tx = k % perRow, ty = k / perRow;
            return new Vector4((float)(tx * TileW) / Size, (float)(ty * TileH) / Size, (float)TileW / Size, (float)TileH / Size);
        }

        /// <summary>True when the slot is a small one.</summary>
        public bool IsSmall(int slot)
        {
            return SmallW > 0 && slot > FullCapacity;
        }

        /// <summary>
        /// One asset's card baked into the next slot: its mesh (every sub-mesh) with its maps (the
        /// game's _MainTex, _XYSMap, _ACIMap and _MainTex_ST), its box (mesh space), the glass threshold.
        /// Returns the slot (1..), 0 when the atlas is full or not made.
        /// </summary>
        public int Bake(Mesh mesh, Texture main, Texture xys, Texture aci, Vector4 mainST, Vector3 min, Vector3 max, float threshold)
        {
            if (_atlas == null || mesh == null || _slots >= Capacity)
                return 0;
            int slot = ++_slots;
            BakeInto(slot, mesh, main, xys, aci, mainST, min, max, threshold);
            return slot;
        }

        private CommandBuffer _batch;
        private int _batched;

        /// <summary>The bakes from here to <see cref="EndBatch"/> go into one buffer.</summary>
        public void BeginBatch()
        {
            if (_batch == null)
                _batch = new CommandBuffer { name = "RealGlass facade cards: bakes" };
            _batch.Clear();
            _batched = 0;
        }

        /// <summary>The batch's bakes drawn (one run, one making of the mips); the number drawn.</summary>
        public int EndBatch()
        {
            int n = _batched;
            if (_batch != null && n > 0)
                Graphics.ExecuteCommandBuffer(_batch);
            if (_batch != null)
                _batch.Clear();
            _batched = 0;
            return n;
        }

        /// <summary>One asset's card baked into a given slot (1..Capacity; the mod's schedule picks it), over whatever was there.</summary>
        public bool BakeInto(int slot, Mesh mesh, Texture main, Texture xys, Texture aci, Vector4 mainST, Vector3 min, Vector3 max, float threshold)
        {
            if (_atlas == null || mesh == null || slot < 1 || slot > Capacity)
                return false;

            // In a batch (BeginBatch .. EndBatch: the mod's frame) the bakes share one buffer, run once --
            // the atlases' mips made again once a frame, not once a bake.
            bool batched = _batch != null;
            CommandBuffer cb = batched ? _batch : new CommandBuffer { name = "RealGlass facade cards: bake" };
            cb.SetRenderTarget(Targets(), _atlas);
            for (int face = 0; face < Faces; face++)
            {
                var block = new MaterialPropertyBlock();
                block.SetMatrix(_idFrame, Frame(face, min, max));
                block.SetVector(_idTile, TileRect(slot, face));
                block.SetVector(_idBakeInfo, new Vector4(threshold, 0f, FilteredIllumination ? 1f : 0f, 0f));
                block.SetTexture(_idMainTex, main != null ? main : Texture2D.whiteTexture);
                block.SetVector(_idMainST, mainST);
                Texture xysT = xys != null ? xys : Texture2D.whiteTexture;
                Texture aciT = aci != null ? aci : Texture2D.blackTexture;
                block.SetTexture(_idXys, xysT);
                block.SetTexture(_idAci, aciT);
                block.SetVector("_XYSMap_TexelSize", new Vector4(1f / xysT.width, 1f / xysT.height, xysT.width, xysT.height));
                block.SetVector("_ACIMap_TexelSize", new Vector4(1f / aciT.width, 1f / aciT.height, aciT.width, aciT.height));
                cb.DrawMesh(_quad, Matrix4x4.identity, _bake, 0, PassClear, block);
                for (int sub = 0; sub < Mathf.Max(1, mesh.subMeshCount); sub++)
                    cb.DrawMesh(mesh, Matrix4x4.identity, _bake, sub, PassBake, block);
            }

            if (!_loggedBake)
            {
                _loggedBake = true;
                Say("facade cards: first bake -- the mesh drawn orthographic into atlas tiles with a depth test, two targets at once");
            }
            if (batched)
            {
                _batched++;
            }
            else
            {
                Graphics.ExecuteCommandBuffer(cb);
                cb.Release();
            }

            // The slot's box.
            int s0 = slot - 1;
            int row = BuildingRows + s0 / 256, col = (s0 % 256) * 3;
            int at = row * RecordWidth + col;
            Put(at, Box(min.x), Box(min.y));
            Put(at + 1, Box(min.z), Box(max.x));
            Put(at + 2, Box(max.y), Box(max.z));
            return true;
        }

        private static int Box(float v)
        {
            return Mathf.Clamp(Mathf.RoundToInt((v + 1024f) * 32f), 0, 65535);
        }

        private void Put(int index, int hi, int lo)
        {
            _recordPixels[index] = new Color32((byte)(hi >> 8), (byte)(hi & 255), (byte)(lo >> 8), (byte)(lo & 255));
        }

        /// <summary>Every building's record emptied; positions from now count from round (x, z) - 4096 m.</summary>
        public void BeginBuildings(float centreX, float centreZ)
        {
            System.Array.Clear(_recordPixels, 0, RecordWidth * BuildingRows);
            Origin = new Vector2(Mathf.Round(centreX) - 4096f, Mathf.Round(centreZ) - 4096f);
        }

        /// <summary>A building of the city map (its id) standing at position, turned by its heading (degrees about y), with the card in slot.</summary>
        public void SetBuilding(int id, Vector3 position, float headingDegrees, int slot)
        {
            if (id <= 0 || id > 65535)
                return;
            int at = (id / 256) * RecordWidth + (id % 256) * 3;
            int x = Mathf.Clamp(Mathf.RoundToInt((position.x - Origin.x) * 8f), 0, 65535);
            int z = Mathf.Clamp(Mathf.RoundToInt((position.z - Origin.y) * 8f), 0, 65535);
            int y = Mathf.Clamp(Mathf.RoundToInt((position.y + 512f) * 16f), 0, 65535);
            float turns = Mathf.Repeat(headingDegrees, 360f) / 360f;
            int heading = Mathf.RoundToInt(turns * 65536f) & 65535;
            Put(at, x, z);
            Put(at + 1, y, heading);
            Put(at + 2, Mathf.Clamp(slot, 0, 65535), 0);
        }

        public void Upload()
        {
            if (_records == null)
                return;
            _records.SetPixels32(_recordPixels);
            _records.Apply(false, false);
        }

        /// <summary>The cards to the shaders (_RGCardInfo2.w = 0 when there are none).</summary>
        public void Bind()
        {
            bool on = _atlas != null && _records != null;
            if (on)
            {
                Shader.SetGlobalTexture(_idAtlas, _atlas);
                Shader.SetGlobalTexture(_idLight, _light);
                Shader.SetGlobalTexture(_idSpec, _spec);
                Shader.SetGlobalTexture(_idRecords, _records);
            }
            Shader.SetGlobalVector(_idInfo, new Vector4(Size, TileW, TileH, Size > 0 ? Size / TileW : 1));
            Shader.SetGlobalVector(_idInfo2, new Vector4(RecordWidth, BuildingRows + SlotRows, BuildingRows, on ? 1f : 0f));
            // The highest level read: a tile's narrower side down to two texels.
            float cap = Mathf.Max(0f, Mathf.Log(Mathf.Min(TileW, TileH), 2f) - 1f);
            Shader.SetGlobalVector(_idOrigin, new Vector4(Origin.x, Origin.y, cap, 0f));
            // The small tiles (FacadeCards.cginc's CardTile): the first small slot (0-based), their size,
            // the row they start at; zeros: one kind of tile.
            Shader.SetGlobalVector("_RGCardInfo3", SmallW > 0 ? new Vector4(FullCapacity, SmallW, SmallH, SplitRow) : Vector4.zero);
        }

        /// <summary>
        /// A reader's material (GlassReflection's, CityReflection's) compiled with the cards or without:
        /// the keyword is set on the material, never globally (the mod's way; the preview's sheet uses it too).
        /// </summary>
        public static void SetReader(Material reader, bool on)
        {
            if (reader == null)
                return;
            if (on && !reader.IsKeywordEnabled(Keyword))
                reader.EnableKeyword(Keyword);
            else if (!on && reader.IsKeywordEnabled(Keyword))
                reader.DisableKeyword(Keyword);
        }

        /// <summary>
        /// The light the cards are lit with, as the game's building shader receives it: the main light
        /// (toward it; the sun, or the moon at night) on a face square to it, the ambient on an upright
        /// face and on a roof, a lit window's light.
        /// </summary>
        public static void SetLight(Vector3 toLight, Color light, Color ambientSide, Color ambientUp, Color lamp)
        {
            Ids();
            Shader.SetGlobalVector("_RGCardSunDir", new Vector4(toLight.x, toLight.y, toLight.z, 0f));
            Shader.SetGlobalVector(_idSun, new Vector4(light.r, light.g, light.b, 0f));
            Shader.SetGlobalVector(_idAmbient, new Vector4(ambientSide.r, ambientSide.g, ambientSide.b, 0f));
            Shader.SetGlobalVector(_idAmbientUp, new Vector4(ambientUp.r, ambientUp.g, ambientUp.b, 0f));
            Shader.SetGlobalVector(_idLamp, new Vector4(lamp.r, lamp.g, lamp.b, 0f));
        }

        /// <summary>
        /// The preview's checks: the refinement off (the walk's point put on the nearest face) or on (the
        /// ray met with the box); the cards' glass lit along the ray instead of as the camera sees it;
        /// debug 1: the water's trace shows where a card answered (green) and where the screen did (red).
        /// </summary>
        public static void SetMode(bool refine, bool glassAlongRay, float debug = 0f)
        {
            Shader.SetGlobalVector("_RGCardMode", new Vector4(refine ? 0f : 1f, glassAlongRay ? 1f : 0f, debug, 0f));
        }

        /// <summary>
        /// At night, the lit-window rule on the illumination value filtered to the footprint (true) or on
        /// the four bins of it (false). The preview's choice (round 2, R5).
        /// </summary>
        public static void SetNight(bool filtered)
        {
            Shader.SetGlobalVector("_RGCardNight", new Vector4(filtered ? 1f : 0f, 0f, 0f, 0f));
        }

        /// <summary>One atlas at a level into a readable texture (the preview's pictures). (Round 2b's level from the frame: <see cref="FacadeCardGain"/>.)</summary>
        public Texture2D Read(bool light, int level)
        {
            RenderTexture source = light ? _light : _atlas;
            if (source == null)
                return null;
            int size = Mathf.Max(1, Size >> level);
            var rt = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            rt.Create();
            _bake.SetVector(_idBakeInfo, new Vector4(0f, level, 0f, 0f));
            Graphics.Blit(source, rt, _bake, PassShow);
            var read = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            RenderTexture before = RenderTexture.active;
            RenderTexture.active = rt;
            read.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
            read.Apply(false);
            RenderTexture.active = before;
            rt.Release();
            Dispose(rt);
            return read;
        }

        public void Destroy()
        {
            if (_batch != null)
            {
                _batch.Release();
                _batch = null;
            }
            Free(ref _atlas);
            Free(ref _light);
            Free(ref _spec);
            if (_records != null)
                Dispose(_records);
            _records = null;
            if (_quad != null)
                Dispose(_quad);
            _quad = null;
            _slots = 0;
        }

        private static void Free(ref RenderTexture rt)
        {
            if (rt == null)
                return;
            rt.Release();
            Dispose(rt);
            rt = null;
        }

        private static void Dispose(Object value)
        {
            if (value == null)
                return;
            if (Application.isPlaying) Object.Destroy(value);
            else Object.DestroyImmediate(value);
        }
    }

    /// <summary>
    /// The cards' LEVEL from the frame (FacadeCardGain.shader; round 2b of the preview): each frame, where
    /// a building with a card is on the screen, the frame's colour against the card's own at that same
    /// point, summed in four classes (walls / glass, faces the main light reaches / not), reduced to 3 x 1
    /// and blended with last frame's in the shader; the readers multiply a card's light by frame / card of its classes
    /// (FacadeCards.cginc's CardGain). The frame is the one the reader reads: last frame's finished
    /// picture (the glass's) or a copy of the camera's target at the gain's own event (the water's).
    /// Its buffer goes on the camera BEFORE the reader's.
    /// </summary>
    public sealed class FacadeCardGain
    {
        public const int PassSample = 0, PassReduce = 1, PassGrade = 2;

        private readonly Material _mat;
        private readonly Mesh _cube;
        private RenderTexture _samples, _copy, _dumpA, _dumpB;
        private readonly RenderTexture[] _sums = new RenderTexture[2];
        private int _newest;
        private bool _fresh = true;
        private int _w, _h;

        /// <summary>The commands, rebuilt by <see cref="Frame"/> every frame.</summary>
        public readonly CommandBuffer Buffer = new CommandBuffer { name = "RealGlass facade cards: the level from the frame" };

        /// <summary>Sample points a side (the target is three times as wide: the walls' sums | the glass's | the weights).</summary>
        public int SamplesX = 96, SamplesY = 54;

        /// <summary>The share of last frame's sums kept (0: each frame alone).</summary>
        public float Keep = 0.9f;

        /// <summary>The caller sets the global _RGGainInvVP itself as the camera renders (the mod); false: recorded with the commands (the previews).</summary>
        public bool MatrixAtRender;

        /// <summary>The camera's inverse in the convention the gain's pass reads (for a caller setting it at render time).</summary>
        public static Matrix4x4 InverseOf(Camera camera)
        {
            return (camera.projectionMatrix * camera.worldToCameraMatrix).inverse;
        }

        /// <summary>How far from its box's face a point may lie and count (m).</summary>
        public float Tolerance = 2f;

        /// <summary>The preview's dumps: two targets the screen's size, per pixel (frame, card, lit weight, glass) and (id, face, counted, N.L).</summary>
        public bool Dump;

        public RenderTexture DumpA
        {
            get { return _dumpA; }
        }

        public RenderTexture DumpB
        {
            get { return _dumpB; }
        }

        public Material Material
        {
            get { return _mat; }
        }

        public FacadeCardGain(Shader gain)
        {
            _mat = new Material(gain) { name = "RealGlass facade card gain", hideFlags = HideFlags.HideAndDontSave };
            _cube = Cube();
        }

        /// <summary>The next frame starts afresh (no last frame's sums): a new view, a new setting.</summary>
        public void Reset()
        {
            _fresh = true;
        }

        private static RenderTexture Float(string name, int w, int h)
        {
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            return rt;
        }

        /// <summary>
        /// This frame's commands, for a screen of width x height: ownCopy -- the camera's target copied
        /// first and read as the frame (the water's), else last frame's finished picture (the glass's).
        /// </summary>
        public void Frame(Camera camera, int width, int height, bool ownCopy)
        {
            if (_samples == null || _samples.width != SamplesX * 3 || _samples.height != SamplesY)
            {
                Free(ref _samples);
                _samples = Float("RealGlass card gain samples", SamplesX * 3, SamplesY);
            }
            for (int k = 0; k < 2; k++)
            {
                if (_sums[k] == null)
                    _sums[k] = Float("RealGlass card gain " + k, 3, 1);
            }
            if (Dump && (_dumpA == null || _w != width || _h != height))
            {
                Free(ref _dumpA);
                Free(ref _dumpB);
                _dumpA = Float("RealGlass card gain dump A", width, height);
                _dumpB = Float("RealGlass card gain dump B", width, height);
            }
            if (ownCopy && (_copy == null || _w != width || _h != height))
            {
                Free(ref _copy);
                _copy = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                {
                    name = "RealGlass card gain frame",
                    hideFlags = HideFlags.HideAndDontSave,
                    useMipMap = true,
                    autoGenerateMips = true,
                    filterMode = FilterMode.Trilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                _copy.Create();
            }
            _w = width;
            _h = height;

            int prev = _newest, cur = 1 - _newest;
            _newest = cur;
            float keep = _fresh ? 0f : Mathf.Clamp01(Keep);
            _fresh = false;

            Buffer.Clear();
            Commands(Buffer, camera, width, height, ownCopy, keep, prev, cur);
        }

        /// <summary>
        /// The mod's way: this frame's measurement appended to a buffer the caller runs at AfterFinalPass
        /// (GlassPrePass.BeforeReflection: just before the reflection pass reads _RGCardGain), the frame
        /// last frame's finished picture. The target ends on the camera's.
        /// </summary>
        public void Record(CommandBuffer cb, Camera camera, int width, int height)
        {
            Frame(camera, width, height, false);
            Buffer.Clear();
            int cur = _newest, prev = 1 - _newest;
            Commands(cb, camera, width, height, false, _keepNow, prev, cur);
        }

        private float _keepNow;

        private void Commands(CommandBuffer cb, Camera camera, int width, int height, bool ownCopy, float keep, int prev, int cur)
        {
            _keepNow = keep;
            var info = new Vector4(0f, ownCopy ? 0f : 1f, 0f, 0f);
            cb.SetGlobalVector("_RGGainInfo2", new Vector4(1f / Mathf.Max(width, 1), 1f / Mathf.Max(height, 1), keep, Tolerance));
            cb.SetGlobalVector("_RGGainSize", new Vector4(SamplesX * 3, SamplesY, 0f, 0f));
            // The camera inverted, in the convention the copies are read with (ComputeScreenPos undoes
            // Unity's render-texture flip: the plain projection, as GlassScreen's _RGPrevVP). Screen uv
            // here is the camera's own viewport's (the game's camera with its menu bar has rect
            // 0, 0.105, 1, 0.895): the depth texture, the copies and this projection all are.
            // The mod sets it as the camera renders (Camera.onPreRender, MatrixAtRender): a buffer recorded
            // in LateUpdate would carry the camera of before any later move.
            if (!MatrixAtRender)
                cb.SetGlobalMatrix("_RGGainInvVP", (camera.projectionMatrix * camera.worldToCameraMatrix).inverse);
            if (ownCopy)
            {
                cb.Blit(BuiltinRenderTextureType.CameraTarget, _copy);
                cb.SetGlobalTexture("_RGGainFrame", _copy);
            }
            cb.SetGlobalVector("_RGGainInfo", info);
            cb.SetRenderTarget(_samples);
            cb.ClearRenderTarget(false, true, Color.clear);
            cb.DrawMesh(_cube, Matrix4x4.identity, _mat, 0, PassSample);
            if (Dump)
            {
                cb.SetGlobalVector("_RGGainInfo", new Vector4(info.x, info.y, info.z, 1f));
                cb.SetRenderTarget(_dumpA);
                cb.ClearRenderTarget(false, true, Color.clear);
                cb.DrawMesh(_cube, Matrix4x4.identity, _mat, 0, PassSample);
                cb.SetGlobalVector("_RGGainInfo", new Vector4(info.x, info.y, info.z, 2f));
                cb.SetRenderTarget(_dumpB);
                cb.ClearRenderTarget(false, true, Color.clear);
                cb.DrawMesh(_cube, Matrix4x4.identity, _mat, 0, PassSample);
                cb.SetGlobalVector("_RGGainInfo", info);
            }
            cb.SetGlobalTexture("_RGGainPrev", _sums[prev]);
            cb.Blit(_samples, _sums[cur], _mat, PassReduce);
            cb.SetGlobalTexture("_RGCardGain", _sums[cur]);
            cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        }

        /// <summary>The readers' switch and limits: on, the least weight a class needs, the gain's range.</summary>
        public static void Apply(bool on, float minWeight, float lowest, float highest)
        {
            Shader.SetGlobalVector("_RGCardGainInfo", new Vector4(on ? 1f : 0f, minWeight, lowest, highest));
        }

        /// <summary>The newest sums read back (the preview's log): [0] the walls' (lit frame, lit card, shaded frame, shaded card), [1] the glass's, [2] the weights.</summary>
        public Color[] Read()
        {
            return ReadFloat(_sums[_newest]);
        }

        /// <summary>
        /// The sums as a log line: the shared level (all the frame's light over all the cards'), then each
        /// class's gain as the readers use it (CardGain: the level x its own share drawn toward 1 by
        /// weight / (weight + trust)) and its weight w -- the class's sample points a frame (each point
        /// counts 0..1 by its lit / shaded and wall / glass shares; held and blended over frames), out of
        /// SamplesX x SamplesY.
        /// </summary>
        public static string Describe(Color[] g, float trust = 20f, float lowest = 0.25f, float highest = 4f)
        {
            if (g == null || g.Length < 3)
                return "none";
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            float frame = g[0].r + g[0].b + g[1].r + g[1].b, card = g[0].g + g[0].a + g[1].g + g[1].a;
            float level = card > 1e-5f && frame > 0f ? frame / card : 1f;
            System.Func<float, float, float, string> r = (f, c, w) =>
            {
                float own = c > 1e-5f && f > 0f ? f / c / Mathf.Max(level, 1e-4f) : 1f;
                float share = 1f + (own - 1f) * w / (w + Mathf.Max(trust, 1f));
                return Mathf.Clamp(level * share, lowest, highest).ToString("F2", inv) + " (w " + w.ToString("F0", inv) + ")";
            };
            return "level " + level.ToString("F2", inv) + ": walls lit " + r(g[0].r, g[0].g, g[2].r) + " shaded " + r(g[0].b, g[0].a, g[2].g) +
                   ", glass lit " + r(g[1].r, g[1].g, g[2].b) + " shaded " + r(g[1].b, g[1].a, g[2].a);
        }

        /// <summary>A float target read back (the preview's log and dumps).</summary>
        public static Color[] ReadFloat(RenderTexture rt)
        {
            if (rt == null)
                return null;
            var t = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false, true);
            RenderTexture before = RenderTexture.active;
            RenderTexture.active = rt;
            t.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            t.Apply(false);
            RenderTexture.active = before;
            Color[] px = t.GetPixels();
            Dispose(t);
            return px;
        }

        private static Mesh Cube()
        {
            var mesh = new Mesh { name = "RealGlass card gain cube", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = new[]
            {
                new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(1, 1, -1), new Vector3(-1, 1, -1),
                new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(1, 1, 1), new Vector3(-1, 1, 1),
            };
            mesh.triangles = new[]
            {
                0, 2, 1, 0, 3, 2,
                4, 5, 6, 4, 6, 7,
                0, 1, 5, 0, 5, 4,
                3, 7, 6, 3, 6, 2,
                0, 4, 7, 0, 7, 3,
                1, 2, 6, 1, 6, 5,
            };
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            return mesh;
        }

        public void Destroy()
        {
            Free(ref _samples);
            Free(ref _copy);
            Free(ref _dumpA);
            Free(ref _dumpB);
            for (int k = 0; k < 2; k++)
                Free(ref _sums[k]);
            Buffer.Clear();
        }

        private static void Free(ref RenderTexture rt)
        {
            if (rt == null)
                return;
            rt.Release();
            Dispose(rt);
            rt = null;
        }

        private static void Dispose(Object value)
        {
            if (value == null)
                return;
            if (Application.isPlaying) Object.Destroy(value);
            else Object.DestroyImmediate(value);
        }
    }
}
