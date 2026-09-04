@echo off
setlocal
title Liar's Bar - 8 Player Mod Installer

rem The game lives under Program Files, so writing to it needs administrator rights.
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrator permission...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"

echo.
pause
endlocal
