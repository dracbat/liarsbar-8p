using System;
using System.Collections.Generic;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Liar's Spin (Liar's Slots): at a table this copy hosts, the host accepts a player's claim,
/// execution, liar call, all-red penalty and jackpot answer only when the rules allow it, judged
/// from the host's own record of the table.
///
/// What is checked, and how hard:
/// <list type="bullet">
/// <item>Every move needs the seat to hold the turn, to be alive, and not to have moved already
/// in that turn. Anything else is refused quietly: a late move after the turn has passed is
/// ordinary lag.</item>
/// <item>A claim is a number from zero up to the game's own ceiling. A number no copy's keys can
/// produce (below zero, or far beyond the ceiling) is a clear cheat; one just above the ceiling
/// is refused.</item>
/// <item>An execution needs five points. The host keeps, for each seat, the last moment its own
/// record showed five or more. Points can fall a moment before an honest execution arrives (the
/// all-red penalty is applied first in the same frame, for one), so an execution is only a clear
/// cheat when the host has watched that seat for a good while and its points have not reached
/// five at any time in that while, nor since the start of the seat's previous turn.</item>
/// <item>A liar call needs a claim on the table from somebody else, and no call already under way.</item>
/// <item>The all-red penalty and the jackpot answer are refused only where they make no sense
/// (the claimant penalising itself, an answer out of turn or twice); they are never a cheat.</item>
/// <item>The all-red penalty falls on the seat whose turn starts while it is due. At a remote
/// player's seat the host notes that seat, accepts the penalty only from it, and applies it
/// itself through the game's own handler before that seat's next move, when its turn ends, or
/// after <see cref="PayerDeadline"/>, whichever comes first. A bot's seat and the host's own keep
/// the game's own path.</item>
/// </list>
///
/// Bots run their moves on the host with no sender, and the host's own seat is the local
/// connection: neither is ever checked. Every allowed move runs the game's own handler, so the
/// table animates exactly as it always did.
/// </summary>
[FairPatch("Liar's Spin: the host accepts only moves the rules allow")]
[HarmonyPatch]
internal static class FairSpin
{
    /// <summary>How long the host must have watched a seat, and how far back it looks, before an execution can be a cheat.</summary>
    internal const float Window = 20f;

    /// <summary>Points an execution needs.</summary>
    internal const int KillPoints = 5;

    private const string Move = "spin move";
    private const string Jackpot = "spin jackpot";

    // ------------------------------------------------------------------ the host's record

    private static int _match = -1;
    private static readonly Dictionary<uint, float> _since = new();       // first look at the seat this match
    private static readonly Dictionary<uint, float> _lastFive = new();    // last time its points were five or more
    private static readonly Dictionary<uint, bool> _held = new();         // whether it held the turn at the last look
    private static readonly Dictionary<uint, (float Now, float Before)> _turnAt = new();

    /// <summary>How long the host waits for the noted seat to take the all-red penalty before applying it itself.</summary>
    internal const float PayerDeadline = 20f;

    // The seat the all-red penalty fell to (a remote player's only), since when, and whether its turn has ended.
    private static uint _payer;
    private static float _payerSince;
    private static bool _payerTurnOver;

    private static void Fresh()
    {
        if (_match == Fair.Match) return;
        _match = Fair.Match;
        _since.Clear();
        _lastFive.Clear();
        _held.Clear();
        _turnAt.Clear();
        _payer = 0;
        _payerTurnOver = false;
    }

    private static void Look(PlayerStats p, float now)
    {
        if (p == null) return;
        Fresh();
        uint id = p.netId;
        if (!_since.ContainsKey(id)) _since[id] = now;
        if (p.spinPoint >= KillPoints) _lastFive[id] = now;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerStats), nameof(PlayerStats.NetworkspinPoint), MethodType.Setter)]
    private static void PointsSet(PlayerStats __instance)
    {
        try
        {
            if (!NetworkServer.active || __instance == null || __instance.GetComponent<SpinGamePlay>() == null) return;
            Look(__instance, Time.unscaledTime);
        }
        catch { }
    }

    /// <summary>A turn starts when the flag goes from off to on; setting it again while held starts nothing.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerStats), nameof(PlayerStats.NetworkHaveTurn), MethodType.Setter)]
    private static void TurnSet(PlayerStats __instance)
    {
        try
        {
            if (!NetworkServer.active || __instance == null || __instance.GetComponent<SpinGamePlay>() == null) return;
            Fresh();
            Turn(__instance, Time.unscaledTime);
        }
        catch { }
    }

    private static void Turn(PlayerStats p, float now)
    {
        uint id = p.netId;
        bool has = p.HaveTurn;
        _held.TryGetValue(id, out bool had);
        _held[id] = has;
        if (has && !had)
        {
            _turnAt.TryGetValue(id, out var t);
            _turnAt[id] = (now, t.Now);
            NotePayer(p, now);
        }
        else if (!has && had && _payer != 0 && _payer == id)
        {
            _payerTurnOver = true;   // applied on the next tick, outside the game's own handling
        }
        Look(p, now);
    }

    // ------------------------------------------------------------------ the all-red penalty

    /// <summary>
    /// A seat's turn starts while the all-red penalty is due and the claim on the table is another
    /// seat's: that seat takes the penalty, as the game has it. Noted only for a remote player's seat.
    /// </summary>
    private static void NotePayer(PlayerStats p, float now)
    {
        if (_payer != 0 || !Fair.Hosting) return;
        var m = Manager.Instance;
        var spin = m != null ? m.SpinGame : null;
        if (spin == null || !spin.GiveAllRedPenalty) return;
        var last = spin.LastBetPlayer;
        if (last == null || last.GetInstanceID() == p.gameObject.GetInstanceID()) return;
        var gp = p.GetComponent<SpinGamePlay>();
        if (gp == null || !Fair.IsRemote(gp.connectionToClient)) return;
        _payer = p.netId;
        _payerSince = now;
        _payerTurnOver = false;
    }

    /// <summary>Whether the penalty noted for a seat is still waiting.</summary>
    private static bool PenaltyWaiting(LiarsSpinGameplayManager spin)
    {
        if (_payer == 0) return false;
        if (spin != null && spin.GiveAllRedPenalty) return true;
        _payer = 0;   // taken, through the game's own handler
        _payerTurnOver = false;
        return false;
    }

    /// <summary>The host applies the noted seat's penalty itself, through the game's own server handler.</summary>
    private static void ApplyPenalty(string why)
    {
        uint id = _payer;
        _payer = 0;
        _payerTurnOver = false;

        var m = Manager.Instance;
        var spin = m != null ? m.SpinGame : null;
        if (spin == null || !spin.GiveAllRedPenalty || m.Players == null) return;
        PlayerStats p = null;
        foreach (var s in m.Players)
            if (s != null && s.netId == id) { p = s; break; }
        if (p == null || p.Dead) return;
        var gp = p.GetComponent<SpinGamePlay>();
        var owner = gp != null ? gp.connectionToClient : null;
        if (!Fair.IsRemote(owner)) return;

        Fair.Refused($"connection {owner.connectionId}: Liar's Spin seat {p.Slot}'s all-red penalty applied by the host ({why})");
        int before = p.spinPoint;
        Fair.AsSentBy(null, gp.UserCode_Cmd_ApplyAllRedPenalty);

        if (_skippedForTest == id)
        {
            _skippedForTest = 0;
            Fair.Result("spin skip", !spin.GiveAllRedPenalty && p.spinPoint == Math.Max(0, before - 3),
                        $"seat {p.Slot}: the host applied the penalty itself ({why}), points {before} to {p.spinPoint}");
        }
    }

    // ------------------------------------------------------------------ a developer test switch

    private static readonly HashSet<int> _skipForTest = new();
    private static uint _skippedForTest;

    /// <summary>
    /// "fair spin skip [conn]": the host sets aside that seat's next all-red penalty as if it never
    /// came, so the host's own application can be seen. Inside the loopback harness only.
    /// </summary>
    private static bool SkipForTest(SpinGamePlay gp, NetworkConnectionToClient sender)
    {
        if (_skipForTest.Count == 0 || !Fair.TestsAllowed || !Fair.IsRemote(sender)) return false;
        if (!_skipForTest.Remove(sender.connectionId)) return false;
        var p = FairLegal.SeatOf(gp);
        _skippedForTest = p != null ? p.netId : 0;
        Plugin.Log.LogInfo($"{Fair.Tag} test: connection {sender.connectionId}'s all-red penalty set aside by the host; the host applies it itself");
        return true;
    }

    /// <summary>A move from the noted seat while its penalty still waits: the penalty comes first, as in the game.</summary>
    private static void PenaltyFirst(PlayerStats p)
    {
        if (p == null || _payer == 0 || p.netId != _payer) return;
        var m = Manager.Instance;
        if (!PenaltyWaiting(m != null ? m.SpinGame : null)) return;
        ApplyPenalty("the seat moved first");
    }

    private static void Register()
    {
        Fair.OnTick("Liar's Spin record", Tick);
        Fair.OnDrive("spin", Test);
    }

    /// <summary>
    /// Every frame on the host, a look at each seat's points, so no change is missed, and the noted
    /// all-red penalty applied once its seat's turn is over or the deadline has passed.
    /// </summary>
    private static void Tick()
    {
        if (!NetworkServer.active || !Fair.Hosting) return;
        var m = Manager.Instance;
        if (m == null || m.Players == null || m.Players.Count == 0) return;
        if (m.Players[0] == null || m.Players[0].GetComponent<SpinGamePlay>() == null) return;

        float now = Time.unscaledTime;
        foreach (var p in m.Players)
            if (p != null) Look(p, now);

        if (PenaltyWaiting(m.SpinGame) && (_payerTurnOver || now - _payerSince >= PayerDeadline))
            ApplyPenalty(_payerTurnOver ? "its turn ended" : "the deadline passed");
    }

    // ------------------------------------------------------------------ the rules

    /// <summary>Everything a rule looks at, read from the host's own record.</summary>
    internal struct Facts
    {
        internal bool Dead, HaveTurn, MovedThisTurn, AnsweredThisTurn, CanGiveBid, CallingLiar;
        internal bool BetBefore, LastBetNone, LastBetIsMe, PenaltyPending;
        internal bool PayerNoted, IsPayer;   // the host noted which seat takes the all-red penalty, and whether it is this one
        internal int LastShowsCount, MaxCount, Count, Points;
        internal bool FiveRecently, WatchedLongEnough;
    }

    /// <summary>The highest claim no copy of any build could send: four reels a seat at the largest table.</summary>
    internal static int HardCeiling(int maxCount) => Math.Max(maxCount, SpinBidCap.ReelsPerSeat * Limits.LobbySeats);

    private static Verdict InTurn(in Facts f, out string why)
    {
        if (f.Dead) { why = "a move from a seat out of the match"; return Verdict.Refuse; }
        if (!f.HaveTurn) { why = "a move out of turn"; return Verdict.Refuse; }
        if (f.MovedThisTurn) { why = "a second move in one turn"; return Verdict.Refuse; }
        why = null;
        return Verdict.Allow;
    }

    internal static Verdict Kill(in Facts f, out string why)
    {
        var v = InTurn(f, out why);
        if (v != Verdict.Allow) return v;
        if (!f.CanGiveBid) { why = "an execution after the moment to move had passed"; return Verdict.Refuse; }
        if (f.Points >= KillPoints || f.FiveRecently) { why = null; return Verdict.Allow; }
        if (f.WatchedLongEnough) { why = "an execution with too few points, which the seat never had"; return Verdict.Caught; }
        why = "an execution with too few points";
        return Verdict.Refuse;
    }

    internal static Verdict Rise(in Facts f, out string why)
    {
        if (f.Count < 0 || f.Count > HardCeiling(f.MaxCount)) { why = "a claim outside anything the game allows"; return Verdict.Caught; }
        var v = InTurn(f, out why);
        if (v != Verdict.Allow) return v;
        // No check of the "may bid" flag here: when the clock runs out an unchanged copy makes its
        // claim for the player without looking at it, so a claim can honestly arrive while it is off.
        if (f.MaxCount > 0 && f.Count > f.MaxCount) { why = "a claim above the ceiling"; return Verdict.Refuse; }
        why = null;
        return Verdict.Allow;
    }

    internal static Verdict Call(in Facts f, out string why)
    {
        var v = InTurn(f, out why);
        if (v != Verdict.Allow) return v;
        if (!f.CanGiveBid) { why = "a liar call after the moment to move had passed"; return Verdict.Refuse; }
        if (f.CallingLiar) { why = "a liar call while one is under way"; return Verdict.Refuse; }
        if (!f.BetBefore || f.LastBetNone) { why = "a liar call with no claim on the table"; return Verdict.Refuse; }
        if (f.LastBetIsMe) { why = "a liar call on the seat's own claim"; return Verdict.Refuse; }
        if (f.LastShowsCount <= 0 || f.LastShowsCount >= KillPoints) { why = "a liar call on a claim that cannot be called"; return Verdict.Refuse; }
        why = null;
        return Verdict.Allow;
    }

    internal static Verdict Penalty(in Facts f, out string why)
    {
        if (f.PenaltyPending && f.LastBetIsMe) { why = "the claimant taking the all-red penalty meant for the others"; return Verdict.Refuse; }
        if (f.PenaltyPending && f.PayerNoted && !f.IsPayer) { why = "an all-red penalty from a seat it did not fall to"; return Verdict.Refuse; }
        why = null;
        return Verdict.Allow;
    }

    internal static Verdict Answer(in Facts f, out string why)
    {
        if (!f.HaveTurn) { why = "a jackpot answer out of turn"; return Verdict.Refuse; }
        if (f.AnsweredThisTurn) { why = "a second jackpot answer in one turn"; return Verdict.Refuse; }
        why = null;
        return Verdict.Allow;
    }

    /// <summary>The host's facts about one seat, right now.</summary>
    internal static Facts Gather(SpinGamePlay gp, PlayerStats p, int count)
    {
        var f = new Facts { Count = count };
        float now = Time.unscaledTime;
        if (p != null)
        {
            Fresh();
            Look(p, now);
            uint id = p.netId;
            f.Dead = p.Dead;
            f.HaveTurn = p.HaveTurn;
            f.MovedThisTurn = FairTurnCount.Already(p, Move);
            f.AnsweredThisTurn = FairTurnCount.Already(p, Jackpot);
            f.Points = p.spinPoint;

            _turnAt.TryGetValue(id, out var t);
            bool seen = _since.TryGetValue(id, out float since);
            bool five = _lastFive.TryGetValue(id, out float lastFive);
            float back = now - Window;
            // The previous turn's start, when the host saw it; otherwise only the time window counts.
            float from = t.Before > 0f ? Math.Min(back, t.Before) : back;
            f.FiveRecently = five && lastFive >= from;
            f.WatchedLongEnough = seen && t.Before > 0f && since <= from && since <= back;
        }

        var m = Manager.Instance;
        var spin = m != null ? m.SpinGame : null;
        if (spin != null)
        {
            f.CanGiveBid = spin.CanGiveBid;
            f.CallingLiar = spin.isCallingLiar;
            f.BetBefore = spin.isBetGivenBefore;
            f.LastShowsCount = spin.LastShowsCount;
            f.MaxCount = spin.MaxCount;
            f.PenaltyPending = spin.GiveAllRedPenalty;
            f.PayerNoted = PenaltyWaiting(spin);
            f.IsPayer = f.PayerNoted && p != null && p.netId == _payer;
            var last = spin.LastBetPlayer;
            f.LastBetNone = last == null;
            f.LastBetIsMe = last != null && gp != null && last.GetInstanceID() == gp.gameObject.GetInstanceID();
        }
        return f;
    }

    // ------------------------------------------------------------------ the hooks

    private delegate Verdict Rule(in Facts f, out string why);

    /// <summary>One move from a remote player: judge it, and let the game's own handler run only when allowed.</summary>
    private static bool Judge(SpinGamePlay gp, NetworkConnectionToClient sender, Rule rule, int count, string kind, string what)
    {
        try
        {
            if (!Fair.Hosting) return true;
            var conn = FairLegal.RemoteOwner(gp, sender);
            if (conn == null) return true;

            var p = FairLegal.SeatOf(gp);
            if (kind != null) PenaltyFirst(p);
            var f = Gather(gp, p, count);
            var v = rule(f, out string why);
            if (v == Verdict.Allow)
            {
                if (kind != null) FairTurnCount.Acted(p, kind);
                return true;
            }
            string seat = p != null ? $"seat {p.Slot}" : "no seat";
            return FairLegal.Apply(v, conn, $"Liar's Spin {what} ({seat}): {why}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"{Fair.Tag} Liar's Spin {what} check failed, the game's own handling runs: {e.Message}");
            return true;
        }
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    [HarmonyPatch(typeof(SpinGamePlay), nameof(SpinGamePlay.UserCode_KillCmd))]
    private static bool KillCmd(SpinGamePlay __instance) =>
        Judge(__instance, Fair.Sender, Kill, 0, Move, "execution");

    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    [HarmonyPatch(typeof(SpinGamePlay), nameof(SpinGamePlay.UserCode_RiseCmd__Int32))]
    private static bool RiseCmd(SpinGamePlay __instance, int count) =>
        Judge(__instance, Fair.Sender, Rise, count, Move, "claim");

    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    [HarmonyPatch(typeof(SpinGamePlay), nameof(SpinGamePlay.UserCode_CallLiar))]
    private static bool CallLiar(SpinGamePlay __instance) =>
        Judge(__instance, Fair.Sender, Call, 0, Move, "liar call");

    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    [HarmonyPatch(typeof(SpinGamePlay), nameof(SpinGamePlay.UserCode_Cmd_ApplyAllRedPenalty))]
    private static bool ApplyAllRedPenalty(SpinGamePlay __instance)
    {
        if (SkipForTest(__instance, Fair.Sender)) return false;
        return Judge(__instance, Fair.Sender, Penalty, 0, null, "all-red penalty");
    }

    /// <summary>The jackpot answer's body is compiled into Mirror's handler, so the handler is what is patched.</summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    [HarmonyPatch(typeof(SpinGamePlay), nameof(SpinGamePlay.InvokeUserCode_CmdAnswerJackpot))]
    private static bool AnswerJackpot(NetworkBehaviour obj, NetworkConnectionToClient senderConnection)
    {
        SpinGamePlay gp = null;
        try { gp = obj != null ? obj.TryCast<SpinGamePlay>() : null; }
        catch { }
        if (gp == null) return true;
        return Judge(gp, senderConnection, Answer, 0, Jackpot, "jackpot answer");
    }

    // ------------------------------------------------------------------ developer tests

    /// <summary>
    /// "fair spin": every rule against a table of cases, with nothing at the table touched.
    /// "fair spin seat [conn] kill|rise|call|penalty|jackpot [count=N] [points=N] [turn=0|1]
    /// [moved=0|1] [recent=0|1] [watched=0|1] [due=0|1] [noted=0|1] [payer=0|1] [enforce]": what the host would do right now with
    /// that move from that player's seat, its record overridden by any pairs given. Nothing runs
    /// for the seat; "enforce" carries out a removal the verdict calls for, through the real path.
    /// "fair spin skip [conn]": the host sets aside that seat's next all-red penalty, and reports
    /// when it has applied the penalty itself.
    /// </summary>
    private static void Test(string rest)
    {
        var (words, pairs) = FairLegalTest.Parse(rest);
        if (words.Count > 0 && words[0].Equals("seat", StringComparison.OrdinalIgnoreCase)) { Seat(words, pairs); return; }
        if (words.Count > 0 && words[0].Equals("skip", StringComparison.OrdinalIgnoreCase))
        {
            var conn = Fair.TestConnection(FairLegalTest.ConnArg(words, 1));
            if (conn == null) { Fair.Result("spin skip", false, "no remote player connected"); return; }
            _skipForTest.Add(conn.connectionId);
            Plugin.Log.LogInfo($"{Fair.Tag} test: connection {conn.connectionId}'s next all-red penalty will be set aside by the host");
            return;
        }
        Table();
    }

    private static void Table()
    {
        int failed = 0;
        var ok = new Facts { HaveTurn = true, CanGiveBid = true, BetBefore = true, LastShowsCount = 3, MaxCount = 4, Points = 6, WatchedLongEnough = true };
        const string T = "spin";

        FairLegalTest.Expect(T, "execution with five points in turn", Kill(ok, out _), Verdict.Allow, ref failed);
        var f = ok; f.Points = 2; f.FiveRecently = true;
        FairLegalTest.Expect(T, "execution just after the penalty took points away", Kill(f, out _), Verdict.Allow, ref failed);
        f = ok; f.Points = 2; f.FiveRecently = false; f.WatchedLongEnough = false;
        FairLegalTest.Expect(T, "execution with too few points, seat not watched long", Kill(f, out _), Verdict.Refuse, ref failed);
        f = ok; f.Points = 2;
        FairLegalTest.Expect(T, "execution with points the seat never had", Kill(f, out _), Verdict.Caught, ref failed);
        f = ok; f.Points = 2; f.HaveTurn = false;
        FairLegalTest.Expect(T, "execution with too few points out of turn", Kill(f, out _), Verdict.Refuse, ref failed);
        f = ok; f.MovedThisTurn = true;
        FairLegalTest.Expect(T, "second move in one turn", Kill(f, out _), Verdict.Refuse, ref failed);

        f = ok; f.Count = 0;
        FairLegalTest.Expect(T, "claim of zero", Rise(f, out _), Verdict.Allow, ref failed);
        f = ok; f.Count = 4;
        FairLegalTest.Expect(T, "claim at the ceiling", Rise(f, out _), Verdict.Allow, ref failed);
        f = ok; f.Count = 1; f.LastShowsCount = 3;
        FairLegalTest.Expect(T, "claim lower than the last one", Rise(f, out _), Verdict.Allow, ref failed);
        f = ok; f.Count = 5;
        FairLegalTest.Expect(T, "claim just above the ceiling", Rise(f, out _), Verdict.Refuse, ref failed);
        f = ok; f.Count = -1;
        FairLegalTest.Expect(T, "claim below zero", Rise(f, out _), Verdict.Caught, ref failed);
        f = ok; f.Count = 1000;
        FairLegalTest.Expect(T, "claim far above anything", Rise(f, out _), Verdict.Caught, ref failed);
        f = ok; f.Count = 2; f.HaveTurn = false;
        FairLegalTest.Expect(T, "claim out of turn", Rise(f, out _), Verdict.Refuse, ref failed);
        f = ok; f.Count = 2; f.Dead = true;
        FairLegalTest.Expect(T, "claim from a seat out of the match", Rise(f, out _), Verdict.Refuse, ref failed);

        FairLegalTest.Expect(T, "liar call on another seat's claim", Call(ok, out _), Verdict.Allow, ref failed);
        f = ok; f.LastBetIsMe = true;
        FairLegalTest.Expect(T, "liar call on its own claim", Call(f, out _), Verdict.Refuse, ref failed);
        f = ok; f.BetBefore = false;
        FairLegalTest.Expect(T, "liar call with no claim", Call(f, out _), Verdict.Refuse, ref failed);
        f = ok; f.LastShowsCount = 5;
        FairLegalTest.Expect(T, "liar call on an execution", Call(f, out _), Verdict.Refuse, ref failed);
        f = ok; f.CallingLiar = true;
        FairLegalTest.Expect(T, "liar call while one is under way", Call(f, out _), Verdict.Refuse, ref failed);

        f = ok; f.PenaltyPending = true;
        FairLegalTest.Expect(T, "all-red penalty taken by another seat", Penalty(f, out _), Verdict.Allow, ref failed);
        f = ok; f.PenaltyPending = true; f.LastBetIsMe = true;
        FairLegalTest.Expect(T, "all-red penalty taken by the claimant", Penalty(f, out _), Verdict.Refuse, ref failed);
        f = ok; f.PenaltyPending = true; f.HaveTurn = false; f.Dead = true;
        FairLegalTest.Expect(T, "all-red penalty from a seat out of the match", Penalty(f, out _), Verdict.Allow, ref failed);
        f = ok; f.PenaltyPending = true; f.PayerNoted = true; f.IsPayer = true;
        FairLegalTest.Expect(T, "all-red penalty from the seat it fell to", Penalty(f, out _), Verdict.Allow, ref failed);
        f = ok; f.PenaltyPending = true; f.PayerNoted = true; f.IsPayer = false;
        FairLegalTest.Expect(T, "all-red penalty from a seat it did not fall to", Penalty(f, out _), Verdict.Refuse, ref failed);
        f = ok; f.PenaltyPending = true; f.PayerNoted = true; f.IsPayer = true; f.LastBetIsMe = true;
        FairLegalTest.Expect(T, "all-red penalty from the claimant, noted or not", Penalty(f, out _), Verdict.Refuse, ref failed);

        FairLegalTest.Expect(T, "jackpot answer in turn", Answer(ok, out _), Verdict.Allow, ref failed);
        f = ok; f.AnsweredThisTurn = true;
        FairLegalTest.Expect(T, "second jackpot answer", Answer(f, out _), Verdict.Refuse, ref failed);
        f = ok; f.HaveTurn = false;
        FairLegalTest.Expect(T, "jackpot answer out of turn", Answer(f, out _), Verdict.Refuse, ref failed);

        Fair.Result(T, failed == 0, failed == 0 ? "every rule gave the expected verdict" : $"{failed} rule(s) gave the wrong verdict");
    }

    private static void Seat(List<string> words, Dictionary<string, string> pairs)
    {
        const string T = "spin seat";
        if (!Fair.Hosting) { Fair.Result(T, false, $"not hosting ({Fair.Why})"); return; }

        var conn = Fair.TestConnection(FairLegalTest.ConnArg(words, 1));
        var p = FairLegalTest.SeatOf(conn);
        var gp = p != null ? p.GetComponent<SpinGamePlay>() : null;
        if (gp == null) { Fair.Result(T, false, "no remote player seated at a Liar's Spin table"); return; }

        string move = "kill";
        foreach (var w in words) if (w is "kill" or "rise" or "call" or "penalty" or "jackpot") move = w;

        var f = Gather(gp, p, FairLegalTest.Int(pairs, "count", 1));
        f.Points = FairLegalTest.Int(pairs, "points", f.Points);
        f.HaveTurn = FairLegalTest.Bool(pairs, "turn", f.HaveTurn);
        f.MovedThisTurn = FairLegalTest.Bool(pairs, "moved", f.MovedThisTurn);
        f.FiveRecently = FairLegalTest.Bool(pairs, "recent", f.FiveRecently);
        f.WatchedLongEnough = FairLegalTest.Bool(pairs, "watched", f.WatchedLongEnough);
        f.CanGiveBid = FairLegalTest.Bool(pairs, "canbid", f.CanGiveBid);
        f.PenaltyPending = FairLegalTest.Bool(pairs, "due", f.PenaltyPending);
        f.PayerNoted = FairLegalTest.Bool(pairs, "noted", f.PayerNoted);
        f.IsPayer = FairLegalTest.Bool(pairs, "payer", f.IsPayer);

        string why;
        Verdict v = move switch
        {
            "rise" => Rise(f, out why),
            "call" => Call(f, out why),
            "penalty" => Penalty(f, out why),
            "jackpot" => Answer(f, out why),
            _ => Kill(f, out why),
        };
        string detail = $"connection {conn.connectionId} seat {p.Slot} {move}: {v}{(why != null ? $" ({why})" : "")} " +
                        $"- points {f.Points}, turn {f.HaveTurn}, moved {f.MovedThisTurn}, five recently {f.FiveRecently}, watched {f.WatchedLongEnough}" +
                        $", penalty due {f.PenaltyPending}, payer noted {f.PayerNoted}, this seat {f.IsPayer}";

        FairLegalTest.Finish(T, v, words, pairs, conn, detail, why);
    }
}
