using System;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Something to watch once you are out: the game's own spectator cameras, plus one for each
/// other seat at the table, so A and D (or the pad) walk round the players as well as through
/// the game's fixed views.
///
/// Eleven seconds after you die the game switches its spectator cameras on, and A and D step
/// through the children of its spectator camera parent: the one showing goes off, the next
/// comes on, and the count wraps on however many children there are. Every seat already has a
/// camera of its own, the one the game cuts to when that seat wins. A copy of each, put among
/// those children, joins the cycle with no patch at all: the game steps, wraps and switches
/// them itself.
///
/// Copies, never the seats' own cameras, because the win still needs those where they are. They
/// are made once a match, the first moment this copy is spectating, switched off, in seat order
/// from the seat after yours, and placed straight after whichever camera is showing, so the next
/// press goes to the next seat. They are scene objects and leave with the scene.
///
/// They show what that seat's winner camera shows: a face and the table, with everyone's cards,
/// dice, reels and hole cards as hidden as they are from any chair. Nothing is sent or read for
/// them. Only at a modded table, which also keeps them out of ranked, and the Dice table's
/// camera mover leaves them where they are.
///
/// Except Liar's Texas at four seats or fewer, and Two-Card Poker (<see cref="TwoCardPoker"/>) at
/// any size, which get none. Above four, <see cref="TexasSwap"/> has every machine hide the cards
/// of the seats it does not own, and a copy that has seat cameras keeps hiding them if the table
/// then shrinks.
/// </summary>
internal static class SeatCams
{
    /// <summary>What every copy's name starts with, so other code can tell them from the game's own.</summary>
    internal const string Prefix = "LB8P_Seat";

    private static int _match;
    private static bool _done;
    private static int _failures;

    /// <summary>Seat cameras added this match, for the log and the test driver.</summary>
    internal static int Added { get; private set; }

    /// <summary>From <see cref="ModTicker"/>.</summary>
    internal static void Tick()
    {
        var m = Manager.Instance;
        if (m == null)
        {
            _match = 0;
            _done = false;
            Added = 0;
            return;
        }

        // A new match gets a new Manager, and new spectator cameras with it.
        int id = m.GetInstanceID();
        if (id != _match)
        {
            _match = id;
            _done = false;
            _failures = 0;
            Added = 0;
        }
        if (_done || _failures >= 3) return;

        var me = DiceTable.Local;
        if (me == null || !me.spectatormode) return;

        // Decided once, the moment this copy starts spectating: a table that is not modded then
        // keeps the game's own cameras for the rest of the match.
        _done = true;
        if (!Table.Modded) return;

        try { Add(m, me); }
        catch (Exception e)
        {
            _done = false;
            _failures++;
            Plugin.Log.LogWarning($"[seatcam] could not add the seat cameras: {e.Message}");
        }
    }

    private static void Add(Manager m, CharController me)
    {
        var parent = m.SpectatorCameraParrent != null ? m.SpectatorCameraParrent.transform : null;
        if (parent == null || parent.childCount == 0 || m.Slots == null || me.playerStats == null) return;

        int seats = Math.Min(DiceTable.Seats, m.Slots.Count);
        int mine = me.playerStats.Slot;
        if (seats < 2 || mine < 0 || mine >= seats) return;

        // A Texas seat carries the mode's gameplay component; no other mode's does. Asked of
        // TexasSwap rather than worked out here, so a table it could not read gets no cameras.
        if (me.playerStats.GetComponent<TexasGamePlay>() != null && !TexasSwap.HidesCards)
        {
            Plugin.Log.LogInfo($"[seatcam] none added: Liar's Texas gets them only above {Limits.VanillaPlayers} seats " +
                               $"(this copy is seat {mine} of {seats})");
            return;
        }

        // Two-Card Poker gets none either, at any size: nobody has shown that a camera this close
        // to a seat never catches a card there.
        if (me.playerStats.GetComponent<PokerGamePlay>() != null)
        {
            Plugin.Log.LogInfo($"[seatcam] none added: Two-Card Poker (this copy is seat {mine} of {seats})");
            return;
        }

        int vanilla = parent.childCount;
        int showing = Mathf.Clamp(me.speccamindex, 0, vanilla - 1);

        // Made under a switched-off holder: a copy made straight under the live parent would wake
        // for a frame, and a camera that wakes takes the view.
        var hold = new GameObject("LB8P_SeatHold");
        hold.SetActive(false);
        int added = 0;
        try
        {
            for (int k = 1; k < seats; k++)
            {
                int s = (mine + k) % seats;
                var seat = m.Slots[s];
                var slot = seat != null ? seat.GetComponent<Slot>() : null;
                var from = slot != null ? slot.Cameraa : null;
                if (from == null) continue;

                var cam = UnityEngine.Object.Instantiate(from, hold.transform, true);
                cam.name = Prefix + s;
                cam.SetActive(false);
                cam.transform.SetParent(parent, true);
                cam.transform.SetSiblingIndex(showing + 1 + added);
                added++;
            }
        }
        finally { UnityEngine.Object.Destroy(hold); }

        Added = added;
        Plugin.Log.LogInfo($"[seatcam] {added} seat camera(s) added after the game's own {vanilla} " +
                           $"(this copy is seat {mine} of {seats})");
    }
}
