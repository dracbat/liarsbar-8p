using System;
using System.Collections.Generic;
using System.Globalization;
using Mirror;

namespace LiarsBar8P;

/// <summary>
/// Small shared pieces for the developer tests of the host's legality checks (Liar's Spin,
/// Liar's Texas, the start of a match, names and chat).
///
/// Every test here runs inside the host and only asks the host's own checks what they would do
/// with a message: nothing is sent, and no move is ever made for a person. A test can tell the
/// host to treat a seat's record differently ("points=1", "turn=0") so the refusal or the cheat
/// path can be watched without waiting for the table to get there by itself; that changes only
/// what the check is told, never the seat or the table.
/// </summary>
internal static class FairLegalTest
{
    /// <summary>"kill 2 points=1 enforce" into its plain words and its key=value pairs.</summary>
    internal static (List<string> Words, Dictionary<string, string> Pairs) Parse(string rest)
    {
        var words = new List<string>();
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (rest ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0) pairs[part.Substring(0, eq)] = part.Substring(eq + 1);
            else words.Add(part);
        }
        return (words, pairs);
    }

    internal static bool Has(List<string> words, string word)
    {
        foreach (var w in words) if (string.Equals(w, word, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The first plain word that is a number: the connection a test is about. 0 means "the first remote player".</summary>
    internal static string ConnArg(List<string> words, int from)
    {
        for (int i = from; i < words.Count; i++)
            if (int.TryParse(words[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return words[i];
        return "0";
    }

    internal static int Int(Dictionary<string, string> pairs, string key, int fallback) =>
        pairs.TryGetValue(key, out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    internal static bool Bool(Dictionary<string, string> pairs, string key, bool fallback)
    {
        if (!pairs.TryGetValue(key, out var s)) return fallback;
        return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The seat at the table that this connection owns, or null.</summary>
    internal static PlayerStats SeatOf(NetworkConnectionToClient conn)
    {
        if (conn == null) return null;
        var m = Manager.Instance;
        if (m == null || m.Players == null) return null;
        foreach (var p in m.Players)
        {
            if (p == null) continue;
            try
            {
                var owner = p.connectionToClient;
                if (owner != null && owner.connectionId == conn.connectionId) return p;
            }
            catch { }
        }
        return null;
    }

    /// <summary>The lobby player this connection owns, or null.</summary>
    internal static PlayerObjectController PlayerOf(NetworkConnectionToClient conn)
    {
        if (conn == null) return null;
        var nm = Fair.Net;
        if (nm == null || nm.GamePlayers == null) return null;
        foreach (var p in nm.GamePlayers)
            if (p != null && p.ConnectionID == conn.connectionId) return p;
        return null;
    }

    /// <summary>
    /// The end of a "seat" test: log the verdict, check it against "expect=allow|refuse|caught"
    /// when given, and with "enforce" carry out a removal the verdict calls for, through the real
    /// path, so the removal and the door's refusal can be watched end to end.
    /// </summary>
    internal static void Finish(string test, Verdict v, List<string> words, Dictionary<string, string> pairs,
                                NetworkConnectionToClient conn, string detail, string why)
    {
        bool pass = true;
        if (pairs.TryGetValue("expect", out var want))
        {
            pass = string.Equals(want, v.ToString(), StringComparison.OrdinalIgnoreCase);
            detail += $" (expected {want})";
        }

        if (v == Verdict.Caught && Has(words, "enforce") && !Fair.TestsAllowed)
        {
            pass = false;
            detail += " - enforce is loopback harness only";
        }
        else if (v == Verdict.Caught && Has(words, "enforce"))
        {
            int before = Fair.CaughtCount;
            FairLegal.Apply(v, conn, $"test: {why}");
            bool removed = Fair.CaughtCount > before || (Fair.KickCheaters != null && !Fair.KickCheaters.Value);
            pass &= removed;
            detail += removed ? " - removal carried out" : " - removal did not happen";
        }
        Fair.Result(test, pass, detail);
    }

    /// <summary>One table-driven check: the verdict a rule gave against the one it should give.</summary>
    internal static int Expect<T>(string test, string the, T got, T want, ref int failed)
    {
        bool pass = EqualityComparer<T>.Default.Equals(got, want);
        if (!pass) failed++;
        Fair.Result(test, pass, $"{the}: {got} (expected {want})");
        return pass ? 1 : 0;
    }
}
