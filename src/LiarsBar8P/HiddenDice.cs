using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// At a Liar's Dice table, nobody's dice but your own are drawn on your screen until the game's
/// own reveal, which still shows them all.
///
/// From each roll until the next reveal starts, other seats' dice stay undrawn on this copy. From
/// the start of the game's reveal, on every screen at once, the dice are drawn again as the game
/// draws them. Your own dice, and everything else at the table, are untouched, and
/// nothing is sent anywhere.
///
/// At every table, with the mod or without it, as dice values are kept out of every log at every
/// table (<see cref="HiddenDiceLog"/>). It changes only what this copy draws on its own
/// screen, so it needs nobody else's agreement, and a player with the mod must never get a look at
/// a cup that a player without it does not.
/// </summary>
internal static class HiddenDice
{
    /// <summary>One other seat's cup, as this copy draws it.</summary>
    private sealed class Cup
    {
        internal DiceGamePlay Hand;
        internal bool Hidden;

        /// <summary>
        /// The renderers of the dice in the cup, found once and again only when the cup's dice
        /// change, and the dice they were found on, in the cup's order.
        /// </summary>
        internal readonly List<Renderer> Parts = new();
        internal readonly List<IntPtr> FoundOn = new();

        /// <summary>The renderers this switched off, the only ones it switches back on.</summary>
        internal readonly HashSet<IntPtr> Off = new();
    }

    private static readonly Dictionary<IntPtr, Cup> _cups = new();
    private static int _match;
    private static bool _wasRevealing;
    private static int _hides, _shows;

    /// <summary>When the last roll or shake put the dice out of sight, for the test driver.</summary>
    internal static float LastHideAt { get; private set; }

    /// <summary>Whether the roll patch went on; without it no bot sits at a Liar's Dice table.</summary>
    internal static bool On { get; set; }

    /// <summary>
    /// From <see cref="ModTicker"/>, on every copy. Nothing to do until a roll or a shake has found
    /// the cups: every mode's manager is in the scene whatever is played, so only a table whose
    /// seats carry dice has any.
    /// </summary>
    internal static void Tick()
    {
        var m = Manager.Instance;
        var d = m != null ? m.DiceGame : null;
        if (d == null)
        {
            if (_cups.Count > 0) Forget();
            return;
        }

        int id = m.GetInstanceID();
        if (id != _match)
        {
            Forget();
            _match = id;
        }
        if (_cups.Count == 0) return;

        // A reveal begins with a liar or spot-on call, and the game then shows every seat's dice.
        bool revealing = d.CalledLiar || d.CalledSpotOn;
        if (revealing && !_wasRevealing) ShowAll();
        _wasRevealing = revealing;

        foreach (var cup in _cups.Values)
            if (cup.Hidden && cup.Hand != null) Draw(cup, false);
    }

    /// <summary>Whether this copy keeps a seat's dice out of sight now, for the test driver.</summary>
    internal static bool IsHidden(DiceGamePlay hand)
    {
        if (hand == null) return false;
        return _cups.TryGetValue(hand.Pointer, out var cup) && cup.Hidden;
    }

    /// <summary>What has been done this match, for the test driver.</summary>
    internal static string Describe() => $"{_cups.Count} other cup(s) known, {_hides} hide(s) and {_shows} reveal(s) this match";

    private static void Forget()
    {
        foreach (var cup in _cups.Values)
        {
            cup.Parts.Clear();
            cup.FoundOn.Clear();
            cup.Off.Clear();
        }
        _cups.Clear();
        _wasRevealing = false;
        _hides = _shows = 0;
    }

    /// <summary>
    /// A roll or a shake: every other seat's dice out of sight until the next reveal. The seats are
    /// the dice seats in the scene, not the manager's roster, which is the server's own and empty
    /// on a client: a first build that read the roster hid nothing on any client.
    /// </summary>
    private static void HideAll()
    {
        if (Manager.Instance == null) return;

        int hidden = 0;
        foreach (var hand in UnityEngine.Object.FindObjectsOfType<DiceGamePlay>())
        {
            try
            {
                var p = hand != null ? hand.GetComponent<PlayerStats>() : null;
                if (p == null || p.isOwned) continue;

                if (!_cups.TryGetValue(hand.Pointer, out var cup))
                    _cups[hand.Pointer] = cup = new Cup { Hand = hand };
                cup.Hidden = true;
                Draw(cup, false);
                hidden++;
            }
            catch (Exception e) { Dev.Warn("hiddendice", $"a seat's dice: {e.Message}"); }
        }
        _hides++;
        LastHideAt = UnityEngine.Time.time;
        Dev.Log("hiddendice", $"{hidden} other seat(s)' dice out of sight until the reveal");
    }

    /// <summary>The reveal: every cup drawn as the game draws it.</summary>
    private static void ShowAll()
    {
        foreach (var cup in _cups.Values)
        {
            if (!cup.Hidden) continue;
            cup.Hidden = false;
            try { if (cup.Hand != null) Draw(cup, true); }
            catch (Exception e) { Dev.Warn("hiddendice", $"a seat's dice at the reveal: {e.Message}"); }
        }
        _shows++;
    }

    /// <summary>
    /// None of the cup's dice drawn on this copy, or, at the reveal, what this switched off put back:
    /// a part the game itself keeps switched off stays off.
    /// </summary>
    private static void Draw(Cup cup, bool show)
    {
        var drawn = cup.Hand.dicerenders;
        if (drawn == null) return;
        if (!SameCup(cup, drawn)) FindParts(cup, drawn);
        foreach (var r in cup.Parts)
        {
            if (r == null) continue;
            if (!show)
            {
                if (!r.enabled) continue;
                r.enabled = false;
                cup.Off.Add(r.Pointer);
            }
            else if (cup.Off.Contains(r.Pointer)) r.enabled = true;
        }
        if (show) cup.Off.Clear();
    }

    /// <summary>
    /// Whether the cup still holds the dice its renderers were found on. Looking them up for every
    /// die on every tick made about 140 new arrays a second at a full table, and the game changes a
    /// cup's dice only when one is lost or put away.
    /// </summary>
    private static bool SameCup(Cup cup, Il2CppSystem.Collections.Generic.List<Dice> drawn)
    {
        if (cup.FoundOn.Count != drawn.Count) return false;
        for (int i = 0; i < drawn.Count; i++)
        {
            var die = drawn[i];
            if ((die != null ? die.Pointer : IntPtr.Zero) != cup.FoundOn[i]) return false;
        }
        return true;
    }

    /// <summary>A die's own model, and the two pictures of its face the game keeps with it.</summary>
    private static void FindParts(Cup cup, Il2CppSystem.Collections.Generic.List<Dice> drawn)
    {
        cup.Parts.Clear();
        cup.FoundOn.Clear();
        for (int i = 0; i < drawn.Count; i++)
        {
            var die = drawn[i];
            cup.FoundOn.Add(die != null ? die.Pointer : IntPtr.Zero);
            if (die == null) continue;
            foreach (var r in die.GetComponentsInChildren<Renderer>(true))
                if (r != null) cup.Parts.Add(r);
            AddOnce(cup, die.dice);
            AddOnce(cup, die.dice2);
        }
    }

    private static void AddOnce(Cup cup, Renderer r)
    {
        if (r == null) return;
        foreach (var have in cup.Parts)
            if (have != null && have.Pointer == r.Pointer) return;
        cup.Parts.Add(r);
    }

    /// <summary>
    /// Each roll, inside the call that makes it, on the host: every other seat's new dice out of
    /// sight before the next frame is drawn. Watching four times a second instead came too late
    /// for some rolls.
    /// </summary>
    [HarmonyPatch]
    internal static class Roll
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(DiceGamePlayManager), nameof(DiceGamePlayManager.AssignAllDiceValues))]
        private static void Rolled()
        {
            try { HideAll(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[hiddendice] at the roll: {e.Message}"); }
        }
    }

    /// <summary>
    /// The cups shaking at the start of a round, on every copy. A client has no roll of anyone
    /// else's to watch, so this is where it hides them.
    /// </summary>
    [HarmonyPatch]
    internal static class Shake
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(DiceGamePlayManager), nameof(DiceGamePlayManager.InvokeUserCode_ShakeTarget__GameObject))]
        private static void Shaken()
        {
            try { HideAll(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[hiddendice] at the shake: {e.Message}"); }
        }
    }
}
