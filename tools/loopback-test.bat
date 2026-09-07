@echo off
setlocal
rem ---------------------------------------------------------------------------
rem  Run several real copies of Liar's Bar against each other on this machine.
rem
rem  Not bots. Each copy is a separate process with its own Mirror connection
rem  over TCP loopback, so everything that happens between machines - messages
rem  to one client, ownership, the client half of a round - actually happens.
rem  Steam cannot do this: every copy signs into the same account and cannot be
rem  several distinct members of one lobby.
rem
rem    loopback-test.bat        five players
rem    loopback-test.bat 8      eight players (needs about 48 GB of memory free)
rem
rem  Each copy writes its own log to %LOCALAPPDATA%\LiarsBar8P\logs.
rem  Close them all with:  taskkill /IM "Liar's Bar.exe" /F
rem ---------------------------------------------------------------------------

set PLAYERS=%1
if "%PLAYERS%"=="" set PLAYERS=5

set GAME=%ProgramFiles(x86)%\Steam\steamapps\common\Liar's Bar
if not exist "%GAME%\Liar's Bar.exe" (
    echo Could not find the game at "%GAME%".
    exit /b 1
)

set SteamAppId=3097560
set LIARSBAR8P_PORT=7777

echo Clearing old logs...
if exist "%LOCALAPPDATA%\LiarsBar8P\logs" del /q "%LOCALAPPDATA%\LiarsBar8P\logs\*.log" >nul 2>&1

echo Starting the host, waiting for %PLAYERS% players...
set LIARSBAR8P_ROLE=host
set LIARSBAR8P_LOOPBACK=host
set LIARSBAR8P_EXPECT=%PLAYERS%
start "" /D "%GAME%" "%GAME%\Liar's Bar.exe"

rem The host needs to reach its lobby before anyone knocks.
timeout /t 45 /nobreak >nul

set LIARSBAR8P_LOOPBACK=client
set LIARSBAR8P_EXPECT=
set /a JOINERS=%PLAYERS%-1

for /l %%i in (2,1,%PLAYERS%) do (
    echo   joining copy %%i of %PLAYERS%...
    set LIARSBAR8P_ROLE=c%%i
    rem Small windows: these are here to be a connection, not to be watched.
    start "" /D "%GAME%" "%GAME%\Liar's Bar.exe" -screen-width 640 -screen-height 400 -screen-fullscreen 0
    timeout /t 30 /nobreak >nul
)

echo.
echo All %PLAYERS% copies launched. The host readies everyone and starts the
echo match on its own once they have all arrived.
echo.
echo Logs:  %LOCALAPPDATA%\LiarsBar8P\logs
echo Close: taskkill /IM "Liar's Bar.exe" /F
endlocal
