@echo off
rem Open the existing UI without rebuilding (developer-only entry point).
rem All logic and Chinese messages live in Start-UiPreview.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-UiPreview.ps1"
