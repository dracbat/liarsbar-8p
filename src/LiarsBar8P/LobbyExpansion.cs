using HarmonyLib;

namespace LiarsBar8P;

/// <summary>
/// Why the lobby's player panels are NOT expanded.
///
/// <c>LobbyController.SpawnSlots</c> is a <c>List&lt;LobbySlot&gt;</c>, and
/// <c>LobbySlot</c> is a <c>NetworkBehaviour</c>: it carries SyncVars (Dolu/occupied,
/// PlayerName, Ready), UI references (name text, ready icon, kick button) and a Kick
/// RPC. These are scene objects, not positional markers.
///
/// They cannot be added correctly from a runtime mod. Mirror identifies runtime-spawned
/// networked objects by an assetId registered in <c>spawnPrefabs</c> at build time,
/// while scene objects carry only a sceneId. Cloning one duplicates a scene identity,
/// which corrupts spawn handling and disconnects every client; stripping the networking
/// destroys the very component the list is meant to store.
///
/// Earlier versions attempted both and broke sessions. This class now only reports the
/// constraint once, so the limit is visible in a log rather than rediscovered.
/// </summary>
internal static class LobbyExpansion
{
    private static bool _reported;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.Start))]
    private static void Report(LobbyController __instance)
    {
        if (_reported) return;
        _reported = true;
        try
        {
            int have = __instance.SpawnSlots == null ? -1 : __instance.SpawnSlots.Count;
            int want = Plugin.MaxPlayers.Value;
            if (have > 0 && have < want)
            {
                Plugin.Log.LogWarning(
                    $"[lobby] {have} lobby panels for {want} players - panels are networked " +
                    "scene objects and cannot be added at runtime; the in-game seat ring is " +
                    "expanded instead");
            }
        }
        catch { }
    }
}
