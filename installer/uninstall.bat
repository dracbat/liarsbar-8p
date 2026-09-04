@echo off
setlocal
title Liar's Bar - 8 Player Mod Uninstaller

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrator permission...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"

echo.
pause
endlocal
