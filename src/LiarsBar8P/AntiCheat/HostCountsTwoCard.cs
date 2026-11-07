using System;
using System.Collections.Generic;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Two-Card Poker: the host counts each seat's hand value itself, from the cards it dealt.
///
/// The host keeps its own record of every seat's two cards: the two it deals the seat, and each
/// card a swap brings in. It counts the hand with the game's own scoring rules, and that count is
/// the value the host holds for the seat, so every challenge, liar call, overall call and
/// face-off is settled on the host's count alone. A value or a shown hand from a player's game
/// that is not the host's count is set to the host's count, and logged as refused; a deal or a
/// swap takes a moment to reach the player, so a difference just after one is not logged.
///
/// Only at a table this copy hosts, for each remote player's seat this copy has dealt to. The
/// host's own seat and bots keep the game's own count. Nothing here removes anyone, and nothing
/// changes what is sent for an honest hand.
/// </summary>
[FairPatch("Two-Card Poker: the host counts each hand from the cards it dealt")]
[HarmonyPatch]
internal static class HostCountsTwoCard
{
    /// <summary>How long after a deal or a swap a different value is still put down to the moment it takes to arrive.</summary>
    private const float Settling = 5f;

    private sealed class Hand
    {
        internal int C0, C1, Match;
        internal int Before;   // the value held just before the latest deal or swap: still arriving, not refused
        internal float At;
        internal bool Said;
    }

    private static readonly Dictionary<uint, Hand> _hands = new();

    // ------------------------------------------------------------------ the game's scoring rules

    /// <summary>One card's value, as <c>PokerGamePlayManager.GetPokerCardValue</c> gives it.</summary>
    internal static int CardValue(int card) => card switch
    {
        1 or 2 => 7,
        3 or 4 => 6,
        5 or 6 => 12,
        7 or 8 => 5,
        >= 9 and <= 12 => 4,
        >= 13 and <= 16 => 3,
        >= 17 and <= 20 => 2,
        21 => -1,
        _ => 0,
    };

    /// <summary>A two-card hand's value, as <c>PokerGamePlay.CalculateTotalValue</c> counts it.</summary>
    internal static int HandValue(int card0, int card1)
    {
        int a = CardValue(card0), b = CardValue(card1);
        if (a != -1 && b != -1 && a != b) return a + b;
        if (a == -1) return b * b;
        return a * a;
    }

    // ------------------------------------------------------------------ the host's record

    private static Hand Find(uint id) => _hands.TryGetValue(id, out var h) && h.Match == Fair.Match ? h : null;

    /// <summary>The host deals a seat its two cards.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(PokerGamePlay), nameof(PokerGamePlay.RandomCards))]
    private static void Dealt(PokerGamePlay __instance, int __0, int __1)
    {
        try
        {
            if (!NetworkServer.active || __instance == null) return;
            _hands[__instance.netId] = new Hand
            {
                C0 = __0, C1 = __1, Match = Fair.Match, At = Time.unscaledTime, Before = __instance.CardValue,
            };
            Settle(__instance, fromSeat: false);
        }
        catch (Exception e) { Plugin.Log.LogError($"{Fair.Tag} a Two-Card Poker deal could not be noted: {e.Message}"); }
    }

    /// <summary>The host sends a seat the card its swap brought in.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(PokerGamePlay), nameof(PokerGamePlay.ChangeCard),
                  new[] { typeof(NetworkConnectionToClient), typeof(int), typeof(int) })]
    private static void Swapped(PokerGamePlay __instance, int __1, int __2)
    {
        try
        {
            if (!NetworkServer.active || __instance == null) return;
            var h = Find(__instance.netId);
            if (h == null || (__1 != 0 && __1 != 1)) return;
            h.Before = __instance.CardValue;
            if (__1 == 0) h.C0 = __2; else h.C1 = __2;
            h.At = Time.unscaledTime;
            h.Said = false;
            Settle(__instance, fromSeat: false);
        }
        catch (Exception e) { Plugin.Log.LogError($"{Fair.Tag} a Two-Card Poker swap could not be noted: {e.Message}"); }
    }

    /// <summary>
    /// Puts the host's count in the host's own copy of the seat's value: the field only, which
    /// every showdown the host settles reads.
    /// </summary>
    private static void Settle(PokerGamePlay poker, bool fromSeat)
    {
        if (!Fair.Hosting) return;
        var conn = poker.connectionToClient;
        if (!Fair.IsRemote(conn)) return;
        var h = Find(poker.netId);
        if (h == null) return;

        int count = HandValue(h.C0, h.C1);
        int had = poker.CardValue;
        if (had == count) return;
        poker.CardValue = count;

        if (fromSeat && !h.Said && had != h.Before && Time.unscaledTime - h.At >= Settling)
        {
            h.Said = true;
            Fair.Refused($"connection {conn.connectionId} Two-Card Poker (seat {HiddenDeal.SeatOf(poker)}): " +
                         $"a hand value that is not the host's count of its cards; the host's count stands");
        }
    }

    /// <summary>A seat's own state as it reaches the host: the value in it is the host's count.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(PokerGamePlay), nameof(PokerGamePlay.DeserializeSyncVars))]
    private static void Reported(PokerGamePlay __instance, bool initialState)
    {
        try { if (!initialState && __instance != null && NetworkServer.active) Settle(__instance, fromSeat: true); }
        catch (Exception e) { Plugin.Log.LogWarning($"{Fair.Tag} a Two-Card Poker hand value was not checked: {e.Message}"); }
    }

    /// <summary>
    /// A message showing a seat's cards: shown as they are when they are the host's record, and
    /// otherwise with the host's record of the seat's cards in their place.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    [HarmonyPatch(typeof(PokerGamePlay), nameof(PokerGamePlay.InvokeUserCode_CmdShowCardsFirst__Int32__Int32__Int32))]
    private static bool Shown(NetworkBehaviour obj, NetworkReader reader, NetworkConnectionToClient senderConnection)
    {
        try
        {
            if (!Fair.Hosting || reader == null) return true;
            var poker = obj != null ? obj.TryCast<PokerGamePlay>() : null;
            var conn = FairLegal.RemoteOwner(poker, senderConnection);
            if (conn == null) return true;
            var h = Find(poker.netId);
            if (h == null) return true;

            int at = reader.Position;
            int w, card1, card2;
            try
            {
                w = NetworkReaderExtensions.ReadInt(reader);
                card1 = NetworkReaderExtensions.ReadInt(reader);
                card2 = NetworkReaderExtensions.ReadInt(reader);
            }
            finally { reader.Position = at; }

            if ((card1 == h.C0 && card2 == h.C1) || (card1 == h.C1 && card2 == h.C0)) return true;

            if (Time.unscaledTime - h.At >= Settling)
                Fair.Refused($"connection {conn.connectionId} Two-Card Poker (seat {HiddenDeal.SeatOf(poker)}): " +
                             "a shown hand that is not the host's record of its cards; the host's record is shown");
            int c0 = h.C0, c1 = h.C1;
            Fair.AsSentBy(null, () => poker.UserCode_CmdShowCardsFirst__Int32__Int32__Int32(w, c0, c1));   // the host's own call
            return false;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"{Fair.Tag} a Two-Card Poker shown hand was not checked, the game's own handling runs: {e.Message}");
            return true;
        }
    }

    // ------------------------------------------------------------------ developer tests

    /// <summary>"fair twocard score": the host's count against the game's scoring rules, nothing at the table touched.</summary>
    internal static void TestScore()
    {
        int failed = 0;
        void Expect(string name, int got, int want)
        {
            if (got == want) return;
            failed++;
            Plugin.Log.LogWarning($"{Fair.Tag} test: twocard score {name}: {got}, expected {want}");
        }
        Expect("cards 1 and 3", HandValue(1, 3), 13);
        Expect("cards 9 and 17", HandValue(9, 17), 6);
        Expect("a pair, 1 and 2", HandValue(1, 2), 49);
        Expect("a pair, 5 and 6", HandValue(5, 6), 144);
        Expect("a pair, 13 and 16", HandValue(13, 16), 9);
        Expect("card 21 first", HandValue(21, 7), 25);
        Expect("card 21 second", HandValue(7, 21), 25);
        Expect("card 21 twice", HandValue(21, 21), 1);
        Expect("card 0 and card 1", HandValue(0, 1), 7);
        Expect("card 12 and card 20", HandValue(12, 20), 6);
        Fair.Result("twocard score", failed == 0, failed == 0 ? "every hand counted as the game counts it" : $"{failed} hand(s) counted wrong");
    }

    /// <summary>
    /// "fair twocard value [conn]": that seat's cards as the host recorded them, the host's count,
    /// and the value the host holds. Then the host puts a different value in its own copy of the
    /// seat, as if one had just arrived, and checks that its own count is put back. Nothing is sent.
    /// </summary>
    internal static void TestValue(NetworkConnectionToClient conn)
    {
        const string T = "twocard value";
        if (!Fair.Hosting) { Fair.Result(T, false, $"not hosting ({Fair.Why})"); return; }
        PokerGamePlay mine = null;
        foreach (var p in UnityEngine.Object.FindObjectsOfType<PokerGamePlay>())
        {
            var o = p.connectionToClient;
            if (o != null && o.connectionId == conn.connectionId) { mine = p; break; }
        }
        if (mine == null) { Fair.Result(T, false, "that connection has no Two-Card Poker seat"); return; }
        var h = Find(mine.netId);
        if (h == null) { Fair.Result(T, false, "no record of that seat's cards this match: deal a hand first"); return; }

        int count = HandValue(h.C0, h.C1);
        int held = mine.CardValue;
        bool said = h.Said;
        h.Said = true;   // the test's own value is not logged as refused
        mine.CardValue = count + 1;
        Settle(mine, fromSeat: true);
        h.Said = said;
        bool back = mine.CardValue == count;
        if (!back) mine.CardValue = held;
        Fair.Result(T, back && held == count,
                    $"connection {conn.connectionId} seat {HiddenDeal.SeatOf(mine)}: cards {h.C0} and {h.C1}, the host's count {count}, " +
                    $"held {held}; a different value was {(back ? "put back to the count" : "NOT put back")}");
    }
}
