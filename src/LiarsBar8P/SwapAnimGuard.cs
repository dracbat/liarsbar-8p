using System;
using HarmonyLib;

namespace LiarsBar8P;

/// <summary>
/// Another player's Liar's Poker card swap, seen from a copy that was never told the card.
///
/// The game deals each seat's two cards to that seat's own copy and to the host, and to nobody
/// else. A swap is then played on every screen: the swapper's own copy raises the card and
/// confirms it, its animator is carried to everyone, and part way through the animation an event
/// (<c>BulletEvents.CardSwitch</c>) draws the card from its number (<c>TexasCard.SetMesh</c>, which
/// reads <c>CardMaterials[cardNumber - 1]</c>). On every other copy that number was never set, so
/// it asks for card -1 and throws: once on each of those copies for every swap a person makes, the
/// lone "Index was out of range" a client logged about two seconds after another player's swap. It
/// is the game's own event and data; nothing of the mod's is on that path.
///
/// So on a copy that does not know the card the event is skipped, which leaves everything as the
/// throw did (the event stopped at that line, and its later steps never ran), without the error.
/// The swapper's own copy and the host know the card, and their event runs as before. Nothing is
/// sent and nothing anyone sees changes, at any table size.
/// </summary>
internal static class SwapAnimGuard
{
    /// <summary>Enough to see it happen in a test, few enough not to fill the log.</summary>
    private const int MaxReports = 5;

    private static int _skipped;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BulletEvents), nameof(BulletEvents.CardSwitch))]
    private static bool CardSwitch(BulletEvents __instance)
    {
        try
        {
            if (__instance == null) return true;

            // Anything else the event could stop on is left to the game, as before.
            var cards = __instance.cards;
            int at = __instance.selectedCard;
            if (cards == null || at < 0 || at >= cards.Count) return true;

            var held = cards[at];
            var card = held != null ? held.GetComponent<TexasCard>() : null;
            var m = Manager.Instance;
            var faces = m != null && m.TexasGame != null ? m.TexasGame.CardMaterials : null;
            if (card == null || faces == null) return true;

            int number = card.cardNumber;
            if (number >= 1 && number <= faces.Count) return true;

            _skipped++;
            if (_skipped <= MaxReports)
            {
                var seat = __instance.GetComponentInParent<PlayerStats>();
                Dev.Log("swapanim", $"seat {(seat != null ? seat.Slot : -1)}'s card swap seen from a copy that was never " +
                                    $"told its card (number {number}) - the game's swap event left as the throw left it");
            }
            return false;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[swapanim] {e.Message}");
            return true;
        }
    }
}
