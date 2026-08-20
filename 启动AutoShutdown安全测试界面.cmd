@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Start-AutoShutdownUiTest.ps1"
exit /b %ERRORLEVEL%
