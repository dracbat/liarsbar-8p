# Changelog

Every player in a lobby must run the **same version**. The running version is shown in
the bottom-left corner in game; the host also warns when someone's build differs.

## v2.0.2

- **The plugin file itself carried the build machine's folder path.** The compiler records
  where an assembly was built and stamps that into the file, so every download contained
  an absolute path from the machine that produced it. The build no longer emits a symbol
  file or records source paths, and packaging now refuses to run if any file about to be
  shipped still contains the build account name.
- The old settings file is matched by its ending rather than its full name, so a copy left
  behind by an earlier version is cleared on install even though nothing names it.
- Every release page now describes what actually changed in it. They had all carried the
  same text, and that text still claimed seats and dealing were untested.

## v2.0.1

- Removed personal identifiers from the project. The plugin id changed from a name-based
  one to `liarsbar.eightplayers`, so the settings file is now
  `BepInEx/config/liarsbar.eightplayers.cfg`. The installer wipes the old file, so nothing
  needs doing by hand.
- Added this changelog. Release notes now come from it rather than being written twice.

## v2.0.0 — the deck was hardcoded

The real cause of cards never working above four players.

- **The deal's deck size is a constant compiled into the game.** Decompiling showed
  `DealBasicOrDevil` builds `Enumerable.Range(1, 20)` and `DealDeck2` builds
  `Range(1, 28)`. Adding card objects never changed it, so every round produced twenty
  cards no matter how many people sat down: four full hands, nothing for the fifth player,
  and an index past the end. That single constant explains the crash, the missing cards,
  and why the deck never appeared to double.
- The constant is now rewritten in memory, scaled by the cards-per-player the game itself
  uses. Five players get 25 and 35; eight get 40 and 56.
- **Card faces are computed, not looked up.** `n<=6` Ace, `n<=12` King, `n<=18` Queen, else
  Joker. Nothing threw on a bigger deck — everything past the last threshold simply became
  a Joker, so a 25 card deck would have dealt six Aces, six Kings, six Queens and *seven*
  Jokers. Thresholds now scale with the deck, giving exactly 12/12/12/4 at eight players:
  two vanilla decks.

## v1.5.1 — installers always start clean

- Both installers delete this mod's plugin and settings before installing, including any
  renamed or duplicated copies. Keeping settings across an update had let a stale option
  silently disable a fix, and a leftover plugin had put players on different builds in the
  same lobby. Both cost whole test sessions.
- BepInEx's generated `interop` folder is deliberately left alone; rebuilding it is slow
  and it does not belong to this mod.

## v1.5.0 — fixes to the mod's own patching

- **Patches were cancelling each other out.** Two of this mod's handlers sat on the same
  game method with no ordering between them. Once one absorbed the error the next saw
  nothing, so the code that seats an extra player may never have run at all. The handler
  that acts now runs first and passes the error on, so the one that reports it still does.
- **Trimming seats was a one-way door.** Spare seats were removed so turn order could not
  land on an empty chair, but they were discarded — if someone joined and the table grew,
  there was no seat for them. Trimmed seats and nameplates are now kept and restored.

## v1.4.1 — simpler card index wrapping

- Removed a "learn the deck size" branch that the following bounds check already covered.
  It only added state that could mislead. An ordinary four player round is unaffected.

## v1.4.0 — lists match the players present

- **Seats and per-player lists were expanded to the maximum and left there**, so anything
  walking them visited empty chairs. The turn indicator pointed at nobody while a real
  player acted, and dealing that walks seats skipped people. Both symptoms were one cause,
  not two. Everything is now sized to the players actually present, and seat indices are
  compacted so no index can exceed the roster.
- Seats this mod added are deactivated rather than destroyed, and seats the game shipped
  with are never removed, so a later round can grow again.
- The card index is wrapped against the vanilla deck so a second deck is an exact copy of
  the first, keeping 6/6/6/2 rather than inventing a distribution. (Superseded by v2.0.0,
  which found the deck size itself was fixed in the game's own code.)

## v1.2.0 — best-effort fixes for dealing above four

- `StartPlayerCount` is corrected before the round is set up. It lagged at four while five
  players were seated, so anything looping over it dealt to four people and left the fifth
  with nothing — exactly the reported symptom.
- Seat indices are compacted into a contiguous range, so a player cannot hold a seat index
  beyond the number of players present and send something indexing off the end.

## v1.1.1 — players move with their seats

- Re-spacing the ring moved the seats, but bodies were placed when they spawned and stayed
  put. The turn arrow follows the seat, so it pointed at empty space while the player acted
  from where they had originally spawned. Players are now moved onto their seats, on the
  host only, so a client cannot fight the position sync.
- The release script had created a version as a draft, which is invisible to the "latest"
  link the online installer fetches — anyone installing "the latest version" silently got
  the previous one, and the script reported success anyway. It now publishes drafts and
  fails outright if the latest release is not the one it just built.

## v1.1.0 — the ring fits the players present

- With five players in an eight seat ring, the occupied seats covered only half the table:
  players bunched on one side, gaps opposite, and the turn indicator pointing into a gap.
  Seats are now spaced evenly for the number of players actually present, and unused seats
  are parked out of the way.
- A player the game could not register now receives the lowest unclaimed seat instead of
  remaining a ghost with no body, no cards and an arrow pointing at their empty chair.

## v1.0.0 — version visibility, and a doubled deck

- The running version is drawn in the bottom-left corner, and the host names anyone whose
  build differs. Three different builds had been in one lobby at once, and that mismatch
  corrupts shared state for everybody.
- Above four players the deck is doubled outright rather than sized per player, keeping the
  vanilla ratios exactly.
- Seats are laid out in index order around the table. New seats had been appended to the
  end of the list but placed physically between the original four, so list order and
  seating order disagreed and the indicator pointed at one seat while someone elsewhere
  acted.

## v0.9.0 — mismatched builds report themselves

- Each client publishes its version through Steam's lobby data and the host checks every
  member, naming anyone who does not match. Steam data rather than an in-game message on
  purpose: it works between mismatched builds, which is exactly when it is needed. Two
  sessions had been spent chasing symptoms that turned out to be one player who had not
  reinstalled.
- Three more per-player collections are grown so the deal can complete, and every remaining
  collection is logged at round setup so a further surprise names itself in one round.

## v0.8.0 — dealing to five, and skipped turns

- More than one collection is involved in a deal; only one was being topped up. The card
  pools and the per-seat list are now all grown.
- Turn order skipped the extra player because the roster count stayed at four. It is now
  corrected after the round starts, even though the game's own start method throws.

## v0.7.0 — the deck is topped up

- With five players seated the deal failed outright: five players need 25 cards from a deck
  built for 20. Spare card objects the game already owns are used first, and cards are only
  duplicated after confirming at runtime that they carry no networking component.

## v0.6.0 — in-game seats instead of lobby panels

- Lobby panels are networked scene objects and **cannot** be added by a mod. Three
  approaches were tried and the first cost several test sessions; the mod now reports the
  limit once instead of attempting it.
- The in-game seat ring is plain markers, so that *is* extended. New seats are placed on
  the circle fitted from the seats the game shipped with, so the original four never move.
  Nameplates are extended too.

## v0.5.0 — a stale setting was disabling the fix

- The previous version had shipped the lobby expansion switched off, and the installer
  preserved existing settings, so that "off" survived the update and the fix silently never
  ran. The option was removed entirely rather than defaulted on.
- The plugin reports its real version instead of a hardcoded one, which had made it
  impossible to tell from a log which build someone was running.

## v0.4.0 — the fifth player can be seated

- Four lobby podiums is a hard ceiling of four players: the game picks a free one from a
  list and fails when none are left. Expanding that list turned out to be required, not
  cosmetic as v0.3.0 had assumed. (v0.6.0 later established this cannot be done safely at
  all, and took a different route.)

## v0.3.0 — clients no longer disconnect

- The mod had been duplicating networked scene objects to add lobby podiums. That corrupted
  spawn handling and disconnected everyone in the lobby. An error inside the game's own
  code, blamed earlier on the game, was a downstream symptom of this.

## v0.2.0 — the fifth player can join

- **The networking layer keeps two connection limits.** Only the visible one was being
  raised; the one actually checked when a client connects is installed separately, and the
  game always set it to four. Steam admitted a fifth player and they were dropped straight
  back to the menu.
- Fixed the lobby seat arc: angles were sorted by a raw value that wraps at 180 degrees, so
  a ring straddling that boundary produced a 113 degree step instead of 22 and scattered
  the podiums, stacking some on top of each other.

## v0.1.0 — first release

- Raises the Steam lobby limit and the connection cap from 4 to 8.
- One-click installer that finds the game through Steam automatically, plus a matching
  uninstaller.
