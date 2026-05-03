@echo off
rem ── Headless Unity test runner ──────────────────────────────────────────
rem
rem Usage:
rem   Tools\run-tests.bat            -> EditMode (fast, ~30-60s)
rem   Tools\run-tests.bat PlayMode   -> PlayMode only (loads Main.unity)
rem   Tools\run-tests.bat all        -> EditMode then PlayMode
rem
rem Requires Unity to be CLOSED -- the editor locks Library/, so this script
rem will fail with "Multiple Unity instances cannot open the same project"
rem if the editor is open. That's expected.
rem
rem Override Unity install path with %UNITY_PATH% if 2021.3.16f1 isn't at the
rem default Hub location.
rem
rem Output: TestResults\EditMode.xml / TestResults\PlayMode.xml (NUnit XML).
rem Exit code: 0 on pass, non-zero on any failure.

setlocal
set MODE=%1
if "%MODE%"=="" set MODE=EditMode

set UNITY=%UNITY_PATH%
if "%UNITY%"=="" set UNITY=C:\Program Files\Unity\Hub\Editor\2021.3.16f1\Editor\Unity.exe

set PROJECT=%~dp0..
set RESULTS=%PROJECT%\TestResults

if not exist "%RESULTS%" mkdir "%RESULTS%"

if /i "%MODE%"=="all" (
    call :run EditMode
    if errorlevel 1 exit /b 1
    call :run PlayMode
    if errorlevel 1 exit /b 1
    exit /b 0
)
call :run %MODE%
exit /b %ERRORLEVEL%

:run
echo [run-tests] %1 ...
"%UNITY%" -runTests -batchmode -projectPath "%PROJECT%" ^
    -testPlatform %1 ^
    -testResults "%RESULTS%\%1.xml" ^
    -logFile - ^
    -nographics
exit /b %ERRORLEVEL%
