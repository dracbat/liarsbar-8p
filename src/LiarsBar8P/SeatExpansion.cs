using System;
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
            GiveSlotComponent(seat, template, slots.Count);
            slots.Add(seat);

            Plugin.Log.LogInfo(
                $"[seats]   + {seat.gameObject.name} pos={pos.ToString("F3")} yaw={yaw:F1} (ring {newAngle:F1}deg)");
        }

        Relayout(slots, c, r, y);
        VerifySeatNumbers(slots);
        Plugin.Log.LogInfo($"[seats] table seats now {slots.Count}");
    }

    /// <summary>
    /// Every seat must carry its own list position as its number, and no two may share one.
    ///
    /// The game numbers a player by the seat they are put in, and turn order walks those
    /// numbers. A seat numbered wrongly puts two players on one number — the turn then
    /// finds whichever comes first and the other never plays — and a gap in the numbers
    /// makes the turn search step over a seat that is occupied. Both have happened.
    ///
    /// Anything wrong here is reported rather than quietly corrected in silence, because a
    /// seat numbering that needs correcting means something upstream is wrong too.
    /// </summary>
    private static void VerifySeatNumbers(Il2CppSystem.Collections.Generic.List<Transform> slots)
    {
        var seen = new System.Collections.Generic.Dictionary<int, int>();
        int problems = 0;

        for (int i = 0; i < slots.Count; i++)
        {
            var t = slots[i];
            if (t == null) { Plugin.Log.LogError($"[seats] seat {i} is missing entirely"); problems++; continue; }

            var slot = t.GetComponent<Slot>();
            if (slot == null)
            {
                Plugin.Log.LogError(
                    $"[seats] seat {i} ('{t.gameObject.name}') has no Slot component - the match " +
                    "will throw when it tries to seat anybody here");
                problems++;
                continue;
            }

            if (slot.SlotID != i)
            {
                Plugin.Log.LogWarning(
                    $"[seats] seat {i} ('{t.gameObject.name}') was numbered {slot.SlotID} - corrected to {i}");
                slot.SlotID = i;
                problems++;
            }

            if (seen.TryGetValue(slot.SlotID, out int other))
            {
                Plugin.Log.LogError(
                    $"[seats] seats {other} and {i} both claim number {slot.SlotID} - two players " +
                    "would share a seat and one of them would never get a turn");
                problems++;
            }
            seen[slot.SlotID] = i;
        }

        if (problems == 0)
            Plugin.Log.LogInfo($"[seats] all {slots.Count} seats numbered 0..{slots.Count - 1}, none shared");
    }

    /// <summary>
    /// Give a new seat the <c>Slot</c> component the game reads its number from.
    ///
    /// This is what stopped the fifth player reaching the table. When the match starts,
    /// the game does not number players by the order it seats them — it reads the number
    /// off a component on the seat itself:
    ///
    ///     Slots[n].GetComponent&lt;Slot&gt;().SlotID  ->  the player's seat number
    ///
    /// A seat created as a bare marker has no such component, so that returns null and the
    /// whole seating sweep throws a NullReferenceException on the first added seat —
    /// leaving four players seated and everyone after them nowhere. It seated exactly four
    /// because four is exactly how many seats the game shipped with.
    ///
    /// The per-seat camera is copied from the seat this one was modelled on, positioned
    /// the same way relative to its own seat, so a person sitting here sees the table from
    /// their own chair rather than from somebody else's.
    /// </summary>
    private static void GiveSlotComponent(Transform seat, Transform template, int index)
    {
        try
        {
            var slot = seat.gameObject.AddComponent<Slot>();
            slot.SlotID = index;

            var from = template != null ? template.GetComponent<Slot>() : null;
            if (from != null && from.Cameraa != null)
            {
                var cam = UnityEngine.Object.Instantiate(from.Cameraa);
                cam.name = $"{seat.gameObject.name}_Camera";

                // Keep the camera where it sits relative to its own seat, not where the
                // template's camera happens to be in the world.
                var local = template.InverseTransformPoint(from.Cameraa.transform.position);
                cam.transform.SetParent(seat, false);
                cam.transform.position = seat.TransformPoint(local);
                cam.transform.rotation = seat.rotation *
                                         (Quaternion.Inverse(template.rotation) * from.Cameraa.transform.rotation);
                slot.Cameraa = cam;
            }
            else
            {
                Plugin.Log.LogWarning(
                    $"[seats] {seat.gameObject.name} has no seat camera to copy - a person sitting " +
                    "here may see the table from the wrong place");
            }

            Plugin.Log.LogInfo($"[seats]     {seat.gameObject.name} is seat number {index}" +
                               (slot.Cameraa != null ? " with its own camera" : ""));
        }
        catch (Exception e)
        {
            Plugin.Log.LogError(
                $"[seats] could not make {seat.gameObject.name} a real seat: {e.Message} - " +
                "the match will not be able to seat anyone here");
        }
    }

    /// <summary>
    /// Lay every seat out evenly around the ring in list order.
    ///
    /// New seats are appended to the end of the list but sit physically between the
    /// original four, so turn order - which follows the list index - jumped across the
    /// table instead of going around it: the indicator pointed at one seat while a
    /// player elsewhere acted. Ordering positions by index makes the two agree.
    ///
    /// The first seat keeps its original angle, so the table is rotated as little as
    /// possible from vanilla.
    /// </summary>
    private static void Relayout(Il2CppSystem.Collections.Generic.List<UnityEngine.Transform> slots,
                                 Vector2 c, float r, float y)
    {
        try
        {
            int n = slots.Count;
            if (n < 2) return;

            var first = slots[0];
            float startAngle = Mathf.Atan2(first.position.z - c.y, first.position.x - c.x) * Mathf.Rad2Deg;
            float step = 360f / n;

            Plugin.Log.LogInfo($"[seats] laying {n} seats out evenly, {step:F1}deg apart, from {startAngle:F1}deg");

            for (int i = 0; i < n; i++)
            {
                var t = slots[i];
                if (t == null) continue;
                float a = startAngle + step * i;
                float rad = a * Mathf.Deg2Rad;
                t.position = new Vector3(c.x + r * Mathf.Cos(rad), y, c.y + r * Mathf.Sin(rad));
                t.rotation = Quaternion.Euler(0f, Mathf.Atan2(-Mathf.Cos(rad), -Mathf.Sin(rad)) * Mathf.Rad2Deg, 0f);
                Plugin.Log.LogInfo($"[seats]   seat {i} -> {a:F1}deg {t.position.ToString("F2")}");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[seats] relayout failed: {e.Message}");
        }
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
                if (comp == null) { UnityEngine.Object.Destroy(clone.gameObject); break; }
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
