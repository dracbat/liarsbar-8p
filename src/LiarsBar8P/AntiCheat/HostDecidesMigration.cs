using System;
using System.Collections.Generic;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// After a host migration, the new host rebuilds each seat's revolver itself: it keeps the public
/// pull count and draws the chamber again, evenly among those not yet pulled (the cylinder's size
/// from the house rules), which is exactly as likely as the real one was. For the count it keeps
/// the highest of what it saw itself while it was still a player, what the seat already shows,
/// and what the game's own restore hands it, so a count never moves back.
///
/// The host takes one restore per seat, right after this copy took the table over, and only one
/// that fits its own record; anything else is refused. The rebuild then runs through the game's own apply,
/// so every copy shows it as before.
/// </summary>
[FairPatch("after a host migration the host rebuilds each revolver")]
[HarmonyPatch]
internal static class HostDecidesMigration
{
    // ------------------------------------------------------------------ what this copy saw as a player

    /// <summary>A record is only used this soon after it was seen, and only from the latest stretch of play.</summary>
    private const float RecordAge = 120f;

    /// <summary>Seats gone this long mean the match is over; a new stretch of play starts when they come back.</summary>
    private const float Gap = 15f;

    private static readonly Dictionary<ulong, (int Pulled, float At, int Stretch)> _seen = new();
    private static int _stretch;
    private static float _lastSeen = -1000f;
    private static float _nextLook;

    /// <summary>
    /// Twice a second, while this copy is a player at someone else's table: the public pull count
    /// of every seat, by Steam id. Never the bullet. Within one stretch of play the highest count
    /// is kept (a count only goes up during a match, and a migration briefly shows fresh seats);
    /// a stretch ends when no seat has been seen for a while, so a count from an earlier match is
    /// never used.
    /// </summary>
    private static void Look()
    {
        float now = Time.unscaledTime;
        if (now < _nextLook) return;
        _nextLook = now + 0.5f;
        if (NetworkServer.active || !NetworkClient.active || !PlayingAtATable()) return;

        var counts = new List<(PlayerStats Ps, int Pulled)>();
        foreach (var d in UnityEngine.Object.FindObjectsOfType<DeckGameplay>()) Add(counts, d, d.currentRevolver);
        foreach (var c in UnityEngine.Object.FindObjectsOfType<ChaosDeckGameplay>()) Add(counts, c, c.currentrevoler);
        foreach (var c in UnityEngine.Object.FindObjectsOfType<ChaosGamePlay>()) Add(counts, c, c.currentrevoler);
        if (counts.Count == 0) return;

        if (now - _lastSeen > Gap) _stretch++;
        _lastSeen = now;
        foreach (var (ps, pulled) in counts)
        {
            ulong id = ps.Player_Id;
            int keep = _seen.TryGetValue(id, out var r) && r.Stretch == _stretch ? Math.Max(r.Pulled, pulled) : pulled;
            _seen[id] = (keep, now, _stretch);
        }
    }

    /// <summary>A match is on, and not ranked or the Velvet Room, where nothing here is ever used.</summary>
    internal static bool PlayingAtATable()
    {
        try
        {
            if (RankedGuard.Near || Manager.Instance == null) return false;
            var nm = Fair.Net;
            return nm != null && !nm.Matchmaking && !nm.isVelvetRoom;
        }
        catch { return false; }
    }

    private static void Add(List<(PlayerStats, int)> counts, Component part, int pulled)
    {
        var ps = part.GetComponent<PlayerStats>();
        if (ps != null && ps.Player_Id != 0) counts.Add((ps, pulled));
    }

    private static bool Recorded(PlayerStats ps, out int pulled)
    {
        pulled = 0;
        if (ps == null || ps.Player_Id == 0 || !_seen.TryGetValue(ps.Player_Id, out var r)) return false;
        if (r.Stretch != _stretch || Time.unscaledTime - r.At > RecordAge) return false;
        pulled = r.Pulled;
        return true;
    }

    // ------------------------------------------------------------------ the rebuild

    /// <summary>
    /// The player whose revolver-numbers command is being handled, or null. Only that command is
    /// a player's word: the game's own rebuild from the old host's snapshot can run while handling
    /// some other player's command, for a seat nobody owns yet, and is never refused.
    /// </summary>
    private static NetworkConnectionToClient _numbersFrom;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerObjectController), nameof(PlayerObjectController.InvokeUserCode_SetRevolverMigrationData__GameObject__Int32__Int32))]
    private static void NumbersIn(NetworkConnectionToClient senderConnection, out NetworkConnectionToClient __state)
    {
        __state = _numbersFrom;
        _numbersFrom = senderConnection;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(PlayerObjectController), nameof(PlayerObjectController.InvokeUserCode_SetRevolverMigrationData__GameObject__Int32__Int32))]
    private static Exception NumbersDone(Exception __exception, NetworkConnectionToClient __state)
    {
        _numbersFrom = __state;
        return __exception;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.ApplyMigrationRevolverState))]
    private static bool Deck(DeckGameplay __instance, ref int current, ref int bullet) =>
        Rebuild(__instance, __instance.currentRevolver, HouseRules.Chambers, ref current, ref bullet, "Liar's Deck");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.ServerApplyMigrationRevolverState))]
    private static bool ChaosDeck(ChaosDeckGameplay __instance, ref int current, ref int bullet) =>
        Rebuild(__instance, __instance.currentrevoler, HouseRules.Chambers, ref current, ref bullet, "Chaos deck");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosGamePlay), nameof(ChaosGamePlay.ServerApplyMigrationRevolverState))]
    private static bool Chaos(ChaosGamePlay __instance, ref int current, ref int bullet) =>
        Rebuild(__instance, __instance.currentrevoler, 6, ref current, ref bullet, "Liar's Chaos");

    /// <summary>Returns false only to refuse a player's copy restoring a seat it may not restore now.</summary>
    private static bool Rebuild(NetworkBehaviour part, int shown, int chambers, ref int current, ref int bullet, string mode)
    {
        try
        {
            if (!NetworkServer.active) return true;
            if (!Fair.Hosting)
            {
                // Left to the game, unless the chamber handed over is not a real one: then it is
                // drawn here too, so no revolver is ever rebuilt with an impossible chamber.
                var nm = Fair.Net;
                if (RankedGuard.Near || nm == null || nm.Matchmaking || nm.isVelvetRoom) return true;
                int n0 = Math.Max(chambers, 1);
                int at = Mathf.Clamp(Math.Max(Math.Max(shown, current), 0), 0, n0 - 1);
                if (bullet >= at && bullet < n0)
                {
                    Plugin.Log.LogInfo($"{Fair.Tag} a {mode} revolver migration left to the game: {Fair.Why}");
                    return true;
                }
                current = at;
                bullet = Draw(at, n0);
                Plugin.Log.LogInfo($"{Fair.Tag} a {mode} revolver rebuilt with its chamber drawn again ({Fair.Why})");
                return true;
            }

            var sender = _numbersFrom;
            bool remote = Fair.IsRemote(sender);
            if (remote)
            {
                var owner = part.connectionToClient;
                if (owner == null || owner.connectionId != sender.connectionId)
                {
                    Fair.Refused($"connection {sender.connectionId}: {mode} revolver numbers for a seat that is not its own");
                    return false;
                }
                if (!HostCheckGuards.RestoreWindow)
                {
                    Fair.Refused($"connection {sender.connectionId}: {mode} revolver numbers outside the restore after a host migration");
                    return false;
                }
                if (!HostCheckGuards.FirstRestore("revolver", part))
                {
                    Fair.Refused($"connection {sender.connectionId}: {mode} revolver numbers a second time");
                    return false;
                }
            }

            var ps = part.GetComponent<PlayerStats>();
            int pulled = Math.Max(shown, 0);
            if (!remote) pulled = Math.Max(pulled, current);
            if (Recorded(ps, out int seen)) pulled = Math.Max(pulled, seen);
            int n = Math.Max(chambers, 1);
            pulled = Mathf.Clamp(pulled, 0, n - 1);

            current = pulled;
            bullet = Draw(pulled, n);

            // Never which chamber: the log would say when that seat's revolver fires.
            Plugin.Log.LogInfo($"{Fair.Tag} {mode} seat {(ps != null ? ps.Slot : -1)}'s revolver rebuilt by the host: " +
                               $"{pulled} of {n} pulled, the bullet drawn again among the {n - pulled} left");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"{Fair.Tag} a {mode} revolver could not be rebuilt: {e.Message}");
            return true;
        }
    }

    /// <summary>Evenly among the chambers not pulled yet, never behind the hammer.</summary>
    internal static int Draw(int pulled, int chambers) => pulled + UnityEngine.Random.Range(0, Math.Max(chambers - pulled, 1));

    // ------------------------------------------------------------------ developer tests

    /// <summary>
    /// "fair migrate [conn]": the host runs its own migration apply for that player's seat with a
    /// restore that does not fit its record, then with one for another seat. The first must be
    /// replaced, keeping the count and a chamber among the unpulled ones; the second must change
    /// nothing.
    /// "fair migrate draw": the even draw, offline, for every cylinder size and count.
    /// </summary>
    internal static void Register()
    {
        Fair.OnTick("revolver record", Look);
        Fair.OnDrive("migrate", rest =>
        {
            string[] words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0 && words[0] == "draw") { DrawTest(); return; }

            var conn = Fair.TestConnection(words.Length > 0 ? words[0] : "");
            if (conn == null) { Fair.Result("migrate", false, "no remote player connected"); return; }

            DeckGameplay mine = null, other = null;
            foreach (var d in UnityEngine.Object.FindObjectsOfType<DeckGameplay>())
            {
                var o = d.connectionToClient;
                if (o != null && o.connectionId == conn.connectionId) mine ??= d;
                else other ??= d;
            }
            if (mine == null) { Fair.Result("migrate", false, "that connection has no Liar's Deck seat at this table"); return; }

            int n = HouseRules.Chambers;
            int before = mine.currentRevolver;
            AsNumbersFrom(conn, () => mine.ApplyMigrationRevolverState(0, -1));
            int after = mine.currentRevolver, loaded = mine.revolverBullet;
            Fair.Result("migrate", after >= before && loaded >= after && loaded < n,
                        $"own seat: count kept={after >= before}, bullet ahead of the hammer inside the cylinder={loaded >= after && loaded < n}");

            if (other == null) { Fair.Result("migrate", true, "no second seat to try a foreign seat on (skipped)"); return; }
            int oc = other.currentRevolver, ob = other.revolverBullet;
            AsNumbersFrom(conn, () => other.ApplyMigrationRevolverState(0, 0));
            Fair.Result("migrate", other.currentRevolver == oc && other.revolverBullet == ob,
                        "another seat's numbers from that player: refused, nothing changed");
        });
    }

    /// <summary>Developer test: run the host's own apply as if that player's numbers command were being handled.</summary>
    private static void AsNumbersFrom(NetworkConnectionToClient conn, Action body)
    {
        var was = _numbersFrom;
        _numbersFrom = conn;
        try { Fair.AsSentBy(conn, body); }
        finally { _numbersFrom = was; }
    }

    private static void DrawTest()
    {
        bool ok = true;
        foreach (int n in new[] { 6, 4, 3 })
            for (int pulled = 0; pulled < n; pulled++)
            {
                var hits = new int[n];
                for (int i = 0; i < 2000; i++)
                {
                    int b = Draw(pulled, n);
                    if (b < pulled || b >= n) { ok = false; continue; }
                    hits[b]++;
                }
                for (int c = pulled; c < n; c++) if (hits[c] == 0) ok = false;
            }
        Fair.Result("migrate", ok, "the bullet is always drawn among the unpulled chambers, and every one of them comes up");
    }
}
