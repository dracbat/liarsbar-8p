using System;
using System.Collections.Generic;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Liar's Poker: the host rolls every pull it owes, and carries out the outcome it rolled itself.
///
/// A pull is owed from the moment the host orders one (a showdown's shot, or a fold as an all-in
/// round ends) or a seat's fold begins. The host rolls it with the game's own roll, once: if the
/// usual moment has not come after a generous wait, it rolls it then and sends the outcome the
/// usual way, and that pull keeps that outcome, never a second roll. A fold ends only once its pull has been rolled
/// (<see cref="FairTexas"/>).
///
/// The host carries out the revolver outcome it rolled, at the usual moment, through the game's
/// own handler, and on its own a little later if the moment passes. It uses the very outcome it
/// rolled, never a fresh roll, so the host and every animation always agree.
///
/// Nothing here removes anyone: anything out of place is something lag could cause, so the host
/// simply acts on its own outcome and refuses the rest. Bots and the host's own seat keep the
/// game's own path.
/// </summary>
[FairPatch("Liar's Texas: the host rolls every pull it owes and carries out the outcome")]
[HarmonyPatch]
internal static class HostDecidesTexas
{
    private const string Outcome = "TexasGamePlay::TargetReceiveRevolverOutcome(";
    private const string ShotOrder = "TexasGamePlay::Revolver(";
    private const string FoldOrder = "TexasGamePlay::FoldTargetRpc(";

    /// <summary>The host lands its own outcome no later than this.</summary>
    private const float Deadline = 30f;

    /// <summary>The host rolls an owed pull itself no later than this after it became owed.</summary>
    internal const float PullDeadline = 30f;

    /// <summary>After a shot's outcome goes out, how long the seat is taken to be still finishing that pull.</summary>
    private const float Finishing = 10f;

    /// <summary>
    /// After a fold's outcome goes out, the seat is still finishing that pull until its fold ends,
    /// however long its game takes: its game ignores a fold order until then. At most this long.
    /// </summary>
    private const float FoldFinishing = 60f;

    private sealed class Death
    {
        internal TexasGamePlay Texas;
        internal float At;
        internal int Match;
        internal bool TestDropped;
    }

    /// <summary>The host's record of one seat's pulls in this match.</summary>
    private sealed class Pull
    {
        internal TexasGamePlay Texas;
        internal int Match;
        internal float OwedSince = -1f;   // when the pull still to be rolled became owed; -1 when none is
        internal float FinishingUntil;    // the seat is still finishing the pull just rolled
        internal bool Fold;               // the pull owed or just rolled is a fold's, which ends with the seat's fold
        internal bool Kept;               // the outcome the host rolled itself, kept for that pull's own ask
        internal bool KeptDies, KeptSaved;
        internal bool TestSkipped;
    }

    private static readonly Dictionary<uint, Death> _deaths = new();
    private static readonly Dictionary<uint, Pull> _pulls = new();
    private static readonly HashSet<int> _drop = new();
    private static readonly HashSet<int> _skip = new();
    private static uint _rolling;   // the seat whose owed pull the host is rolling itself right now

    // ------------------------------------------------------------------ owed pulls

    private static Pull Find(uint id) => _pulls.TryGetValue(id, out var p) && p.Match == Fair.Match ? p : null;

    /// <summary>True while the host owes this seat a pull it has not rolled yet.</summary>
    internal static bool PullOwed(uint id)
    {
        var p = Find(id);
        return p != null && p.OwedSince >= 0f;
    }

    /// <summary>
    /// A pull becomes owed to this seat: a fold's (<paramref name="fold"/>) or a shot's. A fold the
    /// host orders while the seat is still finishing the pull it just took is part of that same
    /// pull, so it owes nothing new.
    /// </summary>
    internal static void Owe(TexasGamePlay tex, bool fold, bool orderedFold)
    {
        if (tex == null || !Fair.Hosting) return;
        float now = Time.unscaledTime;
        var p = Find(tex.netId);
        if (p == null) _pulls[tex.netId] = p = new Pull { Texas = tex, Match = Fair.Match };
        if (p.OwedSince >= 0f) return;                       // already owed: the same pull
        if (orderedFold && now < p.FinishingUntil) return;   // the pull just rolled
        p.OwedSince = now;
        p.Fold = fold;
        p.Kept = false;                                      // a new pull: a kept outcome no longer answers
    }

    /// <summary>The seat's fold ended: it has finished the pull it took.</summary>
    internal static void FoldEnded(uint id)
    {
        var p = Find(id);
        if (p != null) p.FinishingUntil = 0f;
    }

    // ------------------------------------------------------------------ the host's sends

    /// <summary>
    /// The host sends a seat a shot or a fold order (a pull is owed from now), or its pull's
    /// outcome (the pull is rolled; keep a pull that fires).
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(NetworkBehaviour), nameof(NetworkBehaviour.SendTargetRPCInternal))]
    private static void Sending(NetworkBehaviour __instance, NetworkConnection conn, string functionFullName, NetworkWriter writer)
    {
        try
        {
            if (functionFullName == null || functionFullName.IndexOf("TexasGamePlay::", StringComparison.Ordinal) < 0) return;
            bool outcome = functionFullName.IndexOf(Outcome, StringComparison.Ordinal) >= 0;
            bool fold = !outcome && functionFullName.IndexOf(FoldOrder, StringComparison.Ordinal) >= 0;
            bool shot = !outcome && !fold && functionFullName.IndexOf(ShotOrder, StringComparison.Ordinal) >= 0;
            if (!outcome && !fold && !shot) return;
            if (!NetworkServer.active || !Fair.Hosting || __instance == null) return;

            var to = conn != null ? conn.TryCast<NetworkConnectionToClient>() : __instance.connectionToClient;
            if (!Fair.IsRemote(to)) return;
            var tex = __instance.TryCast<TexasGamePlay>();
            if (tex == null) return;

            if (!outcome)
            {
                Owe(tex, fold: fold, orderedFold: fold);
                return;
            }

            // The host's own outcome for this pull, as it sends it.
            if (writer == null || writer.Position < 2) return;
            var bytes = writer.ToArray();
            bool fires = bytes[0] != 0;

            var pull = Find(tex.netId);
            if (pull != null)
            {
                pull.OwedSince = -1f;
                pull.FinishingUntil = Time.unscaledTime + (pull.Fold ? FoldFinishing : Finishing);
                if (_rolling == tex.netId)
                {
                    pull.Kept = true;
                    pull.KeptDies = fires;
                    pull.KeptSaved = bytes[1] != 0;
                }
            }
            if (!fires) return;

            if (!_deaths.ContainsKey(tex.netId))
                _deaths[tex.netId] = new Death { Texas = tex, At = Time.unscaledTime, Match = Fair.Match };
        }
        catch (Exception e) { Plugin.Log.LogError($"{Fair.Tag} a Liar's Texas outcome could not be kept: {e.Message}"); }
    }

    /// <summary>
    /// A message asking for this seat's pull outcome: the game's own roll, or, for a pull the host
    /// already rolled itself, that same outcome again.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.InvokeUserCode_CmdRequestRevolverOutcome))]
    private static bool Asked(NetworkBehaviour obj, NetworkConnectionToClient senderConnection)
    {
        try
        {
            if (!Fair.Hosting) return true;
            var tex = obj != null ? obj.TryCast<TexasGamePlay>() : null;
            var conn = FairLegal.RemoteOwner(tex, senderConnection);
            if (conn == null) return true;
            int id = conn.connectionId;

            var pull = Find(tex.netId);
            if (Dev.Enabled && _skip.Remove(id))
            {
                if (pull == null || pull.OwedSince < 0f)
                    Fair.Result("texas pull", false, $"connection {id}: no owed pull on record, so the message went through");
                else
                {
                    pull.TestSkipped = true;
                    Plugin.Log.LogInfo($"{Fair.Tag} test: connection {id}'s Liar's Poker message dropped by the host; its deadline rolls the pull");
                    return false;
                }
            }
            if (pull == null || !pull.Kept) return true;

            pull.Kept = false;
            tex.TargetReceiveRevolverOutcome(conn, pull.KeptDies, pull.KeptSaved);
            Plugin.Log.LogInfo($"{Fair.Tag} Liar's Texas: connection {id}'s pull answered with the outcome the host already rolled for it");
            return false;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"{Fair.Tag} a Liar's Poker message could not be checked, the game's own handling runs: {e.Message}");
            return true;
        }
    }

    // ------------------------------------------------------------------ deaths

    /// <summary>This seat's death: the host's own outcome lands, and nothing else does.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.InvokeUserCode_CommandBeDead))]
    private static bool Died(NetworkBehaviour obj, NetworkConnectionToClient senderConnection)
    {
        bool handled = false;
        try
        {
            if (!Fair.Hosting || !Fair.IsRemote(senderConnection) || obj == null) return true;
            var owner = obj.connectionToClient;
            if (owner == null || owner.connectionId != senderConnection.connectionId) return true;
            var tex = obj.TryCast<TexasGamePlay>();
            if (tex == null) return true;
            handled = true;
            int id = senderConnection.connectionId;

            _deaths.TryGetValue(tex.netId, out var death);
            if (Dev.Enabled && _drop.Remove(id))
            {
                if (death != null) death.TestDropped = true;
                Plugin.Log.LogInfo($"{Fair.Tag} test: connection {id}'s Liar's Poker message dropped by the host; its deadline lands the outcome");
                return false;
            }
            if (death == null || death.Match != Fair.Match)
            {
                Fair.Refused($"connection {id}: a Liar's Poker message with no fired pull waiting");
                return false;
            }
            Land(death, "the seat's own word arrived");
            return false;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"{Fair.Tag} a Liar's Poker message could not be checked: {e.Message}");
            return !handled;
        }
    }

    private static void Land(Death death, string why)
    {
        _deaths.Remove(death.Texas != null ? death.Texas.netId : 0);
        var tex = death.Texas;
        if (tex == null || death.Match != Fair.Match) return;
        var ps = tex.GetComponent<PlayerStats>();
        if (ps == null || ps.Dead) return;
        Fair.AsSentBy(null, tex.UserCode_CommandBeDead);
        Plugin.Log.LogInfo($"{Fair.Tag} Liar's Texas seat {ps.Slot}'s fired pull landed by the host ({why})");
    }

    // ------------------------------------------------------------------ deadlines

    private static readonly List<Death> _due = new();
    private static readonly List<Pull> _owedDue = new();
    private static readonly List<uint> _stale = new();

    private static void Tick()
    {
        if (_deaths.Count == 0 && _pulls.Count == 0) return;
        if (!Fair.Hosting) { _deaths.Clear(); _pulls.Clear(); return; }
        float now = Time.unscaledTime;

        // Owed pulls not rolled in time: the host rolls them itself.
        _owedDue.Clear();
        _stale.Clear();
        foreach (var kv in _pulls)
        {
            var p = kv.Value;
            if (p.Texas == null || p.Match != Fair.Match) { _stale.Add(kv.Key); continue; }
            if (p.OwedSince >= 0f && now - p.OwedSince >= PullDeadline) _owedDue.Add(p);
        }
        foreach (var key in _stale) _pulls.Remove(key);
        foreach (var p in _owedDue) Roll(p);

        if (_deaths.Count == 0) return;
        _due.Clear();
        foreach (var d in _deaths.Values)
            if (d.Texas == null || d.Match != Fair.Match || now - d.At >= Deadline) _due.Add(d);
        foreach (var d in _due)
        {
            if (d.Texas == null || d.Match != Fair.Match) { _deaths.Remove(d.Texas != null ? d.Texas.netId : 0); continue; }
            Land(d, "the seat's game said nothing in time");
            if (d.TestDropped)
            {
                var ps = d.Texas.GetComponent<PlayerStats>();
                Fair.Result("texas drop", ps != null && ps.Dead, $"seat {(ps != null ? ps.Slot : -1)}: the host landed the fired pull on its own");
            }
        }
    }

    /// <summary>
    /// The host rolls an owed pull itself, with the game's own handler for the seat's ask. The
    /// outcome goes to the seat the usual way, and a pull that fires is landed by the deadline above.
    /// </summary>
    private static void Roll(Pull p)
    {
        p.OwedSince = -1f;   // rolled once, whatever happens next
        var tex = p.Texas;
        var ps = tex != null ? tex.GetComponent<PlayerStats>() : null;
        if (ps == null || ps.Dead || !Fair.IsRemote(tex.connectionToClient)) return;

        _rolling = tex.netId;
        try { Fair.AsSentBy(null, tex.UserCode_CmdRequestRevolverOutcome); }
        finally { _rolling = 0; }

        Plugin.Log.LogInfo($"{Fair.Tag} Liar's Texas seat {ps.Slot}'s owed pull rolled by the host " +
                           $"({(p.Kept ? (p.KeptDies ? "it fires" : "it does not fire") : "no outcome went out")})");
        if (p.TestSkipped)
        {
            p.TestSkipped = false;
            Fair.Result("texas pull", p.Kept, $"seat {ps.Slot}: the host rolled the owed pull itself and sent the outcome: {p.Kept}");
        }
    }

    // ------------------------------------------------------------------ developer tests

    /// <summary>
    /// "fair texasdeath drop [conn]": the host throws away that seat's next message, as if it never
    /// came; the deadline must land the death the host rolled.
    /// "fair texaspull skip [conn]": the host throws away that seat's next ask for a pull outcome, as
    /// if it never came; the pull stays owed and the deadline must roll it and send the outcome.
    /// "fair texaspull skip [conn] takeover": the same, with this copy counted until the next match
    /// as one that took the table over in a host migration.
    /// </summary>
    internal static void Register()
    {
        Fair.OnTick("Liar's Texas deadline", Tick);
        Fair.OnDrive("texasdeath", rest =>
        {
            string[] words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || words[0] != "drop") { Plugin.Log.LogWarning($"{Fair.Tag} test: texasdeath drop [connection]"); return; }
            var conn = Fair.TestConnection(words.Length > 1 ? words[1] : "");
            if (conn == null) { Fair.Result("texas drop", false, "no remote player connected"); return; }
            _drop.Add(conn.connectionId);
            Plugin.Log.LogInfo($"{Fair.Tag} test: connection {conn.connectionId}'s next Liar's Poker message will be dropped by the host");
        });
        Fair.OnDrive("texaspull", rest =>
        {
            string[] words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || words[0] != "skip") { Plugin.Log.LogWarning($"{Fair.Tag} test: texaspull skip [connection] [takeover]"); return; }
            bool takeover = words.Length > 1 && words[words.Length - 1] == "takeover";
            var conn = Fair.TestConnection(words.Length > 1 && words[1] != "takeover" ? words[1] : "");
            if (conn == null) { Fair.Result("texas pull", false, "no remote player connected"); return; }
            if (takeover) FairLegal.PretendTookOverForTest();
            _skip.Add(conn.connectionId);
            Plugin.Log.LogInfo($"{Fair.Tag} test: connection {conn.connectionId}'s next Liar's Poker message will be dropped by the host; " +
                               $"the host rolls the pull itself {PullDeadline:0} s after it became owed " +
                               $"(watched the whole match: {FairLegal.WatchedWholeMatch})");
        });
    }
}
