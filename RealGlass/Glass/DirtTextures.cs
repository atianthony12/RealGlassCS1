using System;
using UnityEngine;

namespace RealGlass.Glass
{
    /// <summary>
    /// The dirt on the glass's fine grain (Dirt.cginc): two textures baked from ambientCG's CC0 maps by
    /// tools/dirt/build-dirt.ps1 (Editor/DirtBaker.cs) and shipped as raw data with their mips --
    /// GRIME, 1024 x 1024 DXT5: a the film of dirt (Surface Imperfections 003), g the water spots
    /// (Surface Imperfections 001), r and b empty (the normal-map layout: g and a each get a block of
    /// their own); RUNS, 1024 x 512 Alpha8, uncompressed: rain's runs down from a pane's top edge
    /// (Leaking 004), v = 1 the top. Both linear (they are amounts, not colours) and repeating. Loaded
    /// with Texture2D.LoadRawTextureData as the room atlases are. Engine types only, C# 4: the GPU
    /// preview runs this same file (tools/preview/sync-sim.ps1).
    /// </summary>
    /// <remarks>
    /// A file: "RGDT", version 1, width, height, mips, Unity's TextureFormat, two means (floats: the
    /// channels' averages, 0..1 -- grime: film, spots; runs: runs, 0), then the data's length and the
    /// data (little-endian).
    /// </remarks>
    public sealed class DirtTextures
    {
        private const int Version = 1;

        public Texture2D Grime { get; private set; }
        public Texture2D Runs { get; private set; }

        /// <summary>The maps' averages: x the film, y the spots, z the runs -- what a pane far away shows.</summary>
        public Vector4 Means { get; private set; }

        /// <summary>Why the last Load failed; "" when it did not.</summary>
        public string Problem { get; private set; }

        public bool Loaded
        {
            get { return Grime != null && Runs != null; }
        }

        public DirtTextures()
        {
            Problem = "";
        }

        /// <summary>Both textures from their files' bytes; false (and <see cref="Problem"/>) when either is wrong.</summary>
        public bool Load(byte[] grime, byte[] runs)
        {
            Destroy();
            try
            {
                float filmMean, spotsMean, runsMean, unused;
                Texture2D g = Read(grime, "dirt", TextureFormat.DXT5, out filmMean, out spotsMean);
                if (g == null)
                    return false;
                Texture2D r = Read(runs, "rain's runs", TextureFormat.Alpha8, out runsMean, out unused);
                if (r == null)
                {
                    UnityEngine.Object.DestroyImmediate(g);
                    return false;
                }
                Grime = g;
                Runs = r;
                Means = new Vector4(filmMean, spotsMean, runsMean, 0f);
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

        private Texture2D Read(byte[] bytes, string what, TextureFormat expected, out float mean0, out float mean1)
        {
            mean0 = mean1 = 0f;
            if (bytes == null || bytes.Length < 40)
            {
                Problem = what + ": no data";
                return null;
            }
            if (bytes[0] != 'R' || bytes[1] != 'G' || bytes[2] != 'D' || bytes[3] != 'T')
            {
                Problem = what + ": not a dirt texture";
                return null;
            }

            int at = 4;
            int version = Int(bytes, ref at);
            int width = Int(bytes, ref at), height = Int(bytes, ref at), mips = Int(bytes, ref at), format = Int(bytes, ref at);
            mean0 = Float(bytes, ref at);
            mean1 = Float(bytes, ref at);
            int length = Int(bytes, ref at);
            if (version != Version || format != (int)expected || width < 4 || height < 4 || width > 4096 || height > 4096 || mips < 1 ||
                !(mean0 > 0.001f && mean0 < 1f) || !(mean1 >= 0f && mean1 < 1f))
            {
                Problem = what + ": version " + version + ", " + width + "x" + height + ", format " + format + ", means " + mean0 + " / " + mean1 + ": not ours";
                return null;
            }
            if (length <= 0 || at + length > bytes.Length)
            {
                Problem = what + ": " + length + " bytes of texture, " + (bytes.Length - at) + " in the file";
                return null;
            }

            var data = new byte[length];
            Buffer.BlockCopy(bytes, at, data, 0, length);

            // Alpha8 where the GPU takes it (LodGlass's masks do the same); else the same values in RGBA32's a.
            TextureFormat made = expected;
            if (expected == TextureFormat.Alpha8 && !SystemInfo.SupportsTextureFormat(TextureFormat.Alpha8))
            {
                made = TextureFormat.RGBA32;
                var wide = new byte[length * 4];
                for (int i = 0; i < length; i++)
                {
                    wide[4 * i] = wide[4 * i + 1] = wide[4 * i + 2] = 255;
                    wide[4 * i + 3] = data[i];
                }
                data = wide;
            }

            // Linear (true): the maps hold amounts, never colours.
            var t = new Texture2D(width, height, made, mips > 1, true);
            if (t.mipmapCount != mips)
            {
                Problem = what + ": " + mips + " mips in the file, " + t.mipmapCount + " in the texture";
                UnityEngine.Object.DestroyImmediate(t);
                return null;
            }

            t.name = "RealGlass " + what;
            t.hideFlags = HideFlags.HideAndDontSave;
            t.wrapMode = TextureWrapMode.Repeat;
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

        /// <summary>The textures and their averages, for the shader (Dirt.cginc).</summary>
        public void Bind()
        {
            if (!Loaded)
                return;

            Shader.SetGlobalTexture("_RGDirtGrime", Grime);
            Shader.SetGlobalTexture("_RGDirtRuns", Runs);
            Shader.SetGlobalVector("_RGDirtMeans", Means);
        }

        public void Destroy()
        {
            // Immediate, as the other textures': the preview runs in the editor, where Destroy is refused.
            if (Grime != null)
                UnityEngine.Object.DestroyImmediate(Grime);
            if (Runs != null)
                UnityEngine.Object.DestroyImmediate(Runs);
            Grime = Runs = null;
        }
    }
}
