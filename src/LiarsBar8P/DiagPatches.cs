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

            // A podium has no children, so its name text, ready icon and buttons live
            // elsewhere. Whether those are per podium or shared decides whether a copied
            // podium can drive UI of its own.
            try
            {
                Plugin.Log.LogInfo($"         NameText     = {Path(s.NameText != null ? s.NameText.transform : null)}");
                Plugin.Log.LogInfo($"         CharNameText = {Path(s.CharNameText != null ? s.CharNameText.transform : null)}");
                Plugin.Log.LogInfo($"         ReadyIcon    = {Path(s.ReadyIcon != null ? s.ReadyIcon.transform : null)}");
                Plugin.Log.LogInfo($"         SelectChar   = {Path(s.SelectChar != null ? s.SelectChar.transform : null)}");
                Plugin.Log.LogInfo($"         KickB        = {Path(s.KickB != null ? s.KickB.transform : null)}");
                Plugin.Log.LogInfo($"         components   = {Components(t.gameObject)}");
            }
            catch (System.Exception e) { Plugin.Log.LogInfo($"         (refs unreadable: {e.Message})"); }
        }
    }

    private static string Path(Transform t)
    {
        if (t == null) return "<null>";
        var parts = new System.Collections.Generic.List<string>();
        var cur = t;
        int guard = 0;
        while (cur != null && guard++ < 32) { parts.Insert(0, cur.name); cur = cur.parent; }
        var parent = t.parent;
        string siblings = parent != null ? $" (siblings={parent.childCount})" : "";
        return string.Join("/", parts) + siblings;
    }

    private static string Components(GameObject go)
    {
        var names = new System.Collections.Generic.List<string>();
        foreach (var c in go.GetComponents<Component>())
            names.Add(c == null ? "<null>" : c.GetIl2CppType().Name);
        return string.Join(", ", names);
    }
}
