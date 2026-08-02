@echo off
REM lpb — alias for livephotobox
REM Usage: lpb [args...]

dotnet run --project "%~dp0LivePhotoBox.CLI" -- %*
