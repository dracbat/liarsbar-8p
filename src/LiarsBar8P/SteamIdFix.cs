using System;
using System.Globalization;
using HarmonyLib;
using Mirror;

namespace LiarsBar8P;

/// <summary>
/// Each joiner's Steam id, put right on the host as their player is made.
///
/// The game fills a new player's id from the Steam lobby's member list, taking the member at the
/// position equal to the number of players already made. That only holds while everyone arrives
/// in the order Steam lists them and nobody but members has a player. Two people arriving out of
/// order, or a bot in the room (a player with no Steam membership), and the id is somebody else's
/// or nobody's. Blocking a player goes by that id, and so does the mod's guard against one person
/// being seated twice, so it has to be the right one.
///
/// The transport knows who is on the other end of each connection, and that is the id used here,
/// as the host's check for players without the mod already does. A connection that is not a Steam
/// one (the loopback harness, the host's own) is left as the game made it. Never in ranked, where
/// a player's copy is never the server anyway.
/// </summary>
[HarmonyPatch]
internal static class SteamIdFix
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CustomNetworkManager), nameof(CustomNetworkManager.OnServerAddPlayer))]
    private static void Made(CustomNetworkManager __instance, NetworkConnectionToClient conn)
    {
        try
        {
            if (__instance == null || conn == null || !NetworkServer.active || __instance.Matchmaking || RankedGuard.Near) return;
            if (!ulong.TryParse(conn.address, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) || id == 0) return;

            PlayerObjectController made = null;
            var identity = conn.identity;
            if (identity != null) made = identity.GetComponent<PlayerObjectController>();
            if (made == null && __instance.GamePlayers != null)
                foreach (var p in __instance.GamePlayers)
                    if (p != null && p.ConnectionID == conn.connectionId) { made = p; break; }

            if (made == null || made.PlayerSteamID == id) return;

            made.NetworkPlayerSteamID = id;
            Plugin.Log.LogInfo($"[steamid] connection {conn.connectionId} was given another member's id by the game - set from the connection");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[steamid] could not check a joiner's id: {e.Message}"); }
    }
}
