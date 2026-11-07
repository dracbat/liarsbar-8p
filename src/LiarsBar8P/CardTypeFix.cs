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
    // One size per deal, each 0 until a round sets it. Liar's Deck 2 deals seven a head where
    // Basic deals five, so the two decks are never the same size and must not share one figure:
    // reading Basic's forty as Deck 2's size would give a fifty-six card deck twenty-two Jokers.
    private static int _basicSize;
    private static int _deck2Size;
    private static bool _loggedBasic, _loggedDeck2;

    /// <summary>Told by DeckSizePatch what each deal will actually produce.</summary>
    internal static void SetDeckSize(string deal, int size)
    {
        if (size <= 0) return;

        if (deal == "DealDeck2")
        {
            if (size != _deck2Size) { _deck2Size = size; _loggedDeck2 = false; }
        }
        else if (size != _basicSize) { _basicSize = size; _loggedBasic = false; }
    }

    /// <summary>
    /// Scale a vanilla threshold to the current deck. Returns the original when the deck
    /// is untouched, so an ordinary four player round behaves exactly as before.
    /// </summary>
    private static int Scale(int threshold, int vanillaDeck, int deckSize)
    {
        if (deckSize <= 0 || deckSize == vanillaDeck) return threshold;
        return (int)Math.Round(threshold * (double)deckSize / vanillaDeck, MidpointRounding.AwayFromZero);
    }

    private static int TypeFor(int n, int deckSize, int vanillaDeck, int t1, int t2, int t3, string which, ref bool logged)
    {
        int a = Scale(t1, vanillaDeck, deckSize);
        int b = Scale(t2, vanillaDeck, deckSize);
        int c = Scale(t3, vanillaDeck, deckSize);

        if (!logged)
        {
            logged = true;
            Plugin.Log.LogInfo(
                $"[cardtype] {which} scaled for a {deckSize} card deck: " +
                $"Ace 1-{a}, King {a + 1}-{b}, Queen {b + 1}-{c}, Joker {c + 1}-{deckSize}");
        }

        if (n <= a) return 1;
        if (n <= b) return 2;
        if (n <= c) return 3;
        return 4;
    }

    // Both originals are pure functions of n, so replacing them outright is safe and keeps
    // the proportions right. Returning false skips the original. Neither has a caller in the
    // current build - the compiler copied both into the deals, which DeckSizePatch rewrites in
    // place - so these only matter if an update calls them again.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.ToCardTypeBasic))]
    private static bool Basic(int n, ref int __result)
    {
        if (_basicSize <= 0 || _basicSize == 20) return true;   // untouched deck: let the game do it
        __result = TypeFor(n, _basicSize, 20, 6, 12, 18, "ToCardTypeBasic", ref _loggedBasic);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.ToCardTypeDeck2))]
    private static bool Deck2(int n, ref int __result)
    {
        if (_deck2Size <= 0 || _deck2Size == 28) return true;
        __result = TypeFor(n, _deck2Size, 28, 8, 16, 24, "ToCardTypeDeck2", ref _loggedDeck2);
        return false;
    }
}
