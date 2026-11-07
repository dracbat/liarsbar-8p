using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Mirror;
using Mirror.RemoteCalls;
using Steamworks;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>What a message is. Every number lives here, so two features can never share one.</summary>
internal enum ModKind : ushort
{
    // 1-15: the channel itself.
    Hello = 1,
    Welcome = 2,

    // 16-31: the loopback proof (ModNetProof).
    ProofPing = 16,
    ProofPong = 17,
    ProofState = 18,
    ProofThrow = 19,

    // Features: take a block of 16 from 32 upward and write it here.

    // 32-47: end-of-match awards (MatchStats, AwardPanel).
    Awards = 32,

    // 48-63: the Tab board (MatchBoard).
    Hands = 48,

    // 64-79: house rules (HouseRules).
    HouseRules = 64,

    // 80-95: the hidden tables (TwoCardPoker).
    HiddenTable = 80,
}

/// <summary><paramref name="from"/> is <see cref="ModNet.FromHost"/> on a client, and the sender's connection id on the host.</summary>
internal delegate void ModHandler(int from, ModReader body);

/// <summary>
/// Small messages between the host and the clients that both run the mod: house rules, awards,
/// anything a feature needs every copy at the table to agree on.
///
/// <b>Why a remote call of its own.</b> The channel is one Rpc and one Command, registered with
/// Mirror at run time under names no game call uses, and sent through Mirror's own send methods
/// on the player objects that already exist. That choice is about what happens to a copy that
/// receives one without knowing it. An unknown remote call costs that copy one line in its log
/// and nothing else; an unknown network message, or raw bytes on the transport, gets it thrown out
/// of the game, because this game disconnects on any message it cannot unpack. A player without
/// the mod, or with an older one, must never be thrown out by this.
///
/// <b>Nobody receives anything they did not ask for.</b> A client says hello first, and only if
/// the lobby says its host runs a release with the channel. The host only ever sends to
/// connections that said hello, and only with the targeted send: the broadcast one would reach
/// every observer, modded or not. So a player without the mod gets no channel traffic at all, and
/// ranked, which is a hosted server on another transport, never sees a byte of it.
///
/// <b>The frame</b> is the whole payload of the call, so Mirror's size prefix is its length: a
/// format byte, the kind as two bytes, then the body. Bodies only ever grow at the end, a kind a
/// copy does not know is dropped quietly (it came from a newer build), and <see cref="Level"/>
/// rises when a release adds something both ends must understand.
///
/// <b>Delivery.</b> Everything goes on the reliable channel, in order with the game's own remote
/// calls in each direction. Host to client cannot be lost while the connection lives. Client to
/// host can lose one message at the instant the host marks the client not ready for a scene
/// change, so a feature that cannot lose a client message has the host ask again.
///
/// <b>Trust.</b> On the host, <c>from</c> is somebody else's copy: check everything. On a client,
/// accept only <see cref="FromHost"/>. Send seat numbers, never names, and let each copy look the
/// name up itself, so names and Steam ids never travel here. Text that reaches the screen goes
/// through <see cref="ModReader.Plain"/>, into a field with rich text off.
///
/// Every feature must behave when the channel is shut, because that is the normal state at a
/// table with an older build: <see cref="Open"/> is false and the sends return false.
/// </summary>
internal static class ModNet
{
    // These three are the wire format. Changing one makes a different channel.
    private const string RpcName = "System.Void LiarsBar8P.ModNet::RpcToClient(System.Byte[])";
    private const string CmdName = "System.Void LiarsBar8P.ModNet::CmdToHost(System.Byte[])";
    private const byte Format = 0xB1;

    private const int HeaderSize = 3;
    private const int Reliable = 0;

    /// <summary>The most a body may be. Far inside every transport's packet, and far more than any feature needs.</summary>
    internal const int MaxBody = 4000;

    /// <summary>The sender, as a client sees it: the host.</summary>
    internal const int FromHost = -1;

    /// <summary>Raise when a release adds something that needs both ends to understand it.</summary>
    internal const ushort Level = 1;

    /// <summary>The first release with this channel. A host on anything older is never greeted.</summary>
    private static readonly System.Version FirstRelease = new(3, 0, 0);

    private const float HelloEvery = 3f;
    private const int MaxHellos = 10;
    private const int MaxOutbox = 16;
    private const float BucketSize = 40f;
    private const float BucketRefill = 20f;
    private const int MaxComplaints = 20;

    private static readonly int RpcHash = unchecked((int)Stable(RpcName));
    private static readonly int CmdHash = unchecked((int)Stable(CmdName));

    internal sealed class Peer
    {
        internal int Id;
        internal ushort Level;
        internal string Version;
        internal NetworkConnectionToClient Connection;
    }

    private static readonly Dictionary<ushort, ModHandler> _handlers = new();
    private static readonly Dictionary<int, Peer> _peers = new();
    private static readonly Dictionary<int, float[]> _buckets = new();
    private static readonly Dictionary<uint, byte[]> _kept = new();
    private static readonly List<int> _gone = new();
    private static readonly Queue<byte[]> _outbox = new();

    // Held here so the managed delegates live as long as Mirror's table points at them.
    private static Action<NetworkBehaviour, NetworkReader, NetworkConnectionToClient> _onRpc, _onCmd;
    private static RemoteCallDelegate _rpcCall, _cmdCall;

    private static bool _installed, _retried, _broken, _open, _hosting;
    private static IntPtr _connection;
    private static int _hellos, _complaints, _unknownKinds, _rateDropped;
    private static float _nextHello;

    /// <summary>The channel is up: on the host when it starts hosting, on a client when the host answers.</summary>
    internal static event Action Opened;

    /// <summary>Host: a connection said hello. Kept messages have already gone to it.</summary>
    internal static event Action<int> PeerJoined;

    /// <summary>Host: a connection that had the channel has gone.</summary>
    internal static event Action<int> PeerLeft;

    /// <summary>The host's level, as it said in its welcome. Zero until then.</summary>
    internal static ushort HostLevel { get; private set; }

    /// <summary>True when messages can flow on this copy.</summary>
    internal static bool Open => _installed && !_broken && (NetworkServer.active || _open);

    /// <summary>Host: has this connection said hello?</summary>
    internal static bool Hears(int connectionId) => _peers.ContainsKey(connectionId);

    /// <summary>Host: is every other connection a peer at this level or above?</summary>
    internal static bool EveryoneAt(ushort level)
    {
        if (!Open || !NetworkServer.active) return false;
        int local = LocalId();
        foreach (var pair in NetworkServer.connections)
        {
            if (pair.Key == local) continue;
            if (!_peers.TryGetValue(pair.Key, out var peer) || peer.Level < level) return false;
        }
        return true;
    }

    /// <summary>What to do with one kind of message. Once, at load, on every copy.</summary>
    internal static void On(ModKind kind, ModHandler handler) => _handlers[(ushort)kind] = handler;

    // ------------------------------------------------------------------ setup

    /// <summary>
    /// Register the two calls with Mirror. Early on purpose: Mirror keeps whichever call registers
    /// a number last, and the game's own calls register as their classes first load. Going first
    /// means that if a game update ever brings a call with the same number, the game's call wins
    /// and only this channel is lost, which <see cref="OursStill"/> notices at the next session.
    /// </summary>
    internal static void Install()
    {
        if (_installed) return;
        try
        {
            if (Muted())
            {
                Plugin.Log.LogWarning("[modnet] muted for this copy - it behaves like a build without the channel");
                _retried = true;
                return;
            }

            ushort rpc = (ushort)RpcHash, cmd = (ushort)CmdHash;
            if (RemoteProcedureCalls.GetDelegate(rpc) is not null || RemoteProcedureCalls.GetDelegate(cmd) is not null)
            {
                Plugin.Log.LogError("[modnet] a game call already uses our number - no channel");
                _retried = true;
                return;
            }

            _onRpc = OnRpc;
            _onCmd = OnCmd;
            _rpcCall = _onRpc;
            _cmdCall = _onCmd;

            // Against the base class, so every spawned object in the game can carry it.
            var carrier = Il2CppType.Of<NetworkBehaviour>();
            RemoteProcedureCalls.RegisterRpc(carrier, RpcName, _rpcCall);
            RemoteProcedureCalls.RegisterCommand(carrier, CmdName, _cmdCall, false);

            // Mirror numbers the calls itself, from the names. If it filed them under other
            // numbers than the ones sent below, nothing would ever arrive: say so now.
            _installed = true;
            if (!OursStill())
            {
                _installed = false;
                _retried = true;
                Plugin.Log.LogError("[modnet] Mirror did not file the calls under the numbers this sends - no channel");
                return;
            }

            Plugin.Log.LogInfo($"[modnet] ready, level {Level}, rpc {rpc:X4}, cmd {cmd:X4}");
        }
        catch (Exception e)
        {
            _installed = false;
            Plugin.Log.LogError($"[modnet] not available: {e.Message}");
        }
    }

    private static bool OursStill()
    {
        var rpc = RemoteProcedureCalls.GetDelegate((ushort)RpcHash);
        var cmd = RemoteProcedureCalls.GetDelegate((ushort)CmdHash);
        return rpc is not null && cmd is not null && _rpcCall is not null && _cmdCall is not null &&
               rpc.Pointer == _rpcCall.Pointer && cmd.Pointer == _cmdCall.Pointer;
    }

    /// <summary>Developer test switch: one side of a loopback test behaves like a build without the channel.</summary>
    private static bool Muted()
    {
        if (!Dev.Enabled) return false;
        string who = Environment.GetEnvironmentVariable("LIARSBAR8P_NET_MUTE");
        return (who == "host" && Loopback.Mine == Loopback.Role.Host) ||
               (who == "client" && Loopback.Mine == Loopback.Role.Client);
    }

    /// <summary>The number Mirror gives a call's name: 32-bit FNV-1a over the low byte of each character.</summary>
    private static uint Stable(string text)
    {
        unchecked
        {
            uint hash = 0x811C9DC5;
            foreach (char c in text) hash = (hash ^ (byte)c) * 0x01000193;
            return hash;
        }
    }

    // ------------------------------------------------------------------ the tick

    /// <summary>From <see cref="ModTicker"/>.</summary>
    internal static void Tick()
    {
        if (!_installed)
        {
            // One more try, if registering at load threw.
            if (!_retried) { _retried = true; Install(); }
            return;
        }

        WatchSession();
        if (_broken) return;

        if (NetworkServer.active) Prune();
        else if (NetworkClient.active) ClientTick();
    }

    /// <summary>
    /// A new session of any kind starts the channel from nothing: hosting, joining, or a host
    /// migration, which rebuilds every connection. One change is not a new session: a host's own
    /// client arriving. The server starts first and the host's client only attaches once the
    /// lobby scene has loaded, and taking that for a new session threw away everything kept in
    /// between, so a late arrival never got it.
    /// </summary>
    private static void WatchSession()
    {
        bool hosting = NetworkServer.active;
        var connection = NetworkClient.connection;
        IntPtr id = connection is null ? IntPtr.Zero : connection.Pointer;
        if (hosting == _hosting && id == _connection) return;

        if (hosting && _hosting && _connection == IntPtr.Zero)
        {
            _connection = id;
            return;
        }

        _hosting = hosting;
        _connection = id;
        _open = false;
        _hellos = 0;
        _nextHello = 0f;
        HostLevel = 0;
        _peers.Clear();
        _buckets.Clear();
        _kept.Clear();
        _outbox.Clear();

        if (!hosting && id == IntPtr.Zero) { _broken = false; return; }

        _broken = !OursStill();
        if (_broken)
        {
            Plugin.Log.LogError("[modnet] a game call has taken our number - no channel this session");
            return;
        }

        if (hosting) Announce();
    }

    private static void Announce()
    {
        try { Opened?.Invoke(); }
        catch (Exception e) { Complain($"an Opened listener threw: {e.Message}"); }
    }

    private static void Prune()
    {
        if (_peers.Count == 0) return;

        _gone.Clear();
        var live = NetworkServer.connections;
        foreach (var id in _peers.Keys)
            if (!live.ContainsKey(id)) _gone.Add(id);

        foreach (var id in _gone)
        {
            _peers.Remove(id);
            _buckets.Remove(id);
            Plugin.Log.LogInfo($"[modnet] connection {id} left");
            try { PeerLeft?.Invoke(id); }
            catch (Exception e) { Complain($"a PeerLeft listener threw: {e.Message}"); }
        }
    }

    private static void ClientTick()
    {
        if (!NetworkClient.ready) return;

        if (_open)
        {
            if (LocalCarrier() == null) return;
            while (_outbox.Count > 0) PushToHost(_outbox.Dequeue());
            return;
        }

        float now = Time.realtimeSinceStartup;
        if (now < _nextHello || _hellos >= MaxHellos) return;
        _nextHello = now + HelloEvery;

        if (LocalCarrier() == null || !HostHasChannel()) return;

        _hellos++;
        PushToHost(Build(ModKind.Hello, new ModWriter().U16(Level).Text(Plugin.Version, 24)));

        if (_hellos == MaxHellos)
            Plugin.Log.LogInfo("[modnet] the host never answered - no channel this session");
    }

    /// <summary>
    /// Whether greeting the host is worth doing. The order matters: nothing may touch Steam on the
    /// ranked screen, and ranked must never see a byte of this.
    /// </summary>
    private static bool HostHasChannel() => Gate(out _);

    private static bool Gate(out string why)
    {
        if (RankedGuard.Near) { why = "ranked"; return false; }
        if (Loopback.Active) { why = "loopback harness"; return true; }

        var lobby = SteamLobby.Instance;
        if (lobby == null || lobby.CurrentLobbyID == 0) { why = "not in a lobby"; return false; }

        var nm = lobby.manager;
        if (nm == null || nm.Matchmaking || nm.isVelvetRoom) { why = "not a lobby table"; return false; }

        // The lobby of the table being played, which after an invite accepted while seated is not
        // the one CurrentLobbyID names. Reading that one would greet a host who never asked.
        if (!Table.Lobby(out var id, out why)) return false;

        string theirs = SteamMatchmaking.GetLobbyData(id, VersionCheck.Key);
        if (string.IsNullOrEmpty(theirs))
            theirs = SteamMatchmaking.GetLobbyMemberData(id, SteamMatchmaking.GetLobbyOwner(id), VersionCheck.Key);

        bool has = System.Version.TryParse(theirs, out var version) && version >= FirstRelease;
        why = $"the lobby says v{Matchmaking.Clean(theirs, 16)}, the channel needs v{FirstRelease} or later";
        return has;
    }

    // ------------------------------------------------------------------ sending

    /// <summary>Host: to one connection. False if that connection has no channel.</summary>
    internal static bool SendTo(int connectionId, ModKind kind, ModWriter body)
    {
        if (!Open || !NetworkServer.active) return false;
        var frame = Build(kind, body);
        if (frame == null) return false;

        if (connectionId == LocalId()) { Dispatch(FromHost, frame); return true; }
        if (!_peers.TryGetValue(connectionId, out var peer)) return false;
        return PushToClient(peer.Connection, frame);
    }

    /// <summary>Host: to every peer, and to this copy.</summary>
    internal static bool SendToAll(ModKind kind, ModWriter body)
    {
        if (!Open || !NetworkServer.active) return false;
        var frame = Build(kind, body);
        if (frame == null) return false;
        Deliver(frame);
        return true;
    }

    /// <summary>
    /// Host: as <see cref="SendToAll"/>, and anyone arriving later gets it too. The same kind and
    /// slot replaces what was kept before. Use it for state; use <see cref="SendToAll"/> for events.
    /// </summary>
    internal static bool Keep(ModKind kind, ModWriter body, ushort slot = 0)
    {
        if (!Open || !NetworkServer.active) return false;
        var frame = Build(kind, body);
        if (frame == null) return false;
        _kept[((uint)kind << 16) | slot] = frame;
        Deliver(frame);
        return true;
    }

    internal static void Forget(ModKind kind, ushort slot = 0) => _kept.Remove(((uint)kind << 16) | slot);

    /// <summary>Client: to the host. On the host itself it arrives at once, from its own connection id.</summary>
    internal static bool SendToHost(ModKind kind, ModWriter body)
    {
        if (!Open) return false;
        var frame = Build(kind, body);
        if (frame == null) return false;

        if (NetworkServer.active) { Dispatch(LocalId(), frame); return true; }

        if (NetworkClient.ready && LocalCarrier() != null) return PushToHost(frame);

        // Not ready, during a scene change: held, and sent once ready again.
        if (_outbox.Count >= MaxOutbox) { Complain("the outbox is full - dropped a message"); return false; }
        _outbox.Enqueue(frame);
        return true;
    }

    private static void Deliver(byte[] frame)
    {
        foreach (var peer in _peers.Values) PushToClient(peer.Connection, frame);
        Dispatch(FromHost, frame);
    }

    private static int LocalId()
    {
        var local = NetworkServer.localConnection;
        return local is null ? 0 : local.connectionId;
    }

    private static byte[] Build(ModKind kind, ModWriter body)
    {
        int length = body?.Length ?? 0;
        if (length > MaxBody)
        {
            Complain($"kind {(ushort)kind} is {length} bytes and the limit is {MaxBody} - not sent");
            return null;
        }

        var frame = new byte[HeaderSize + length];
        frame[0] = Format;
        frame[1] = (byte)(ushort)kind;
        frame[2] = (byte)((ushort)kind >> 8);
        body?.CopyTo(frame, HeaderSize);
        return frame;
    }

    private static NetworkBehaviour LocalCarrier()
    {
        var me = NetworkClient.localPlayer;
        if (me == null) return null;
        var parts = me.NetworkBehaviours;
        return parts != null && parts.Length > 0 ? parts[0] : null;
    }

    private static bool PushToHost(byte[] frame)
    {
        var carrier = LocalCarrier();
        if (carrier == null || frame == null) return false;

        var writer = NetworkWriterPool.Get();
        try
        {
            writer.WriteBytes(new Il2CppStructArray<byte>(frame), 0, frame.Length);
            carrier.SendCommandInternal(CmdName, CmdHash, writer, Reliable, false);
            return true;
        }
        finally { NetworkWriterPool.Return(writer); }
    }

    private static bool PushToClient(NetworkConnectionToClient conn, byte[] frame)
    {
        var who = conn?.identity;
        if (who == null || frame == null) return false;   // connected with no player object: a stray
        var parts = who.NetworkBehaviours;
        if (parts == null || parts.Length == 0) return false;

        var writer = NetworkWriterPool.Get();
        try
        {
            writer.WriteBytes(new Il2CppStructArray<byte>(frame), 0, frame.Length);
            parts[0].SendTargetRPCInternal(conn, RpcName, RpcHash, writer, Reliable);
            return true;
        }
        finally { NetworkWriterPool.Return(writer); }
    }

    // ------------------------------------------------------------------ receiving

    private static void OnRpc(NetworkBehaviour carrier, NetworkReader reader, NetworkConnectionToClient sender)
    {
        try
        {
            if (_broken || NetworkServer.active) return;   // a host delivers to itself directly
            var frame = Take(reader);
            if (frame == null) return;

            if (KindOf(frame) == (ushort)ModKind.Welcome) { Welcomed(frame); return; }
            if (_open) Dispatch(FromHost, frame);
        }
        catch (Exception e) { Complain($"a message from the host: {e.Message}"); }
    }

    private static void OnCmd(NetworkBehaviour carrier, NetworkReader reader, NetworkConnectionToClient sender)
    {
        try
        {
            if (_broken || sender is null || !NetworkServer.active) return;

            int id = sender.connectionId;
            if (!Allowed(id)) return;

            var frame = Take(reader);
            if (frame == null) return;

            if (KindOf(frame) == (ushort)ModKind.Hello) { Greet(sender, frame); return; }
            if (_peers.ContainsKey(id)) Dispatch(id, frame);
        }
        catch (Exception e) { Complain($"a message from a client: {e.Message}"); }
    }

    /// <summary>A token bucket per connection. Too much is dropped and counted, and never costs anybody the connection.</summary>
    private static bool Allowed(int id)
    {
        float now = Time.realtimeSinceStartup;
        if (!_buckets.TryGetValue(id, out var bucket))
            _buckets[id] = bucket = new[] { BucketSize, now };

        bucket[0] = Math.Min(BucketSize, bucket[0] + (now - bucket[1]) * BucketRefill);
        bucket[1] = now;

        if (bucket[0] < 1f) { _rateDropped++; return false; }
        bucket[0] -= 1f;
        return true;
    }

    private static ushort KindOf(byte[] frame) => (ushort)(frame[1] | frame[2] << 8);

    /// <summary>The frame out of the call's payload, or null. The size is checked before anything is allocated.</summary>
    private static byte[] Take(NetworkReader reader)
    {
        int length = reader.Remaining;
        if (length < HeaderSize || length > HeaderSize + MaxBody)
        {
            Complain($"ignored a frame of {length} bytes");
            return null;
        }

        var native = new Il2CppStructArray<byte>(length);
        reader.ReadBytes(native, length);
        byte[] frame = native;

        if (frame[0] != Format)
        {
            Complain($"ignored a frame in format {frame[0]:X2}");
            return null;
        }
        return frame;
    }

    private static void Greet(NetworkConnectionToClient conn, byte[] frame)
    {
        int id = conn.connectionId;
        if (_peers.ContainsKey(id)) return;   // a retry that crossed the welcome

        var body = new ModReader(frame, HeaderSize);
        var peer = new Peer
        {
            Id = id,
            Level = body.U16(),
            Version = ModReader.Plain(body.Text(), 24),
            Connection = conn
        };
        if (body.Failed) { Complain($"connection {id} sent a broken hello"); return; }

        _peers[id] = peer;
        Plugin.Log.LogInfo($"[modnet] connection {id} has the channel, level {peer.Level}, v{peer.Version}");

        PushToClient(conn, Build(ModKind.Welcome, new ModWriter().U16(Level).Text(Plugin.Version, 24).I32(id)));
        foreach (var kept in _kept.Values) PushToClient(conn, kept);

        try { PeerJoined?.Invoke(id); }
        catch (Exception e) { Complain($"a PeerJoined listener threw: {e.Message}"); }
    }

    private static void Welcomed(byte[] frame)
    {
        if (_open) return;

        var body = new ModReader(frame, HeaderSize);
        ushort level = body.U16();
        string version = ModReader.Plain(body.Text(), 24);
        if (body.Failed) { Complain("the host sent a broken welcome"); return; }

        HostLevel = level;
        _open = true;
        Plugin.Log.LogInfo($"[modnet] the host has the channel, level {level}, v{version}");
        Announce();
    }

    private static void Dispatch(int from, byte[] frame)
    {
        ushort kind = KindOf(frame);
        if (!_handlers.TryGetValue(kind, out var handler))
        {
            _unknownKinds++;   // from a newer build: not a fault
            return;
        }

        try { handler(from, new ModReader(frame, HeaderSize)); }
        catch (Exception e) { Complain($"the handler for kind {kind} threw: {e.Message}"); }
    }

    private static void Complain(string what)
    {
        if (_complaints >= MaxComplaints) return;
        _complaints++;
        Plugin.Log.LogWarning($"[modnet] {what}");
    }

    // ------------------------------------------------------------------ test hooks

    internal static int RateDropped => _rateDropped;
    internal static int UnknownKinds => _unknownKinds;

    /// <summary>For the test driver: what the gate reads on this copy, and what it decides.</summary>
    internal static string DescribeGate()
    {
        bool yes = Gate(out string why);
        return $"installed={_installed} open={Open} gate={(yes ? "yes" : "no")} ({why})";
    }

    /// <summary>Proof only: bytes exactly as given, past every check on this side.</summary>
    internal static void RawToHostForProof(byte[] frame) => PushToHost(frame);

    /// <summary>Proof only: to a connection that may never have said hello.</summary>
    internal static void ForceToClientForProof(NetworkConnectionToClient conn, ModKind kind) =>
        PushToClient(conn, Build(kind, null));
}

/// <summary>Builds a message body. Bodies only ever grow at the end.</summary>
internal sealed class ModWriter
{
    private readonly MemoryStream _stream = new(64);

    internal int Length => (int)_stream.Length;

    internal ModWriter U8(byte value) { _stream.WriteByte(value); return this; }

    internal ModWriter U16(ushort value)
    {
        _stream.WriteByte((byte)value);
        _stream.WriteByte((byte)(value >> 8));
        return this;
    }

    internal ModWriter I32(int value)
    {
        for (int shift = 0; shift < 32; shift += 8) _stream.WriteByte((byte)(value >> shift));
        return this;
    }

    /// <summary>One length byte, then UTF-8, cut on a character boundary and never inside one.</summary>
    internal ModWriter Text(string value, int maxBytes)
    {
        maxBytes = Math.Min(maxBytes, 255);
        value ??= "";

        int chars = value.Length;
        while (chars > 0 && Encoding.UTF8.GetByteCount(value.AsSpan(0, chars)) > maxBytes) chars--;
        if (chars > 0 && char.IsHighSurrogate(value[chars - 1])) chars--;

        var bytes = Encoding.UTF8.GetBytes(value.Substring(0, chars));
        _stream.WriteByte((byte)bytes.Length);
        _stream.Write(bytes, 0, bytes.Length);
        return this;
    }

    internal void CopyTo(byte[] into, int at) => Array.Copy(_stream.GetBuffer(), 0, into, at, Length);
}

/// <summary>Reads a body. Never throws: past the end it gives zeroes and sets <see cref="Failed"/>.</summary>
internal sealed class ModReader
{
    private readonly byte[] _bytes;
    private int _at;

    internal ModReader(byte[] bytes, int at) { _bytes = bytes; _at = at; }

    internal bool Failed { get; private set; }
    internal int Remaining => _bytes.Length - _at;

    private bool Need(int count)
    {
        if (Remaining >= count) return true;
        Failed = true;
        _at = _bytes.Length;
        return false;
    }

    internal byte U8() => Need(1) ? _bytes[_at++] : (byte)0;

    internal ushort U16()
    {
        if (!Need(2)) return 0;
        int value = _bytes[_at] | _bytes[_at + 1] << 8;
        _at += 2;
        return (ushort)value;
    }

    internal int I32()
    {
        if (!Need(4)) return 0;
        int value = _bytes[_at] | _bytes[_at + 1] << 8 | _bytes[_at + 2] << 16 | _bytes[_at + 3] << 24;
        _at += 4;
        return value;
    }

    internal string Text()
    {
        int length = U8();
        if (!Need(length)) return "";
        string value = Encoding.UTF8.GetString(_bytes, _at, length);
        _at += length;
        return value;
    }

    /// <summary>
    /// Text somebody else chose, made safe to draw: one line, no markup, short. The same cleaning
    /// the lobby list gives names. The field it lands in must still have rich text off.
    /// </summary>
    internal static string Plain(string raw, int max) => Matchmaking.Clean(raw, max);
}
