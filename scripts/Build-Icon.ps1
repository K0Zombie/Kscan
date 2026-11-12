$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$source = [Drawing.Image]::FromFile((Join-Path $root 'docs/icon-concepts/kscan-icon-user-selected.png'))
$master = New-Object Drawing.Bitmap 1280,1280
$graphics = [Drawing.Graphics]::FromImage($master)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$plate = New-Object Drawing.Drawing2D.GraphicsPath
$plate.AddArc(20,20,280,280,180,90)
$plate.AddArc(980,20,280,280,270,90)
$plate.AddArc(980,980,280,280,0,90)
$plate.AddArc(20,980,280,280,90,90)
$plate.CloseFigure()
$fill = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#132B43'))
$edge = New-Object Drawing.Pen ([Drawing.ColorTranslator]::FromHtml('#EAF4FF')),24
$graphics.FillPath($fill,$plate)
$graphics.DrawPath($edge,$plate)
$graphics.DrawImage($source,0,0,1280,1280)
$graphics.Dispose(); $fill.Dispose(); $edge.Dispose(); $plate.Dispose(); $source.Dispose()
$master.Save((Join-Path $root 'GUI/Assets/kscan.png'),[Drawing.Imaging.ImageFormat]::Png)
$sizes = @(16,24,32,48,64,128,256)
$images = @()
foreach ($size in $sizes) {
    $bitmap = New-Object Drawing.Bitmap $size,$size
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($master,0,0,$size,$size)
    $g.Dispose()
    $memory = New-Object IO.MemoryStream
    $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
    $images += ,$memory.ToArray()
    if ($size -eq 256) { $bitmap.Save((Join-Path $root 'packaging/arch/gui-extra/usr/share/icons/hicolor/256x256/apps/kscan.png'),[Drawing.Imaging.ImageFormat]::Png) }
    $bitmap.Dispose(); $memory.Dispose()
}
$stream = [IO.File]::Create((Join-Path $root 'GUI/Assets/kscan.ico'))
$writer = New-Object IO.BinaryWriter $stream
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i=0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
$writer.Dispose(); $stream.Dispose()

# Review small icons against contrasting backgrounds; use the actual ICO sizes.
$preview = New-Object Drawing.Bitmap 720,180
$g = [Drawing.Graphics]::FromImage($preview)
$backgrounds = @('#FFFFFF','#131313','#0078D4','#D9D9D9')
for ($i=0; $i -lt $backgrounds.Count; $i++) {
    $brush = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml($backgrounds[$i]))
    $g.FillRectangle($brush,$i*180,0,180,180); $brush.Dispose()
    $g.DrawImage($master,$i*180+24,24,64,64)
    $g.DrawImage($master,$i*180+24,112,16,16)
    $g.DrawImage($master,$i*180+58,108,24,24)
    $g.DrawImage($master,$i*180+100,104,32,32)
}
$g.Dispose()
$preview.Save((Join-Path $root 'docs/icon-concepts/contrast-preview.png'),[Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose(); $master.Dispose()
