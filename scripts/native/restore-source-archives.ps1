# Recover fixed production build inputs; no historical validation workspace is read.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build-environment.ps1')
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$downloads = Join-Path $projectRoot 'artifacts\cache\downloads'
New-Item -ItemType Directory -Force $downloads | Out-Null
$archives = @(
    @{ Name='ffmpeg-n9.0.1.tar.gz'; Url='https://github.com/FFmpeg/FFmpeg/archive/refs/tags/n9.0.1.tar.gz'; Algorithm='SHA256'; Hash='195D54BEBE1A27F84D77F4B989D193466F305B355DA92292766A69F16880B18A' },
    @{ Name='x264-b35605ace3ddf7c1a5d67a2eb553f034aef41d55.tar.gz'; Url='https://github.com/mirror/x264/archive/b35605ace3ddf7c1a5d67a2eb553f034aef41d55.tar.gz'; Algorithm='SHA512'; Hash='BFAC118DA55DA2FCC4587B17A3184D9ED70D6E03188BC0497F5922DF22B5685FA49FA4325F9C5196C76D03E6E3F56D5906475C540D0DF7E1739DC4182816EEBE' }
)
foreach ($archive in $archives) {
    $path = Join-Path $downloads $archive.Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $partial = "$path.download"
        $checkedInSource = Join-Path $projectRoot ('build\native\video\sources\' + $archive.Name)
        if (-not (Test-Path -LiteralPath $checkedInSource -PathType Leaf)) { throw "Missing checked-in pinned source: $checkedInSource" }
        Copy-Item -LiteralPath $checkedInSource -Destination $partial
        if ((Get-FileHash -LiteralPath $partial -Algorithm $archive.Algorithm).Hash -ne $archive.Hash) {
            throw "Downloaded source archive hash mismatch: $partial"
        }
        Move-Item -LiteralPath $partial -Destination $path
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm $archive.Algorithm).Hash -ne $archive.Hash) {
        throw "Source cache is invalid; refusing it: $path"
    }
}
