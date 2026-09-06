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

    /// <summary>Bots whose hand has already been built this round.</summary>
    private static readonly HashSet<int> _handBuilt = new();

    internal static void RoundStarting() => _handBuilt.Clear();

    /// <summary>
    /// Put the cards and the revolver in a bot's hands.
    ///
    /// Everything a player physically receives arrives over their own connection: the card
    /// objects, the flag saying they are holding cards, the revolver being loaded. A bot
    /// has no connection, so none of it reaches them — they are dealt a hand that exists
    /// only as numbers, sit there empty-handed with no gun, and the round waits forever
    /// for everyone to be holding cards before giving out the first turn.
    ///
    /// The host therefore runs the receiving end for them. These are the same methods a
    /// real client runs when the message arrives, called directly rather than sent, so a
    /// bot ends up in the state a person would be in.
    /// </summary>
    private static void HoldCards(PlayerStats p)
    {
        try
        {
            var gp = p.GetComponent<DeckGameplay>();
            if (gp == null || gp.cardTypes == null) return;

            int count = gp.cardTypes.Count;
            if (count == 0) return;

            int id = p.Slot;
            if (_handBuilt.Contains(id))
            {
                // Already built; just keep the flag true as cards are played.
                if (!gp.HaveCards) gp.NetworkHaveCards = true;
                return;
            }
            _handBuilt.Add(id);

            // The card values it was dealt, and every one of them face down and active.
            var types = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(count);
            var active = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(count);
            for (int i = 0; i < count; i++) { types[i] = gp.cardTypes[i]; active[i] = i; }

            gp.ApplyCardState(types, active, true);

            int objects = gp.Cards != null ? gp.Cards.Count : -1;
            Dev.Log("bot", $"{p.PlayerName} given {count} card(s) in hand ({objects} card objects) - " +
                           "done here because a bot has no connection to be dealt over");

            LoadRevolver(p, gp);
        }
        catch (Exception e)
        {
            Dev.Warn("bot", $"{p.PlayerName} could not be given a hand: {e.Message}");
        }
    }

    /// <summary>
    /// Load a bot's revolver. Same reason as the cards: the game runs this on the player's
    /// own client, and a bot has none, so its gun stays unloaded while everyone else's is
    /// ready.
    /// </summary>
    private static void LoadRevolver(PlayerStats p, DeckGameplay gp)
    {
        try
        {
            gp.StartCoroutine(gp.UpdateRevolverUI());
            Dev.Log("bot", $"{p.PlayerName} loaded its revolver");
        }
        catch (Exception e)
        {
            Dev.Warn("bot", $"{p.PlayerName} could not load its revolver: {e.Message}");
        }
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
