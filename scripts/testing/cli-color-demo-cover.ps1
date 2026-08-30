param(
    [Parameter(Mandatory = $true)]
    [string]$Exe
)

$ErrorActionPreference = 'Continue'
$PSNativeCommandUseErrorActionPreference = $false

$Exe = (Resolve-Path $Exe).Path

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$designs = Join-Path $root 'designs'

function Get-SampleBySize {
    param([long]$Size)
    Get-ChildItem -LiteralPath $designs -Recurse -File |
        Where-Object { $_.Length -eq $Size } |
        Select-Object -First 1
}

$redmi = Get-SampleBySize -Size 8943926
$xiaomi = Get-SampleBySize -Size 4038241
$oppo = Get-SampleBySize -Size 13762654
$oppoEdited = Get-SampleBySize -Size 20123765
$appleImg = Get-SampleBySize -Size 2856220
$appleVid = Get-SampleBySize -Size 2782940

function Show-Header {
    param([string]$Title)
    Write-Host ''
    Write-Host ('=' * 60)
    Write-Host (" $Title")
    Write-Host ('=' * 60)
}

Show-Header '[13] cover --help'
& $Exe 'cover' '--help'

if ($null -ne $redmi) {
    Show-Header '[14] cover single view (v1)'
    & $Exe 'cover' $redmi.FullName
} else {
    Write-Host 'WARN: v1 sample (redmi) not found, skipping [14].'
}

if ($null -ne $xiaomi) {
    Show-Header '[15] cover single view (v2)'
    & $Exe 'cover' $xiaomi.FullName
} else {
    Write-Host 'WARN: v2 sample (xiaomi) not found, skipping [15].'
}

if ($null -ne $redmi) {
    Show-Header '[16] cover single dry-run (current + new cover)'
    & $Exe 'cover' $redmi.FullName '--frame' '10' '--dry-run'
} else {
    Write-Host 'WARN: v1 sample (redmi) not found, skipping [16].'
}

if ($null -ne $appleImg -and $null -ne $appleVid) {
    Show-Header '[17] cover dual view (Apple)'
    & $Exe 'cover' $appleImg.FullName $appleVid.FullName
    Show-Header '[18] cover dual dry-run (photo + video + new cover)'
    & $Exe 'cover' $appleImg.FullName $appleVid.FullName '--frame' '10' '--dry-run'
} else {
    Write-Host 'WARN: Apple dual-file samples not found, skipping [17]/[18].'
}

if ($null -ne $oppoEdited) {
    Show-Header '[19] cover OPPO edited view (original + current cover)'
    & $Exe 'cover' $oppoEdited.FullName
} else {
    Write-Host 'WARN: OPPO edited sample not found, skipping [19].'
}

if ($null -ne $oppo) {
    Show-Header '[20] cover OPPO dry-run (original + new current cover)'
    & $Exe 'cover' $oppo.FullName '--frame' '10' '--dry-run'
} else {
    Write-Host 'WARN: OPPO sample not found, skipping [20].'
}

if ($null -ne $redmi) {
    Show-Header '[21] cover error path (should be red)'
    & $Exe 'cover' $redmi.FullName '--at' '1' '--frame' '2'
} else {
    Write-Host 'WARN: v1 sample (redmi) not found, skipping [21].'
}

Show-Header '[22] split --help'
& $Exe 'split' '--help'

if ($null -ne $redmi) {
    Show-Header '[23] split single dry-run (scan/summary colors)'
    & $Exe 'split' $redmi.FullName '--dry-run'
    Show-Header '[24] split error path (should be red)'
    & $Exe 'split' $redmi.FullName '-p' 'nosuchproto'
} else {
    Write-Host 'WARN: v1 sample (redmi) not found, skipping [23]/[24].'
}
