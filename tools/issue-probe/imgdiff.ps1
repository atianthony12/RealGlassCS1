# Two pictures of the same size compared: pixels over a threshold (largest channel), how many are
# bluer in B, their bounding box, and (with -Mask, a third picture) how many fall where that picture is
# not black, or (-BandFrom, a picture of check view 15) within 2 px of a glass pixel's edge. -Crop x,y,w,h,scale writes A | B | |A-B| x4 of that region, scaled, to -CropOut.
param([string]$A, [string]$B, [int]$Threshold = 8, [string]$Mask = '', [string]$Crop = '', [string]$CropOut = '', [string]$BandFrom = '')
$ErrorActionPreference = 'Stop'
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
public static class IssueDiff
{
    public static int[] Read(Bitmap b)
    {
        var r = new Rectangle(0, 0, b.Width, b.Height);
        BitmapData d = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new int[b.Width * b.Height];
        System.Runtime.InteropServices.Marshal.Copy(d.Scan0, px, 0, px.Length);
        b.UnlockBits(d);
        return px;
    }
    public static string Compare(Bitmap a, Bitmap b, Bitmap mask, int threshold)
    {
        if (a.Width != b.Width || a.Height != b.Height) return "sizes differ";
        int[] pa = Read(a), pb = Read(b), pm = mask != null ? Read(mask) : null;
        int over = 0, bluer = 0, inMask = 0, x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, w = a.Width;
        long sum = 0;
        for (int i = 0; i < pa.Length; i++)
        {
            int ra = (pa[i] >> 16) & 255, ga = (pa[i] >> 8) & 255, ba = pa[i] & 255;
            int rb = (pb[i] >> 16) & 255, gb = (pb[i] >> 8) & 255, bb = pb[i] & 255;
            int d = Math.Max(Math.Abs(ra - rb), Math.Max(Math.Abs(ga - gb), Math.Abs(ba - bb)));
            if (d <= threshold) continue;
            over++; sum += d;
            if (bb - ba > 8 && bb - ba > rb - ra) bluer++;
            if (pm != null && (pm[i] & 0xFFFFFF) != 0) inMask++;
            int x = i % w, y = i / w;
            x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
        }
        return over + " px over " + threshold + "/255 (mean " + (over > 0 ? (sum / over).ToString() : "0") + "), " + bluer + " bluer in B" +
               (pm != null ? ", " + inMask + " in the mask" : "") + (over > 0 ? ", box x " + x0 + ".." + x1 + " y " + y0 + ".." + y1 : "");
    }
    // Check view 15: glass pixels are green, yellow or red; the band is every pixel within 2 px of a
    // change between glass and not.
    public static Bitmap Band(Bitmap v)
    {
        int w = v.Width, h = v.Height;
        int[] p = Read(v);
        var glass = new bool[p.Length];
        for (int i = 0; i < p.Length; i++)
        {
            int r = (p[i] >> 16) & 255, g = (p[i] >> 8) & 255, b = p[i] & 255;
            // As saved (tonemapped): green (49, 133, 69), yellow and red alike.
            glass[i] = (g > 100 && g > 1.5 * r && g > 1.5 * b) || (r > 100 && g > 100 && b < 0.6 * Math.Min(r, g)) || (r > 100 && r > 1.5 * g && r > 1.5 * b);
        }
        var o = new Bitmap(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool edge = false;
                for (int dy = -2; dy <= 2 && !edge; dy++)
                    for (int dx = -2; dx <= 2 && !edge; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx >= 0 && yy >= 0 && xx < w && yy < h && glass[yy * w + xx] != glass[y * w + x]) edge = true;
                    }
                o.SetPixel(x, y, edge ? Color.White : Color.Black);
            }
        return o;
    }
    public static void Crop(Bitmap a, Bitmap b, int x, int y, int cw, int ch, int s, string path)
    {
        var o = new Bitmap(cw * s * 3, ch * s);
        for (int j = 0; j < ch; j++)
            for (int i = 0; i < cw; i++)
            {
                Color ca = a.GetPixel(x + i, y + j), cb = b.GetPixel(x + i, y + j);
                Color cd = Color.FromArgb(Math.Min(255, 4 * Math.Abs(ca.R - cb.R)), Math.Min(255, 4 * Math.Abs(ca.G - cb.G)), Math.Min(255, 4 * Math.Abs(ca.B - cb.B)));
                for (int v = 0; v < s; v++)
                    for (int u = 0; u < s; u++)
                    {
                        o.SetPixel(i * s + u, j * s + v, ca);
                        o.SetPixel(cw * s + i * s + u, j * s + v, cb);
                        o.SetPixel(2 * cw * s + i * s + u, j * s + v, cd);
                    }
            }
        o.Save(path, ImageFormat.Png);
    }
}
'@
$ba = [Drawing.Bitmap]::FromFile((Resolve-Path $A))
$bb = [Drawing.Bitmap]::FromFile((Resolve-Path $B))
$bm = if ($Mask -ne '') { [Drawing.Bitmap]::FromFile((Resolve-Path $Mask)) } elseif ($BandFrom -ne '') { [IssueDiff]::Band([Drawing.Bitmap]::FromFile((Resolve-Path $BandFrom))) } else { $null }
[IssueDiff]::Compare($ba, $bb, $bm, $Threshold)
if ($Crop -ne '') {
    $c = $Crop.Split(',') | ForEach-Object { [int]$_ }
    [IssueDiff]::Crop($ba, $bb, $c[0], $c[1], $c[2], $c[3], $c[4], $CropOut)
}
