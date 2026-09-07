# Status

Target: 8 players, all modes. Priority: Liar's Deck, then Liar's Dice.

`docs/PLAYER-LIMITS.md` is the map of every place the game assumes four players.
`CHANGELOG.md` is what changed in each release, and why.

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
| The table arrow follows the turn | `[dev:arrow] pointing the table arrow at seat 5 … 6 … 7 … 0` |
| Clean boot | no patch failures, no exceptions from the mod |

## Verified with other people

| Item | Evidence |
|---|---|
| 5 players connect and stay | `connections=5`, `numPlayers=5`, no disconnects |
| No Mirror scene corruption | no `already spawned`, no duplicate NetworkManager |
| In-game seat ring and nameplates beyond 4 | seen in play |

## Not yet proven

- **A full round with eight people.** Everything above is either verified solo or
  verified at five. Six, seven and eight have never been in one lobby.
- **What the "arrow" on the table actually is.** The group named `TurnArrows` holds four
  objects 90° apart, one per shipped seat — and at four players *all four are switched on at
  once, on every turn*. Nothing drawn on the tabletop changes angle when the turn moves, in
  a four player round or an eight player one. So they are static seat markings, not a turn
  indicator: a player reads the marking nearest the person playing, and at eight players
  there is a marking in front of only every other seat, which is exactly what "the arrow is
  pointing at the person to the right" looks like. What actually shows whose turn it is has
  not been identified; `PlayerStats.SetEmissionByTurn` changes a *material* on the player,
  which no transform-and-active-state scan would ever have caught.
- **Whether turns pass by themselves with real players.** In every bot run the watchdog has
  to nudge each turn — but it does so at four players as well, where the game is otherwise
  vanilla. Bots do not play through the game's own throw path (they clear `HaveTurn`
  directly), so the game's pass-turn step is never triggered for them. This is most likely
  an artefact of the bots rather than something a human table hits, and only real players
  can settle it.
- **Liar's Dice, Chaos, Spin and Poker.** They share the turn-order and cap fixes, which
  are mode-independent, but no round has been played in them since.

## Known structural limits

- Lobby podiums cannot be spawned through Mirror — a scene object has no build-time
  assetId. The mod uses copies with a fresh, never-spawned identity, which the networking
  layer ignores; their state is therefore filled in per machine rather than synced.
- Every player must run the same version. The build is drawn top-left in game and the
  host warns about mismatches, because mixed versions have corrupted whole sessions.
