param(
    [ValidateSet('Release')][string]$Configuration = 'Release',
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,31}$')][string]$RecordId = 'iteration-5-bridge-1',
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,31}$')][string]$OutputSet = 'iteration-5-bridge-1'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$workspace = Join-Path $projectRoot '.ai-tmp\workspace\P5-R5'
$buildDir = Join-Path $workspace 'cmake-build'
$artifactDir = Join-Path $projectRoot "artifacts\native\$Configuration\win-x64"
$outputDir = Join-Path $workspace $OutputSet
$source = Join-Path $workspace 'huawei-extraction-pass-b\motion.mp4'
$native = Join-Path $artifactDir 'LivePhotoBox.Native.dll'
$sidecar = Join-Path $artifactDir 'LivePhotoBox.Video.Libav.dll'
$smoke = Join-Path $artifactDir 'lpb_video_sidecar_conversion_smoke.exe'
$log = Join-Path $workspace "$RecordId-video-sidecar-conversion.log"
$expectedSourceHash = '664EF6DBA25D7B04228C79B874A0EDA742B1211E17EE770D0E82E765BD454CE6'
$expectedSourceBytes = 6519404L

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath = [string](& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
if (-not $vsPath) { throw 'Visual Studio MSVC x64 build tools are required for the sidecar conversion smoke.' }
$cmake = Join-Path $vsPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
foreach ($path in @($source, $native, $sidecar, $cmake)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "P5-R5 sidecar conversion prerequisite is missing: $path" }
}
if (Test-Path -LiteralPath $log) { throw "Create-only sidecar conversion log already exists: $log" }
if ((Get-Item -LiteralPath $source).Length -ne $expectedSourceBytes -or
    (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $expectedSourceHash) {
    throw 'The canonical Huawei derived video does not match the frozen P4-v1-derived identity.'
}
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$outputs = @(
    (Join-Path $outputDir 'huawei-hevc.mp4'),
    (Join-Path $outputDir 'huawei-h264.mp4'),
    (Join-Path $outputDir 'huawei-cancelled.mp4'),
    (Join-Path $outputDir 'huawei-missing-sidecar.mp4'))
foreach ($path in $outputs) {
    if (Test-Path -LiteralPath $path) { throw "Create-only conversion output already exists: $path" }
}

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($log, "SOURCE=$source`r`nSOURCE_BYTES=$expectedSourceBytes`r`nSOURCE_SHA256=$expectedSourceHash`r`n", $utf8NoBom)

function ConvertTo-WindowsCommandLineArgument([string]$Value) {
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            ++$backslashes
            continue
        }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * (2 * $backslashes + 1)))
            [void]$builder.Append('"')
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) {
            [void]$builder.Append(('\' * $backslashes))
            $backslashes = 0
        }
        [void]$builder.Append($character)
    }
    if ($backslashes -gt 0) { [void]$builder.Append(('\' * (2 * $backslashes))) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Invoke-Logged([string]$Executable, [string[]]$Arguments) {
    $script:SidecarSmokeExitCode = -1
    $quotedArguments = @($Arguments | ForEach-Object { ConvertTo-WindowsCommandLineArgument ([string]$_) })
    $argumentText = $quotedArguments -join ' '
    [System.IO.File]::AppendAllText($log, "ARGV: $Executable $argumentText`r`n", $utf8NoBom)
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $startInfo.Arguments = $argumentText
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "Could not start logged process: $Executable" }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $script:SidecarSmokeExitCode = $process.ExitCode
    $process.Dispose()
    [System.IO.File]::AppendAllText($log, "STDOUT:`r`n$stdout`r`nSTDERR:`r`n$stderr`r`nEXIT_CODE=$script:SidecarSmokeExitCode`r`n", $utf8NoBom)
    if ($stdout.Length -gt 0) { [Console]::Out.Write($stdout) }
    if ($stderr.Length -gt 0) { [Console]::Error.Write($stderr) }
}

$buildArgs = @('--build', $buildDir, '--config', $Configuration, '--target', 'lpb_video_sidecar_conversion_smoke',
    '--', '/m:1', '/p:CL_MPCount=1', '/p:UseMultiToolTask=false', '/v:minimal')
Invoke-Logged $cmake $buildArgs
if ($script:SidecarSmokeExitCode -ne 0) { throw "Video sidecar conversion smoke target build failed ($script:SidecarSmokeExitCode); see $log" }
if (-not (Test-Path -LiteralPath $smoke -PathType Leaf)) { throw "The conversion smoke executable was not produced: $smoke" }

Invoke-Logged $smoke @('critical-hevc', $source, $outputs[0])
if ($script:SidecarSmokeExitCode -ne 0) { throw "Huawei HEVC-to-HEVC sidecar conversion failed ($script:SidecarSmokeExitCode); see $log" }
Invoke-Logged $smoke @('critical-h264', $source, $outputs[1])
if ($script:SidecarSmokeExitCode -ne 0) { throw "Huawei HEVC-to-H.264 truthful capability route failed ($script:SidecarSmokeExitCode); see $log" }
Invoke-Logged $smoke @('cancel', $source, $outputs[2])
if ($script:SidecarSmokeExitCode -ne 0) { throw "Huawei sidecar cancellation cleanup failed ($script:SidecarSmokeExitCode); see $log" }

$missingScript = Join-Path $PSScriptRoot 'test-p5-r5-missing-sidecar.ps1'
$missingArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $missingScript,
    '-Configuration', $Configuration, '-RecordId', $RecordId, '-OutputSet', $OutputSet)
Invoke-Logged 'powershell.exe' $missingArgs
if ($script:SidecarSmokeExitCode -ne 0) { throw "Huawei missing-sidecar conversion failed ($script:SidecarSmokeExitCode); see $log" }

$finalSourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
if ($finalSourceHash -ne $expectedSourceHash -or (Get-Item -LiteralPath $source).Length -ne $expectedSourceBytes) {
    throw 'The canonical Huawei derived video changed during sidecar conversion testing.'
}
[System.IO.File]::AppendAllText($log, "FINAL_SOURCE_SHA256=$finalSourceHash`r`n", $utf8NoBom)
foreach ($path in @($outputs[0], $outputs[1])) {
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        [System.IO.File]::AppendAllText($log, "OUTPUT=$path`r`nOUTPUT_SHA256=$hash`r`n", $utf8NoBom)
    }
}
Write-Output 'EXIT_CODE=0'
Write-Output "LOG=$log"
