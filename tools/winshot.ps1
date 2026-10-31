param([int]$ProcId, [string]$Out, [double]$Scale = 0.5)
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public class W { [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
[DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
public struct RECT { public int L,T,R,B; } }
'@
$h = (Get-Process -Id $ProcId).MainWindowHandle
$r = New-Object W+RECT; [W]::GetClientRect($h,[ref]$r) | Out-Null
$bmp = New-Object System.Drawing.Bitmap $r.R, $r.B
$g=[System.Drawing.Graphics]::FromImage($bmp); $dc=$g.GetHdc(); [W]::PrintWindow($h,$dc,3) | Out-Null; $g.ReleaseHdc($dc)
$s=New-Object System.Drawing.Bitmap $bmp, ([int]($r.R*$Scale)), ([int]($r.B*$Scale)); $s.Save($Out)
"$($r.R)x$($r.B)"
