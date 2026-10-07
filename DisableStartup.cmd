@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0DisableStartup.ps1"
if errorlevel 1 pause
