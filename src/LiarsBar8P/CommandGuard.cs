using System;
using HarmonyLib;
using Mirror;

namespace LiarsBar8P;

/// <summary>
/// Mirror disconnects any client whose Command throws on the server
/// (NetworkServer.exceptionsDisconnect). The game's own
/// <c>PlayerObjectController.CmdSetPlayer</c> null-references for players beyond the
/// fourth, so the fifth player was admitted and then immediately dropped:
///
///   Disconnecting connection: connection(4) because handling a message of type
///   Mirror.CommandMessage caused an Exception ... at PlayerObjectController.CmdSetPlayer
///
/// These finalizers swallow that exception so the connection survives, and log the
/// lobby list sizes at the moment of failure to identify what is still sized for four.
/// Swallowing is deliberate: a partially-initialised player is recoverable, a
/// disconnected one is not.
/// </summary>
internal static class CommandGuard
{
    private static int _setPlayerFailures;

    private static string Sizes()
    {
        try
        {
            var lc = LobbyController.Instance;
            if (lc == null) return "LobbyController.Instance == NULL";

            string n(Il2CppSystem.Collections.Generic.List<UnityEngine.GameObject> l)
                => l == null ? "null" : l.Count.ToString();

            return $"SpawnSlots={(lc.SpawnSlots == null ? "null" : lc.SpawnSlots.Count.ToString())} " +
                   $"Bars={(lc.Bars == null ? "null" : lc.Bars.Count.ToString())} " +
                   $"Sayilar={(lc.Sayilar == null ? "null" : lc.Sayilar.Count.ToString())} " +
                   $"SwitchBars={(lc.SwitchBars == null ? "null" : lc.SwitchBars.Count.ToString())} " +
                   $"lines={(lc.lines == null ? "null" : lc.lines.Count.ToString())}";
        }
        catch (Exception e) { return $"<size probe failed: {e.Message}>"; }
    }

    private static string Who(PlayerObjectController p)
    {
        try
        {
            if (p == null) return "player == null";
            return $"connId={p.ConnectionID} idNo={p.PlayerIdNumber} slot={p.InGameSlot} " +
                   $"name='{p.PlayerName}' skin={p.PlayerSkin}";
        }
        catch (Exception e) { return $"<player probe failed: {e.Message}>"; }
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(PlayerObjectController), nameof(PlayerObjectController.CmdSetPlayer))]
    private static Exception CmdSetPlayer_Finalizer(Exception __exception, PlayerObjectController __instance)
    {
        if (__exception == null) return null;

        _setPlayerFailures++;
        Plugin.Log.LogWarning(
            $"[guard] CmdSetPlayer threw ({_setPlayerFailures}) - swallowed so the player is not dropped");
        Plugin.Log.LogWarning($"[guard]   player : {Who(__instance)}");
        Plugin.Log.LogWarning($"[guard]   lobby  : {Sizes()}");
        Plugin.Log.LogWarning($"[guard]   server : connections={NetworkServer.connections?.Count} " +
                              $"max={NetworkServer.maxConnections}");
        Plugin.Log.LogWarning($"[guard]   detail : {__exception.GetType().Name}: {__exception.Message}");

        return null; // swallowed: Mirror will not disconnect this connection
    }

    /// <summary>Same treatment for the cleanup path, which also null-references.</summary>
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(PlayerObjectController), nameof(PlayerObjectController.OnStopClient))]
    private static Exception OnStopClient_Finalizer(Exception __exception)
    {
        if (__exception == null) return null;
        Plugin.Log.LogWarning($"[guard] OnStopClient threw - swallowed ({__exception.Message})");
        return null;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(PlayerObjectController), nameof(PlayerObjectController.CmdSetPlayerName))]
    private static Exception CmdSetPlayerName_Finalizer(Exception __exception)
    {
        if (__exception == null) return null;
        Plugin.Log.LogWarning($"[guard] CmdSetPlayerName threw - swallowed ({__exception.Message})");
        return null;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(PlayerObjectController), nameof(PlayerObjectController.SetMyData))]
    private static Exception SetMyData_Finalizer(Exception __exception)
    {
        if (__exception == null) return null;
        Plugin.Log.LogWarning($"[guard] SetMyData threw - swallowed ({__exception.Message})");
        return null;
    }
}
