Add-Type -AssemblyName System.Drawing

$assetsPath = Join-Path $PSScriptRoot "..\LivePhotoBox\Assets"
$sourcePath = Join-Path $assetsPath "AppIcon.png"

if (-not (Test-Path $sourcePath)) {
    throw "Source icon not found: $sourcePath"
}

function New-ResampleReadyBitmap {
    param(
        [System.Drawing.Bitmap]$SourceBitmap
    )

    $prepared = New-Object System.Drawing.Bitmap($SourceBitmap.Width, $SourceBitmap.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    for ($y = 0; $y -lt $SourceBitmap.Height; $y++) {
        for ($x = 0; $x -lt $SourceBitmap.Width; $x++) {
            $prepared.SetPixel($x, $y, $SourceBitmap.GetPixel($x, $y))
        }
    }

    $radius = 8
    $globalR = 0
    $globalG = 0
    $globalB = 0
    $globalCount = 0

    for ($y = 0; $y -lt $SourceBitmap.Height; $y++) {
        for ($x = 0; $x -lt $SourceBitmap.Width; $x++) {
            $pixel = $SourceBitmap.GetPixel($x, $y)
            if ($pixel.A -gt 0) {
                $globalR += $pixel.R
                $globalG += $pixel.G
                $globalB += $pixel.B
                $globalCount++
            }
        }
    }

    $fallbackR = 255
    $fallbackG = 255
    $fallbackB = 255

    if ($globalCount -gt 0) {
        $fallbackR = [int]($globalR / $globalCount)
        $fallbackG = [int]($globalG / $globalCount)
        $fallbackB = [int]($globalB / $globalCount)
    }

    for ($y = 0; $y -lt $prepared.Height; $y++) {
        for ($x = 0; $x -lt $prepared.Width; $x++) {
            $pixel = $prepared.GetPixel($x, $y)
            if ($pixel.A -ne 0) {
                continue
            }

            $r = 0
            $g = 0
            $b = 0
            $count = 0

            for ($ny = [Math]::Max(0, $y - $radius); $ny -le [Math]::Min($prepared.Height - 1, $y + $radius); $ny++) {
                for ($nx = [Math]::Max(0, $x - $radius); $nx -le [Math]::Min($prepared.Width - 1, $x + $radius); $nx++) {
                    $neighbor = $SourceBitmap.GetPixel($nx, $ny)
                    if ($neighbor.A -gt 0) {
                        $r += $neighbor.R
                        $g += $neighbor.G
                        $b += $neighbor.B
                        $count++
                    }
                }
            }

            if ($count -gt 0) {
                $prepared.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(0, [int]($r / $count), [int]($g / $count), [int]($b / $count)))
            }
            else {
                $prepared.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(0, $fallbackR, $fallbackG, $fallbackB))
            }
        }
    }

    return $prepared
}

function New-ScaledBitmap {
    param(
        [System.Drawing.Image]$SourceImage,
        [int]$Size
    )

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $imageAttributes = New-Object System.Drawing.Imaging.ImageAttributes

    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $imageAttributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $graphics.DrawImage(
            $SourceImage,
            (New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)),
            0,
            0,
            $SourceImage.Width,
            $SourceImage.Height,
            [System.Drawing.GraphicsUnit]::Pixel,
            $imageAttributes
        )
    }
    finally {
        $imageAttributes.Dispose()
        $graphics.Dispose()
    }

    return $bitmap
}

$targets = @(
    @{ Name = 'Square150x150Logo.png'; Size = 150 },
    @{ Name = 'Square150x150Logo.scale-200.png'; Size = 300 },
    @{ Name = 'Square44x44Logo.png'; Size = 44 },
    @{ Name = 'Square44x44Logo.scale-100.png'; Size = 44 },
    @{ Name = 'Square44x44Logo.scale-125.png'; Size = 55 },
    @{ Name = 'Square44x44Logo.scale-150.png'; Size = 66 },
    @{ Name = 'Square44x44Logo.scale-200.png'; Size = 88 },
    @{ Name = 'Square44x44Logo.scale-400.png'; Size = 176 },
    @{ Name = 'Square44x44Logo.targetsize-16.png'; Size = 16 },
    @{ Name = 'Square44x44Logo.targetsize-24.png'; Size = 24 },
    @{ Name = 'Square44x44Logo.targetsize-32.png'; Size = 32 },
    @{ Name = 'Square44x44Logo.targetsize-48.png'; Size = 48 },
    @{ Name = 'Square44x44Logo.targetsize-256.png'; Size = 256 },
    @{ Name = 'Square44x44Logo.altform-unplated_targetsize-16.png'; Size = 16 },
    @{ Name = 'Square44x44Logo.targetsize-24_altform-unplated.png'; Size = 24 },
    @{ Name = 'Square44x44Logo.altform-unplated_targetsize-32.png'; Size = 32 },
    @{ Name = 'Square44x44Logo.altform-unplated_targetsize-48.png'; Size = 48 },
    @{ Name = 'Square44x44Logo.altform-unplated_targetsize-256.png'; Size = 256 },
    @{ Name = 'Square44x44Logo.altform-lightunplated_targetsize-16.png'; Size = 16 },
    @{ Name = 'Square44x44Logo.altform-lightunplated_targetsize-24.png'; Size = 24 },
    @{ Name = 'Square44x44Logo.altform-lightunplated_targetsize-32.png'; Size = 32 },
    @{ Name = 'Square44x44Logo.altform-lightunplated_targetsize-48.png'; Size = 48 },
    @{ Name = 'Square44x44Logo.altform-lightunplated_targetsize-256.png'; Size = 256 },
    @{ Name = 'StoreLogo.png'; Size = 50 },
    @{ Name = 'LockScreenLogo.scale-200.png'; Size = 48 }
)

$sourceBytes = [System.IO.File]::ReadAllBytes($sourcePath)
$stream = New-Object System.IO.MemoryStream(,$sourceBytes)
$sourceBitmap = [System.Drawing.Bitmap]::FromStream($stream)
$sourceImage = New-ResampleReadyBitmap -SourceBitmap $sourceBitmap

try {
    foreach ($target in $targets) {
        $bitmap = New-ScaledBitmap -SourceImage $sourceImage -Size $target.Size
        try {
            $outputPath = Join-Path $assetsPath $target.Name
            $bitmap.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Png)
            Write-Output ("Generated {0}" -f $target.Name)
        }
        finally {
            $bitmap.Dispose()
        }
    }

}
finally {
    $sourceImage.Dispose()
    $sourceBitmap.Dispose()
    $stream.Dispose()
}
