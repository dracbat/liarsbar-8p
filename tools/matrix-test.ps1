<#
    Play one table per (mode, player count) and report what happened.

    The old harness launched copies on a stopwatch - forty-five seconds for the host, thirty
    for each joiner - which is most of five minutes before a table of eight even forms, and
    is wrong in both directions: too long when the machine is quick, and not long enough when
    it is busy. This waits for the log line that says the copy is actually ready, so a run
    takes as long as it takes and no longer.

    A "table" here is a game mode together with its deck or dice variant, because the lobby's
    left and right arrows choose between genuinely different games - one of the deck variants
    is dealt by an entirely separate manager with its own copy of the deal.

    Each run gets its own folder of logs and a verdict drawn from them, so a matrix of tables
    against sizes can be left to run and read afterwards.

        .\tools\matrix-test.ps1                             every table, 5 to 8
        .\tools\matrix-test.ps1 -Tables deck0 -Sizes 8      one table
        .\tools\matrix-test.ps1 -PlaySeconds 240            give each round longer

    Close everything with:  taskkill /IM "Liar's Bar.exe" /F
#>
param(
    [string[]] $Tables = @('deck0', 'deck1', 'deck2', 'dice0', 'dice1', 'texas', 'spin', 'poker', 'chaos'),
    [int[]]    $Sizes = @(5, 6, 7, 8),
    [int]      $PlaySeconds = 120,
    [int]      $LaunchTimeout = 180,
    [string]   $Map = '',            # 0-3: the bar. Blank leaves whatever the machine last used.
    [int]      $Shots = 0,           # seconds between screenshots on the host; 0 takes none
    [switch]   $TurnProbe,           # hand the turn round on purpose and report which seats it reached
    [string]   $Results = "$env:LOCALAPPDATA\LiarsBar8P\matrix",
    [switch]   $Append
)

$ErrorActionPreference = 'Stop'

# name -> game mode, deck variant, dice variant. A blank variant leaves the lobby's own.
$Catalogue = @{
    deck0 = @{ Mode = 'LiarsDeck';  Deck = '0'; Dice = '' }
    deck1 = @{ Mode = 'LiarsDeck';  Deck = '1'; Dice = '' }
    deck2 = @{ Mode = 'LiarsDeck';  Deck = '2'; Dice = '' }
    # There is no deck3. The lobby reports four deck variants and the arrow cycles three of
    # them: ChangeGameModeDeckRight increments, then resets to zero the moment the value
    # reaches 3. A cell asking for it silently played Basic a second time and was filed as a
    # fourth variant, which is a row of results about a table that does not exist.
    dice0 = @{ Mode = 'LiarsDice';  Deck = '';  Dice = '0' }
    dice1 = @{ Mode = 'LiarsDice';  Deck = '';  Dice = '1' }
    texas = @{ Mode = 'LiarsTexas'; Deck = '';  Dice = '' }
    spin  = @{ Mode = 'LiarsSpin';  Deck = '';  Dice = '' }
    poker = @{ Mode = 'LiarsPoker'; Deck = '';  Dice = '' }
    chaos = @{ Mode = 'LiarsChaos'; Deck = '';  Dice = '' }
}

# What the census should call each of these once the match is running. The mode is chosen by
# pressing the lobby arrow, and that arrow does more than set a number - the deck variants are
# three different games and one of them switches the game mode outright. The lobby also keeps
# whatever it was last left on, so a run can quietly inherit the previous cell's table. Asking
# the players what they are actually holding, and checking it against what was asked for, is
# the only way to know a cell tested the mode it is filed under.
$Expected = @{
    deck0 = 'Basic variant'; deck1 = 'Devil variant'; deck2 = 'Chaos Deck'
    dice0 = 'Dice';  dice1 = 'Dice';  texas = 'Texas'
    spin  = 'Spin';  poker = 'Poker'; chaos = 'Chaos Deck'
}

$Game = "${env:ProgramFiles(x86)}\Steam\steamapps\common\Liar's Bar"
$Exe  = "$Game\Liar's Bar.exe"
$Logs = "$env:LOCALAPPDATA\LiarsBar8P\logs"

if (-not (Test-Path $Exe)) { Write-Host "Game not found at $Game" -ForegroundColor Red; exit 1 }

New-Item -ItemType Directory -Force -Path $Results | Out-Null

function Stop-Copies {
    # Stop-Process rather than taskkill: redirecting a native tool's stderr in Windows
    # PowerShell turns "no such process" - the ordinary case - into a terminating error.
    Get-Process -Name "Liar's Bar" -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill() } catch { }
    }
    $waited = 0
    while ((Get-Process -Name "Liar's Bar" -ErrorAction SilentlyContinue) -and $waited -lt 30) {
        Start-Sleep -Seconds 1; $waited++
    }
}

# Wait for a line to appear in a copy's own log. Returns $true if it turned up in time.
function Wait-ForLine {
    param([string] $Path, [string] $Pattern, [int] $TimeoutSec, [int] $ProcId)

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ($ProcId -and -not (Get-Process -Id $ProcId -ErrorAction SilentlyContinue)) { return $false }
        if (Test-Path $Path) {
            if (Select-String -Path $Path -Pattern $Pattern -SimpleMatch -Quiet -ErrorAction SilentlyContinue) { return $true }
        }
        Start-Sleep -Milliseconds 750
    }
    return $false
}

function Start-Copy {
    param([string] $Role, [string] $LoopbackRole, [int] $Expect, [hashtable] $Table, [switch] $Small)

    # The developer tools drive every seat, and they live behind a BepInEx config setting that
    # a release build rewrites to its shipped default. A run with them off throws no cards,
    # takes no turns and reports zeroes in every column - which is indistinguishable from a
    # mode that works. Said here instead, where it cannot be reset by an install.
    $env:LIARSBAR8P_DEV      = '1'
    # Only the host takes pictures, and only when asked - a shot is a few megabytes and a
    # visible pause, and eight copies all doing it would change what is being measured.
    $env:LIARSBAR8P_SHOTSEVERY = if ($Shots -gt 0 -and $LoopbackRole -eq 'host') { "$Shots" } else { '' }
    $env:SteamAppId          = '3097560'
    $env:LIARSBAR8P_PORT     = '7777'
    $env:LIARSBAR8P_ROLE     = $Role
    $env:LIARSBAR8P_LOOPBACK = $LoopbackRole
    $env:LIARSBAR8P_MODE     = $Table.Mode
    $env:LIARSBAR8P_DECKMODE = $Table.Deck
    $env:LIARSBAR8P_DICEMODE = $Table.Dice
    $env:LIARSBAR8P_MAP      = $Map
    $env:LIARSBAR8P_EXPECT   = if ($Expect -gt 0) { "$Expect" } else { '' }

    # Only the host drives the turn probe, and only when asked. A client running it would
    # be talking to itself: the turn is the server's to give.
    $env:LIARSBAR8P_TURNPROBE = if ($TurnProbe -and $LoopbackRole -eq 'host') { '1' } else { '' }

    # ArgumentList refuses an empty collection, so the host is started without one at all.
    if ($Small) {
        return Start-Process -FilePath $Exe -WorkingDirectory $Game -PassThru `
            -ArgumentList @('-screen-width', '640', '-screen-height', '400', '-screen-fullscreen', '0')
    }
    return Start-Process -FilePath $Exe -WorkingDirectory $Game -PassThru
}

# ---------------------------------------------------------------- reading a finished run

function Read-Verdict {
    param([string] $Folder, [string] $Name, [string] $Mode, [int] $Players)

    $hostLog = Get-ChildItem "$Folder\host-*.log" -ErrorAction SilentlyContinue | Select-Object -First 1
    $all     = @(Get-ChildItem "$Folder\*.log" -ErrorAction SilentlyContinue)

    $r = [ordered]@{
        Table = $Name; Mode = $Mode; Players = $Players; Peers = $all.Count
        Ran = ''; Bar = ''; Started = $false; SceneLive = $false
        Seated = 0; SeatGood = 0; SeatWrong = 0; WorstOffset = ''; Gap = ''
        DealtSeats = 0; ShortSeats = 0; Hand = 0
        Turns = 0; Slots = 0; TurnRing = ''
        Devils = 0; Chaos = 0; ChaosDone = 0; Bids = 0; DiceCalls = 0
        Aims = 0; AimBad = 0; Shots = 0; ShotsLost = 0
        Raises = 0; Folds = 0; Claims = 0; DiceSaves = 0
        AimRing = ''; Driving = $false; ModeOk = $true
        Exceptions = 0; Dropped = 0; ModErrors = 0; BadRpc = ''; FirstError = ''
        Notes = ''
    }

    if (-not $hostLog) { $r.Notes = 'no host log'; return [pscustomobject]$r }
    $h = @(Get-Content $hostLog.FullName -ErrorAction SilentlyContinue)
    $joined = $h -join "`n"

    if ($joined -match 'starting a (\w+) match') { $r.Started = $true }
    if ($joined -match "the table running this match is ([^\r\n]+)") { $r.Ran = $Matches[1].Trim() }

    # Did this cell test the mode it is filed under? The mode is chosen by pressing the lobby
    # arrow and the lobby keeps whatever it was last left on, so a cell can quietly inherit the
    # previous one's table - and every number below would then describe the wrong mode while
    # looking perfectly healthy. The players are asked what they are holding, and that is
    # checked against what was requested.
    if ($Expected.ContainsKey($Name) -and $r.Ran) {
        if ($r.Ran -notlike "*$($Expected[$Name])*") {
            $r.ModeOk = $false
            $r.Notes = "WRONG MODE - asked for $($Expected[$Name]), the table played '$($r.Ran)'"
        }
    }
    # The scene the match itself loaded, not the lobby it left. There are four bars and the
    # host's own saved choice decides which one, so a run's verdict has to say where it was.
    $scenes = $h | Select-String -Pattern 'OnClientChangeScene[^:]*: (\S+) \(op=' -AllMatches |
              ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } |
              Where-Object { $_ -notmatch 'SteamLobby' }
    if ($scenes) { $r.Bar = @($scenes)[-1] }

    # The census block where the most seats were holding cards, not the last one.
    #
    # The last block is whatever the table happened to look like when the run was killed,
    # which is usually a round part-way through or hands already played out - so a mode that
    # dealt all eight in perfectly reported "dealt 0/8" and looked broken. The question being
    # asked is whether the deal ever reached every seat, and the best block answers it.
    foreach ($census in ($h | Select-String -SimpleMatch '[census] ' | Where-Object { $_.Line -match 'at the table' })) {
        $r.SceneLive = $true
        if ($census.Line -match '\[census\] (\d+) at the table') { $seated = [int]$Matches[1] } else { continue }

        $dealtHere = 0; $shortHere = 0; $handHere = 0
        $from = $census.LineNumber
        for ($i = $from; $i -lt [Math]::Min($from + 12, $h.Count); $i++) {
            if ($h[$i] -notmatch 'seat \d+ (in |OUT)') { break }
            # The card values are developer-only, so the seat line comes in two shapes. Matching
            # only the one with values made a perfectly dealt table read back as "dealt 0/8".
            if ($h[$i] -match '(\d+) dealt(?: \[[^\]]*\])?, (\d+) of (\d+) card objects') {
                $dealt = [int]$Matches[1]; $out = [int]$Matches[2]
                if ($dealt -gt 0) { $dealtHere++ }
                if ($dealt -gt 0 -and $out -lt $dealt) { $shortHere++ }
                # The biggest hand seen. A hand size is the only way to tell from outside
                # whether the deck was sized for the table - at three players the deal must
                # not shrink below what four players get, and nothing else in this report
                # would show it if it did.
                if ($dealt -gt $handHere) { $handHere = $dealt }
            }
        }

        if ($dealtHere -ge $r.DealtSeats) {
            $r.DealtSeats = $dealtHere
            $r.ShortSeats = $shortHere
            $r.Hand = $handHere
        }
        if ($seated -gt $r.Seated) { $r.Seated = $seated }
    }

    # The mode's own mechanic, not just its deal: a variant that never puts its special card
    # on the table has had everything except the thing that makes it a different game tested.
    $r.Devils    = ($h | Select-String -SimpleMatch "DEVIL'S DEAL started").Count
    $r.Chaos     = ($h | Select-String -SimpleMatch 'CHAOS thrown by').Count
    $r.Bids      = ($h | Select-String -SimpleMatch 'DICE bid ').Count
    $r.DiceCalls = ($h | Select-String -Pattern 'DICE (liar|spot-on) called').Count
    $r.Raises    = ($h | Select-String -Pattern 'TEXAS (call|all in) by').Count
    $r.Folds     = ($h | Select-String -SimpleMatch 'TEXAS fold by').Count
    $r.Claims    = ($h | Select-String -SimpleMatch 'SPIN claim of').Count
    # How often the Liar's Dice reveal had to be put back on its feet. Not an error:
    # it is the mod catching a fault the game has above four players, and a cell where
    # it fired and the bidding carried on is the recovery working.
    $r.DiceSaves = ($h | Select-String -SimpleMatch 'round recovered').Count
    $r.ChaosDone = ($h | Select-String -SimpleMatch 'chaos aim resolved').Count

    # Who a seat chose to shoot, and who the game then shot. Two numbers, because the aim
    # ring was broken for half the table while the chaos card still 'fired' every time.
    $r.Aims      = ($h | Select-String -Pattern '\[aim\] .* aims at .* and fires').Count
    $r.AimBad    = ($h | Select-String -SimpleMatch 'meant to shoot').Count
    $r.Shots     = ($h | Select-String -Pattern 'the shot from .* landed on ').Count
    $r.ShotsLost = ($h | Select-String -SimpleMatch 'landed on nobody').Count

    # Whether every seat could point at every other seat, asked directly rather than waiting
    # for a chaos card to turn up and give the question a chance to be asked.
    if ($joined -match '\[aimring\] every seat at this table of (\d+) can point') { $r.AimRing = "all $($Matches[1])" }
    elseif ($joined -match '\[aimring\] (\d+) seat\(s\) at this table of (\d+) cannot reach') { $r.AimRing = "BLIND $($Matches[1])/$($Matches[2])" }

    # A run where the harness was not driving proves nothing, and every count above would be a
    # zero that reads like a pass. Say so instead of reporting it as a result.
    $r.Driving = ($h | Select-String -SimpleMatch 'DEVELOPER MODE IS ON').Count -gt 0
    if (-not $r.Driving) { $r.Notes = 'NOT DRIVEN - developer mode was off, every count below is meaningless' }

    if ($joined -match 'the turn reached all (\d+) seats') { $r.TurnRing = "all $($Matches[1])" }
    elseif ($joined -match 'the turn reached only (\d+) of (\d+) seats') { $r.TurnRing = "ONLY $($Matches[1])/$($Matches[2])" }

    $r.Turns = ($h | Select-String -SimpleMatch 'active slot ->').Count
    $r.Slots = (($h | Select-String -Pattern 'active slot -> (\d+)' -AllMatches |
                 ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value }) | Select-Object -Unique).Count

    $worst = -1.0
    foreach ($f in $all) {
        $text = @(Get-Content $f.FullName -ErrorAction SilentlyContinue)
        if ($text.Count -eq 0) { continue }

        $seat = $text | Select-String -SimpleMatch '[seatcheck]' | Where-Object { $_.Line -match 'GOOD|WRONG' } | Select-Object -Last 1
        if ($seat) {
            $r.SceneLive = $true
            if ($seat.Line -match '-> GOOD') { $r.SeatGood++ } else { $r.SeatWrong++ }
            if ($seat.Line -match 'seat ([0-9.]+)m, from the table edge') {
                $o = [double]$Matches[1]
                if ($o -gt $worst) { $worst = $o }
            }
            if ($seat.Line -match 'gaps ([0-9.]+)\.\.([0-9.]+)deg') { $r.Gap = "$($Matches[1])-$($Matches[2])" }
        }

        # Only errors from the running game. BepInEx prints a page of TypeLoadException
        # warnings from Harmony scanning Unity's own assemblies at every startup, and
        # counting those made a clean run look like a broken one.
        $r.Exceptions += ($text | Select-String -Pattern '\[Error\] Unity:.*(IndexOutOfRangeException|ArgumentOutOfRangeException|NullReferenceException|InvalidOperationException)').Count
        $r.Dropped    += ($text | Select-String -SimpleMatch 'Disconnecting connection').Count
        $r.ModErrors  += ($text | Select-String -Pattern "\[Error\].*Liar's Bar 8 Players").Count

        # The first real exception, and the first frame of wherever it came from. A count on
        # its own says a cell went wrong and nothing about how, so every count above zero has
        # meant opening eight log files by hand to find one line. Developer mode asks Unity
        # for stack traces, so when there is one the method is right there.
        if (-not $r.FirstError) {
            $hit = $text | Select-String -Pattern '\[Error\] Unity:.*(IndexOutOfRangeException|ArgumentOutOfRangeException|NullReferenceException|InvalidOperationException)' |
                   Select-Object -First 1
            if ($hit) {
                $msg = ($hit.Line -replace '^.*\[Error\] Unity: ', '').Trim()
                $at  = ''
                for ($k = $hit.LineNumber; $k -lt [Math]::Min($hit.LineNumber + 8, $text.Count); $k++) {
                    if ($text[$k] -match '^\s*at ([^\r\n]+)') { $at = $Matches[1].Trim(); break }
                }
                $r.FirstError = if ($at) { "$msg | $at" } else { $msg }
            }
        }

        $rpc = $text | Select-String -SimpleMatch '[rpc] ' | Select-Object -First 1
        if ($rpc -and -not $r.BadRpc -and $rpc.Line -match '\[rpc\] (.+?) threw') { $r.BadRpc = $Matches[1] }
    }
    if ($worst -ge 0) { $r.WorstOffset = "{0:F2}" -f $worst }

    if ($r.ShortSeats -gt 0 -and $r.Driving -and $r.ModeOk) { $r.Notes = "$($r.ShortSeats) seat(s) dealt cards they never received" }
    return [pscustomobject]$r
}

# ---------------------------------------------------------------------------- the matrix

$csv = "$Results\summary.csv"
$summary = @()
if ($Append -and (Test-Path $csv)) { $summary = @(Import-Csv $csv) }

$runNo = 0
$total = $Tables.Count * $Sizes.Count

foreach ($name in $Tables) {
    if (-not $Catalogue.ContainsKey($name)) { Write-Host "unknown table '$name'" -ForegroundColor Red; continue }
    $table = $Catalogue[$name]

    foreach ($n in $Sizes) {
        $runNo++
        Write-Host ""
        Write-Host "[$runNo/$total] $name ($($table.Mode)) with $n players ----------------" -ForegroundColor Cyan

        Stop-Copies
        Remove-Item "$Logs\*.log" -Force -ErrorAction SilentlyContinue

        $ok = $true
        $hostProc = Start-Copy -Role 'host' -LoopbackRole 'host' -Expect $n -Table $table
        $hostLog = "$Logs\host-$($hostProc.Id).log"

        if (-not (Wait-ForLine -Path $hostLog -Pattern '[loopback] hosting on' -TimeoutSec $LaunchTimeout -ProcId $hostProc.Id)) {
            Write-Host "  host never reached its lobby" -ForegroundColor Red
            $ok = $false
        }

        if ($ok) {
            for ($i = 2; $i -le $n; $i++) {
                $c = Start-Copy -Role "c$i" -LoopbackRole 'client' -Expect 0 -Table $table -Small
                if (-not (Wait-ForLine -Path "$Logs\c$i-$($c.Id).log" -Pattern '[loopback] joining' -TimeoutSec $LaunchTimeout -ProcId $c.Id)) {
                    Write-Host "  copy $i never connected" -ForegroundColor Red
                    $ok = $false; break
                }
                Write-Host "  copy $i of $n in" -ForegroundColor DarkGray
            }
        }

        if ($ok) {
            if (Wait-ForLine -Path $hostLog -Pattern 'copies are here and ready' -TimeoutSec 150 -ProcId $hostProc.Id) {
                Write-Host "  match started" -ForegroundColor Green
            } else {
                Write-Host "  the match never started" -ForegroundColor Yellow
            }
            # Play time is counted from when the round starts, not from when the match does.
            #
            # Those are three and a half minutes apart at eight players: "starting a match"
            # means the host pressed start, and then eight copies of an HDRP game each load
            # the bar on one PC. Sleeping a fixed time from the earlier of the two spent
            # almost all of it on the loading screen - a 260 second window left about 35
            # seconds of actual play, and one cell managed a single turn before it was killed.
            # Every large cell in every matrix before this one was measuring a minute of a
            # game it claimed to have played for four.
            #
            # The census prints the table's name from the players' own components as soon as
            # there are players holding cards, so it is the first thing that is true only once
            # the round is really under way.
            $ready = Wait-ForLine -Path $hostLog -Pattern 'the table running this match is' `
                                  -TimeoutSec 300 -ProcId $hostProc.Id
            if ($ready) {
                Write-Host "  round under way" -ForegroundColor Green
            } else {
                Write-Host "  the round never started - playing the clock out anyway" -ForegroundColor Yellow
            }

            Start-Sleep -Seconds $PlaySeconds
        }

        Stop-Copies

        $folder = Join-Path $Results ("{0}-{1}p" -f $name, $n)
        if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $folder | Out-Null
        Copy-Item "$Logs\*.log" $folder -ErrorAction SilentlyContinue

        $v = Read-Verdict -Folder $folder -Name $name -Mode $table.Mode -Players $n
        $summary += $v

        Write-Host ("  ran on '{0}'  seats {1} good / {2} wrong  dealt {3}/{4}  short {5}  ring {6} aimring {19}  devil {7}  chaos {8}/{9}  bids {10}  calls {11}  texas {20}/{21}  spin {22}  dicesaves {23}  aim {12}/{13} bad {14}  shots {15} lost {16}  exc {17}  modErr {18}" -f `
            $v.Ran, $v.SeatGood, $v.SeatWrong, $v.DealtSeats, $v.Seated, $v.ShortSeats, $v.TurnRing,
            $v.Devils, $v.ChaosDone, $v.Chaos, $v.Bids, $v.DiceCalls,
            $v.Aims, ($v.Aims + $v.AimBad), $v.AimBad, $v.Shots, $v.ShotsLost,
            $v.Exceptions, $v.ModErrors, $(if ($v.Driving) { $v.AimRing } else { 'NOT DRIVEN' }),
            $v.Raises, $v.Folds, $v.Claims, $v.DiceSaves) -ForegroundColor Gray

        $summary | Export-Csv $csv -NoTypeInformation -Force
    }
}

Write-Host ""
Write-Host "==================== matrix complete ====================" -ForegroundColor Cyan
$summary | Format-Table Table, Players, Ran, Seated, SeatGood, SeatWrong, WorstOffset, DealtSeats, ShortSeats, TurnRing, Devils, Chaos, Bids, DiceCalls, Exceptions, Dropped, ModErrors -AutoSize
Write-Host "Logs and summary.csv: $Results"
