$ErrorActionPreference = 'Stop'
foreach ($drive in @('C', 'D')) {
    $volume = Get-PSDrive -Name $drive -PSProvider FileSystem
    Write-Host "$drive free bytes: $($volume.Free)"
    if ($volume.Free -lt 2GB) { throw "$drive has less than 2 GiB free" }
    $probe = Join-Path $volume.Root 'codeql-storage-probe.bin'
    $bytes = [Text.Encoding]::UTF8.GetBytes('CodeQL storage read/write probe')
    $file = [IO.File]::Open($probe, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $file.Write($bytes); $file.Flush($true); $file.Position = 0
        $read = [byte[]]::new($bytes.Length)
        $file.ReadExactly($read)
        if ([Convert]::ToHexString($read) -ne [Convert]::ToHexString($bytes)) {
            throw "$drive storage probe did not round-trip"
        }
    } finally { $file.Dispose(); Remove-Item -LiteralPath $probe }
}
Get-CimInstance Win32_OperatingSystem | Select-Object TotalVisibleMemorySize, FreePhysicalMemory | Format-List
