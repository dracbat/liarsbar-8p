using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiarsBar8P;

internal static partial class BotBrain
{
    /// <summary>
    /// Liar's Deck, Basic, Devil and Deck 2: throw one to three cards as the round's card, or call
    /// the last throw a lie, or, at the Devil table, make the Devil's Deal.
    ///
    /// What the bot knows is what its chair knows: the cards in its own hand, the round's card, how
    /// many cards each player holds and how many lie in the middle, and how many the last player
    /// threw. From those it works out how likely the last throw is to be honest: each of the cards
    /// nobody has shown it is the round's card or a joker about as often as the deck holds them,
    /// less the ones in its own hand. It calls a liar when that is unlikely, by its own suspicion,
    /// and otherwise throws: honest cards when it has them, now and then with a lie slipped in by
    /// its own bluffing habit, and one or two lies when it has none. The devil card is thrown on
    /// its own, as the game demands, and a bot holding it plays it now and then as the trap it is.
    /// The Devil's Deal is made only when the game allows it and the bot, not holding the devil
    /// card itself, thinks the card more likely in the middle than not.
    ///
    /// Its throw is a person's: its cards come up as if it held the look key, it picks them, and
    /// the game's own throw does the rest, the animation, the cards leaving its hand and landing on
    /// the table, and the turn passing on. If its cards are not up within a few seconds, or its clock
    /// runs low first, it throws without the look, through the same server-side throw. Its calls go
    /// to the server-side bodies a person's keys reach, and the voice lines that go with them are
    /// said on their own, so a line that fails never stops the move.
    /// </summary>
    private static class Deck
    {
        /// <summary>The card that stands for any round card, and the devil card's place in a throw.</summary>
        private const int Joker = 4;
        private const int DevilCard = -1;

        /// <summary>The game's own limit on one throw.</summary>
        private const int MostPerThrow = 3;

        /// <summary>A throw less likely to be honest than this, plus the bot's suspicion, is called.</summary>
        private const double CallBelow = 0.3;

        /// <summary>How long a bot waits for its cards to come up before throwing without the look.</summary>
        private const float RaiseWait = 3f;

        /// <summary>How long its cards stay up after a throw, for the animation to play out.</summary>
        private const float HoldAfterThrow = 1.5f;

        /// <summary>A call the game has not taken after this long is followed by a throw.</summary>
        private const float CallTaken = 2f;

        /// <summary>How often a bot holding the devil card plays it when it has something else to play.</summary>
        private const double DevilRate = 0.35;

        /// <summary>The Devil's Deal, made only when the devil card is likelier in the middle than this.</summary>
        private const double DealFrom = 0.55;

        private static int _round;
        private static bool _wasSettling;

        internal static void MatchStarting()
        {
            _round = 0;
            _wasSettling = false;
        }

        internal static void Tick(Manager m)
        {
            var dm = m.DeckGamePlayManager;
            if (dm == null) return;
            float now = Time.time;

            // A call being settled, or the round being dealt: no move then. The caller keeps the turn
            // while its call is settled, so this is what keeps a bot from moving in the middle of one.
            bool settling;
            try { settling = BotTurnGuard.Settling(dm); }
            catch (Exception e)
            {
                CannotTell("Liar's Deck", e);
                foreach (var seat in BotSeats.Seated()) LetGoOfThrow(seat, MindOf(seat), now);
                return;
            }
            if (_wasSettling && !settling) _round++;
            _wasSettling = settling;
            BotTurnGuard.HeldWhileSettling(settling && BotTurnGuard.ABotHolds(), () => BotTurnGuard.Flags(dm));

            foreach (var seat in BotSeats.Seated())
            {
                var gp = seat.Deck;
                if (gp == null) continue;
                var p = seat.Player;
                var mind = MindOf(seat);
                LetGoOfThrow(seat, mind, now);

                if (p.Dead || !p.HaveTurn || settling)
                {
                    Idle(mind);
                    seat.Looking = !p.Dead && now < mind.LookUntil;
                    continue;
                }

                // A turn: its cards come up while it thinks, as a person holds the look key.
                seat.Looking = !dm.BlocksLookInput && !(TestNoLook && Fair.TestsAllowed);

                try
                {
                    if (mind.Throw != null) { FinishThrow(m, seat, mind, now); continue; }
                    if (mind.CalledAt > 0f)
                    {
                        if (now - mind.CalledAt < CallTaken) continue;
                        // The game did not take the call: throw instead, at once.
                        mind.CalledAt = -1f;
                        Plugin.Log.LogInfo($"[bot] seat {p.Slot}'s call was not taken - it throws instead");
                        Choose(dm, seat, mind, mustThrow: true);
                        continue;
                    }

                    string moment = $"{_round}:{dm.CardsOnTable}:{SeatOf(dm.LastBetPlayer)}";
                    if (!Ready(mind, p, m, moment)) continue;

                    if (TestFault == Fault.Move && Fair.TestsAllowed) throw new InvalidOperationException("test: the move fails");
                    Choose(dm, seat, mind, mustThrow: false);
                }
                catch (Exception e)
                {
                    mind.Throw = null;
                    Plugin.Log.LogWarning($"[bot] seat {p.Slot} could not move in Liar's Deck: {e.Message}");
                    BotTurnGuard.Trip($"a bot's move in Liar's Deck failed ({e.Message})");
                }
            }
        }

        // ---------------------------------------------------------------- the choice

        private static void Choose(DeckGamePlayManager dm, BotSeats.Seat seat, Mind mind, bool mustThrow)
        {
            var gp = seat.Deck;
            var p = seat.Player;
            var hand = Cards.Of(gp.Cards);
            int round = dm.RoundCard;
            bool devilTable = dm.DeckMode == DeckGamePlayManager.deckmode.Devil;

            int last = SeatOf(dm.LastBetPlayer);
            bool canCall = !mustThrow && last >= 0 && last != p.Slot && dm.LastRoundCount > 0;

            if (canCall)
            {
                // The Devil's Deal: the game's own test first, which shows nothing on anybody's screen.
                bool holdsDevil = hand.Exists(c => c.Devil);
                if (devilTable && !holdsDevil && gp.CanCallDevilsDeal())
                {
                    double inMiddle = DevilInMiddle(dm, seat, hand.Count);
                    if ((inMiddle >= DealFrom && mind.Rng.NextDouble() < 0.5) || (TestDeal && Fair.TestsAllowed))
                    {
                        Plugin.Log.LogInfo($"[bot] seat {p.Slot} makes the Devil's Deal ({dm.CardsOnTable} cards in the middle)");
                        mind.CalledAt = Time.time;
                        gp.TryCallSpotOn();
                        return;
                    }
                }

                double honest = Honest(dm, seat, hand, round);
                if (hand.Count == 0 || honest < CallBelow + mind.Suspicion || (TestCallAll && Fair.TestsAllowed))
                {
                    Dev.Log("bot", $"seat {p.Slot} calls LIAR on seat {last}'s {dm.LastRoundCount} card(s)");
                    mind.CalledAt = Time.time;
                    Voice(seat, "liar", () => gp.PlayLiarCommand());
                    gp.RequestCallLiar();
                    return;
                }
            }

            if (hand.Count == 0) return;   // nothing to throw and nothing to call: the guard passes it on

            var pick = Pick(hand, round, mind, devilTable);
            Begin(seat, mind, pick, dm.DeckMode == DeckGamePlayManager.deckmode.Deck2);
        }

        /// <summary>
        /// The chance the last throw is all the round's card or jokers, from what this chair can see:
        /// the cards in play, the deck's usual share of good cards, and its own hand.
        /// </summary>
        private static double Honest(DeckGamePlayManager dm, BotSeats.Seat seat, List<Cards.Held> hand, int round)
        {
            int inPlay = Math.Max(1, dm.CardsOnTable + CardsHeld(seat));
            double share = dm.DeckMode == DeckGamePlayManager.deckmode.Deck2 ? 12.0 / 28.0 : 8.0 / 20.0;
            int mineGood = hand.FindAll(c => !c.Devil && (c.Type == round || c.Type == Joker)).Count;
            int unseen = Math.Max(1, inPlay - hand.Count);
            double each = (share * inPlay - mineGood) / unseen;
            each = Math.Min(0.98, Math.Max(0.02, each));
            return Math.Pow(each, Math.Max(1, dm.LastRoundCount));
        }

        /// <summary>
        /// The devil card is in the middle about as often as the middle's share of the cards this bot
        /// cannot see: the middle and every other hand, by their counts.
        /// </summary>
        private static double DevilInMiddle(DeckGamePlayManager dm, BotSeats.Seat seat, int mine)
        {
            int middle = dm.CardsOnTable;
            int unseen = Math.Max(1, middle + CardsHeld(seat) - mine);
            return Math.Min(1.0, middle / (double)unseen);
        }

        /// <summary>Every card held at the table, a public count: each hand shows how many cards it has.</summary>
        private static int CardsHeld(BotSeats.Seat seat)
        {
            int n = 0;
            var m = Manager.Instance;
            if (m == null || m.Players == null) return n;
            foreach (var q in m.Players)
            {
                if (q == null || q.Dead) continue;
                var gp = q.GetComponent<DeckGameplay>();
                if (gp != null) n += Cards.Count(gp.Cards);
            }
            return n;
        }

        /// <summary>What to throw: the devil card alone, honest cards, a lie slipped in, or lies.</summary>
        private static List<Cards.Held> Pick(List<Cards.Held> hand, int round, Mind mind, bool devilTable)
        {
            var devil = hand.Find(c => c.Devil);
            var good = hand.FindAll(c => !c.Devil && (c.Type == round || c.Type == Joker));
            var bad = hand.FindAll(c => !c.Devil && c.Type != round && c.Type != Joker);

            // The devil card goes alone: when it is all the bot has, or now and then as a trap.
            if (devil != null && (good.Count == 0 && bad.Count == 0 || mind.Rng.NextDouble() < DevilRate))
                return new List<Cards.Held> { devil };

            var pick = new List<Cards.Held>();
            if (good.Count > 0)
            {
                int n = 1 + mind.Rng.Next(Math.Min(MostPerThrow, good.Count));
                Shuffle(good, mind.Rng);
                pick.AddRange(good.GetRange(0, n));
                if (bad.Count > 0 && pick.Count < MostPerThrow && mind.Rng.NextDouble() < mind.Bluff)
                    pick.Add(bad[mind.Rng.Next(bad.Count)]);
                return pick;
            }

            if (bad.Count > 0)
            {
                Shuffle(bad, mind.Rng);
                int n = bad.Count > 1 && mind.Rng.NextDouble() < 0.25 ? 2 : 1;
                pick.AddRange(bad.GetRange(0, n));
                return pick;
            }

            // Only the devil card left to throw.
            if (devil != null) pick.Add(devil);
            return pick;
        }

        // ---------------------------------------------------------------- the throw

        /// <summary>The cards chosen, picked out in the hand, waiting for the bot's cards to come up.</summary>
        private static void Begin(BotSeats.Seat seat, Mind mind, List<Cards.Held> pick, bool keptHands)
        {
            if (pick.Count == 0) return;
            var gp = seat.Deck;
            Cards.Select(gp.Cards, keptHands ? gp.selectedCards : null, pick);
            mind.Throw = new List<GameObject>();
            foreach (var c in pick) mind.Throw.Add(c.Go);
            mind.ThrowSince = Time.time;
            Dev.Log("bot", $"seat {seat.Player.Slot} throws {pick.Count} card(s){(Fair.TestsAllowed && pick.Exists(c => c.Devil) ? ", the devil card" : "")}");
        }

        private static void FinishThrow(Manager m, BotSeats.Seat seat, Mind mind, float now)
        {
            var gp = seat.Deck;
            float clock = 99f;
            try { clock = m.CountDown; } catch { }

            // The game's own throw ends the turn in the same call, so a turn still here a moment
            // later is a throw that did not go.
            if (mind.ThrewAt > 0f)
            {
                if (now - mind.ThrewAt < 1f) return;
                Plugin.Log.LogInfo($"[bot] seat {seat.Player.Slot}'s throw did not go - it throws without the look");
                Unlooked(seat, mind, now);
                return;
            }

            if (gp.CanUseLookTableActions())
            {
                mind.ThrewAt = now;
                mind.LookUntil = now + HoldAfterThrow;
                gp.ThrowCards();
                return;
            }

            // Not up yet: wait, unless the wait or the clock has run out.
            if (now - mind.ThrowSince < RaiseWait && !(mind.ClockSeen && clock < LastMoment)) return;
            Plugin.Log.LogInfo($"[bot] seat {seat.Player.Slot}'s cards did not come up - it throws without the look");
            Unlooked(seat, mind, now);
        }

        /// <summary>
        /// The chosen cards thrown through the server-side throw a person's throw reaches, with the
        /// throwing animation set by hand for the throw to be seen. The animation may not start from
        /// a hand whose cards never came up, so the cards are taken out of the hand on every machine
        /// here, through the server's own call for it, and the hand is right whether it plays or not.
        /// </summary>
        private static void Unlooked(BotSeats.Seat seat, Mind mind, float now)
        {
            var gp = seat.Deck;
            var types = new Il2CppSystem.Collections.Generic.List<int>();
            int thrown = 0;
            foreach (var go in mind.Throw)
            {
                var card = go != null ? go.GetComponent<Card>() : null;
                if (card == null || !go.activeSelf) continue;
                types.Add(card.Devil ? DevilCard : card.cardtype);
                thrown++;
            }
            var places = Cards.Places(gp.Cards, mind.Throw);
            mind.Throw = null;
            mind.ThrewAt = 0f;
            if (thrown == 0) return;

            int held = Cards.Count(gp.Cards);
            bool emptied = held <= thrown;
            mind.LookUntil = now + HoldAfterThrow;
            mind.ThrowSetAt = now;
            gp.animator.SetBool("Throw", true);
            gp.RequestThrowCards(types, emptied);
            gp.ServerHideCards(places);
            Dev.Log("bot", $"seat {seat.Player.Slot} threw {thrown} card(s) without the look: {held} in its hand, {Cards.Count(gp.Cards)} now");
        }

        /// <summary>The throw set without the look is let go of again once its animation has started.</summary>
        private static void LetGoOfThrow(BotSeats.Seat seat, Mind mind, float now)
        {
            if (mind.ThrowSetAt <= 0f || now - mind.ThrowSetAt < 1f) return;
            mind.ThrowSetAt = 0f;
            try { seat.Body.animator.SetBool("Throw", false); } catch { }
        }
    }

    // -------------------------------------------------------------------- shared by both decks

    /// <summary>A seat's slot from its player object, or -1.</summary>
    private static int SeatOf(GameObject go)
    {
        try
        {
            var p = go != null ? go.GetComponent<PlayerStats>() : null;
            return p != null ? p.Slot : -1;
        }
        catch { return -1; }
    }

    private static void Shuffle<T>(List<T> list, System.Random rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>A hand of cards as the seat holds them: the card objects still in it, and what each is.</summary>
    private static class Cards
    {
        internal sealed class Held
        {
            internal GameObject Go;
            internal Card Card;
            internal int Type;
            internal bool Devil;
        }

        /// <summary>The cards still in a hand: the hand's card objects that are switched on.</summary>
        internal static List<Held> Of(Il2CppSystem.Collections.Generic.List<GameObject> cards)
        {
            var hand = new List<Held>();
            if (cards == null) return hand;
            foreach (var go in cards)
            {
                if (go == null || !go.activeSelf) continue;
                var card = go.GetComponent<Card>();
                if (card == null) continue;
                hand.Add(new Held { Go = go, Card = card, Type = card.cardtype, Devil = card.Devil });
            }
            return hand;
        }

        /// <summary>How many cards a hand still holds, which every player at the table can see.</summary>
        internal static int Count(Il2CppSystem.Collections.Generic.List<GameObject> cards)
        {
            int n = 0;
            if (cards == null) return n;
            foreach (var go in cards) if (go != null && go.activeSelf) n++;
            return n;
        }

        /// <summary>
        /// Where the given cards sit in the hand, those still in it: the places the game's own
        /// taking of cards from a hand goes by.
        /// </summary>
        internal static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> Places(
            Il2CppSystem.Collections.Generic.List<GameObject> cards, List<GameObject> these)
        {
            var at = new List<int>();
            for (int i = 0; cards != null && i < cards.Count; i++)
            {
                var go = cards[i];
                if (go == null || !go.activeSelf) continue;
                if (these.Exists(t => t != null && t.Pointer == go.Pointer)) at.Add(i);
            }
            var places = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(at.Count);
            for (int i = 0; i < at.Count; i++) places[i] = at[i];
            return places;
        }

        /// <summary>
        /// Exactly the chosen cards picked out, as a person's clicks pick them: each card's own mark,
        /// and the hand's list of picked cards where the table keeps one.
        /// </summary>
        internal static void Select(Il2CppSystem.Collections.Generic.List<GameObject> cards,
                                    Il2CppSystem.Collections.Generic.List<GameObject> picked, List<Held> pick)
        {
            foreach (var go in cards)
            {
                var card = go != null ? go.GetComponent<Card>() : null;
                if (card != null) card.Selected = false;
            }
            picked?.Clear();
            foreach (var c in pick)
            {
                c.Card.Selected = true;
                picked?.Add(c.Go);
            }
        }
    }
}
