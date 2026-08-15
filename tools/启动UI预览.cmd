@echo off
rem Launch UI preview with rebuild (developer-only entry point).
rem All logic and Chinese messages live in Start-UiPreview.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-UiPreview.ps1" -Build
