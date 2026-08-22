<#
verify-all-variants.ps1

One-click regression: export ALL merge/split variants with the current source
and verify every output file (XMP marker, history Source, HEIC decode,
embedded-video playback, no injection-related warnings).

Scenarios:
  - merge --all-variants  : Apple HEIC+MOV (12 single-file outputs)
  - split --all-variants  : OPPO/OnePlus single-file JPG (7 pairs = 14 files)
  - HUAWEI HEIC merge     : real HUAWEI HEIC source (byte-level XMP verify)
  - repair                : fixable real-device JPG (history + content intact)
  - cover                 : single-file + dual-file cover (Cover history)
  - HUAWEI split -> merge : re-merge our own split output (single-XMP regression)

Output goes to <repo>\ai-tmp\verify-run-<timestamp> (gitignored).
Exit code 0 = all PASS, 1 = at least one FAIL.

Usage:
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-all-variants.ps1
  powershell ... -SkipBuild -SkipHuawei -OutputDir D:\tmp\verify
#>

param(
    [string]$OutputDir = "",
    [switch]$SkipBuild,
    [switch]$SkipHuawei,
    [string]$AppleImage = "",
    [string]$AppleVideo = "",
    [string]$OppoSource = "",
    [string]$HuaweiHeic = "",
    [string]$HuaweiVideo = "",
    [string]$RepairSource = "",
    [string]$VivoImage = "",
    [string]$VivoVideo = "",
    [string]$HuaweiDesignHeic = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

# UTF-8 console output so Chinese sample names render correctly.
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# ---------- locate tools ----------
$cli = Join-Path $repo "LivePhotoBox.CLI\bin\Debug\net9.0-windows10.0.19041.0\livephotobox-boot.exe"
$exiftool = Join-Path $repo "LivePhotoBox\Tools\exiftool.exe"
$heifDec = Join-Path $repo "LivePhotoBox\Tools\heif-dec.exe"
$ffprobe = (Get-Command ffprobe -ErrorAction SilentlyContinue).Source
if (-not $ffprobe) { $ffprobe = "" }

$missing = @()
if (-not (Test-Path $cli)) { $missing += "CLI binary (run without -SkipBuild)" }
if (-not (Test-Path $exiftool)) { $missing += "exiftool" }
if (-not (Test-Path $heifDec)) { $missing += "heif-dec" }
if (-not $ffprobe) { $missing += "ffprobe (add to PATH)" }
if ($missing.Count -gt 0) {
    Write-Host "ERROR: missing tools: $($missing -join ', ')" -ForegroundColor Red
    exit 2
}

if (-not $OutputDir) {
    $OutputDir = Join-Path $repo ("ai-tmp\verify-run-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

# ---------- samples (overridable via params, defaults to local test set) ----------
if (-not $AppleImage) { $AppleImage = Join-Path $repo "designs\各个机型测试\苹果双文件.HEIC" }
if (-not $AppleVideo) { $AppleVideo = Join-Path $repo "designs\各个机型测试\苹果双文件.MOV" }
if (-not $OppoSource) { $OppoSource  = Join-Path $repo "designs\各个机型测试\一加.jpg" }
if (-not $HuaweiHeic) { $HuaweiHeic = "C:\Users\LengxiQwQ\Downloads\实况照片样本\华为测试主图\主图测试-原图.heic" }
if (-not $HuaweiVideo) { $HuaweiVideo = "C:\Users\LengxiQwQ\Downloads\实况照片样本\华为实况照片样本\输出_拆分照片\heic原始p80 max.MOV" }
if (-not $RepairSource) { $RepairSource = Join-Path $repo "designs\各个机型测试\红米老款.JPG" }
if (-not $VivoImage) { $VivoImage = Join-Path $repo "designs\各个机型测试\vivo双文件.jpg" }
if (-not $VivoVideo) { $VivoVideo = Join-Path $repo "designs\各个机型测试\vivo双文件.mp4" }
if (-not $HuaweiDesignHeic) { $HuaweiDesignHeic = Join-Path $repo "designs\各个机型测试\华为.heic" }

$appleImg = $AppleImage
$appleMov = $AppleVideo
$oppoJpg  = $OppoSource
$huaweiMov = $HuaweiVideo
$repairJpg = $RepairSource
$vivoImg = $VivoImage
$vivoMov = $VivoVideo
$huaweiDesign = $HuaweiDesignHeic

$allOk = $true
$failList = New-Object System.Collections.Generic.List[string]
$passCount = 0
$failCount = 0

function Run-Verify([string[]]$Files, [string]$ExpectedSource, [switch]$SkipVideo, [string]$ExpectedAction = "") {
    $script:global:failList = $failList
    foreach ($f in $Files) {
        $pyArgs = @(
            (Join-Path $PSScriptRoot "Python_Scripts\verify_variants.py"),
            $f,
            "--exiftool", $exiftool,
            "--heif-dec", $heifDec,
            "--ffprobe", $ffprobe
        )
        if ($ExpectedSource) { $pyArgs += "--expected-source"; $pyArgs += $ExpectedSource }
        if ($SkipVideo) { $pyArgs += "--skip-video" }
        if ($ExpectedAction) { $pyArgs += "--expected-action"; $pyArgs += $ExpectedAction }
        $rc = & python @pyArgs
        if ($LASTEXITCODE -eq 0) {
            $script:passCount++
            Write-Host $rc -ForegroundColor Green
        } else {
            $script:failCount++
            $script:allOk = $false
            $failList.Add($f)
            Write-Host $rc -ForegroundColor Red
        }
    }
}

function Invoke-MergeExport([string]$Image, [string]$Video, [string]$Out, [string]$ExtraArgs) {
    $outDir = Join-Path $OutputDir $Out
    if ($ExtraArgs) {
        & $cli merge $Image $Video $ExtraArgs.Split(" ") -o $outDir -y | Out-Null
    } else {
        & $cli merge $Image $Video --all-variants -o $outDir | Out-Null
    }
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAIL: merge export failed ($Out)" -ForegroundColor Red
        $script:allOk = $false
        $failList.Add("merge:$Out")
        return
    }
    return $outDir
}

# ---------- 0. build CLI from current source ----------
if (-not $SkipBuild) {
    Write-Host "Building CLI..." -ForegroundColor Cyan
    dotnet build (Join-Path $repo "LivePhotoBox.CLI\LivePhotoBox.CLI.csproj") -v q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: CLI build failed" -ForegroundColor Red
        exit 2
    }
}

Write-Host "Output: $OutputDir" -ForegroundColor Cyan

# ---------- 1. merge --all-variants (Apple source) ----------
if ((Test-Path $appleImg) -and (Test-Path $appleMov)) {
    Write-Host "`n[1/6] merge --all-variants (Apple HEIC+MOV)" -ForegroundColor Cyan
    $mergeDir = Invoke-MergeExport $appleImg $appleMov "merge" ""
    if ($mergeDir) {
        $variantsDir = Get-ChildItem -LiteralPath $mergeDir -Directory -Filter "*_variants" |
            Select-Object -First 1 -ExpandProperty FullName
        if (Test-Path $variantsDir) {
            $files = Get-ChildItem -LiteralPath $variantsDir -File
            Run-Verify $files.FullName "Apple"
        }
    }
} else {
    Write-Host "WARN: Apple samples missing, skipping merge export" -ForegroundColor Yellow
}

# ---------- 2. split --all-variants (OPPO source) ----------
if (Test-Path $oppoJpg) {
    Write-Host "`n[2/6] split --all-variants (OPPO 一加.jpg)" -ForegroundColor Cyan
    $splitOut = Join-Path $OutputDir "split"
    & $cli split $oppoJpg --all-variants -o $splitOut | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $variantsDir = Get-ChildItem -LiteralPath $splitOut -Directory -Filter "split_*_All_Variants" |
            Select-Object -First 1 -ExpandProperty FullName
        if (Test-Path $variantsDir) {
            $files = Get-ChildItem -LiteralPath $variantsDir -File
            Run-Verify $files.FullName "OppoLivePhoto" -SkipVideo
        }
    } else {
        Write-Host "FAIL: split export failed" -ForegroundColor Red
        $script:allOk = $false
        $failList.Add("split:all-variants")
    }
} else {
    Write-Host "WARN: OPPO sample missing, skipping split export" -ForegroundColor Yellow
}

# ---------- 3. HUAWEI HEIC merge (real device source) ----------
if (-not $SkipHuawei -and (Test-Path $huaweiHeic) -and (Test-Path $huaweiMov)) {
    Write-Host "`n[3/6] HUAWEI HEIC merge (real device source)" -ForegroundColor Cyan
    $hwOut = Invoke-MergeExport $huaweiHeic $huaweiMov "huawei" "-p huawei -f heic+mp4"
    if ($hwOut) {
        $name = [System.IO.Path]::GetFileNameWithoutExtension($huaweiHeic)
        $outFile = Join-Path $hwOut "$($name)huawei.heic"
        if (Test-Path $outFile) {
            Run-Verify @($outFile) "HuaweiMovingPhoto"
        } else {
            # Actual naming: {originalBaseName}{protocolSuffix}.heic
            $candidates = Get-ChildItem -LiteralPath $hwOut -Filter "*.heic"
            if ($candidates) { Run-Verify $candidates.FullName "HuaweiMovingPhoto" }
            else {
                Write-Host "FAIL: HUAWEI output not found" -ForegroundColor Red
                $script:allOk = $false
                $failList.Add("huawei:no-output")
            }
        }
    }
} else {
    Write-Host "`n[3/6] HUAWEI merge skipped (samples missing or -SkipHuawei)" -ForegroundColor Yellow
}

# ---------- 4. repair (real-device JPG with fixable issues) ----------
if (Test-Path $repairJpg) {
    Write-Host "`n[4/6] repair (红米老款.JPG, all devices)" -ForegroundColor Cyan
    $repairOut = Join-Path $OutputDir "repair"
    & $cli repair $repairJpg --all-devices -o $repairOut -y | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $repaired = Get-ChildItem -LiteralPath $repairOut -File -Filter "*_repaired.*" |
            Select-Object -First 1 -ExpandProperty FullName
        if ($repaired) {
            Run-Verify @($repaired) "" -ExpectedAction "Repair"
        } else {
            Write-Host "FAIL: repaired output not found" -ForegroundColor Red
            $script:allOk = $false
            $failList.Add("repair:no-output")
        }
    } else {
        Write-Host "FAIL: repair failed" -ForegroundColor Red
        $script:allOk = $false
        $failList.Add("repair:failed")
    }
} else {
    Write-Host "`n[4/6] repair skipped (sample missing)" -ForegroundColor Yellow
}

# ---------- 5. cover (single-file + dual-file) ----------
$mergeDirForCover = Join-Path $OutputDir "merge"
$coverVariantsDir = Get-ChildItem -LiteralPath $mergeDirForCover -Directory -Filter "*_variants" -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName
$singleCoverSrc = Get-ChildItem -LiteralPath $coverVariantsDir -File -Filter "*_MotionPhoto_JPEG+MP4.jpg" -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($singleCoverSrc) {
    Write-Host "`n[5/6] cover single-file ($($singleCoverSrc.Name))" -ForegroundColor Cyan
    $coverOut = Join-Path $OutputDir "cover-single"
    & $cli cover $singleCoverSrc.FullName --at 1.0 -o $coverOut -y | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $covered = Get-ChildItem -LiteralPath $coverOut -File -Filter "*_cover*" -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty FullName
        if ($covered) {
            Run-Verify @($covered) "" -ExpectedAction "Cover"
        } else {
            Write-Host "FAIL: cover output not found" -ForegroundColor Red
            $script:allOk = $false
            $failList.Add("cover:no-output")
        }
    } else {
        Write-Host "FAIL: cover failed" -ForegroundColor Red
        $script:allOk = $false
        $failList.Add("cover:failed")
    }
} else {
    Write-Host "`n[5/6] cover single-file skipped (no merge variant)" -ForegroundColor Yellow
}

if ((Test-Path $vivoImg) -and (Test-Path $vivoMov)) {
    Write-Host "`n[5/6] cover dual-file (vivo双文件)" -ForegroundColor Cyan
    $coverDualOut = Join-Path $OutputDir "cover-dual"
    & $cli cover $vivoImg $vivoMov --at 1.0 -o $coverDualOut -y | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $coverImgs = Get-ChildItem -LiteralPath $coverDualOut -File -Filter "*_cover*.JPG" -ErrorAction SilentlyContinue
        $coverVids = Get-ChildItem -LiteralPath $coverDualOut -File -Filter "*_cover*.MP4" -ErrorAction SilentlyContinue
        if ($coverImgs) { Run-Verify $coverImgs.FullName "" -SkipVideo -ExpectedAction "Cover" }
        if ($coverVids) { Run-Verify $coverVids.FullName "" -SkipVideo -ExpectedAction "Cover" }
        if (-not $coverImgs -and -not $coverVids) {
            Write-Host "FAIL: dual-file cover outputs not found" -ForegroundColor Red
            $script:allOk = $false
            $failList.Add("cover-dual:no-output")
        }
    } else {
        Write-Host "FAIL: dual-file cover failed" -ForegroundColor Red
        $script:allOk = $false
        $failList.Add("cover-dual:failed")
    }
} else {
    Write-Host "`n[5/6] cover dual-file skipped (samples missing)" -ForegroundColor Yellow
}

# ---------- 6. HUAWEI split -> merge (single-XMP regression) ----------
if (Test-Path $huaweiDesign) {
    Write-Host "`n[6/6] HUAWEI split -> merge (single-XMP regression)" -ForegroundColor Cyan
    $hwSplitOut = Join-Path $OutputDir "huawei-split"
    & $cli split $huaweiDesign -o $hwSplitOut -y | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $splitImg = Get-ChildItem -LiteralPath $hwSplitOut -File -Filter "*_split.heic" -ErrorAction SilentlyContinue |
            Select-Object -First 1
        $splitVid = Get-ChildItem -LiteralPath $hwSplitOut -File -Filter "*_split.MP4" -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($splitImg -and $splitVid) {
            # 拆分产物本身也要验证（图片 + 视频都必须带本软件 XMP 标识）。
            Run-Verify @($splitImg.FullName) "HuaweiMovingPhoto" -SkipVideo
            Run-Verify @($splitVid.FullName) "HuaweiMovingPhoto" -SkipVideo

            $hwMergeOut = Join-Path $OutputDir "huawei-split-merge"
            & $cli merge $splitImg.FullName $splitVid.FullName -p huawei -f heic+mp4 -o $hwMergeOut -y | Out-Null
            if ($LASTEXITCODE -eq 0) {
                $mergedHw = Get-ChildItem -LiteralPath $hwMergeOut -File -Filter "*.heic" -ErrorAction SilentlyContinue |
                    Select-Object -First 1
                if ($mergedHw) {
                    Run-Verify @($mergedHw.FullName) "HuaweiMovingPhoto" -ExpectedAction "Merge"
                } else {
                    Write-Host "FAIL: HUAWEI split->merge output not found" -ForegroundColor Red
                    $script:allOk = $false
                    $failList.Add("huawei-split-merge:no-output")
                }
            } else {
                Write-Host "FAIL: HUAWEI split->merge failed" -ForegroundColor Red
                $script:allOk = $false
                $failList.Add("huawei-split-merge:failed")
            }
        } else {
            Write-Host "FAIL: HUAWEI split outputs not found" -ForegroundColor Red
            $script:allOk = $false
            $failList.Add("huawei-split:no-output")
        }
    } else {
        Write-Host "FAIL: HUAWEI split failed" -ForegroundColor Red
        $script:allOk = $false
        $failList.Add("huawei-split:failed")
    }
} else {
    Write-Host "`n[6/6] HUAWEI split->merge skipped (design sample missing)" -ForegroundColor Yellow
}

# ---------- summary ----------
Write-Host "`n==========================================" -ForegroundColor Cyan
Write-Host "PASS: $passCount   FAIL: $failCount" -ForegroundColor $(if ($allOk) { "Green" } else { "Red" })
if (-not $allOk) {
    Write-Host "Failed files:" -ForegroundColor Red
    foreach ($f in $failList) { Write-Host "  $f" -ForegroundColor Red }
}
Write-Host "Output: $OutputDir" -ForegroundColor Cyan

if ($allOk) { exit 0 } else { exit 1 }
