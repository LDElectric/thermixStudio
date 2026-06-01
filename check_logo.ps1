Add-Type -AssemblyName System.Drawing

$srcPath = "c:\Users\Leonam Dias\Documents\Projetos C#\Thermix Studio\flir_logo_filled.png"
$dstPath = "c:\Users\Leonam Dias\Documents\Projetos C#\Thermix Studio\flir_logo_cropped.png"

$src = New-Object System.Drawing.Bitmap($srcPath)
$w = $src.Width
$h = $src.Height

Write-Host "Source: ${w}x${h}"

# Find bounding box of opaque content (white logo)
$minX = $w; $maxX = 0; $minY = $h; $maxY = 0

for ($y = 0; $y -lt $h; $y++) {
    for ($x = 0; $x -lt $w; $x++) {
        $px = $src.GetPixel($x, $y)
        if ($px.A -gt 10) {
            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
    if ($y % 100 -eq 0) { Write-Host "  Scanning row $y..." }
}

$contentW = $maxX - $minX + 1
$contentH = $maxY - $minY + 1
Write-Host "Content bounds: ($minX,$minY) to ($maxX,$maxY) = ${contentW}x${contentH}"

# Add 2px padding
$pad = 2
$cropX = [Math]::Max(0, $minX - $pad)
$cropY = [Math]::Max(0, $minY - $pad)
$cropW = [Math]::Min($contentW + 2*$pad, $w - $cropX)
$cropH = [Math]::Min($contentH + 2*$pad, $h - $cropY)

Write-Host "Cropping to ($cropX,$cropY) ${cropW}x${cropH}"

$dst = New-Object System.Drawing.Bitmap($cropW, $cropH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($dst)
$g.DrawImage($src, 0, 0, [System.Drawing.Rectangle]::new($cropX, $cropY, $cropW, $cropH), [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose()

$dst.Save($dstPath, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Saved cropped to $dstPath (${cropW}x${cropH})"

# Also save as the final flir_logo.png replacement
$finalPath = "c:\Users\Leonam Dias\Documents\Projetos C#\Thermix Studio\flir_logo_new.png"
$dst.Save($finalPath, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Saved as $finalPath"

$src.Dispose()
$dst.Dispose()
