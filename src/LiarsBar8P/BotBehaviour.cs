using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// What a bot does when the turn reaches it: wait a moment, then make the simplest legal
/// move, so the round keeps moving.
///
/// Deliberately not clever. The point of a bot here is to prove that a table of eight can
/// deal, take turns in the right order and finish a round — not to play well. Anything
/// resembling strategy would only make a failed round harder to read.
///
/// The move goes through <c>RequestThrowCards</c>, which is where a person's play lands
/// too: on the host it calls the server-side throw directly, so it needs no connection and
/// no ownership, and a bot's turn is settled by exactly the code that settles a person's.
/// </summary>
internal static class BotBehaviour
{
    /// <summary>How long a bot appears to think, so a round is watchable.</summary>
    private const float MinThinkSeconds = 1f;
    private const float MaxThinkSeconds = 3f;

    /// <summary>When each bot currently holding a turn intends to play.</summary>
    private static readonly Dictionary<int, float> _playAt = new();

    /// <summary>
    /// The active slot a bot last played on, so it plays once per turn rather than once
    /// per tick. Throwing does not clear the turn flag straight away, so without this a
    /// bot sees "my turn" again a second later and throws its entire hand in one go —
    /// which is what it did.
    /// </summary>
    private static readonly Dictionary<int, int> _playedOn = new();

    /// <summary>Varied per bot without a random number generator, which the log needs stable.</summary>
    private static float ThinkTime(int seat) =>
        MinThinkSeconds + ((seat * 7 % 5) / 4f) * (MaxThinkSeconds - MinThinkSeconds);

    internal static void Tick()
    {
        if (!Dev.Enabled) return;
        if (!Dev.IsServer) return;                 // only the host may act for a bot

        var m = Dev.Mgr;
        if (m == null || m.Players == null) return;

        // During an unattended test the host's own seat is played too. Without it the round
        // reaches the tester's turn and stops there, which tests nothing beyond the first
        // turn. Only ever while the automatic test is running - in an ordinary session,
        // bots play and people play for themselves.
        bool playEveryone = DevAutoTest.Active;

        foreach (var p in Dev.TablePlayers())
        {
            if (p == null) continue;
            if (!BotManager.IsBot(p) && !playEveryone) continue;

            HoldCards(p);

            if (!p.HaveTurn || p.Dead)
            {
                _playAt.Remove(p.Slot);
                continue;
            }

            // Already had a go while this slot was the active one.
            if (_playedOn.TryGetValue(p.Slot, out int playedOn) && playedOn == m.ActivePlayerSlot) continue;

            if (!_playAt.TryGetValue(p.Slot, out float due))
            {
                due = Time.time + ThinkTime(p.Slot);
                _playAt[p.Slot] = due;
                Dev.Log("bot", $"{p.PlayerName} has the turn (seat {p.Slot}) - playing in " +
                               $"{due - Time.time:F1}s");
                continue;
            }

            if (Time.time < due) continue;
            _playAt.Remove(p.Slot);
            _playedOn[p.Slot] = m.ActivePlayerSlot;
            Play(p);
        }
    }

    /// <summary>
    /// Tell the table a bot is holding the cards it was dealt.
    ///
    /// The game raises that flag by sending a message to the player's own connection, and
    /// a bot has no connection for it to arrive at. So a bot is dealt cards and is never
    /// recorded as holding any — and the round waits for everyone to be holding cards
    /// before it hands out the first turn, so nothing happens at all. The host sets the
    /// flag for them instead, which is what the message would have done.
    /// </summary>
    private static void HoldCards(PlayerStats p)
    {
        try
        {
            var gp = p.GetComponent<DeckGameplay>();
            if (gp == null || gp.cardTypes == null) return;

            bool shouldHold = gp.cardTypes.Count > 0;
            if (gp.HaveCards == shouldHold) return;

            gp.NetworkHaveCards = shouldHold;
            Dev.Log("bot", $"{p.PlayerName} holds {gp.cardTypes.Count} card(s) - " +
                           "flag set here because a bot has no connection to be told on");
        }
        catch { }
    }

    /// <summary>
    /// Throw a single card. One card is always a legal play, whatever the round card is —
    /// the claim is a lie as often as not, which is the game working as intended.
    /// </summary>
    private static void Play(PlayerStats p)
    {
        try
        {
            var gp = p.GetComponent<DeckGameplay>();
            if (gp == null)
            {
                Dev.Warn("bot", $"{p.PlayerName} has no gameplay component - passing the turn instead");
                DevCommands.SkipTurn();
                return;
            }

            var hand = gp.cardTypes;
            if (hand == null || hand.Count == 0)
            {
                Dev.Warn("bot", $"{p.PlayerName} has no cards - passing the turn instead");
                DevCommands.SkipTurn();
                return;
            }

            int type = hand[0];
            var thrown = new Il2CppSystem.Collections.Generic.List<int>();
            thrown.Add(type);

            bool emptied = hand.Count <= 1;
            hand.RemoveAt(0);

            Dev.Log("bot", $"{p.PlayerName} (seat {p.Slot}) throws 1 card, {hand.Count} left" +
                           (emptied ? " - hand empty" : ""));

            gp.RequestThrowCards(thrown, emptied);

            // Hand the turn back. Throwing does not end a bot's turn on its own: the game
            // ends it from a scheduled pass that does not complete for a player with no
            // connection, so the bot keeps the turn flag, will not act again because it has
            // already played, and the table deadlocks with everyone waiting on it. Clearing
            // it here lets the turn move on to the next player.
            if (p.HaveTurn) p.NetworkHaveTurn = false;
        }
        catch (Exception e)
        {
            Dev.Warn("bot", $"{p.PlayerName} could not play: {e.Message} - passing the turn instead");
            try { DevCommands.SkipTurn(); } catch { }
        }
    }
}
