using System;
using System.Text;
using HarmonyLib;
using Mirror;

namespace LiarsBar8P;

/// <summary>
/// Fair play for the size of each Liar's Dice roll.
///
/// <b>What it does.</b> At a table this copy hosts, the host decides how many dice each seat rolls,
/// from the table's own number: five, or three under the Three Dice house rule, and never fewer
/// than none. A seat that already rolls fewer keeps its number, as in the game itself.
///
/// <b>Caught.</b> The host also checks each seat's own count of its dice as it arrives. No unchanged
/// copy ever counts above the five every seat starts with, so a count above that removes the
/// player, and the roll is the table's number either way. Never for a bot or the host's own seat,
/// never in ranked.
/// </summary>
internal static class FairDiceCount
{
    /// <summary>The dice every seat starts a match with, in the game itself.</summary>
    internal const int GameDice = 5;

    /// <summary>The dice a seat holds under the Three Dice house rule (the house rule's own number).</summary>
    internal const int ThreeDice = 3;

    /// <summary>The most dice a seat may roll at this table.</summary>
    internal static int Size => HouseRules.Host(HouseRules.Rule.ThreeDice) ? ThreeDice : GameDice;

    /// <summary>A seat's dice for the roll, within what the table allows.</summary>
    internal static int Within(int have, int size) => have > size ? size : have < 0 ? 0 : have;

    /// <summary>A number of dice reported by a seat's own copy, decided.</summary>
    internal static FairThrows.Verdict JudgeReported(int value, out string why)
    {
        if (value > GameDice) { why = $"{value} dice, more than any seat starts with"; return FairThrows.Verdict.Caught; }
        if (value < 0) { why = "fewer than no dice"; return FairThrows.Verdict.Refuse; }
        why = "a number a seat can hold";
        return FairThrows.Verdict.Accept;
    }

    /// <summary>Every seat's roll size put within the table's number, on the host.</summary>
    internal static int Settle()
    {
        int fixedUp = 0;
        var m = Manager.Instance;
        if (m == null || m.Players == null) return 0;
        int size = Size;
        foreach (var p in m.Players)
        {
            if (p == null) continue;
            var gp = p.GetComponent<DiceGamePlay>();
            if (gp == null) continue;
            int want = Within(gp.TotalDices, size);
            if (want == gp.TotalDices) continue;
            gp.TotalDices = want;   // the host's own copy only; nothing goes on the wire
            fixedUp++;
        }
        return fixedUp;
    }

    /// <summary>
    /// Just before each roll, on the host, after the house rules have set their number: every
    /// seat rolls within the table's number. This is also the moment a round starts, so the seats
    /// already out are noted for the bid checks.
    /// </summary>
    [FairPatch("each Liar's Dice roll is a size the host allows")]
    [HarmonyPatch(typeof(DiceGamePlayManager), nameof(DiceGamePlayManager.AssignAllDiceValues))]
    internal static class Roll
    {
        // Fair.Install calls Register only on a class marked FairPatch, which FairDiceCount itself is not.
        private static void Register() => FairDiceCount.Register();

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static void Before()
        {
            try
            {
                if (!Fair.Hosting) return;
                FairHands.DealSeen();
                int n = Settle();
                if (n > 0) Plugin.Log.LogInfo($"{Fair.Tag} {n} seat(s) roll the table's number of dice");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"{Fair.Tag} roll size not checked: {e.Message}"); }
        }
    }

    /// <summary>
    /// A seat's own state as it reaches the host. The host's copy never hears its own state back,
    /// so on the host this only ever runs for a remote player's seat.
    /// </summary>
    [FairPatch("a seat's own count of its dice checked as it reaches the host")]
    [HarmonyPatch(typeof(DiceGamePlay), nameof(DiceGamePlay.DeserializeSyncVars))]
    internal static class Reported
    {
        [HarmonyPostfix]
        private static void After(DiceGamePlay __instance, bool initialState)
        {
            try
            {
                if (initialState || __instance == null || !Fair.Hosting) return;
                var conn = __instance.connectionToClient;
                if (!Fair.IsRemote(conn)) return;

                int have = __instance.TotalDices;
                var v = JudgeReported(have, out string why);
                if (v == FairThrows.Verdict.Accept) return;

                __instance.TotalDices = Within(have, Size);
                int seat = FairHands.SeatOf(__instance);
                if (v == FairThrows.Verdict.Caught) Fair.Caught(conn, $"Liar's Dice (seat {seat}): {why}");
                else Fair.Refused($"connection {conn.connectionId} Liar's Dice (seat {seat}): {why}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"{Fair.Tag} dice count not checked: {e.Message}"); }
        }
    }

    // ------------------------------------------------------------------ developer tests

    internal static void Register() => Fair.OnDrive("dice", Tests.Run);

    /// <summary>
    /// Developer tests, inside the host only: the host changes its own copy of a seat's number of
    /// dice, runs its own check before a roll, and puts the number back. Nothing is sent.
    /// </summary>
    private static class Tests
    {
        internal static void Run(string rest)
        {
            var parts = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string what = parts.Length > 0 ? parts[0] : "";

            if (what == "selftest")
            {
                bool ok = true;
                var sb = new StringBuilder();
                void Check(string name, bool pass) { ok &= pass; sb.Append($"{name}={(pass ? "ok" : "WRONG")} "); }
                Check("report-99", JudgeReported(99, out _) == FairThrows.Verdict.Caught);
                Check("report-6", JudgeReported(6, out _) == FairThrows.Verdict.Caught);
                Check("report-5", JudgeReported(5, out _) == FairThrows.Verdict.Accept);
                Check("report-4", JudgeReported(4, out _) == FairThrows.Verdict.Accept);
                Check("report-0", JudgeReported(0, out _) == FairThrows.Verdict.Accept);
                Check("report-neg", JudgeReported(-3, out _) == FairThrows.Verdict.Refuse);
                Check("within-high", Within(99, 5) == 5);
                Check("within-three", Within(5, 3) == 3);
                Check("within-fewer", Within(4, 5) == 4);
                Check("within-neg", Within(-2, 5) == 0);
                Fair.Result("dice selftest", ok, sb.ToString().Trim());
                return;
            }

            if (what != "inflate")
            {
                Plugin.Log.LogWarning($"{Fair.Tag} test: fair dice <inflate|selftest> [connection]");
                return;
            }
            if (!Fair.Hosting) { Fair.Result("dice inflate", false, $"not hosting ({Fair.Why})"); return; }

            var conn = Fair.TestConnection(parts.Length > 1 ? parts[1] : "");
            DiceGamePlay gp = null;
            var m = Manager.Instance;
            if (conn != null && m != null && m.Players != null)
                foreach (var p in m.Players)
                    if (p != null && p.connectionToClient != null && p.connectionToClient.connectionId == conn.connectionId)
                        gp = p.GetComponent<DiceGamePlay>();
            if (gp == null) { Fair.Result("dice inflate", false, "no remote player at a Liar's Dice table"); return; }

            int was = gp.TotalDices;
            try
            {
                gp.TotalDices = 99;
                Settle();
                int got = gp.TotalDices;
                Fair.Result("dice inflate", got == Size, $"a seat set to 99 dice rolls {got}, the table allows {Size}");
            }
            finally { gp.TotalDices = was; }
        }
    }
}
