# The whole matrix, in the order that finds problems soonest.
#
# Every mode a player can actually choose from the lobby arrows, at every table size from one
# to eight, and then the same thing again in each of the other three bars. One results file,
# appended to as it goes, so a sweep that is stopped half way is still worth reading.
#
# The order is deliberate, and it is by table size rather than by mode. A cell takes about ten
# minutes, so a sweep that ran every size of one mode before starting the next would not reach
# Liar's Spin at all until five hours in - and if something there is broken at eight players,
# five hours is a long time to have been finding out. Size first means every mode is seen at
# eight players inside the first hour, which is where this mod differs from the game and where
# a problem is most likely to be waiting.
#
# Big tables before small ones for the same reason: below five players the mod is supposed to
# stay out of the way, and that is a weaker claim to test than the one it exists to make. The
# bars come last, because the bar changes where the chairs are and nothing else - every run
# already measures the seat ring, so a wrong table would have shown up earlier as well.
#
# Poker and the standalone Chaos mode are not here. The lobby's mode arrows cycle Liar's Deck,
# Texas, Dice and Spin and nothing else, so neither is reachable by a player; the harness can
# force them, and testing what nobody can select would pad the matrix rather than fill it.

param(
    [string] $Results = "$env:LOCALAPPDATA\LiarsBar8P\matrix",
    [switch] $SkipBig,
    [switch] $SkipSmall,
    [switch] $SkipBars
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$matrix = Join-Path $here 'matrix-test.ps1'

# Every table a player can pick from the lobby. Three deck variants, not four - the deck
# arrow cycles 0, 1, 2 and resets, so what was filed as a fourth was Basic run twice.
$All = @('deck0', 'deck1', 'deck2', 'dice0', 'dice1', 'texas', 'spin')

# Two tables for the bar sweep: an ordinary deck and the one with the aiming phase in it.
$PerBar = @('deck0', 'deck2')

$started = Get-Date
Write-Host "sweep started $started" -ForegroundColor Cyan
Write-Host "results -> $Results" -ForegroundColor DarkGray

function Phase {
    param([string] $What, [string[]] $Tables, [int[]] $Sizes, [int] $Seconds, [string] $Map, [switch] $First)

    Write-Host ""
    Write-Host "================ $What ================" -ForegroundColor Magenta

    # Not $args: that is an automatic variable, and splatting it would be splatting
    # whatever PowerShell had already put there.
    $opt = @{
        Tables      = $Tables
        Sizes       = $Sizes
        PlaySeconds = $Seconds
        Results     = $Results
        Map         = $Map
    }
    if (-not $First) { $opt['Append'] = $true }

    & $matrix @opt
}

$first = $true

if (-not $SkipBig) {
    # Bar 0, the sizes the game was never built for - every mode at each size before moving
    # down, so a mode that is broken at eight says so in the first hour rather than the fifth.
    foreach ($n in 8, 7, 6, 5) {
        Phase -What "$n players, every mode, bar 0" -Tables $All -Sizes @($n) `
              -Seconds 150 -Map '0' -First:$first
        $first = $false
    }
}

if (-not $SkipSmall) {
    # Bar 0, one to four players: here the mod is supposed to be invisible, and that is the
    # claim being tested rather than the one it exists to make.
    foreach ($n in 4, 3, 2, 1) {
        Phase -What "$n player(s), every mode, bar 0" -Tables $All -Sizes @($n) `
              -Seconds 100 -Map '0' -First:$first
        $first = $false
    }
}

if (-not $SkipBars) {
    foreach ($map in @('1', '2', '3')) {
        Phase -What "bar $map, eight players" -Tables $PerBar -Sizes @(8) `
              -Seconds 150 -Map $map -First:$first
        $first = $false
    }

    # One smaller table per bar as well: the seat ring has to re-space the chairs when the
    # table is not full, and that is a different piece of arithmetic from a full ring.
    foreach ($map in @('1', '2', '3')) {
        Phase -What "bar $map, five players" -Tables @('deck0') -Sizes @(5) `
              -Seconds 130 -Map $map
    }
}

$took = (Get-Date) - $started
Write-Host ""
Write-Host ("sweep finished in {0:hh\:mm\:ss}" -f $took) -ForegroundColor Cyan

$csv = Join-Path $Results 'summary.csv'
if (Test-Path $csv) {
    $rows = @(Import-Csv $csv)
    Write-Host "$($rows.Count) cells recorded" -ForegroundColor Cyan

    $bad = $rows | Where-Object {
        $_.Driving -ne 'True' -or $_.ModeOk -ne 'True' -or
        [int]$_.ModErrors -gt 0 -or [int]$_.Exceptions -gt 0 -or
        [int]$_.SeatWrong -gt 0 -or [int]$_.AimBad -gt 0 -or
        $_.AimRing -like 'BLIND*' -or $_.TurnRing -like 'ONLY*'
    }

    if ($bad) {
        Write-Host "$($bad.Count) cell(s) want looking at:" -ForegroundColor Yellow
        $bad | Select-Object Table, Players, Bar, Driving, ModeOk, SeatWrong, AimRing, AimBad,
                             TurnRing, Exceptions, ModErrors, Notes |
               Format-Table -AutoSize | Out-String -Width 220 | Write-Host
    } else {
        Write-Host "nothing flagged" -ForegroundColor Green
    }
}
