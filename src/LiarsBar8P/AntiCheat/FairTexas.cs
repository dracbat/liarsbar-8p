using System;
using System.Collections.Generic;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Liar's Texas (Liar's Poker): at a table this copy hosts, the host accepts a player's call,
/// all-in and fold only in the order the game itself makes them, judged from its own record.
///
/// <list type="bullet">
/// <item>A call or an all-in needs the seat to hold the turn and not to have called already in
/// that turn. The one exception is an all-in the host itself asked that seat for when an all-in
/// round ends: that one is accepted whenever it comes. Anything else is refused quietly.</item>
/// <item>A fold is accepted when the seat began one (the game's fold always opens with its own
/// message first) or the host itself told the seat to fold. A fold out of nowhere is refused,
/// at a table this copy has hosted since the match began. A fold ends only once the host has
/// rolled the seat's pull (<see cref="HostDecidesTexas"/>); until then its end is refused.</item>
/// <item>The Coward's Fold is spent once used. The host keeps the last moment its record showed
/// the seat still had one. A Coward's Fold from a seat whose record has shown none for a long
/// while, or a fold message in a form the game never sends, removes the player.</item>
/// </list>
///
/// Turn checks are not used on folds: the game hands the turn on as soon as a fold begins, and
/// the host itself orders folds and all-ins outside anybody's turn. Bots never send these, and
/// the host's own seat is never checked.
/// </summary>
[FairPatch("Liar's Texas: the host accepts only calls and folds the rules allow")]
[HarmonyPatch]
internal static class FairTexas
{
    /// <summary>How long the host must have seen a seat without a Coward's Fold before using one is a cheat.</summary>
    internal const float CowardWindow = 30f;

    private const string Raise = "texas raise";

    // ------------------------------------------------------------------ the host's record

    private static int _match = -1;
    private static readonly Dictionary<uint, float> _since = new();
    private static readonly Dictionary<uint, float> _hadCoward = new();
    private static readonly Dictionary<uint, int> _allInAsked = new();
    private static readonly Dictionary<uint, int> _foldAsked = new();
    private static readonly Dictionary<uint, int> _foldBegun = new();

    private static void Fresh()
    {
        if (_match == Fair.Match) return;
        _match = Fair.Match;
        _since.Clear();
        _hadCoward.Clear();
        _allInAsked.Clear();
        _foldAsked.Clear();
        _foldBegun.Clear();
    }

    private static void Look(TexasGamePlay gp, float now)
    {
        if (gp == null) return;
        Fresh();
        uint id = gp.netId;
        if (!_since.ContainsKey(id)) _since[id] = now;
        if (gp.CowardFoldCount > 0) _hadCoward[id] = now;
    }

    private static void Count(Dictionary<uint, int> d, uint id, int by)
    {
        d.TryGetValue(id, out int n);
        n = Math.Max(0, Math.Min(n + by, 4));
        if (n == 0) d.Remove(id); else d[id] = n;
    }

    private static int Of(Dictionary<uint, int> d, uint id) => d.TryGetValue(id, out int n) ? n : 0;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.NetworkCowardFoldCount), MethodType.Setter)]
    private static void CowardSet(TexasGamePlay __instance)
    {
        try { if (NetworkServer.active) Look(__instance, Time.unscaledTime); }
        catch { }
    }

    /// <summary>The host asks a seat to go all in as an all-in round ends: that seat's all-in is then expected.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.ALLINTargetRpc))]
    private static void AllInAsked(TexasGamePlay __instance)
    {
        try { if (NetworkServer.active && __instance != null) { Fresh(); Count(_allInAsked, __instance.netId, 1); } }
        catch { }
    }

    /// <summary>The host tells a seat to fold: that seat's fold is then expected.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.FoldTargetRpc))]
    private static void FoldAsked(TexasGamePlay __instance)
    {
        try { if (NetworkServer.active && __instance != null) { Fresh(); Count(_foldAsked, __instance.netId, 1); } }
        catch { }
    }

    private static void Register()
    {
        Fair.OnTick("Liar's Texas record", Tick);
        Fair.OnDrive("texas", Test);
    }

    /// <summary>How often the host looks over the table: often enough for a 30-second window.</summary>
    private const float LookEvery = 0.25f;

    private static float _nextLook;
    private static int _texasMatch = -1;

    private static void Tick()
    {
        if (!Fair.Hosting) return;
        float now = Time.unscaledTime;
        if (now < _nextLook) return;
        _nextLook = now + LookEvery;

        var m = Manager.Instance;
        if (m == null || m.Players == null || m.Players.Count == 0) return;
        if (_texasMatch != Fair.Match)
        {
            // The mode is the same for a whole match: once seen, it is not looked up again.
            if (m.Players[0] == null || m.Players[0].GetComponent<TexasGamePlay>() == null) return;
            _texasMatch = Fair.Match;
        }

        foreach (var p in m.Players)
            if (p != null) Look(p.GetComponent<TexasGamePlay>(), now);
    }

    // ------------------------------------------------------------------ the rules

    internal struct Facts
    {
        internal bool HaveTurn, RaisedThisTurn, AllIn, AllInAsked, Watched;
        internal bool NoFold, Coward, CowardRecently, WatchedLongEnough, PullOwed;
        internal int CowardCount, FoldExpected;
    }

    internal static Verdict Rised(in Facts f, out string why)
    {
        if (f.AllIn && f.AllInAsked) { why = null; return Verdict.Allow; }
        if (!f.HaveTurn)
        {
            if (f.AllIn && !f.Watched) { why = null; return Verdict.Allow; }   // an all-in the host may have asked for before it took over
            why = f.AllIn ? "an all-in out of turn that the host never asked for" : "a call out of turn";
            return Verdict.Refuse;
        }
        if (f.RaisedThisTurn) { why = "a second call in one turn"; return Verdict.Refuse; }
        why = null;
        return Verdict.Allow;
    }

    internal static Verdict FoldBegins(in Facts f, out string why)
    {
        if (f.NoFold && f.Coward) { why = "a fold message no unchanged game sends"; return Verdict.Caught; }
        if (f.Coward && f.CowardCount <= 0 && !f.CowardRecently && f.WatchedLongEnough)
        {
            why = "a Coward's Fold the seat had not had for a long while";
            return Verdict.Caught;
        }
        why = null;
        return Verdict.Allow;
    }

    internal static Verdict Folded(in Facts f, out string why)
    {
        if (f.PullOwed) { why = "a fold that ended before the host had rolled its pull"; return Verdict.Refuse; }
        if (f.FoldExpected > 0 || !f.Watched) { why = null; return Verdict.Allow; }
        why = "a fold the seat never began and the host never asked for";
        return Verdict.Refuse;
    }

    internal static Facts Gather(TexasGamePlay gp, PlayerStats p)
    {
        var f = new Facts { Watched = FairLegal.WatchedWholeMatch };
        if (gp == null) return f;
        float now = Time.unscaledTime;
        Look(gp, now);
        uint id = gp.netId;

        f.AllInAsked = Of(_allInAsked, id) > 0;
        f.FoldExpected = Of(_foldAsked, id) + Of(_foldBegun, id);
        f.CowardCount = gp.CowardFoldCount;
        bool seen = _since.TryGetValue(id, out float since);
        f.CowardRecently = _hadCoward.TryGetValue(id, out float had) && had >= now - CowardWindow;
        f.WatchedLongEnough = seen && since <= now - CowardWindow;
        f.PullOwed = HostDecidesTexas.PullOwed(id);

        if (p != null)
        {
            f.HaveTurn = p.HaveTurn;
            f.RaisedThisTurn = FairTurnCount.Already(p, Raise);
        }
        return f;
    }

    // ------------------------------------------------------------------ the hooks

    private static TexasGamePlay AsTexas(NetworkBehaviour obj)
    {
        try { return obj != null ? obj.TryCast<TexasGamePlay>() : null; }
        catch { return null; }
    }

    private static string SeatName(PlayerStats p) => p != null ? $"seat {p.Slot}" : "no seat";

    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.UserCode_CMDRised__Boolean))]
    private static bool CMDRised(TexasGamePlay __instance, bool AllInN)
    {
        try
        {
            if (!Fair.Hosting) return true;
            var conn = FairLegal.RemoteOwner(__instance, Fair.Sender);
            if (conn == null) return true;

            var p = FairLegal.SeatOf(__instance);
            var f = Gather(__instance, p);
            f.AllIn = AllInN;
            var v = Rised(f, out string why);
            if (v != Verdict.Allow)
                return FairLegal.Apply(v, conn, $"Liar's Texas {(AllInN ? "all-in" : "call")} ({SeatName(p)}): {why}");

            if (AllInN && f.AllInAsked) Count(_allInAsked, __instance.netId, -1);
            else FairTurnCount.Acted(p, Raise);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"{Fair.Tag} Liar's Texas call check failed, the game's own handling runs: {e.Message}");
            return true;
        }
    }

    /// <summary>The message every fold opens with (and the one a shot the host ordered opens with).</summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.UserCode_playcmdses__Boolean__Boolean))]
    private static bool FoldBegin(TexasGamePlay __instance, bool nofold, bool coward)
    {
        try
        {
            if (!Fair.Hosting) return true;
            var conn = FairLegal.RemoteOwner(__instance, Fair.Sender);
            if (conn == null) return true;

            var p = FairLegal.SeatOf(__instance);
            var f = Gather(__instance, p);
            f.NoFold = nofold;
            f.Coward = coward;
            var v = FoldBegins(f, out string why);
            if (v != Verdict.Allow)
                return FairLegal.Apply(v, conn, $"Liar's Texas fold ({SeatName(p)}): {why}");

            if (!nofold)
            {
                Count(_foldBegun, __instance.netId, 1);
                HostDecidesTexas.Owe(__instance, fold: true, orderedFold: false);   // every fold takes a pull
            }
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"{Fair.Tag} Liar's Texas fold check failed, the game's own handling runs: {e.Message}");
            return true;
        }
    }

    /// <summary>The end of a fold. Its body is compiled into Mirror's handler, so the handler is what is patched.</summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.InvokeUserCode_CMDFolded))]
    private static bool FoldEnd(NetworkBehaviour obj, NetworkConnectionToClient senderConnection)
    {
        try
        {
            if (!Fair.Hosting) return true;
            var gp = AsTexas(obj);
            var conn = FairLegal.RemoteOwner(gp, senderConnection);
            if (conn == null) return true;

            var p = FairLegal.SeatOf(gp);
            var f = Gather(gp, p);
            var v = Folded(f, out string why);
            if (v != Verdict.Allow)
                return FairLegal.Apply(v, conn, $"Liar's Texas fold ({SeatName(p)}): {why}");

            // One expected fold is used up: the one the seat began first, else one the host asked for.
            uint id = gp.netId;
            if (Of(_foldBegun, id) > 0) Count(_foldBegun, id, -1);
            else Count(_foldAsked, id, -1);
            HostDecidesTexas.FoldEnded(id);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"{Fair.Tag} Liar's Texas fold check failed, the game's own handling runs: {e.Message}");
            return true;
        }
    }

    // ------------------------------------------------------------------ developer tests

    /// <summary>
    /// "fair texas": every rule against a table of cases, nothing at the table touched.
    /// "fair texas seat [conn] call|allin|fold|coward|end [turn=0|1] [raised=0|1] [asked=0|1]
    /// [count=N] [recent=0|1] [watched=0|1] [expected=N] [owed=0|1] [enforce]": what the host would do
    /// right now with that message from that player's seat. Nothing runs for the seat.
    /// </summary>
    private static void Test(string rest)
    {
        var (words, pairs) = FairLegalTest.Parse(rest);
        if (words.Count > 0 && words[0].Equals("seat", StringComparison.OrdinalIgnoreCase)) { Seat(words, pairs); return; }

        int failed = 0;
        const string T = "texas";
        var ok = new Facts { HaveTurn = true, Watched = true, WatchedLongEnough = true, CowardCount = 1 };

        FairLegalTest.Expect(T, "call in turn", Rised(ok, out _), Verdict.Allow, ref failed);
        var f = ok; f.RaisedThisTurn = true;
        FairLegalTest.Expect(T, "second call in one turn", Rised(f, out _), Verdict.Refuse, ref failed);
        f = ok; f.HaveTurn = false;
        FairLegalTest.Expect(T, "call out of turn", Rised(f, out _), Verdict.Refuse, ref failed);
        f = ok; f.HaveTurn = false; f.AllIn = true; f.AllInAsked = true;
        FairLegalTest.Expect(T, "all-in the host asked for, out of turn", Rised(f, out _), Verdict.Allow, ref failed);
        f = ok; f.RaisedThisTurn = true; f.AllIn = true; f.AllInAsked = true;
        FairLegalTest.Expect(T, "all-in the host asked for, after a call in the same turn", Rised(f, out _), Verdict.Allow, ref failed);
        f = ok; f.HaveTurn = false; f.AllIn = true;
        FairLegalTest.Expect(T, "all-in out of turn nobody asked for", Rised(f, out _), Verdict.Refuse, ref failed);
        f = ok; f.HaveTurn = false; f.AllIn = true; f.Watched = false;
        FairLegalTest.Expect(T, "all-in out of turn after a host migration", Rised(f, out _), Verdict.Allow, ref failed);

        FairLegalTest.Expect(T, "a plain fold begins", FoldBegins(ok, out _), Verdict.Allow, ref failed);
        f = ok; f.NoFold = true;
        FairLegalTest.Expect(T, "a shot the host ordered begins", FoldBegins(f, out _), Verdict.Allow, ref failed);
        f = ok; f.Coward = true;
        FairLegalTest.Expect(T, "a Coward's Fold the seat has", FoldBegins(f, out _), Verdict.Allow, ref failed);
        f = ok; f.Coward = true; f.CowardCount = 0; f.CowardRecently = true;
        FairLegalTest.Expect(T, "a Coward's Fold just spent (lag)", FoldBegins(f, out _), Verdict.Allow, ref failed);
        f = ok; f.Coward = true; f.CowardCount = 0; f.WatchedLongEnough = false;
        FairLegalTest.Expect(T, "a Coward's Fold, seat not watched long", FoldBegins(f, out _), Verdict.Allow, ref failed);
        f = ok; f.Coward = true; f.CowardCount = 0;
        FairLegalTest.Expect(T, "a Coward's Fold long gone", FoldBegins(f, out _), Verdict.Caught, ref failed);
        f = ok; f.Coward = true; f.NoFold = true;
        FairLegalTest.Expect(T, "a fold message in a form the game never sends", FoldBegins(f, out _), Verdict.Caught, ref failed);

        f = ok; f.FoldExpected = 1; f.HaveTurn = false;
        FairLegalTest.Expect(T, "a fold ends after it began, turn already passed", Folded(f, out _), Verdict.Allow, ref failed);
        f = ok; f.FoldExpected = 0;
        FairLegalTest.Expect(T, "a fold ends that never began", Folded(f, out _), Verdict.Refuse, ref failed);
        f = ok; f.FoldExpected = 0; f.Watched = false;
        FairLegalTest.Expect(T, "a fold ends after a host migration", Folded(f, out _), Verdict.Allow, ref failed);
        f = ok; f.FoldExpected = 1; f.PullOwed = true;
        FairLegalTest.Expect(T, "a fold ends before the host rolled its pull", Folded(f, out _), Verdict.Refuse, ref failed);
        f = ok; f.FoldExpected = 1; f.PullOwed = false;
        FairLegalTest.Expect(T, "a fold ends after the host rolled its pull", Folded(f, out _), Verdict.Allow, ref failed);

        Fair.Result(T, failed == 0, failed == 0 ? "every rule gave the expected verdict" : $"{failed} rule(s) gave the wrong verdict");
    }

    private static void Seat(List<string> words, Dictionary<string, string> pairs)
    {
        const string T = "texas seat";
        if (!Fair.Hosting) { Fair.Result(T, false, $"not hosting ({Fair.Why})"); return; }

        var conn = Fair.TestConnection(FairLegalTest.ConnArg(words, 1));
        var p = FairLegalTest.SeatOf(conn);
        var gp = p != null ? p.GetComponent<TexasGamePlay>() : null;
        if (gp == null) { Fair.Result(T, false, "no remote player seated at a Liar's Texas table"); return; }

        string move = "call";
        foreach (var w in words) if (w is "call" or "allin" or "fold" or "coward" or "end") move = w;

        var f = Gather(gp, p);
        f.AllIn = move == "allin";
        f.Coward = move == "coward";
        f.HaveTurn = FairLegalTest.Bool(pairs, "turn", f.HaveTurn);
        f.RaisedThisTurn = FairLegalTest.Bool(pairs, "raised", f.RaisedThisTurn);
        f.AllInAsked = FairLegalTest.Bool(pairs, "asked", f.AllInAsked);
        f.CowardCount = FairLegalTest.Int(pairs, "count", f.CowardCount);
        f.CowardRecently = FairLegalTest.Bool(pairs, "recent", f.CowardRecently);
        f.WatchedLongEnough = FairLegalTest.Bool(pairs, "watched", f.WatchedLongEnough);
        f.Watched = FairLegalTest.Bool(pairs, "whole", f.Watched);
        f.FoldExpected = FairLegalTest.Int(pairs, "expected", f.FoldExpected);
        f.PullOwed = FairLegalTest.Bool(pairs, "owed", f.PullOwed);

        string why;
        Verdict v = move switch
        {
            "fold" or "coward" => FoldBegins(f, out why),
            "end" => Folded(f, out why),
            _ => Rised(f, out why),
        };
        string detail = $"connection {conn.connectionId} seat {p.Slot} {move}: {v}{(why != null ? $" ({why})" : "")} " +
                        $"- turn {f.HaveTurn}, called {f.RaisedThisTurn}, all-in asked {f.AllInAsked}, coward count {f.CowardCount}, " +
                        $"fold expected {f.FoldExpected}, pull owed {f.PullOwed}, whole match {f.Watched}";
        FairLegalTest.Finish(T, v, words, pairs, conn, detail, why);
    }
}
