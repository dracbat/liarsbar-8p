using System;
using System.Collections.Generic;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Fair play: no screen draws another seat's cards, the host's own included.
///
/// <b>Why.</b> With <see cref="HiddenDeal"/> on, each player's machine is only sent plain cards for
/// the other seats, so they all look alike there. The host dealt every hand, the bots' too, so
/// its own game holds them all; this makes the host's screen look exactly like everyone else's.
///
/// <b>What changes.</b> On the host of a table where hands are hidden (<see cref="Fair.HideHands"/>),
/// every card in a hand that is not the host's own is drawn as the same plain card the other
/// players' machines are sent, so the host's screen looks like everyone else's. Only the drawing
/// changes: each card keeps its real value for the game and the bots, and keeps its place, so the
/// number of cards in each hand still shows. Cards on the table (the reveal, the thrown pile) are in nobody's
/// hand and are drawn as the game draws them. Liar's Texas hands are covered by
/// <see cref="TexasSwap"/>, which puts the question mark on every card of a seat this machine does
/// not own.
///
/// Nothing changes on a player's own copy.
/// </summary>
[FairPatch("other seats' cards drawn alike on the host's screen")]
[HarmonyPatch]
internal static class HiddenCards
{
    internal const int DecoyCard = HiddenDeal.DecoyCard;

    // The cards of every hand the host does not own: the card objects (Liar's Deck shapes), the
    // card parts (Chaos shapes) and the poker card parts. Rebuilt a few times a second.
    private static readonly HashSet<IntPtr> _objects = new(), _cards = new(), _poker = new();
    private static int _builtAt = -1000, _frame = -1;
    private static bool _on;

    internal static int HiddenCount => _objects.Count + _poker.Count;

    private static void Register() => Fair.OnTick("hidden cards", Tick);

    /// <summary>On only on the host of a table where hands are hidden. Read once a frame.</summary>
    internal static bool On
    {
        get
        {
            int f = Time.frameCount;
            if (f != _frame)
            {
                _frame = f;
                bool was = _on;
                _on = NetworkServer.active && Manager.Instance != null && Fair.HideHands;
                if (was && !_on) Clear();
            }
            return _on;
        }
    }

    private static void Tick()
    {
        if (!On) return;
        if (Time.frameCount - _builtAt < 15) return;
        Build();
    }

    private static void Clear()
    {
        _objects.Clear();
        _cards.Clear();
        _poker.Clear();
        _builtAt = -1000;
    }

    /// <summary>Every card in every hand the host does not own.</summary>
    internal static void Build()
    {
        _builtAt = Time.frameCount;
        _objects.Clear();
        _cards.Clear();
        _poker.Clear();
        var m = Manager.Instance;
        if (m == null) return;
        foreach (var p in TableHand.Seats(m))
        {
            try
            {
                if (p == null || p.isOwned) continue;

                var deck = p.GetComponent<DeckGameplay>();
                if (deck != null) AddCards(deck.Cards);

                var chaos = p.GetComponent<ChaosDeckGameplay>();
                if (chaos != null) AddCards(chaos.Cards);

                var poker = p.GetComponent<PokerGamePlay>();
                if (poker != null && poker.Cards != null)
                    foreach (var go in poker.Cards)
                    {
                        if (go == null) continue;
                        var pc = go.GetComponent<PokerCard>();
                        if (pc != null) _poker.Add(pc.Pointer);
                    }
            }
            catch { }
        }
    }

    private static void AddCards(Il2CppSystem.Collections.Generic.List<GameObject> cards)
    {
        if (cards == null) return;
        foreach (var go in cards)
        {
            if (go == null) continue;
            _objects.Add(go.Pointer);
            var c = go.GetComponent<Card>();
            if (c != null) _cards.Add(c.Pointer);
        }
    }

    private static bool Ready()
    {
        if (!On) return false;
        if (Time.frameCount - _builtAt > 30) Build();   // first use, or the ticker is not running
        return true;
    }

    // ------------------------------------------------------------------ the drawing

    /// <summary>Liar's Deck: every card shape the game puts on a card in another seat's hand.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.ApplyMeshToObject))]
    private static void DeckShape(GameObject target, ref int type)
    {
        try
        {
            if (target == null || type == DecoyCard || !Ready()) return;
            if (_objects.Contains(target.Pointer)) type = DecoyCard;
        }
        catch { }
    }

    /// <summary>The Chaos deck draws its hands with its own card shapes.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosDeckGamePlayManager), nameof(ChaosDeckGamePlayManager.ApplyChaosDeckCardMesh))]
    private static void ChaosShape(Card card, ref int cardType)
    {
        try
        {
            if (card == null || cardType == DecoyCard || !Ready()) return;
            if (_cards.Contains(card.Pointer)) cardType = DecoyCard;
        }
        catch { }
    }

    /// <summary>
    /// Two-Card Poker paints a card's face from its value every frame. For a hand the host does
    /// not own, the value is plain for the length of the painting only and put straight back.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(PokerCard), nameof(PokerCard.SetCard))]
    private static void PokerFace(PokerCard __instance, out int __state)
    {
        __state = int.MinValue;
        try
        {
            if (__instance == null || __instance.cardtype == DecoyCard || !Ready()) return;
            if (!_poker.Contains(__instance.Pointer)) return;
            __state = __instance.cardtype;
            __instance.cardtype = DecoyCard;
        }
        catch { }
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(PokerCard), nameof(PokerCard.SetCard))]
    private static Exception PokerFaceDone(Exception __exception, PokerCard __instance, int __state)
    {
        try { if (__state != int.MinValue && __instance != null) __instance.cardtype = __state; }
        catch { }
        return __exception;
    }

    // ------------------------------------------------------------------ for the test driver

    internal static bool IsHidden(GameObject go) => go != null && _objects.Contains(go.Pointer);
    internal static bool IsHidden(PokerCard pc) => pc != null && _poker.Contains(pc.Pointer);
}
