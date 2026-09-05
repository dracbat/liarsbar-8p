using System;
using HarmonyLib;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Spaces the occupied seats evenly around the table for the number of players actually
/// present.
///
/// The seat list is expanded to the configured maximum so indices exist, but the ring was
/// then laid out for that maximum: with five players in an eight seat ring the occupied
/// seats 0-4 covered only half the table, leaving players bunched together with empty
/// gaps opposite - and the turn indicator pointing into one of those gaps.
///
/// This cannot be done at Manager.Start, where Players.Count is still 0. It runs when the
/// round is set up and the roster is known, and re-runs if the count changes.
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
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.ResetRound))]
    private static void Space(DeckGamePlayManager __instance, bool first)
    {
        try
        {
            var m = Manager.Instance;
            if (m == null || m.Slots == null || m.Slots.Count == 0) return;

            int players = PlayerCount();
            if (players < 2) return;
            if (players == _lastCount) return;
            _lastCount = players;

            var slots = m.Slots;
            int n = Mathf.Min(players, slots.Count);

            // fit the ring from the seats the game shipped with, not from ones we moved
            var pts = new Vector2[Mathf.Min(4, slots.Count)];
            float y = 0f;
            for (int i = 0; i < pts.Length; i++)
            {
                pts[i] = new Vector2(slots[i].position.x, slots[i].position.z);
                y += slots[i].position.y;
            }
            y /= pts.Length;

            Geometry.FitCircle(pts, out Vector2 c, out float r);

            float start = Mathf.Atan2(slots[0].position.z - c.y, slots[0].position.x - c.x) * Mathf.Rad2Deg;
            float step = 360f / n;

            Plugin.Log.LogInfo(
                $"[seatring] {players} players -> spacing {n} seats {step:F1}deg apart " +
                $"around ({c.x:F2}, {c.y:F2}) r={r:F2}");

            for (int i = 0; i < n; i++)
            {
                var t = slots[i];
                if (t == null) continue;
                float a = start + step * i;
                float rad = a * Mathf.Deg2Rad;
                t.position = new Vector3(c.x + r * Mathf.Cos(rad), y, c.y + r * Mathf.Sin(rad));
                t.rotation = Quaternion.Euler(
                    0f, Mathf.Atan2(-Mathf.Cos(rad), -Mathf.Sin(rad)) * Mathf.Rad2Deg, 0f);
            }

            // park any unused seats on the ring behind the others so nothing sits mid-table
            for (int i = n; i < slots.Count; i++)
            {
                var t = slots[i];
                if (t == null) continue;
                float a = start + step * (i % Mathf.Max(1, n));
                float rad = a * Mathf.Deg2Rad;
                t.position = new Vector3(c.x + r * 1.9f * Mathf.Cos(rad), y - 4f, c.y + r * 1.9f * Mathf.Sin(rad));
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[seatring] spacing failed: {e.Message}");
        }
    }
}
