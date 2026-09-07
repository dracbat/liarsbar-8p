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

    internal static bool Active => Dev.Enabled && Plugin.DevAutoTest != null && Plugin.DevAutoTest.Value;

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
