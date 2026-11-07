using System;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Each seat's loaded chamber stays with the host. At a table where hidden hands are on, the
/// table is sent a stand-in for it (<see cref="HostRevolver.StandIn"/>); the host's own copy keeps
/// its record for everything it decides. The pull count is untouched: it is the public "(n|6)"
/// every seat shows. The stand-in is chosen so that every unchanged copy shows each pull exactly
/// as the host settles it.
///
/// Off, nothing here changes what is sent. Either way the host keeps its own record, which settles
/// every pull (<see cref="HostRevolver.Judge"/>).
/// </summary>
[FairPatch("each seat's loaded chamber stays with the host")]
[HarmonyPatch]
internal static class HideTheBullet
{
    private const int NoSwap = int.MinValue;
    private static bool _peeking;

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.SerializeSyncVars))]
    private static void DeckOut(DeckGameplay __instance, bool forceAll, out int __state) =>
        __state = Before(__instance, false, forceAll);

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.SerializeSyncVars))]
    private static Exception DeckBack(DeckGameplay __instance, int __state, Exception __exception)
    {
        After(__instance, false, __state);
        return __exception;
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.SerializeSyncVars))]
    private static void ChaosOut(ChaosDeckGameplay __instance, bool forceAll, out int __state) =>
        __state = Before(__instance, true, forceAll);

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.SerializeSyncVars))]
    private static Exception ChaosBack(ChaosDeckGameplay __instance, int __state, Exception __exception)
    {
        After(__instance, true, __state);
        return __exception;
    }

    /// <summary>The host loads a revolver again (a new match, a reload, a migration): the stand-in goes out fresh.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeckGameplay), "set_NetworkrevolverBullet")]
    private static void DeckLoaded(DeckGameplay __instance) => Loaded(__instance, false);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ChaosDeckGameplay), "set_Networkrevolverbulllet")]
    private static void ChaosLoaded(ChaosDeckGameplay __instance) => Loaded(__instance, true);

    private static int Before(NetworkBehaviour part, bool chaos, bool forceAll)
    {
        try
        {
            if (!NetworkServer.active || !Fair.Hosting) return NoSwap;
            var gun = HostRevolver.For(part, chaos);
            if (gun == null) return NoSwap;

            int truth = gun.Bullet;
            int current = gun.Current;
            int wire = Fair.HideHands ? HostRevolver.StandIn(gun) : truth;
            gun.LastWire = wire;

            if (!_peeking)
            {
                ulong bits = part.syncVarDirtyBits;
                bool sendsCurrent = forceAll || (bits & gun.CurrentBit) != 0;
                bool sendsBullet = forceAll || (bits & gun.BulletBit) != 0;
                if (sendsCurrent) gun.WireCurrent = current;
                if (sendsBullet) gun.WireBullet = wire;
                if (sendsCurrent || sendsBullet) gun.Sent();
            }

            if (wire == truth) return NoSwap;
            gun.PutBullet(wire);   // last: nothing below can leave the stand-in in place
            return truth;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"{Fair.Tag} a revolver could not be prepared to send: {e.Message}");
            return NoSwap;
        }
    }

    private static void After(NetworkBehaviour part, bool chaos, int restore)
    {
        if (restore == NoSwap) return;
        try { HostRevolver.Find(part)?.PutBullet(restore); }
        catch (Exception e) { Plugin.Log.LogError($"{Fair.Tag} a revolver could not be put back after sending: {e.Message}"); }
    }

    private static void Loaded(NetworkBehaviour part, bool chaos)
    {
        try
        {
            if (!NetworkServer.active || !Fair.Hosting) return;
            var gun = HostRevolver.For(part, chaos);
            if (gun == null) return;
            if (!gun.HasOpenFiringPull()) gun.Showing = false;
            if (Fair.HideHands) part.SetSyncVarDirtyBit(gun.BulletBit);
        }
        catch (Exception e) { Plugin.Log.LogError($"{Fair.Tag} a reloaded revolver could not be noted: {e.Message}"); }
    }

    // ------------------------------------------------------------------ developer test

    /// <summary>
    /// "fair bulletpeek": the host writes each revolver seat's values as it would send them and
    /// checks that each is written as intended and that the host still holds its own record. The
    /// log never says which chamber is loaded.
    /// </summary>
    internal static void Register()
    {
        Fair.OnDrive("bulletpeek", _ =>
        {
            if (!Fair.Hosting) { Fair.Result("bulletpeek", false, $"not hosting ({Fair.Why})"); return; }
            int seen = 0;
            foreach (var deck in UnityEngine.Object.FindObjectsOfType<DeckGameplay>()) { Peek(deck, false); seen++; }
            foreach (var chaos in UnityEngine.Object.FindObjectsOfType<ChaosDeckGameplay>()) { Peek(chaos, true); seen++; }
            if (seen == 0) Fair.Result("bulletpeek", false, "no Liar's Deck or Chaos deck seat at this table");
        });
    }

    private static void Peek(NetworkBehaviour part, bool chaos)
    {
        var gun = HostRevolver.For(part, chaos);
        if (gun == null) return;
        int truth = gun.Bullet;
        bool hide = Fair.HideHands;
        int want = hide ? HostRevolver.StandIn(gun) : truth;

        var writer = new NetworkWriter();
        _peeking = true;
        try { part.SerializeSyncVars(writer, true); }
        finally { _peeking = false; }

        bool kept = gun.Bullet == truth;
        bool wrote = gun.LastWire == want;
        bool tail = true;
        if (!chaos)
        {
            // The value as written, read back from the output.
            var bytes = writer.ToArray();
            int n = bytes.Length;
            if (n >= 4)
            {
                int last = bytes[n - 4] | (bytes[n - 3] << 8) | (bytes[n - 2] << 16) | (bytes[n - 1] << 24);
                tail = last == want;
            }
        }
        Fair.Result("bulletpeek", kept && wrote && tail,
                    $"{gun.Mode} seat {gun.Seat}: hidden hands {(hide ? "on" : "off")}, " +
                    $"written as intended={wrote && tail}, host still holds its own={kept}");
    }
}
