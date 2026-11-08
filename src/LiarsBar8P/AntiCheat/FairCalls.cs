using System;
using System.Collections.Generic;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Fair play for the liar and spot-on calls of Liar's Deck, the Chaos deck and Liar's Dice.
///
/// At a table this copy hosts, a call from a player's copy is played only when the calling seat
/// is in play and holds the turn, as the rules say. A call just after the turn moved on, or from a
/// seat that is out or on its way out, is refused quietly: the call is dropped and the round goes
/// on, and nobody is removed for it. The game's own calls (bots, the host's own seat, the
/// game's own automatic call) are never checked, and an accepted call runs the game's own handler.
///
/// In Liar's Dice and Liar's Spin a call leaves the caller's copy as two commands, one straight
/// after the other: the announcement everybody hears ("LIAR!" or "SPOT ON!"), then the call. The
/// announcement's own handler ends the caller's turn on the host, so the call that follows arrives
/// from a seat that no longer holds it. The host notes a seat that announces a call on its turn
/// (<see cref="Announced"/>), and that seat's call a moment later counts as made on that turn.
/// Liar's Deck and the Chaos deck send the call alone.
/// </summary>
[FairPatch("liar and spot-on calls only from a seat in play, on its turn")]
[HarmonyPatch]
internal static class FairCalls
{
    internal static FairThrows.Verdict Judge(bool dead, bool longDead, bool turn, out string why)
    {
        if (longDead) { why = "a call from a seat out of the game since before this deal"; return FairThrows.Verdict.Refuse; }
        if (dead) { why = "a call from a seat that is out"; return FairThrows.Verdict.Refuse; }
        if (!turn) { why = "a call out of turn"; return FairThrows.Verdict.Refuse; }
        why = "a call on the caller's turn";
        return FairThrows.Verdict.Accept;
    }

    private static bool Check(NetworkBehaviour obj, NetworkConnectionToClient sender, string what, Announced.Kind kind)
    {
        try
        {
            if (!Fair.Hosting || !Fair.IsRemote(sender) || obj == null) return true;
            var owner = obj.connectionToClient;
            if (owner == null || owner.connectionId != sender.connectionId) return true;
            var p = obj.GetComponent<PlayerStats>();
            if (p == null) return true;

            bool dead = p.Dead || HostRevolver.Falling(obj);
            bool announced = Announced.Take(p, kind);
            var v = Judge(dead, FairHands.LongDead(p), p.HaveTurn || announced, out string why);
            switch (v)
            {
                case FairThrows.Verdict.Caught:
                    Fair.Caught(sender, $"{what} (seat {p.Slot}): {why}");
                    return false;
                case FairThrows.Verdict.Refuse:
                    Fair.Refused($"connection {sender.connectionId} {what} (seat {p.Slot}): {why}");
                    return false;
                default:
                    return true;
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"{Fair.Tag} {what} not checked: {e.Message}");
            return true;
        }
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.InvokeUserCode_CmdCallLiar))]
    private static bool DeckLiar(NetworkBehaviour obj, NetworkConnectionToClient senderConnection) =>
        Check(obj, senderConnection, "Liar's Deck liar call", Announced.Kind.Liar);

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.InvokeUserCode_CmdCallSpotOn))]
    private static bool DeckSpotOn(NetworkBehaviour obj, NetworkConnectionToClient senderConnection) =>
        Check(obj, senderConnection, "Liar's Deck spot-on call", Announced.Kind.SpotOn);

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.InvokeUserCode_CmdCallLiar))]
    private static bool ChaosLiar(NetworkBehaviour obj, NetworkConnectionToClient senderConnection) =>
        Check(obj, senderConnection, "Chaos deck liar call", Announced.Kind.Liar);

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(DiceGamePlay), nameof(DiceGamePlay.InvokeUserCode_CallLier))]
    private static bool DiceLiar(NetworkBehaviour obj, NetworkConnectionToClient senderConnection) =>
        Check(obj, senderConnection, "Liar's Dice liar call", Announced.Kind.Liar);

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(DiceGamePlay), nameof(DiceGamePlay.InvokeUserCode_CallSpotOn))]
    private static bool DiceSpotOn(NetworkBehaviour obj, NetworkConnectionToClient senderConnection) =>
        Check(obj, senderConnection, "Liar's Dice spot-on call", Announced.Kind.SpotOn);

    // ------------------------------------------------------------------ the announcements

    // The handlers below have the game's announcement compiled into them, so they are what is
    // patched. A prefix runs before the game ends the caller's turn.

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(DiceGamePlay), nameof(DiceGamePlay.InvokeUserCode_PlayLiarCMD))]
    private static void DiceLiarAnnounced(NetworkBehaviour obj, NetworkConnectionToClient senderConnection) =>
        Announced.Note(obj, senderConnection, Announced.Kind.Liar);

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(DiceGamePlay), nameof(DiceGamePlay.InvokeUserCode_PlaySpotOnCMD))]
    private static void DiceSpotOnAnnounced(NetworkBehaviour obj, NetworkConnectionToClient senderConnection) =>
        Announced.Note(obj, senderConnection, Announced.Kind.SpotOn);

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(SpinGamePlay), nameof(SpinGamePlay.InvokeUserCode_PlayLiarCMD))]
    private static void SpinLiarAnnounced(NetworkBehaviour obj, NetworkConnectionToClient senderConnection) =>
        Announced.Note(obj, senderConnection, Announced.Kind.Liar);

    /// <summary>
    /// The seats that announced a call on their turn, on the host: which turn they held
    /// (<see cref="FairTurnCount"/>) and when. The call that follows counts as made on that turn when
    /// it is the same kind of call, from the same seat, on the same turn, within
    /// <see cref="Window"/> seconds, and only once. A seat that announces without holding the turn is
    /// noted for nothing, so its call is judged exactly as before. The announcement and the call
    /// leave the caller's copy together; the window only allows for a slow connection.
    /// </summary>
    internal static class Announced
    {
        internal enum Kind { Liar, SpotOn }

        internal const float Window = 5f;

        private static readonly Dictionary<uint, (int Turn, float At, Kind What)> _seen = new();
        private static int _match = -1;

        /// <summary>The test driver's "old": every announcement forgotten, as before 3.0.2.</summary>
        internal static bool OffForTest { get; set; }

        /// <summary>A remote player's announcement, before the game's handler ends the turn.</summary>
        internal static void Note(NetworkBehaviour obj, NetworkConnectionToClient sender, Kind what)
        {
            try
            {
                if (!Fair.Hosting || !Fair.IsRemote(sender) || obj == null) return;
                var owner = obj.connectionToClient;
                if (owner == null || owner.connectionId != sender.connectionId) return;
                var p = obj.GetComponent<PlayerStats>();
                if (p == null || p.Dead || !p.HaveTurn) return;
                Fresh();
                _seen[p.netId] = (FairTurnCount.Of(p), Time.unscaledTime, what);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"{Fair.Tag} an announced call was not noted: {e.Message}"); }
        }

        /// <summary>Whether this seat announced this call on the turn it held, a moment ago. Used once.</summary>
        internal static bool Take(PlayerStats p, Kind what)
        {
            if (p == null) return false;
            Fresh();
            if (!_seen.TryGetValue(p.netId, out var seen)) return false;
            _seen.Remove(p.netId);
            if (OffForTest) return false;
            return seen.What == what && seen.Turn == FairTurnCount.Of(p) && Time.unscaledTime - seen.At <= Window;
        }

        private static void Fresh()
        {
            if (_match == Fair.Match) return;
            _match = Fair.Match;
            _seen.Clear();
        }
    }

    // ------------------------------------------------------------------ developer test

    private static void Register()
    {
        Fair.OnDrive("callorder", CallOrder.Arm);
        Fair.OnTick("call order test", CallOrder.Tick);
    }

    /// <summary>
    /// The test driver's "fair callorder &lt;dice|spoton|spin&gt; [old] [connection]", inside the
    /// loopback harness only. At the next moment a remote player's seat may call, the host runs that
    /// seat's two commands through the game's own handlers, as that player's connection and in the
    /// order its copy sends them: the announcement, then the call. It reports whether the call was
    /// played. With "old" the announcement is forgotten, as before 3.0.2: the call is then refused and
    /// nobody holds the turn, so the test gives the seat its turn back and the table plays on.
    /// </summary>
    private static class CallOrder
    {
        private static string _what;   // dice, spoton or spin; null when not armed
        private static bool _old;
        private static string _conn = "";
        private static float _until;

        internal static void Arm(string rest)
        {
            var parts = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string what = parts.Length > 0 ? parts[0] : "";
            if (what != "dice" && what != "spoton" && what != "spin")
            {
                Plugin.Log.LogWarning($"{Fair.Tag} test: fair callorder <dice|spoton|spin> [old] [connection]");
                return;
            }
            _old = false;
            _conn = "";
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i] == "old") _old = true;
                else _conn = parts[i];
            }
            _what = what;
            _until = Time.unscaledTime + 180f;
            Plugin.Log.LogInfo($"{Fair.Tag} test: {Name()} armed, waiting for a remote seat that may call");
        }

        private static string Name() => $"callorder {_what}{(_old ? " old" : "")}";

        internal static void Tick()
        {
            if (_what == null) return;
            if (!Fair.TestsAllowed) { _what = null; return; }
            if (Time.unscaledTime > _until)
            {
                Fair.Result(Name(), false, "no moment to call came within 3 minutes");
                _what = null;
                return;
            }
            if (!Fair.Hosting) return;

            var conn = Fair.TestConnection(_conn);
            var m = Manager.Instance;
            if (conn == null || m == null || m.Players == null) return;
            PlayerStats seat = null;
            foreach (var p in m.Players)
                if (p != null && p.connectionToClient != null && p.connectionToClient.connectionId == conn.connectionId) { seat = p; break; }
            if (seat == null || seat.Dead || !seat.HaveTurn) return;

            try
            {
                if (_what == "spin") Spin(conn, seat);
                else Dice(conn, seat, _what == "spoton");
            }
            catch (Exception e)
            {
                Fair.Result(Name(), false, $"the test failed: {e.Message}");
                _what = null;
            }
        }

        private static void Dice(NetworkConnectionToClient conn, PlayerStats seat, bool spotOn)
        {
            var d = Manager.Instance.DiceGame;
            var gp = seat.GetComponent<DiceGamePlay>();
            if (d == null || gp == null) return;
            if (!d.BetPlaced || d.CalledLiar || d.CalledSpotOn || d.DrinkPhaseActive) return;
            if (d.LastBidPlayer == null || d.LastBidPlayer.netId == seat.netId) return;
            if (spotOn && !d.CanSpotOn) return;

            string test = Name();
            _what = null;
            int refusedBefore = Fair.RefusedCount;
            Run(conn, reader =>
            {
                if (spotOn)
                {
                    DiceGamePlay.InvokeUserCode_PlaySpotOnCMD(gp, reader, conn);
                    DiceGamePlay.InvokeUserCode_CallSpotOn(gp, reader, conn);
                }
                else
                {
                    DiceGamePlay.InvokeUserCode_PlayLiarCMD(gp, reader, conn);
                    DiceGamePlay.InvokeUserCode_CallLier(gp, reader, conn);
                }
            });
            Report(test, seat, spotOn ? d.CalledSpotOn : d.CalledLiar, Fair.RefusedCount - refusedBefore);
        }

        private static void Spin(NetworkConnectionToClient conn, PlayerStats seat)
        {
            var s = Manager.Instance.SpinGame;
            var gp = seat.GetComponent<SpinGamePlay>();
            if (s == null || gp == null) return;
            if (!s.CanGiveBid || s.isCallingLiar || s.CalledLiar || !s.isBetGivenBefore) return;
            if (s.LastShowsCount <= 0 || s.LastShowsCount >= 5) return;
            var last = s.LastBetPlayer;
            if (last == null || last.GetInstanceID() == gp.gameObject.GetInstanceID()) return;

            string test = Name();
            _what = null;
            int refusedBefore = Fair.RefusedCount;
            Run(conn, reader =>
            {
                SpinGamePlay.InvokeUserCode_PlayLiarCMD(gp, reader, conn);
                SpinGamePlay.InvokeUserCode_CallLiar(gp, reader, conn);
            });
            Report(test, seat, s.isCallingLiar || s.CalledLiar, Fair.RefusedCount - refusedBefore);
        }

        /// <summary>The two commands, as the player's own connection; neither carries anything to read.</summary>
        private static void Run(NetworkConnectionToClient conn, Action<NetworkReader> body)
        {
            bool wasOff = Announced.OffForTest;
            Announced.OffForTest = _old;
            var writer = NetworkWriterPool.Get();
            var reader = NetworkReaderPool.Get(writer.ToArray());
            try { Fair.AsSentBy(conn, () => body(reader)); }
            finally
            {
                NetworkReaderPool.Return(reader);
                NetworkWriterPool.Return(writer);
                Announced.OffForTest = wasOff;
            }
        }

        private static void Report(string test, PlayerStats seat, bool played, int refused)
        {
            if (!_old)
            {
                Fair.Result(test, played && refused == 0,
                    $"seat {seat.Slot}'s announcement, then its call, as its own connection: " +
                    $"{(played ? "the call was played" : "the call was NOT played")}, {refused} refused");
                return;
            }

            bool asBefore = !played && refused > 0 && !seat.HaveTurn;
            if (!played)
            {
                try { seat.NetworkHaveTurn = true; }
                catch (Exception e) { Plugin.Log.LogWarning($"{Fair.Tag} test: the turn could not be given back: {e.Message}"); }
            }
            Fair.Result(test, asBefore,
                $"announcement forgotten, as before 3.0.2: {(played ? "the call was played" : "the call was refused")}, " +
                $"{refused} refused{(played ? "" : $"; seat {seat.Slot}'s turn given back so the table plays on")}");
        }
    }
}
