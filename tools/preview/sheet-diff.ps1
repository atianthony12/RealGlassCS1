# Compares two contact sheets cell by cell: pixels differing by more than -Over codes, the mean and
# largest difference. -Width / -Height: a shot's size; the sheet's label strip is 22 px, its side 70 px.
param([string]$A, [string]$B, [int]$Width = 960, [int]$Height = 540, [int]$Over = 2,
      # Compare column -ColA of sheet A with column -ColB of sheet B (-1: every column with itself).
      [int]$ColA = -1, [int]$ColB = -1)
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class SheetDiff {
    public static byte[] Bytes(Bitmap b, out int stride) {
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        stride = d.Stride; var a = new byte[d.Stride * b.Height]; Marshal.Copy(d.Scan0, a, 0, a.Length); b.UnlockBits(d); return a;
    }
    public static string Cell(byte[] a, int sa, byte[] b, int sb, int xa, int ya, int xb, int yb, int w, int h, int over) {
        long sum = 0; int most = 0, count = 0;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
            int pa = (ya + y) * sa + (xa + x) * 3, pb = (yb + y) * sb + (xb + x) * 3; int m = 0;
            for (int c = 0; c < 3; c++) { int d = Math.Abs(a[pa + c] - b[pb + c]); sum += d; if (d > m) m = d; }
            if (m > over) count++; if (m > most) most = m;
        }
        return string.Format("{0} px over {1} codes ({2:P2}), mean {3:F3}, most {4}", count, over, count / (double)(w * h), sum / (3.0 * w * h), most);
    }
}
"@ -ReferencedAssemblies System.Drawing
$ba = New-Object System.Drawing.Bitmap $A; $bb = New-Object System.Drawing.Bitmap $B
$sa = 0; $sb = 0
$pa = [SheetDiff]::Bytes($ba, [ref]$sa); $pb = [SheetDiff]::Bytes($bb, [ref]$sb)
$rows = [int](($ba.Height - 22) / $Height); $cols = [int](($ba.Width - 70) / $Width)
for ($r = 0; $r -lt $rows; $r++) {
    if ($ColA -ge 0) {
        Write-Host ("row {0}: A col {1} vs B col {2}: {3}" -f $r, $ColA, $ColB, [SheetDiff]::Cell($pa, $sa, $pb, $sb, 70 + $ColA * $Width, 22 + $r * $Height, 70 + $ColB * $Width, 22 + $r * $Height, $Width, $Height, $Over))
    } else {
        for ($c = 0; $c -lt $cols; $c++) {
            Write-Host ("row {0} col {1}: {2}" -f $r, $c, [SheetDiff]::Cell($pa, $sa, $pb, $sb, 70 + $c * $Width, 22 + $r * $Height, 70 + $c * $Width, 22 + $r * $Height, $Width, $Height, $Over))
        }
    }
}
$ba.Dispose(); $bb.Dispose()
