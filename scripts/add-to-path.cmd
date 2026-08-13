@echo off
setlocal EnableExtensions
title Live Photo Box - Add folder to PATH

set "LIVEPHOTO_DIR=%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$dir = $env:LIVEPHOTO_DIR.TrimEnd('\');" ^
  "$marker = Join-Path $dir 'livephotobox-boot.exe';" ^
  "if (-not (Test-Path -LiteralPath $marker)) { Write-Host 'ERROR: livephotobox-boot.exe not found next to this script.' -ForegroundColor Red; Write-Host 'Run this script from the Live Photo Box folder.' -ForegroundColor Red; exit 1 };" ^
  "$p = [Environment]::GetEnvironmentVariable('Path','User');" ^
  "$parts = @();" ^
  "if ($p) { $parts = @($p -split ';' | Where-Object { $_ -ne '' }) };" ^
  "$norm = $dir.ToLowerInvariant().TrimEnd('\');" ^
  "$hit = @($parts | Where-Object { $_.ToLowerInvariant().TrimEnd('\') -eq $norm });" ^
  "if ($hit.Count -gt 0) { Write-Host 'Already in user PATH:' -ForegroundColor Yellow; Write-Host ('  ' + $dir); exit 0 };" ^
  "if ($parts.Count -eq 0) { $new = $dir } else { $new = $dir + ';' + ($parts -join ';') };" ^
  "[Environment]::SetEnvironmentVariable('Path',$new,'User');" ^
  "Write-Host 'ADDED to user PATH (no admin needed):' -ForegroundColor Green;" ^
  "Write-Host ('  ' + $dir) -ForegroundColor White;" ^
  "Write-Host 'Restart your terminal, then run:  livephoto --version' -ForegroundColor Cyan"

set "RC=%ERRORLEVEL%"
echo.
echo Finished. Press any key to close this window...
pause >nul
endlocal & exit /b %RC%
