using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Mirror;

namespace LiarsBar8P;

/// <summary>
/// Fair play: each seat's dealt hand goes only to the player whose seat it is.
///
/// <b>What it covers.</b> Every card deal: Liar's Deck (and a hand put back after a host
/// migration), the Chaos deck, Two-Card Poker, the older Liar's Deck table, and the card a Liar's
/// Poker swap brings in. The calls are listed in the table below.
///
/// <b>What changes.</b> At a table this copy hosts (<see cref="Fair.HideHands"/>), each seat's hand
/// is sent only to that seat, through Mirror's targeted send, and to the host's own game, which
/// dealt it and plays its bots from it. Every other seat gets the same number of plain cards, so
/// every hand still shows the right count. Every reveal the game makes carries its own cards (the
/// liar and spot-on reveals, the poker showdown), so nothing a player sees at a reveal comes from
/// these values. How other seats' cards are drawn on the host's own screen is
/// <see cref="HiddenCards"/>'s job.
///
/// <b>Safe by construction.</b> The payload is never rebuilt from scratch: the copy for other
/// players is the game's own bytes with only the card values overwritten, after the bytes have
/// been read back and found to hold exactly the layout the game writes. Anything unexpected (a
/// payload of another shape, a seat with no owner that is not a bot) sends the game's own call
/// unchanged, so a surprise costs privacy for one deal and never a broken one. Vanilla hosts,
/// ranked and any table this copy does not host are untouched.
/// </summary>
[FairPatch("each hand is dealt only to its own player")]
[HarmonyPatch]
internal static class HiddenDeal
{
    /// <summary>The plain card every other seat's hand is made of: an ordinary card in every deck.</summary>
    internal const int DecoyCard = 1;

    /// <summary>What other players are told a Texas swap brought in: no card at all.</summary>
    internal const int NoCard = -1;

    /// <summary>How a call lays out its values.</summary>
    internal enum Shape
    {
        /// <summary>One int[]: the hand.</summary>
        Hand,
        /// <summary>int[] hand, int[] still-held indexes, bool.</summary>
        Restore,
        /// <summary>A fixed number of plain ints, each one a card.</summary>
        Ints,
        /// <summary>Two plain ints: which card, then the card that came in. Only the second is private.</summary>
        Swap,
    }

    internal sealed class Call
    {
        internal string Name, Short;
        internal Shape Shape;
        internal int Ints;
    }

    // How Mirror names the calls, exactly as the game passes them to SendRPCInternal.
    private static readonly Dictionary<string, Call> _calls = new(StringComparer.Ordinal);

    private static void Add(string name, string shortName, Shape shape, int ints = 0) =>
        _calls[name] = new Call { Name = name, Short = shortName, Shape = shape, Ints = ints };

    static HiddenDeal()
    {
        Add("System.Void DeckGameplay::SetCardsRpc(System.Int32[])", "SetCardsRpc", Shape.Hand);
        Add("System.Void DeckGameplay::RestoreCardsRpc(System.Int32[],System.Int32[],System.Boolean)", "RestoreCardsRpc", Shape.Restore);
        Add("System.Void ChaosDeckGameplay::RpcDealHand(System.Int32[])", "RpcDealHand", Shape.Hand);
        Add("System.Void PokerGamePlay::RandomCards(System.Int32,System.Int32)", "PokerRandomCards", Shape.Ints, 2);
        Add("System.Void BlorfGamePlay::RandomCards(System.Int32,System.Int32,System.Int32,System.Int32,System.Int32)",
            "BlorfRandomCards", Shape.Ints, 5);
        Add("System.Void BlorfGamePlay::RandomCardsDeck2(System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32)",
            "BlorfRandomCardsDeck2", Shape.Ints, 7);
        Add("System.Void TexasGamePlay::ApplySwitch(System.Int32,System.Int32)", "TexasApplySwitch", Shape.Swap, 2);
    }

    /// <summary>What the host did with the last send of each seat and call, for the test driver.</summary>
    internal sealed class Sent
    {
        internal string Call;
        internal Shape Shape;
        internal uint NetId;
        internal int Seat, Owner = -1;
        internal int[] Truth;
        internal byte[] Original, Decoy;
        internal int[] Private;          // byte offsets of the values only the owner gets
        internal readonly List<(int Conn, bool Real, byte[] Payload)> To = new();
    }

    private static readonly Dictionary<(uint, string), Sent> _last = new();
    private static int _match = -1, _targeted, _fellBack;

    internal static int Targeted => _targeted;
    internal static int FellBack => _fellBack;
    internal static IEnumerable<Sent> LastDeals => _last.Values;
    internal static IEnumerable<Call> Calls => _calls.Values;

    private static void Register()
    {
        Fair.OnDrive("hands", HandsTests.Drive);
    }

    // ------------------------------------------------------------------ the send

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(NetworkBehaviour), "SendRPCInternal")]
    private static bool Send(NetworkBehaviour __instance, string functionFullName, int functionHashCode,
                             NetworkWriter writer, int channelId, bool includeOwner)
    {
        // Cheapest test first: almost every remote call is something else.
        if (functionFullName == null || functionFullName.Length < 40) return true;
        if (!_calls.TryGetValue(functionFullName, out var call)) return true;

        try
        {
            if (!NetworkServer.active || !Fair.HideHands || __instance == null || writer == null) return true;
            if (Fair.Match != _match) { _match = Fair.Match; _last.Clear(); }

            byte[] original = Bytes(writer);
            if (!Payload.Read(original, call, out int[] at, out int[] truth))
                return FallBack($"a {call.Short} this copy could not read");

            var identity = __instance.netIdentity;
            if (identity == null) return true;

            var owner = __instance.connectionToClient;
            if (owner == null && !BotSeats.Is(__instance))
                return FallBack($"a {call.Short} for a seat with no owner that is not a bot");

            byte[] decoy = Payload.Hidden(original, call, at);

            var sent = new Sent
            {
                Call = call.Short, Shape = call.Shape, NetId = __instance.netId, Seat = SeatOf(__instance),
                Owner = owner != null ? owner.connectionId : -1,
                Truth = truth, Original = original, Decoy = decoy, Private = at,
            };

            foreach (var conn in Observers(identity))
            {
                if (!includeOwner && owner != null && conn.connectionId == owner.connectionId) continue;
                bool real = Real(conn, owner);
                if (real) __instance.SendTargetRPCInternal(conn, functionFullName, functionHashCode, writer, channelId);
                else SendBytes(__instance, conn, functionFullName, functionHashCode, decoy, channelId);
                sent.To.Add((conn.connectionId, real, real ? original : decoy));
            }

            _last[(sent.NetId, sent.Call)] = sent;
            _targeted++;
            if (Dev.Enabled) HandsTests.CheckDeal(sent);
            return false;
        }
        catch (Exception e)
        {
            // Receiving a deal twice is harmless, so the game's own call is the safe side: the
            // hand arrives, this one deal without the protection, and the log says so.
            Plugin.Log.LogError($"{Fair.Tag} hidden deal failed, the game's own call goes out instead: {e.Message}");
            _fellBack++;
            return true;
        }
    }

    /// <summary>Who gets the real values: the seat's owner, and the host's own game.</summary>
    internal static bool Real(NetworkConnectionToClient conn, NetworkConnectionToClient owner)
    {
        if (conn == null) return false;
        if (!Fair.IsRemote(conn)) return true;   // the host's own local connection: it dealt them
        return owner != null && owner.connectionId == conn.connectionId;
    }

    /// <summary>The connections this seat's calls go to: its ready observers.</summary>
    internal static List<NetworkConnectionToClient> Observers(NetworkIdentity identity)
    {
        var list = new List<NetworkConnectionToClient>();
        var obs = identity != null ? identity.observers : null;
        if (obs == null) return list;
        foreach (var kv in obs)
        {
            var c = kv.Value;
            if (c != null && c.isReady) list.Add(c);
        }
        return list;
    }

    private static bool FallBack(string why)
    {
        _fellBack++;
        Fair.Refused($"hidden deal skipped once: {why} - the game's own call went out");
        return true;
    }

    private static void SendBytes(NetworkBehaviour part, NetworkConnectionToClient conn, string name, int hash,
                                  byte[] payload, int channel)
    {
        var w = NetworkWriterPool.Get();
        try
        {
            w.WriteBytes(new Il2CppStructArray<byte>(payload), 0, payload.Length);
            part.SendTargetRPCInternal(conn, name, hash, w, channel);
        }
        finally { NetworkWriterPool.Return(w); }
    }

    internal static byte[] Bytes(NetworkWriter writer)
    {
        var arr = writer.ToArray();
        if (arr == null) return Array.Empty<byte>();
        var b = new byte[arr.Length];
        for (int i = 0; i < b.Length; i++) b[i] = arr[i];
        return b;
    }

    internal static int SeatOf(UnityEngine.Component part)
    {
        try
        {
            var p = part.GetComponent<PlayerStats>();
            return p != null ? p.Slot : -1;
        }
        catch { return -1; }
    }
}

/// <summary>
/// Reading and rewriting a hidden call's payload. This Mirror build writes an int as four
/// little-endian bytes and an array as a four-byte count (-1 for none) followed by its values;
/// a payload is only accepted when that layout accounts for every byte.
/// </summary>
internal static class Payload
{
    /// <summary>The byte offsets of the private values, and the values, or false when the shape is not the expected one.</summary>
    internal static bool Read(byte[] b, HiddenDeal.Call call, out int[] at, out int[] values)
    {
        at = null;
        values = null;
        if (b == null) return false;
        switch (call.Shape)
        {
            case HiddenDeal.Shape.Hand:
            case HiddenDeal.Shape.Restore:
            {
                int pos = 0;
                if (!Count(b, ref pos, out int n)) return false;
                int first = pos;
                pos += 4 * n;
                if (pos > b.Length) return false;
                if (call.Shape == HiddenDeal.Shape.Restore)
                {
                    if (!Count(b, ref pos, out int m)) return false;
                    pos += 4 * m;
                    pos += 1;   // the bool
                }
                if (pos != b.Length) return false;
                at = new int[n];
                for (int i = 0; i < n; i++) at[i] = first + 4 * i;
                break;
            }
            case HiddenDeal.Shape.Ints:
                if (b.Length != 4 * call.Ints) return false;
                at = new int[call.Ints];
                for (int i = 0; i < call.Ints; i++) at[i] = 4 * i;
                break;
            case HiddenDeal.Shape.Swap:
                if (b.Length != 8) return false;
                at = new[] { 4 };
                break;
            default:
                return false;
        }
        values = new int[at.Length];
        for (int i = 0; i < at.Length; i++) values[i] = Int(b, at[i]);
        return true;
    }

    /// <summary>A copy of the payload with every private value replaced; same length, same layout.</summary>
    internal static byte[] Hidden(byte[] original, HiddenDeal.Call call, int[] at)
    {
        int value = call.Shape == HiddenDeal.Shape.Swap ? HiddenDeal.NoCard : HiddenDeal.DecoyCard;
        var copy = (byte[])original.Clone();
        foreach (int p in at) Put(copy, p, value);
        return copy;
    }

    private static bool Count(byte[] b, ref int pos, out int n)
    {
        n = -1;
        if (pos + 4 > b.Length) return false;
        n = Int(b, pos);
        pos += 4;
        return n >= 0 && n <= 64;   // no hand comes near; a null array (-1) is not a deal
    }

    internal static int Int(byte[] b, int at) =>
        b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24);

    internal static void Put(byte[] b, int at, int v)
    {
        b[at] = (byte)v;
        b[at + 1] = (byte)(v >> 8);
        b[at + 2] = (byte)(v >> 16);
        b[at + 3] = (byte)(v >> 24);
    }
}
