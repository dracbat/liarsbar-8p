using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// A backstop for a round of Liar's Dice whose reveal never finishes.
///
/// <b>The fault this was written for is fixed.</b> Above four players, resolving a liar call
/// used to throw <c>IndexOutOfRangeException</c> part way through
/// <c>DiceGamePlayManager.ShowPlayer</c>, and that ended the round: the coroutine stopped where
/// it threw, so no dice were shown, no loser was chosen, nobody was given the turn, and the
/// table sat there. The cause was a four-player array built inside the routine, and it is now
/// rewritten along with the seven deals in <see cref="DealArrayPatch"/> - measured at zero
/// faults across a round of liar calls and spot-on calls at five players, where before every
/// single call killed the table.
///
/// This is kept because it is not the same thing as knowing the reveal can never stop again.
/// It is a coroutine several minutes of animation long, running on the machine that is also
/// hosting seven other copies of the game, and it is reached through Mirror; a mod cannot
/// promise it will always complete. What a mod can do is notice when it has not and put the
/// round back on its feet, which costs nothing while everything is working.
///
/// The recovery is the game's own: <c>StopRevealFlowAndRecoverRound</c>, which it calls itself
/// when somebody leaves in the middle of a reveal. It stops the coroutines, clears the liar and
/// spot-on flags, resets the dice panel, re-rolls, hands the turn on and restarts the countdown.
/// The round continues and what is lost is that call's reveal - the dice are re-rolled rather
/// than shown, so nobody is punished for that particular lie. A poor outcome, and a much better
/// one than a table nobody can leave except by quitting.
///
/// <b>How it knows.</b> Two ways. BepInEx sees every line the game logs, so an array fault
/// during a reveal is caught within a tick of being thrown. A timeout backs that up for a
/// reveal that stops without saying anything, set well above how long a healthy reveal takes -
/// about fifty-five seconds at four players, measured, and longer with more people to show - so
/// it cannot cut a working one short.
///
/// This is not a developer tool, and it is not off in a release. It has nothing to do when the
/// game is behaving, and the day it does have something to do is the day somebody is mid-match.
/// </summary>
internal static class DiceRevealGuard
{
    /// <summary>
    /// Longest a reveal is allowed to take before it is treated as dead.
    ///
    /// A liar reveal at four players takes about fifty-five seconds, measured from the call to
    /// the next bid, because it shows every player's dice one at a time. More people means more
    /// to show, so this grows with the table rather than being one number that is either too
    /// tight for eight or useless for four.
    /// </summary>
    private static float Patience(int seats) => 90f + 12f * Math.Max(0, seats - Limits.VanillaPlayers);

    /// <summary>When an array fault was last logged, in ticks. Written from BepInEx's log thread.</summary>
    private static long _faultAt;

    private static float _callStartedAt = -1f;
    private static bool _wasResolving;
    private static int _recovered;

    // ------------------------------------------------------------------ watching the log

    private sealed class Listener : ILogListener
    {
        public LogLevel LogLevelFilter => LogLevel.All;

        public void LogEvent(object sender, LogEventArgs e)
        {
            try
            {
                if (e == null || e.Data == null) return;
                string text = e.Data.ToString();
                if (text == null || text.IndexOf("IndexOutOfRangeException", StringComparison.Ordinal) < 0) return;

                // Written rather than acted on: this arrives on whatever thread logged it, and
                // touching Unity from there is not allowed. The tick reads it.
                System.Threading.Interlocked.Exchange(ref _faultAt, DateTime.UtcNow.Ticks);
            }
            catch { }
        }

        public void Dispose() { }
    }

    private static bool _listening;

    internal static void Listen()
    {
        if (_listening) return;
        try
        {
            BepInEx.Logging.Logger.Listeners.Add(new Listener());
            _listening = true;
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[dicefix] could not watch the log: {e.Message}"); }
    }

    // ----------------------------------------------------------------------- the watchdog

    internal static void Tick()
    {
        try
        {
            if (!Mirror.NetworkServer.active) { _callStartedAt = -1f; return; }

            var m = Manager.Instance;
            if (m == null || !m.GameStarted) { _callStartedAt = -1f; return; }

            // From the component on the players. Every mode's manager object is awake in every
            // match, so asking whether the dice manager exists would be asking nothing.
            if (TableHand.Playing() != TableHand.Kind.Dice) { _callStartedAt = -1f; return; }

            var d = m.DiceGame;
            if (d == null) { _callStartedAt = -1f; return; }

            bool resolving;
            try { resolving = d.CalledLiar || d.CalledSpotOn; }
            catch { return; }

            if (!resolving)
            {
                _callStartedAt = -1f;
                _wasResolving = false;
                return;
            }

            if (!_wasResolving)
            {
                _wasResolving = true;
                _callStartedAt = Time.time;
                System.Threading.Interlocked.Exchange(ref _faultAt, 0);   // only faults from now on count
                return;
            }

            int seats = AimRing.Seats(m);
            float waited = Time.time - _callStartedAt;

            bool threw = System.Threading.Interlocked.Read(ref _faultAt) != 0;
            bool tooLong = waited > Patience(seats);

            if (!threw && !tooLong) return;

            _wasResolving = false;
            _callStartedAt = -1f;
            System.Threading.Interlocked.Exchange(ref _faultAt, 0);

            Plugin.Log.LogWarning(
                threw
                    ? $"[dicefix] the reveal threw while showing the dice at a table of {seats} - " +
                      "putting the round back on its feet; this round's dice are re-rolled rather than shown"
                    : $"[dicefix] the reveal has been going {waited:F0}s at a table of {seats} and " +
                      "nothing has moved - putting the round back on its feet");

            if (threw) Describe(m, d);
            Recover(d);
        }
        catch (Exception e) { Plugin.Log.LogError($"[dicefix] tick failed: {e.Message}"); }
    }

    /// <summary>
    /// Everything the reveal could have been indexing, at the moment it stopped.
    ///
    /// The faulting line cannot be read, so the next best thing is a picture of the table taken
    /// the instant it broke: how many players the roster holds, what the synced count says, each
    /// seat's number and whether it is out, and how many dice each seat is holding. An array
    /// index that ran off the end ran off the end of one of these, and having them written down
    /// side by side is what turns the next attempt at this from guesswork into arithmetic.
    ///
    /// Developer mode only. A player has no use for it and it is several lines per fault.
    /// </summary>
    private static bool _described;

    private static void Describe(Manager m, DiceGamePlayManager d)
    {
        if (!Dev.Enabled || _described) return;
        _described = true;                       // once a session: it is the same every time

        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"roster={(m.Players != null ? m.Players.Count : -1)}");
            sb.Append($" startCount={m.StartPlayerCount}");
            sb.Append($" activeSlot={m.ActivePlayerSlot}");
            try { sb.Append($" lastCount={d.LastCount} lastDice={d.LastDice} total={d.TotalCount} max={d.MaxCount}"); }
            catch { }

            Plugin.Log.LogWarning($"[dicefix] when it broke: {sb}");

            if (m.Players == null) return;
            foreach (var p in m.Players)
            {
                if (p == null) continue;
                int dice = -1;
                try
                {
                    var gp = p.GetComponent<DiceGamePlay>();
                    if (gp != null && gp.DiceValues != null) dice = gp.DiceValues.Count;
                }
                catch { }

                Plugin.Log.LogWarning(
                    $"[dicefix]   seat {p.Slot} '{p.PlayerName}' dead={p.Dead} finished={p.Fnished} dice={dice}");
            }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[dicefix] could not describe the table: {e.Message}"); }
    }

    /// <summary>
    /// The game's own recovery. Private, so it is called by reflection rather than copied -
    /// it stops the coroutines, clears the flags, resets the panel, re-rolls, gives the turn
    /// and restarts the countdown, and a hand-written stand-in would be all of that to keep
    /// correct as well as wrong in its own new ways.
    /// </summary>
    private static System.Reflection.MethodInfo _recover;
    private static bool _looked;

    private static void Recover(DiceGamePlayManager d)
    {
        if (!_looked)
        {
            _looked = true;
            _recover = AccessTools.Method(typeof(DiceGamePlayManager), "StopRevealFlowAndRecoverRound",
                                          Type.EmptyTypes);
            if (_recover == null)
                Plugin.Log.LogError("[dicefix] StopRevealFlowAndRecoverRound is missing - the round cannot be recovered");
        }

        if (_recover == null) return;

        try
        {
            _recover.Invoke(d, null);
            _recovered++;
            Plugin.Log.LogWarning($"[dicefix] round recovered ({_recovered} so far this match)");
        }
        catch (Exception e) { Plugin.Log.LogError($"[dicefix] the recovery itself failed: {e.Message}"); }
    }

    internal static void MatchOver()
    {
        _recovered = 0;
        _described = false;
    }
}
