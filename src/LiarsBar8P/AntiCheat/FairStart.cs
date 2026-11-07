using System;
using System.Collections.Generic;
using HarmonyLib;
using Mirror;

namespace LiarsBar8P;

/// <summary>
/// Only the host starts a match at a table it hosts.
///
/// In an unchanged game only the host's own copy ever starts a match (on every other copy the
/// start button marks the player ready instead), and nothing in this mod starts one from
/// elsewhere. So the host accepts a start only from itself: a start request from any other
/// player is dropped and the player removed. The host's own start, and the mod's own starts,
/// run inside the host and are never touched.
/// </summary>
[FairPatch("only the host starts the match")]
[HarmonyPatch(typeof(PlayerObjectController), nameof(PlayerObjectController.InvokeUserCode_CmdStartGame__String))]
internal static class FairStart
{
    /// <summary>The rule: a start request from a remote player at a table this copy hosts is a clear cheat.</summary>
    internal static Verdict Decide(bool hosting, bool remote) =>
        hosting && remote ? Verdict.Caught : Verdict.Allow;

    /// <summary>The start's body is compiled into Mirror's handler, so the handler is what is patched.</summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    private static bool Start(NetworkConnectionToClient senderConnection)
    {
        try
        {
            if (!Fair.IsRemote(senderConnection)) return true;
            var v = Decide(Fair.Hosting, true);
            return FairLegal.Apply(v, senderConnection, "asked the host to start the match, which only the host can do");
        }
        catch (Exception e)
        {
            // Failing safe here means refusing: only the host's own start is ever honest, and that never reaches this branch.
            Plugin.Log.LogWarning($"{Fair.Tag} start check failed: {e.Message}");
            return !(Fair.Hosting && Fair.IsRemote(senderConnection));
        }
    }

    private static void Register() => Fair.OnDrive("start", Test);

    /// <summary>
    /// "fair start": the rule against its cases. "fair start seat [conn] [enforce]": what the host
    /// would do with a start request from that player, and with "enforce", the real handler's
    /// check run on it (it removes the player; the match does not start).
    /// </summary>
    private static void Test(string rest)
    {
        var (words, pairs) = FairLegalTest.Parse(rest);
        if (words.Count > 0 && words[0].Equals("seat", StringComparison.OrdinalIgnoreCase)) { Seat(words, pairs); return; }

        int failed = 0;
        const string T = "start";
        FairLegalTest.Expect(T, "start from a remote player while hosting", Decide(true, true), Verdict.Caught, ref failed);
        FairLegalTest.Expect(T, "the host's own start", Decide(true, false), Verdict.Allow, ref failed);
        FairLegalTest.Expect(T, "a start at a table this copy does not host", Decide(false, true), Verdict.Allow, ref failed);
        FairLegalTest.Expect(T, "the bots and the host's own code", Fair.IsRemote(null), false, ref failed);
        Fair.Result(T, failed == 0, failed == 0 ? "every case gave the expected verdict" : $"{failed} case(s) gave the wrong verdict");
    }

    private static void Seat(List<string> words, Dictionary<string, string> pairs)
    {
        const string T = "start seat";
        if (!Fair.Hosting) { Fair.Result(T, false, $"not hosting ({Fair.Why})"); return; }
        var conn = Fair.TestConnection(FairLegalTest.ConnArg(words, 1));
        if (conn == null) { Fair.Result(T, false, "no remote player connected"); return; }

        if (FairLegalTest.Has(words, "enforce"))
        {
            if (!Fair.TestsAllowed) { Fair.Result(T, false, "loopback harness only"); return; }
            // The real check, with this player as the sender; the game's own start never runs here.
            int before = Fair.CaughtCount;
            bool ran = Start(conn);
            bool removed = Fair.CaughtCount > before || (Fair.KickCheaters != null && !Fair.KickCheaters.Value);
            Fair.Result(T, !ran && removed, $"connection {conn.connectionId}: start dropped {!ran}, removal {(removed ? "carried out" : "did not happen")}");
            return;
        }
        var v = Decide(true, Fair.IsRemote(conn));
        FairLegalTest.Finish(T, v, words, pairs, conn, $"connection {conn.connectionId}: {v}", "a start request from a player");
    }
}
