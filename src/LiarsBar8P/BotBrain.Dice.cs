using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiarsBar8P;

internal static partial class BotBrain
{
    /// <summary>
    /// Liar's Dice, Basic and Traditional: raise the bid, call it a lie, or call it spot on.
    ///
    /// What the bot knows is what its chair knows: its own dice, the standing bid, how many dice
    /// each player still in the match holds, and the rules. From those it works out how likely the
    /// bid is: the bid's face among its own dice, plus the chance that enough of everybody else's
    /// dice show it, each with one chance in six, or one in three in Traditional, where ones count
    /// as every face. Dice of players who are out do not count, as they do not in the game's own
    /// count at the reveal.
    ///
    /// It calls a liar when the bid is less likely than about one in three, spot on when it would
    /// often be exactly right, and otherwise makes the smallest raise it believes most: the same
    /// number of a higher face, or one more die of any face. Now and then, by its own bluffing
    /// habit, it raises on a face it does not hold. It bids only faces two to six, and never more
    /// than the game's own bid arrows let a person bid.
    ///
    /// Its moves go to the same server-side bodies a person's keys reach: the bid, then the end of
    /// its turn, then handing the turn on, which is the order the game's own input uses. The voice
    /// lines that go with them are sent the way a person's copy sends them, as commands, which
    /// <see cref="BotSeats"/> runs on the host, each on its own so that a line that fails never
    /// stops the move it goes with.
    /// </summary>
    private static class Dice
    {
        /// <summary>
        /// The game's bid arrows stop here once a bid is standing, whatever the table size, so a
        /// person can never bid past it and neither does a bot.
        /// </summary>
        private const int HighestBid = 20;

        /// <summary>The usual dice per player, for a seat that has not been told its count.</summary>
        private const int DicePerPlayer = 5;

        /// <summary>A bid less likely than this, plus the bot's suspicion, is called a lie.</summary>
        private const double CallBelow = 0.3;

        /// <summary>Spot on when the bid would be exactly right at least this often.</summary>
        private const double SpotOnFrom = 0.33;

        /// <summary>
        /// An opening bid the bot believes at least this much, less half its boldness. Openings at
        /// even odds were called at once, so a round was two bids; this leaves the table room to raise.
        /// </summary>
        private const double OpenAt = 0.75;

        /// <summary>A bluff is a raise it does not believe, but never one less likely than this.</summary>
        private const double BluffFloor = 0.2;

        private static int _round;
        private static bool _lastBet, _lastResolving;

        internal static void MatchStarting()
        {
            _round = 0;
            _lastBet = false;
            _lastResolving = false;
        }

        internal static void Tick(Manager m)
        {
            var d = m.DiceGame;
            if (d == null) return;

            // Each round starts with no bid standing, after a call has been settled.
            bool bet = d.BetPlaced;
            bool resolving;
            try { resolving = BotTurnGuard.Settling(d); }
            catch (Exception e) { CannotTell("Liar's Dice", e); return; }
            if ((_lastBet && !bet) || (_lastResolving && !resolving)) _round++;
            _lastBet = bet;
            _lastResolving = resolving;

            foreach (var seat in BotSeats.Seated())
            {
                var p = seat.Player;
                var mind = MindOf(seat);
                if (p.Dead || !p.HaveTurn || resolving) { Idle(mind); continue; }

                var gp = seat.Dice;
                var own = gp != null ? gp.DiceValues : null;
                if (own == null || own.Count == 0) continue;   // not rolled yet

                string moment = $"{_round}:{bet}:{d.LastCount}:{d.LastDice}";
                if (!Ready(mind, p, m, moment)) continue;

                // A move that throws is not tried again at this moment: BotTurnGuard passes the turn on.
                try
                {
                    if (TestFault == Fault.Move && Fair.TestsAllowed) throw new InvalidOperationException("test: the move fails");
                    Move(m, d, seat, mind);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[bot] seat {p.Slot} could not move in Liar's Dice: {e.Message}");
                    BotTurnGuard.Trip($"a bot's move in Liar's Dice failed ({e.Message})");
                }
            }
        }

        /// <summary>What a chair can see: its own dice, and how many dice everybody else holds.</summary>
        private sealed class View
        {
            internal readonly List<int> Mine = new();
            internal int Others;
            internal bool Traditional;

            /// <summary>How many of the bot's own dice count for this face.</summary>
            internal int Have(int face)
            {
                int n = 0;
                foreach (int v in Mine)
                    if (v == face || (Traditional && face != 1 && v == 1)) n++;
                return n;
            }

            /// <summary>The chance that one of somebody else's dice counts for this face.</summary>
            internal double Each(int face) => Traditional && face != 1 ? 1.0 / 3.0 : 1.0 / 6.0;

            /// <summary>The chance that at least this many dice on the table show the face.</summary>
            internal double AtLeast(int count, int face)
            {
                int need = count - Have(face);
                return need <= 0 ? 1.0 : Tail(Others, Each(face), need);
            }

            /// <summary>The chance that exactly this many do.</summary>
            internal double Exactly(int count, int face)
            {
                int need = count - Have(face);
                return need < 0 ? 0.0 : Point(Others, Each(face), need);
            }
        }

        private static View Look(Manager m, DiceGamePlayManager d, BotSeats.Seat seat)
        {
            var view = new View { Traditional = (int)d.DiceMode == 1 };
            foreach (int v in seat.Dice.DiceValues) view.Mine.Add(v);

            foreach (var q in m.Players)
            {
                if (q == null || q.Dead || q.Pointer == seat.Player.Pointer) continue;
                var hand = q.GetComponent<DiceGamePlay>();
                int held = hand != null ? hand.TotalDices : 0;
                view.Others += held > 0 ? held : DicePerPlayer;
            }
            return view;
        }

        private static void Move(Manager m, DiceGamePlayManager d, BotSeats.Seat seat, Mind mind)
        {
            var p = seat.Player;
            var view = Look(m, d, seat);
            int onTable = view.Others + view.Mine.Count;

            bool standing = d.BetPlaced && d.LastCount > 0;
            int arrows = d.MaxCount > 0 ? d.MaxCount : onTable;
            int cap = Math.Min(standing ? Math.Min(arrows, HighestBid) : arrows, onTable);
            if (cap < 1) cap = 1;

            if (!standing)
            {
                Open(seat, mind, view, cap);
                return;
            }

            int count = d.LastCount, face = d.LastDice;
            double likely = count > onTable ? 0.0 : view.AtLeast(count, face);

            bool canSpot = false;
            try { canSpot = d.CanSpotOn; } catch { }
            if (canSpot && (view.Exactly(count, face) >= SpotOnFrom || (TestSpotOn && Fair.TestsAllowed)))
            {
                SpotOn(seat, count, face);
                return;
            }

            if (likely < CallBelow + mind.Suspicion) { Liar(seat, count, face); return; }

            // Every smallest raise: the same count on a higher face, or one more die of any face.
            var raises = new List<(int count, int face, double p)>();
            for (int f = Math.Max(2, face + 1); f <= 6 && count <= cap; f++) raises.Add((count, f, view.AtLeast(count, f)));
            if (count + 1 <= cap)
                for (int f = 2; f <= 6; f++) raises.Add((count + 1, f, view.AtLeast(count + 1, f)));

            if (raises.Count == 0) { Liar(seat, count, face); return; }

            var pick = raises[0];
            foreach (var r in raises) if (r.p > pick.p) pick = r;

            // Raising on what it believes least, now and then: a bluff, but never a hopeless one.
            if (mind.Rng.NextDouble() < mind.Bluff)
            {
                var bluffs = raises.FindAll(r => r.p >= BluffFloor && r.p < pick.p);
                if (bluffs.Count > 0) pick = bluffs[mind.Rng.Next(bluffs.Count)];
            }

            // A raise it believes less than the bid is false is worse than calling the bid.
            if (pick.p < 1.0 - likely && likely < 0.5) { Liar(seat, count, face); return; }

            Bid(seat, pick.count, pick.face);
        }

        /// <summary>The first bid of a round: its commonest face, as many as it believes.</summary>
        private static void Open(BotSeats.Seat seat, Mind mind, View view, int cap)
        {
            int face = 2;
            for (int f = 3; f <= 6; f++) if (view.Have(f) >= view.Have(face)) face = f;
            if (mind.Rng.NextDouble() < mind.Bluff) face = 2 + mind.Rng.Next(5);

            double believe = OpenAt - mind.Bluff / 2;
            int count = 1;
            for (int c = 2; c <= cap; c++)
            {
                if (view.AtLeast(c, face) < believe) break;
                count = c;
            }
            Bid(seat, count, face);
        }

        // ---------------------------------------------------------------- the moves

        private static void Bid(BotSeats.Seat seat, int count, int face)
        {
            var gp = seat.Dice;
            Dev.Log("bot", $"seat {seat.Player.Slot} bids {count} x {face}");
            gp.UserCode_PlaceBet__Int32__Int32(count, face);
            Voice(seat, "raise", () => gp.PlayOnRiseCMD());
            gp.UserCode_ResetTurn();
            gp.UserCode_GiveTurn();
        }

        private static void Liar(BotSeats.Seat seat, int count, int face)
        {
            var gp = seat.Dice;
            Dev.Log("bot", $"seat {seat.Player.Slot} calls LIAR on {count} x {face}");
            Voice(seat, "liar", () => gp.PlayLiarCMD());
            gp.UserCode_CallLier();
        }

        private static void SpotOn(BotSeats.Seat seat, int count, int face)
        {
            var gp = seat.Dice;
            Plugin.Log.LogInfo($"[bot] seat {seat.Player.Slot} calls SPOT ON on {count} x {face}");
            Voice(seat, "spot on", () => gp.PlaySpotOnCMD());
            gp.UserCode_CallSpotOn();
        }

        // ---------------------------------------------------------------- chances

        /// <summary>P(X = k) for X the number of successes in n tries at p each.</summary>
        private static double Point(int n, double p, int k)
        {
            if (k < 0 || k > n) return 0.0;
            double ln = LogChoose(n, k) + k * Math.Log(p) + (n - k) * Math.Log(1.0 - p);
            return Math.Exp(ln);
        }

        /// <summary>P(X &gt;= k).</summary>
        private static double Tail(int n, double p, int k)
        {
            if (k <= 0) return 1.0;
            if (k > n) return 0.0;
            double sum = 0.0;
            for (int i = k; i <= n; i++) sum += Point(n, p, i);
            return Math.Min(1.0, sum);
        }

        private static double LogChoose(int n, int k)
        {
            double s = 0.0;
            for (int i = 1; i <= k; i++) s += Math.Log(n - k + i) - Math.Log(i);
            return s;
        }
    }
}
