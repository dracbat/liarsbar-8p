@echo off
setlocal
title Liar's Bar - 8 Player Mod Uninstaller

rem The game lives under Program Files, so removing files from it needs administrator
rem rights. The elevation check is the same as install.bat's, for the same reasons: this
rem file's path goes to PowerShell through the environment so an apostrophe in it cannot
rem break the command, and fltmc rather than "net session" decides who is an
rem administrator, with the relaunched copy marked so it can never relaunch again.
set "LB8P_SELF=%~f0"

fltmc >nul 2>&1
if %errorLevel% neq 0 (
    if /i "%~1"=="/elevated" (
        echo Could not get administrator rights, so nothing was removed.
        echo Right click uninstall.bat and choose "Run as administrator".
        echo.
        pause
        exit /b 1
    )
    echo Requesting administrator permission...
    powershell -NoProfile -Command "Start-Process -FilePath $env:LB8P_SELF -ArgumentList '/elevated' -Verb RunAs"
    exit /b
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"

echo.
pause
endlocal
