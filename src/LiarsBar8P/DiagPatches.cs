using HarmonyLib;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Read-only instrumentation. The seat rings, per-mode player prefabs and UI
/// nameplates are pre-placed scene objects, so their real counts and world
/// positions can only be observed at runtime. Everything the 8-seat work needs
/// to know gets logged here.
/// </summary>
internal static class DiagPatches
{
    private static void Line(string s)
    {
        if (Plugin.Verbose.Value) Plugin.Log.LogInfo(s);
    }

    private static int CountOf<T>(Il2CppSystem.Collections.Generic.List<T> list)
        => list == null ? -1 : list.Count;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Manager), nameof(Manager.Start))]
    private static void Manager_Start_Postfix(Manager __instance)
    {
        try
        {
            Line("================ Manager.Start ================");
            Line($"  mode              = {__instance.mode}");
            Line($"  StartPlayerCount  = {__instance.StartPlayerCount}");
            Line($"  ActivePlayerSlot  = {__instance.ActivePlayerSlot}");
            Line($"  Slots.Count       = {CountOf(__instance.Slots)}");
            Line($"  Players.Count     = {CountOf(__instance.Players)}");
            Line($"  NameTexts.Count   = {CountOf(__instance.NameTexts)}");

            Line("  -- per-mode player prefab lists --");
            Line($"    MatchMakingPlayerPrefabs = {CountOf(__instance.MatchMakingPlayerPrefabs)}");
            Line($"    RoulettePlayerPrefabs    = {CountOf(__instance.RoulettePlayerPrefabs)}");
            Line($"    ChaosPlayerPrefabs       = {CountOf(__instance.ChaosPlayerPrefabs)}");
            Line($"    ChaosDeckPlayerPrefabs   = {CountOf(__instance.ChaosDeckPlayerPrefabs)}");
            Line($"    DicePlayerPrefabs        = {CountOf(__instance.DicePlayerPrefabs)}");
            Line($"    PokerPlayerPrefabs       = {CountOf(__instance.PokerPlayerPrefabs)}");
            Line($"    TexasPlayerPrefabs       = {CountOf(__instance.TexasPlayerPrefabs)}");
            Line($"    LiarsSpinPrefabs         = {CountOf(__instance.LiarsSpinPrefabs)}");

            DumpSlots(__instance);
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogError($"Manager diag failed: {e.Message}");
        }
    }

    /// <summary>
    /// Seat transforms drive where extra players would have to be placed, so log
    /// world position and yaw for each existing seat.
    /// </summary>
    private static void DumpSlots(Manager m)
    {
        var slots = m.Slots;
        if (slots == null) { Line("  Slots == null"); return; }

        Line("  -- seat transforms --");
        Vector3 centre = Vector3.zero;
        int n = 0;

        for (int i = 0; i < slots.Count; i++)
        {
            var t = slots[i];
            if (t == null) { Line($"    [{i}] null"); continue; }
            centre += t.position;
            n++;
            Line($"    [{i}] pos={t.position.ToString("F3")} " +
                 $"yaw={t.eulerAngles.y:F1} name={t.gameObject.name}");
        }

        if (n > 0)
        {
            centre /= n;
            Line($"  seat centroid = {centre.ToString("F3")}");
            for (int i = 0; i < slots.Count; i++)
            {
                var st = slots[i];
                if (st == null) continue;
                var d = st.position - centre;
                Line($"    [{i}] radius={new Vector2(d.x, d.z).magnitude:F3} " +
                     $"angle={Mathf.Atan2(d.z, d.x) * Mathf.Rad2Deg:F1}deg");
            }
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.Start))]
    private static void Lobby_Start_Postfix(LobbyController __instance)
    {
        try
        {
            Line("================ LobbyController.Start ================");
            Line($"  Mode            = {__instance.Mode}");
            Line($"  SpawnSlots      = {CountOf(__instance.SpawnSlots)}");
            Line($"  DeckModes       = {CountOf(__instance.DeckModes)}");
            Line($"  DiceModes       = {CountOf(__instance.DiceModes)}");
            Line($"  CurrentLobbyID  = {__instance.CurrentLobbyID}");
            LobbySlotDiag.Dump(__instance);
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogError($"Lobby diag failed: {e.Message}");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CustomNetworkManager), nameof(CustomNetworkManager.Start))]
    private static void Net_Start_Postfix(CustomNetworkManager __instance)
    {
        try
        {
            Line("================ CustomNetworkManager.Start ================");
            Line($"  maxConnections  = {__instance.maxConnections}");
            Line($"  Mode            = {__instance.Mode}");
            Line($"  GamePlayers     = {CountOf(__instance.GamePlayers)}");
            Line($"  isVelvetRoom    = {__instance.isVelvetRoom}");
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogError($"Net diag failed: {e.Message}");
        }
    }
}

internal static class LobbySlotDiag
{
    /// <summary>
    /// Lobby podium slots are pre-placed scene transforms. Their positions decide
    /// where additional slots have to go, so record them precisely.
    /// </summary>
    internal static void Dump(LobbyController lc)
    {
        var slots = lc.SpawnSlots;
        if (slots == null) { Plugin.Log.LogInfo("  SpawnSlots == null"); return; }

        Plugin.Log.LogInfo("  -- lobby spawn slots --");
        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            if (s == null) { Plugin.Log.LogInfo($"    [{i}] null"); continue; }
            var t = s.transform;
            Plugin.Log.LogInfo(
                $"    [{i}] name={t.gameObject.name} pos={t.position.ToString("F3")} " +
                $"localPos={t.localPosition.ToString("F3")} yaw={t.eulerAngles.y:F1} " +
                $"parent={(t.parent != null ? t.parent.name : "<none>")} children={t.childCount}");
        }
    }
}
