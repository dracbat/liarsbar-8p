# Status

Target: 8 players (configurable 2–16). Modes prioritised: Liar's Deck, then Liar's Dice,
then the rest.

## Verified working

| Item | Evidence |
|---|---|
| BepInEx 6 IL2CPP loads into the game | `Chainloader startup complete`, plugin logs on every boot |
| `NetworkManager.maxConnections` 4 → 8 | `[cap] maxConnections 4 -> 8` at runtime |
| Steam lobby member limit 4 → 8 | **Steam API** `GetLobbyMemberLimit` returns `8` on a created lobby |
| Lobby creation succeeds with raised cap | `[lobby] created result=k_EResultOK` |

The Steam member-limit result is the strongest evidence available without a second
machine: it is Steam reporting the lobby's capacity, not the mod reporting its own
intent.

## Implemented, not yet verified end to end

| Item | Why unverified |
|---|---|
| Lobby podium slot expansion (4 → 8, arc-fitted) | Needs a populated lobby to see occupied slots |
| Proportional deck scaling for Liar's Deck | Needs a started match; deck composition is read at runtime and scaled from it |

## Not yet started

- In-game seat ring (`Manager.Slots`) expansion
- Per-mode seated character objects (8 separate lists)
- Nameplate UI (`Manager.NameTexts`)
- Turn order / win condition / revolver assignment review for >4
- Liar's Dice scaling
- Remaining modes: Chaos, Poker, Texas, Spin, Arena, Velvet

## The honest blocker

Seats, nameplates and dealing all live in the gameplay scene, which normally requires a
full lobby to reach. Their real counts and positions can only be observed at runtime,
and correctness for 8 players can only be proven with 8 connected clients.

A developer solo-test hook (`DeckGamePlayManager.EditorSoloDeckTest`) exists and is
being used to try to reach the gameplay scene alone. If that works, the seat/deck work
can be developed and largely validated locally. If it does not, that work needs a real
multiplayer session to make progress safely.

Shipping untested seat/deck patches would be worse than shipping nothing: a desync
mid-match is harder to diagnose than a lobby that refuses to start.
