using System;
using HarmonyLib;

namespace LiarsBar8P;

/// <summary>
/// Keeps the deck's composition in proportion when the deck grows.
///
/// Decompiling the game shows the card type functions are pure arithmetic over the card
/// index, with thresholds baked in for the vanilla deck:
///
///     ToCardTypeBasic(n) : n&lt;=6 Ace, n&lt;=12 King, n&lt;=18 Queen, else Joker  (20 cards, 6/6/6/2)
///     ToCardTypeDeck2(n) : n&lt;=8 Ace, n&lt;=16 King, n&lt;=24 Queen, else Joker  (28 cards, 8/8/8/4)
///
/// Neither throws on a larger index - they simply call everything past the last threshold
/// a Joker. So a 25 card deck would deal 6 Aces, 6 Kings, 6 Queens and *7* Jokers, which
/// would wreck the bluffing odds far more quietly than a crash would.
///
/// The thresholds are therefore scaled with the deck. At eight players the basic deck
/// becomes exactly 12/12/12/4 - two vanilla decks, the composition originally asked for -
/// and intermediate sizes stay as close to 3:3:3:1 as whole cards allow.
/// </summary>
internal static class CardTypeFix
{
    private static int _deckSize;          // 0 until a round sets it
    private static bool _logged;

    /// <summary>Told by DeckSizePatch what the deal will actually produce.</summary>
    internal static void SetDeckSize(int size)
    {
        if (size > 0 && size != _deckSize)
        {
            _deckSize = size;
            _logged = false;
        }
    }

    /// <summary>
    /// Scale a vanilla threshold to the current deck. Returns the original when the deck
    /// is untouched, so an ordinary four player round behaves exactly as before.
    /// </summary>
    private static int Scale(int threshold, int vanillaDeck)
    {
        if (_deckSize <= 0 || _deckSize == vanillaDeck) return threshold;
        return (int)Math.Round(threshold * (double)_deckSize / vanillaDeck, MidpointRounding.AwayFromZero);
    }

    private static int TypeFor(int n, int vanillaDeck, int t1, int t2, int t3, string which)
    {
        int a = Scale(t1, vanillaDeck);
        int b = Scale(t2, vanillaDeck);
        int c = Scale(t3, vanillaDeck);

        if (!_logged)
        {
            _logged = true;
            Plugin.Log.LogInfo(
                $"[cardtype] {which} scaled for a {_deckSize} card deck: " +
                $"Ace 1-{a}, King {a + 1}-{b}, Queen {b + 1}-{c}, Joker {c + 1}-{_deckSize}");
        }

        if (n <= a) return 1;
        if (n <= b) return 2;
        if (n <= c) return 3;
        return 4;
    }

    // Both originals are pure functions of n, so replacing them outright is safe and keeps
    // the proportions right. Returning false skips the original.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.ToCardTypeBasic))]
    private static bool Basic(int n, ref int __result)
    {
        if (_deckSize <= 0 || _deckSize == 20) return true;   // untouched deck: let the game do it
        __result = TypeFor(n, 20, 6, 12, 18, "ToCardTypeBasic");
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.ToCardTypeDeck2))]
    private static bool Deck2(int n, ref int __result)
    {
        if (_deckSize <= 0 || _deckSize == 28) return true;
        __result = TypeFor(n, 28, 8, 16, 24, "ToCardTypeDeck2");
        return false;
    }
}
