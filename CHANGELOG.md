# Changelog

Every player in a lobby must run the **same version**. The running version is shown in
the top-left corner in game; the host also warns when someone's build differs.

**v1.0.0 is reserved for the first release proven to work with eight people.** Everything
so far is below it. Versions that were once numbered 1.x and 2.x were folded into the same
0.x line to make room — `1.x.y` became `0.1x.y` and `2.x.y` became `0.2x.y`, so the order
is unchanged: what was v2.1.0 is now v0.21.0. Nothing else about those releases changed.

## v0.23.0 — eight players actually play

The core loop, end to end at eight: everyone registers, everyone is seated, everyone is
dealt, and the turn goes all the way around the table and wraps back to the first seat.

- **The fifth player could not be seated, and that was one missing component.** When a
  match starts, the game does not number a player by the order it seats them — it reads
  the number off the seat itself: `Slots[n].GetComponent<Slot>().SlotID`. The seats this
  mod adds were bare markers with no such component, so that returned null and the whole
  seating sweep threw a NullReferenceException on the first added seat. It stopped after
  four because four is exactly how many seats the game shipped with — everyone after that
  was left in the lobby roster, never at the table. Added seats now carry a real `Slot`
  numbered to match their position, with their own seat camera copied across so a person
  sitting there sees the table from their own chair.
- **Seat numbers are checked rather than assumed.** Every seat must carry its own list
  position and no two may share one; a duplicate puts two players on one number and one of
  them never gets a turn. Anything wrong is reported, not silently patched over.
- **The first turn was never handed out**, so a fully dealt table simply sat there. The
  game gives it from inside the deal's animation, behind a filter that does not always
  produce anybody. A round that is dealt, holding cards and has nobody able to act is now
  recognised as the stall it is and moved on through the game's own `GiveTurn`.
- With eight at the table the deal is right: forty cards, forty card objects, five each.

Developer mode gained enough to prove all of the above by itself — bots take a turn,
throw one card and hand the turn on, and the host's own seat is played too while an
automatic test is running, so a whole round runs unattended.

Lobby podium placement was also corrected: the shipped podiums are a row, not a ring, and
extending them as a ring put the extra four *behind* the original four. Wider table
layout, seat spacing and camera framing are deliberately still outstanding — playable
first.

## v0.22.0 — a way to test eight players without eight people

Everything here is off unless `DeveloperMode` is switched on, and none of it changes an
ordinary game. It exists because every remaining fault needed eight people in a room to
see, and that is not a test anybody can run twice.

- **Bots that fill the empty seats.** A bot is the game's own player object, spawned
  through the networking layer, registered in the same roster, given a lobby podium by the
  same method and dealt to by the same deal — not a simulation. Eight players now assemble
  in a lobby on demand, and the four extra podiums added in v0.21.0 have been seen in use
  for the first time. On its turn a bot waits a second or three and throws one card, which
  is always a legal move; it is not meant to play well.
- **A debug panel on F8**, with the same commands on function keys: add or remove a bot,
  fill the table, print the player list, the seat assignments, the lobby podiums, the deck
  state or whose turn it is, skip a turn, start the match. It also shows a live count of
  players, seats, the active slot and the deck size.
- **A full account in the log** of registration, lobby creation, seat assignment, dealing,
  turn changes, eliminations, round boundaries and deck size — each tagged so one concern
  can be read back on its own. It calls out the things that have gone wrong before:
  a player dealt nothing, two players on one seat, the turn indicator disagreeing with
  whose turn it is.
- **`AutoTestFullTable`** runs the whole thing by itself: host, fill, start, report. One
  launch, one round, no keyboard.

What it found in its first hour, all of it previously invisible:

- **`Manager.StartGame` throws part way through seating.** It seats about four players and
  then stops, so at eight only five reach the table and the rest are simply lost — in the
  lobby, in the roster, never at the table. This is the largest remaining limit, and it is
  now visible in one line rather than costing a session to notice.
- The game's own `SpawnPlayerwithskin` seats a player without adding them to the roster,
  so seating anyone outside that sweep does not count them.
- `OrderSprtes`, a per-player list, ships with **three** entries and was only grown once a
  round was already being set up — after seating had needed it. It is now grown first.
- A player with no connection is never told they are holding cards, and the round waits
  for everyone to be holding cards before the first turn.

## v0.21.2 — the installer is reusable, and says so

- The installer always asked GitHub for the newest release, so re-running the same file
  has always been enough to update - but nothing said so, and it told people to check the
  bottom left for their version, which is no longer where it is. It now says both.

## v0.21.1 — the version moves to the top left

- The version was drawn along the bottom of the screen, on the same line as the game's
  own version string. Two of them overlapped into one unreadable smear, which defeats
  the point of showing it at all. It now sits in the top left, on a dark panel so it
  stays legible over a bright menu as well as a dark bar.

## v0.21.0 — the sixth player, and turn order that skipped seats

Everything here came from mapping the game's compiled code for hard-coded fours before
changing anything. That map is now `docs/PLAYER-LIMITS.md`.

- **The sixth player was refused by the Steam transport, underneath the game.** There is a
  third connection limit nobody had found: the Steam transport's own server checks its
  connection count before the game or its networking layer ever see the attempt, and
  rejects with *"would exceed max connection count"*. The host does not connect to itself
  over Steam, so that count holds only the other players — a limit of four meant four
  guests plus the host, exactly the five that worked, and the sixth bounced with nothing
  in anyone's log. Raised wherever that server is built.
- **Turn order silently skipped seats five to eight.** Advancing a turn searches for the
  next living player and, on finding an empty seat, checks `seat > 3` before wrapping to
  seat zero. A full table happened to work; the moment anyone in the first four seats died
  — which is most of a round in this game — the search stepped past seat 3 and jumped
  straight back to the start, never visiting the later seats. That is the reported "it
  makes players play when the arrow isn't pointing at them". The same hard-coded seat
  three governed going backwards. All five places are now rewritten in memory, the same
  way the deck size is.
- **Who is on your left, right and across was a fixed four-by-four table.** Anything
  outside the first four seats fell through to "seat zero", so every extra player thought
  seat zero lay in all three directions. It is now worked out from the number of players
  present, and at four players produces exactly the table the game shipped with.
- **Players beyond the fourth now appear in the lobby.** The lobby has four podiums and
  the host picks a free one for each arriving player; the fifth threw, and because that
  same code is what places their body, they had no podium and no avatar until the match
  started. Extra podiums are now built from the existing ones — with a fresh, never-spawned
  identity, so the networking layer ignores them entirely — placed by continuing the arc
  the originals stand on, and each is given its own name plate rather than sharing one.
- **A seat given to an extra player never reached anybody else.** The fallback that seats
  a player the game could not place was writing the plain field instead of the synced one,
  so only the host knew about it. Every other player kept seeing them in seat zero.
- **Removed a deck patch that was attached to the wrong method.** It was scaling the list
  of cards handed to `AddCards`, which is not the deal — it is a player *playing* cards.
  Above four players it would have replaced a two-card play with a whole deck's worth. The
  deal itself has been handled properly since v0.20.0.
- One configurable maximum. Everything that raises a cap now reads a single value, and the
  count the game shipped with is kept separate from it, since scaling the deck in
  proportion needs the original and must not follow the setting.
- The plugin no longer waits a fixed number of frames before its optional self-test; it
  waits for Steam, which was not ready and made the test fail every time.

## v0.20.2

- **The plugin file itself carried the build machine's folder path.** The compiler records
  where an assembly was built and stamps that into the file, so every download contained
  an absolute path from the machine that produced it. The build no longer emits a symbol
  file or records source paths, and packaging now refuses to run if any file about to be
  shipped still contains the build account name.
- The old settings file is matched by its ending rather than its full name, so a copy left
  behind by an earlier version is cleared on install even though nothing names it.
- Every release page now describes what actually changed in it. They had all carried the
  same text, and that text still claimed seats and dealing were untested.

## v0.20.1

- Removed personal identifiers from the project. The plugin id changed from a name-based
  one to `liarsbar.eightplayers`, so the settings file is now
  `BepInEx/config/liarsbar.eightplayers.cfg`. The installer wipes the old file, so nothing
  needs doing by hand.
- Added this changelog. Release notes now come from it rather than being written twice.

## v0.20.0 — the deck was hardcoded

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

## v0.15.1 — installers always start clean

- Both installers delete this mod's plugin and settings before installing, including any
  renamed or duplicated copies. Keeping settings across an update had let a stale option
  silently disable a fix, and a leftover plugin had put players on different builds in the
  same lobby. Both cost whole test sessions.
- BepInEx's generated `interop` folder is deliberately left alone; rebuilding it is slow
  and it does not belong to this mod.

## v0.15.0 — fixes to the mod's own patching

- **Patches were cancelling each other out.** Two of this mod's handlers sat on the same
  game method with no ordering between them. Once one absorbed the error the next saw
  nothing, so the code that seats an extra player may never have run at all. The handler
  that acts now runs first and passes the error on, so the one that reports it still does.
- **Trimming seats was a one-way door.** Spare seats were removed so turn order could not
  land on an empty chair, but they were discarded — if someone joined and the table grew,
  there was no seat for them. Trimmed seats and nameplates are now kept and restored.

## v0.14.1 — simpler card index wrapping

- Removed a "learn the deck size" branch that the following bounds check already covered.
  It only added state that could mislead. An ordinary four player round is unaffected.

## v0.14.0 — lists match the players present

- **Seats and per-player lists were expanded to the maximum and left there**, so anything
  walking them visited empty chairs. The turn indicator pointed at nobody while a real
  player acted, and dealing that walks seats skipped people. Both symptoms were one cause,
  not two. Everything is now sized to the players actually present, and seat indices are
  compacted so no index can exceed the roster.
- Seats this mod added are deactivated rather than destroyed, and seats the game shipped
  with are never removed, so a later round can grow again.
- The card index is wrapped against the vanilla deck so a second deck is an exact copy of
  the first, keeping 6/6/6/2 rather than inventing a distribution. (Superseded by v0.20.0,
  which found the deck size itself was fixed in the game's own code.)

## v0.12.0 — best-effort fixes for dealing above four

- `StartPlayerCount` is corrected before the round is set up. It lagged at four while five
  players were seated, so anything looping over it dealt to four people and left the fifth
  with nothing — exactly the reported symptom.
- Seat indices are compacted into a contiguous range, so a player cannot hold a seat index
  beyond the number of players present and send something indexing off the end.

## v0.11.1 — players move with their seats

- Re-spacing the ring moved the seats, but bodies were placed when they spawned and stayed
  put. The turn arrow follows the seat, so it pointed at empty space while the player acted
  from where they had originally spawned. Players are now moved onto their seats, on the
  host only, so a client cannot fight the position sync.
- The release script had created a version as a draft, which is invisible to the "latest"
  link the online installer fetches — anyone installing "the latest version" silently got
  the previous one, and the script reported success anyway. It now publishes drafts and
  fails outright if the latest release is not the one it just built.

## v0.11.0 — the ring fits the players present

- With five players in an eight seat ring, the occupied seats covered only half the table:
  players bunched on one side, gaps opposite, and the turn indicator pointing into a gap.
  Seats are now spaced evenly for the number of players actually present, and unused seats
  are parked out of the way.
- A player the game could not register now receives the lowest unclaimed seat instead of
  remaining a ghost with no body, no cards and an arrow pointing at their empty chair.

## v0.10.0 — version visibility, and a doubled deck

- The running version is drawn in the top-left corner, and the host names anyone whose
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
