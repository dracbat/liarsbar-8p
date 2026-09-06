using System;
using HarmonyLib;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Spaces the occupied seats evenly for the number of players present, and moves the
/// players onto them.
///
/// The seat list is expanded to the configured maximum so the indices exist, but laying
/// the ring out for that maximum left five players occupying seats 0-4 of an eight seat
/// ring - half the table, bunched together with empty gaps opposite.
///
/// Re-spacing alone is not enough: bodies are placed when they spawn, using wherever the
/// seat was at that moment, so moving a seat transform afterwards leaves the character
/// behind. The turn indicator follows the seat, which is why it pointed at empty space
/// while a player acted from somewhere else. Players are therefore moved with their
/// seats.
///
/// This runs at round setup rather than Manager.Start, where Players.Count is still zero.
/// </summary>
internal static class SeatRing
{
    private static int _lastCount = -1;

    private static int PlayerCount()
    {
        try
        {
            var m = Manager.Instance;
            if (m == null) return 0;
            if (m.Players != null && m.Players.Count > 0) return m.Players.Count;
            if (m.StartPlayerCount > 0) return m.StartPlayerCount;
        }
        catch { }
        return 0;
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.Last)]    // after the roster is corrected and seats compacted
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.ResetRound))]
    private static void Space(DeckGamePlayManager __instance, bool first)
    {
        try
        {
            var m = Manager.Instance;
            if (m == null || m.Slots == null || m.Slots.Count == 0) return;

            int players = PlayerCount();
            if (players < 2) return;

            var slots = m.Slots;
            int n = Mathf.Min(players, slots.Count);

            // fit the ring from the seats the game shipped with, so repeated rounds
            // cannot drift as a result of seats this mod has already moved
            int baseCount = Mathf.Min(Limits.VanillaPlayers, slots.Count);
            var pts = new Vector2[baseCount];
            float y = 0f;
            for (int i = 0; i < baseCount; i++)
            {
                pts[i] = new Vector2(slots[i].position.x, slots[i].position.z);
                y += slots[i].position.y;
            }
            y /= baseCount;

            Geometry.FitCircle(pts, out Vector2 c, out float r);

            float start = Mathf.Atan2(slots[0].position.z - c.y, slots[0].position.x - c.x) * Mathf.Rad2Deg;
            float step = 360f / n;

            if (players != _lastCount)
            {
                _lastCount = players;
                Plugin.Log.LogInfo(
                    $"[seatring] {players} players -> {n} seats {step:F1}deg apart " +
                    $"around ({c.x:F2}, {c.y:F2}) r={r:F2}");
            }

            for (int i = 0; i < n; i++)
            {
                var t = slots[i];
                if (t == null) continue;
                float rad = (start + step * i) * Mathf.Deg2Rad;
                t.position = new Vector3(c.x + r * Mathf.Cos(rad), y, c.y + r * Mathf.Sin(rad));
                t.rotation = Quaternion.Euler(
                    0f, Mathf.Atan2(-Mathf.Cos(rad), -Mathf.Sin(rad)) * Mathf.Rad2Deg, 0f);
            }

            // unused seats go below and outside so nothing is left standing mid-table
            for (int i = n; i < slots.Count; i++)
            {
                var t = slots[i];
                if (t == null) continue;
                float rad = (start + step * (i % Mathf.Max(1, n))) * Mathf.Deg2Rad;
                t.position = new Vector3(
                    c.x + r * 1.9f * Mathf.Cos(rad), y - 4f, c.y + r * 1.9f * Mathf.Sin(rad));
            }

            SnapPlayersToSeats(m);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[seatring] spacing failed: {e.Message}");
        }
    }

    /// <summary>
    /// Put each player where their seat now is. Server only: a client moving a networked
    /// player would fight Mirror's synchronisation.
    /// </summary>
    private static void SnapPlayersToSeats(Manager m)
    {
        try
        {
            if (m.Players == null || m.Slots == null) return;
            try { if (!m.isServer) return; } catch { }

            int moved = 0;
            for (int i = 0; i < m.Players.Count; i++)
            {
                var p = m.Players[i];
                if (p == null) continue;

                int slot = p.Slot;
                if (slot < 0 || slot >= m.Slots.Count)
                {
                    Plugin.Log.LogWarning(
                        $"[seatring] '{p.PlayerName}' holds slot {slot}, outside 0..{m.Slots.Count - 1}");
                    continue;
                }

                var seat = m.Slots[slot];
                var root = p.transform != null ? p.transform.root : null;
                if (seat == null || root == null) continue;

                if (Vector3.Distance(root.position, seat.position) > 0.05f)
                {
                    root.position = seat.position;
                    root.rotation = seat.rotation;
                    moved++;
                }
            }

            if (moved > 0) Plugin.Log.LogInfo($"[seatring] moved {moved} player(s) onto their seats");
        }
        catch (Exception e) { Plugin.Log.LogError($"[seatring] snap failed: {e.Message}"); }
    }
}
