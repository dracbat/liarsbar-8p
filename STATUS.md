# Status

Target: 8 players. Modes prioritised: Liar's Deck, then Liar's Dice.

## Verified working

| Item | Evidence |
|---|---|
| Steam lobby member limit 4 → 8 | Steam API `GetLobbyMemberLimit` returns 8 |
| **`NetworkServer.Listen(4)` → 8** | the real join blocker — see below |
| 5 players connect and stay connected | live: `connections=5`, `numPlayers=5`, no disconnect |
| No Mirror scene corruption | no `already spawned`, no duplicate NetworkManager |
| All patch sets apply cleanly | zero errors on a full boot |

### The join bug (fixed in 0.2.0)

Mirror keeps **two** connection limits: the serialized
`NetworkManager.maxConnections`, and the static `NetworkServer.maxConnections` that
`NetworkServer.Listen(int)` installs — and only the second is checked when a client
connects. The game calls `Listen(4)` regardless, so Steam admitted a 5th player and
Mirror dropped them straight back to the menu.

### The disconnect bug (fixed in 0.3.0 — self-inflicted)

The mod cloned `LobbyController.SpawnSlots` entries to add podiums. Those are Mirror
**scene objects**; duplicating them corrupted spawn handling and disconnected every
client. The `NullReferenceException` in `PlayerObjectController.CmdSetPlayer` blamed on
the game was a downstream symptom of this.

## The structural limit

`SpawnSlots` is a `List<LobbySlot>`, and `LobbySlot` is a **NetworkBehaviour** —
SyncVars (`Dolu`/occupied, `PlayerName`, `Ready`), UI references, a Kick RPC. It is a
scene object, not a positional marker.

**Extra lobby panels cannot be added by a runtime mod.** Mirror identifies
runtime-spawned networked objects by an `assetId` registered in `spawnPrefabs` at build
time; scene objects carry only a `sceneId`. Three approaches were tried and all failed
for this reason:

| Approach | Outcome |
|---|---|
| Clone the slot | duplicate scene identity → corrupted spawns → clients disconnected |
| Clone inactive, strip networking | destroyed the very component the list stores → NRE |
| Create a plain GameObject | wrong type — the list needs `LobbySlot` |

Consequence: with more than 4 players, `CmdSetPlayer` throws
`InvalidOperationException: Sequence contains no elements` from a LINQ `First()` over
free panels. The command guard swallows it so the player is **not** disconnected — they
are registered (`GamePlayers`, `numPlayers`) but have no lobby panel.

## Implemented, unproven in a real match

- **In-game seat ring 4 → 8.** `Manager.Slots` is `List<Transform>` — plain markers,
  safely expandable. Geometry captured live: exact circle, centre
  (0.353, 0.111, -8.909), radius 1.330, 90° apart, each facing centre
  (`yaw = atan2(-cos θ, -sin θ)`, verified against all four original seats).
- **Nameplates 4 → 8** (`List<TMPro.Examples.WarpTextExample>`). Non-fatal if it fails.
- **Proportional Liar's Deck scaling** — has still never executed.

## The open question

The 5th player is registered but has no lobby panel. **Can the host start a match when
that player cannot ready up?**

- Yes → the seat expansion takes over and a 5+ game is plausible.
- No → 8 players requires patching the game's own files, not a Harmony mod.

Everything else is blocked behind that answer.

## Not started

In-game UI beyond nameplates, turn order / win conditions above 4, Liar's Dice scaling,
and the remaining modes (Chaos, Poker, Texas, Spin, Arena, Velvet).
