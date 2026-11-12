using UnityEngine;

namespace RealGlass.Water
{
    /// <summary>
    /// The painted shores (<see cref="ShorePaint"/>) on the GPU, for the foam: an Alpha8 map on the shore
    /// map's own grid (2161 x 2161, 8 m samples from -8640 m, 4.7 MB), 128 + the paint. Made only once
    /// something is painted (until then the shader's mapping w = 0: no paint read); the changed samples
    /// copied in at most <see cref="Rate"/> times a second while a stroke goes on.
    /// </summary>
    internal sealed class ShorePaintTexture
    {
        private const int Side = 2161;
        private const int Centre = 1080;
        private const float Origin = -8640f;
        private const float Rate = 15f;

        private Texture2D _texture;
        private byte[] _map;
        private int _generation = -1;
        private float _sentAt = float.NegativeInfinity;

        public Texture Texture
        {
            get { return _texture; }
        }

        /// <summary>The shader's _RGShorePaintMapping: uv = xz x x + y, w 1 while a map is up.</summary>
        public Vector4 Mapping
        {
            get
            {
                if (_texture == null)
                    return Vector4.zero;
                float spacing = ShorePaintMask.Cell;
                return new Vector4(1f / (spacing * Side), (-Origin / spacing + 0.5f) / Side, 0f, 1f);
            }
        }

        /// <summary>Each frame the foam shows: the paint's changes onto the GPU.</summary>
        public void Update()
        {
            ShorePaintMask mask = ShorePaint.Mask;
            if (_generation != ShorePaint.Generation)
            {
                _generation = ShorePaint.Generation;
                mask.MarkAllDirty();
                if (_map != null)
                {
                    for (int i = 0; i < _map.Length; i++)
                        _map[i] = 128;
                }
            }

            if (_texture == null && !mask.Any)
                return;

            float now = Time.realtimeSinceStartup;
            if (_texture != null && now - _sentAt < 1f / Rate)
                return;

            int x0, z0, x1, z1;
            if (!mask.TakeDirty(out x0, out z0, out x1, out z1) && _texture != null)
                return;

            if (_texture == null)
            {
                _map = new byte[Side * Side];
                for (int i = 0; i < _map.Length; i++)
                    _map[i] = 128;
                _texture = new Texture2D(Side, Side, TextureFormat.Alpha8, false, true)
                {
                    name = "RealGlassShorePaint",
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                x0 = z0 = -Centre;
                x1 = z1 = Side - 1 - Centre;
                Log.Msg("water: painted shores on the map (" + mask.TileCount + " tiles of 64 m)");
            }

            // Under the paint's lock: a save drops emptied tiles on its own thread (ShorePaint.Encode).
            lock (ShorePaint.Sync)
                mask.Bake(_map, Side, Centre, x0, z0, x1, z1);
            _texture.LoadRawTextureData(_map);
            _texture.Apply(false);
            _sentAt = now;
        }

        public void Destroy()
        {
            if (_texture != null)
                Object.Destroy(_texture);
            _texture = null;
            _map = null;
            _generation = -1;
        }
    }
}
