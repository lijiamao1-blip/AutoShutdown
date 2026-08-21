@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Start-AutoShutdownUiTest.ps1"
set "launchExitCode=%ERRORLEVEL%"
if not "%launchExitCode%"=="0" (
    echo.
    echo Launch failed. See the error and log path above.
    pause
)
exit /b %launchExitCode%
