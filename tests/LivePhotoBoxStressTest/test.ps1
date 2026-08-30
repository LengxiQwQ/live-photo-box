param(
    [string]$Dir = "D:/图片/相册/苹果导出2025年",
    [int]$Iterations = 30,
    [int]$DelayMs = 50
)

$ErrorActionPreference = "Continue"
$toolsDir = "D:/Projects/live-photo-box/LivePhotoBox/Tools"
$exifTool = "$toolsDir/exiftool.exe"
$ffmpeg = "$toolsDir/ffmpeg.exe"

if (-not (Test-Path $Dir)) { Write-Host "ERROR: Directory not found: $Dir"; exit 1 }
if (-not (Test-Path $exifTool)) { Write-Host "ERROR: exiftool not found"; exit 1 }

Write-Host "=============================================="
Write-Host "  Live Photo Box - EditPage Stress Test"
Write-Host "=============================================="
Write-Host "  Dir:       $Dir"
Write-Host "  Iters:     $Iterations"
Write-Host "  Delay:     ${DelayMs}ms"

# ---- 1. Scan ----
Write-Host ""
Write-Host "=== Test 1: Directory Scan ==="
$scanSw = [Diagnostics.Stopwatch]::StartNew()
$allFiles = Get-ChildItem -Path $Dir -File | Where-Object { $_.Extension -match '\.(jpg|jpeg|heic|heif|png|bmp|gif|tiff|tif|webp|mov|mp4)$' }
$scanElapsed = $scanSw.ElapsedMilliseconds
$imageFiles = $allFiles | Where-Object { $_.Extension -notmatch '\.(mov|mp4)$' }
$videoFiles = $allFiles | Where-Object { $_.Extension -match '\.(mov|mp4)$' }
Write-Host "  Scan time: ${scanElapsed}ms"
Write-Host "  Images: $($imageFiles.Count)"
Write-Host "  Videos: $($videoFiles.Count)"

# ---- 2. exiftool switch stress ----
Write-Host ""
Write-Host "=== Test 2: exiftool Rapid Switch ($Iterations iters, ${DelayMs}ms gap) ==="
$switchSw = [Diagnostics.Stopwatch]::StartNew()
$timings = @()
$errors = 0
$rng = [Random]::new(42)

for ($i = 0; $i -lt $Iterations; $i++) {
    $file = $imageFiles[$rng.Next($imageFiles.Count)]
    $t1 = $switchSw.ElapsedMilliseconds
    try {
        $null = & $exifTool -j -ImageWidth -ImageHeight -Make -Model -DateTimeOriginal -MediaDuration -AvgBitrate -ContentIdentifier $file.FullName 2>$null
        $elapsed = $switchSw.ElapsedMilliseconds - $t1
        $timings += $elapsed
    } catch {
        $errors++
        $timings += ($switchSw.ElapsedMilliseconds - $t1)
    }
    if ($i % 20 -eq 0 -and $i -gt 0) { Write-Host "  Progress: $i/$Iterations" }
    Start-Sleep -Milliseconds $DelayMs
}
$switchElapsed = $switchSw.ElapsedMilliseconds
$avgSwitch = [Math]::Round(($timings | Measure-Object -Average).Average, 0)
$sortedTimings = $timings | Sort-Object
$p50 = $sortedTimings[[Math]::Floor($sortedTimings.Count * 0.50)]
$p95 = $sortedTimings[[Math]::Floor($sortedTimings.Count * 0.95)]
$slowCount = ($timings | Where-Object { $_ -gt 500 }).Count

Write-Host "  Total:    $([Math]::Round($switchElapsed / 1000, 1))s"
Write-Host "  Average:  ${avgSwitch}ms"
Write-Host "  Fastest:  $($sortedTimings[0])ms"
Write-Host "  Slowest:  $($sortedTimings[-1])ms"
Write-Host "  P50:      ${p50}ms"
Write-Host "  P95:      ${p95}ms"
Write-Host "  Slow(>500ms): $slowCount"
Write-Host "  Errors:   $errors"

# ---- 3. ffmpeg frame extraction ----
if (Test-Path $ffmpeg) {
    Write-Host ""
    Write-Host "=== Test 3: ffmpeg Frame Extraction (Live Photos) ==="
    $movNames = @{}
    foreach ($f in $videoFiles) {
        $base = [IO.Path]::GetFileNameWithoutExtension($f.Name)
        $movNames[$base] = $f.FullName
    }
    $livePhotos = @()
    foreach ($f in $imageFiles) {
        $base = [IO.Path]::GetFileNameWithoutExtension($f.Name)
        if ($movNames.ContainsKey($base)) {
            $livePhotos += @{ Photo = $f.FullName; Video = $movNames[$base]; PhotoName = $f.Name }
        }
    }
    Write-Host "  Live photo pairs: $($livePhotos.Count)"

    if ($livePhotos.Count -gt 0) {
        $frameIters = [Math]::Min($Iterations, $livePhotos.Count * 3)
        $frameSw = [Diagnostics.Stopwatch]::StartNew()
        $frameTimings = @()
        $frameErrors = 0

        for ($i = 0; $i -lt $frameIters; $i++) {
            $lp = $livePhotos[$i % $livePhotos.Count]
            $t1 = $frameSw.ElapsedMilliseconds
            try {
                $tmpDir = [IO.Path]::Combine([IO.Path]::GetTempPath(), "lpb_stress_$([Guid]::NewGuid())")
                New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null
                $proc = Start-Process -FilePath $ffmpeg `
                    -ArgumentList "-i `"$($lp.Video)`" -fps_mode passthrough -q:v 3 -f image2 `"$tmpDir\frame_%06d.jpg`" -y -loglevel error" `
                    -Wait -NoNewWindow -PassThru
                $frameCount = (Get-ChildItem -Path $tmpDir -Filter "frame_*.jpg" -ErrorAction SilentlyContinue).Count
                Remove-Item -Path $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
                $elapsed = $frameSw.ElapsedMilliseconds - $t1
                $frameTimings += @{ Time = $elapsed; Frames = $frameCount; Name = $lp.PhotoName }
            } catch {
                $frameErrors++
            }
            if ($i % 5 -eq 0 -and $i -gt 0) { Write-Host "  Frames: $i/$frameIters" }
            Start-Sleep -Milliseconds ($DelayMs * 2)
        }
        $frameElapsed = $frameSw.ElapsedMilliseconds
        if ($frameTimings.Count -gt 0) {
            $avgFrame = [Math]::Round(($frameTimings | ForEach-Object { $_.Time } | Measure-Object -Average).Average, 0)
            $sortedFrames = $frameTimings | Sort-Object { $_.Time }
            Write-Host "  Total:    $([Math]::Round($frameElapsed / 1000, 1))s"
            Write-Host "  Average:  ${avgFrame}ms"
            Write-Host "  Fastest:  $($sortedFrames[0].Time)ms ($($sortedFrames[0].Name): $($sortedFrames[0].Frames) frames)"
            Write-Host "  Slowest:  $($sortedFrames[-1].Time)ms ($($sortedFrames[-1].Name): $($sortedFrames[-1].Frames) frames)"
            Write-Host "  Errors:   $frameErrors"
        }
    }
}

Write-Host ""
Write-Host "=============================================="
Write-Host "  Stress Test Complete"
Write-Host "=============================================="
