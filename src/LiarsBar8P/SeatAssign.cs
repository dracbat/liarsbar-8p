using System;
using HarmonyLib;

namespace LiarsBar8P;

/// <summary>
/// Players beyond the fourth connect but end up as ghosts: no character model, no cards,
/// and the turn indicator pointing at an empty seat.
///
/// The cause is CmdSetPlayer throwing "Sequence contains no elements" - it picks a free
/// lobby panel with LINQ First() and there are only four. The command guard stops the
/// player being disconnected, but that command is also what registers them, so the rest
/// of their setup never happens.
///
/// The panel list cannot be extended (LobbySlot is a networked scene object). What can
/// be done is finishing the part that matters: giving them an in-game seat index. This
/// logs every player's assignment so the seating scheme is visible, and fills in a seat
/// for anyone left unassigned.
/// </summary>
internal static class SeatAssign
{
    private static void Report(string when)
    {
        try
        {
            var nm = UnityEngine.Object.FindObjectOfType<CustomNetworkManager>();
            if (nm == null || nm.GamePlayers == null) return;

            var seen = new System.Collections.Generic.List<int>();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < nm.GamePlayers.Count; i++)
            {
                var p = nm.GamePlayers[i];
                if (p == null) continue;
                sb.Append($"  [{i}] '{p.PlayerName}' idNo={p.PlayerIdNumber} seat={p.InGameSlot}");
                seen.Add(p.InGameSlot);
            }
            Plugin.Log.LogInfo($"[seatassign] {when}:{sb}");

            // duplicates mean two players share a seat - worth shouting about
            for (int i = 0; i < seen.Count; i++)
                for (int j = i + 1; j < seen.Count; j++)
                    if (seen[i] == seen[j])
                        Plugin.Log.LogWarning($"[seatassign] DUPLICATE seat {seen[i]} - two players share it");
        }
        catch (Exception e) { Plugin.Log.LogError($"[seatassign] {e.Message}"); }
    }

    /// <summary>
    /// After CmdSetPlayer, give a seat to anyone who did not get one. Seats are taken
    /// from the lowest index not already claimed, so the ring stays contiguous.
    /// </summary>
    // Harmony chains finalizers, and one returning null clears the exception for the
    // rest. This one acts on the failure, so it must run before CommandGuard swallows
    // it, and it passes the exception along so that guard still reports it.
    [HarmonyFinalizer]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(PlayerObjectController), nameof(PlayerObjectController.CmdSetPlayer))]
    private static Exception AssignSeat(Exception __exception, PlayerObjectController __instance)
    {
        try
        {
            if (__exception == null) { Report("after CmdSetPlayer"); return null; }

            var nm = UnityEngine.Object.FindObjectOfType<CustomNetworkManager>();
            if (nm == null || nm.GamePlayers == null) return null;

            var taken = new System.Collections.Generic.HashSet<int>();
            foreach (var p in nm.GamePlayers)
                if (p != null && p != __instance) taken.Add(p.InGameSlot);

            int seat = 0;
            while (taken.Contains(seat) && seat < Plugin.MaxPlayers.Value) seat++;

            Plugin.Log.LogWarning(
                $"[seatassign] '{__instance.PlayerName}' got no seat from CmdSetPlayer " +
                $"(no free lobby panel) - assigning seat {seat}");
            __instance.InGameSlot = seat;

            Report("after manual assignment");
        }
        catch (Exception e) { Plugin.Log.LogError($"[seatassign] {e.Message}"); }
        return __exception;   // let CommandGuard report and clear it
    }
}
