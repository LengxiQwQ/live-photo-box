@echo off
REM lpb - alias for livephotobox (development build)
REM Usage: lpb [args...]
REM Dev alias: always runs this repo's LivePhotoBox.CLI project, never an installed copy.

dotnet run --project "%~dp0LivePhotoBox.CLI\LivePhotoBox.CLI.csproj" -- %*
