using System;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Lets a claim in Liar's Spin be as large as the table it is made about.
///
/// A round of Liar's Spin is a claim about how many of something are showing across everyone's
/// reels. Each player has four, so a full and honest count at four players tops out at sixteen
/// and at eight players at thirty-two. The ceiling on what can be claimed is
/// <c>LiarsSpinGameplayManager.MaxCount</c> - a plain number set in the scene, which the bid
/// keys clamp and wrap against, and which nothing in the game ever recomputes.
///
/// If that number was set for four players then above four the mode quietly stops working as a
/// game. It does not crash and nothing in a log complains: bidding simply cannot go past the
/// halfway point of what is honestly on the table, so the claims that make the last half of a
/// round interesting cannot be made at all, and a player wondering why the number will not go
/// any higher has nothing to tell them. That is a worse failure than a crash, because it looks
/// like the mode working.
///
/// So the ceiling is raised to four reels per seat, and both the shipped value and the new one
/// are put in the log - the shipped value because it had never been read, and because if it
/// turns out to be generous already this changes nothing and should say so. It is only ever
/// raised, never lowered: a table smaller than four keeps whatever the game shipped.
/// </summary>
internal static class SpinBidCap
{
    /// <summary>Reels in front of each player. Four, and not derived from the seat count.</summary>
    private const int ReelsPerSeat = 4;

    private static LiarsSpinGameplayManager _done;

    internal static void Tick()
    {
        try
        {
            var m = Manager.Instance;
            if (m == null || !m.GameStarted) { _done = null; return; }

            var spin = m.SpinGame;
            if (spin == null || ReferenceEquals(_done, spin)) return;

            int n = AimRing.Seats(m);
            if (n < 2) return;

            _done = spin;

            int shipped = spin.MaxCount;
            int wanted = ReelsPerSeat * n;

            if (shipped >= wanted)
            {
                Plugin.Log.LogInfo(
                    $"[spin] the highest claim allowed is {shipped}, and {n} players have " +
                    $"{wanted} reels between them - the shipped ceiling is high enough, left alone");
                return;
            }

            spin.MaxCount = wanted;
            Plugin.Log.LogWarning(
                $"[spin] the highest claim allowed was {shipped} but {n} players have {wanted} " +
                $"reels between them - raised to {wanted}, so a true claim can still be made");
        }
        catch (Exception e) { Plugin.Log.LogError($"[spin] could not check the bid ceiling: {e.Message}"); }
    }
}
