using HarmonyLib;
using Mirror;
using Steamworks;

namespace LiarsBar8P;

/// <summary>
/// Mirror keeps two separate connection limits: the serialized
/// <c>NetworkManager.maxConnections</c>, and the static
/// <c>NetworkServer.maxConnections</c> that <c>NetworkServer.Listen(int)</c> installs
/// and that is actually consulted when a client connects.
///
/// Raising only the first lets Steam admit a fifth player to the lobby while Mirror
/// refuses the connection underneath. Both are raised here, and the server-side limit
/// is re-asserted on every connection in case anything resets it.
/// </summary>
internal static class JoinFix
{
    private static int Target => Plugin.MaxPlayers.Value;

    /// <summary>The authoritative cap: whatever is passed to Listen becomes the limit.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(NetworkServer), nameof(NetworkServer.Listen))]
    private static void Listen_Prefix(ref int maxConns)
    {
        if (maxConns < Target)
        {
            Plugin.Log.LogInfo($"[join] NetworkServer.Listen({maxConns}) -> {Target}");
            maxConns = Target;
        }
    }

    private static void Reassert(string where)
    {
        try
        {
            if (NetworkServer.maxConnections < Target)
            {
                Plugin.Log.LogWarning(
                    $"[join] NetworkServer.maxConnections was {NetworkServer.maxConnections} at {where} -> {Target}");
                NetworkServer.maxConnections = Target;
            }
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogError($"[join] reassert failed: {e.Message}");
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CustomNetworkManager), nameof(CustomNetworkManager.OnServerConnect))]
    private static void OnServerConnect_Prefix(CustomNetworkManager __instance, NetworkConnectionToClient conn)
    {
        Reassert("OnServerConnect");
        try
        {
            Plugin.Log.LogInfo(
                $"[join] incoming connId={conn?.connectionId} | " +
                $"server.max={NetworkServer.maxConnections} manager.max={__instance.maxConnections} | " +
                $"connections={NetworkServer.connections?.Count} numPlayers={__instance.numPlayers} | " +
                $"GamePlayers={(__instance.GamePlayers != null ? __instance.GamePlayers.Count : -1)}");
        }
        catch (System.Exception e) { Plugin.Log.LogError($"[join] log failed: {e.Message}"); }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CustomNetworkManager), nameof(CustomNetworkManager.OnServerConnect))]
    private static void OnServerConnect_Postfix(CustomNetworkManager __instance, NetworkConnectionToClient conn)
    {
        try
        {
            bool alive = conn != null && NetworkServer.connections != null
                         && NetworkServer.connections.ContainsKey(conn.connectionId);
            Plugin.Log.LogInfo($"[join] after OnServerConnect connId={conn?.connectionId} stillConnected={alive}");
        }
        catch { }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CustomNetworkManager), nameof(CustomNetworkManager.OnServerAddPlayer))]
    private static void OnServerAddPlayer_Postfix(CustomNetworkManager __instance, NetworkConnectionToClient conn)
    {
        try
        {
            Plugin.Log.LogInfo(
                $"[join] player added connId={conn?.connectionId} " +
                $"GamePlayers={(__instance.GamePlayers != null ? __instance.GamePlayers.Count : -1)} " +
                $"numPlayers={__instance.numPlayers}");
        }
        catch { }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CustomNetworkManager), nameof(CustomNetworkManager.OnServerDisconnect))]
    private static void OnServerDisconnect_Postfix(CustomNetworkManager __instance, NetworkConnectionToClient conn)
    {
        try
        {
            Plugin.Log.LogWarning(
                $"[join] DISCONNECT connId={conn?.connectionId} " +
                $"remaining={NetworkServer.connections?.Count} " +
                $"GamePlayers={(__instance.GamePlayers != null ? __instance.GamePlayers.Count : -1)}");
        }
        catch { }
    }

    /// <summary>
    /// The game keeps its own join lock. Log it, and clear it while the lobby still has
    /// room, since a stale lock would refuse players the caps now allow.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.UpdatePlayerCountInSteam))]
    private static void UpdatePlayerCount_Postfix()
    {
        try
        {
            var sl = SteamLobby.Instance;
            if (sl == null) return;

            int members = -1, limit = -1;
            if (sl.CurrentLobbyID != 0)
            {
                var id = new CSteamID(sl.CurrentLobbyID);
                members = SteamMatchmaking.GetNumLobbyMembers(id);
                limit = SteamMatchmaking.GetLobbyMemberLimit(id);
                if (limit < Target)
                {
                    SteamMatchmaking.SetLobbyMemberLimit(id, Target);
                    Plugin.Log.LogWarning($"[join] lobby member limit was {limit} -> {Target}");
                }
            }

            Plugin.Log.LogInfo(
                $"[join] steam members={members}/{limit} JoinLocked={sl.JoinLocked} " +
                $"server.max={NetworkServer.maxConnections}");

            if (sl.JoinLocked && members >= 0 && members < Target)
            {
                sl.JoinLocked = false;
                Plugin.Log.LogWarning("[join] JoinLocked was true with room to spare - cleared");
            }
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogError($"[join] steam state check failed: {e.Message}");
        }
    }
}
