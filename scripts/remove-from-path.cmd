@echo off
setlocal EnableExtensions
title Live Photo Box - Remove from PATH

set "LIVEPHOTO_DIR=%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$dir = $env:LIVEPHOTO_DIR.TrimEnd('\');" ^
  "$p = [Environment]::GetEnvironmentVariable('Path','User');" ^
  "$parts = @();" ^
  "if ($p) { $parts = @($p -split ';' | Where-Object { $_ -ne '' }) };" ^
  "$norm = $dir.ToLowerInvariant().TrimEnd('\');" ^
  "$removed = @($parts | Where-Object { $_.ToLowerInvariant().TrimEnd('\') -eq $norm });" ^
  "if ($removed.Count -eq 0) { Write-Host 'Not found in user PATH - nothing to remove.' -ForegroundColor Yellow; exit 0 };" ^
  "$kept = @($parts | Where-Object { $_.ToLowerInvariant().TrimEnd('\') -ne $norm });" ^
  "$new = $kept -join ';';" ^
  "[Environment]::SetEnvironmentVariable('Path',$new,'User');" ^
  "Write-Host ('REMOVED from user PATH (' + $removed.Count + ' entry):') -ForegroundColor Green;" ^
  "Write-Host ('  ' + $dir) -ForegroundColor White;" ^
  "Write-Host 'Restart your terminal for it to take effect.' -ForegroundColor Cyan"

set "RC=%ERRORLEVEL%"
echo.
echo Finished. Press any key to close this window...
pause >nul
endlocal & exit /b %RC%
