using System;
using System.Collections.Generic;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Liar's Poker (the lobby's Texas): at a table this copy hosts, the host accepts a player's call,
/// all-in and fold only in the order the game itself makes them, judged from its own record.
///
/// <list type="bullet">
/// <item>A call or an all-in needs the seat to hold the turn and not to have called already in
/// that turn. The one exception is an all-in the host itself asked that seat for when an all-in
/// round ends: that one is accepted whenever it comes. Anything else is refused quietly. Nothing
/// takes the turn from a seat before its call arrives: the game hands the turn on a moment after
/// the call itself.</item>
/// <item>A fold is accepted when the seat began one (the game's own fold always opens with its own
/// message first) or the host itself told the seat to fold. A fold out of nowhere is refused, at a
/// table this copy has hosted since the match began.</item>
/// <item>Every fold the seat began takes a pull of its revolver, which its game asks the host for
/// before it ends the fold. A fold that ends without asking is never refused, which would leave the
/// table waiting on a turn nobody holds: the host rolls that pull itself, through the game's own
/// handling, before the fold ends, and answers a late request with the same outcome.</item>
/// <item>The Coward's Fold is spent once used. The host keeps the last moment its record showed
/// the seat still had one. A Coward's Fold from a seat whose record has shown none for a long
/// while, or a fold message in a form the game never sends, removes the player.</item>
/// </list>
///
/// Since the game's 14 Nov 2026 update the host settles every pull itself (the shot's outcome is
/// the game's, rolled on the host), so this only keeps the order of the moves. Turn checks are not
/// used on folds: the game takes the turn from a seat in the very message that begins its fold,
/// and the host itself orders folds and all-ins outside anybody's turn. Bots never send these, the
/// host's own seat is never checked, and neither is the test harness playing a seat from the host.
/// </summary>
[FairPatch("Liar's Poker: the host accepts only calls and folds the rules allow")]
[HarmonyPatch]
internal static class FairTexas
{
    /// <summary>How long the host must have seen a seat without a Coward's Fold before using one is a cheat.</summary>
    internal const float CowardWindow = 30f;

    /// <summary>
    /// How long a fold the seat began may go without its pull being asked for before the host rolls
    /// it: many times what any honest game takes, which is the time a revolver takes to come up.
    /// </summary>
    internal const float PullDeadline = 30f;

    private const string Raise = "texas raise";

    // ------------------------------------------------------------------ the host's record

    private static int _match = -1;
    private static readonly Dictionary<uint, float> _since = new();
    private static readonly Dictionary<uint, float> _hadCoward = new();
    private static readonly Dictionary<uint, int> _allInAsked = new();
    private static readonly Dictionary<uint, int> _foldAsked = new();
    private static readonly Dictionary<uint, int> _foldBegun = new();

    /// <summary>A fold the seat began whose pull has not been asked for yet, and since when.</summary>
    private static readonly Dictionary<uint, float> _owed = new();

    /// <summary>A fold whose pull the host rolled itself: a request that comes after gets the same outcome.</summary>
    private static readonly HashSet<uint> _hostSettled = new();

    private static void Fresh()
    {
        if (_match == Fair.Match) return;
        _match = Fair.Match;
        _since.Clear();
        _hadCoward.Clear();
        _allInAsked.Clear();
        _foldAsked.Clear();
        _foldBegun.Clear();
        _owed.Clear();
        _hostSettled.Clear();
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
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.AllInTargetRpc))]
    private static void AllInAsked(TexasGamePlay __instance, NetworkConnectionToClient cn)
    {
        try
        {
            if (!NetworkServer.active || __instance == null || !Fair.IsRemote(cn)) return;
            Fresh();
            Count(_allInAsked, __instance.netId, 1);
        }
        catch { }
    }

    /// <summary>
    /// The host tells a seat to fold, with a shot it has already rolled: that seat's fold is then
    /// expected. Since the game's 14 Nov 2026 update the message goes out from inside this, without
    /// the call it used to go through, and with no opening message from the seat when the host
    /// asks for none.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.ServerFoldShot))]
    private static void FoldAsked(TexasGamePlay __instance, NetworkConnectionToClient cn)
    {
        try
        {
            if (!NetworkServer.active || __instance == null || !Fair.IsRemote(cn)) return;
            Fresh();
            Count(_foldAsked, __instance.netId, 1);
        }
        catch { }
    }

    private static void Register()
    {
        Fair.OnTick("Liar's Poker record", Tick);
        Fair.OnDrive("texas", Test);
    }

    /// <summary>How often the host looks over the table: often enough for a 30-second window.</summary>
    private const float LookEvery = 0.25f;

    private static float _nextLook;
    private static int _texasMatch = -1;
    private static readonly List<uint> _late = new();

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

        // A fold whose pull was never asked for, long past any honest game's time: rolled by the host.
        if (_owed.Count == 0) return;
        _late.Clear();
        foreach (var kv in _owed)
            if (now - kv.Value >= PullDeadline) _late.Add(kv.Key);
        foreach (var id in _late)
        {
            _owed.Remove(id);
            foreach (var p in m.Players)
            {
                var gp = p != null ? p.GetComponent<TexasGamePlay>() : null;
                if (gp == null || gp.netId != id) continue;
                if (!p.Dead && SettleOwed(gp))
                    Fair.Refused($"connection {(gp.connectionToClient != null ? gp.connectionToClient.connectionId : -1)}: " +
                                 $"Liar's Poker fold ({SeatName(p)}): its pull was not asked for in {PullDeadline:0} s; the host rolled it");
                break;
            }
        }
    }

    /// <summary>
    /// Roll a fold's pull for the seat, through the game's own handling of the seat's request: the
    /// shot, a death and the outcome sent to the seat are all the game's. True when it ran.
    /// </summary>
    private static bool SettleOwed(TexasGamePlay gp)
    {
        try
        {
            Fair.AsSentBy(null, () => gp.UserCode_CmdRequestRevolverOutcome());
            _hostSettled.Add(gp.netId);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"{Fair.Tag} Liar's Poker: the host could not roll a fold's pull: {e.Message}");
            return false;
        }
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

    /// <summary>
    /// The end of a fold. A fold the seat began and whose pull it never asked for is allowed too:
    /// the host rolls that pull itself first (see the class comment), so it is never a reason here.
    /// </summary>
    internal static Verdict Folded(in Facts f, out string why)
    {
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
        f.PullOwed = _owed.ContainsKey(id);

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

    /// <summary>
    /// A call or an all-in. The seat still holds the turn here when it is its own: the game takes it
    /// away a moment later, from the call itself (TexasGamePlay.WaitForGiveTurn).
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.UserCode_CmdRised__Boolean))]
    private static bool CmdRised(TexasGamePlay __instance, bool allIn)
    {
        try
        {
            if (!Fair.Hosting) return true;
            var conn = FairLegal.RemoteOwner(__instance, Fair.Sender);
            if (conn == null) return true;

            var p = FairLegal.SeatOf(__instance);
            var f = Gather(__instance, p);
            f.AllIn = allIn;
            var v = Rised(f, out string why);
            if (v != Verdict.Allow)
                return FairLegal.Apply(v, conn, $"Liar's Poker {(allIn ? "all-in" : "call")} ({SeatName(p)}): {why}");

            if (allIn && f.AllInAsked) Count(_allInAsked, __instance.netId, -1);
            else FairTurnCount.Acted(p, Raise);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"{Fair.Tag} Liar's Poker call check failed, the game's own handling runs: {e.Message}");
            return true;
        }
    }

    /// <summary>
    /// The message every fold opens with (and the one a showdown shot opens with, which is no fold).
    /// The game takes the turn away in this very message, so the turn is never asked about here. A
    /// fold the host asked for is already expected; any other is the seat's own, and its pull is
    /// then owed until the seat asks for it.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.UserCode_PlayCmdSes__Boolean__Boolean))]
    private static bool FoldBegin(TexasGamePlay __instance, bool noFold, bool coward)
    {
        try
        {
            if (!Fair.Hosting) return true;
            var conn = FairLegal.RemoteOwner(__instance, Fair.Sender);
            if (conn == null) return true;

            // Counted before anything else can fail: a fold that began and was not counted would
            // have its end refused, and nothing else hands the turn on.
            Fresh();
            uint id = __instance.netId;
            bool own = !noFold && Of(_foldAsked, id) == 0;
            if (own)
            {
                Count(_foldBegun, id, 1);
                _owed[id] = Time.unscaledTime;
                _hostSettled.Remove(id);
            }

            var p = FairLegal.SeatOf(__instance);
            var f = Gather(__instance, p);
            f.NoFold = noFold;
            f.Coward = coward;
            var v = FoldBegins(f, out string why);
            if (v == Verdict.Allow) return true;

            if (own)
            {
                Count(_foldBegun, id, -1);
                _owed.Remove(id);
            }
            return FairLegal.Apply(v, conn, $"Liar's Poker fold ({SeatName(p)}): {why}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"{Fair.Tag} Liar's Poker fold check failed, the game's own handling runs: {e.Message}");
            return true;
        }
    }

    /// <summary>Developer test "fair texas foldend": the armed seat's fold ends before its pull is asked for.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.UserCode_PlayCmdSes__Boolean__Boolean))]
    private static void FoldBegun(TexasGamePlay __instance, bool noFold, bool __runOriginal)
    {
        if (_foldEndTest < 0 || noFold || !__runOriginal || !Fair.TestsAllowed) return;
        try
        {
            var conn = FairLegal.RemoteOwner(__instance, Fair.Sender);
            if (conn == null || conn.connectionId != _foldEndTest) return;
            _foldEndTest = -1;
            FoldEndsNow(__instance, conn);
        }
        catch (Exception e) { Fair.Result("texas foldend", false, $"the test failed: {e.Message}"); }
    }

    /// <summary>
    /// A request for a seat's pull. Its pull is no longer owed. If the host already rolled it for a
    /// fold that ended first, the seat gets that same outcome, and the game does not roll again.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.UserCode_CmdRequestRevolverOutcome))]
    private static bool PullAsked(TexasGamePlay __instance)
    {
        try
        {
            if (!Fair.Hosting) return true;
            var conn = FairLegal.RemoteOwner(__instance, Fair.Sender);
            if (conn == null) return true;

            Fresh();
            uint id = __instance.netId;
            _owed.Remove(id);
            if (!_hostSettled.Remove(id)) return true;

            __instance.TargetReceiveRevolverOutcome(conn, __instance._shotWillDie, __instance._shotGodSave);
            Plugin.Log.LogInfo($"{Fair.Tag} connection {conn.connectionId}: a late request for a Liar's Poker pull the host " +
                               "already rolled - the same outcome sent again, no second roll");
            return false;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"{Fair.Tag} Liar's Poker pull request not checked, the game's own handling runs: {e.Message}");
            return true;
        }
    }

    /// <summary>
    /// The end of a fold. Its body is compiled into Mirror's handler, so the handler is what is
    /// patched. Every honest end follows a fold the seat began or the host asked for, and a fold the
    /// seat began has asked for its pull before it ends: Mirror keeps each connection's messages in
    /// order. One that has not is let through once the host has rolled the pull itself.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.InvokeUserCode_CmdFolded))]
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
                return FairLegal.Apply(v, conn, $"Liar's Poker fold ({SeatName(p)}): {why}");

            uint id = gp.netId;
            if (_owed.Remove(id) && p != null && !p.Dead && SettleOwed(gp))
                Fair.Refused($"connection {conn.connectionId}: Liar's Poker fold ({SeatName(p)}): " +
                             "a fold that ended before its pull was asked for; the host rolled it");

            // One expected fold is used up: one the host asked for first, as the seat's own opening
            // message counts as none while one is waiting; else the one the seat began.
            if (Of(_foldAsked, id) > 0) Count(_foldAsked, id, -1);
            else Count(_foldBegun, id, -1);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"{Fair.Tag} Liar's Poker fold check failed, the game's own handling runs: {e.Message}");
            return true;
        }
    }

    // ------------------------------------------------------------------ developer tests

    private static int _foldEndTest = -1;

    /// <summary>
    /// The test's fold end: the game's own handler for the seat's message, as that seat's copy
    /// would send it, run right after the seat's fold began and before its pull is asked for.
    /// </summary>
    private static void FoldEndsNow(TexasGamePlay gp, NetworkConnectionToClient conn)
    {
        int refused = Fair.RefusedCount;
        bool owed = _owed.ContainsKey(gp.netId);
        var writer = NetworkWriterPool.Get();
        var reader = NetworkReaderPool.Get(writer.ToArray());
        try { Fair.AsSentBy(conn, () => TexasGamePlay.InvokeUserCode_CmdFolded(gp, reader, conn)); }
        finally
        {
            NetworkReaderPool.Return(reader);
            NetworkWriterPool.Return(writer);
        }
        var p = FairLegal.SeatOf(gp);
        bool settled = _hostSettled.Contains(gp.netId);
        Fair.Result("texas foldend", owed && settled && Fair.RefusedCount == refused + 1,
                    $"connection {conn.connectionId} {SeatName(p)}: its fold ended before its pull was asked for - " +
                    $"pull owed {owed}, rolled by the host {settled} (it {(gp._shotWillDie ? "fired" : "was empty")}), " +
                    $"the fold went on: folded {gp.Folded}; its own request, when it comes, gets the same outcome");
    }

    /// <summary>
    /// "fair texas": every rule against a table of cases, nothing at the table touched.
    /// "fair texas seat [conn] call|allin|fold|coward|end [turn=0|1] [raised=0|1] [asked=0|1]
    /// [count=N] [recent=0|1] [watched=0|1] [whole=0|1] [expected=N] [owed=0|1] [enforce]": what the
    /// host would do right now with that message from that player's seat. Nothing runs for the seat.
    /// "fair texas foldend [conn]": at that seat's next fold, the host ends the fold as the seat's
    /// copy, before the seat asks for its pull, as a copy that skipped the request would.
    /// </summary>
    private static void Test(string rest)
    {
        var (words, pairs) = FairLegalTest.Parse(rest);
        if (words.Count > 0 && words[0].Equals("seat", StringComparison.OrdinalIgnoreCase)) { Seat(words, pairs); return; }
        if (words.Count > 0 && words[0].Equals("foldend", StringComparison.OrdinalIgnoreCase)) { ArmFoldEnd(words); return; }

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
        f = ok; f.HaveTurn = false;
        FairLegalTest.Expect(T, "a fold begins, the turn already taken by the game", FoldBegins(f, out _), Verdict.Allow, ref failed);
        f = ok; f.NoFold = true;
        FairLegalTest.Expect(T, "a showdown shot begins", FoldBegins(f, out _), Verdict.Allow, ref failed);
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
        FairLegalTest.Expect(T, "a fold ends before its pull was asked for (the host rolls it)", Folded(f, out _), Verdict.Allow, ref failed);
        f = ok; f.FoldExpected = 1; f.PullOwed = false;
        FairLegalTest.Expect(T, "a fold ends after its pull was asked for", Folded(f, out _), Verdict.Allow, ref failed);

        Fair.Result(T, failed == 0, failed == 0 ? "every rule gave the expected verdict" : $"{failed} rule(s) gave the wrong verdict");
    }

    private static void ArmFoldEnd(List<string> words)
    {
        const string T = "texas foldend";
        if (!Fair.Hosting) { Fair.Result(T, false, $"not hosting ({Fair.Why})"); return; }
        var conn = Fair.TestConnection(FairLegalTest.ConnArg(words, 1));
        var p = FairLegalTest.SeatOf(conn);
        if (p == null || p.GetComponent<TexasGamePlay>() == null) { Fair.Result(T, false, "no remote player seated at a Liar's Poker table"); return; }
        _foldEndTest = conn.connectionId;
        Plugin.Log.LogInfo($"{Fair.Tag} test: at connection {conn.connectionId}'s next fold, the host ends it before its pull is asked for");
    }

    private static void Seat(List<string> words, Dictionary<string, string> pairs)
    {
        const string T = "texas seat";
        if (!Fair.Hosting) { Fair.Result(T, false, $"not hosting ({Fair.Why})"); return; }

        var conn = Fair.TestConnection(FairLegalTest.ConnArg(words, 1));
        var p = FairLegalTest.SeatOf(conn);
        var gp = p != null ? p.GetComponent<TexasGamePlay>() : null;
        if (gp == null) { Fair.Result(T, false, "no remote player seated at a Liar's Poker table"); return; }

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
                        $"fold expected {f.FoldExpected}, pull owed {f.PullOwed}" +
                        $"{(move == "end" && f.PullOwed && v == Verdict.Allow ? " (the host would roll it first)" : "")}, whole match {f.Watched}";
        FairLegalTest.Finish(T, v, words, pairs, conn, detail, why);
    }
}
