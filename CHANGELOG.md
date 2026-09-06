# Changelog

Every player in a lobby must run the **same version**. The running version is shown in
the bottom-left corner in game; the host also warns when someone's build differs.

## v2.0.1

- Removed personal identifiers from the project. The plugin id changed from a
  name-based one to `liarsbar.eightplayers`, so the settings file is now
  `BepInEx/config/liarsbar.eightplayers.cfg`. The installer wipes the old file, so
  nothing needs doing by hand.
- Added this changelog. Release notes now come from it rather than being written twice.

## v2.0.0 — the deck was hardcoded

The real cause of cards never working above four players.

- **The deal's deck size is a constant compiled into the game.** Decompiling showed
  `DealBasicOrDevil` builds `Enumerable.Range(1, 20)` and `DealDeck2` builds
  `Range(1, 28)`. Adding card objects never changed it, so every round produced twenty
  cards no matter how many people sat down: four full hands, nothing for the fifth
  player, and an index past the end. That single constant explains the crash, the
  missing cards, and why the deck never appeared to double.
- The constant is now rewritten in memory, scaled by the cards-per-player the game
  itself uses. Five players get 25 and 35; eight get 40 and 56.
- **Card faces are computed, not looked up.** `n<=6` Ace, `n<=12` King, `n<=18` Queen,
  else Joker. Nothing threw on a bigger deck - everything past the last threshold simply
  became a Joker, so a 25 card deck would have dealt six Aces, six Kings, six Queens and
  *seven* Jokers. Thresholds now scale with the deck, giving exactly 12/12/12/4 at eight
  players: two vanilla decks.

## v1.5.1 — installers always start clean

- Both installers delete this mod's plugin and settings before installing, including any
  renamed or duplicated copies. Keeping settings across an update had let a stale option
  silently disable a fix.
- BepInEx's generated `interop` folder is deliberately left alone; rebuilding it is slow
  and it does not belong to this mod.

## v1.5.0 — fixes to the mod's own patching

- **Patches were cancelling each other out.** Two Harmony finalizers on the same method
  with no priority: once one clears an exception the next sees nothing, so the code that
  seats an extra player may never have run at all.
- Trimmed seats are now kept and restored, so a table that grows mid-session has seats
  for the new arrivals instead of running out.

## v1.3.0 — lists match the players present

- Seats and per-player lists were expanded to the configured maximum and left there, so
  anything walking them visited empty chairs. The turn indicator pointed at nobody while
  a real player acted. Everything is now sized to the players actually present.
- Seat indices are compacted to a contiguous range, and `StartPlayerCount` is corrected -
  it lagged at four, which alone dealt to only four of five players.

## v1.2.0 — the turn order pointed at the wrong seat

- New seats were appended to the end of the list but placed physically between the
  original four, so list order and seating order disagreed. Seats are now laid out in
  index order around the table.

## v1.0.0 — version visibility

- The running version is drawn in the bottom-left corner, and the host is warned when a
  player's build differs. Mixed versions had silently corrupted several sessions.
- The deck grows with the player count so everyone still receives five cards.

## v0.3.0 — clients no longer disconnect

- The mod had been duplicating Mirror scene objects to add lobby podiums. That corrupted
  spawn handling and disconnected everyone in the lobby. Lobby panels are networked scene
  objects and cannot be added at runtime, so the mod no longer tries; the in-game seat
  ring is expanded instead.

## v0.2.0 — the fifth player can join

- **Mirror keeps two connection limits.** `NetworkManager.maxConnections` is the visible
  one; `NetworkServer.maxConnections`, installed by `NetworkServer.Listen(int)`, is the
  one actually checked when a client connects. The game calls `Listen(4)` regardless, so
  Steam admitted a fifth player and Mirror dropped them straight back to the menu.

## v0.1.0 — first release

- Raises the Steam lobby limit and Mirror's connection cap from 4 to 8.
- One-click installer that finds the game through Steam automatically.
