using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// What a bot does when the turn reaches it: wait a moment, then make the simplest legal
/// move, so the round keeps moving.
///
/// Deliberately not clever. The point of a bot here is to prove that a table of eight can
/// deal, take turns in the right order and finish a round — not to play well. Anything
/// resembling strategy would only make a failed round harder to read.
///
/// The move goes through <c>RequestThrowCards</c>, which is where a person's play lands
/// too: on the host it calls the server-side throw directly, so it needs no connection and
/// no ownership, and a bot's turn is settled by exactly the code that settles a person's.
/// </summary>
internal static class BotBehaviour
{
    /// <summary>How long a bot appears to think, so a round is watchable.</summary>
    private const float MinThinkSeconds = 1f;
    private const float MaxThinkSeconds = 3f;

    /// <summary>When each bot currently holding a turn intends to play.</summary>
    private static readonly Dictionary<int, float> _playAt = new();

    /// <summary>
    /// The turn a bot last played on, so it plays once per turn rather than once per tick.
    /// Throwing does not clear the turn flag straight away, so without this a bot sees "my
    /// turn" again a second later and throws its entire hand in one go — which is what it
    /// did.
    ///
    /// Counted rather than keyed on the active seat number, which was the first attempt.
    /// Seat numbers come round again: once play had been all the way round the table and
    /// arrived back at seat 0, that bot found it had "already played on turn 0", declined,
    /// and the table stopped dead at the end of the first lap every time.
    /// </summary>
    private static readonly Dictionary<int, int> _playedOn = new();

    /// <summary>Counts turns, so each one is distinguishable from the same seat a lap later.</summary>
    private static int _turnNo;
    private static int _lastActive = -1;

    /// <summary>
    /// Whether the round is in a state where nobody should be acting: a liar call being
    /// resolved, or cards still being dealt. Both are periods the game itself will not take
    /// input during.
    /// </summary>
    private static bool RoundIsResolving()
    {
        try
        {
            if (DealTrace.Dealing) return true;
            return TableHand.LiarCallInFlight();
        }
        catch { return false; }
    }

    /// <summary>Notice the turn moving, and count it.</summary>
    private static void CountTurn(Manager m)
    {
        int active = m.ActivePlayerSlot;
        if (active == _lastActive) return;
        _lastActive = active;
        _turnNo++;
    }

    /// <summary>
    /// Varied per bot without a random number generator, which the log needs stable.
    ///
    /// Cut short at a table that plays itself. The Chaos deck throws for a player who does
    /// not act quickly enough, and it does so well inside the one to three seconds a seat was
    /// taking to decide - so every turn was thrown by the game's timer before the seat got to
    /// it, and a four minute round produced three deliberate plays and not one chaos card.
    /// Deciding in under half a second is not a realistic player, and is not meant to be: it
    /// is what puts the choice of card back in the test's hands.
    /// </summary>
    private static float ThinkTime(int seat)
    {
        float min = MinThinkSeconds, max = MaxThinkSeconds;
        if (AutoThrows()) { min = 0.2f; max = 0.5f; }
        return min + ((seat * 7 % 5) / 4f) * (max - min);
    }

    /// <summary>Whether the table being played takes the turn away from a slow player.</summary>
    private static bool AutoThrows() => TableHand.Playing() == TableHand.Kind.ChaosDeck;

    internal static void Tick()
    {
        if (!Dev.Enabled) return;
        if (!Dev.IsServer) return;                 // only the host may act for a bot

        var m = Dev.Mgr;
        if (m == null || m.Players == null) return;

        CountTurn(m);

        // Nobody plays while the round is resolving or still being dealt.
        //
        // A liar call stops play: the cards are revealed and somebody pulls a trigger, and
        // nothing else is anyone's turn until that finishes. Without this the other seats
        // carried on throwing cards over the top of it - and the same during the deal, before
        // the hands had even arrived. The game blocks a person's input at these moments; it
        // has no way to block a seat being played for from the host, so that is done here.
        if (RoundIsResolving())
        {
            _advanceFrom = -1;          // whatever was pending belongs to the old round
            _playAt.Clear();
            RecoverStalledLiarCall();   // still watch for one that never finishes
            return;
        }

        AdvanceIfStuck(m);
        RecoverStalledLiarCall();

        // During an unattended test every seat is played, not just the bots'. Without it the
        // round reaches the tester's turn and stops there, which tests nothing beyond the
        // first turn. Only ever during a test - in an ordinary session, bots play and people
        // play for themselves.
        //
        // The loopback harness counts as one, and is the more useful case. Those seats are
        // real connected clients with properly spawned player objects, so a liar call played
        // for them resolves through the game's own code: the cards are revealed, somebody
        // pulls a trigger, and a player can actually be eliminated. None of that is reachable
        // with bots, whose player objects are never network-spawned - every targeted message
        // to one is refused as "not spawned", the trigger pull among them. Bots can play a
        // round; only real connections can finish a match.
        //
        // Driving, not Active: "the option is switched on" stays true for the whole launch, so
        // a host who ran one bot test and then invited friends over Steam had every friend's
        // turn played for them a second after it arrived, with about every third forced move
        // calling them a liar in their own name.
        bool playEveryone = DevAutoTest.Driving || Loopback.Mine == Loopback.Role.Host;

        foreach (var p in Dev.TablePlayers())
        {
            if (p == null) continue;
            if (!BotManager.IsBot(p) && !playEveryone) continue;

            // Only a bot needs its hand built by hand. A real client is dealt over its own
            // connection, and doing it again here would fight the deal that already worked.
            if (BotManager.IsBot(p)) HoldCards(p);

            if (!p.HaveTurn || p.Dead)
            {
                _playAt.Remove(p.Slot);
                continue;
            }

            // Already had a go on this turn.
            if (_playedOn.TryGetValue(p.Slot, out int playedOn) && playedOn == _turnNo) continue;

            if (!_playAt.TryGetValue(p.Slot, out float due))
            {
                due = Time.time + ThinkTime(p.Slot);
                _playAt[p.Slot] = due;
                Dev.Log("bot", $"{p.PlayerName} has the turn (seat {p.Slot}) - playing in " +
                               $"{due - Time.time:F1}s");
                continue;
            }

            if (Time.time < due) continue;
            _playAt.Remove(p.Slot);
            _playedOn[p.Slot] = _turnNo;
            Act(p);
        }
    }

    /// <summary>Bots whose hand has already been built this round.</summary>
    private static readonly HashSet<int> _handBuilt = new();

    /// <summary>
    /// The active slot a bot has just played from, and when to move the turn on if the
    /// game has not managed it by then. -1 means nothing is pending.
    /// </summary>
    private static int _advanceFrom = -1;
    private static float _advanceAt;

    /// <summary>The seat that threw last this round, and so the one there is a claim from
    /// to challenge. -1 means nothing has been thrown yet.</summary>
    private static int _lastThrower = -1;

    /// <summary>When a bot last called liar, so a resolution that never finishes is
    /// noticed rather than silently hanging the table. Zero means none outstanding.</summary>
    private static float _calledAt;

    internal static void RoundStarting()
    {
        _handBuilt.Clear();
        _playedOn.Clear();
        _playAt.Clear();
        _advanceFrom = -1;
        _lastThrower = -1;
        _calledAt = 0f;
        _rarity.Clear();
    }

    /// <summary>
    /// Recover from a liar call that never resolves.
    ///
    /// Resolving a call means somebody pulls a trigger, and the game runs that on the
    /// losing player's own machine. A bot has no machine, so this is exactly the kind of
    /// step that can stop dead. The round is a lost cause once that happens, so rather than
    /// leave the table frozen this says so plainly in the log and restarts the round -
    /// which keeps a long unattended test running, and leaves a record of every time it was
    /// needed instead of a silent hang to be diagnosed later.
    /// </summary>
    private static void RecoverStalledLiarCall()
    {
        if (_calledAt <= 0f || Time.time - _calledAt < 30f) return;
        _calledAt = 0f;

        try
        {
            var deck = Dev.Deck;
            if (deck == null) return;

            // The last call of a match does not resolve into a new round, because there is
            // no new round - it resolves into somebody winning. This fired at exactly that
            // moment and restarted the round anyway, dealing a fresh hand to a table with
            // one player left and stepping on the victory screen. A won match is not a
            // stalled one.
            int alive = 0;
            foreach (var p in Dev.TablePlayers())
                if (p != null && !p.Dead) alive++;

            if (alive < 2)
            {
                Dev.Log("bot", $"liar call did not start a new round, and should not have - " +
                               $"{alive} player(s) left, so the match is over");
                return;
            }

            Dev.Warn("bot", "a liar call has not resolved after 30s - the round is stuck, " +
                            "restarting it. The trigger pull runs on the losing player's own " +
                            "machine and a bot has none.");
            deck.AbortLiarResolveAndResetRound();
        }
        catch (Exception e) { Dev.Warn("bot", $"could not restart the stuck round: {e.Message}"); }
    }

    /// <summary>
    /// Move the turn on when a bot's throw has not moved it.
    ///
    /// Deliberately late and conditional: if the game passes the turn itself - which it does
    /// for anyone with a real connection - the active slot has already changed by the time
    /// this looks, and it does nothing. It only steps in for the case that genuinely has
    /// nobody to complete it. The wait also lets the throw animation play, so the turn does
    /// not jump before the card has landed.
    /// </summary>
    private static void AdvanceIfStuck(Manager m)
    {
        if (_advanceFrom < 0 || Time.time < _advanceAt) return;

        if (m.ActivePlayerSlot != _advanceFrom) { _advanceFrom = -1; return; }

        try
        {
            foreach (var q in Dev.TablePlayers())
                if (q != null && q.HaveTurn) q.NetworkHaveTurn = false;

            m.GiveTurn();
            Dev.Log("bot", $"turn had not moved on from seat {_advanceFrom} after a bot played " +
                           $"- moved it to seat {m.ActivePlayerSlot}");
        }
        catch (Exception e) { Dev.Warn("bot", $"could not move the turn on: {e.Message}"); }

        _advanceFrom = -1;
    }

    /// <summary>
    /// Put the cards and the revolver in a bot's hands.
    ///
    /// Everything a player physically receives arrives over their own connection: the card
    /// objects, the flag saying they are holding cards, the revolver being loaded. A bot
    /// has no connection, so none of it reaches them — they are dealt a hand that exists
    /// only as numbers, sit there empty-handed with no gun, and the round waits forever
    /// for everyone to be holding cards before giving out the first turn.
    ///
    /// The host therefore runs the receiving end for them. These are the same methods a
    /// real client runs when the message arrives, called directly rather than sent, so a
    /// bot ends up in the state a person would be in.
    /// </summary>
    private static void HoldCards(PlayerStats p)
    {
        try
        {
            var gp = p.GetComponent<DeckGameplay>();
            if (gp == null || gp.cardTypes == null) return;

            int count = gp.cardTypes.Count;
            if (count == 0) return;

            int id = p.Slot;
            if (_handBuilt.Contains(id))
            {
                // Already built; just keep the flag true as cards are played.
                if (!gp.HaveCards) gp.NetworkHaveCards = true;
                return;
            }
            _handBuilt.Add(id);

            // The card values it was dealt, and every one of them face down and active.
            var types = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(count);
            var active = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(count);
            for (int i = 0; i < count; i++) { types[i] = gp.cardTypes[i]; active[i] = i; }

            gp.ApplyCardState(types, active, true);

            // Applying the state records the hand; showing it is a separate step, and it is
            // the one that puts the card meshes in the player's hands. Without it a bot has
            // five cards on paper and empty hands on screen.
            gp.SetHaveCards(true);

            int objects = gp.Cards != null ? gp.Cards.Count : -1;
            Dev.Log("bot", $"{p.PlayerName} given {count} card(s) in hand ({objects} card objects) - " +
                           "done here because a bot has no connection to be dealt over");

            LoadRevolver(p, gp);
        }
        catch (Exception e)
        {
            Dev.Warn("bot", $"{p.PlayerName} could not be given a hand: {e.Message}");
        }
    }

    /// <summary>
    /// Load a bot's revolver. Same reason as the cards: the game runs this on the player's
    /// own client, and a bot has none, so its gun stays unloaded while everyone else's is
    /// ready.
    /// </summary>
    private static void LoadRevolver(PlayerStats p, DeckGameplay gp)
    {
        try
        {
            gp.StartCoroutine(gp.UpdateRevolverUI());
            Dev.Log("bot", $"{p.PlayerName} loaded its revolver");
        }
        catch (Exception e)
        {
            Dev.Warn("bot", $"{p.PlayerName} could not load its revolver: {e.Message}");
        }
    }

    /// <summary>
    /// Throw a single card. One card is always a legal play, whatever the round card is —
    /// the claim is a lie as often as not, which is the game working as intended.
    /// </summary>
    private static void Play(PlayerStats p)
    {
        try
        {
            var gp = TableHand.For(p);
            if (gp == null)
            {
                Dev.Warn("bot", $"{p.PlayerName} has no gameplay component - passing the turn instead");
                DevCommands.SkipTurn();
                return;
            }

            var hand = gp.Cards;
            if (hand == null || hand.Count == 0)
            {
                Dev.Warn("bot", $"{p.PlayerName} has no cards - passing the turn instead");
                DevCommands.SkipTurn();
                return;
            }

            // Which card to throw is the front of the hand, except where a rarer card is
            // sitting in it - then that one goes first. A deck's special cards are the whole
            // point of the variant it belongs to, and throwing off the front meant a run
            // could go its whole length without one ever reaching the table, so the mechanic
            // that makes the mode a different game was never once exercised.
            int pick = Special(hand);
            int type = hand[pick];

            var thrown = new Il2CppSystem.Collections.Generic.List<int>();
            thrown.Add(type);

            bool emptied = hand.Count <= 1;
            hand.RemoveAt(pick);

            Dev.Log("bot", $"{p.PlayerName} (seat {p.Slot}) throws a {type}, {hand.Count} left" +
                           (emptied ? " - hand empty" : ""));

            gp.Throw(thrown, emptied);
            _lastThrower = p.Slot;

            // Throwing does not end a bot's turn on its own: the game ends it from a
            // scheduled pass that never completes for a player with no connection. Clearing
            // the turn flag here is not enough either - that was tried, and it left the
            // active slot pointing at the bot that had just played, so the watchdog handed
            // the turn straight back to it, it declined to play twice, and the table sat
            // there. The turn has to actually be moved on, which is what this schedules.
            var m = Dev.Mgr;
            if (m != null)
            {
                _advanceFrom = m.ActivePlayerSlot;
                _advanceAt = Time.time + 2.5f;
            }
        }
        catch (Exception e)
        {
            Dev.Warn("bot", $"{p.PlayerName} could not play: {e.Message} - passing the turn instead");
            try { DevCommands.SkipTurn(); } catch { }
        }
    }

    /// <summary>How many moves the bots have made between them, so the choice of move
    /// varies without a random number generator - the log has to stay reproducible.</summary>
    private static int _moves;

    /// <summary>
    /// Decide between throwing a card and calling the previous player a liar.
    ///
    /// A round only ever ends one of two ways: everybody empties their hand, or somebody
    /// calls liar and one of the two pulls a trigger. Bots that only ever threw cards took
    /// the first exit every single time, so no shot was ever fired, nobody was ever
    /// eliminated, and the match could not reach a winner however long it was left running.
    /// Calling liar is what makes a finished game reachable at all.
    /// </summary>
    private static void Act(PlayerStats p)
    {
        _moves++;
        if (WantsLiar(p)) { CallLiar(p); return; }
        Play(p);
    }

    /// <summary>
    /// Whether calling liar is both legal and chosen this turn.
    ///
    /// Legal needs a claim on the table to challenge that is not this player's own, and no
    /// call already in flight. Chosen is every third move, which eliminates players at a
    /// watchable rate while still letting most hands get played out.
    /// </summary>
    /// <summary>
    /// Which card in hand to lead with: the one the table has fewest of.
    ///
    /// What makes a deck variant a different game is its special card, and a run that never
    /// puts one on the table has tested everything except the difference. Which value that
    /// card has is not something this should need to know - and guessing was worse than
    /// useless: "the highest value in hand" was tried first, on the reasoning that a deck is
    /// built by mapping a range of numbers onto faces in order, and it turns out the devil
    /// card is <c>-1</c>. The heuristic was not merely unhelpful, it actively avoided the one
    /// card the test existed to play.
    ///
    /// Counting them answers it without guessing. A deck of forty deals twelve each of three
    /// faces, four jokers and eight devils, so the special card is by construction the rarest
    /// thing on the table, whatever number it happens to carry.
    ///
    /// Every other move rather than always, so ordinary cards still get played and a round
    /// does not become nothing but special cards.
    /// </summary>
    private static int Special(Il2CppSystem.Collections.Generic.List<int> hand)
    {
        if (hand == null || hand.Count <= 1) return 0;

        // Every move, not every other one. A forty card deck puts about one devil card into
        // play at a time, so it sits in exactly one hand - and that seat has to get a turn
        // and choose to throw before the round ends. Holding it back on half of the moves
        // meant three rounds went by without the card ever reaching the table.
        if (_rarity.Count == 0) CountTheDeck();

        int best = 0, fewest = int.MaxValue;
        for (int i = 0; i < hand.Count; i++)
        {
            if (!_rarity.TryGetValue(hand[i], out int n) || n <= 0) n = 1;   // never seen: rare
            if (n < fewest) { fewest = n; best = i; }
        }
        return best;
    }

    /// <summary>
    /// How many of each card value are in play, counted across every hand at the table.
    ///
    /// Taken once a round, from the server's own view of everybody's cards - which is a thing
    /// no player can see and this is not pretending to be one. It drives nothing but which
    /// card a test throws first.
    /// </summary>
    private static void CountTheDeck()
    {
        try
        {
            foreach (var p in Dev.TablePlayers())
            {
                var hand = TableHand.For(p);
                var cards = hand != null ? hand.Cards : null;
                if (cards == null) continue;

                for (int i = 0; i < cards.Count; i++)
                {
                    _rarity.TryGetValue(cards[i], out int n);
                    _rarity[cards[i]] = n + 1;
                }
            }

            if (_rarity.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var kv in _rarity)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append($"{kv.Value} x {kv.Key}");
                }
                Dev.Log("bot", $"cards in play this round: {sb}");
            }
        }
        catch (Exception e) { Dev.Warn("bot", $"could not count the deck: {e.Message}"); }
    }

    private static readonly System.Collections.Generic.Dictionary<int, int> _rarity = new();

    private static bool WantsLiar(PlayerStats p)
    {
        try
        {
            if (TableHand.LiarCallInFlight()) return false;

            // Who to challenge is tracked here rather than read from the game's own
            // LastBetPlayer. That field is filled in by the announcing side of a claim,
            // which never runs for a player with no connection, so it stays empty for a
            // whole table of bots and every call was declined as "nothing to challenge".
            // The last throw is something this already knows for certain.
            if (_lastThrower < 0 || _lastThrower == p.Slot) return false;

            var gp = TableHand.For(p);
            bool empty = gp == null || gp.Count == 0;

            // With nothing left to throw, calling is the only move there is.
            //
            // Every fifth move rather than every third. A call ends the round, and at eight
            // players every third move meant two cards on the table and then a call - so a
            // round was over before most seats had played at all, and a mechanic that needs
            // its card thrown *and then* challenged had almost no room to happen.
            return empty || (_moves % 5) == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Call the previous player a liar, through <c>RequestCallLiar</c> - the same entry
    /// point the LIAR key uses - so the reveal, the revolver and the elimination all run
    /// exactly as they do for a person.
    ///
    /// Unlike a throw this does not clear the turn flag afterwards. A liar call ends the
    /// round, and the round end is what hands out turns next; taking the flag away
    /// underneath that sequence would be reaching into the middle of it.
    /// </summary>
    private static void CallLiar(PlayerStats p)
    {
        try
        {
            var gp = TableHand.For(p);
            if (gp == null) { Play(p); return; }

            Dev.Log("bot", $"{p.PlayerName} (seat {p.Slot}) calls LIAR on seat {_lastThrower} " +
                           $"(cards on table {TableHand.CardsOnTable()})");
            gp.CallLiar();
            _calledAt = Time.time;
        }
        catch (Exception e)
        {
            Dev.Warn("bot", $"{p.PlayerName} could not call liar: {e.Message} - throwing instead");
            try { Play(p); } catch { }
        }
    }
}
