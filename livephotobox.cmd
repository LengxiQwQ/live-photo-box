@echo off
REM livephotobox — Live Photo Box CLI
REM Usage: livephotobox [args...]   (aliases: lpb)

dotnet run --project "%~dp0LivePhotoBox.CLI" -- %*
