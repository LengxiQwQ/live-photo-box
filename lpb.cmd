@echo off
REM lpb — Live Photo Box CLI development launcher
REM Usage: lpb [args...]   (e.g. lpb --version, lpb protocols, lpb merge ...)

dotnet run --project "%~dp0LivePhotoBox.CLI" -- %*
