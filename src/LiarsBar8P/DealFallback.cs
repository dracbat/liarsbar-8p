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
            var report = new List<string>();
            int holding = 0, dealt = 0, novalues = 0;

            foreach (var p in m.Players)
            {
                if (p == null || p.Dead) continue;
                var gp = p.GetComponent<DeckGameplay>();
                if (gp == null) continue;

                if (gp.cardTypes == null || gp.cardTypes.Count == 0) { novalues++; continue; }

                dealt++;
                int want = gp.cardTypes.Count;
                int showing = Showing(gp);

                report.Add($"  seat {p.Slot} '{p.PlayerName}': {want} cards dealt, " +
                           $"{showing} of {Count(gp.Cards)} card objects switched on " +
                           $"({Visible(gp)} of them actually in view), holding={gp.HaveCards}");

                // What matters is whether the cards are actually in the hand, not whether
                // the game says they are. A player in one of the seats this mod adds could
                // be marked as holding a hand while holding nothing - which is exactly what
                // "the corner spots don't get cards" looks like from the inside.
                if (gp.HaveCards && showing >= want) holding++;
                else waiting.Add(p);
            }

            if (dealt == 0) { _dealtAt = 0f; return; }

            if (_dealtAt == 0f) { _dealtAt = Time.time; return; }
            if (Time.time - _dealtAt < GraceSeconds) return;

            _doneThisRound = true;

            Plugin.Log.LogInfo("[dealcards] hands three seconds after the deal:\n" +
                               string.Join("\n", report));

            if (novalues > 0)
                Plugin.Log.LogWarning($"[dealcards] {novalues} players were never dealt any cards at all");

            if (waiting.Count == 0)
            {
                // The game managed it by itself; nothing to do, and worth knowing.
                Plugin.Log.LogInfo($"[dealcards] all {holding} hands arrived on their own");
                return;
            }

            Plugin.Log.LogWarning(
                $"[dealcards] {waiting.Count} of {dealt} players were dealt cards but are not " +
                "holding them - handing them over directly");

            foreach (var p in waiting) Hand(p);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[dealcards] failed: {e.Message}");
            _doneThisRound = true;
        }
    }

    /// <summary>
    /// How many of a player's card objects have been switched on.
    ///
    /// This asks the object itself, not whether it is on screen: the whole hand is hidden
    /// while a player has their cards down, so a hand that is dealt and a hand that is empty
    /// look the same from the outside. What the deal switches on is the object.
    /// </summary>
    private static int Showing(DeckGameplay gp)
    {
        try
        {
            if (gp.Cards == null) return 0;
            int n = 0;
            foreach (var c in gp.Cards)
                if (c != null && c.activeSelf) n++;
            return n;
        }
        catch { return 0; }
    }

    /// <summary>The same count, but for cards a parent is not hiding - only for the report.</summary>
    private static int Visible(DeckGameplay gp)
    {
        try
        {
            if (gp.Cards == null) return 0;
            int n = 0;
            foreach (var c in gp.Cards)
                if (c != null && c.activeInHierarchy) n++;
            return n;
        }
        catch { return 0; }
    }

    private static int Count(Il2CppSystem.Collections.Generic.List<UnityEngine.GameObject> list)
        => list == null ? -1 : list.Count;

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
