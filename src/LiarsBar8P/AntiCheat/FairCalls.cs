using System;
using HarmonyLib;
using Mirror;

namespace LiarsBar8P;

/// <summary>
/// Fair play for the liar and spot-on calls of Liar's Deck, the Chaos deck and Liar's Dice.
///
/// At a table this copy hosts, a call from a player's copy is played only when the calling seat
/// is in play and holds the turn, as the rules say. A call just after the turn moved on, or from a
/// seat that is out or on its way out, is refused quietly: the call is dropped and the round goes
/// on, and nobody is removed for it. The game's own calls (bots, the host's own seat, the
/// game's own automatic call) are never checked, and an accepted call runs the game's own handler.
/// </summary>
[FairPatch("liar and spot-on calls only from a seat in play, on its turn")]
[HarmonyPatch]
internal static class FairCalls
{
    internal static FairThrows.Verdict Judge(bool dead, bool longDead, bool turn, out string why)
    {
        if (longDead) { why = "a call from a seat out of the game since before this deal"; return FairThrows.Verdict.Refuse; }
        if (dead) { why = "a call from a seat that is out"; return FairThrows.Verdict.Refuse; }
        if (!turn) { why = "a call out of turn"; return FairThrows.Verdict.Refuse; }
        why = "a call on the caller's turn";
        return FairThrows.Verdict.Accept;
    }

    private static bool Check(NetworkBehaviour obj, NetworkConnectionToClient sender, string what)
    {
        try
        {
            if (!Fair.Hosting || !Fair.IsRemote(sender) || obj == null) return true;
            var owner = obj.connectionToClient;
            if (owner == null || owner.connectionId != sender.connectionId) return true;
            var p = obj.GetComponent<PlayerStats>();
            if (p == null) return true;

            bool dead = p.Dead || HostRevolver.Falling(obj);
            var v = Judge(dead, FairHands.LongDead(p), p.HaveTurn, out string why);
            switch (v)
            {
                case FairThrows.Verdict.Caught:
                    Fair.Caught(sender, $"{what} (seat {p.Slot}): {why}");
                    return false;
                case FairThrows.Verdict.Refuse:
                    Fair.Refused($"connection {sender.connectionId} {what} (seat {p.Slot}): {why}");
                    return false;
                default:
                    return true;
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"{Fair.Tag} {what} not checked: {e.Message}");
            return true;
        }
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.InvokeUserCode_CmdCallLiar))]
    private static bool DeckLiar(NetworkBehaviour obj, NetworkConnectionToClient senderConnection) =>
        Check(obj, senderConnection, "Liar's Deck liar call");

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.InvokeUserCode_CmdCallSpotOn))]
    private static bool DeckSpotOn(NetworkBehaviour obj, NetworkConnectionToClient senderConnection) =>
        Check(obj, senderConnection, "Liar's Deck spot-on call");

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.InvokeUserCode_CmdCallLiar))]
    private static bool ChaosLiar(NetworkBehaviour obj, NetworkConnectionToClient senderConnection) =>
        Check(obj, senderConnection, "Chaos deck liar call");

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(DiceGamePlay), nameof(DiceGamePlay.InvokeUserCode_CallLier))]
    private static bool DiceLiar(NetworkBehaviour obj, NetworkConnectionToClient senderConnection) =>
        Check(obj, senderConnection, "Liar's Dice liar call");

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(DiceGamePlay), nameof(DiceGamePlay.InvokeUserCode_CallSpotOn))]
    private static bool DiceSpotOn(NetworkBehaviour obj, NetworkConnectionToClient senderConnection) =>
        Check(obj, senderConnection, "Liar's Dice spot-on call");
}
