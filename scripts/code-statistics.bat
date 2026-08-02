@echo off
cd /d "%~dp0\.."

where cloc >nul 2>nul
if errorlevel 1 (
    if exist "%USERPROFILE%\.local\bin\cloc.exe" (
        set "CLOC_CMD=%USERPROFILE%\.local\bin\cloc.exe"
    ) else (
        echo [ERROR] cloc not found. Please install it first.
        echo.
        pause
        exit /b 1
    )
) else (
    set "CLOC_CMD=cloc"
)

echo Counting lines of code...
echo.

%CLOC_CMD% "LivePhotoBox" "LivePhotoBox.Core" "LivePhotoBox.CLI" --exclude-dir=bin,obj,Tools > "%TEMP%\cloc.tmp"
type "%TEMP%\cloc.tmp"

for /f "tokens=3-5" %%a in ('findstr /b "SUM:" "%TEMP%\cloc.tmp"') do set /a _total=%%a+%%b+%%c
del "%TEMP%\cloc.tmp" 2>nul

echo.
call echo Total physical lines (code + comment + blank): %_total%
echo.
echo Done!
pause
