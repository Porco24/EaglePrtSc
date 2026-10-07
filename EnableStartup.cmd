@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0EnableStartup.ps1"
if errorlevel 1 (
  pause
  exit /b 1
)
start "" "%~dp0EaglePrtSc.exe"
