using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// The aiming keys in the Chaos deck, for a table of five or more.
///
/// When a chaos card lands, whoever threw it gets the revolver and about nine seconds to pick
/// a target with A and D, LB and RB, or the D-pad. In the shipped game that handling is not in
/// <c>LeftAim</c> and <c>RightAim</c>, although methods by those names exist: the build
/// compiled a copy of both straight into <c>ChaosDeckGameplay.UpdateCall</c>, and nothing calls
/// the real ones. So the patches <see cref="AimRing"/> put on them in 1.1.1 never ran when a
/// person pressed a key. What ran instead was the shipped copy, which only moves the aim
/// between -1, 0 and 1 - three targets. At seven players those are the three people furthest
/// away; both neighbours could never be picked, dead chairs were not skipped, and an aim that
/// opened anywhere else could not be moved at all. A player reported exactly that: at seven,
/// choosing a target "was not very easy". The test harness never saw it, because it moved the
/// aim by calling <c>LeftAim</c> and <c>RightAim</c> directly - the one path no player uses.
///
/// So this sits on <c>UpdateCall</c> itself. While the player at this computer is choosing,
/// at a table of more than four, it reads the same keys the game reads - the same buttons, the
/// same half-way D-pad threshold, a held D-pad still counting once - and steps the aim with
/// <see cref="AimRing.Step"/>: over every chair, skipping the dead and the finished, carrying
/// on round past either end. The new aim is sent to the server through the game's own
/// <c>SyncAimToServer</c>, exactly as the shipped keys send it.
///
/// The shipped copy still has to be kept from running as well, or every press would move the
/// aim twice. It sits behind a single check - is the aim locked? - which nothing else in that
/// method reads, so for the length of each <c>UpdateCall</c> the lock is held up, on the plain
/// field rather than through the networked setter, so nothing is marked to be sent; it is put
/// back the moment the method returns, even if it throws. Holding it up on every frame of the
/// choice rather than only when a key is pressed matters for the D-pad: the shipped code
/// remembers whether the pad was already held, and a reminder it never got to update would
/// count one push as two. The mod keeps the game's own memory of that up to date instead.
///
/// Four players or fewer, a seat that is not yours, a forced shot (which is locked by the
/// game itself), or somebody already dead - all of those are left to the shipped code
/// untouched. Liar's Poker is not affected by any of this: its keys really do call
/// <c>LeftAim</c> and <c>RightAim</c>.
/// </summary>
[HarmonyPatch]
internal static class AimKeys
{
    /// <summary>The D-pad's horizontal axis, as the game names it in its input settings.</summary>
    private const string PadAxis = "DPadX";

    /// <summary>How far the D-pad has to be pushed to count - the game's own threshold.</summary>
    private const float PadThreshold = 0.5f;

    /// <summary>Mirror's change flag for <c>Aim</c>, as the game's own setter passes it.</summary>
    private const ulong AimDirtyBit = 8UL;

    /// <summary>The last frame the aim keys were read for the seat at this computer.</summary>
    private static int _lastFrame = -10;

    // The seat at this computer that is choosing, worked out once when its choice opens rather
    // than on every frame of it. Asking a component for its player or its manager hands back a
    // new wrapper object each time, and this runs every frame for the nine seconds of a
    // choice; none of it can change until the choice is over - the table does not change size
    // in the middle of a round - so it is learnt on the first frame and reused after.

    /// <summary>Which object the facts below belong to.</summary>
    private static IntPtr _seat;
    private static PlayerStats _stats;
    private static Manager _m;
    private static int _me;
    private static int _n;

    /// <summary>More than four seats: the keys are the mod's. Otherwise the shipped keys are left alone.</summary>
    private static bool _wide;

    /// <summary>
    /// This computer is a client, so the server sends every aim it is given back to it. The
    /// host's own aim never comes back, so nothing below needs doing there.
    /// </summary>
    private static bool _client;

    /// <summary>Whether this choice has had its one-time opening look (<see cref="AimRing.FixOpening"/>).</summary>
    private static bool _opened;

    /// <summary>
    /// The aim the keys last chose on this computer, this choice - or <see cref="NoAim"/> before
    /// the first press.
    ///
    /// On a client every press is set locally and sent to the server, and the server then sends
    /// its own aim back. Between the two there is a round trip in which the server still holds
    /// the aim from one or two presses ago, and when that older value arrives it is written
    /// straight over the newer one. Two quick taps then made the readout and the pose jump back
    /// a seat, and a third tap in that gap stepped from the old value and sent the wrong seat
    /// altogether - the press was simply lost. Four players never noticed, because nobody needs
    /// more than two presses to reach anyone there; with six people to go round and the aim
    /// carrying on past either end, tapping quickly is how the table is crossed. So the aim the
    /// keys chose is put back whenever an older one arrives over it: the server applies the
    /// presses in the order they were sent and will end on this one anyway.
    /// </summary>
    private static int _intended = NoAim;
    private const int NoAim = int.MinValue;

    private static bool _announced;
    private static bool _refusedReported;

    /// <summary>Errors reported so far. This runs every frame; one fault must not fill the log.</summary>
    private static int _errors;
    private const int MaxErrors = 5;

    // ---------------------------------------------------------- presses the harness makes

    /// <summary>Presses waiting to be made, by the seat that is to make them.</summary>
    private static readonly Dictionary<IntPtr, int> _injected = new();

    /// <summary>
    /// Press an aiming key on a seat, the way a player would - for the test harness only.
    ///
    /// The press is not acted on here. It is left for the seat's next <c>UpdateCall</c> to
    /// pick up, at exactly the point where a real key is read, so that a harness run goes
    /// through every line a keypress goes through and the one bug the old harness could not
    /// see - keys that never reached the code under test - would show up as a failed walk.
    ///
    /// A seat that belongs to this computer takes it as a keypress. On the host a bot's seat
    /// does too, since the host is the only machine that can move a bot at all; somebody
    /// else's seat never does - their own machine presses their keys.
    /// </summary>
    internal static bool Inject(ChaosDeckGameplay seat, int direction)
    {
        if (!Dev.Enabled || seat == null || direction == 0) return false;
        try
        {
            _injected[seat.Pointer] = direction < 0 ? -1 : 1;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Whether a press made by <see cref="Inject"/> has not been picked up yet.</summary>
    internal static bool Pending(ChaosDeckGameplay seat)
    {
        if (_injected.Count == 0 || seat == null) return false;
        try { return _injected.ContainsKey(seat.Pointer); }
        catch { return false; }
    }

    /// <summary>
    /// Take back a press that has not been picked up.
    ///
    /// A press is only picked up while the seat is choosing and its aim is still open. One made
    /// just before the aim locked, or just before the choice ended, would otherwise wait here
    /// for the seat's next choice and move its aim on the very first frame - before the harness
    /// had decided anything - leaving a press in the log nobody made and the counts off by one.
    /// </summary>
    internal static void Cancel(ChaosDeckGameplay seat)
    {
        if (_injected.Count == 0 || seat == null) return;
        try { _injected.Remove(seat.Pointer); }
        catch { }
    }

    /// <summary>Take back every press not yet picked up - between rounds, and when nobody is choosing.</summary>
    internal static void ClearInjected() => _injected.Clear();

    /// <summary>A seat with no player behind it, on the host: a bot.</summary>
    private static bool HostsBot(ChaosDeckGameplay seat)
    {
        try { return Dev.Enabled && seat.isServer && seat.connectionToClient == null; }
        catch { return false; }
    }

    // ------------------------------------------------------------------- the keys

    /// <summary>
    /// Read the aiming keys, move the aim, and hold the shipped copy off for this call.
    ///
    /// Ordered so that the check that fails on almost every frame comes first: this runs on
    /// every player's object every frame in a Chaos deck match, and for all but the one
    /// person aiming it ends at the first line. For that one person, who they are and how big
    /// the table is are worked out on the first frame of the choice and reused after it.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.UpdateCall))]
    private static void Keys(ChaosDeckGameplay __instance, ref bool __state)
    {
        try
        {
            if (!__instance.TakingAim || __instance.AimLocked) return;

            int injected = 0;
            if (_injected.Count != 0 && _injected.Remove(__instance.Pointer, out int pressed)) injected = pressed;

            if (!__instance.isOwned)
            {
                if (injected != 0) PressForBot(__instance, injected);
                return;
            }

            // A frame missed - the aim locked, the choice ended, the seat was not choosing -
            // means this is a new choice, and so does a different player object.
            int frame = Time.frameCount;
            IntPtr seat = __instance.Pointer;
            if (frame - _lastFrame > 1 || seat != _seat)
            {
                _intended = NoAim;
                _opened = false;
                if (!Learn(__instance, seat)) { _lastFrame = -10; return; }   // not known yet: ask again next frame
            }
            _lastFrame = frame;

            // Four or fewer is the shipped keys' table; the rest of the shipped gate is a seat
            // with somebody alive in it.
            if (!_wide || _stats.Dead) return;

            // Up first, so that whatever happens below, the shipped copy does not also act on
            // this frame's keys.
            __instance.AimLocked = true;
            __state = true;

            if (!_opened)
            {
                _opened = true;
                Announce(_n);
                if (AimRing.FixOpening(__instance, _m, _me, _n)) _intended = __instance.Aim;
            }
            else if (_client && _intended != NoAim)
            {
                int arrived = __instance.Aim;
                if (arrived != _intended)
                {
                    // An older aim from the server, written over the one the keys chose. Put
                    // back, before the keys are read, so the next press steps from the right
                    // place and the readout never shows the seat it was moved away from.
                    __instance.NetworkAim = _intended;
                    AimRing.ShowPose(__instance, _n, _intended);
                    if (Dev.Enabled)
                        Plugin.Log.LogInfo($"[aimkeys] seat {_me}: the server sent back an older aim ({arrived}) " +
                                           $"- kept {_intended}, which it has yet to reach");
                }
            }

            int direction = ReadKeys(__instance);
            if (direction == 0) direction = injected;
            if (direction == 0) return;

            if (AimRing.MoveDeck(__instance, _m, _me, _n, direction, out int from, out int to))
            {
                _intended = to;
                if (Dev.Enabled) LogPress(_me, from, to, _n, injected != 0);
            }
        }
        catch (Exception e)
        {
            if (++_errors <= MaxErrors) Plugin.Log.LogError($"[aimkeys] {e.Message}");
        }
    }

    /// <summary>
    /// The first frame of a choice: who is choosing, at what size of table. False when that
    /// cannot be told yet - no player on the seat, no manager, a table size not yet sent - so
    /// it is asked again next frame rather than settled wrongly for the whole choice. A table
    /// of four or fewer is settled: the shipped keys have it, and nothing is asked again
    /// until the next choice.
    /// </summary>
    private static bool Learn(ChaosDeckGameplay self, IntPtr seat)
    {
        _seat = seat;
        _wide = false;
        _stats = self.playerStats;
        if (_stats == null) return false;

        _client = !self.isServer;
        _wide = AimRing.Who(self, out _m, out _me, out _n);
        return _wide || (_n > 0 && _n <= Limits.VanillaPlayers);
    }

    /// <summary>
    /// A harness press on a seat this computer does not own. On the host a bot's seat takes
    /// it, since the host is the only machine that can move a bot at all; anybody else's seat
    /// never does - their own machine presses their keys. Developer mode only, and rare enough
    /// - one press a tick - that it works everything out afresh each time.
    /// </summary>
    private static void PressForBot(ChaosDeckGameplay seat, int direction)
    {
        if (!HostsBot(seat))
        {
            if (!_refusedReported)
            {
                _refusedReported = true;
                Plugin.Log.LogWarning("[aimkeys] harness press on a seat another machine owns - " +
                                      "ignored; that machine presses its own keys");
            }
            return;
        }

        var stats = seat.playerStats;
        if (stats == null || stats.Dead) return;
        if (!AimRing.Who(seat, out var m, out int me, out int n)) return;   // four or fewer

        if (AimRing.MoveDeck(seat, m, me, n, direction, out int from, out int to))
            LogPress(me, from, to, n, true);
    }

    private static void LogPress(int me, int from, int to, int n, bool harness)
        => Plugin.Log.LogInfo($"[aimkeys] seat {me} aim {from} -> {to} of a {n} seat ring" +
                              (harness ? " (harness press)" : ""));

    /// <summary>
    /// The shipped key reading, line for line: the D-pad counts on the frame it crosses half
    /// way, and left wins if both are asked for at once.
    ///
    /// The game's own "D-pad already held" flags are written here, because the shipped code
    /// that normally writes them is being held off. Leaving them stale would make the first
    /// frame after the choice ends - when the shipped code runs again - see a fresh push.
    ///
    /// The pad's axis is asked for by name, as the game asks for it. From here that name has to
    /// be copied into the game's own kind of string on every call, which the game's compiled
    /// code does not pay: one small string a frame, for the one seat choosing, for the length
    /// of the choice. The axis has to be read every frame for the held-pad memory to stay
    /// right, so that copy is the one cost here that is not worked out once and kept.
    /// </summary>
    private static int ReadKeys(ChaosDeckGameplay self)
    {
        float x = Input.GetAxis(PadAxis);
        bool padLeft = x < -PadThreshold;
        bool padRight = x > PadThreshold;

        bool left = (padLeft && !self.dpadLeftPressed)
                    || Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.JoystickButton4);
        bool right = (padRight && !self.dpadRightPressed)
                     || Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.JoystickButton5);

        self.dpadLeftPressed = padLeft;
        self.dpadRightPressed = padRight;

        if (left) return -1;
        if (right) return 1;
        return 0;
    }

    /// <summary>
    /// Put the lock back down - after the method returns, and after it throws.
    ///
    /// Always back to open: the lock was only raised here when it had been found open, and
    /// nothing inside <c>UpdateCall</c> locks the aim for real.
    /// </summary>
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.UpdateCall))]
    private static Exception Release(ChaosDeckGameplay __instance, bool __state, Exception __exception)
    {
        if (__state)
        {
            try { __instance.AimLocked = false; }
            catch (Exception e)
            {
                if (++_errors <= MaxErrors) Plugin.Log.LogError($"[aimkeys] could not release the aim lock: {e.Message}");
            }
        }
        return __exception;
    }

    /// <summary>Said once, so that a player's log shows the mod was moving their aim.</summary>
    private static void Announce(int n)
    {
        if (_announced) return;
        _announced = true;
        Plugin.Log.LogInfo($"[aimkeys] aiming at a table of {n}: A / D, LB / RB and the D-pad step over " +
                           "every seat still in, and carry on round past either end");
    }

    // ----------------------------------------------------------------- when the aim locks

    /// <summary>
    /// When the countdown ends and the aim locks, send everybody the aim the server is really
    /// going to fire at.
    ///
    /// A press made in the last moment before the lock reaches the server after it, and the
    /// server drops it. The player's own machine has already moved its aim and its readout,
    /// and because the server's aim did not change it never sends a correction - so for the
    /// two seconds before the shot the readout names somebody who is not going to be shot.
    /// Marking the aim as changed at the lock makes the server send its own value once more,
    /// which puts every machine's readout on the person the shot will really go to. Nothing
    /// about the shot itself changes. The shipped game has the same gap; it is closed here
    /// only for tables of five or more, where the readout is the only way to tell.
    ///
    /// The lock arrives with that value, and from then on the keys no longer run for the seat,
    /// so nothing puts back the aim the keys chose (<see cref="_intended"/>) over the one the
    /// server is going to fire at.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.LockTheAim))]
    private static void Locked(ChaosDeckGameplay __instance)
    {
        try
        {
            if (!__instance.isServer) return;
            if (!AimRing.Who(__instance, out _, out _, out _)) return;
            __instance.SetSyncVarDirtyBit(AimDirtyBit);
        }
        catch (Exception e) { Plugin.Log.LogError($"[aimkeys] could not resend the locked aim: {e.Message}"); }
    }

    // ------------------------------------------------------------ the harness's evidence

    /// <summary>
    /// Developer mode only: what the server is about to fire at, next to what the harness
    /// chose with the keys. Does nothing at all in normal play.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.ServerResolveFire))]
    private static void Resolving(ChaosDeckGameplay __instance, int aimDirection)
    {
        if (!Dev.Enabled) return;
        try { AimDrive.Resolving(__instance, aimDirection); }
        catch (Exception e) { Dev.Warn("aim", $"could not check the shot: {e.Message}"); }
    }
}
