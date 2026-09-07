using System;
using HarmonyLib;

namespace LiarsBar8P;

/// <summary>
/// Answers the handful of Steam questions the lobby asks, when there is no Steam lobby.
///
/// The loopback harness connects copies of the game to each other over TCP and never creates
/// a Steam lobby, because every copy is signed into the same account and cannot be several
/// distinct members of one. Almost all of the game copes with that. One thing does not:
///
///     LobbyController.Update:  Int32.Parse(SteamMatchmaking.GetLobbyData(lobby, key))
///
/// With no lobby, <c>GetLobbyData</c> returns an empty string and <c>Int32.Parse</c> throws -
/// every frame, from Update, which stops the rest of Update running and strands the lobby.
///
/// This is not a bug in the game and not a bug in the mod: it is the honest consequence of
/// running without Steam, and a real game over Steam never sees it. So the fix is scoped to
/// exactly that: when the harness is running and Steam has nothing to say, an empty answer
/// becomes "0". It does nothing whatsoever in a normal game - the guard is the first line.
/// </summary>
internal static class LoopbackSteamStub
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Steamworks.SteamMatchmaking), nameof(Steamworks.SteamMatchmaking.GetLobbyData))]
    private static void EmptyBecomesZero(ref string __result)
    {
        if (!Loopback.Active) return;
        if (string.IsNullOrEmpty(__result)) __result = "0";
    }
}
