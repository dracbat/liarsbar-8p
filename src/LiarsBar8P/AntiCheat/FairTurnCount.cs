using System;
using System.Collections.Generic;
using HarmonyLib;
using Mirror;

namespace LiarsBar8P;

/// <summary>
/// Counts each seat's turns on the host, so a check can tell "a second move in the same turn"
/// from "the first move of a new one".
///
/// Why a count and not the turn flag itself: in Liar's Texas and Liar's Spin the turn is handed
/// on a moment after a move (the game waits for the animation, then clears the flag and gives the
/// turn to the next seat), so for that moment a seat that has already moved still holds the turn.
/// The host accepts one move of each kind per turn, and a turn counted from the flag being set
/// tells the turns apart without guessing at timings.
///
/// Every write of the turn flag on the host goes through the game's own setter
/// (<c>PlayerStats.set_NetworkHaveTurn</c>; checked in the dump: no mode writes the field
/// directly on the server, only the client's own sync reader does), and so does every write the
/// mod makes (bots, the turn kickstart, the developer commands). So a turn is counted every time
/// a seat is given it, including a turn given again to a seat that already held it, which only
/// ever makes a check more lenient, never stricter.
/// </summary>
[FairPatch("the host counts each seat's turns")]
[HarmonyPatch(typeof(PlayerStats), nameof(PlayerStats.NetworkHaveTurn), MethodType.Setter)]
internal static class FairTurnCount
{
    private static readonly Dictionary<uint, int> _turn = new();
    private static readonly Dictionary<(uint Seat, string What), int> _acted = new();
    private static int _match = -1;

    [HarmonyPostfix]
    private static void Set(PlayerStats __instance)
    {
        try
        {
            if (!NetworkServer.active || __instance == null || !__instance.HaveTurn) return;
            Fresh();
            uint seat = __instance.netId;
            _turn.TryGetValue(seat, out int n);
            _turn[seat] = n + 1;
        }
        catch { }
    }

    /// <summary>A new match starts every seat's count over.</summary>
    private static void Fresh()
    {
        if (_match == Fair.Match) return;
        _match = Fair.Match;
        _turn.Clear();
        _acted.Clear();
    }

    /// <summary>Which turn this seat is on, as the host counts them.</summary>
    internal static int Of(PlayerStats p)
    {
        Fresh();
        if (p == null) return 0;
        _turn.TryGetValue(p.netId, out int n);
        return n;
    }

    /// <summary>Whether this seat has already made a move of this kind in the turn it holds now.</summary>
    internal static bool Already(PlayerStats p, string what)
    {
        Fresh();
        if (p == null) return false;
        return _acted.TryGetValue((p.netId, what), out int at) && at == Of(p);
    }

    /// <summary>Remember that this seat has made a move of this kind in the turn it holds now.</summary>
    internal static void Acted(PlayerStats p, string what)
    {
        Fresh();
        if (p != null) _acted[(p.netId, what)] = Of(p);
    }

    /// <summary>Forget the moves a seat made this turn: for a test that must not leave a mark.</summary>
    internal static void Forget(PlayerStats p, string what)
    {
        Fresh();
        if (p != null) _acted.Remove((p.netId, what));
    }
}
