using System;
using HarmonyLib;
using Mirror;

namespace LiarsBar8P;

/// <summary>
/// Bots and the people who come to their table, on the host.
///
/// <b>Kick.</b> The game's own Kick on its four podiums sends a person away through their
/// connection, and a bot has none, so the call went nowhere and the bot stayed. On a bot's podium
/// it now takes the bot away, the way REMOVE BOT does. The mod's Kick on podiums five to eight does
/// the same (<see cref="BlockButtons"/>).
///
/// <b>A person at a full table.</b> Bots are not connections, so the game lets a person in however
/// many bots sit there, and with every podium taken it has nowhere to put them. Just before the
/// game makes their player, the newest bot gives up its seat. Should that part fail to go on,
/// bots leave the last seat free instead (<see cref="ArrivalOn"/>).
///
/// <b>The next host.</b> As a match starts the game picks the first other player in the room to take
/// over if the host leaves, and tells that player's copy to get ready for it. A bot has no copy to
/// tell, so with a bot first in the room nobody was ready. The choice now passes over bots to the
/// first person, and with nobody but bots there is nobody to choose: a host leaving a table of bots
/// may end the match.
///
/// The lobby list and Quick Join need nothing here: they count a lobby's Steam members, and bots are
/// never members, so a row counts the people at the table. The count the game itself writes on the
/// lobby includes bots, but it writes it from its lobby's every frame, and the lists read it only
/// when Steam has no count of its own.
///
/// Each part is applied apart, and each does nothing at a table with no bots.
/// </summary>
internal static class BotLobby
{
    /// <summary>A bot gives its seat to a person joining a full table. Set at load from <see cref="Arrival"/> going on.</summary>
    internal static bool ArrivalOn { get; set; }

    private static CustomNetworkManager Net
    {
        get
        {
            try
            {
                var nm = NetworkManager.singleton;
                return nm != null ? nm.TryCast<CustomNetworkManager>() : null;
            }
            catch { return null; }
        }
    }

    [HarmonyPatch]
    internal static class Kick
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(LobbySlot), nameof(LobbySlot.Kick))]
        private static bool Pressed(LobbySlot __instance)
        {
            try
            {
                if (__instance == null || !NetworkServer.active) return true;
                var bot = BotManager.On(__instance.gameObject.name);
                if (bot == null) return true;

                BotManager.Remove(bot, "the host's Kick");
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[bot] the game's Kick on a podium failed: {e.Message}");
                return true;
            }
        }
    }

    [HarmonyPatch]
    internal static class Arrival
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CustomNetworkManager), nameof(CustomNetworkManager.OnServerAddPlayer))]
        private static void MakeRoom(CustomNetworkManager __instance, NetworkConnectionToClient conn)
        {
            try
            {
                if (conn == null || !BotManager.GiveWay(__instance)) return;
                Plugin.Log.LogInfo($"[bot] the table was full: a bot gave its seat to connection {conn.connectionId}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[bot] could not make room for a joiner: {e.Message}"); }
        }
    }

    [HarmonyPatch]
    internal static class NextHost
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerObjectController), nameof(PlayerObjectController.SetMeAsHost))]
        private static bool NotABot(PlayerObjectController __instance)
        {
            // Never hand the server to a bot, or to a player this copy removed for cheating.
            if (!NetworkServer.active || !(BotManager.IsBot(__instance) || Removed(__instance))) return true;

            try
            {
                var nm = Net;
                PlayerObjectController next = null;
                if (nm != null && nm.GamePlayers != null)
                    foreach (var p in nm.GamePlayers)
                        if (p != null && !p.isLocalPlayer && !BotManager.IsBot(p) && !Removed(p)) { next = p; break; }

                if (next == null)
                {
                    Plugin.Log.LogInfo("[bot] nobody to take over as host: everybody else here is a bot");
                    return false;
                }

                Plugin.Log.LogInfo($"[bot] the next host is connection {next.ConnectionID}, not a bot or a removed player");
                next.SetMeAsHost();
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[bot] could not pick the next host past a bot: {e.Message}"); }
            return false;
        }

        private static bool Removed(PlayerObjectController p)
        {
            try { return p != null && BlockList.SessionHolds(p.PlayerSteamID); }
            catch { return false; }
        }
    }
}
