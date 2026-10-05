@echo off
setlocal
title Liar's Bar - 8 Player Mod Installer

rem The game lives under Program Files, so writing to it needs administrator rights.
rem
rem This file's own path reaches PowerShell through the environment, not pasted into its
rem command. Pasted in, it sat inside a quoted string, and an apostrophe in it - a folder
rem like C:\Users\O'Brien, or the zip unpacked into the game's own Liar's Bar folder -
rem ended the string early, and the window closed on a PowerShell error.
set "LB8P_SELF=%~f0"

rem fltmc answers only to an administrator. This used to ask "net session", which also
rem fails when Windows' Server service is switched off, administrator or not - so every
rem elevated copy saw itself as not elevated and started another, with no prompt to stop
rem it, for ever. The relaunched copy is now marked, and asks only once.
fltmc >nul 2>&1
if %errorLevel% neq 0 (
    if /i "%~1"=="/elevated" (
        echo Could not get administrator rights, so nothing was installed.
        echo Right click install.bat and choose "Run as administrator".
        echo.
        pause
        exit /b 1
    )
    echo Requesting administrator permission...
    powershell -NoProfile -Command "Start-Process -FilePath $env:LB8P_SELF -ArgumentList '/elevated' -Verb RunAs"
    exit /b
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"

echo.
pause
endlocal
