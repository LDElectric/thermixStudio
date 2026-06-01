Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Image]::FromFile("c:\Users\Leonam Dias\Documents\Projetos C#\Thermix Studio\FLIR0060.jpg")
$bmp = New-Object System.Drawing.Bitmap($img)

$w = $bmp.Width
$h = $bmp.Height

$minX = $w; $maxX = 0; $minY = $h; $maxY = 0

for ($y = [int]($h * 0.8); $y -lt $h; $y++) {
    for ($x = 0; $x -lt [int]($w * 0.3); $x++) {
        $px = $bmp.GetPixel($x, $y)
        # Search for bright white pixels of the logo
        if ($px.R -gt 240 -and $px.G -gt 240 -and $px.B -gt 240) {
            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
}

Write-Host "FLIR0060.jpg Original Logo Bounds: X: $minX-$maxX, Y: $minY-$maxY"
Write-Host "Width: $($maxX - $minX + 1), Height: $($maxY - $minY + 1)"

$bmp.Dispose()
$img.Dispose()
