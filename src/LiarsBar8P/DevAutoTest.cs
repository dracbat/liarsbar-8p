using System;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Runs a full eight player round on its own: host, fill the table with bots, start.
///
/// Driving the developer panel by hand needs somebody at the keyboard, and sending
/// keystrokes from outside is unreliable — Windows refuses to hand focus to a window on
/// request, so half the presses land somewhere else. A test that only sometimes runs is
/// not a test, so this does the same sequence from inside the game, on a timer, and says
/// what it is doing at each step.
/// </summary>
internal static class DevAutoTest
{
    private enum Phase { Waiting, Filling, Starting, Done }

    private static Phase _phase = Phase.Waiting;
    private static float _next;

    /// <summary>
    /// Whether this automatic test should run at all.
    ///
    /// Never under the loopback harness. Both want to decide when the match starts and who is
    /// at the table, and this one moves first: it filled the host's lobby with bots on a timer
    /// before the real copies had finished connecting, so the table that got tested was a table
    /// of bots with the genuine clients locked out of it - the exact opposite of what the
    /// harness exists to prove.
    /// </summary>
    internal static bool Active =>
        Dev.Enabled
        && Plugin.DevAutoTest != null && Plugin.DevAutoTest.Value
        && Loopback.Mine == Loopback.Role.Off;

    /// <summary>
    /// Whether this automatic test is currently driving a match - i.e. whether the mod is
    /// allowed to play other people's seats for them.
    ///
    /// Not the same question as <c>Active</c>, and the difference is a nasty one. Active is
    /// just "the option is switched on", which stays true for the whole launch. Someone who
    /// ran one unattended bot test and then hosted a lobby for friends in the same session had
    /// every friend's turn played for them a second or two after it arrived, with roughly every
    /// third forced move calling them a liar. This is set when the test actually starts a match
    /// and cleared the moment that match ends.
    /// </summary>
    internal static bool Driving { get; private set; }

    /// <summary>Called from the ticker: the match this test started is over.</summary>
    internal static void MatchEnded()
    {
        if (!Driving) return;
        Driving = false;
        _phase = Phase.Waiting;
        _next = 0f;
        Dev.Log("auto", "the test match has ended - no longer playing anybody's seat for them");
    }

    /// <summary>
    /// How many players to sit down, counting the host. Zero in the config means every
    /// seat; a smaller number tests a partly full table, where the seats have to be
    /// re-spaced to share the ring out evenly - a case a full table never exercises.
    /// </summary>
    private static int Target
    {
        get
        {
            int want = Plugin.DevTestPlayers != null ? Plugin.DevTestPlayers.Value : 0;
            if (want <= 0) return Limits.Max;
            return Mathf.Clamp(want, 2, Limits.Max);
        }
    }

    internal static void Tick()
    {
        if (!Active || _phase == Phase.Done) return;

        try
        {
            switch (_phase)
            {
                case Phase.Waiting:
                    // Wait for a lobby with the host's own player already in it, then give
                    // it a breath: the lobby is still wiring itself up on the frame it
                    // reports ready.
                    if (Dev.Lobby == null || !Dev.IsServer || Dev.LobbyPlayers().Count < 1) return;
                    if (_next == 0f) { _next = Time.time + 4f; return; }
                    if (Time.time < _next) return;

                    Dev.Log("auto", $"lobby is up - seating {Target} at the table");
                    _phase = Phase.Filling;
                    BotManager.FillTo(Target);
                    _next = Time.time + 25f;   // long enough to look at an eight player lobby
                    return;

                case Phase.Filling:
                    if (Time.time < _next) return;

                    int players = Dev.LobbyPlayers().Count;
                    if (players < Target)
                    {
                        Dev.Warn("auto", $"only {players}/{Target} made it into the lobby - starting anyway");
                    }
                    DevCommands.PrintPlayerList();
                    DevCommands.PrintPodiums();

                    Dev.Log("auto", $"starting a {players} player match");
                    _phase = Phase.Starting;
                    Driving = true;
                    DevCommands.StartMatch();
                    _next = Time.time + 20f;
                    return;

                case Phase.Starting:
                    if (Time.time < _next) return;

                    Dev.Log("auto", "match should be running - state follows");
                    DevCommands.PrintSeats();
                    DevCommands.PrintDeck();
                    DevCommands.PrintTurn();
                    _phase = Phase.Done;
                    return;
            }
        }
        catch (Exception e)
        {
            Dev.Warn("auto", $"failed in {_phase}: {e.Message}");
            _phase = Phase.Done;
        }
    }
}
