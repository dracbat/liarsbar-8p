using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Fair play for the card throws of Liar's Deck (Basic, Devil, Deck 2) and the Chaos deck.
///
/// <b>What it does.</b> At a table this copy hosts, every throw a player's copy asks for is checked
/// by the host before the game plays it: how many cards, which values, whose turn it is, and that
/// the cards are ones the host itself dealt to that seat and the seat has not played yet. Taking
/// the thrown cards out of the hand afterwards is checked the same way: only the cards just
/// thrown, only places the hand has. The counts a seat relays to the table to show its throw are
/// the host's own count of the throw it accepted. The host decides from its own record whether a
/// hand is empty.
///
/// <b>What it never does.</b> It never runs for a bot or the host's own seat, never in ranked, and
/// never changes what an accepted throw does: the game's own handler runs, with its own calls to
/// the table, so a player without the mod sees exactly what they always did.
///
/// <b>Refused or caught.</b> Anything lag could explain (a throw just after the turn moved on, a
/// card already played, a hand the host is unsure of) is dropped quietly. Only what no unchanged
/// copy can ever send, at any lag, removes the player: more cards in one throw than the game lets
/// anyone pick, a value no card of the deck has, a card the host never dealt that seat (once the
/// deal is old enough that nothing from before it can still be on its way), a hand place that
/// does not exist, or a throw from a seat that has been out since before the deal.
/// </summary>
internal static class FairThrows
{
    internal enum Verdict { Accept, Refuse, Caught }

    /// <summary>The most cards one throw may hold: the game's own limit for each deck.</summary>
    internal const int DeckMax = 3, ChaosMax = 1;

    /// <summary>The devil card's value in a throw and in the host's record.</summary>
    internal const int DevilCard = -1;

    // ------------------------------------------------------------------ what the tests read

    /// <summary>The last decision taken, for the test driver.</summary>
    internal static Verdict Last { get; private set; }
    internal static string LastWhy { get; private set; } = "";

    /// <summary>Whether the game's own handler ran for the last checked throw or hide.</summary>
    internal static bool LastRan { get; private set; }

    // Test switches: make the next check see a seat as out, or as out since before the deal.
    private static bool _testDead, _testLongDead;

    // ------------------------------------------------------------------ the throw in flight

    // Per seat: how many cards the host accepted in the throw the seat has not yet taken out of
    // its hand. 0 when none is in flight; missing when the host does not know (a hand it took
    // over in a host migration), and then the gentler rule applies.
    private static readonly Dictionary<uint, int> _pending = new();
    private static int _match = -1;

    private static void Sync()
    {
        if (_match == Fair.Match) return;
        _match = Fair.Match;
        _pending.Clear();
    }

    private static int Pending(NetworkBehaviour part)
    {
        Sync();
        try { return part != null && _pending.TryGetValue(part.netId, out int n) ? n : -1; }
        catch { return -1; }
    }

    private static void SetPending(NetworkBehaviour part, int n)
    {
        Sync();
        try
        {
            if (part == null) return;
            if (n < 0) _pending.Remove(part.netId);
            else _pending[part.netId] = n;
        }
        catch { }
    }

    /// <summary>A new deal for a seat: nothing is in flight, unless this copy took the table over mid-round.</summary>
    private static void NewHand(NetworkBehaviour part) => SetPending(part, TookOver() ? -1 : 0);

    private static bool TookOver()
    {
        try
        {
            var nm = Fair.Net;
            if (nm != null && nm.MigrationStarted) return true;
            var hm = HostMigration.Instance;
            return hm != null && hm.isNewHost;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ decisions

    private static Verdict Say(Verdict v, string why, out string said)
    {
        said = why;
        return v;
    }

    /// <summary>
    /// One throw, decided. Pure: it reads what it is given and changes nothing, so the test
    /// driver can ask it about a throw that is never played.
    /// </summary>
    internal static Verdict JudgeThrow(bool chaos, IList<int> types, bool dead, bool longDead, bool turn,
                                       FairHands.Held held, out string why)
    {
        int max = chaos ? ChaosMax : DeckMax;
        if (types == null) return Say(Verdict.Refuse, "a throw with nothing in it", out why);

        // What no unchanged copy can pick, whenever it is sent.
        if (types.Count > max) return Say(Verdict.Caught, $"{types.Count} cards in one throw, more than the game allows", out why);
        int devils = 0;
        for (int i = 0; i < types.Count; i++)
        {
            int t = types[i];
            if (t < DevilCard || t > 4) return Say(Verdict.Caught, "a card value no card of this deck has", out why);
            if (t == 0) return Say(Verdict.Refuse, "a card with no value yet", out why);
            if (t == DevilCard)
            {
                if (chaos) return Say(Verdict.Refuse, "a devil card at the Chaos deck", out why);
                devils++;
            }
        }
        if (devils > 0 && types.Count > 1) return Say(Verdict.Caught, "the devil card thrown together with other cards", out why);

        // Whose turn, and who is still playing.
        if (longDead) return Say(Verdict.Caught, "a throw from a seat out of the game since before this deal", out why);
        if (dead) return Say(Verdict.Refuse, "a throw from a seat that is out", out why);
        if (!turn) return Say(Verdict.Refuse, "a throw out of turn", out why);

        // The cards themselves, against the host's own deal.
        if (held == null)
            return Say(Verdict.Accept, "no record of this hand (taken over, or changed outside a deal): the game decides", out why);
        if (types.Count == 0)
            return held.Left.Count == 0
                ? Say(Verdict.Accept, "nothing left to throw", out why)
                : Say(Verdict.Refuse, "a throw with no cards from a seat that still holds some", out why);
        if (!held.Holds(types, out _))
        {
            if (held.Settled && MoreThanDealt(held, types))
                return Say(Verdict.Caught, "a card the host never dealt to this seat", out why);
            return Say(Verdict.Refuse, "a card this seat has already played", out why);
        }
        return Say(Verdict.Accept, "a card the seat holds, on its turn", out why);
    }

    /// <summary>More copies of some value in the throw than the whole deal gave the seat.</summary>
    internal static bool MoreThanDealt(FairHands.Held held, IList<int> types)
    {
        if (held == null || held.Dealt == null || types == null) return false;
        var counts = new Dictionary<int, int>();
        foreach (int t in held.Dealt) counts[t] = counts.TryGetValue(t, out int c) ? c + 1 : 1;
        var thrown = new Dictionary<int, int>();
        for (int i = 0; i < types.Count; i++)
        {
            int t = types[i];
            thrown[t] = thrown.TryGetValue(t, out int c) ? c + 1 : 1;
            if (thrown[t] > (counts.TryGetValue(t, out int d) ? d : 0)) return true;
        }
        return false;
    }

    /// <summary>
    /// Taking cards out of a hand, decided. <paramref name="active"/> says, for each hand place,
    /// whether the host's copy still has that card in the hand.
    /// </summary>
    internal static Verdict JudgeHide(bool chaos, IList<int> indexes, IList<bool> active, int pending, out string why)
    {
        if (indexes == null || active == null) return Say(Verdict.Refuse, "a hand with no places", out why);
        int places = active.Count;
        if (indexes.Count > places) return Say(Verdict.Caught, "more cards taken out than a hand has places", out why);
        var seen = new HashSet<int>();
        for (int i = 0; i < indexes.Count; i++)
        {
            int at = indexes[i];
            if (at < 0 || at >= places) return Say(Verdict.Caught, "a hand place that does not exist", out why);
            if (!seen.Add(at)) return Say(Verdict.Caught, "the same hand place twice in one go", out why);
        }
        for (int i = 0; i < indexes.Count; i++)
            if (!active[indexes[i]]) return Say(Verdict.Refuse, "a card already out of the hand", out why);

        if (pending >= 0)
        {
            if (indexes.Count != pending)
                return Say(Verdict.Refuse, $"{indexes.Count} card(s) taken out after a throw of {pending}", out why);
        }
        else if (indexes.Count == 0 || indexes.Count > (chaos ? ChaosMax : DeckMax))
            return Say(Verdict.Refuse, $"{indexes.Count} card(s) taken out at once", out why);

        return Say(Verdict.Accept, "the cards just thrown", out why);
    }

    /// <summary>
    /// A count a seat relays to the table to show its throw, decided. On accept,
    /// <paramref name="truth"/> is the count the table is shown: the host's own, when it knows it.
    /// </summary>
    internal static Verdict JudgeRelay(int count, int places, int pending, int max, out int truth, out string why)
    {
        truth = count;
        if (count < 0 || count > places) return Say(Verdict.Caught, "a thrown-card count larger than a hand", out why);
        if (pending > 0)
        {
            truth = pending;
            return Say(Verdict.Accept, count == pending ? "the throw just accepted" : "shown as the throw the host accepted", out why);
        }
        if (pending == 0) return Say(Verdict.Refuse, "a thrown-card count with no throw to show", out why);
        if (count > max) return Say(Verdict.Refuse, "a thrown-card count larger than a throw", out why);
        return Say(Verdict.Accept, "no record of the throw: the game decides", out why);
    }

    /// <summary>Acts on a decision: logs it, and for a clear cheat removes the player.</summary>
    private static bool Act(Verdict v, string why, NetworkConnectionToClient conn, Component part, string what)
    {
        Last = v;
        LastWhy = why;
        int seat = FairHands.SeatOf(part);
        switch (v)
        {
            case Verdict.Caught:
                Fair.Caught(conn, $"{what} (seat {seat}): {why}");
                return false;
            case Verdict.Refuse:
                Fair.Refused($"connection {conn?.connectionId} {what} (seat {seat}): {why}");
                return false;
            default:
                return true;
        }
    }

    // ------------------------------------------------------------------ helpers

    private static List<int> Copy(Il2CppSystem.Collections.Generic.List<int> list)
    {
        if (list == null) return null;
        var copy = new List<int>(list.Count);
        for (int i = 0; i < list.Count; i++) copy.Add(list[i]);
        return copy;
    }

    private static List<bool> Active(Il2CppSystem.Collections.Generic.List<GameObject> cards)
    {
        var on = new List<bool>();
        if (cards == null) return on;
        for (int i = 0; i < cards.Count; i++)
        {
            var go = cards[i];
            on.Add(go != null && go.activeSelf);
        }
        return on;
    }

    private static int ActiveCount(Il2CppSystem.Collections.Generic.List<GameObject> cards)
    {
        int n = 0;
        foreach (bool b in Active(cards)) if (b) n++;
        return n;
    }

    private static void Seat(Component part, out PlayerStats p, out bool dead, out bool longDead, out bool turn)
    {
        p = part != null ? part.GetComponent<PlayerStats>() : null;
        dead = _testDead || (p != null && p.Dead) || HostRevolver.Falling(part);
        longDead = _testLongDead || FairHands.LongDead(p);
        turn = !_testDead && p != null && p.HaveTurn;
        _testDead = _testLongDead = false;
    }

    /// <summary>Peeks at the number a command carries without taking it from the message.</summary>
    private static bool PeekInt(NetworkReader reader, out int value)
    {
        value = 0;
        try
        {
            int at = reader.Position;
            value = NetworkReaderExtensions.ReadInt(reader);
            reader.Position = at;
            return true;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ Liar's Deck

    /// <summary>
    /// Liar's Deck: each throw a player asks for, checked against the host's own deal before the
    /// game plays it. A throw the host accepts runs through the game's own handler unchanged.
    /// </summary>
    [FairPatch("each Liar's Deck throw checked against the host's own deal")]
    [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.ServerThrowCards))]
    internal static class DeckThrow
    {
        // Fair.Install calls Register only on a class marked FairPatch, which FairThrows itself is not.
        private static void Register() => FairThrows.Register();

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Before(DeckGameplay __instance, Il2CppSystem.Collections.Generic.List<int> types,
                                   out List<int> __state)
        {
            __state = null;
            try
            {
                if (!Fair.Hosting || !Fair.SentBy(__instance, out var conn)) return true;
                LastRan = false;
                var list = Copy(types);
                Seat(__instance, out _, out bool dead, out bool longDead, out bool turn);
                var held = FairHands.Checked(__instance, __instance.cardTypes);
                var v = JudgeThrow(false, list, dead, longDead, turn, held, out string why);
                if (!Act(v, why, conn, __instance, "Liar's Deck throw")) return false;
                __state = list;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Fair.Tag} Liar's Deck throw not checked: {e.Message}");
                return true;
            }
        }

        [HarmonyPostfix]
        private static void After(DeckGameplay __instance, List<int> __state, bool __runOriginal)
        {
            if (__state == null || !__runOriginal) return;
            try
            {
                LastRan = true;
                FairHands.For(__instance)?.Take(__state);
                SetPending(__instance, __state.Count);
            }
            catch { }
        }
    }

    /// <summary>
    /// Liar's Deck: a seat takes the cards it just threw out of its hand. Only those cards, only
    /// places the hand has; taking the last card out is how a hand ends, so it is the host's to check.
    /// </summary>
    [FairPatch("Liar's Deck cards leave a hand only after the throw that played them")]
    [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.ServerHideCards))]
    internal static class DeckHide
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Before(DeckGameplay __instance, Il2CppStructArray<int> indexes, out bool __state)
        {
            __state = false;
            try
            {
                if (!Fair.Hosting || !Fair.SentBy(__instance, out var conn)) return true;
                LastRan = false;
                var idx = indexes == null ? null : new List<int>(indexes);
                var v = JudgeHide(false, idx, Active(__instance.Cards), Pending(__instance), out string why);
                if (!Act(v, why, conn, __instance, "Liar's Deck hand")) return false;
                __state = true;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Fair.Tag} Liar's Deck hand not checked: {e.Message}");
                return true;
            }
        }

        [HarmonyPostfix]
        private static void After(DeckGameplay __instance, bool __state, bool __runOriginal)
        {
            if (!__state || !__runOriginal) return;
            LastRan = true;
            SetPending(__instance, 0);
            try { var held = FairHands.For(__instance); if (held != null) held.Unhidden = 0; }
            catch { }
        }
    }

    /// <summary>A Liar's Deck hand dealt on the server: no throw of it is in flight yet.</summary>
    [FairPatch("a Liar's Deck deal starts each seat's throws afresh")]
    [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.ApplyCardState))]
    internal static class DeckDealt
    {
        [HarmonyPostfix]
        private static void After(DeckGameplay __instance)
        {
            try { if (NetworkServer.active && !FairHands.InReceived) NewHand(__instance); }
            catch { }
        }
    }

    private enum Relay { None, Props, Meshes }
    private static Relay _relay;
    private static IntPtr _relayFor;

    /// <summary>
    /// Liar's Deck: the counts a seat relays to the table to show its throw on every screen. The
    /// table is shown the throw the host accepted. The game's own showing of a throw, as the host
    /// plays it, is not touched: only a count relayed on a seat's behalf is.
    /// </summary>
    [FairPatch("a Liar's Deck throw is shown as the host accepted it")]
    [HarmonyPatch]
    internal static class DeckRelays
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.InvokeUserCode_CmdSetThrownCardMeshes__Int32))]
        private static void MeshesIn(NetworkBehaviour obj) => Enter(Relay.Meshes, obj);

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.InvokeUserCode_CmdSetThrownCardMeshes__Int32))]
        private static Exception MeshesOut(Exception __exception) { _relay = Relay.None; return __exception; }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.InvokeUserCode_CmdDeckSyncThrowPropsVisible__Int32))]
        private static void PropsIn(NetworkBehaviour obj) => Enter(Relay.Props, obj);

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.InvokeUserCode_CmdDeckSyncThrowPropsVisible__Int32))]
        private static Exception PropsOut(Exception __exception) { _relay = Relay.None; return __exception; }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.RpcSetThrownCardMeshes))]
        private static bool Meshes(DeckGameplay __instance, ref int count) => Check(Relay.Meshes, __instance, ref count);

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.RpcDeckSyncThrowPropsVisible))]
        private static bool Props(DeckGameplay __instance, ref int count) => Check(Relay.Props, __instance, ref count);

        private static void Enter(Relay kind, NetworkBehaviour obj)
        {
            _relay = kind;
            try { _relayFor = obj != null ? obj.Pointer : IntPtr.Zero; }
            catch { _relayFor = IntPtr.Zero; }
        }

        private static bool Check(Relay kind, DeckGameplay gp, ref int count)
        {
            try
            {
                if (_relay != kind || gp == null || gp.Pointer != _relayFor) return true;
                if (!Fair.Hosting || !Fair.SentBy(gp, out var conn)) return true;
                int places = gp.Cards != null ? gp.Cards.Count : 0;
                var v = JudgeRelay(count, places, Pending(gp), DeckMax, out int truth, out string why);
                if (!Act(v, why, conn, gp, kind == Relay.Meshes ? "Liar's Deck thrown cards" : "Liar's Deck throw props")) return false;
                count = truth;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Fair.Tag} Liar's Deck throw count not checked: {e.Message}");
                return true;
            }
        }
    }

    // ------------------------------------------------------------------ the Chaos deck

    /// <summary>
    /// The Chaos deck: each throw checked against the host's own deal before the game plays it,
    /// and the hand counted as emptied only when the host's own record agrees.
    /// </summary>
    [FairPatch("each Chaos deck throw checked against the host's own deal")]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.ServerThrowCards))]
    internal static class ChaosThrow
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Before(ChaosDeckGameplay __instance, Il2CppSystem.Collections.Generic.List<int> types,
                                   ref bool emptiedHand, out List<int> __state)
        {
            __state = null;
            try
            {
                if (!Fair.Hosting || !Fair.SentBy(__instance, out var conn)) return true;
                LastRan = false;
                var list = Copy(types);
                Seat(__instance, out _, out bool dead, out bool longDead, out bool turn);
                var held = FairHands.Checked(__instance, __instance.CardTypes);
                var v = JudgeThrow(true, list, dead, longDead, turn, held, out string why);
                if (!Act(v, why, conn, __instance, "Chaos deck throw")) return false;

                // An emptied hand ends the seat's round. The host says so only when its own record
                // and its own copy of the hand both agree nothing is left after this throw; if
                // either says the hand is empty, the game's own word stands.
                if (emptiedHand && held != null && list != null
                    && held.Left.Count > list.Count && ActiveCount(__instance.Cards) > list.Count)
                {
                    emptiedHand = false;
                    Fair.Refused($"connection {conn.connectionId} Chaos deck (seat {FairHands.SeatOf(__instance)}): " +
                                 "a hand said to be empty that still holds cards");
                }
                __state = list;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Fair.Tag} Chaos deck throw not checked: {e.Message}");
                return true;
            }
        }

        [HarmonyPostfix]
        private static void After(ChaosDeckGameplay __instance, List<int> __state, bool __runOriginal)
        {
            if (__state == null || !__runOriginal) return;
            try
            {
                LastRan = true;
                FairHands.For(__instance)?.Take(__state);
                SetPending(__instance, __state.Count);
            }
            catch { }
        }
    }

    /// <summary>The Chaos deck: cards leave a hand only after the throw that played them, as in Liar's Deck.</summary>
    [FairPatch("Chaos deck cards leave a hand only after the throw that played them")]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.ServerChaosHideCards))]
    internal static class ChaosHide
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Before(ChaosDeckGameplay __instance, Il2CppStructArray<int> indexes, out bool __state)
        {
            __state = false;
            try
            {
                if (!Fair.Hosting || !Fair.SentBy(__instance, out var conn)) return true;
                LastRan = false;
                var idx = indexes == null ? null : new List<int>(indexes);
                var v = JudgeHide(true, idx, Active(__instance.Cards), Pending(__instance), out string why);
                if (!Act(v, why, conn, __instance, "Chaos deck hand")) return false;
                __state = true;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Fair.Tag} Chaos deck hand not checked: {e.Message}");
                return true;
            }
        }

        [HarmonyPostfix]
        private static void After(ChaosDeckGameplay __instance, bool __state, bool __runOriginal)
        {
            if (!__state || !__runOriginal) return;
            LastRan = true;
            SetPending(__instance, 0);
            try { var held = FairHands.For(__instance); if (held != null) held.Unhidden = 0; }
            catch { }
        }
    }

    /// <summary>A Chaos deck hand dealt, or cleared for a new round: no throw of it is in flight.</summary>
    [FairPatch("a Chaos deck deal starts each seat's throws afresh")]
    [HarmonyPatch]
    internal static class ChaosDealt
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.RpcDealHand))]
        private static void Dealt(ChaosDeckGameplay __instance)
        {
            try { if (NetworkServer.active) NewHand(__instance); }
            catch { }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.ClearHandForNewRound))]
        private static void Cleared(ChaosDeckGameplay __instance)
        {
            try { if (NetworkServer.active) NewHand(__instance); }
            catch { }
        }
    }

    /// <summary>
    /// The Chaos deck has three older hand commands that today's game never sends. Every change to
    /// a hand goes through the checked throw and hide above, so these are not passed on from a
    /// player's copy. A hand place that does not exist, or a count larger than a hand, is something
    /// no copy of the game can send.
    /// </summary>
    [FairPatch("older Chaos deck hand commands are not passed on")]
    [HarmonyPatch]
    internal static class ChaosOlder
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.InvokeUserCode_TurnoffCard__Int32))]
        private static bool OneCard(NetworkBehaviour obj, NetworkReader reader)
        {
            try
            {
                if (!Fair.Hosting || !Fair.SentBy(obj, out var conn)) return true;
                var gp = obj.TryCast<ChaosDeckGameplay>();
                int places = gp != null && gp.Cards != null ? gp.Cards.Count : 0;
                Verdict v = PeekInt(reader, out int at) && (at < 0 || at >= places) ? Verdict.Caught : Verdict.Refuse;
                Act(v, v == Verdict.Caught ? "a hand place that does not exist" : "an older command, not passed on",
                    conn, gp, "Chaos deck card");
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Fair.Tag} older Chaos deck command not checked: {e.Message}");
                return false;
            }
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.InvokeUserCode_SetCardsCmd))]
        private static bool WholeHand(NetworkBehaviour obj)
        {
            try
            {
                if (!Fair.Hosting || !Fair.SentBy(obj, out var conn)) return true;
                Act(Verdict.Refuse, "an older command, not passed on", conn, obj, "Chaos deck hand");
                return false;
            }
            catch { return false; }
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.InvokeUserCode_CmdChaosSetThrownCardMeshes__Int32))]
        private static bool Meshes(NetworkBehaviour obj, NetworkReader reader)
        {
            try
            {
                if (!Fair.Hosting || !Fair.SentBy(obj, out var conn)) return true;
                var gp = obj.TryCast<ChaosDeckGameplay>();
                int places = 0;
                if (gp != null)
                {
                    if (gp.Cards != null) places = gp.Cards.Count;
                    if (gp.CardsMasa != null) places = Math.Max(places, gp.CardsMasa.Count);
                }
                Verdict v = PeekInt(reader, out int count) && (count < 0 || count > places) ? Verdict.Caught : Verdict.Refuse;
                Act(v, v == Verdict.Caught ? "a thrown-card count larger than a hand" : "an older command, not passed on",
                    conn, gp, "Chaos deck thrown cards");
                return false;
            }
            catch { return false; }
        }
    }

    // ------------------------------------------------------------------ developer tests

    internal static void Register()
    {
        Fair.OnDrive("throw", Tests.Throw);
        Fair.OnDrive("hide", Tests.Hide);
        Fair.OnDrive("relay", Tests.Relay);
        Fair.OnDrive("dealt", Tests.Hands);   // "fair dealt": the host's record of each seat's dealt hand
    }

    /// <summary>
    /// Developer tests, inside the host only. Each one either asks the pure decisions above about a
    /// made-up throw, or runs the host's own handler for a seat's throw as if that seat had sent it,
    /// with values the host makes up itself, and reads back what the host decided. Nothing is sent
    /// to anyone on a seat's behalf. A test that expects a clear cheat removes that test seat,
    /// unless the host's KickCheaters setting is off.
    /// </summary>
    private static class Tests
    {
        private static bool Find(string which, out NetworkConnectionToClient conn, out DeckGameplay deck,
                                 out ChaosDeckGameplay chaos, out PlayerStats seat)
        {
            conn = Fair.TestConnection(which);
            deck = null; chaos = null; seat = null;
            if (conn == null) { Plugin.Log.LogWarning($"{Fair.Tag} test: no remote player to test with"); return false; }
            var m = Manager.Instance;
            if (m == null || m.Players == null) return false;
            foreach (var p in m.Players)
            {
                if (p == null || p.connectionToClient == null || p.connectionToClient.connectionId != conn.connectionId) continue;
                seat = p;
                deck = p.GetComponent<DeckGameplay>();
                if (deck == null) chaos = p.GetComponent<ChaosDeckGameplay>();
                return deck != null || chaos != null;
            }
            Plugin.Log.LogWarning($"{Fair.Tag} test: connection {conn.connectionId} has no deck seat");
            return false;
        }

        private static (string What, string Conn) Args(string rest)
        {
            var parts = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return (parts.Length > 0 ? parts[0] : "", parts.Length > 1 ? parts[1] : "");
        }

        private static NetworkBehaviour Part(DeckGameplay d, ChaosDeckGameplay c) => d != null ? d : c;

        private static FairHands.Held Record(DeckGameplay d, ChaosDeckGameplay c) =>
            d != null ? FairHands.Checked(d, d.cardTypes) : FairHands.Checked(c, c.CardTypes);

        /// <summary>Runs the host's own throw handler for the seat, as if it had sent this throw.</summary>
        private static void Throw(NetworkConnectionToClient conn, DeckGameplay d, ChaosDeckGameplay c, List<int> types)
        {
            var list = new Il2CppSystem.Collections.Generic.List<int>();
            foreach (int t in types) list.Add(t);
            Fair.AsSentBy(conn, () =>
            {
                if (d != null) d.ServerThrowCards(list, false);
                else c.ServerThrowCards(list, false);
            });
        }

        private static void Expect(string test, Verdict want, int caught0, int refused0)
        {
            bool pass = Last == want && !LastRan;
            if (want == Verdict.Caught) pass &= Fair.CaughtCount > caught0 || Fair.RefusedCount > refused0;
            if (want == Verdict.Refuse) pass &= Fair.RefusedCount > refused0 && Fair.CaughtCount == caught0;
            Fair.Result(test, pass, $"decided {Last} ({LastWhy}), game handler ran={LastRan}, " +
                                    $"caught +{Fair.CaughtCount - caught0}, refused +{Fair.RefusedCount - refused0}");
        }

        internal static void Throw(string rest)
        {
            var (what, which) = Args(rest);
            if (what == "selftest") { SelfTest(); return; }
            if (!Fair.Hosting) { Fair.Result($"throw {what}", false, $"not hosting ({Fair.Why})"); return; }
            if (!Find(which, out var conn, out var d, out var c, out var seat)) return;

            bool chaos = c != null;
            var held = Record(d, c);
            int caught0 = Fair.CaughtCount, refused0 = Fair.RefusedCount;
            Last = Verdict.Accept; LastWhy = ""; LastRan = false;

            switch (what)
            {
                case "five":
                    Throw(conn, d, c, new List<int> { 1, 1, 2, 2, 3 });
                    Expect("throw five", Verdict.Caught, caught0, refused0);
                    return;

                case "notdealt":
                {
                    if (held == null || !held.Settled)
                    {
                        Fair.Result("throw notdealt", false, "no settled record for that seat yet: deal, wait, try again");
                        return;
                    }
                    var types = NotDealt(held, chaos ? ChaosMax : DeckMax);
                    if (types == null) { Fair.Result("throw notdealt", false, "no value fits under the throw limit"); return; }
                    if (!seat.HaveTurn) { Fair.Result("throw notdealt", false, "wait for that seat's turn (the card check follows the turn check)"); return; }
                    Throw(conn, d, c, types);
                    Expect("throw notdealt", Verdict.Caught, caught0, refused0);
                    return;
                }

                case "dead":
                    _testLongDead = true;
                    Throw(conn, d, c, new List<int> { 1 });
                    Expect("throw dead", Verdict.Caught, caught0, refused0);
                    return;

                case "out":
                    _testDead = true;
                    Throw(conn, d, c, new List<int> { 1 });
                    Expect("throw out", Verdict.Refuse, caught0, refused0);
                    return;

                case "turn":
                {
                    if (seat.HaveTurn) { Fair.Result("throw turn", false, "that seat has the turn: try when it does not"); return; }
                    var types = held != null && held.Left.Count > 0 ? new List<int> { held.Left[0] } : new List<int> { 1 };
                    Throw(conn, d, c, types);
                    Expect("throw turn", Verdict.Refuse, caught0, refused0);
                    return;
                }

                case "legal":
                {
                    // Asked of the decision only: a legal throw would really be played for the seat.
                    if (held == null || held.Left.Count == 0) { Fair.Result("throw legal", false, "no record with cards for that seat"); return; }
                    var types = new List<int> { held.Left[0] };
                    var v = JudgeThrow(chaos, types, false, false, true, held, out string why);
                    Fair.Result("throw legal", v == Verdict.Accept, $"decided {v} ({why})");
                    return;
                }
            }
            Plugin.Log.LogWarning($"{Fair.Tag} test: fair throw <five|notdealt|dead|out|turn|legal|selftest> [connection]");
        }

        /// <summary>Some value repeated one more time than the deal gave it, within the throw limit.</summary>
        private static List<int> NotDealt(FairHands.Held held, int max)
        {
            for (int t = 1; t <= 4; t++)
            {
                int have = 0;
                foreach (int x in held.Dealt) if (x == t) have++;
                if (have + 1 > max) continue;
                var types = new List<int>();
                for (int i = 0; i <= have; i++) types.Add(t);
                return types;
            }
            return null;
        }

        internal static void Hide(string rest)
        {
            var (what, which) = Args(rest);
            if (!Fair.Hosting) { Fair.Result($"hide {what}", false, $"not hosting ({Fair.Why})"); return; }
            if (!Find(which, out var conn, out var d, out var c, out _)) return;
            int caught0 = Fair.CaughtCount, refused0 = Fair.RefusedCount;
            Last = Verdict.Accept; LastWhy = ""; LastRan = false;

            void Run(int[] idx) => Fair.AsSentBy(conn, () =>
            {
                var arr = new Il2CppStructArray<int>(idx);
                if (d != null) d.ServerHideCards(arr);
                else c.ServerChaosHideCards(arr);
            });

            var part = Part(d, c);
            var cards = d != null ? d.Cards : c.Cards;
            switch (what)
            {
                case "oob":
                    Run(new[] { 99 });
                    Expect("hide oob", Verdict.Caught, caught0, refused0);
                    return;
                case "extra":
                {
                    // A card still in the hand, with no throw of it in flight.
                    if (Pending(part) > 0) { Fair.Result("hide extra", false, "a throw is in flight for that seat: try again"); return; }
                    var on = Active(cards);
                    int at = on.IndexOf(true);
                    if (at < 0) { Fair.Result("hide extra", false, "that seat holds no cards"); return; }
                    Run(new[] { at });
                    Expect("hide extra", Verdict.Refuse, caught0, refused0);
                    return;
                }
                case "legal":
                {
                    var on = Active(cards);
                    int at = on.IndexOf(true);
                    if (at < 0) { Fair.Result("hide legal", false, "that seat holds no cards"); return; }
                    var v = JudgeHide(c != null, new List<int> { at }, on, 1, out string why);
                    Fair.Result("hide legal", v == Verdict.Accept, $"decided {v} ({why})");
                    return;
                }
            }
            Plugin.Log.LogWarning($"{Fair.Tag} test: fair hide <oob|extra|legal> [connection]");
        }

        /// <summary>The relayed thrown-card counts, asked of the decision only.</summary>
        internal static void Relay(string rest)
        {
            bool ok = true;
            var sb = new StringBuilder();
            void Check(string name, Verdict got, Verdict want, int truth = -2, int wantTruth = -2)
            {
                bool pass = got == want && (wantTruth == -2 || truth == wantTruth);
                ok &= pass;
                sb.Append($"{name}={(pass ? "ok" : "WRONG")} ");
            }
            int t;
            Check("negative", JudgeRelay(-1, 5, 2, DeckMax, out t, out _), Verdict.Caught);
            Check("past-hand", JudgeRelay(6, 5, 2, DeckMax, out t, out _), Verdict.Caught);
            Check("as-thrown", JudgeRelay(2, 5, 2, DeckMax, out t, out _), Verdict.Accept, t, 2);
            Check("rewritten", JudgeRelay(3, 5, 1, DeckMax, out t, out _), Verdict.Accept, t, 1);
            Check("no-throw", JudgeRelay(1, 5, 0, DeckMax, out t, out _), Verdict.Refuse);
            Check("unknown-ok", JudgeRelay(2, 5, -1, DeckMax, out t, out _), Verdict.Accept, t, 2);
            Check("unknown-big", JudgeRelay(4, 5, -1, DeckMax, out t, out _), Verdict.Refuse);
            Fair.Result("relay", ok, sb.ToString().Trim());
        }

        /// <summary>Each seat's record: seat numbers and counts only, never a card's value.</summary>
        internal static void Hands(string rest)
        {
            var m = Manager.Instance;
            var sb = new StringBuilder();
            foreach (var kv in FairHands.All)
            {
                int seat = -1;
                try
                {
                    if (m != null && m.Players != null)
                        foreach (var p in m.Players)
                            if (p != null && p.netId == kv.Key) seat = p.Slot;
                }
                catch { }
                var h = kv.Value;
                sb.Append($"seat {seat}: dealt {h.Dealt?.Length ?? 0}, left {h.Left.Count}, last throw {h.LastThrow}, " +
                          $"{(h.Unknown ? "set aside" : h.Settled ? "settled" : "new")}, in flight {(_pending.TryGetValue(kv.Key, out int n) ? n.ToString() : "?")}; ");
            }
            Plugin.Log.LogInfo($"{Fair.Tag} test: hands {(sb.Length == 0 ? "none recorded" : sb.ToString())}");
        }

        /// <summary>The throw and hide decisions on made-up hands; no seat, no network.</summary>
        private static void SelfTest()
        {
            bool ok = true;
            var sb = new StringBuilder();
            void Check(string name, Verdict got, Verdict want)
            {
                bool pass = got == want;
                ok &= pass;
                sb.Append($"{name}={(pass ? "ok" : $"WRONG({got})")} ");
            }

            var held = new FairHands.Held { Dealt = new[] { 1, 1, 2, 4, -1 }, At = Time.unscaledTime - 60f };
            held.Left.AddRange(held.Dealt);
            var fresh = new FairHands.Held { Dealt = new[] { 1, 2, 3, 4, 2 }, At = Time.unscaledTime };
            fresh.Left.AddRange(fresh.Dealt);

            Check("legal", JudgeThrow(false, new List<int> { 1, 1 }, false, false, true, held, out _), Verdict.Accept);
            Check("four", JudgeThrow(false, new List<int> { 1, 1, 2, 4 }, false, false, true, held, out _), Verdict.Caught);
            Check("chaos-two", JudgeThrow(true, new List<int> { 1, 2 }, false, false, true, null, out _), Verdict.Caught);
            Check("value", JudgeThrow(false, new List<int> { 9 }, false, false, true, held, out _), Verdict.Caught);
            Check("zero", JudgeThrow(false, new List<int> { 0 }, false, false, true, held, out _), Verdict.Refuse);
            Check("devil-plus", JudgeThrow(false, new List<int> { -1, 1 }, false, false, true, held, out _), Verdict.Caught);
            Check("devil", JudgeThrow(false, new List<int> { -1 }, false, false, true, held, out _), Verdict.Accept);
            Check("notdealt", JudgeThrow(false, new List<int> { 3 }, false, false, true, held, out _), Verdict.Caught);
            Check("notdealt-new", JudgeThrow(false, new List<int> { 1, 1 }, false, false, true, fresh, out _), Verdict.Refuse);
            Check("turn", JudgeThrow(false, new List<int> { 1 }, false, false, false, held, out _), Verdict.Refuse);
            Check("out", JudgeThrow(false, new List<int> { 1 }, true, false, false, held, out _), Verdict.Refuse);
            Check("long-out", JudgeThrow(false, new List<int> { 1 }, true, true, false, held, out _), Verdict.Caught);
            Check("no-record", JudgeThrow(false, new List<int> { 3 }, false, false, true, null, out _), Verdict.Accept);
            Check("empty", JudgeThrow(false, new List<int>(), false, false, true, held, out _), Verdict.Refuse);

            var played = new FairHands.Held { Dealt = new[] { 1, 2, 2 }, At = Time.unscaledTime - 60f };
            played.Left.AddRange(played.Dealt);
            played.Take(new List<int> { 1 });
            Check("played", JudgeThrow(false, new List<int> { 1 }, false, false, true, played, out _), Verdict.Refuse);

            var on = new List<bool> { true, true, false, true, true };
            Check("hide-ok", JudgeHide(false, new List<int> { 0, 1 }, on, 2, out _), Verdict.Accept);
            Check("hide-oob", JudgeHide(false, new List<int> { 5 }, on, 1, out _), Verdict.Caught);
            Check("hide-neg", JudgeHide(false, new List<int> { -1 }, on, 1, out _), Verdict.Caught);
            Check("hide-dup", JudgeHide(false, new List<int> { 1, 1 }, on, 2, out _), Verdict.Caught);
            Check("hide-gone", JudgeHide(false, new List<int> { 2 }, on, 1, out _), Verdict.Refuse);
            Check("hide-more", JudgeHide(false, new List<int> { 0, 1, 3 }, on, 1, out _), Verdict.Refuse);
            Check("hide-none", JudgeHide(false, new List<int> { 0 }, on, 0, out _), Verdict.Refuse);
            Check("hide-unknown", JudgeHide(true, new List<int> { 0 }, on, -1, out _), Verdict.Accept);
            Check("hide-unknown-big", JudgeHide(true, new List<int> { 0, 1 }, on, -1, out _), Verdict.Refuse);

            Fair.Result("throw selftest", ok, sb.ToString().Trim());
        }
    }
}
