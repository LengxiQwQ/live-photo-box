@echo off
rem ============================================================
rem  cli-color-demo.cmd - Live Photo Box CLI color demo
rem
rem  Runs every command that renders colored output so you can
rem  inspect the full color design in a real terminal:
rem    [1]   lpb --version                (title, version)
rem    [2]   lpb --info                    (labels, tools, feedback)
rem    [3]   lpb protocols                (table, marks, legend)
rem    [4]   lpb update-check             (network check)
rem    [5]   lpb merge --help             (grouped colored help)
rem    [5a]  lpb protocols --help
rem    [5b]  lpb --help                   (root help with global flags)
rem    [5c]  lpb update-check --help
rem    [5d]  lpb backend                (backend fields and status table)
rem    [6]   merge dry-run                 (single-pair summary)
rem    [7]   merge error path              (red error output)
rem    [8]   merge batch dry-run           (batch scan summary)
rem    [9]   lpb repair --help             (grouped colored help)
rem    [10]  repair single --dry-run       (diagnosis summary)
rem    [11]  repair batch --dry-run        (scan + repair list)
rem    [12]  repair error path             (red error output)
rem    [13]  cover --help                  (grouped colored help)
rem    [14]  cover single view (v1)        (photo/protocol/size/duration/cover)
rem    [15]  cover single view (v2)        (photo/protocol/size/duration/cover)
rem    [16]  cover single dry-run          (current + new cover)
rem    [17]  cover dual view (Apple)       (photo + video)
rem    [18]  cover dual dry-run            (photo + video + new cover)
rem    [19]  cover OPPO edited view        (original + current cover)
rem    [20]  cover OPPO dry-run            (original + new current cover)
rem    [21]  cover error path              (red error output)
rem    [22]  split --help                  (grouped colored help)
rem    [23]  split single dry-run          (scan/summary colors)
rem    [24]  split error path              (red error output)
rem
rem  Usage:
rem    cli-color-demo.cmd                 use project sample assets
rem    cli-color-demo.cmd IMAGE VIDEO     use your own image+video pair
rem ============================================================
setlocal
chcp 65001 >nul

set "ROOT=%~dp0..\.."
set "EXE=%ROOT%\LivePhotoBox.CLI\bin\Debug\net9.0-windows10.0.19041.0\livephotobox-boot.exe"

if not exist "%EXE%" (
    echo ERROR: CLI not built yet. Run:
    echo   dotnet build LivePhotoBox.CLI\LivePhotoBox.CLI.csproj
    exit /b 1
)

rem --- test assets: command-line args > %%TEMP%%\lpb-demo > project samples ---
if not "%~1"=="" (
    set "DEMO_IMG=%~1"
    set "DEMO_VID=%~2"
) else if exist "%TEMP%\lpb-demo\demo.HEIC" (
    set "DEMO_IMG=%TEMP%\lpb-demo\demo.HEIC"
    set "DEMO_VID=%TEMP%\lpb-demo\demo.MOV"
) else (
set "DEMO_IMG=%ROOT%\sample-assets-backup\Samples\Merge\merge_sample_01.JPG"
set "DEMO_VID=%ROOT%\sample-assets-backup\Samples\Merge\merge_sample_01.MOV"
)
set "BATCH_DIR=%ROOT%\sample-assets-backup\Samples\Merge"

echo ============================================================
echo  [1] lpb --version
echo ============================================================
"%EXE%" --version
echo.

echo ============================================================
echo  [2] lpb --info
echo ============================================================
"%EXE%" --info
echo.

echo ============================================================
echo  [3] lpb protocols
echo ============================================================
"%EXE%" protocols
echo.

echo ============================================================
echo  [4] lpb update-check
echo ============================================================
"%EXE%" update-check
echo.

echo ============================================================
echo  [5] lpb merge --help
echo ============================================================
"%EXE%" merge --help
echo.

echo ============================================================
echo  [5a] lpb protocols --help
echo ============================================================
"%EXE%" protocols --help
echo.

echo ============================================================
echo  [5b] lpb --help  (root help)
echo ============================================================
"%EXE%" --help
echo.

echo ============================================================
echo  [5c] lpb update-check --help
echo ============================================================
"%EXE%" update-check --help
echo.

echo ============================================================
echo  [5d] lpb backend
echo ============================================================
"%EXE%" backend
echo.

echo ============================================================
echo  [6] merge single pair --dry-run  (confirmation summary)
echo ============================================================
"%EXE%" merge "%DEMO_IMG%" "%DEMO_VID%" --key-timestamp 2.5 --dry-run
echo.

echo ============================================================
echo  [7] merge error path  (should be red)
echo ============================================================
"%EXE%" merge "%DEMO_IMG%" "%DEMO_VID%" -p nosuchproto
echo.

echo ============================================================
echo  [8] merge batch --dry-run  (scan summary)
echo ============================================================
"%EXE%" merge -d "%BATCH_DIR%" --dry-run
echo.

echo ============================================================
echo  [9] repair --help  (grouped colored help)
echo ============================================================
"%EXE%" repair --help
echo.

echo ============================================================
echo  [10] repair single file --dry-run  (diagnosis summary)
echo ============================================================
"%EXE%" repair "%DEMO_IMG%" --dry-run --all-devices
echo.

echo ============================================================
echo  [11] repair batch --dry-run  (scan + repair list)
echo ============================================================
"%EXE%" repair -d "%BATCH_DIR%" --dry-run --all-devices
echo.

echo ============================================================
echo  [12] repair error path  (should be red)
echo ============================================================
"%EXE%" repair "%DEMO_IMG%" --no-rotate --no-thumbnail --no-heic --no-video
echo.

echo ============================================================
echo  [13-24] cover + split color demo
echo ============================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0cli-color-demo-cover.ps1" -Exe "%EXE%"
echo.

echo.
echo All done. Press any key to close this window.
pause >nul
endlocal
