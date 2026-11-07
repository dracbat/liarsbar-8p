using System;
using HarmonyLib;
using Mirror;

namespace LiarsBar8P;

/// <summary>
/// The Chaos deck. The host settles a wrong caller's pull from its own record, as in Liar's Deck
/// (see <see cref="HostRevolver"/>). Every shot fires only on the host's own countdown.
/// </summary>
[FairPatch("Chaos deck: the host settles each self-shot and keeps the fire to its countdown")]
[HarmonyPatch]
internal static class HostDecidesChaos
{
    /// <summary>A wrong caller's pull begins: the host notes its own outcome before the seat is told.</summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.StartRevolverProcesses))]
    private static void Pulling(ChaosDeckGameplay __instance, NetworkConnectionToClient cn)
    {
        try { HostRevolver.Pulling(__instance, true, cn); }
        catch (Exception e) { Plugin.Log.LogError($"{Fair.Tag} a Chaos deck pull could not be noted: {e.Message}"); }
    }

    /// <summary>A message about this seat's pull: the host's record decides.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.InvokeUserCode_CommandBeDead))]
    private static bool Fired(NetworkBehaviour obj, NetworkConnectionToClient senderConnection) =>
        HostRevolver.Report(obj, senderConnection, true, saysFired: true);

    /// <summary>A message about this seat's pull: the host's record decides.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.InvokeUserCode_CmdRevolverSurvived))]
    private static bool Empty(NetworkBehaviour obj, NetworkConnectionToClient senderConnection) =>
        HostRevolver.Report(obj, senderConnection, true, saysFired: false);

    /// <summary>A seat goes out by the game's own path: shots due at the same moment land with it.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosDeckGameplay), "ServerBeDeadFromRevolver")]
    private static void GoingOut(ChaosDeckGameplay __instance) => HostRevolver.GoingOut(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.UserCode_CommandBeDead))]
    private static void Out(ChaosDeckGameplay __instance) => HostRevolver.GoingOut(__instance);

    /// <summary>
    /// A fire command from a player's copy is dropped: every shot fires on the host's countdown.
    /// Refused, not removed: a dropped command changes nothing an honest player does.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.InvokeUserCode_CmdResolveFire__Int32))]
    private static bool Fire(NetworkConnectionToClient senderConnection)
    {
        try
        {
            if (!Allowed(senderConnection))
            {
                Fair.Refused($"connection {senderConnection.connectionId}: a Chaos deck fire command; the countdown fires the shot");
                return false;
            }
        }
        catch (Exception e) { Plugin.Log.LogError($"{Fair.Tag} a Chaos deck fire command could not be checked: {e.Message}"); }
        return true;
    }

    /// <summary>A shot command from a player's copy is dropped too: the host fires every shot itself.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.InvokeUserCode_HitTargetCmd__Int32))]
    private static bool Shot(NetworkConnectionToClient senderConnection)
    {
        try
        {
            if (!Allowed(senderConnection))
            {
                Fair.Refused($"connection {senderConnection.connectionId}: a Chaos deck shot command; the host fires every shot");
                return false;
            }
        }
        catch (Exception e) { Plugin.Log.LogError($"{Fair.Tag} a Chaos deck shot command could not be checked: {e.Message}"); }
        return true;
    }

    internal static bool Allowed(NetworkConnectionToClient sender) => !(Fair.Hosting && Fair.IsRemote(sender));

    internal static void Register()
    {
        // "fair chaosfire [conn]": the host checks a fire command as if that seat had sent one.
        Fair.OnDrive("chaosfire", rest =>
        {
            var conn = Fair.TestConnection(rest);
            if (conn == null) { Fair.Result("chaosfire", false, "no remote player connected"); return; }
            bool allowed = true;
            Fair.AsSentBy(conn, () => allowed = Allowed(Fair.Sender));
            Fair.Result("chaosfire", !allowed, $"connection {conn.connectionId}: a fire command from a seat is {(allowed ? "let through" : "refused")}");
        });
    }
}

/// <summary>
/// Liar's Chaos: a shot counts only from the seat the host has just asked to fire, once for each
/// time it asked. Anything else is refused. The host's own seat and bots keep the game's own path.
/// </summary>
[FairPatch("Liar's Chaos: a shot only from the seat the host asked to fire")]
[HarmonyPatch]
internal static class HostAsksChaosFire
{
    /// <summary>A seat's copy answers a fire at once; this is many times that.</summary>
    private const float Window = 20f;

    private static readonly System.Collections.Generic.Dictionary<uint, float> _asked = new();

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(ChaosGamePlay), nameof(ChaosGamePlay.FnishMasterProcesses))]
    private static void Asked(ChaosGamePlay __instance, NetworkConnectionToClient cn)
    {
        try
        {
            if (!NetworkServer.active || __instance == null) return;
            if (!Fair.Hosting) { _asked.Clear(); return; }
            if (Fair.IsRemote(cn)) _asked[__instance.netId] = UnityEngine.Time.unscaledTime;
        }
        catch (Exception e) { Plugin.Log.LogError($"{Fair.Tag} a Liar's Chaos fire could not be noted: {e.Message}"); }
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(ChaosGamePlay), nameof(ChaosGamePlay.InvokeUserCode_HitTargetCmd))]
    private static bool Shot(NetworkBehaviour obj, NetworkConnectionToClient senderConnection)
    {
        try
        {
            if (!Fair.Hosting || !Fair.IsRemote(senderConnection) || obj == null) return true;
            uint id = obj.netId;
            var owner = obj.connectionToClient;
            bool own = owner != null && owner.connectionId == senderConnection.connectionId;
            bool asked = _asked.TryGetValue(id, out float at) && UnityEngine.Time.unscaledTime - at < Window;
            if (own && asked)
            {
                _asked.Remove(id);
                return true;
            }
            Fair.Refused($"connection {senderConnection.connectionId}: a Liar's Chaos shot the host did not ask that seat for");
            return false;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"{Fair.Tag} a Liar's Chaos shot could not be checked: {e.Message}");
            return true;
        }
    }
}
