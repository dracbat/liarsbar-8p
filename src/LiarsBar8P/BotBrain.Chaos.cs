using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiarsBar8P;

internal static partial class BotBrain
{
    /// <summary>
    /// The Chaos deck: throw one card as the round's card, or call the last throw a lie, and when
    /// the gun comes to the bot, aim it.
    ///
    /// The choice of card or call is Liar's Deck's, one card at a time, as the table allows. The
    /// chaos card is thrown whenever the bot holds it, sooner rather than later: nobody can call it,
    /// and it hands the thrower the gun.
    ///
    /// The gun comes to whoever threw the chaos card or caught a liar. A bot aims at somebody still
    /// in the match, never itself and never a seat that is out, picked at random but weighted to
    /// whoever has survived the most pulls of their own revolver, which everybody at the table has
    /// watched: each pull survived leaves fewer empty chambers. It aims once, after a moment's
    /// thought, the way the game's own keys set the aim, and the game fires when its countdown ends,
    /// as it does for a person. An aim the game has locked on somebody is left alone.
    /// </summary>
    private static class Chaos
    {
        private const int Joker = 4;

        /// <summary>The Chaos deck's own card. The round card there is only ever 1 or 2.</summary>
        private const int ChaosCard = 3;

        private const double CallBelow = 0.3;
        private const float RaiseWait = 2.5f;
        private const float HoldAfterThrow = 1.5f;
        private const float CallTaken = 2f;

        /// <summary>The share of the Chaos deck that is honest for a round: the round's card and jokers.</summary>
        private const double GoodShare = 0.45;

        /// <summary>How long a bot holding the gun takes to aim.</summary>
        private const float MinAim = 0.8f;
        private const float MaxAim = 1.8f;

        private static int _round;
        private static bool _wasSettling;

        internal static void MatchStarting()
        {
            _round = 0;
            _wasSettling = false;
        }

        internal static void Tick(Manager m)
        {
            var cm = m.ChaosDeckGame;
            if (cm == null) return;

            float now = Time.time;
            foreach (var seat in BotSeats.Seated())
            {
                if (seat.Chaos == null) continue;
                try { Aim(m, seat, MindOf(seat), now); }
                catch (Exception e) { Plugin.Log.LogWarning($"[bot] seat {seat.Player.Slot} could not aim: {e.Message}"); }
            }

            // A call, a chaos card or a shot being settled, or the round being dealt: no move then.
            bool settling;
            try { settling = BotTurnGuard.Settling(m, cm); }
            catch (Exception e) { CannotTell("the Chaos deck", e); return; }
            if (_wasSettling && !settling) _round++;
            _wasSettling = settling;
            BotTurnGuard.HeldWhileSettling(settling && BotTurnGuard.ABotHolds(), () => BotTurnGuard.Flags(cm));

            foreach (var seat in BotSeats.Seated())
            {
                var gp = seat.Chaos;
                if (gp == null) continue;
                var p = seat.Player;
                var mind = MindOf(seat);

                if (p.Dead || !p.HaveTurn || settling)
                {
                    Idle(mind);
                    seat.Looking = !p.Dead && now < mind.LookUntil;
                    continue;
                }
                seat.Looking = !cm.BlocksLookInput && !(TestNoLook && Fair.TestsAllowed);

                try
                {
                    if (mind.Throw != null) { FinishThrow(m, seat, mind, now); continue; }
                    if (mind.CalledAt > 0f)
                    {
                        if (now - mind.CalledAt < CallTaken) continue;
                        mind.CalledAt = -1f;
                        Plugin.Log.LogInfo($"[bot] seat {p.Slot}'s call was not taken - it throws instead");
                        Choose(cm, seat, mind, mustThrow: true);
                        continue;
                    }

                    string moment = $"{_round}:{cm.CardsOnTable}:{SeatOf(cm.LastBetPlayer)}";
                    if (!Ready(mind, p, m, moment)) continue;

                    if (TestFault == Fault.Move && Fair.TestsAllowed) throw new InvalidOperationException("test: the move fails");
                    Choose(cm, seat, mind, mustThrow: false);
                }
                catch (Exception e)
                {
                    mind.Throw = null;
                    Plugin.Log.LogWarning($"[bot] seat {p.Slot} could not move in the Chaos deck: {e.Message}");
                    BotTurnGuard.Trip($"a bot's move in the Chaos deck failed ({e.Message})");
                }
            }
        }

        // ---------------------------------------------------------------- the gun

        /// <summary>
        /// Holding the gun: aim once, a moment after it arrives, unless the game has locked the aim.
        /// A seat whose shot has gone is not aiming any more, though its mark can stay up a moment
        /// until <see cref="BotPuppet"/> takes it down as its own copy would.
        /// </summary>
        private static void Aim(Manager m, BotSeats.Seat seat, Mind mind, float now)
        {
            var gp = seat.Chaos;
            bool aiming = gp.TakingAim && !seat.Player.Dead && seat.FiredAt < 0f;
            if (!aiming)
            {
                mind.WasAiming = false;
                mind.AimDue = -1f;
                return;
            }

            // A new turn with the gun: aim once, after a moment's thought.
            if (!mind.WasAiming)
            {
                mind.WasAiming = true;
                mind.AimDue = now + MinAim + (float)mind.Rng.NextDouble() * (MaxAim - MinAim);
                return;
            }
            if (mind.AimDue < 0f || now < mind.AimDue) return;
            mind.AimDue = -1f;

            if (gp.AimLocked)
            {
                Dev.Log("bot", $"seat {seat.Player.Slot}'s aim is locked by the game - left where it is");
                return;
            }

            var target = Target(m, seat, mind);
            if (target == null)
            {
                Plugin.Log.LogInfo($"[bot] seat {seat.Player.Slot} has the gun and nobody to aim at");
                return;
            }

            int aim = gp.GetAimDirectionForSlot(target.Slot);
            gp.NetworkAim = aim;
            AimRing.ShowPose(gp, AimRing.Seats(m), aim);
            Plugin.Log.LogInfo($"[bot] seat {seat.Player.Slot} aims at seat {target.Slot} " +
                               $"({target.GetComponent<ChaosDeckGameplay>().currentrevoler} pull(s) survived)");
        }

        /// <summary>
        /// Somebody else still in the match, at random, weighted to whoever has survived the most
        /// pulls of their own revolver, a count the whole table has watched.
        /// </summary>
        private static PlayerStats Target(Manager m, BotSeats.Seat seat, Mind mind)
        {
            var who = new List<PlayerStats>();
            var weight = new List<double>();
            double total = 0;
            foreach (var q in m.Players)
            {
                if (q == null || q.Dead || q.Pointer == seat.Player.Pointer) continue;
                var gp = q.GetComponent<ChaosDeckGameplay>();
                int survived = gp != null ? Math.Max(0, gp.currentrevoler) : 0;
                double w = 1.0 + 1.5 * survived;
                who.Add(q);
                weight.Add(w);
                total += w;
            }
            if (who.Count == 0) return null;

            double roll = mind.Rng.NextDouble() * total;
            for (int i = 0; i < who.Count; i++)
            {
                roll -= weight[i];
                if (roll <= 0) return who[i];
            }
            return who[who.Count - 1];
        }

        // ---------------------------------------------------------------- the card or the call

        private static void Choose(ChaosDeckGamePlayManager cm, BotSeats.Seat seat, Mind mind, bool mustThrow)
        {
            var gp = seat.Chaos;
            var p = seat.Player;
            var hand = Cards.Of(gp.Cards);
            int round = cm.RoundCard;

            int last = SeatOf(cm.LastBetPlayer);
            bool canCall = !mustThrow && last >= 0 && last != p.Slot && cm.LastRoundCount > 0;
            if (canCall)
            {
                double honest = Honest(cm, hand, round);
                if (hand.Count == 0 || honest < CallBelow + mind.Suspicion || (TestCallAll && Fair.TestsAllowed))
                {
                    Dev.Log("bot", $"seat {p.Slot} calls LIAR on seat {last}");
                    mind.CalledAt = Time.time;
                    Voice(seat, "liar", () => gp.PlayLiarCommand());
                    gp.RequestCallLiar();
                    return;
                }
            }
            if (hand.Count == 0) return;

            // One card: the chaos card when it holds it, most of the time, then an honest one, then a lie.
            var chaos = hand.Find(c => c.Type == ChaosCard);
            var good = hand.FindAll(c => c.Type == round || c.Type == Joker);
            var bad = hand.FindAll(c => c.Type != round && c.Type != Joker && c.Type != ChaosCard);

            Cards.Held pick;
            if (chaos != null && (mind.Rng.NextDouble() < 0.7 || good.Count == 0 && bad.Count == 0)) pick = chaos;
            else if (good.Count > 0 && !(bad.Count > 0 && mind.Rng.NextDouble() < mind.Bluff)) pick = good[mind.Rng.Next(good.Count)];
            else if (bad.Count > 0) pick = bad[mind.Rng.Next(bad.Count)];
            else pick = chaos ?? hand[0];

            Cards.Select(gp.Cards, gp.selectedCards, new List<Cards.Held> { pick });
            mind.Throw = new List<GameObject> { pick.Go };
            mind.ThrowSince = Time.time;
            Dev.Log("bot", $"seat {p.Slot} throws a card{(Fair.TestsAllowed && pick.Type == ChaosCard ? ", the chaos card" : "")}");
        }

        /// <summary>The chance the last card thrown is the round's card or a joker, from what this chair can see.</summary>
        private static double Honest(ChaosDeckGamePlayManager cm, List<Cards.Held> hand, int round)
        {
            int held = 0;
            var m = Manager.Instance;
            if (m != null && m.Players != null)
                foreach (var q in m.Players)
                {
                    var gp = q != null && !q.Dead ? q.GetComponent<ChaosDeckGameplay>() : null;
                    if (gp != null) held += Cards.Count(gp.Cards);
                }
            int inPlay = Math.Max(1, cm.CardsOnTable + held);
            int mineGood = hand.FindAll(c => c.Type == round || c.Type == Joker).Count;
            int unseen = Math.Max(1, inPlay - hand.Count);
            double each = (GoodShare * inPlay - mineGood) / unseen;
            each = Math.Min(0.98, Math.Max(0.02, each));
            return Math.Pow(each, Math.Max(1, cm.LastRoundCount));
        }

        private static void FinishThrow(Manager m, BotSeats.Seat seat, Mind mind, float now)
        {
            var gp = seat.Chaos;
            float clock = 99f;
            try { clock = m.CountDown; } catch { }

            // As in Liar's Deck: a turn still here a moment after the game's own throw did not throw.
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
            if (now - mind.ThrowSince < RaiseWait && !(mind.ClockSeen && clock < LastMoment)) return;
            Plugin.Log.LogInfo($"[bot] seat {seat.Player.Slot}'s cards did not come up - it throws without the look");
            Unlooked(seat, mind, now);
        }

        /// <summary>
        /// The chosen card thrown through the server-side throw a person's throw reaches, then taken
        /// out of the hand on every machine through the server's own call for it, so the hand is
        /// right whether or not an animation plays for the throw.
        /// </summary>
        private static void Unlooked(BotSeats.Seat seat, Mind mind, float now)
        {
            var gp = seat.Chaos;
            var types = new Il2CppSystem.Collections.Generic.List<int>();
            foreach (var go in mind.Throw)
            {
                var card = go != null ? go.GetComponent<Card>() : null;
                if (card != null && go.activeSelf) types.Add(card.cardtype);
            }
            var places = Cards.Places(gp.Cards, mind.Throw);
            mind.Throw = null;
            mind.ThrewAt = 0f;
            if (types.Count == 0) return;

            int held = Cards.Count(gp.Cards);
            bool emptied = held <= types.Count;
            mind.LookUntil = now + HoldAfterThrow;
            gp.RequestThrowCards(types, emptied);
            gp.ServerChaosHideCards(places);
            Dev.Log("bot", $"seat {seat.Player.Slot} threw a card without the look: {held} in its hand, {Cards.Count(gp.Cards)} now");
        }
    }
}
