using System;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// The poses a bot's seat would be given by its owner's machine, given by the host instead.
///
/// A seated player's own copy of the game sets a few of their animator's values each frame from
/// what the server has told everyone: shown at the reveal, out of the match, won it, holding cards,
/// taking aim. The seat's network animator then carries those values to every other machine. A
/// bot's seat has no owner, so nobody sets them; but with no owner the server's animator is the one
/// that is carried, so setting them here on the host shows them on every screen.
///
/// Only values the server has already made public go in, which is all the owner's copy uses for
/// them, and one the brain chooses: whether a bot in Liar's Deck or the Chaos deck has its cards
/// raised, as a person holds the look key while choosing what to throw. A bot never looks under its
/// cup in Liar's Dice, because it has nobody to show its dice to; what the host draws of them is
/// <see cref="HiddenDice"/>'s.
///
/// Also, once per seat, the animator is told to run whether or not the host's camera can see the
/// seat: a seat behind the host still has to finish an animation the game waits for, a throw's
/// among them.
/// </summary>
internal static class BotPuppet
{
    // The animator values the owner's copy sets, as the game spells them.
    private const string Shown = "Show";
    private const string Out = "Dead";
    private const string Won = "Winner";
    private const string Holding = "HaveCard";
    private const string Raised = "Look";
    private const string Aiming = "TakeAim";

    /// <summary>From <see cref="ModTicker"/>, on the host.</summary>
    internal static void Tick()
    {
        if (!BotSeats.Any || !NetworkServer.active) return;

        foreach (var seat in BotSeats.Seated())
        {
            try
            {
                var body = seat.Body;
                var anim = body != null ? body.animator : null;
                if (anim == null) continue;

                if (!seat.Animating)
                {
                    anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    seat.Animating = true;
                }

                if (seat.Dice != null) Dice(anim, seat.Player);
                else if (seat.Deck != null) Deck(anim, seat);
                else if (seat.Chaos != null) Chaos(anim, seat);

                if (seat.Player.Winner && !seat.Won)
                {
                    seat.Won = true;
                    Plugin.Log.LogInfo($"[bot] the match is won by a bot, at seat {seat.Player.Slot}");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[bot] seat {SlotOf(seat)}'s pose: {e.Message}");
            }
        }
    }

    /// <summary>What the owner's copy of a dice seat sets: the pose at the reveal, out, and the win.</summary>
    private static void Dice(Animator anim, PlayerStats p)
    {
        anim.SetBool(Out, p.Dead);
        if (p.Winner)
        {
            anim.SetBool(Won, true);
            anim.SetBool(Shown, false);
        }
        else if (!p.Dead)
            anim.SetBool(Shown, p.Show);
    }

    /// <summary>
    /// What the owner's copy of a Liar's Deck seat sets: its cards held while it has any, and raised
    /// while the bot chooses. Only the pose: the seat's own flag for looking is read nowhere but on
    /// its owner's copy, so it stays as it is. The game sets the win and the death on every machine
    /// itself.
    /// </summary>
    private static void Deck(Animator anim, BotSeats.Seat seat)
    {
        bool dead = seat.Player.Dead;
        anim.SetBool(Holding, seat.Deck.HaveCards && !dead);
        anim.SetBool(Raised, seat.Looking && !dead);
    }

    /// <summary>How long after its shot a seat's own copy takes its gun down, as the shot's picture ends.</summary>
    private const float GunDownAfter = 2f;

    /// <summary>
    /// The Chaos deck's: the same, and the aiming pose while it holds the gun. Its death is told to
    /// one machine only, the shooter's, so it is set here as well for every screen. Once its shot
    /// has gone, its gun is taken down the server's way, which tells every machine.
    /// </summary>
    private static void Chaos(Animator anim, BotSeats.Seat seat)
    {
        var gp = seat.Chaos;
        var p = seat.Player;
        if (seat.FiredAt >= 0f && Time.time - seat.FiredAt >= GunDownAfter)
        {
            seat.FiredAt = -1f;
            if (gp.TakingAim)
            {
                gp.ClearAimStateAfterFire();
                if (gp.TakingAim) gp.NetworkTakingAim = false;
                Dev.Log("bot", $"seat {p.Slot}'s gun put away after its shot, as its own copy would");
            }
        }
        bool aiming = gp.TakingAim && !p.Dead;
        anim.SetBool(Out, p.Dead);
        anim.SetBool(Holding, gp.HaveCards && !p.Dead);
        anim.SetBool(Aiming, aiming);
        anim.SetBool(Raised, seat.Looking && !p.Dead && !aiming);
    }

    private static int SlotOf(BotSeats.Seat seat)
    {
        try { return seat.Player != null ? seat.Player.Slot : -1; }
        catch { return -1; }
    }
}
