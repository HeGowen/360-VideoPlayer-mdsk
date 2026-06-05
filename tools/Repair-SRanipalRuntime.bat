@echo off
set SCRIPT_DIR=%~dp0
powershell.exe -ExecutionPolicy Bypass -NoProfile -File "%SCRIPT_DIR%Repair-SRanipalRuntime.ps1"
