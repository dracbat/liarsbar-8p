using System;
using HarmonyLib;
using Mirror;

namespace LiarsBar8P;

/// <summary>
/// Liar's Deck Basic, Devil and Deck 2 (and every punishment that reaches the revolver the same
/// way): the host settles each player's revolver pull from its own record (see
/// <see cref="HostRevolver"/>), through the game's own server handlers, so every copy animates
/// exactly as before. Bots and the host's own seat keep the game's own path.
/// </summary>
[FairPatch("Liar's Deck: the host settles each revolver pull")]
[HarmonyPatch]
internal static class HostDecidesDeck
{
    internal static void Register() => HostRevolver.Register();

    /// <summary>A pull begins: the host notes its own outcome before the seat is told.</summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.StartRevolverProcesses))]
    private static void Pulling(DeckGameplay __instance, NetworkConnectionToClient cn)
    {
        try { HostRevolver.Pulling(__instance, false, cn); }
        catch (Exception e) { Plugin.Log.LogError($"{Fair.Tag} a Liar's Deck pull could not be noted: {e.Message}"); }
    }

    /// <summary>A seat goes out by the game's own path: shots due at the same moment land with it.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(DeckGameplay), "ServerKillPlayer")]
    private static void GoingOut(DeckGameplay __instance) => HostRevolver.GoingOut(__instance);

    /// <summary>A message about this seat's pull: the host's record decides.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.InvokeUserCode_CmdKillPlayer))]
    private static bool Fired(NetworkBehaviour obj, NetworkConnectionToClient senderConnection) =>
        HostRevolver.Report(obj, senderConnection, false, saysFired: true);

    /// <summary>A message about this seat's pull: the host's record decides.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.InvokeUserCode_CmdRevolverSurvived))]
    private static bool Empty(NetworkBehaviour obj, NetworkConnectionToClient senderConnection) =>
        HostRevolver.Report(obj, senderConnection, false, saysFired: false);
}
