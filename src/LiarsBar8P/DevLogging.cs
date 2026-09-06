using System;
using HarmonyLib;

namespace LiarsBar8P;

/// <summary>
/// A running account of everything that decides who plays and with what: registration,
/// lobby creation, seating, dealing, turn changes, eliminations and round boundaries.
///
/// Every failure this mod has had above four players showed up as one of these happening
/// to the wrong player, in the wrong order, or not at all — and each one cost a test
/// session to find because the log was silent. Off unless developer mode is on.
///
/// Nothing here patches a method whose compiled code <see cref="TurnOrderFix"/> rewrites:
/// Harmony redirects a method by writing over its entry, which would move what that scan
/// reads. Turn changes are observed through the synced variable instead, which is the
/// thing that actually decides whose turn it is.
/// </summary>
internal static class DevLogging
{
    // ------------------------------------------------------------- registration

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerObjectController), nameof(PlayerObjectController.OnStartClient))]
    private static void PlayerRegistered(PlayerObjectController __instance)
    {
        if (!Dev.Enabled) return;
        try
        {
            Dev.Log("join", $"registered {Dev.Describe(__instance)} " +
                            $"({Dev.LobbyPlayers().Count} in the lobby now)");
        }
        catch (Exception e) { Dev.Warn("join", e.Message); }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerObjectController), nameof(PlayerObjectController.OnStopClient))]
    private static void PlayerLeft(PlayerObjectController __instance)
    {
        if (!Dev.Enabled) return;
        try { Dev.Log("join", $"left {Dev.Describe(__instance)}"); }
        catch (Exception e) { Dev.Warn("join", e.Message); }
    }

    // ------------------------------------------------------------------- lobby

    [HarmonyPostfix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.Start))]
    private static void LobbyOpened(LobbyController __instance)
    {
        if (!Dev.Enabled) return;
        try
        {
            Dev.Log("lobby", $"lobby ready: {(__instance.SpawnSlots == null ? -1 : __instance.SpawnSlots.Count)} " +
                             $"podiums, mode={__instance.Mode}, id={__instance.CurrentLobbyID}");
        }
        catch (Exception e) { Dev.Warn("lobby", e.Message); }
    }

    // ------------------------------------------------------------------ seating

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Manager), nameof(Manager.SpawnPlayerwithskin))]
    private static void Seated(PlayerObjectController player, int slot, int skin)
    {
        if (!Dev.Enabled) return;
        try { Dev.Log("seat", $"seated '{player?.PlayerName}' on seat {slot} (skin {skin})"); }
        catch (Exception e) { Dev.Warn("seat", e.Message); }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerStats), "set_NetworkSlot")]
    private static void SeatChanged(PlayerStats __instance, int value)
    {
        if (!Dev.Enabled) return;
        try { Dev.Log("seat", $"'{__instance.PlayerName}' seat -> {value}"); }
        catch { }
    }

    // ------------------------------------------------------------------ dealing

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.DealBasicOrDevil))]
    private static void DealtBasic() => ReportDeal("DealBasicOrDevil");

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.DealDeck2))]
    private static void DealtDeck2() => ReportDeal("DealDeck2");

    private static void ReportDeal(string which)
    {
        if (!Dev.Enabled) return;
        try
        {
            var d = Dev.Deck;
            int table = d?.MasaCards?.Count ?? -1;
            var sb = new System.Text.StringBuilder();
            sb.Append($"{which}: deck size {DeckSizePatch.CurrentSize}, {table} card objects, hands:");

            int dealt = 0, empty = 0;
            foreach (var p in Dev.TablePlayers())
            {
                if (p == null) continue;
                var gp = p.GetComponent<DeckGameplay>();
                int n = gp?.cardTypes?.Count ?? -1;
                sb.Append($" {p.PlayerName}={n}");
                if (n > 0) dealt++; else empty++;
            }
            Dev.Log("deal", sb.ToString());

            // The whole point of the exercise: did everybody actually get cards?
            if (empty > 0) Dev.Warn("deal", $"{empty} player(s) were dealt nothing, {dealt} were dealt cards");
        }
        catch (Exception e) { Dev.Warn("deal", e.Message); }
    }

    // -------------------------------------------------------------------- turns

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Manager), "set_NetworkActivePlayerSlot")]
    private static void ActiveSlotChanged(Manager __instance, int value)
    {
        if (!Dev.Enabled) return;
        try
        {
            string who = "nobody";
            foreach (var p in Dev.TablePlayers())
                if (p != null && p.Slot == value) { who = Dev.Describe(p); break; }
            Dev.Log("turn", $"active slot -> {value} :: {who}");
        }
        catch { }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerStats), "set_NetworkHaveTurn")]
    private static void TurnFlag(PlayerStats __instance, bool value)
    {
        if (!Dev.Enabled || !value) return;
        try { Dev.Log("turn", $"turn given to {Dev.Describe(__instance)}"); }
        catch { }
    }

    // ------------------------------------------------------------- eliminations

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerStats), "set_NetworkDead")]
    private static void Died(PlayerStats __instance, bool value)
    {
        if (!Dev.Enabled || !value) return;
        try
        {
            int alive = 0;
            foreach (var p in Dev.TablePlayers()) if (p != null && !p.Dead) alive++;
            Dev.Log("dead", $"{__instance.PlayerName} is out (seat {__instance.Slot}); {alive} still in");
        }
        catch { }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerStats), "set_NetworkHealth")]
    private static void HealthChanged(PlayerStats __instance, int value)
    {
        if (!Dev.Enabled) return;
        try { Dev.Log("dead", $"{__instance.PlayerName} health -> {value}"); }
        catch { }
    }

    // ------------------------------------------------------------------ rounds

    [HarmonyPrefix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.ResetRound))]
    private static void RoundStarting(bool first)
    {
        if (!Dev.Enabled) return;
        try
        {
            var m = Dev.Mgr;
            int alive = 0;
            foreach (var p in Dev.TablePlayers()) if (p != null && !p.Dead) alive++;
            Dev.Log("round", $"--- round starting ({(first ? "first" : "next")}) --- " +
                             $"{Dev.TablePlayers().Count} seated, {alive} alive, " +
                             $"StartPlayerCount={(m != null ? m.StartPlayerCount : -1)}");
        }
        catch (Exception e) { Dev.Warn("round", e.Message); }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Manager), "set_NetworkGameStarted")]
    private static void GameStarted(bool value)
    {
        if (!Dev.Enabled) return;
        try { Dev.Log("round", value ? "match started" : "match ended"); }
        catch { }
    }
}
