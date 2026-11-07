using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// What a bot does with its turn: think for a moment, then make the move a person at that seat
/// might make, through the same server-side bodies a person's key press reaches.
///
/// One rule above every mode: a bot reads only what a person in its chair could know. Its own
/// dice or cards, the bids and claims on the table, how many dice or cards each player holds, and
/// what has been shown to everybody. Never another seat's hand, which the host holds for everyone
/// and nobody may see. Each mode's player is in a file of its own beside this one.
///
/// Each bot has a little character of its own for the match: how often it bluffs and how quickly
/// it calls somebody a liar, drawn when the match starts, so that a table of bots does not play
/// as one. It takes between one and a half and four seconds over a move, and never lets its turn
/// run out: under two seconds left, it makes its move at once. If that move fails, the turn is
/// passed on by <see cref="BotTurnGuard"/>, which does not need anything here to work.
///
/// Runs on the host, four times a second, whatever developer mode says. Liar's Dice, Liar's Deck
/// (Basic, Devil and Deck 2) and the Chaos deck have a player here. A bot in a mode that has no
/// player yet should never be seated, because those modes refuse bots in the lobby; if one is,
/// its turn is passed on when the clock runs low, so the table keeps moving.
/// </summary>
internal static partial class BotBrain
{
    private const float MinThink = 1.5f;
    private const float MaxThink = 4f;

    /// <summary>Under this many seconds on the clock a bot moves at once.</summary>
    private const float LastMoment = 2f;

    /// <summary>One bot's state for the match.</summary>
    private sealed class Mind
    {
        internal System.Random Rng;

        /// <summary>How often it bluffs, 0.05 to 0.25.</summary>
        internal double Bluff;

        /// <summary>Added to the point at which it calls a liar: positive is quicker to call.</summary>
        internal double Suspicion;

        /// <summary>When it will move, and the moment of the game it is thinking about.</summary>
        internal float Due = -1f;
        internal string Thinking;

        /// <summary>The moment of the game it last moved at, so it moves once at each.</summary>
        internal string Moved;

        /// <summary>Whether the turn clock has been seen running since the turn began.</summary>
        internal bool ClockSeen;

        internal bool HadTurn;

        // Liar's Deck and the Chaos deck: the cards chosen and waiting to be thrown once the bot's
        // cards are up, and since when; how long its cards stay up after a throw; when it set the
        // throw itself, without the look, so it can let go of it again; and when it called a liar,
        // so a call the game did not take is followed by a throw.
        internal List<GameObject> Throw;
        internal float ThrowSince, ThrewAt, LookUntil, ThrowSetAt, CalledAt = -1f;

        // The Chaos deck's gun: whether the bot was holding it at the last look, and when it aims.
        internal bool WasAiming;
        internal float AimDue = -1f;
    }

    private static readonly Dictionary<uint, Mind> _minds = new();
    private static int _match;
    private static bool _saidNoPlayer;

    /// <summary>
    /// Developer test switch: every bot thinks this many seconds, past the clock if need be, so the
    /// last-moment move can be seen to happen. Zero is off, and it does nothing outside developer mode.
    /// </summary>
    internal static float TestThink { get; set; }

    internal enum Fault { None, Move, Brain, Settle, Tick }

    /// <summary>
    /// Developer test switch: every bot's move throws, or the brain does not run at all, or asking the
    /// game whether a call is being settled fails, or the brain's whole tick throws, so that
    /// <see cref="BotTurnGuard"/> can be seen to pass the turn on. It does nothing outside developer mode.
    /// </summary>
    internal static Fault TestFault { get; set; }

    /// <summary>
    /// Developer test switch: at Liar's Deck and the Chaos deck no bot raises its cards, so each one
    /// throws without the look, which in play only happens when its cards do not come up in time.
    /// It does nothing outside developer mode.
    /// </summary>
    internal static bool TestNoLook { get; set; }

    /// <summary>
    /// Developer test switch: in Liar's Dice a bot calls spot on whenever the game lets it, so the
    /// call can be watched, which is rare in play. It does nothing outside developer mode.
    /// </summary>
    internal static bool TestSpotOn { get; set; }

    /// <summary>
    /// Developer test switch: in Liar's Deck and the Chaos deck a bot calls liar on every throw it
    /// can, so that a devil card, which a bot believes as often as any single card, is called and
    /// its punishment can be watched. It does nothing outside developer mode.
    /// </summary>
    internal static bool TestCallAll { get; set; }

    /// <summary>
    /// Developer test switch: at the Devil table a bot makes the Devil's Deal whenever the game lets
    /// it, so the call can be watched settling, which is rare in play. It does nothing outside
    /// developer mode.
    /// </summary>
    internal static bool TestDeal { get; set; }

    /// <summary>From <see cref="ModTicker"/>.</summary>
    internal static void Tick()
    {
        if (!BotSeats.Any || !NetworkServer.active) return;
        if (TestFault == Fault.Brain && Fair.TestsAllowed) return;
        if (TestFault == Fault.Tick && Fair.TestsAllowed) throw new MissingMethodException("test: the brain's tick fails");

        var m = Manager.Instance;
        if (m == null) return;

        int id = m.GetInstanceID();
        if (id != _match)
        {
            _match = id;
            _minds.Clear();
            _saidNoPlayer = false;
            _saidVoice.Clear();
            Dice.MatchStarting();
            Deck.MatchStarting();
            Chaos.MatchStarting();
        }
        if (!m.GameStarted) return;

        switch (TableHand.Playing())
        {
            case TableHand.Kind.Dice:
                Dice.Tick(m);
                break;
            case TableHand.Kind.Deck:
                Deck.Tick(m);
                break;
            case TableHand.Kind.ChaosDeck:
                Chaos.Tick(m);
                break;
            default:
                PassWhenLate(m);
                break;
        }
    }

    /// <summary>
    /// The game could not be asked whether a call is being settled, as after an update that renamed
    /// a flag it is asked by: each table asks inside a try of its own and comes here. No bot moves,
    /// since a move in the middle of a call would break the round; their cards go down, bots are off,
    /// and <see cref="BotTurnGuard"/> passes their turns on by the clock alone.
    /// </summary>
    private static void CannotTell(string table, Exception e)
    {
        foreach (var seat in BotSeats.Seated()) seat.Looking = false;
        BotTurnGuard.Trip($"the game could not be asked whether a call is being settled in {table} ({e.Message})");
    }

    private static readonly HashSet<string> _saidVoice = new(StringComparer.Ordinal);

    /// <summary>
    /// A voice line that goes with a move, said on its own: the move is made whether or not the line
    /// can be. A move that placed its bid and then threw in the voice line held the turn until the
    /// guard passed it on. A line that fails is said once a match.
    /// </summary>
    private static void Voice(BotSeats.Seat seat, string line, Action say)
    {
        try { say(); }
        catch (Exception e)
        {
            if (_saidVoice.Add(line))
                Plugin.Log.LogWarning($"[bot] seat {seat.Player.Slot}'s voice line '{line}' failed, the move goes on: {e.Message}");
        }
    }

    /// <summary>The bot's mind for this match, drawn the first time it is asked for.</summary>
    private static Mind MindOf(BotSeats.Seat seat)
    {
        uint key = seat.Identity.netId;
        if (_minds.TryGetValue(key, out var mind)) return mind;

        var rng = new System.Random(unchecked(Environment.TickCount * 31 + (int)key * 7919));
        mind = new Mind
        {
            Rng = rng,
            Bluff = 0.05 + rng.NextDouble() * 0.20,
            Suspicion = -0.08 + rng.NextDouble() * 0.18,
        };
        _minds[key] = mind;
        return mind;
    }

    /// <summary>
    /// Whether a bot holding the turn moves now. <paramref name="moment"/> names the state of the
    /// game it is looking at, so a bot moves once at each and thinks afresh when it changes.
    /// </summary>
    private static bool Ready(Mind mind, PlayerStats p, Manager m, string moment)
    {
        // A new turn begins whenever the turn arrives, even at a moment that looks like the last.
        if (!mind.HadTurn) { mind.HadTurn = true; mind.Moved = null; mind.ClockSeen = false; }
        if (mind.Moved == moment) return false;

        float clock = 0f;
        try { clock = m.CountDown; } catch { }
        if (clock >= LastMoment) mind.ClockSeen = true;

        float now = Time.time;
        if (mind.Due < 0f || mind.Thinking != moment)
        {
            float think = MinThink + (float)mind.Rng.NextDouble() * (MaxThink - MinThink);
            // Never planned past the last moment: the clock decides, not the think time.
            if (clock > LastMoment) think = Math.Min(think, Math.Max(0.3f, clock - LastMoment - 0.5f));
            if (TestThink > 0f && Fair.TestsAllowed) think = TestThink;
            mind.Due = now + think;
            mind.Thinking = moment;
            return false;
        }

        bool late = mind.ClockSeen && clock < LastMoment;
        if (now < mind.Due && !late) return false;

        mind.Due = -1f;
        mind.Moved = moment;
        if (late) Dev.Log("bot", $"seat {p.Slot}'s clock is nearly out - moving now");
        return true;
    }

    /// <summary>A bot without the turn, or out of the match, forgets the move it was planning.</summary>
    private static void Idle(Mind mind)
    {
        mind.Due = -1f;
        mind.HadTurn = false;
        mind.Throw = null;
        mind.ThrewAt = 0f;
        mind.CalledAt = -1f;
    }

    /// <summary>
    /// A bot in a mode with no player here: pass its turn on as the clock runs out. It should not
    /// happen, since the lobby keeps bots out of those modes; this keeps such a table moving.
    /// </summary>
    private static void PassWhenLate(Manager m)
    {
        float clock;
        try { clock = m.CountDown; } catch { return; }
        if (clock <= 0f || clock >= LastMoment) return;

        foreach (var seat in BotSeats.Seated())
        {
            var p = seat.Player;
            if (p == null || p.Dead || !p.HaveTurn) continue;

            if (!_saidNoPlayer)
            {
                _saidNoPlayer = true;
                Plugin.Log.LogWarning($"[bot] a bot is seated at {TableHand.Playing()}, which has no bot player yet - its turns are passed on");
            }

            try
            {
                p.NetworkHaveTurn = false;
                switch (TableHand.Playing())
                {
                    case TableHand.Kind.Texas: m.GiveTurnTexas(); break;
                    case TableHand.Kind.Spin: m.GiveTurnSpin(); break;
                    default: m.GiveTurn(); break;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[bot] could not pass seat {p.Slot}'s turn: {e.Message}"); }
            return;
        }
    }
}
