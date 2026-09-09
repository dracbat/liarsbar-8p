using System;
using HarmonyLib;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Says out loud when a mode's own mechanic fires, and at what size of table.
///
/// "Liar's Deck works at eight players" has meant three different things over this project's
/// life, each weaker than it sounded: the caps were raised, then everyone was seated, then
/// everyone was dealt. None of those is the game. The deck variants a player picks from the
/// lobby arrows differ in exactly one respect - the special card and what it does - so a run
/// that deals eight hands and never once puts a devil or a chaos card on the table has not
/// tested the variant at all, only the parts every variant shares.
///
/// These are the moments where a variant stops being Liar's Deck:
///
/// <list type="bullet">
/// <item>the <b>devil's deal</b>, offered when a liar call or a spot-on call resolves in the
/// Devil deck - the game runs it from inside the resolve routine rather than from any button,
/// so it needs a call to be made and then followed through;</item>
/// <item>a <b>chaos card</b> thrown in the Chaos deck, which stops the round and sends the
/// table into the aiming phase.</item>
/// </list>
///
/// Each is reported with the seat that caused it and how many were at the table, because the
/// question being asked is never "does this work" but "does this still work with eight".
/// </summary>
[HarmonyPatch]
internal static class MechanicsTrace
{
    private static int Seats()
    {
        try
        {
            var m = Manager.Instance;
            return m != null && m.Players != null ? m.Players.Count : -1;
        }
        catch { return -1; }
    }

    private static string Who(Component c)
    {
        try
        {
            if (c == null) return "somebody";
            var stats = c.GetComponent<PlayerStats>();
            if (stats == null) return c.gameObject != null ? c.gameObject.name : "somebody";
            return $"'{stats.PlayerName}' (seat {stats.Slot})";
        }
        catch { return "somebody"; }
    }

    // ------------------------------------------------------------------ the devil

    /// <summary>
    /// The devil's deal, taken. Reached from the liar and spot-on resolve routines, so it is
    /// the tail end of a round rather than a move somebody makes.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.StartDevilProcesses))]
    private static void DevilsDeal(DeckGameplay __instance)
    {
        try
        {
            Plugin.Log.LogWarning(
                $"[mechanic] DEVIL'S DEAL started for {Who(__instance)} at a table of {Seats()}");
            _devils++;
        }
        catch { }
    }

    /// <summary>The devil collecting. Worth a line of its own: it is the outcome, not the offer.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.PlayDevilsDeadSound))]
    private static void DevilTookSomeone()
    {
        try { Plugin.Log.LogWarning($"[mechanic] the devil collected at a table of {Seats()}"); }
        catch { }
    }

    /// <summary>
    /// A devil card was on the table when the round ended - which is what makes the deal
    /// available in the first place.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.LastRoundHasDevilCard))]
    private static void DevilCardOnTable(bool __result)
    {
        try
        {
            if (!__result || !Plugin.Verbose.Value) return;
            Plugin.Log.LogInfo($"[mechanic] a devil card was on the table this round (table of {Seats()})");
        }
        catch { }
    }

    // ------------------------------------------------------------------- the chaos

    /// <summary>A chaos card reached the table and the round turned into an aiming phase.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ChaosDeckGamePlayManager), nameof(ChaosDeckGamePlayManager.BeginChaosFromThrow))]
    private static void ChaosThrown(GameObject thrower,
                                    Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> thrownTypes)
    {
        try
        {
            string cards = "nothing";
            if (thrownTypes != null && thrownTypes.Length > 0)
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < thrownTypes.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(thrownTypes[i]);
                }
                cards = sb.ToString();
            }

            string who = thrower != null ? Who(thrower.transform) : "somebody";
            Plugin.Log.LogWarning(
                $"[mechanic] CHAOS thrown by {who} at a table of {Seats()} - cards {cards}");
            _chaos++;
        }
        catch { }
    }

    /// <summary>The aiming phase finishing is what says the chaos card resolved rather than hung.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ChaosDeckGamePlayManager), nameof(ChaosDeckGamePlayManager.FinishChaosAimPhase))]
    private static void ChaosResolved()
    {
        try
        {
            Plugin.Log.LogWarning($"[mechanic] chaos aim resolved at a table of {Seats()}");
            _chaosDone++;
        }
        catch { }
    }

    /// <summary>
    /// Who the server decided the shot landed on. The ground truth for the aiming ring.
    ///
    /// The seat a player chose and the seat the game shot are two different claims, and until
    /// this was printed only the first was ever visible. Above four players they came apart:
    /// the aim table had no row for seats four and up, so a choice made by one of them
    /// resolved to nobody and the shot quietly went nowhere. Reporting both is what turns
    /// "the chaos card fired" into "the chaos card hit the person it was pointed at".
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ChaosDeckGameplay), "ServerApplyShotToTarget")]
    private static void ShotLanded(ChaosDeckGameplay __instance, PlayerStats player, bool __result)
    {
        try
        {
            _shots++;
            string who = "nobody";
            if (player != null)
            {
                string n = player.PlayerName;
                who = string.IsNullOrEmpty(n) ? $"seat {player.Slot}" : $"'{n}' (seat {player.Slot})";
            }

            Plugin.Log.LogWarning(
                $"[mechanic] the shot from {Who(__instance)} landed on {who}" +
                (__result ? "" : " - and was refused") + $" at a table of {Seats()}");
        }
        catch { }
    }

    // -------------------------------------------------------------------- the dice

    /// <summary>
    /// A bid. Liar's Dice is a bidding game, so this is the move the mode is made of - the
    /// equivalent of a card reaching the table, and the thing that has to be seen happening
    /// at eight players before the mode can be called tested.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(DiceGamePlay), nameof(DiceGamePlay.PlaceBet))]
    private static void DiceBid(DiceGamePlay __instance, int count, int dice)
    {
        try
        {
            _bids++;
            if (Plugin.Verbose != null && Plugin.Verbose.Value)
                Plugin.Log.LogInfo($"[mechanic] DICE bid {count} x {dice} by {Who(__instance)} " +
                                   $"at a table of {Seats()}");
        }
        catch { }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DiceGamePlay), nameof(DiceGamePlay.CallLier))]
    private static void DiceLiar(DiceGamePlay __instance)
    {
        try
        {
            _diceLiar++;
            Plugin.Log.LogWarning($"[mechanic] DICE liar called by {Who(__instance)} at a table of {Seats()}");
        }
        catch { }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DiceGamePlay), nameof(DiceGamePlay.CallSpotOn))]
    private static void DiceSpotOn(DiceGamePlay __instance)
    {
        try
        {
            _diceSpotOn++;
            Plugin.Log.LogWarning($"[mechanic] DICE spot-on called by {Who(__instance)} at a table of {Seats()}");
        }
        catch { }
    }

    // ------------------------------------------------------------------- the texas

    /// <summary>
    /// A seat matching the stake, or shoving everything in. The move Liar's Texas is made of.
    ///
    /// Patched on the server-side body rather than on the command, so it counts what the table
    /// actually did with the move rather than what somebody tried to send. A command that
    /// Mirror drops for want of authority never reaches here, which is the point: a run where
    /// every move was refused should not read like a run where every move was made.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.UserCode_CMDRised__Boolean))]
    private static void TexasRaised(TexasGamePlay __instance, bool AllInN)
    {
        try
        {
            _raises++;
            if (AllInN) _allIns++;
            if (Plugin.Verbose != null && Plugin.Verbose.Value)
                Plugin.Log.LogInfo($"[mechanic] TEXAS {(AllInN ? "all in" : "call")} by {Who(__instance)} " +
                                   $"at a table of {Seats()}");
        }
        catch { }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.UserCode_CMDFolded))]
    private static void TexasFolded(TexasGamePlay __instance)
    {
        try
        {
            _folds++;
            if (Plugin.Verbose != null && Plugin.Verbose.Value)
                Plugin.Log.LogInfo($"[mechanic] TEXAS fold by {Who(__instance)} at a table of {Seats()}");
        }
        catch { }
    }

    // -------------------------------------------------------------------- the spin

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SpinGamePlay), nameof(SpinGamePlay.UserCode_RiseCmd__Int32))]
    private static void SpinClaim(SpinGamePlay __instance, int count)
    {
        try
        {
            _claims++;
            if (Plugin.Verbose != null && Plugin.Verbose.Value)
                Plugin.Log.LogInfo($"[mechanic] SPIN claim of {count} by {Who(__instance)} " +
                                   $"at a table of {Seats()}");
        }
        catch { }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SpinGamePlay), nameof(SpinGamePlay.UserCode_CallLiar))]
    private static void SpinLiar(SpinGamePlay __instance)
    {
        try
        {
            _spinLiar++;
            Plugin.Log.LogWarning($"[mechanic] SPIN liar called by {Who(__instance)} at a table of {Seats()}");
        }
        catch { }
    }

    // ------------------------------------------------------------------ the tally

    private static int _devils, _chaos, _chaosDone;
    private static int _bids, _diceLiar, _diceSpotOn;
    private static int _shots;
    private static int _raises, _allIns, _folds, _claims, _spinLiar;

    /// <summary>
    /// What fired during this match, printed once when it ends.
    ///
    /// A run that never reaches a mechanic looks exactly like a run where the mechanic is
    /// broken until somebody says which it was, and a count of zero is the only thing that
    /// can say so.
    /// </summary>
    internal static void MatchOver()
    {
        if (_devils == 0 && _chaos == 0 && _bids == 0 && _diceLiar == 0 && _diceSpotOn == 0 &&
            _shots == 0 && _raises == 0 && _folds == 0 && _claims == 0) return;

        Plugin.Log.LogWarning(
            $"[mechanic] this match: {_devils} devil's deal(s), {_chaos} chaos throw(s) " +
            $"({_chaosDone} resolved, {_shots} aimed shot(s)), {_bids} dice bid(s), " + $"{_diceLiar} dice liar call(s), " +
            $"{_diceSpotOn} spot-on call(s), {_raises} texas call(s) ({_allIns} all in), " +
            $"{_folds} fold(s), {_claims} spin claim(s), {_spinLiar} spin liar call(s)");

        _devils = 0;
        _chaos = 0;
        _chaosDone = 0;
        _bids = 0;
        _diceLiar = 0;
        _diceSpotOn = 0;
        _shots = 0;
        _raises = 0;
        _allIns = 0;
        _folds = 0;
        _claims = 0;
        _spinLiar = 0;
    }
}
