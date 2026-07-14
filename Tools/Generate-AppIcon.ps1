param(
    [string]$OutputPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'Assets\AppIcon.ico')
)

Add-Type -AssemblyName System.Drawing.Common

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = @()

foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $inset = [Math]::Max(1, [Math]::Round($size * 0.03))
    $edge = $size - ($inset * 2)
    $radius = [Math]::Max(3, [Math]::Round($size * 0.25))
    $diameter = $radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($inset, $inset, $diameter, $diameter, 180, 90)
    $path.AddArc($inset + $edge - $diameter, $inset, $diameter, $diameter, 270, 90)
    $path.AddArc($inset + $edge - $diameter, $inset + $edge - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($inset, $inset + $edge - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()

    $start = [System.Drawing.PointF]::new(0, 0)
    $finish = [System.Drawing.PointF]::new($size, $size)
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $start,
        $finish,
        [System.Drawing.ColorTranslator]::FromHtml('#2484F2'),
        [System.Drawing.ColorTranslator]::FromHtml('#0D63D5'))
    $graphics.FillPath($brush, $path)

    $fontSize = [Math]::Max(8, $size * 0.54)
    $font = [System.Drawing.Font]::new('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $format = [System.Drawing.StringFormat]::new()
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $format.FormatFlags = [System.Drawing.StringFormatFlags]::NoClip
    $textRect = [System.Drawing.RectangleF]::new(0, -($size * 0.025), $size, $size)
    $graphics.DrawString('Q', $font, [System.Drawing.Brushes]::White, $textRect, $format)

    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }

    $stream.Dispose()
    $format.Dispose()
    $font.Dispose()
    $brush.Dispose()
    $path.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}

$directory = Split-Path $OutputPath -Parent
if ($directory) { [System.IO.Directory]::CreateDirectory($directory) | Out-Null }
$file = [System.IO.File]::Open($OutputPath, [System.IO.FileMode]::Create)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)
    $offset = 6 + (16 * $images.Count)
    foreach ($image in $images) {
        $writer.Write([byte]$(if ($image.Size -eq 256) { 0 } else { $image.Size }))
        $writer.Write([byte]$(if ($image.Size -eq 256) { 0 } else { $image.Size }))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Bytes.Length
    }
    foreach ($image in $images) {
        $writer.Write([byte[]]$image.Bytes)
    }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Output "Generated $OutputPath"
