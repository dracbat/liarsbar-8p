using System;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Developer only: proves <see cref="ModNet"/> between real copies of the game in the loopback
/// harness. Needs developer mode and <c>LIARSBAR8P_NETPROOF</c>, and does nothing in a normal game.
///
///   1           both ends have the channel: the handshake, kept state reaching a late arrival,
///               thirty messages in order at every size up to the limit, one more across the
///               scene change into the match, a handler that throws, and rubbish from the client.
///               None of it may cost the connection.
///   force       the host also sends to every connection whether it said hello or not. With
///               <c>LIARSBAR8P_NET_MUTE=client</c> that is a client with no handler at all.
///
/// <c>LIARSBAR8P_NET_MUTE=host</c> with mode 1 is the other way round: a host without the channel
/// being greeted by a client that has it. Every line says PASS or FAIL, under <c>[netproof]</c>.
/// </summary>
internal static class ModNetProof
{
    private const int Count = 30;   // under the host's burst allowance of 40
    private static readonly int[] Sizes = { 0, 1, 100, 1000, ModNet.MaxBody - 8 };

    /// <summary>Force mode: after a connection is seen, then every this many seconds, this many times.</summary>
    private const float ForceFirst = 2f;
    private const float ForceEvery = 20f;
    private const int ForceTimes = 3;

    private static string _mode;
    private static int _nextPing, _nextPong, _dropsSaid, _forced;
    private static bool _kept, _sceneSent, _noiseSent;
    private static float _forceAt = -1f;

    private static void Say(string line) => Plugin.Log.LogWarning($"[netproof] {line}");

    internal static void Install()
    {
        // It sends test traffic to every connection, so it works only inside the loopback harness.
        if (!Fair.TestsAllowed) return;
        _mode = Environment.GetEnvironmentVariable("LIARSBAR8P_NETPROOF");
        if (string.IsNullOrEmpty(_mode)) return;

        ModNet.On(ModKind.ProofPing, OnPing);
        ModNet.On(ModKind.ProofPong, OnPong);
        ModNet.On(ModKind.ProofState, (from, body) => Say($"PASS kept state arrived: {body.Text()}"));
        ModNet.On(ModKind.ProofThrow, (from, body) => throw new InvalidOperationException("on purpose"));
        ModNet.PeerJoined += Burst;
        Say($"on, mode '{_mode}'");
    }

    private static void Burst(int connection)
    {
        Say($"PASS hello from connection {connection}");
        for (int seq = 0; seq < Count; seq++) Ping(connection, seq);
        ModNet.SendTo(connection, ModKind.ProofThrow, null);
    }

    private static void Ping(int connection, int seq)
    {
        int size = Sizes[seq % Sizes.Length];
        var body = new ModWriter().I32(seq).I32(size);
        for (int i = 0; i < size; i++) body.U8((byte)(seq + i));
        if (!ModNet.SendTo(connection, ModKind.ProofPing, body)) Say($"FAIL could not send ping {seq}");
    }

    private static void OnPing(int from, ModReader body)
    {
        if (from != ModNet.FromHost) { Say($"FAIL a ping from {from}, not the host"); return; }

        int seq = body.I32(), size = body.I32();
        bool good = seq == _nextPing && body.Remaining == size;
        for (int i = 0; good && i < size; i++) good = body.U8() == (byte)(seq + i);
        _nextPing = seq + 1;
        if (!good || body.Failed) Say($"FAIL ping {seq} ({size} bytes) arrived wrong");
        if (seq == Count) Say("ping across the scene change arrived intact");

        ModNet.SendToHost(ModKind.ProofPong, new ModWriter().I32(seq));
    }

    private static void OnPong(int from, ModReader body)
    {
        int seq = body.I32();
        if (seq != _nextPong) Say($"FAIL pong {seq}, wanted {_nextPong}");
        _nextPong = seq + 1;
        if (_nextPong == Count) Say($"PASS {Count} messages in order, sizes up to {ModNet.MaxBody}");
        if (_nextPong == Count + 1) Say("PASS across the scene change, both ways");
    }

    /// <summary>From <see cref="ModTicker"/>.</summary>
    internal static void Tick()
    {
        if (string.IsNullOrEmpty(_mode)) return;
        float now = Time.realtimeSinceStartup;

        if (NetworkServer.active)
        {
            // Kept before anybody has said hello, so a client can only get it by catching up.
            if (!_kept && ModNet.Open)
            {
                _kept = true;
                ModNet.Keep(ModKind.ProofState, new ModWriter().Text("kept before you arrived", 64));
            }

            if (!_sceneSent && NetworkServer.isLoadingScene && _nextPong >= Count)
            {
                _sceneSent = true;
                foreach (var pair in NetworkServer.connections)
                    if (ModNet.Hears(pair.Key)) Ping(pair.Key, Count);
                Say("sent one during the scene load");
            }

            if (_mode == "force") Force(now);

            if (ModNet.RateDropped != _dropsSaid)
            {
                _dropsSaid = ModNet.RateDropped;
                Say($"rate limit has dropped {_dropsSaid}, unknown kinds {ModNet.UnknownKinds}");
            }
            return;
        }

        // Client: rubbish after the good traffic. Nothing here may cost the connection.
        if (!_noiseSent && ModNet.Open && _nextPing >= Count)
        {
            _noiseSent = true;
            ModNet.RawToHostForProof(new byte[] { 0x01 });
            ModNet.RawToHostForProof(new byte[] { 0x00, 0x00, 0x00 });
            ModNet.RawToHostForProof(new byte[] { 0xB1, 0xFF, 0xFF });
            ModNet.RawToHostForProof(new byte[5000]);
            for (int i = 0; i < 200; i++) ModNet.RawToHostForProof(new byte[] { 0xB1, 0xFE, 0xFF });
            Say("sent the rubbish");
        }
    }

    /// <summary>
    /// A call at every other connection, hello or not: once soon after one has a player, in the
    /// lobby, and twice more later, which with the harness starting the match lands in it.
    /// </summary>
    private static void Force(float now)
    {
        if (_forced >= ForceTimes) return;

        if (_forceAt < 0f)
        {
            foreach (var pair in NetworkServer.connections)
                if (pair.Key != 0 && pair.Value != null && pair.Value.identity != null) { _forceAt = now + ForceFirst; break; }
            return;
        }
        if (now < _forceAt) return;

        _forceAt = now + ForceEvery;
        _forced++;
        int sent = 0;
        foreach (var pair in NetworkServer.connections)
        {
            if (pair.Key == 0 || pair.Value == null || pair.Value.identity == null) continue;
            ModNet.ForceToClientForProof(pair.Value, ModKind.ProofThrow);
            sent++;
        }
        Say($"forced call {_forced} of {ForceTimes} at {sent} connection(s), said hello or not (heard: {Heard()})");
    }

    private static string Heard()
    {
        var ids = new System.Text.StringBuilder();
        foreach (var pair in NetworkServer.connections)
            if (pair.Key != 0) ids.Append(ids.Length > 0 ? " " : "").Append(pair.Key).Append(ModNet.Hears(pair.Key) ? "=yes" : "=no");
        return ids.ToString();
    }
}
