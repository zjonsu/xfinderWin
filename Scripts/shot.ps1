Add-Type @'
using System;
using System.Runtime.InteropServices;
public class WS4 {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    public struct R { public int L, T, Rt, B; }
}
'@
[WS4]::SetProcessDpiAwarenessContext([IntPtr]::op_Explicit(-4)) | Out-Null
Add-Type -AssemblyName System.Drawing
$p = (Get-Process XFinder | Where-Object { $_.MainWindowHandle -ne 0 })[0]
$r = New-Object WS4+R
[WS4]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.Rt - $r.L; $h = $r.B - $r.T
$bmp = New-Object Drawing.Bitmap($w, $h)
$g = [Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
# PW_RENDERFULLCONTENT(2) — DWM 콘텐츠 포함, 가려져 있어도 창이 직접 그림
$ok = [WS4]::PrintWindow($p.MainWindowHandle, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
$bmp.Save('D:\project\xFinder\build-shot.png', [Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"printwindow=$ok ${w}x${h}"
