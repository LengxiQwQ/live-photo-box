@echo off
REM livephotobox - Live Photo Box CLI (development build)
REM Usage: livephotobox [args...]   (aliases: lpb)
REM Dev alias: always runs this repo's LivePhotoBox.CLI project, never an installed copy.

dotnet run --project "%~dp0LivePhotoBox.CLI\LivePhotoBox.CLI.csproj" -- %*
