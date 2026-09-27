# 抓取「性能监测」窗口截图（按进程句柄，避免标题匹配的编码问题）
$ErrorActionPreference = 'Stop'
$Out = "D:\DS\perf-monitor\shot.png"
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Cap2 {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$proc = Get-Process dotnet -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { "找不到带窗口的 dotnet 进程"; exit 1 }
$h = $proc.MainWindowHandle
"PID={0}  句柄={1}  标题={2}" -f $proc.Id, $h, $proc.MainWindowTitle

[void][Cap2]::ShowWindow($h, 5)          # SW_SHOW
[void][Cap2]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 800

$r = New-Object Cap2+RECT
[void][Cap2]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
"窗口矩形 = {0},{1}  {2}x{3}" -f $r.Left, $r.Top, $w, $ht
if ($w -le 0 -or $ht -le 0) { "窗口尺寸异常"; exit 1 }

$bmp = New-Object System.Drawing.Bitmap($w, $ht)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [Cap2]::PrintWindow($h, $hdc, 2)
$g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"PrintWindow={0}  已保存 {1} ({2} 字节)" -f $ok, $Out, (Get-Item $Out).Length
