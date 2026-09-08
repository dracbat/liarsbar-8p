# Status

Target: 8 players, all modes. Priority: Liar's Deck, then Liar's Dice.

`docs/PLAYER-LIMITS.md` is the map of every place the game assumes four players.
`CHANGELOG.md` is what changed in each release, and why.

## Verified with eight real connections

Eight separate copies of the game, each with its own Mirror connection, played together on
one machine through the loopback harness. Seating measured on **every** machine, not just
the host:

| Players | Gap between seats | Furthest anyone sat from their own seat |
|---|---|---|
| 5 | 72.0° | 0.01 m |
| 6 | 60.0° | 0.01 m |
| 7 | 51.4° | 0.00 m |
| 8 | 45.0° | 0.00 m |

Zero errors from the mod on any peer, at any size. Also played in **Liar's Dice** (six and
eight) and **Liar's Poker** (seven).

A copy of the game holds about 3 GB at steady state, so eight of them need roughly 24 GB -
well within a 64 GB machine. An earlier note here said 8 GB a copy and concluded eight
could never be tested; that measurement was taken from long-running copies with screenshots
on, and it was wrong.

## Verified from a real run

Confirmed in `BepInEx/LogOutput.log` on a live host, current build:

| Item | Evidence |
|---|---|
| Steam lobby member limit 4 → 8 | `GetLobbyMemberLimit` returns 8 |
| `NetworkServer.Listen(4)` → 8 | Mirror's real connection check |
| Steam transport server cap 4 → 8 | `[transport] server maxConnections 4 -> 8` |
| Host manager's own field 4 → 8 | the hosting instance differs from the one patched at startup |
| Turn order wrap, all five sites | `[turn] … wraps at seat 3 -> 7` |
| Deck size constant rewritten | `[decksize] DealBasicOrDevil: deck 20 -> 25` |
| 8 lobby podiums, each with its own name plate | `[podium] lobby has 8 podiums for up to 8 players` |
| 8 podiums in two rows, all on screen | seen in a screenshot of an eight player lobby |
| 8 seated, all dealt five cards | `[dealcards] hands three seconds after the deal` lists all eight |
| Turn rotates through all eight seats | `active slot -> 4 … 5 … 6 … 7 … 0 … 1` |
| Whose turn it is, shown top-left | `Your turn` / `BOT-3's turn`, and the tabletop markings hidden |
| Clean boot | no patch failures, no exceptions from the mod |

## Verified with other people

| Item | Evidence |
|---|---|
| 5 players connect and stay | `connections=5`, `numPlayers=5`, no disconnects |
| No Mirror scene corruption | no `already spawned`, no duplicate NetworkManager |
| In-game seat ring and nameplates beyond 4 | seen in play |

## Not yet proven

- **What the "arrow" on the table actually is.** The group named `TurnArrows` holds four
  objects 90° apart, one per shipped seat — and at four players *all four are switched on at
  once, on every turn*. Nothing drawn on the tabletop changes angle when the turn moves, in
  a four player round or an eight player one. So they are static seat markings, not a turn
  indicator: a player reads the marking nearest the person playing, and at eight players
  there is a marking in front of only every other seat, which is exactly what "the arrow is
  pointing at the person to the right" looks like. What actually shows whose turn it is has
  not been identified; `PlayerStats.SetEmissionByTurn` changes a *material* on the player,
  which no transform-and-active-state scan would ever have caught.
- **Whether turns pass by themselves when a person presses the key.** Bots throw through
  `RequestThrowCards`, the same entry point a person uses, and no longer clear `HaveTurn`
  themselves — `AdvanceIfStuck` moves the turn on 2.5 s later instead. The pass still never
  completes on its own for them, and cannot: it is a scheduled step that runs on the
  throwing player's *own* machine, and a bot's object is never network-spawned, so no
  targeted message reaches it. The loopback run has the same shape, because the host is
  driving those seats server-side rather than each copy playing for itself. So the watchdog
  spam in a test run is still an artefact of how the test drives play, and only a person at
  a keyboard settles it.
- **Liar's Chaos, Texas, Velvet Room and Arena.** Liar's Dice and Liar's Poker have now been
  played beyond four; these four have not. They share the caps, the turn-order fix and the
  seat ring, all of which are mode-independent, but that is reasoning rather than evidence.
- **Liar's Spin (Slots).** The harness selects the mode and reports the match starting, and
  then no game scene loads — it stays in the lobby. Not yet established whether that is the
  harness's way of choosing a mode or something about the mode itself; nothing in the mod is
  implicated either way, since it takes no part in mode selection.
- **A real eight-person table.** Eight *connections* have been proven, all from one machine.
  Eight people on eight machines, over Steam rather than loopback, is still untested — and
  it is the only thing left before v1.0.0.

## Known structural limits

- Lobby podiums cannot be spawned through Mirror — a scene object has no build-time
  assetId. The mod uses copies with a fresh, never-spawned identity, which the networking
  layer ignores; their state is therefore filled in per machine rather than synced.
- Every player must run the same version. The build is drawn top-left in game, and every
  peer — not just the host — warns about mismatches, because mixed versions have corrupted
  whole sessions.
