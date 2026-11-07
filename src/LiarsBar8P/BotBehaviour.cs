using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// A test instrument for Liar's Deck and the Chaos deck: during a harness run it plays the real
/// seats (the host's and the loopback clients'), waiting a moment and then making the simplest
/// legal move, so the round keeps moving. Bots' seats are never played here: their moves are
/// <see cref="BotBrain"/>'s, and a seat played from two places would move twice.
///
/// Deliberately not clever. The point is to prove that a table of eight can deal, take turns in
/// the right order and finish a round, not to play well. Anything resembling strategy would only
/// make a failed round harder to read.
///
/// The move goes through <c>RequestThrowCards</c>, which is where a person's play lands
/// too: on the host it calls the server-side throw directly, so it needs no connection and
/// no ownership, and the turn is settled by exactly the code that settles a person's.
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
    /// resolved, cards still being dealt, or the game holding every player's moves, as it does
    /// while it makes the call itself for the last player with cards. All are periods the game
    /// itself will not take input during. That last one was missed: once everybody else had
    /// emptied their hand, the game handed the host's seat the turn to make that call for it,
    /// this threw a card for the seat in the moment before the game did, and the table stopped.
    /// </summary>
    private static bool RoundIsResolving()
    {
        try
        {
            if (DealTrace.Dealing) return true;
            if (TableHand.LiarCallInFlight()) return true;
            var m = Manager.Instance;
            switch (TableHand.Playing())
            {
                case TableHand.Kind.Deck: return m.DeckGamePlayManager != null && m.DeckGamePlayManager.BlocksBidInput;
                case TableHand.Kind.ChaosDeck: return m.ChaosDeckGame != null && m.ChaosDeckGame.BlocksBidInput;
            }
            return false;
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

        // Only the two games this understands. Everything below reaches for a hand of cards
        // through TableHand, which exists on Liar's Deck and the Chaos deck and nowhere else -
        // so in Liar's Dice or Texas every seat reached Play(), found no component, logged
        // "has no gameplay component" and skipped its own turn. That is worse than doing
        // nothing: it took the turn away from a mode that was perfectly capable of playing it.
        // Those modes are ModePlay's.
        var kind = TableHand.Playing();
        if (kind != TableHand.Kind.Deck && kind != TableHand.Kind.ChaosDeck && kind != TableHand.Kind.None) return;

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
            _playAt.Clear();
            RecoverStalledLiarCall();   // still watch for one that never finishes
            return;
        }

        RecoverStalledLiarCall();

        // Only during an unattended test, and then every real seat is played. Without it the
        // round reaches the tester's turn and stops there, which tests nothing beyond the
        // first turn. In an ordinary session people play for themselves.
        //
        // The loopback harness counts as one, and is the more useful case. Those seats are
        // real connected clients with properly spawned player objects, so a liar call played
        // for them resolves through the game's own code: the cards are revealed, somebody
        // pulls a trigger, and a player can actually be eliminated.
        //
        // Driving, not Active: "the option is switched on" stays true for the whole launch, so
        // a host who ran one bot test and then invited friends over Steam had every friend's
        // turn played for them a second after it arrived, with about every third forced move
        // calling them a liar in their own name.
        bool playEveryone = DevAutoTest.Driving || Loopback.Mine == Loopback.Role.Host;
        if (!playEveryone) return;

        foreach (var p in Dev.TablePlayers())
        {
            if (p == null || BotManager.IsBot(p)) continue;
            // Outside the loopback test harness the automatic test plays the host's own seat and
            // nobody else's: anyone else at the table plays their own turns.
            if (!Fair.TestsAllowed && !p.isLocalPlayer) continue;

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
                Dev.Log("bot", $"seat {p.Slot} has the turn - playing in " +
                               $"{due - Time.time:F1}s");
                continue;
            }

            if (Time.time < due) continue;
            _playAt.Remove(p.Slot);
            _playedOn[p.Slot] = _turnNo;
            Act(p);
        }
    }

    /// <summary>The seat that threw last this round, and so the one there is a claim from
    /// to challenge. -1 means nothing has been thrown yet.</summary>
    private static int _lastThrower = -1;

    /// <summary>When a bot last called liar, so a resolution that never finishes is
    /// noticed rather than silently hanging the table. Zero means none outstanding.</summary>
    private static float _calledAt;

    /// <summary>A call still being settled this long after it was made has stalled.</summary>
    private const float StalledAfter = 60f;

    internal static void RoundStarting()
    {
        RoundKeepingHands();
    }

    /// <summary>
    /// A Liar's Deck 2 round that keeps everybody's hand. The turn state belongs to the round
    /// just ended and goes; the hands stay as they are, so no bot is given its cards twice.
    /// Without this the first seat to play called liar on whoever threw last in the old round.
    /// </summary>
    internal static void RoundKeepingHands()
    {
        _playedOn.Clear();
        _playAt.Clear();
        _lastThrower = -1;
        _calledAt = 0f;
        _rarity.Clear();
    }

    /// <summary>
    /// Recover from a liar call that never resolves.
    ///
    /// Resolving a call means somebody pulls a trigger. Before bots had seats on the network,
    /// a bot could not, so this was exactly the kind of step that stopped dead; a bot's seat
    /// now pulls its trigger on the host, and this should never be needed. If it is, the round
    /// is a lost cause, so rather than leave the table frozen this says so plainly in the
    /// log and restarts the round - which keeps a long unattended test running, and leaves
    /// a record of every time it was needed instead of a silent hang to be diagnosed later.
    ///
    /// Only a call still being settled after a minute counts. It used to fire thirty
    /// seconds after the harness's call whether or not the call had long been settled, and
    /// the devil card's volley at a table of eight takes longer than that by itself. And
    /// only at the Liar's Deck table: the Chaos deck's manager has no restart, and the Liar's
    /// Deck one it used to reach for there dealt a round to nobody.
    /// </summary>
    private static void RecoverStalledLiarCall()
    {
        if (_calledAt <= 0f || Time.time - _calledAt < StalledAfter) return;
        if (!TableHand.LiarCallInFlight()) { _calledAt = 0f; return; }
        _calledAt = 0f;

        try
        {
            if (TableHand.Playing() != TableHand.Kind.Deck)
            {
                Dev.Warn("bot", $"a liar call has not resolved after {StalledAfter:0}s at the {TableHand.Playing()} table, " +
                                "which has no restart - left to the game");
                return;
            }

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

            Dev.Warn("bot", $"a liar call has not resolved after {StalledAfter:0}s - the round is stuck, " +
                            "restarting it (RecoverStalledLiarCall)");
            deck.AbortLiarResolveAndResetRound();
        }
        catch (Exception e) { Dev.Warn("bot", $"could not restart the stuck round: {e.Message}"); }
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
                Dev.Warn("bot", $"seat {p.Slot} has no gameplay component - passing the turn instead");
                DevCommands.SkipTurn();
                return;
            }

            // The cards really in the hand, its card objects. The list of dealt values it used
            // to throw from, and shorten, is never shortened by the game: Liar's Deck 2 deals
            // it back out by place when a round keeps the hands, so a shortened list put every
            // card in the wrong place, and the seat's card objects, never taken from the hand,
            // stayed full. Seen in batch B2: a Deck 2 table where this harness's seats held
            // seven cards on every screen, found none here, and passed their turns for ever.
            var held = gp.InHand();
            if (held.Count == 0)
            {
                Dev.Warn("bot", $"seat {p.Slot} has no cards - passing the turn instead");
                DevCommands.SkipTurn();
                return;
            }

            // Which card to throw is the front of the hand, except where a rarer card is
            // sitting in it - then that one goes first. A deck's special cards are the whole
            // point of the variant it belongs to, and throwing off the front meant a run
            // could go its whole length without one ever reaching the table, so the mechanic
            // that makes the mode a different game was never once exercised.
            var types = new System.Collections.Generic.List<int>();
            foreach (var h in held) types.Add(h.Card.Devil ? -1 : h.Card.cardtype);
            int pick = Special(types);
            int type = types[pick];

            var thrown = new Il2CppSystem.Collections.Generic.List<int>();
            thrown.Add(type);

            bool emptied = held.Count <= 1;
            Dev.Log("bot", $"seat {p.Slot} throws a {type}, {held.Count - 1} left" +
                           (emptied ? " - hand empty" : ""));

            // The server-side throw ends the turn and schedules the pass, as it does for a
            // person: these seats are real connections, so nothing here moves the turn on.
            // Then the card leaves the hand on every machine, through the server's own call.
            gp.Throw(thrown, emptied);
            gp.Hide(held[pick].Index);
            _lastThrower = p.Slot;
        }
        catch (Exception e)
        {
            Dev.Warn("bot", $"seat {p.Slot} could not play: {e.Message} - passing the turn instead");
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
    private static int Special(System.Collections.Generic.List<int> hand)
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
    /// Taken once a round, and only inside the loopback test harness: outside it nobody's cards
    /// but the player's own are looked at, and every card counts as rare. It drives nothing but
    /// which card a test throws first.
    /// </summary>
    private static void CountTheDeck()
    {
        if (!Fair.TestsAllowed) return;
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

    /// <summary>The seat whose throw stands on the table, from the manager actually running, or -1.</summary>
    private static int LastBidder()
    {
        try
        {
            var m = Manager.Instance;
            GameObject last = null;
            switch (TableHand.Playing())
            {
                case TableHand.Kind.Deck: last = m.DeckGamePlayManager != null ? m.DeckGamePlayManager.LastBetPlayer : null; break;
                case TableHand.Kind.ChaosDeck: last = m.ChaosDeckGame != null ? m.ChaosDeckGame.LastBetPlayer : null; break;
            }
            var ps = last != null ? last.GetComponent<PlayerStats>() : null;
            return ps != null ? ps.Slot : -1;
        }
        catch { return -1; }
    }

    private static bool WantsLiar(PlayerStats p)
    {
        try
        {
            if (TableHand.LiarCallInFlight()) return false;

            // Who to challenge is the game's own LastBetPlayer, written by the server-side
            // throw. Bots' throws reach that throw now that their seats are on the network,
            // and this harness does not see them: tracking only its own throws, it found its
            // own seat as the last thrower at a table of bots, and never called. Its own
            // record is kept for a table whose manager has none.
            int last = LastBidder();
            if (last < 0) last = _lastThrower;
            if (last < 0 || last == p.Slot) return false;

            var gp = TableHand.For(p);
            bool empty = gp == null || gp.InHand().Count == 0;

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

            int last = LastBidder();
            Dev.Log("bot", $"seat {p.Slot} calls LIAR on seat {(last >= 0 ? last : _lastThrower)} " +
                           $"(cards on table {TableHand.CardsOnTable()})");
            gp.CallLiar();
            _calledAt = Time.time;
        }
        catch (Exception e)
        {
            Dev.Warn("bot", $"seat {p.Slot} could not call liar: {e.Message} - throwing instead");
            try { Play(p); } catch { }
        }
    }
}
