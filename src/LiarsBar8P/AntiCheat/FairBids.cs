using System;
using System.Text;
using HarmonyLib;
using Mirror;

namespace LiarsBar8P;

/// <summary>
/// Fair play for the bids of Liar's Dice.
///
/// <b>What it does.</b> At a table this copy hosts, every bid a player's copy places is checked by
/// the host before the game takes it: a face a die has, a count a table can hold, on the bidder's
/// own turn, from a seat still in the game, and higher than the bid standing on the table by the
/// game's own rule. The bid the table sees, and hears read out, is then always one the rules allow.
///
/// <b>What it never does.</b> It never runs for a bot or the host's own seat, never in ranked, and
/// an accepted bid runs through the game's own handler unchanged, so a player without the mod
/// sees and hears exactly what they always did.
///
/// <b>Refused or caught.</b> The game's own bid picker always offers a face of one to six and a
/// count no larger than five dice a seat at the biggest table; a value outside that is something
/// no unchanged copy can send, and removes the player. So does a bid from a seat that has been out
/// since before the roll. Everything that lag between the table's state and the turn could explain
/// (out of turn, not higher than the last bid, above this table's limit) is dropped quietly.
/// </summary>
internal static class FairBids
{
    /// <summary>The most a bid may ever ask for, whatever the table: five dice a seat at the biggest table, and never under the game's own cap of twenty.</summary>
    internal static int Cap => Math.Max(20, 5 * Math.Max(8, Limits.Max));

    /// <summary>The game's own cap on a raise once a bid stands.</summary>
    internal const int RaiseCap = 20;

    internal static FairThrows.Verdict Last { get; private set; }
    internal static string LastWhy { get; private set; } = "";
    internal static bool LastRan { get; private set; }

    private static bool _testDead, _testLongDead;

    private static FairThrows.Verdict Say(FairThrows.Verdict v, string why, out string said)
    {
        said = why;
        return v;
    }

    /// <summary>One bid, decided. Pure, so the test driver can ask it about a bid never placed.</summary>
    internal static FairThrows.Verdict JudgeBid(int count, int face, bool dead, bool longDead, bool turn,
                                                bool betPlaced, int lastCount, int lastFace, int maxCount, out string why)
    {
        if (face < 0 || face > 6) return Say(FairThrows.Verdict.Caught, "a die face that does not exist", out why);
        if (count < 0 || count > Cap) return Say(FairThrows.Verdict.Caught, $"a bid of {count}, more dice than any table holds", out why);
        if (face == 0 || count == 0) return Say(FairThrows.Verdict.Refuse, "an empty bid", out why);

        if (longDead) return Say(FairThrows.Verdict.Caught, "a bid from a seat out of the game since before this roll", out why);
        if (dead) return Say(FairThrows.Verdict.Refuse, "a bid from a seat that is out", out why);
        if (!turn) return Say(FairThrows.Verdict.Refuse, "a bid out of turn", out why);

        if (betPlaced && lastCount > 0)
        {
            // The game's own rule: a higher face at the same count, or a higher count. At the cap
            // the bid stays at the cap, with any face.
            bool raise = count > lastCount
                         || (count == lastCount && face > lastFace)
                         || (count == RaiseCap && lastCount == RaiseCap);
            if (!raise) return Say(FairThrows.Verdict.Refuse, "a bid that does not raise the last one", out why);
            int top = Math.Max(Math.Max(RaiseCap, maxCount), lastCount + 1);
            if (count > top) return Say(FairThrows.Verdict.Refuse, "a bid above this table's limit", out why);
        }
        else if (maxCount > 0 && count > maxCount)
            return Say(FairThrows.Verdict.Refuse, "an opening bid above this table's limit", out why);

        return Say(FairThrows.Verdict.Accept, "a raise on the bidder's turn", out why);
    }

    /// <summary>
    /// Every bid a player's copy places reaches this handler on the host. The bots' bids and the
    /// host's own come here too, with no sender, and are never checked.
    /// </summary>
    [FairPatch("each Liar's Dice bid checked against the rules before the table sees it")]
    [HarmonyPatch(typeof(DiceGamePlay), nameof(DiceGamePlay.UserCode_PlaceBet__Int32__Int32))]
    internal static class Bid
    {
        // Fair.Install calls Register only on a class marked FairPatch, which FairBids itself is not.
        private static void Register() => FairBids.Register();

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Before(DiceGamePlay __instance, int count, int dice, out bool __state)
        {
            __state = false;
            try
            {
                if (!Fair.Hosting || !Fair.SentBy(__instance, out var conn)) return true;
                LastRan = false;

                var p = __instance.GetComponent<PlayerStats>();
                bool dead = _testDead || (p != null && p.Dead);
                bool longDead = _testLongDead || FairHands.LongDead(p);
                bool turn = !_testDead && p != null && p.HaveTurn;
                _testDead = _testLongDead = false;

                var m = Manager.Instance;
                var d = m != null ? m.DiceGame : null;
                bool placed = d != null && d.BetPlaced;
                int lastCount = d != null ? d.LastCount : 0, lastFace = d != null ? d.LastDice : 0;
                int max = d != null ? d.MaxCount : 0;

                var v = JudgeBid(count, dice, dead, longDead, turn, placed, lastCount, lastFace, max, out string why);
                Last = v;
                LastWhy = why;
                int seat = FairHands.SeatOf(__instance);
                if (v == FairThrows.Verdict.Caught) { Fair.Caught(conn, $"Liar's Dice bid (seat {seat}): {why}"); return false; }
                if (v == FairThrows.Verdict.Refuse) { Fair.Refused($"connection {conn.connectionId} Liar's Dice bid (seat {seat}): {why}"); return false; }
                __state = true;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Fair.Tag} Liar's Dice bid not checked: {e.Message}");
                return true;
            }
        }

        [HarmonyPostfix]
        private static void After(bool __state, bool __runOriginal)
        {
            if (__state && __runOriginal) LastRan = true;
        }
    }

    // ------------------------------------------------------------------ developer tests

    internal static void Register() => Fair.OnDrive("bid", Tests.Run);

    /// <summary>
    /// Developer tests, inside the host only: the host runs its own bid handler for a seat as if
    /// that seat had placed a bid the host makes up, and reads back what it decided. Nothing is
    /// sent on the seat's behalf; a bid that would be accepted is only ever asked of the decision.
    /// A test that expects a clear cheat removes that test seat, unless KickCheaters is off.
    /// </summary>
    private static class Tests
    {
        internal static void Run(string rest)
        {
            var parts = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string what = parts.Length > 0 ? parts[0] : "";
            if (what == "selftest") { SelfTest(); return; }
            if (!Fair.Hosting) { Fair.Result($"bid {what}", false, $"not hosting ({Fair.Why})"); return; }

            var conn = Fair.TestConnection(parts.Length > 1 ? parts[1] : "");
            var m = Manager.Instance;
            var d = m != null ? m.DiceGame : null;
            DiceGamePlay gp = null;
            PlayerStats seat = null;
            if (conn != null && m != null && m.Players != null)
                foreach (var p in m.Players)
                    if (p != null && p.connectionToClient != null && p.connectionToClient.connectionId == conn.connectionId)
                    { seat = p; gp = p.GetComponent<DiceGamePlay>(); }
            if (gp == null || d == null) { Fair.Result($"bid {what}", false, "no remote player at a Liar's Dice table"); return; }

            int caught0 = Fair.CaughtCount, refused0 = Fair.RefusedCount;
            Last = FairThrows.Verdict.Accept; LastWhy = ""; LastRan = false;
            void Place(int count, int face) => Fair.AsSentBy(conn, () => gp.UserCode_PlaceBet__Int32__Int32(count, face));
            void Expect(string test, FairThrows.Verdict want, string because = null)
            {
                bool pass = Last == want && !LastRan && (because == null || LastWhy.Contains(because));
                if (want == FairThrows.Verdict.Caught) pass &= Fair.CaughtCount > caught0 || Fair.RefusedCount > refused0;
                if (want == FairThrows.Verdict.Refuse) pass &= Fair.RefusedCount > refused0 && Fair.CaughtCount == caught0;
                Fair.Result(test, pass, $"decided {Last} ({LastWhy}), game handler ran={LastRan}, " +
                                        $"caught +{Fair.CaughtCount - caught0}, refused +{Fair.RefusedCount - refused0}");
            }

            switch (what)
            {
                case "face7": Place(1, 7); Expect("bid face7", FairThrows.Verdict.Caught); return;
                case "count999": Place(999, 3); Expect("bid count999", FairThrows.Verdict.Caught); return;
                case "dead": _testLongDead = true; Place(1, 3); Expect("bid dead", FairThrows.Verdict.Caught); return;
                case "out": _testDead = true; Place(1, 3); Expect("bid out", FairThrows.Verdict.Refuse); return;
                case "turn":
                    if (seat.HaveTurn) { Fair.Result("bid turn", false, "that seat has the turn: try when it does not"); return; }
                    Place(Math.Max(1, d.LastCount + 1), 3);
                    Expect("bid turn", FairThrows.Verdict.Refuse);
                    return;
                case "noraise":
                    if (!seat.HaveTurn || !d.BetPlaced || d.LastCount <= 0 || d.LastCount >= RaiseCap)
                    { Fair.Result("bid noraise", false, "needs that seat's turn with a bid under the cap standing"); return; }
                    Place(d.LastCount, Math.Max(1, d.LastDice));
                    Expect("bid noraise", FairThrows.Verdict.Refuse, "raise");
                    return;
                case "legal":
                {
                    bool placed = d.BetPlaced && d.LastCount > 0;
                    int count = placed ? d.LastCount + 1 : 1;
                    var v = JudgeBid(count, 3, false, false, true, placed, d.LastCount, d.LastDice, Math.Max(d.MaxCount, count), out string why);
                    Fair.Result("bid legal", v == FairThrows.Verdict.Accept, $"decided {v} ({why})");
                    return;
                }
            }
            Plugin.Log.LogWarning($"{Fair.Tag} test: fair bid <face7|count999|dead|out|turn|noraise|legal|selftest> [connection]");
        }

        private static void SelfTest()
        {
            bool ok = true;
            var sb = new StringBuilder();
            void Check(string name, FairThrows.Verdict got, FairThrows.Verdict want)
            {
                bool pass = got == want;
                ok &= pass;
                sb.Append($"{name}={(pass ? "ok" : $"WRONG({got})")} ");
            }
            const FairThrows.Verdict A = FairThrows.Verdict.Accept, R = FairThrows.Verdict.Refuse, C = FairThrows.Verdict.Caught;
            // count, face, dead, longDead, turn, placed, lastCount, lastFace, max
            Check("opening", JudgeBid(3, 4, false, false, true, false, 0, 0, 20, out _), A);
            Check("opening-40", JudgeBid(40, 4, false, false, true, false, 0, 0, 40, out _), A);
            Check("opening-over", JudgeBid(21, 4, false, false, true, false, 0, 0, 20, out _), R);
            Check("face-up", JudgeBid(5, 6, false, false, true, true, 5, 3, 20, out _), A);
            Check("count-up", JudgeBid(6, 1, false, false, true, true, 5, 3, 20, out _), A);
            Check("same", JudgeBid(5, 3, false, false, true, true, 5, 3, 20, out _), R);
            Check("lower", JudgeBid(4, 6, false, false, true, true, 5, 3, 20, out _), R);
            Check("at-cap", JudgeBid(20, 1, false, false, true, true, 20, 6, 20, out _), A);
            Check("over-cap", JudgeBid(22, 1, false, false, true, true, 5, 6, 20, out _), R);
            Check("face0", JudgeBid(3, 0, false, false, true, false, 0, 0, 20, out _), R);
            Check("face7", JudgeBid(3, 7, false, false, true, false, 0, 0, 20, out _), C);
            Check("face-neg", JudgeBid(3, -1, false, false, true, false, 0, 0, 20, out _), C);
            Check("count-neg", JudgeBid(-1, 3, false, false, true, false, 0, 0, 20, out _), C);
            Check("count-huge", JudgeBid(Cap + 1, 3, false, false, true, false, 0, 0, 20, out _), C);
            Check("turn", JudgeBid(6, 3, false, false, false, true, 5, 3, 20, out _), R);
            Check("out", JudgeBid(6, 3, true, false, false, true, 5, 3, 20, out _), R);
            Check("long-out", JudgeBid(6, 3, true, true, false, true, 5, 3, 20, out _), C);
            Fair.Result("bid selftest", ok, sb.ToString().Trim());
        }
    }
}
