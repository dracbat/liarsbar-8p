using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// The host's own record of what each seat holds in Liar's Deck and the Chaos deck, so a throw
/// can be checked against the cards the host dealt and not against whatever a player's copy says.
///
/// <b>Where it comes from.</b> Only from the host's own deal. Liar's Deck: every hand the server
/// puts in a seat goes through <c>DeckGameplay.ApplyCardState</c> on the server (the deal, the
/// deal the mod hands over again when the game's animation stalls, a host migration), and the
/// values it is given are the server's. The Chaos deck: the server deals each hand through one
/// call, <c>RpcDealHand</c>, with the values it drew. Nothing a client sends ever adds to a record.
///
/// <b>What changes it.</b> A throw the host accepted takes its cards out; nothing else does. So the
/// record can only ever be the same as the truth or larger than it (a card the host itself took
/// out of a hand for some other reason stays in the record). Larger only makes the checks more
/// forgiving, never stricter, which is the safe side: a record that might be smaller than the
/// truth would blame an honest player for throwing a card they hold.
///
/// <b>When it is not trusted.</b> A hand the host did not see dealt (this copy took over after a
/// migration, or the game cleared or re-showed a hand some other way) has no record, and no hand
/// check runs for that seat until its next deal. Before each check the record is also compared
/// with the server's own list of the seat's cards; if they differ, the record is set aside the
/// same way. Every one of these fails open: no check, never a false blame.
///
/// Bots and the host's own seat are recorded like anyone (the record is the host's own data),
/// but no check ever runs for them: only commands from a remote player are checked.
/// </summary>
internal static class FairHands
{
    /// <summary>One seat's hand as the host dealt it, less the throws the host accepted.</summary>
    internal sealed class Held
    {
        /// <summary>The values as dealt, in deal order.</summary>
        internal int[] Dealt;

        /// <summary>Still in the hand: the deal less every accepted throw.</summary>
        internal readonly List<int> Left = new();

        /// <summary>Cards thrown and accepted that the seat's copy has not yet taken out of its hand.</summary>
        internal int Unhidden;

        /// <summary>The size of the last throw the host accepted, for the counts the seat relays after it.</summary>
        internal int LastThrow;

        /// <summary>Something other than a deal changed the hand: no hand check until the next deal.</summary>
        internal bool Unknown;

        /// <summary>When the host dealt it (unscaled seconds), so a throw that may have left before the deal is never blamed.</summary>
        internal float At;

        /// <summary>Old enough that nothing sent before this deal can still be on its way.</summary>
        internal bool Settled => Time.unscaledTime - At >= SettleSeconds;

        /// <summary>Whether this many of these values are still in the hand.</summary>
        internal bool Holds(IList<int> types, out int missing)
        {
            missing = 0;
            var left = new List<int>(Left);
            for (int i = 0; i < types.Count; i++)
            {
                int at = left.IndexOf(types[i]);
                if (at < 0) { missing = types[i]; return false; }
                left.RemoveAt(at);
            }
            return true;
        }

        /// <summary>Takes an accepted throw out of the hand.</summary>
        internal void Take(IList<int> types)
        {
            for (int i = 0; i < types.Count; i++)
            {
                int at = Left.IndexOf(types[i]);
                if (at >= 0) Left.RemoveAt(at);
            }
            Unhidden += types.Count;
            LastThrow = types.Count;
        }
    }

    private static readonly Dictionary<uint, Held> _hands = new();
    private static readonly HashSet<uint> _deadAtDeal = new();
    private static readonly HashSet<uint> _mismatchSaid = new();
    private static int _match = -1;
    private static int _snapFrame = -1;
    private static float _dealAt = -1000f;

    /// <summary>
    /// How long after a deal or roll before the host treats anything still arriving as sent after
    /// it. Far longer than any lag a connection survives, so a late message is never blamed.
    /// </summary>
    internal const float SettleSeconds = 10f;

    /// <summary>A new match, or this copy stopped hosting: every record from before is let go.</summary>
    private static void Sync()
    {
        if (_match == Fair.Match) return;
        _match = Fair.Match;
        _hands.Clear();
        _deadAtDeal.Clear();
        _mismatchSaid.Clear();
    }

    /// <summary>Every seat's record, for the test driver's counts.</summary>
    internal static IEnumerable<KeyValuePair<uint, Held>> All { get { Sync(); return _hands; } }

    internal static void Clear()
    {
        _hands.Clear();
        _deadAtDeal.Clear();
        _mismatchSaid.Clear();
    }

    /// <summary>The record for a seat, or null when the host has none it trusts.</summary>
    internal static Held For(NetworkBehaviour part)
    {
        Sync();
        if (part == null) return null;
        try
        {
            if (!_hands.TryGetValue(part.netId, out var held) || held.Unknown) return null;
            return held;
        }
        catch { return null; }
    }

    /// <summary>
    /// The record for a seat, checked against the server's own list of the seat's cards first.
    /// The server's list is the deal and is never shortened by a throw, so it should read the
    /// same as the record's deal; if it does not, something other than a deal the host saw has
    /// changed the hand, and the record is set aside until the next one. An empty or missing
    /// server list proves nothing either way and leaves the record as it is.
    /// </summary>
    internal static Held Checked(NetworkBehaviour part, Il2CppSystem.Collections.Generic.List<int> serverList)
    {
        var held = For(part);
        if (held == null) return null;
        try
        {
            if (serverList == null || serverList.Count == 0) return held;
            bool same = serverList.Count == held.Dealt.Length;
            for (int i = 0; same && i < held.Dealt.Length; i++)
                if (serverList[i] != held.Dealt[i]) same = false;
            if (same) return held;

            held.Unknown = true;
            if (_mismatchSaid.Add(part.netId))
                Plugin.Log.LogInfo($"{Fair.Tag} a hand changed outside a deal the host saw (seat {SeatOf(part)}): " +
                                   "no hand check for it until its next deal");
            return null;
        }
        catch { return null; }
    }

    /// <summary>A hand the host dealt. Only ever called with the server's own values.</summary>
    internal static void Dealt(NetworkBehaviour part, IList<int> types)
    {
        Sync();
        if (part == null || types == null) return;
        try
        {
            var held = new Held { Dealt = new int[types.Count], At = Time.unscaledTime };
            for (int i = 0; i < types.Count; i++)
            {
                held.Dealt[i] = types[i];
                held.Left.Add(types[i]);
            }
            _hands[part.netId] = held;
            _mismatchSaid.Remove(part.netId);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"{Fair.Tag} could not record a hand: {e.Message}"); }
    }

    /// <summary>The hand was changed some way the host does not follow: no hand check until its next deal.</summary>
    internal static void Forget(NetworkBehaviour part)
    {
        Sync();
        try { if (part != null && _hands.TryGetValue(part.netId, out var held)) held.Unknown = true; }
        catch { }
    }

    /// <summary>
    /// A deal (or a dice roll) is under way: every seat already dead at this moment has been out
    /// for at least a whole round. Taken once a frame, however many hands the deal hands out.
    /// </summary>
    internal static void DealSeen()
    {
        Sync();
        if (_snapFrame == Time.frameCount) return;
        _snapFrame = Time.frameCount;
        _dealAt = Time.unscaledTime;
        try
        {
            var m = Manager.Instance;
            if (m == null || m.Players == null) return;
            foreach (var p in m.Players)
                if (p != null && p.Dead) _deadAtDeal.Add(p.netId);
        }
        catch { }
    }

    /// <summary>
    /// Dead since before the latest deal or roll, and dead still. Nothing in flight can explain a
    /// command from such a seat: a whole round of animations has passed since it was last alive.
    /// A seat that died in this round is dead but not this, and only ever refused. For the first
    /// seconds after a deal nobody is, so nothing still on its way from before it is ever blamed.
    /// </summary>
    internal static bool LongDead(PlayerStats p)
    {
        Sync();
        if (Time.unscaledTime - _dealAt < SettleSeconds) return false;
        try { return p != null && p.Dead && _deadAtDeal.Contains(p.netId); }
        catch { return false; }
    }

    /// <summary>A seat number for the log; never a name.</summary>
    internal static int SeatOf(Component part)
    {
        try
        {
            var p = part != null ? part.GetComponent<PlayerStats>() : null;
            return p != null ? p.Slot : -1;
        }
        catch { return -1; }
    }

    // ------------------------------------------------------------------ the deals

    private static int _rpcDepth;

    /// <summary>True while the host's own copy runs a call it received, not a decision of the server's.</summary>
    internal static bool InReceived => _rpcDepth > 0;

    /// <summary>
    /// The host is also a client, and runs every call it sends to the table on its own copy. A
    /// hand shown that way is a picture of what the server already decided, not a deal, and a
    /// later change to what goes on the wire must never be able to rewrite the host's record.
    /// So a hand set while the host's copy runs a call it received is not recorded.
    /// </summary>
    [FairPatch("the host tells its own deals from calls it receives", order: 2)]
    [HarmonyPatch(typeof(Mirror.RemoteCalls.RemoteProcedureCalls), nameof(Mirror.RemoteCalls.RemoteProcedureCalls.Invoke))]
    internal static class Received
    {
        [HarmonyPrefix]
        private static void Before(Mirror.RemoteCalls.RemoteCallType remoteCallType, out bool __state)
        {
            __state = remoteCallType != Mirror.RemoteCalls.RemoteCallType.Command;
            if (__state) _rpcDepth++;
        }

        [HarmonyFinalizer]
        private static Exception After(Exception __exception, bool __state)
        {
            if (__state && _rpcDepth > 0) _rpcDepth--;
            return __exception;
        }
    }

    /// <summary>
    /// Liar's Deck: every hand the server sets, by whatever path, ends here on the server. The
    /// values are the server's: the deal (after the Short Hand rule has cut it), the hand the mod
    /// hands over again when the deal animation stalls, a migration's restored hands. A null set
    /// of active places means the whole hand is in play.
    /// </summary>
    [FairPatch("the host keeps its own record of each Liar's Deck hand", order: 2)]
    [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.ApplyCardState))]
    internal static class DeckDeal
    {
        [HarmonyPostfix]
        private static void After(DeckGameplay __instance, Il2CppStructArray<int> syncedTypes, Il2CppStructArray<int> activeIndexes)
        {
            try
            {
                if (!NetworkServer.active || __instance == null || syncedTypes == null || _rpcDepth > 0) return;
                DealSeen();

                var dealt = new List<int>(syncedTypes.Length);
                for (int i = 0; i < syncedTypes.Length; i++) dealt.Add(syncedTypes[i]);
                Dealt(__instance, dealt);

                // Some places out of play (a migration's hand): those cards are not in the hand.
                if (activeIndexes != null && _hands.TryGetValue(__instance.netId, out var held))
                {
                    var on = new HashSet<int>();
                    for (int i = 0; i < activeIndexes.Length; i++) on.Add(activeIndexes[i]);
                    held.Left.Clear();
                    for (int i = 0; i < dealt.Count; i++)
                        if (on.Contains(i)) held.Left.Add(dealt[i]);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"{Fair.Tag} Liar's Deck hand not recorded: {e.Message}"); }
        }
    }

    /// <summary>The Chaos deck: the server's one call that deals a seat its hand, with the values it drew.</summary>
    [FairPatch("the host keeps its own record of each Chaos deck hand", order: 2)]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.RpcDealHand))]
    internal static class ChaosDeal
    {
        // First, and with nothing to return: whatever another patch then does with the call
        // (sends it differently, or not at all), the host has seen what it dealt.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Before(ChaosDeckGameplay __instance, Il2CppStructArray<int> handTypes)
        {
            try
            {
                if (!NetworkServer.active || __instance == null || handTypes == null) return;
                DealSeen();
                var dealt = new List<int>(handTypes.Length);
                for (int i = 0; i < handTypes.Length; i++) dealt.Add(handTypes[i]);
                Dealt(__instance, dealt);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"{Fair.Tag} Chaos deck hand not recorded: {e.Message}"); }
        }
    }

    /// <summary>The Chaos deck empties a hand between rounds; until the next deal there is nothing to check against.</summary>
    [FairPatch("a Chaos deck hand cleared for a new round", order: 2)]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.ClearHandForNewRound))]
    internal static class ChaosClear
    {
        [HarmonyPrefix]
        private static void Before(ChaosDeckGameplay __instance)
        {
            try { if (NetworkServer.active) Forget(__instance); }
            catch { }
        }
    }

    /// <summary>
    /// A Chaos deck hand shown again some older way than a deal. The host's record may then be
    /// smaller than the cards the seat really holds, so it is set aside until the next deal. (A
    /// player's copy cannot cause this: FairThrows does not pass the older command on.)
    /// </summary>
    [FairPatch("a Chaos deck hand shown again the old way", order: 2)]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.InvokeUserCode_SetCardsRpc))]
    internal static class ChaosShownAgain
    {
        [HarmonyPrefix]
        private static void Before(NetworkBehaviour obj)
        {
            try { if (NetworkServer.active) Forget(obj); }
            catch { }
        }
    }
}
