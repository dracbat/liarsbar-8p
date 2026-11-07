using System;
using HarmonyLib;
using Mirror;

namespace LiarsBar8P;

/// <summary>What the host does with one command from a player's copy.</summary>
internal enum Verdict
{
    /// <summary>The rules allow it: the game's own handling runs, exactly as it always did.</summary>
    Allow,
    /// <summary>Not allowed now, but lag or honest play could explain it: dropped quietly.</summary>
    Refuse,
    /// <summary>Something no unchanged copy could ever send: dropped, and the player removed.</summary>
    Caught,
}

/// <summary>
/// Shared pieces of the host's legality checks (Liar's Spin, Liar's Texas, the start of a match,
/// names and chat): who sent a command, whether this copy has watched the whole match, and one
/// place that turns a verdict into the log line, the refusal or the removal.
///
/// "Watched the whole match" matters because some checks lean on the host's own record of a
/// seat (its turns, its points, a fold it began). A copy that took the table over in a host
/// migration never saw the start of that record, so on such a copy those checks only ever
/// allow, never refuse or remove.
/// </summary>
[FairPatch("the host notes the matches it has watched from the start")]
[HarmonyPatch(typeof(Manager), nameof(Manager.StartGame))]
internal static class FairLegal
{
    private static int _startedMatch = -1;

    [HarmonyPostfix]
    private static void Started()
    {
        try { if (NetworkServer.active) _startedMatch = Fair.Match; }
        catch { }
    }

    private static void Register() => Fair.OnTick("legal checks", Tick);

    private static void Tick()
    {
        // A copy that stops being the server forgets the match it watched, so a later host
        // migration onto this copy is never mistaken for a match it saw from the start.
        if (!NetworkServer.active) _startedMatch = -1;
    }

    /// <summary>
    /// Developer test, inside the loopback harness only: until the next match starts, this copy
    /// counts as one that took the table over in a host migration.
    /// </summary>
    internal static void PretendTookOverForTest()
    {
        if (Fair.TestsAllowed) _startedMatch = -1;
    }

    /// <summary>This copy started the match being played as its server and has been its server since.</summary>
    internal static bool WatchedWholeMatch
    {
        get
        {
            try
            {
                if (!NetworkServer.active || _startedMatch != Fair.Match || _startedMatch < 0) return false;
                var hm = HostMigration.Instance;
                if (hm != null && hm.isNewHost) return false;
                var nm = Fair.Net;
                if (nm != null && nm.MigrationStarted) return false;
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// The remote player who sent the command now being handled, when it is for an object that
    /// player owns. Null for bots, the host's own seat and the host's own code.
    /// </summary>
    internal static NetworkConnectionToClient RemoteOwner(NetworkBehaviour part, NetworkConnectionToClient sender)
    {
        if (part == null || !Fair.IsRemote(sender)) return null;
        try
        {
            var owner = part.connectionToClient;
            return owner != null && owner.connectionId == sender.connectionId ? sender : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Carry out a verdict on a command from this connection: log a refusal, or remove the
    /// player for a clear cheat. Returns true when the game's own handling may run.
    /// </summary>
    internal static bool Apply(Verdict v, NetworkConnectionToClient conn, string what)
    {
        switch (v)
        {
            case Verdict.Allow:
                return true;
            case Verdict.Caught:
                Fair.Caught(conn, what);
                return false;
            default:
                Fair.Refused($"connection {(conn != null ? conn.connectionId : -1)}: {what}");
                return false;
        }
    }

    /// <summary>The seat a gameplay part belongs to, or null.</summary>
    internal static PlayerStats SeatOf(NetworkBehaviour part)
    {
        try { return part != null ? part.GetComponent<PlayerStats>() : null; }
        catch { return null; }
    }
}
