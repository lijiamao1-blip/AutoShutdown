@echo off
rem Publish launcher (ASCII only). All logic lives in Publish-SafeRelease.ps1.
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish-SafeRelease.ps1"
if errorlevel 1 (
  echo.
  echo Publish failed. Please read the errors above.
  pause
  exit /b 1
)
pause
exit /b 0
