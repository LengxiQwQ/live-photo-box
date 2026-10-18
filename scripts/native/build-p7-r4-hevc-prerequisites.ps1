[CmdletBinding()]
param(
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,23}$')][string]$BuildId = 'r4-hevc-v2',
    [ValidateRange(1,64)][int]$Jobs = [Math]::Min([Environment]::ProcessorCount,8)
)
$ErrorActionPreference='Stop'
function Get-HevcPrerequisiteSha256([string]$LiteralPath) {
    # MSBuild invokes Windows PowerShell 5.1, whose inherited module path may
    # not expose Get-FileHash. Keep the build independent of module autoload.
    $algorithm=[System.Security.Cryptography.SHA256]::Create()
    $stream=[System.IO.File]::Open($LiteralPath,[System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,[System.IO.FileShare]::Read)
    try { return ([System.BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-','') }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
$projectRoot=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$workspace=Join-Path $projectRoot '.ai-tmp\workspace\P7\R4-large-batch-soak\hevc-migration'
$baseWorkspace=Join-Path $workspace 'base-prerequisites'
# Keep the old frozen profile/cache untouched. It supplies the same pinned
# minimal FFmpeg/x264; only the final x265 archive is replaced by static 8+10.
& (Join-Path $PSScriptRoot 'build-p5-r5-video-sidecar-prerequisites.ps1') `
    -BuildId "$BuildId-base" -Jobs $Jobs -WorkspaceRootOverride $baseWorkspace
if(-not $?){throw 'Pinned minimal FFmpeg/x264 prerequisite build failed.'}
$baseRecord=Join-Path $baseWorkspace "$BuildId-base-libav-prerequisites.json"
$base=Get-Content -LiteralPath $baseRecord -Raw -Encoding UTF8 | ConvertFrom-Json
& python (Join-Path $projectRoot 'tools\p7-r4-soak-validation\build_hevc_codecs.py') $base.codecPrefix
if($LASTEXITCODE -ne 0){throw 'Pinned static x2658+10 build failed.'}
$codecIdentity=Join-Path $workspace 'multidepth-codec-identity.json'
$record=[ordered]@{
    profile='P7-R4-HEVC-v2'; ffmpegPrefix=$base.ffmpegPrefix
    codecPrefix=(Join-Path $workspace 'codecs')
    basePrerequisite=$baseRecord
    basePrerequisiteSha256=(Get-HevcPrerequisiteSha256 $baseRecord)
    codecIdentity=$codecIdentity
    codecIdentitySha256=(Get-HevcPrerequisiteSha256 $codecIdentity)
    sourceDepths=@(8,10); defaultDepth=8; sharedCodecs=$false; cliIncluded=$false
}
$path=Join-Path $workspace "$BuildId-libav-prerequisites.json"
$json=$record|ConvertTo-Json -Depth 6
if(Test-Path -LiteralPath $path){
    # PS5 and PS7 indent JSON differently. Compare every identity field using
    # the same host serializer, without rewriting the existing receipt.
    $existing=Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    if(($existing | ConvertTo-Json -Depth 6).Trim() -ne $json.Trim()){throw 'Existing HEVC-v2 prerequisite identity differs.'}
}else{
    [System.IO.File]::WriteAllText($path,$json+[Environment]::NewLine,[System.Text.UTF8Encoding]::new($false))
}
Write-Output "HEVC_V2_PREREQUISITES=$path"
