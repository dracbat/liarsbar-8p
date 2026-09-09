using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Puts a round of Liar's Dice back on its feet when the reveal after a liar call dies.
///
/// Above four players, resolving a liar call throws <c>IndexOutOfRangeException</c> inside
/// <c>DiceGamePlayManager.ShowPlayer</c>, and that is the end of the round: the coroutine stops
/// where it threw, so the dice are never shown, the loser is never chosen, nobody is given the
/// turn, and the table sits there. Measured at four, six and eight players - clean at four, dead
/// at six and eight, on the first liar call every time.
///
/// <b>Why this recovers rather than fixes.</b> The fault is inside a coroutine, and this project
/// does not patch a coroutine's <c>MoveNext</c> - doing so breaks the state machine that resumes
/// it. Reading the method is not possible either: the decompiler gives up at its state switch
/// and emits no body, and this build of the game logs exceptions without a stack. So the faulting
/// line is not available to be corrected, and a fix invented without it would be a guess dressed
/// up as a repair. What is available is the game's own answer to a reveal that cannot finish:
/// <c>StopRevealFlowAndRecoverRound</c>, which the game itself calls when somebody leaves in the
/// middle of one. It stops the coroutines, clears the liar and spot-on flags, resets the dice
/// panel, re-rolls, hands the turn on and restarts the countdown.
///
/// So the round continues, and what is lost is the reveal animation for that call - the dice are
/// re-rolled rather than shown. That is a real cost and it is not pretended otherwise; it is
/// smaller than a table that stops dead on the first liar call anyone makes.
///
/// <b>How it knows.</b> Two ways, and the first is the one that normally fires. BepInEx sees
/// every line the game logs, so the exception itself is the trigger - the round is recovered
/// within a tick of it being thrown, rather than after a wait. The second is a timeout, for a
/// reveal that stops for any other reason: it is set well above how long a healthy reveal takes
/// (about fifty-five seconds at four players, measured, and longer with more people to show), so
/// it will not cut a working one short.
///
/// This is not a developer tool. It runs for everybody, because the bug does.
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
