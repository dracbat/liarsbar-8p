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

Zero errors from the mod on any peer, at any size.

## Played with real people, on real machines

**2026-09-08, v0.31.0: five people, five PCs, over Steam.** Not the loopback harness — five
separate machines in a Steam lobby, playing Liar's Deck Basic to a winner. Confirmed from the
host's log afterwards:

| | |
|---|---|
| All five seated on the ring | 5 of 5, gaps 72.0–72.1°, nobody more than 0.00 m from their own seat |
| All five dealt | five cards each, all holding them |
| Rounds played | 35 round resets across the session, matches run to a winner |
| Errors from the mod | **none** |
| Remote calls that threw | **none** |
| Deal routines patched | 7 of 7 |

That is the first time this has been true of anything but a single machine talking to itself.
**Eight people on eight machines is still the open question** — five proves the networking
path, not the table size.

> The log records real Steam names. Do not paste one into an issue or a screenshot without
> reading it first.

## Verified per mode, at every size

Each cell is a real table of that many separate copies of the game. "Dealt" means every seat
holding the cards it was dealt, read back from the server; "turn ring" means the turn was
handed round deliberately and reached every seat still in the game.

| Table | 5 | 6 | 7 | 8 | Its own mechanic |
|---|---|---|---|---|---|
| Liar's Deck — Basic | ✅ | ✅ | ✅ | ✅ | — |
| Liar's Deck — Devil | ✅ | ✅ | ✅ | ✅ | devil's deal fired at 7 |
| Chaos Deck | ✅ | ✅ | ✅ | ✅ | chaos card thrown and resolved at every size |
| Liar's Dice | ✅ | ✅ | ⚠️ | ⚠️ | no cards to deal |
| Liar's Texas | — | — | — | ✅ | — |
| Liar's Poker | ✅ | — | — | — | — |

⚠️ Liar's Dice: seated and playing, but the turn-ring probe reached five of seven and seven of
eight. The probe hands the turn on with the generic method and Dice may not use it, so this is
as likely to be the wrong question as a wrong answer. Unresolved either way.

The deck arrow reaches **three** variants reliably — Basic, Devil, Chaos Deck. A fourth
position exists in the game's own cycling code; runs asking for it landed on one of the other
three, so whatever is there is untested.

> **That sign-off was too generous, and v0.31.0 says how.** It also claimed Liar's Dice at
> six and eight and Liar's Poker at seven, on the strength of the seating being right and no
> errors being logged. The seating *was* right. Nobody was dealt a hand: every mode except
> Liar's Deck threw on the first step of its deal, silently, and the only thing watching the
> deal read Liar's Deck's own component. Liar's Poker at five players did not merely fail to
> deal - it dropped every connection in the game. What "played in" is allowed to mean here is
> now "every seat holding the cards it was dealt, and the mode's own mechanic seen to fire".

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
- **Velvet Room and Arena.** Neither is a table in a private lobby - the lobby's mode arrows
  reach four games (Liar's Deck, Texas, Dice, Spin) and neither of these is among them. They
  are listed in the mode enum and have no seated-player table of their own, so there is
  nothing here for the seat ring or the deal to be right or wrong about. Not tested, and not
  currently believed to be in scope.
- **The other three bars.** The venue is a host setting remembered in PlayerPrefs, and there
  are four of them with four different rooms. Every test this project has ever run happened
  in whichever one this machine last played in. The seat ring measures the table it finds
  rather than assuming one, so it ought not to care; that is reasoning, not evidence.
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
