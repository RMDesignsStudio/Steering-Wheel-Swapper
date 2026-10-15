# Renders the RM Designs icon (from src/WheelSwap.App/Brand.xaml) on a purple square into src/WheelSwap.App/app.ico
# (16-256 px, PNG-compressed entries). Run: powershell -ExecutionPolicy Bypass -File design\make_icon.ps1
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$root = Split-Path $PSScriptRoot
$dict = [System.Windows.Markup.XamlReader]::Parse((Get-Content (Join-Path $root "src\WheelSwap.App\Brand.xaml") -Raw))
$logo = $dict["RmIcon"].Drawing
$purple = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0x43, 0x00, 0xAD))
$pngs = @()
foreach ($size in 16, 24, 32, 48, 64, 128, 256) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $dc.DrawRectangle($purple, $null, (New-Object System.Windows.Rect 0, 0, $size, $size))
    $pad = [Math]::Max(1.5, $size * 0.12)
    $b = $logo.Bounds
    $scale = ($size - 2 * $pad) / $b.Width
    $h = $b.Height * $scale
    $dc.PushTransform((New-Object System.Windows.Media.TranslateTransform $pad, (($size - $h) / 2)))
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform $scale, $scale))
    $dc.PushTransform((New-Object System.Windows.Media.TranslateTransform (-$b.X), (-$b.Y)))
    $dc.DrawDrawing($logo)
    $dc.Pop(); $dc.Pop(); $dc.Pop(); $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream; $enc.Save($ms)
    $pngs += , @($size, $ms.ToArray())
    if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $PSScriptRoot "app_icon_256.png"), $ms.ToArray()) }
}
# ICO container: header, directory, PNG images
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $s = $p[0]; $d = $p[1]
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$d.Length); $w.Write([UInt32]$offset); $offset += $d.Length
}
foreach ($p in $pngs) { $w.Write($p[1]) }
$w.Flush()
$ico = Join-Path $root "src\WheelSwap.App\app.ico"
[IO.File]::WriteAllBytes($ico, $out.ToArray())
"wrote $ico ($($out.Length) bytes)"
