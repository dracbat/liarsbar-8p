using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Puts the cards in people's hands when the game's own deal does not.
///
/// The deal happens in two halves. The first works: every player is given five card
/// values, and that half is plain server code. The second half — the animation that puts
/// the card objects into hands and then gives out the first turn — is a coroutine, and at
/// more than four players it does not run to completion. The table ends up dealt on paper
/// and empty in the hands, with nobody able to act.
///
/// Rather than depend on that coroutine, this hands the cards over directly, through the
/// game's own <c>SetCardsRpc</c>. That is the same message the animation sends when it
/// works, so every client — a person across the internet as much as the host — receives
/// their hand through the path the game already uses. Only what the animation would have
/// done is done; nothing is invented.
///
/// It waits first, and does nothing if the hands arrive on their own, so on a table where
/// the game's own deal works this never fires.
/// </summary>
internal static class DealFallback
{
    /// <summary>Long enough for the real deal, which takes a second or two of animation.</summary>
    private const float GraceSeconds = 3f;

    private static float _dealtAt;
    private static bool _doneThisRound;

    internal static void RoundStarting()
    {
        _dealtAt = 0f;
        _doneThisRound = false;
    }

    internal static void Tick()
    {
        if (_doneThisRound) return;

        try
        {
            if (!Mirror.NetworkServer.active) return;

            var m = Manager.Instance;
            if (m == null || m.Players == null || m.Players.Count < 2) return;

            // Wait until there is something to hand out at all.
            var waiting = new List<PlayerStats>();
            int holding = 0, dealt = 0;

            foreach (var p in m.Players)
            {
                if (p == null || p.Dead) continue;
                var gp = p.GetComponent<DeckGameplay>();
                if (gp == null || gp.cardTypes == null || gp.cardTypes.Count == 0) continue;

                dealt++;
                if (gp.HaveCards) holding++;
                else waiting.Add(p);
            }

            if (dealt == 0) { _dealtAt = 0f; return; }

            if (_dealtAt == 0f) { _dealtAt = Time.time; return; }
            if (Time.time - _dealtAt < GraceSeconds) return;

            _doneThisRound = true;

            if (waiting.Count == 0)
            {
                // The game managed it by itself; nothing to do, and worth knowing.
                Plugin.Log.LogInfo($"[dealcards] all {holding} hands arrived on their own");
                return;
            }

            Plugin.Log.LogWarning(
                $"[dealcards] {waiting.Count} of {dealt} players were dealt cards but never given " +
                "them - handing them over directly");

            foreach (var p in waiting) Hand(p);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[dealcards] failed: {e.Message}");
            _doneThisRound = true;
        }
    }

    /// <summary>Send one player the hand they were dealt, the way the game would have.</summary>
    private static void Hand(PlayerStats p)
    {
        try
        {
            var gp = p.GetComponent<DeckGameplay>();
            if (gp == null || gp.cardTypes == null) return;

            int count = gp.cardTypes.Count;
            var types = new Il2CppStructArray<int>(count);
            var active = new Il2CppStructArray<int>(count);
            for (int i = 0; i < count; i++) { types[i] = gp.cardTypes[i]; active[i] = i; }

            // Applying the state here is what actually puts the cards in the hand on this
            // machine - sending the message alone did not, which cost a test run to learn.
            // The message is still sent afterwards, because that is what carries the hand
            // to a player on another computer.
            gp.ApplyCardState(types, active, true);
            gp.SetHaveCards(true);
            gp.SetCardsRpc(types);

            int objects = gp.Cards != null ? gp.Cards.Count : -1;
            Plugin.Log.LogInfo($"[dealcards] '{p.PlayerName}' handed {count} cards ({objects} card objects)");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[dealcards] could not hand '{p.PlayerName}' their cards: {e.Message}");
        }
    }
}
