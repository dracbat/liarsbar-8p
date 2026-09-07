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

    // There are deliberately NO Harmony patches on DealBasicOrDevil or DealDeck2 here.
    //
    // Both of those methods have their bytes scanned and rewritten by DeckSizePatch - the
    // deck size and the three card-face thresholds are immediate operands inside them. A
    // Harmony patch detours the method by overwriting its opening bytes, which is precisely
    // what that scan reads, so the two cannot both be applied to one method: at best the
    // scan fails and the log cheerfully reports a deck it did not resize, at worst a write
    // lands inside the detour stub and the next deal jumps into nothing.
    //
    // This was a postfix on each of them purely to print the resulting hands. The same
    // picture comes from the ResetRound prefix below and from DealTrace's hook on the outer
    // GiveCardsVisualRoutine, neither of which is byte-scanned - so nothing was lost by
    // deleting them, and the rule holds: never Harmony-patch a method you also native-scan.
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

    /// <summary>
    /// Report turns, seats and deaths on a machine that is not the host.
    ///
    /// The hooks below are postfixes on SyncVar setters, and Mirror only calls those on the
    /// server - a client receives the new value by deserialisation instead. So on a client
    /// every one of them was silent, and a loopback copy's log said nothing at all about the
    /// things the loopback harness exists to observe.
    ///
    /// This is a poll, so it is edge-sampled: a flag set and cleared inside one quarter-second
    /// tick shows up as one transition or none. Lines are tagged "seen" rather than "turn" to
    /// keep that honest - a missing line here is not evidence that nothing happened. The
    /// host's own log keeps the exact, unsampled ordering from the setters.
    /// </summary>
    private static readonly System.Collections.Generic.List<PlayerStats> _watched = new();
    private static float _watchedAt = -999f;
    private static int _lastSlot = int.MinValue;
    private static readonly System.Collections.Generic.Dictionary<int, bool> _lastTurn = new();
    private static readonly System.Collections.Generic.Dictionary<int, bool> _lastDead = new();

    internal static void PollClientState()
    {
        if (!Dev.Enabled) return;
        try
        {
            var m = Manager.Instance;
            if (m == null) { _lastSlot = int.MinValue; _lastTurn.Clear(); _lastDead.Clear(); return; }
            if (Mirror.NetworkServer.active) return;      // the setters already cover the host

            if (UnityEngine.Time.time - _watchedAt >= 2f)
            {
                _watchedAt = UnityEngine.Time.time;
                _watched.Clear();
                var all = UnityEngine.Object.FindObjectsOfType<PlayerStats>();
                if (all != null)
                    for (int i = 0; i < all.Count; i++)
                        if (all[i] != null) _watched.Add(all[i]);
            }

            int slot = m.ActivePlayerSlot;
            if (slot != _lastSlot)
            {
                _lastSlot = slot;
                string who = "nobody";
                for (int i = 0; i < _watched.Count; i++)
                    if (_watched[i] != null && _watched[i].Slot == slot) { who = Dev.Describe(_watched[i]); break; }
                Dev.Log("seen", $"active slot -> {slot} :: {who}");
            }

            for (int i = 0; i < _watched.Count; i++)
            {
                var p = _watched[i];
                if (p == null) continue;
                int key = p.Slot;

                bool turn = p.HaveTurn;
                if (!_lastTurn.TryGetValue(key, out bool hadTurn) || hadTurn != turn)
                {
                    _lastTurn[key] = turn;
                    if (turn) Dev.Log("seen", $"turn given to {Dev.Describe(p)}");
                }

                bool dead = p.Dead;
                if (!_lastDead.TryGetValue(key, out bool wasDead) || wasDead != dead)
                {
                    _lastDead[key] = dead;
                    if (dead) Dev.Log("seen", $"{p.PlayerName} is out (seat {p.Slot})");
                }
            }
        }
        catch { }
    }

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

    /// <summary>
    /// Report the hands after the deal. Hung on ResetRound - which calls the deal, and which
    /// nothing byte-scans - rather than on the deal itself, which is scanned.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.ResetRound))]
    private static void RoundDealt(DeckGamePlayManager __instance)
    {
        if (!Dev.Enabled) return;
        // Which of the two deals ran is not worth guessing at from here; the hands are the
        // point, and DeckSizePatch already names the one it resized.
        try { ReportDeal("the deal"); }
        catch { }
    }

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
