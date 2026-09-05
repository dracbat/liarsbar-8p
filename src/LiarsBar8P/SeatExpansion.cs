using HarmonyLib;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// The in-game table has four seats (<c>Manager.Slots</c>) and four nameplates
/// (<c>Manager.NameTexts</c>). The game picks a free seat with LINQ, so four seats is a
/// hard ceiling: a fifth player produces "Sequence contains no elements", exactly as the
/// lobby podiums did.
///
/// Captured from a live match, the seats sit on an exact circle:
///   centre (0.353, 0.111, -8.909), radius 1.330, 90 degrees apart, each facing centre.
/// Rather than hardcode that, the circle is fitted from whatever seats exist and new
/// seats are inserted into the widest gaps, so the ring stays even for any player count
/// and the original four never move.
/// </summary>
internal static class SeatExpansion
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Manager), nameof(Manager.Start))]
    private static void Expand(Manager __instance)
    {
        try
        {
            int want = Plugin.MaxPlayers.Value;
            ExpandSeats(__instance, want);
            ExpandNameplates(__instance, want);
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogError($"[seats] expansion failed: {e}");
        }
    }

    private static void ExpandSeats(Manager m, int want)
    {
        var slots = m.Slots;
        if (slots == null || slots.Count == 0 || slots.Count >= want) return;

        int have = slots.Count;
        Plugin.Log.LogInfo($"[seats] expanding table seats {have} -> {want}");

        var pts = new Vector2[have];
        float y = 0f;
        for (int i = 0; i < have; i++)
        {
            var t = slots[i];
            pts[i] = new Vector2(t.position.x, t.position.z);
            y += t.position.y;
        }
        y /= have;

        Geometry.FitCircle(pts, out Vector2 c, out float r);
        Plugin.Log.LogInfo($"[seats] ring fit: centre=({c.x:F3}, {c.y:F3}) radius={r:F3}");

        // existing angles, normalised
        var angles = new System.Collections.Generic.List<float>();
        for (int i = 0; i < have; i++)
        {
            float a = Mathf.Atan2(pts[i].y - c.y, pts[i].x - c.x) * Mathf.Rad2Deg;
            angles.Add((a % 360f + 360f) % 360f);
        }

        var template = slots[have - 1];

        for (int k = 0; k < want - have; k++)
        {
            float newAngle = WidestGapMidpoint(angles);
            angles.Add(newAngle);
            angles.Sort();

            float rad = newAngle * Mathf.Deg2Rad;
            var pos = new Vector3(c.x + r * Mathf.Cos(rad), y, c.y + r * Mathf.Sin(rad));

            // face the centre of the table: verified against all four original seats
            float yaw = Mathf.Atan2(-Mathf.Cos(rad), -Mathf.Sin(rad)) * Mathf.Rad2Deg;

            var seat = Markers.Create(template, $"Spawn{have + k + 1}_8P", pos, yaw);
            slots.Add(seat);

            Plugin.Log.LogInfo(
                $"[seats]   + {seat.gameObject.name} pos={pos.ToString("F3")} yaw={yaw:F1} (ring {newAngle:F1}deg)");
        }

        Plugin.Log.LogInfo($"[seats] table seats now {slots.Count}");
    }

    /// <summary>Angle bisecting the largest empty arc, so the ring stays even.</summary>
    private static float WidestGapMidpoint(System.Collections.Generic.List<float> sorted)
    {
        sorted.Sort();
        int n = sorted.Count;
        float best = -1f, at = 0f;
        for (int i = 0; i < n; i++)
        {
            float a = sorted[i];
            float b = sorted[(i + 1) % n];
            float gap = b - a;
            if (gap <= 0f) gap += 360f;
            if (gap > best) { best = gap; at = a + gap * 0.5f; }
        }
        return (at % 360f + 360f) % 360f;
    }

    /// <summary>
    /// Nameplates hang above each seat. Their offset from their own seat is reused so a
    /// new plate sits the same way over its new seat. Non-fatal: a missing nameplate
    /// costs a label, a thrown exception would cost the match.
    /// </summary>
    private static void ExpandNameplates(Manager m, int want)
    {
        try
        {
            var texts = m.NameTexts;
            var slots = m.Slots;
            if (texts == null || texts.Count == 0 || texts.Count >= want) return;
            if (slots == null || slots.Count < want) return;

            int have = texts.Count;
            Plugin.Log.LogInfo($"[seats] expanding nameplates {have} -> {want}");

            var lastText = texts[have - 1];
            var lastSeat = slots[have - 1];
            Vector3 offset = lastText.transform.position - lastSeat.position;

            for (int k = have; k < want; k++)
            {
                var seat = slots[k];
                var clone = Cloning.SafeClone(lastText.transform, $"NameText{k + 1}_8P");
                if (clone == null) break;

                clone.position = seat.position + offset;
                clone.rotation = lastText.transform.rotation;

                var comp = clone.GetComponent<TMPro.Examples.WarpTextExample>();
                if (comp == null) { Object.Destroy(clone.gameObject); break; }
                texts.Add(comp);
                Plugin.Log.LogInfo($"[seats]   + nameplate for seat {k + 1}");
            }

            Plugin.Log.LogInfo($"[seats] nameplates now {texts.Count}");
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogWarning($"[seats] nameplate expansion skipped: {e.Message}");
        }
    }
}
