using System;
using HarmonyLib;

namespace LiarsBar8P;

/// <summary>
/// Makes the deck's composition repeat, which is what actually doubles the deck.
///
/// Growing MasaCards and ResetCards adds card objects, but a card's face comes from
/// DeckGamePlayManager.ToCardTypeBasic(int n), which maps a card index to a type from a
/// fixed table sized for the vanilla twenty card deck. Asking it for card 20 or beyond
/// indexes past that table - the ArgumentOutOfRangeException seen inside
/// DealBasicOrDevil - and because only twenty real cards exist, five players get four
/// full hands and the fifth gets nothing.
///
/// Wrapping the index modulo the vanilla deck size turns card 20 back into card 0, so the
/// second deck is an exact copy of the first: 6/6/6/2 becomes 12/12/12/4, preserving the
/// bluffing odds rather than inventing a new distribution.
///
/// The vanilla size is learned from the indices the game itself asks for before any
/// wrapping is needed, so no deck size is hardcoded.
/// </summary>
internal static class CardTypeFix
{
    /// <summary>Highest index seen while the game was still asking within its own range.</summary>
    private static int _highestNatural = -1;
    private static int _vanillaSize = 20;
    private static bool _logged;

    private static int Wrap(int n, string which)
    {
        if (n < 0) return n;

        // learn the real deck size: indices the game asks for before we ever wrap
        if (n <= _highestNatural + 1 && n < _vanillaSize)
        {
            if (n > _highestNatural) _highestNatural = n;
            return n;
        }

        if (n < _vanillaSize) return n;

        int wrapped = n % _vanillaSize;
        if (!_logged)
        {
            _logged = true;
            Plugin.Log.LogInfo(
                $"[cardtype] {which}({n}) is past the {_vanillaSize} card table - wrapping to " +
                $"{wrapped}; the deck now repeats, which is what actually doubles it");
        }
        return wrapped;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.ToCardTypeBasic))]
    private static void Basic(ref int n) => n = Wrap(n, "ToCardTypeBasic");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.ToCardTypeDeck2))]
    private static void Deck2(ref int n) => n = Wrap(n, "ToCardTypeDeck2");

    /// <summary>
    /// Record the deck the game shipped with, so wrapping repeats exactly one deck rather
    /// than a number picked in advance.
    /// </summary>
    internal static void NoteVanillaDeck(int size)
    {
        if (size > 0 && size != _vanillaSize)
        {
            _vanillaSize = size;
            Plugin.Log.LogInfo($"[cardtype] vanilla deck is {size} cards; extra cards repeat it");
        }
    }
}
