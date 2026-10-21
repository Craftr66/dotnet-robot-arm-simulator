# Rasterize the existing AxisLab lettermark into Windows icon sizes; no external artwork.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$iconDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Desktop\Assets'
New-Item -ItemType Directory -Path $iconDirectory -Force | Out-Null
$frames = @()
foreach ($size in @(16, 32, 48, 256)) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $radius = [single]($size * 0.2)
    $diameter = $radius * 2
    $path.AddArc(0, 0, $diameter, $diameter, 180, 90)
    $path.AddArc($size - $diameter - 1, 0, $diameter, $diameter, 270, 90)
    $path.AddArc($size - $diameter - 1, $size - $diameter - 1, $diameter, $diameter, 0, 90)
    $path.AddArc(0, $size - $diameter - 1, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    $background = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(100, 221, 190))
    $foreground = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(18, 43, 41))
    $graphics.FillPath($background, $path)
    $font = New-Object System.Drawing.Font('Segoe UI', ([single]($size * 0.65)), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rectangle = New-Object System.Drawing.RectangleF(0, ([single](-$size * 0.015)), $size, $size)
    $graphics.DrawString('A', $font, $foreground, $rectangle, $format)
    $memory = New-Object System.IO.MemoryStream
    $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += ,@{ Size = $size; Bytes = $memory.ToArray() }
    $memory.Dispose(); $format.Dispose(); $font.Dispose(); $background.Dispose(); $foreground.Dispose()
    $path.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$stream = [System.IO.File]::Create((Join-Path $iconDirectory 'AxisLab.ico'))
$writer = New-Object System.IO.BinaryWriter($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $stream.Dispose() }
Write-Host 'Created src/Desktop/Assets/AxisLab.ico'
