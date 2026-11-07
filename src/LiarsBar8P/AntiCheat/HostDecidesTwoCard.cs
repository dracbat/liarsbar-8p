using System;
using System.Collections.Generic;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Two-Card Poker: the host decides each shotgun pull from its own record.
///
/// The host loads every seat's chambers itself and counts each fire it asks a seat for, so it
/// knows at each one whether the chamber is loaded. The host lands each loaded shot at the usual
/// moment at the seat aimed at then, as the game does, and on its own by a deadline at the seat
/// aimed at when it fired. Each hand's value is the host's own count (<see cref="HostCountsTwoCard"/>).
///
/// Nothing here removes anyone: anything out of place is something lag or a host migration could
/// cause, so the host simply acts on its own record. The host's own seat keeps the game's own path.
/// </summary>
[FairPatch("Two-Card Poker: the host decides each shotgun pull")]
[HarmonyPatch]
internal static class HostDecidesTwoCard
{
    /// <summary>The host lands a loaded shot by itself this long after the fire, whatever arrives.</summary>
    private const float Deadline = 8f;

    private sealed class Shot
    {
        internal float At;
        internal int Aim;
        internal bool TestDropped;
    }

    private sealed class Gun
    {
        internal PokerGamePlay Poker;
        internal int Fired;
        internal readonly List<Shot> Loaded = new();
    }

    private static readonly Dictionary<IntPtr, Gun> _guns = new();
    private static readonly HashSet<int> _drop = new();

    private static Gun For(PokerGamePlay poker)
    {
        if (!_guns.TryGetValue(poker.Pointer, out var gun))
            _guns[poker.Pointer] = gun = new Gun { Poker = poker };
        return gun;
    }

    /// <summary>The host asks a seat to fire: count it, and note a loaded chamber.</summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(PokerGamePlay), nameof(PokerGamePlay.TryFire))]
    private static void Firing(PokerGamePlay __instance, NetworkConnectionToClient cn)
    {
        try
        {
            if (!NetworkServer.active || __instance == null) return;
            // Counted whenever this copy serves the table, so the count always matches the shooter's.
            var gun = For(__instance);
            int chamber = gun.Fired++;
            if (!Fair.Hosting || !Fair.IsRemote(cn)) return;

            if (__instance.fullbullets.Contains(chamber))
                gun.Loaded.Add(new Shot { At = Time.unscaledTime, Aim = __instance.Aim });
        }
        catch (Exception e) { Plugin.Log.LogError($"{Fair.Tag} a Two-Card Poker pull could not be noted: {e.Message}"); }
    }

    /// <summary>A message about a fire: a loaded chamber lands at the seat aimed at; anything else is refused.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(PokerGamePlay), nameof(PokerGamePlay.InvokeUserCode_HitTargetCmd))]
    private static bool Hit(NetworkBehaviour obj, NetworkConnectionToClient senderConnection)
    {
        try
        {
            if (!Fair.Hosting || !Fair.IsRemote(senderConnection) || obj == null) return true;
            var owner = obj.connectionToClient;
            if (owner == null || owner.connectionId != senderConnection.connectionId) return true;
            var poker = obj.TryCast<PokerGamePlay>();
            if (poker == null) return true;
            int id = senderConnection.connectionId;

            var gun = For(poker);
            var shot = gun.Loaded.Count > 0 ? gun.Loaded[0] : null;
            if (Dev.Enabled && _drop.Remove(id))
            {
                if (shot != null) shot.TestDropped = true;
                Plugin.Log.LogInfo($"{Fair.Tag} test: connection {id}'s Two-Card Poker hit word dropped by the host; its deadline lands it");
                return false;
            }
            if (shot == null)
            {
                Fair.Refused($"connection {id}: a Two-Card Poker hit with no loaded chamber waiting");
                return false;
            }
            gun.Loaded.RemoveAt(0);
            Land(gun, shot, "at the usual moment", poker.Aim);   // the game's own handler, at the aim it holds now
            return false;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"{Fair.Tag} a Two-Card Poker hit could not be checked: {e.Message}");
            return true;
        }
    }

    /// <summary>
    /// Lands a loaded chamber at the seat aimed at: the given aim, or the aim the host recorded
    /// when it fired when the given one is not a seat still in.
    /// </summary>
    private static bool Land(Gun gun, Shot shot, string why, int aim)
    {
        var poker = gun.Poker;
        if (poker == null) return false;
        var target = poker.GetAim(aim);
        if ((target == null || target.Dead) && aim != shot.Aim)
        {
            aim = shot.Aim;
            target = poker.GetAim(aim);
        }
        if (target == null || target.Dead) return false;

        int was = poker.Aim;
        poker.Aim = aim;   // the field only, for the game's own handler; put back straight after
        try { Fair.AsSentBy(null, poker.UserCode_HitTargetCmd); }
        finally { poker.Aim = was; }
        Plugin.Log.LogInfo($"{Fair.Tag} Two-Card Poker: a loaded chamber landed by the host at seat {target.Slot} ({why})");
        return target.Dead;
    }

    private static readonly List<IntPtr> _gone = new();

    private static void Tick()
    {
        if (_guns.Count == 0) return;
        if (!NetworkServer.active) { _guns.Clear(); return; }
        float now = Time.unscaledTime;
        _gone.Clear();
        foreach (var kv in _guns)
        {
            var gun = kv.Value;
            if (gun.Poker == null) { _gone.Add(kv.Key); continue; }
            if (!Fair.Hosting) { gun.Loaded.Clear(); continue; }
            while (gun.Loaded.Count > 0 && now - gun.Loaded[0].At >= Deadline)
            {
                var shot = gun.Loaded[0];
                gun.Loaded.RemoveAt(0);
                bool landed = Land(gun, shot, "on its own after the deadline", shot.Aim);
                if (shot.TestDropped) Fair.Result("twocard drop", landed, $"the host landed the loaded chamber on its own: {landed}");
            }
        }
        foreach (var key in _gone) _guns.Remove(key);
    }

    /// <summary>
    /// "fair twocard drop [conn]": the host throws away that seat's next hit word, as if it never
    /// came; the deadline must land the hit at the seat aimed at.
    /// "fair twocard stray [conn]": the host checks a hit word from that seat with nothing loaded
    /// waiting, as if it had just arrived; it must be refused.
    /// "fair twocard score" and "fair twocard value [conn]": the host's own hand count, see
    /// <see cref="HostCountsTwoCard"/>.
    /// </summary>
    internal static void Register()
    {
        Fair.OnTick("Two-Card Poker deadline", Tick);
        Fair.OnDrive("twocard", rest =>
        {
            string[] words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string what = words.Length > 0 ? words[0] : "";
            if (what == "score") { HostCountsTwoCard.TestScore(); return; }
            var conn = Fair.TestConnection(words.Length > 1 ? words[1] : "");
            if (conn == null) { Fair.Result($"twocard {what}", false, "no remote player connected"); return; }

            if (what == "drop")
            {
                _drop.Add(conn.connectionId);
                Plugin.Log.LogInfo($"{Fair.Tag} test: connection {conn.connectionId}'s next Two-Card Poker hit word will be dropped by the host");
                return;
            }
            if (what == "value") { HostCountsTwoCard.TestValue(conn); return; }
            if (what == "stray")
            {
                PokerGamePlay mine = null;
                foreach (var p in UnityEngine.Object.FindObjectsOfType<PokerGamePlay>())
                {
                    var o = p.connectionToClient;
                    if (o != null && o.connectionId == conn.connectionId) { mine = p; break; }
                }
                if (mine == null) { Fair.Result("twocard stray", false, "that connection has no Two-Card Poker seat"); return; }
                var gun = For(mine);
                if (gun.Loaded.Count > 0) { Fair.Result("twocard stray", false, "a loaded chamber is waiting for that seat; try between pulls"); return; }
                int refusedBefore = Fair.RefusedCount;
                bool let = Hit(mine, conn);
                Fair.Result("twocard stray", !let && Fair.RefusedCount > refusedBefore,
                            $"connection {conn.connectionId}: a hit with nothing loaded waiting was {(let ? "let through" : "refused")}");
                return;
            }
            Plugin.Log.LogWarning($"{Fair.Tag} test: twocard drop|stray|value [connection], twocard score");
        });
    }
}
