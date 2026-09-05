using System;
using HarmonyLib;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Dealing runs off the end of the deck above four players:
///
///   ArgumentOutOfRangeException at DeckGamePlayManager.DealBasicOrDevil()
///
/// Five players need 25 cards from a deck built for 20. The card lists are
/// List&lt;GameObject&gt; of real card objects, so the deck is topped up before the round
/// is set up.
///
/// Two sources, safest first:
///   1. ExtraCards - spare card objects the game already owns. Nothing is created.
///   2. Cloning an existing card, but ONLY after confirming the card carries no
///      NetworkIdentity. Duplicating networked scene objects is what corrupted spawn
///      handling and disconnected everyone earlier; that must never be repeated blindly.
/// </summary>
internal static class DeckFix
{
    private const int CardsPerPlayer = 5;
    private static bool _networkedCardsWarned;

    private static int PlayerCount()
    {
        try
        {
            var m = Manager.Instance;
            if (m == null) return 0;
            if (m.Players != null && m.Players.Count > 0) return m.Players.Count;
            if (m.StartPlayerCount > 0) return m.StartPlayerCount;
        }
        catch { }
        return 0;
    }

    /// <summary>True when the object carries no Mirror identity and is safe to duplicate.</summary>
    private static bool IsSafeToClone(GameObject go)
    {
        try
        {
            if (go == null) return false;
            var ids = go.GetComponentsInChildren<Mirror.NetworkIdentity>(true);
            return ids == null || ids.Length == 0;
        }
        catch { return false; }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.ResetRound))]
    private static void TopUpDeck(DeckGamePlayManager __instance, bool first)
    {
        try
        {
            int players = PlayerCount();
            if (players <= 0) return;

            int need = players * CardsPerPlayer;
            var deck = __instance.ResetCards;
            if (deck == null) { Plugin.Log.LogWarning("[deckfix] ResetCards is null"); return; }

            int have = deck.Count;
            int extras = __instance.ExtraCards != null ? __instance.ExtraCards.Count : 0;
            Plugin.Log.LogInfo(
                $"[deckfix] players={players} need={need} ResetCards={have} ExtraCards={extras}");

            if (have >= need) { Plugin.Log.LogInfo("[deckfix] deck already large enough"); return; }

            // --- source 1: spares the game already owns -------------------------------
            int taken = 0;
            if (__instance.ExtraCards != null)
            {
                while (deck.Count < need && __instance.ExtraCards.Count > 0)
                {
                    var spare = __instance.ExtraCards[0];
                    __instance.ExtraCards.RemoveAt(0);
                    if (spare == null) continue;
                    spare.SetActive(true);
                    deck.Add(spare);
                    taken++;
                }
                if (taken > 0) Plugin.Log.LogInfo($"[deckfix] took {taken} card(s) from ExtraCards");
            }

            if (deck.Count >= need)
            {
                Plugin.Log.LogInfo($"[deckfix] deck now {deck.Count} - satisfied from spares");
                return;
            }

            // --- source 2: clone, but only if cards are genuinely not networked --------
            var sample = deck.Count > 0 ? deck[0] : null;
            if (!IsSafeToClone(sample))
            {
                if (!_networkedCardsWarned)
                {
                    _networkedCardsWarned = true;
                    Plugin.Log.LogError(
                        "[deckfix] cards carry a NetworkIdentity - refusing to duplicate them. " +
                        $"deck={deck.Count} need={need}. More players than the deck supports.");
                }
                return;
            }

            int made = 0;
            while (deck.Count < need)
            {
                var src = deck[made % Mathf.Max(1, have)];
                if (src == null) break;

                bool wasActive = src.activeSelf;
                src.SetActive(false);
                var clone = UnityEngine.Object.Instantiate(src, src.transform.parent);
                src.SetActive(wasActive);

                clone.name = $"{src.name}_8P{made}";
                clone.SetActive(wasActive);
                deck.Add(clone);
                made++;

                if (made > 64) break; // never loop away
            }
            Plugin.Log.LogInfo($"[deckfix] cloned {made} card(s); deck now {deck.Count} (need {need})");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[deckfix] failed, deck left untouched: {e}");
        }
    }
}
