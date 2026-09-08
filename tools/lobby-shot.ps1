<#
    Photograph a lobby of N players without ever starting the match.

    The lobby is where the podiums and name plates live, and the match harness leaves it
    within seconds of the last copy arriving - so the one screen that needs looking at is
    the one no test ever held still. This holds it: the host is told to expect one more
    player than will ever arrive, so it waits, and the lobby stays up to be photographed.

        .\tools\lobby-shot.ps1 -Players 8
        .\tools\lobby-shot.ps1 -Players 5 -Hold 90

    Shots land in %LOCALAPPDATA%\LiarsBar8P\shots\<run>, and the folder is printed at the end.
#>
param(
    [int]    $Players = 8,
    [int]    $Hold = 60,           # seconds to sit in the lobby taking pictures
    [string] $Map = '',
    [switch] $KeepHud             # leave the developer overlay visible
)

$ErrorActionPreference = 'Stop'

$Game = "${env:ProgramFiles(x86)}\Steam\steamapps\common\Liar's Bar"
$Exe  = "$Game\Liar's Bar.exe"
$Logs = "$env:LOCALAPPDATA\LiarsBar8P\logs"

if (-not (Test-Path $Exe)) { Write-Host "Game not found at $Game" -ForegroundColor Red; exit 1 }

function Stop-Copies {
    Get-Process -Name "Liar's Bar" -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
    $waited = 0
    while ((Get-Process -Name "Liar's Bar" -ErrorAction SilentlyContinue) -and $waited -lt 30) {
        Start-Sleep -Seconds 1; $waited++
    }
}

function Wait-ForLine {
    param([string] $Path, [string] $Pattern, [int] $TimeoutSec, [int] $ProcId)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ($ProcId -and -not (Get-Process -Id $ProcId -ErrorAction SilentlyContinue)) { return $false }
        if ((Test-Path $Path) -and (Select-String -Path $Path -Pattern $Pattern -SimpleMatch -Quiet -ErrorAction SilentlyContinue)) { return $true }
        Start-Sleep -Milliseconds 750
    }
    return $false
}

Stop-Copies
Remove-Item "$Logs\*.log" -Force -ErrorAction SilentlyContinue

$env:SteamAppId           = '3097560'
$env:LIARSBAR8P_PORT      = '7777'
$env:LIARSBAR8P_MODE      = ''
$env:LIARSBAR8P_DECKMODE  = ''
$env:LIARSBAR8P_DICEMODE  = ''
$env:LIARSBAR8P_MAP       = $Map
$env:LIARSBAR8P_TURNPROBE = ''
$env:LIARSBAR8P_CLEAN_SHOTS = if ($KeepHud) { '' } else { '1' }

# One more than will ever turn up: the host readies nobody and starts nothing.
$env:LIARSBAR8P_ROLE     = 'host'
$env:LIARSBAR8P_LOOPBACK = 'host'
$env:LIARSBAR8P_EXPECT   = "$($Players + 1)"

$hostProc = Start-Process -FilePath $Exe -WorkingDirectory $Game -PassThru
$hostLog  = "$Logs\host-$($hostProc.Id).log"

if (-not (Wait-ForLine -Path $hostLog -Pattern '[loopback] hosting on' -TimeoutSec 180 -ProcId $hostProc.Id)) {
    Write-Host "host never reached its lobby" -ForegroundColor Red; Stop-Copies; exit 1
}

$env:LIARSBAR8P_LOOPBACK = 'client'
$env:LIARSBAR8P_EXPECT   = ''

for ($i = 2; $i -le $Players; $i++) {
    $env:LIARSBAR8P_ROLE = "c$i"
    $c = Start-Process -FilePath $Exe -WorkingDirectory $Game -PassThru `
         -ArgumentList @('-screen-width', '640', '-screen-height', '400', '-screen-fullscreen', '0')
    if (-not (Wait-ForLine -Path "$Logs\c$i-$($c.Id).log" -Pattern '[loopback] joining' -TimeoutSec 180 -ProcId $c.Id)) {
        Write-Host "copy $i never connected" -ForegroundColor Red; Stop-Copies; exit 1
    }

    # Connected is not the same as registered, and the difference is most of a minute for a
    # copy that is still loading. Waiting on the joining line alone photographed a lobby of
    # eight that only had seven people in it, and blamed the mod for the missing name plate.
    if (-not (Wait-ForLine -Path $hostLog -Pattern "($i in the lobby now)" -TimeoutSec 180 -ProcId $hostProc.Id)) {
        Write-Host "copy $i connected but never reached the host's roster" -ForegroundColor Red
        Stop-Copies; exit 1
    }
    Write-Host "  copy $i of $Players in" -ForegroundColor DarkGray
}

Write-Host "all $Players in the lobby - holding for $Hold s" -ForegroundColor Green
Start-Sleep -Seconds $Hold

$folder = (Select-String -Path $hostLog -Pattern 'screenshots go to (.+)$' | Select-Object -Last 1)
Stop-Copies

if ($folder -and $folder.Matches[0].Groups[1].Value) {
    $dir = $folder.Matches[0].Groups[1].Value.Trim()
    Write-Host "shots: $dir" -ForegroundColor Cyan
    Get-ChildItem $dir -ErrorAction SilentlyContinue | Select-Object -Last 5 Name, Length | Format-Table -AutoSize
} else {
    Write-Host "no screenshots were taken - is ScreenshotEverySeconds still 0 in the config?" -ForegroundColor Yellow
}
