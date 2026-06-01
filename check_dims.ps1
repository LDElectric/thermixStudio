Add-Type -AssemblyName System.Drawing
$img1 = [System.Drawing.Image]::FromFile("c:\Users\Leonam Dias\Documents\Projetos C#\Thermix Studio\FLIR0060.jpg")
$img2 = [System.Drawing.Image]::FromFile("c:\Users\Leonam Dias\Documents\Projetos C#\Thermix Studio\FLIR0060_analise.jpg")

Write-Host "Original: $($img1.Width)x$($img1.Height)"
Write-Host "Render: $($img2.Width)x$($img2.Height)"

$img1.Dispose()
$img2.Dispose()
