# Liar's Bar: Fully Loaded (formerly the 8 Player Mod): Technical Notes

## Target build

| | |
|---|---|
| Steam AppID | `3097560` |
| Build ID | `24035789` (as of 2026-09-04) |
| Unity | 2022.3.27f1, HDRP |
| Scripting backend | **IL2CPP** |
| Networking | **Mirror** + FizzySteamworks (Steam P2P), kcp2k fallback |
| Voice | Dissonance (MirrorIgnorance integration) |

Vanilla hashes (backed up in `backup/vanilla/`):

- `GameAssembly.dll` — `542782A64107C13FF6FF12C5EDF018BBBE614BEA8BC03FA20093602E21D02D9C`
- `global-metadata.dat` — `119F7F0E9A8A882D8FCFE92BB281025137E1B81BB2D6C8302BF237BE3FACD084`

If these hashes change, the game updated and the mod must be re-verified.

## Modding approach

IL2CPP compiles C# to native code, so the game's methods cannot be edited on disk.
Everything is done with **BepInEx 6 (IL2CPP) + Harmony runtime patches**.

BepInEx regenerates readable "interop" assemblies into `BepInEx/interop/` on first
launch. Those carry the real type/method/field *names* but **not** the method bodies —
the bodies live as native code in `GameAssembly.dll`. Practical consequence:

- Names and signatures: fully available. Patch by name; survives most updates.
- Hardcoded literals inside methods: **not** readable without native decompilation.

That is why values were first discovered by *instrumenting at runtime* rather than by
reading constants. Most caps in this game turn out to be serialized Unity inspector
fields anyway, which can simply be written at runtime.

The ones that are not — the deck size and the seat number turn order wraps at — were
found by decompiling `GameAssembly.dll` with Cpp2IL, whose output resolves call targets
to real names. Those constants are immediate operands in machine code, out of reach of
any Harmony patch, so the mod rewrites them in memory at startup (`NativeCode`,
`DeckSizePatch`, `TurnOrderFix`). Every such write is guarded: the surrounding
instruction sequence must match exactly and be the only match in the method, or the fix
reports itself and is skipped.

The decompiler's output is the game's own code in readable form and is never committed.
What was learned from it lives in `docs/PLAYER-LIMITS.md`.

## Architecture

Internal codename for the game is "Velvet". Devs are Turkish — expect Turkish
identifiers (`MasaCards` = table cards, `Sansur` = censor, `Sayilar` = numbers).

### Networking / lobby

- `CustomNetworkManager : Mirror.NetworkManager`
  - `maxConnections` — **vanilla 4** (inherited Mirror field)
  - `List<PlayerObjectController> GamePlayers` — dynamic, no fixed cap
  - `GameMode Mode`, `int DeckMode`, `int DiceMode`, `int LobbyType`
- `SteamLobby` — `HostLobby()` calls `SteamMatchmaking.CreateLobby(type, maxMembers)`
  with **maxMembers = 4** in vanilla
- `LobbyController` — `List<> SpawnSlots` = **4** podium slots (lobby UI)
- `PlayerObjectController.InGameSlot` (SyncVar) — seat assignment, index into `Manager.Slots`

### In-game

- `Manager` (central, `NetworkBehaviour`)
  - `List<Transform> Slots` — seat transforms
  - `List<> Players`, `List<> NameTexts`
  - Per-mode seated-character lists: `DicePlayerPrefabs`, `PokerPlayerPrefabs`,
    `TexasPlayerPrefabs`, `ChaosPlayerPrefabs`, `ChaosDeckPlayerPrefabs`,
    `RoulettePlayerPrefabs`, `LiarsSpinPrefabs`, `MatchMakingPlayerPrefabs`
  - `int ActivePlayerSlot`, `int StartPlayerCount`
- `PlayerStats` — `int Slot`, `Health`, `Dead`, `HaveTurn`, `Winner`
- `CharController : NetworkBehaviour` — base for every per-mode gameplay controller

### Game modes (`CustomNetworkManager.GameMode`)

| Value | Mode | Manager |
|---|---|---|
| 0 | LiarsDeck | `DeckGamePlayManager` |
| 1 | LiarsDice | `DiceGamePlayManager` |
| 2 | LiarsChaos | `ChaosGamePlayManager` |
| 3 | LiarsPoker | `PokerGamePlayManager` |
| 4 | VelvetRoom | `VelvetRoomManager` |
| 5 | LiarsTexas | `TexasGamePlayManager` |
| 6 | LiarsSpin | `LiarsSpinGameplayManager` |
| 7 | Arena | `ArenaGamePlayManager` |

The enum names are not the released names: 5 `LiarsTexas` is the released Liar's Poker and 6
`LiarsSpin` is Liar's Slots. 3 `LiarsPoker` was never released; the mod calls it Two-Card Poker
(`TwoCardPoker.cs`) and only ever reaches it by swapping 5 for 3 on the host as the match starts.
Never pass 3 to `ChangeGameMode`: it writes `LastMode`, which the next lobby reads back.

### Deck construction (Liar's Deck)

Relevant members on `DeckGamePlayManager`:

- `void AddCards(List<int> types)` — builds the deck from a type list
- `int ToCardTypeBasic(int n)` / `int ToCardTypeDeck2(int n)` — index → card type mapping.
  **Do not patch these**: they are inlined into both deal methods at every site the deal
  uses, so a Harmony patch on them has no call site and never runs. The thresholds
  (6/12/18 basic, 8/16/24 deck2) are rewritten as immediate operands inside the deals
  instead — `DealBasicOrDevil` contains the block twice.
- `void DealBasicOrDevil()`, `void DealDeck2()` — dealing entry points
- `IEnumerator GiveCardPlayer()` — per-player card handout
- `void ResetRound(bool first)` — round setup
- `List<> MasaCards` / `ResetCards` / `OpenCards` / `ExtraCards`

Also present and potentially useful: `bool EditorSoloDeckTest(Manager mgr)` — a
developer solo-test hook.

## Verified findings

| Cap | Vanilla | Patched | Verified by |
|---|---|---|---|
| `NetworkManager.maxConnections` | 4 | 8 | plugin log at runtime |
| Steam lobby `maxMembers` | 4 | 8 | **Steam API** `GetLobbyMemberLimit` = 8 |
| `LobbyController.SpawnSlots` | 4 | — | runtime count |

## Things that are not obvious and cost a day each

- **Every mode's manager object is awake in the scene, whatever is being played.** So
  `manager.SomeGame != null && activeInHierarchy` identifies *nothing*. The only reliable
  discriminator is the gameplay component bolted onto the players — exactly one of
  `DeckGameplay`, `ChaosDeckGameplay`, `PokerGamePlay`, `TexasGamePlay`, … is added, and it
  is the mode. `TableHand.Playing()` is the single place that decides this; nothing else
  should ask the question its own way. It was got wrong four separate times before that rule
  existed.

- **The deal is not mode-independent.** Seven managers each carry their own copy of the
  card-handout coroutine, each with its own `new PlayerStats[4]` and its own four-seat
  cursor. See `docs/PLAYER-LIMITS.md` §4b. The caps, the turn order and the seat ring *are*
  mode-independent, and that fact was over-generalised into "so is everything else".

- **The lobby's deck arrow picks a game, not a ruleset.** `DeckMode 2` is dealt by
  `ChaosDeckGamePlayManager`, a different manager with a different round, which never calls
  `DeckGamePlayManager.ResetRound` — so anything hung off that hook simply does not happen
  there. Three variants are reachable: Basic, Devil, Chaos Deck.

- **The devil card's type value is `-1`,** not a value above the ordinary faces. Any
  "special cards come last" reasoning is backwards.

- **The venue is a host setting remembered in PlayerPrefs (`CurrentMap`), not a default.**
  There are four bars — `Game`, `Game_JaponBarı`, `Game_CinBarı`, `RusBari` — with four
  different rooms, and a test runs in whichever one that machine last played in.

- **This build ships colliding two-byte Mirror RPC hashes.** Mirror warns about one pair at
  startup, and the Chaos deck makes every client throw handling remote calls that arrive on
  the wrong component. It reproduces at four players: it is the game's, not this mod's.
  `RpcTrace` names the offending call and keeps the connection rather than letting Mirror
  drop everybody. One pair is `ChaosDeckGameplay::RpcSyncRevolverUi` and
  `ChaosDeckGamePlayManager::PlayHeartBeat` (26549): two seconds after every Chaos deck shot,
  every copy logs `Found no receiver for incoming ClientRpc [26549]`.

- **`LobbyController.UpdatePlayerCountInSteam` is never called: its body is inlined.** The game
  writes the lobby's `playercount` from the lobby's `OnStartClient`, `OnStopClient` and `Update`,
  so a patch on the method itself never runs (batch B3 found `JoinFix`'s logging postfix there
  silent all session). `playercount` counts bots; the lists read Steam's member count first.

- **A spectator camera added under the game's spectator camera parent joins A and D for free.**
  The game steps through that parent's children by index and wraps on their count, so
  `SeatCams` only puts a switched-off copy of each seat's winner camera there, made under a
  switched-off holder so it cannot wake and take the view for a frame. Anything that moves the
  game's spectator cameras (`DiceTable.SpectatorCams`) must skip the copies by name.

- **A class with a member called `Words` hides the word table.** Several features keep a table
  or method named `Words`; inside those, the word table is `LiarsBar8P.Words`. Everything the
  player reads goes through `Words.T`, `Words.F` or `Words.N` with its English text, and a line
  shown and logged at once is built twice: English for the log, the game's language for the
  screen (`Matchmaking.WhyNot(lobby, shown: true)`).

- **A test copy's "main window" is BepInEx's console, not the game.** A launcher that starts the
  game minimised and then places its main window places the console; the game's own window
  (`UnityWndClass`) opens where the game last saved it, which is the main screen, and comes back
  there whenever something restores it. Launched at 3440x1440 it covered the main screen. The kit
  now writes each copy's place into the game's saved window position before it starts it, starts
  copies at its small size, and names the place in `LIARSBAR8P_WINDOW_AT`; a copy with that set,
  a plain one as well, finds its game window by class and holds its place there every frame,
  minimised or not, without activating it (`Ultrawide.Spot`), and never asks for full screen. The
  kit starts no build without that hold (it looks for `Ultrawide.HoldsEveryCopy` in the plugin): at
  the first menu the game resizes its window to the saved size, and every size in its list is wider
  than the second screen, so 2.0.0, 1.1.3 or any older build would come up on the main screen.

- **A check every frame comes after the move.** The game shows its own window, centred on the
  screen it is on, the moment the plugins have loaded, and centres it again whenever it resizes it:
  a window wider than its screen is centred onto the next one. So the copy's hold also wraps the
  window's own procedure from `Plugin.Load` on, and turns every move Windows is about to make into
  a move to the named place, the parking place of a minimised window excepted.

- **Moving another process's window waits for that process.** `ShowWindow` and
  `SetWindowPlacement` on a window of another thread return only once that thread has handled
  them, so a guard that used them stalled with the window still on show whenever the copy was not
  pumping messages. The kit's guard posts its minimise (`ShowWindowAsync`), sets the restored
  place on a thread of its own, kills a copy whose window is still over the main screen a second
  after it was seen or is not answering, and writes a heartbeat the launchers require.

- **A window started minimised can still be activated.** `Start-Process -WindowStyle Minimized`
  asks for SW_SHOWMINIMIZED, which activates the first window the process shows (BepInEx's console)
  and so hands the new process the foreground. Start test copies with SW_SHOWMINNOACTIVE through
  `CreateProcess` instead, and leave the game window alone afterwards: restoring it from minimised
  let the game take the foreground by itself once the desktop had been idle past Windows'
  foreground lock. A minimised copy still renders and takes its screenshots.

- **The saved window place only counts on the monitor Unity remembers.** With no monitor
  remembered (no `UnitySelectMonitor` value), Unity opened a test copy on the main screen, centred
  there at its launch size, before the plugin had started loading, so the copy's hold could not
  reach it, and the guard killed it a second later. The kit writes the remembered monitor (1 is
  the second screen) along with the place before each start, and puts the machine's own value back
  afterwards; with it a copy opened wholly on the second screen. Unity's own `-monitor 2` argument, which
  would leave the saved state alone, was tried instead and is not used: it picks the right screen
  but ignores the saved place and opens the window in that screen's top left corner, where the
  frame's invisible 7 px border lies over the main screen, about 0.8 s before the plugin starts
  loading, and the guard killed that copy too.

- **A test copy can take the foreground with nobody touching it.** It happened twice in one run,
  during the menu load and as the lobby loaded, around the times the game applies its display
  settings.
  Of the calls that can do it, Unity's player imports SetForegroundWindow (two call sites),
  SetFocus, ShowWindow and SetWindowPos, and not SetActiveWindow or BringWindowToTop; the game's own
  code calls none of them. A held copy now answers in its own window procedure: every move or show
  goes without activation (SWP_NOACTIVATE), a click activates nothing (MA_NOACTIVATE), and every
  activation message is logged with its time. SetForegroundWindow cannot be turned down by a
  message, so `HeldFocus` hands a foreground the copy gets straight back to the window that had it,
  from a thread of its own that notes the last outside foreground window every 25 ms, since the
  game's thread stops while a scene loads. It never notes another held copy's window (each held
  copy makes a named mark the others look for), or two copies would pass the foreground back and
  forth, each counting a fresh steal, and the window in front would never get it back. It makes at
  most six hand-backs in any ten seconds. Unity can take the foreground as its window opens,
  before any plugin runs, so the launcher hands the copy the window that was in front when it
  started. No native code is patched. Windows' foreground lock
  explains less than it seemed: `SystemParametersInfo` can read a different value from the
  registry, so an idle desktop alone may not let a process through.

- **Every test copy runs on a desktop of its own.** Since batch K2 the launchers start each copy on
  the desktop `LiarsBar8P-kit` in the user's window station (`WinSta0\LiarsBar8P-kit` as
  STARTUPINFO's desktop), made if it is not there. Only the input desktop is ever drawn on a screen
  and only a window on it can be in front, so nothing a copy or Unity does can reach the user's
  screens or focus, which nothing inside the plugin could promise: Unity took the foreground
  as its window first opened, before any plugin had loaded. The game's own child processes, its
  console host and crash handler, inherit the desktop. The kit never switches desktops and changes
  no Windows setting. Three things follow. The launcher keeps its desktop handle until the copy has
  a window there: a desktop with no handle and nothing on it is gone at once, and a process whose
  desktop is gone before it joins cannot start. `EnumWindows` sees only the caller's desktop, so
  the guard and the watcher, which run on the main desktop, see no window of such a copy (the watcher
  writes ON THE OWNER'S DESKTOP for any window of a registered copy there, which must never
  appear), and the kit lists the copy's windows with `EnumDesktopWindows`. And `HeldFocus` hands
  nothing back on that desktop (`LIARSBAR8P_DESKTOP` tells it), since its target would be a window
  on the main desktop. The desktop goes by itself with its last process, which `stop-test.ps1`
  checks. A copy that can be seen, for picture work, takes `mm-launch.ps1 -Shown -OwnerDesktop`.
  The probe for it was `charmap.exe`, not Notepad: on this PC `notepad.exe` is redirected to the
  Store app (Image File Execution Options, AppExecutionAliasRedirect), which may not start where
  it is told. On that desktop the copies drew at the game's own cap of 60 frames a second, and
  their screenshots were whole pictures.

- **Never pipe a kit script into `Select-Object -First`.** PowerShell stops the script once it has
  that many lines: a `screen-settings.ps1 -Save` cut short that way (batch K2) saved the display
  settings and not the game's choices, so the restore after that run had no `LastMode` to put back.

- **`stop-test.ps1` restores the settings and drops the saved copy.** Its restore puts the game's
  choices and graphics quality back and deletes their saved copies, so a second launch under the
  same lock needs `screen-settings.ps1 -Save` again first. Without it (7 Nov, the board picture
  run) the second copy's mode and deck variant (`LastMode` and `DeckMode`, both 0 after it) had
  nothing to be put back from.

- **Two sessions share one game.** One install, one set of saved settings, one loopback port and
  room for two copies: only one session tests at a time. The kit's `lock.ps1 -Take <name> -For
  <what>` takes the test lock (one atomic create; of two takes at once exactly one wins), and every
  kit call after it sets `LIARSBAR8P_TESTLOCK` to that name. Starting copies, `deploy-test.ps1`
  (the lock check, then the repo's `deploy.ps1`, which names nothing of the kit), `restore-owner.ps1`
  (the installed plugin and config back from a backup, checked byte for byte), `screen-settings.ps1`
  `-Save`, `-Lower` and `-Restore`, and `stop-test.ps1` all refuse without it, and `stop-test.ps1`
  stops only the holder's own copies. The lock names the session's own process, so a lock whose
  session has ended can be taken over, with a line in the kit's lock log, and the new holder
  inherits its copies. `lock.ps1 -Release <name>` refuses until the holder's copies are gone, the
  game's plugin and config are the ones recorded at the take, and `screen-settings.ps1 -Check`
  finds the settings restored. `lock.ps1 -Status` says who holds it.

- **A bare test copy measures the game without the mod.** With `LIARSBAR8P_DEV=1` set, as the kit
  does, `LIARSBAR8P_BARE=1` makes `Plugin.Load` put on only the window hold, the kit's driver, the
  loopback harness, the ranked block and the self test's private lobby (`DiagAutoHost`), and the
  ticker run only those. A copy compared with a bare one at the same state shows what the mod itself
  holds. The config file's developer setting does not count for it, so a player cannot reach it. A
  bare copy hosts no public Steam lobby and joins none: the driver's `host`, `hostgame`, `quick`,
  `join` and `idjoin`, and the self test's `host` role, refuse there. With none of the mod's lobby
  parts its lobby would carry no modded tag, and the game's own list would show it to players
  without the mod. Its one Steam lobby is `hostprivate`, kept private as in a full copy. Below a bare
  copy, `mm-launch.ps1 -NoMod` starts the game with Doorstop's own switch off (`--doorstop-enabled
  false`, which this install honours: its `doorstop_config.ini` has `ignore_disable_switch = false`):
  no BepInEx and no mod, so no `coreclr.dll` in the process, no log and no console window. Nothing
  then holds its window, so it runs on the kit desktop only.

- **What a copy holds is the game's own, and its graphics quality decides most of it** (batch M2,
  6 Nov, one copy at a time on the kit desktop, `mem-run.ps1`). Full, bare and no mod at all agree
  within about 0.1 GB of committed private memory wherever they were compared: 8.9 GB at the menu
  (no mod 8.9 too), 10.2 GB in the lobby, 13.2 GB at a table at Ultra quality. The
  working set, what is actually resident, is smaller: 2.6 to 2.7 GB at the menu, 4.1 to 4.2 GB in
  the lobby, 7.7 to 8.2 GB at a table of one copy and its bots, and 10.1 to 11.0 GB for each of two
  copies at a table of two. Seats and characters barely count: 4 seats to 8 adds about 0.06 GB, and
  eight different characters hold no more than eight of one. Quality does: at an 8-seat table High
  (2) holds 8.5 GB private and 2.6 GB resident, Low (0) 7.0 GB and 1.7 GB, against Ultra's 13.3 GB
  and 8.0 GB. So the test PC's cap of two copies is the game at Ultra, and a player short of memory
  should lower the quality, not the table size. Two things these figures are not:
  - **The two-copy working sets are the test PC's, not a player's.** They are two copies of the
    game side by side on one machine, with the harness playing their seats. A player runs one copy,
    and the one-copy rows are the nearest to what theirs holds, at any table size.
  - **The no-mod menu figure was taken in a bigger window.** With no mod nothing holds a copy's
    window, so at its first menu the game sized it to the saved display size, not the kit's 960x540
    the full and bare copies were held at. Its 8.9 GB private agrees with theirs all the same; its
    working set is not a like-for-like comparison.

- **The harness's house rules need a settled lobby.** A lone host (`LIARSBAR8P_EXPECT=1`) chose
  `LIARSBAR8P_RULES` in the tick its hosting began, and the new session `HouseRules` noticed later in
  that same tick put the lobby back on CLASSIC: the first QUICK runs of batch M2 played CLASSIC, with
  `[rules] host picked QUICK` in the log and `match starts at Dice on CLASSIC` after it. With more
  copies the pick came minutes later and held. The harness now picks again before the start if the
  lobby has dropped it (`Loopback.KeepRules`, once a run). A test of a rule checks the log's
  `match starts at ... on <rules>` line, never only that the rule was asked for.

- **The game starts no match for one player.** On the host, the lobby's Start stays dark until it
  holds two players or more, all of them ready, and `StartGameOrReady` does nothing while Start is
  dark. So a lone harness copy needs at least one bot, and a bare copy, which has no bots, needs a
  second copy. The harness's `all N copies are here` line only says a start was asked for, and it
  is now said only when Start is lit; `[loopback] the match is up with N seats` comes once the match
  has its seats, in bare copies too, and is the line a test that measures a table waits for.

- **The display settings save in one place and reload from another.** Every save goes through
  `SettingsManager.SaveToPrefs` (Apply, closing the settings, quitting), and the size is a place
  in the settings' own list, so `Ultrawide` writes a wide place as the game's 1920x1080 there.
  The arrows only move the selector; Apply applies. F7 reloads the selector from the
  `PlayerPreferences` object, not from `SettingsManager`, so nothing that is meant to survive an
  F7 may be written into that object.

- **The mod's config is not a place to keep a player's choices.** Both installers delete
  `liarsbar.eightplayers.cfg` on every install, on purpose, so a stale setting can never survive
  an update. Anything the player chose and must keep goes under `%LOCALAPPDATA%\LiarsBar8P`, which
  no installer or uninstaller touches: the block list (`blocked.txt`) and the kept wide size and
  spent one-time pick (`display.txt`). A developer copy with a role keeps its own copy of each.

- **A bot is a lobby object with a connection number below zero, and its seat is spawned with no
  owner.** `BotManager.IsBot` reads `ConnectionID < 0` (a SyncVar, so a client can ask it too);
  never the name. At the table the host knows its bots' seats by network id (`BotSeats.Is`); a
  client matches a seat's `Player_Id` to the lobby. Bot Steam ids are `Loopback.FirstId + 100000 +
  n`, clear of the harness's copies. `NetworkServer.Spawn(seat, lobbyPlayer)` refuses an owner with
  no connection, so `BotSeats.Spawn` calls the other overload with a null connection: the seat is
  then a real networked object, the server paths run (`isServer` is true on it), and its
  `NetworkAnimator` is driven by the server, so animator values set on the host reach everyone.
- **A seat with no owner has nobody to send its commands or receive its target calls.**
  `BotSeats.Calls` catches both in Mirror's `SendCommandInternal` / `SendTargetRPCInternal` and runs
  the body on the host through `RemoteProcedureCalls.Invoke` (a target call is a `ClientRpc` there).
  Target calls go by the table in `BotSeats.Rules`: Run, or Drop for anything that would paint the
  owner's own screen or hand the host a secret (`TargetSetDiceValues` is one). A name not in the table
  is dropped with one warning. The Dice rows are proven; the Deck and Chaos rows were read but not
  yet run; the Texas and Slots rows are the design's and unproven. `AsOwner` sets `isOwned` for one
  synchronous call on a seat only; a coroutine it starts runs unowned.
- **The bot brain calls the server-side bodies, not the commands.** `BotBrain` moves with
  `UserCode_*` (no hash can misroute them) and sends only the voice lines as real commands, which
  the redirect runs; the end-of-match `[bot]` line counts both. It reads only what the seat could
  know. `BotBehaviour`, `ModePlay` and `AimDrive` are test instruments for real seats now and skip
  bot seats, so a seat is never played twice; `BotBehaviour` lost the hand building that only bots
  used, and in batch B2 `AdvanceIfStuck`: the game's own throw passes the turn. `CountTheDeck` stays
  for the harness's people's seats only; a bot never reads another seat's hand. At the deck tables a
  bot's throw is the game's `ThrowCards` from its look pose, and its calls are `PlayLiarCommand` (the
  voice line, on its own) then `RequestCallLiar`, or `TryCallSpotOn` for the Devil's Deal after the
  game's own `CanCallDevilsDeal`, which shows nothing on any screen.
- **Liar's Dice, Liar's Deck and the Chaos deck take bots.** `BotManager.Unwelcome` names every other
  table, and a lobby that switches to one sends its bots away; take a table's line out when its brain
  passes. Each table needs its own patches on as well (`HiddenDice.On`; `BotSeats.ThrowsOn` with
  `TriggerOn` for Liar's Deck or `GunDownOn` for the Chaos deck), so a patch that fails after a game
  update keeps bots from that table only. In the
  lobby a bot holds only `Ready`: `Loaded` there means "back from a match", and the game answers it
  by re-seating the player and clearing both flags. In the match it is set `Loaded` once, as a
  person's copy does. The game's own dice arrows stop at 20 once a bid stands; the brain does too.
- **There are eleven characters, and two of them carry another's outfit setter.** The lobby's
  `Skins` list runs 0 to 10; 9 and 10 (Dubai and Rasim) have no outfits, and the `SkinSetter` on their
  seats reports another character's number. Ask the lobby object's `PlayerSkin` which character a
  seat is, never the seat's setter.
- **The game's own "Die" state does not exist.** A Liar's Dice seat's second drink calls
  `Animator.Play("Die")`, and the copy that runs it logs `Animator.GotoState: State could not be
  found`. That is the game's, for people as much as bots; the slump comes from the seat's `Dead`
  value, which `BotPuppet` sets for a bot.
- **`HiddenDiceLog` keeps dice values out of every log, at every table.** Before a mode takes bots,
  check that no log holds its cards, reels or hole cards either.
- **`HiddenDice` keeps every other seat's dice undrawn from each roll until a reveal starts, at
  every table.** It switches other seats' dice off as each round is rolled and keeps them off until
  a reveal starts, then switches back on only what it switched off. An earlier build checked at
  4 Hz instead, which came too late: `WaitforNextRound` clears the call flags and rolls in the same
  frame. A die's face pictures count among its renderers. The driver's `botstate` counts, for every
  seat on the host, the dice drawn (counts only); `dicewatch on` counts every frame in which another
  seat's die is drawn outside a reveal (the answer must be none); `dicerow` and `dicerowat N` list
  each seat's dice (faces only during a reveal). All three work only in the loopback harness.
- **Nothing in the game ends a bot's turn for it.** Only the owner's copy acts when a turn's clock
  runs out (in Liar's Dice it bids, inside `DiceGamePlay.DiceGame()`); `Manager.Update` counts
  `CountDown` down only while the active seat holds the turn and stops at nought, and a caller keeps
  the turn while the call is settled. So `BotTurnGuard` passes a bot's turn on (`NetworkHaveTurn =
  false`, `GiveTurn()`) once its clock, seen running this turn, is under one second outside a
  reveal, and then refuses new bots for the session. It reads only the turn and the clock, so it
  works when the brain cannot run. The driver's `botfail move` / `botfail brain` exercise it. At the
  deck tables it also stands aside while the game holds every move (`BlocksBidInput`), which it does
  while it makes the liar call itself for the last player still holding cards.
- **A game member that an update renames fails the whole method that names it, as it is compiled.**
  The exception comes out of the call to that method, so a `try` inside the method never runs; only
  the caller's `try` catches it. That is why every `BotTurnGuard.Settling` overload (private flags
  such as `liarResolveRoutineActive` and `chaosThrowResolving`) is asked inside a `try` by its callers,
  and why ModTicker's two bot catches trip the bots. When the flags cannot be read, no bot moves and
  the guard passes a bot's turn once its clock has sat under a second for 20 s. `botfail settle` and
  `botfail tick` exercise both.
- **A bot's throw takes its own cards out of its hand.** At both deck tables a bot that throws
  without the look calls the server's hide for the cards it threw (`ServerHideCards` or
  `ServerChaosHideCards`) itself, so its hand is right however the throw was made. `botlook off`
  makes every bot throw that way.
- **`Manager.Players` is the server's roster.** On a client it is empty, so anything that runs on
  every copy finds the seats in the scene instead (`FindObjectsOfType<PlayerStats>()` or the mode's
  own component). `TableHand.Playing()` asks the roster, so on a client it says None. A first build
  of the dice hiding read the roster and hid nothing on any client.
- **Liar's Deck asks whether a seat has a connection before every trigger pull.**
  `DeckGamePlayManager.IsPlayerAvailableForRevolver` needs the seat's `connectionToClient`, so a bot's
  seat never pulled, after a lost call or in the devil card's volley, and the round went on as if
  it had pulled and lived. `BotSeats.Trigger` answers yes for a living bot's seat; the pull then
  runs on the host through the game's own branch for a seat with no connection. The Chaos deck has
  no such check.
- **The Chaos deck's aim mark is taken down by the seat's own copy.** `TakingAim` goes false only in
  `ClearAimStateAfterFire`, which runs from the shot's picture on each copy, and `CmdClearAimState`.
  A bot's stayed up after the chaos card's volley (a seat killed mid-volley keeps its mark too), so
  read the manager's `ChaosStarted`, `MasterStarted` and `chaosThrowResolving` for whether the guns
  are out, never a seat's mark. `BotSeats.GunDown` notes when the server fires a bot's gun
  (`ServerResolveFire`), and `BotPuppet` takes its mark down the server's way two seconds later.
- **A deck seat's hand is its switched-on card objects.** `cardTypes` is the deal and no throw shortens
  it; Liar's Deck 2 sends it back out by place when a round keeps the hands, with the places of the
  cards still switched on. The harness used to throw from that list and shorten it, which misplaced
  every card in Deck 2 and left its seats' hands full on every screen; it now throws from the card
  objects and takes the card from the hand the server's way (`TableHand.InHand`, `Hide`). A bot's
  throw picks its cards on the card objects (`Card.Selected`, and the hand's `selectedCards` in Deck
  2, which the game's throw reads there instead) and calls the game's own `ThrowCards` once its look
  pose is up.
- **Awards count bots.** `MatchStats` counts at the server bodies a bot's moves reach too, so a bot
  can take an award, as in a run where four of the five went to bots. Bots keep counting: awards are not for people only.
- **Test copies write the game's saved mode and character.** The lobby's mode and arrows save
  `LastMode`, `DeckMode`, `DiceMode` and `CurrentMap` in the game's PlayerPrefs, which a player's own game uses
  too, and a copy whose character the driver changes (`skinfor`) saves `LastSkin`. Read all five
  before a test session and put them back after it.
- **A changelog heading is `## vX.Y.Z — title`.** `release.ps1` takes a release's notes from the
  section headed with its tag (`v3.0.0`), and the What's New window finds a version's section by the
  same `## v` heading, so a section headed `## 3.0.0` would reach neither. `release.ps1` also refuses
  while any `## Unreleased` is left. A `### ` heading inside a section is drawn as a gold line in the
  What's New window.
- **Strict versions count only seated players.** The host's Start waits for a seated player on
  another version (they have a connection, so the game's kick reaches them), never for a Steam lobby
  member who is not seated: nothing on the host can remove one of those, and somebody who has not
  sat down yet is the arriving wait's, which has its own limit. A joiner leaves a lobby of the pool
  whose host shows another version, and also an untagged lobby whose host's own member data shows a
  mod version, which is what releases from before the pool publish.
- **A Liar's Deck host migration always deals a new round.** The game's own restore on the new host
  (`HostMigration.TryFinalizeDeckRoundRestore`) ends with `PrepareForMigrationHardReset` and
  `WaitforNextRoundMigration` once every seat has spawned, at every table, with the mod or without
  it. So the release notes promise no change there.
- **The pause screen's panels follow their canvas two frames late.** When a bar loads at a wide size,
  `UltrawideArt.FitCanvases` changes the canvases' scaling in the same tick as the panel pass, and the
  canvas only takes its new size a frame or two later, so the pass sized the fade, loading and credits
  panels from the old one (2488x1044 instead of 2584x1084 at 1032x432). The panel pass now runs again
  two frames after any canvas's scaling changes.
- **The test driver needs `LIARSBAR8P_MMTEST`, in loopback too.** Without it the copy reads no
  commands at all; `mm-launch.ps1 -Test idle` is enough for a loopback copy.

## Known constraints

1. **Every player must run the mod, with the same `MaxPlayers` value.** Mirror
   requires host and clients to agree on spawned objects and seat indices; a vanilla
   client in a modded lobby will desync.
2. **Game updates can break it.** Patches are name-based rather than offset-based,
   which survives most patches, but re-verify after every update (check the hashes above).
