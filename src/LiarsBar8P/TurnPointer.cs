using System;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Gives every seat the marking on the table that the shipped four seats have.
///
/// The table carries a group called <c>TurnArrows</c> holding four discs, each turned to a
/// different quarter of the table — 180, 90, 0 and 270 degrees, which are exactly the
/// bearings of the four seats the game shipped with. Each draws a pale chevron on the
/// tabletop in front of its seat.
///
/// The name is misleading, and it cost a night to establish that. They are *not* a turn
/// indicator. In a four player round all four are switched on at once, on every turn, and
/// nothing drawn on the tabletop changes angle when the turn moves — verified by watching
/// every drawn thing on the table across turns, at four players and at eight. Nothing in
/// the game's code aims anything at a player either. They are seat markings, and the game
/// simply leaves them on.
///
/// Which explains the report exactly. With eight players the seats sit forty-five degrees
/// apart on the same circle, so the four shipped markings land on seats 0, 2, 4 and 6 and
/// the other four seats have none. A player reads the marking nearest whoever is playing
/// and finds it belongs to their neighbour: "the arrow is pointing at the person to the
/// right".
///
/// So the fix is not to aim anything. It is to give the other four seats the marking they
/// are missing, by copying one of the shipped discs and turning it to the seat's own
/// bearing — the same relationship the shipped four already have with their seats. Copying
/// carries the artwork's own orientation with it, whatever that is, so no assumption is
/// made about which way the chevron is painted. Each seat ends up marked exactly as the
/// game marks a seat.
///
/// At four players there is nothing to add and this does nothing at all.
/// </summary>
internal static class TurnPointer
{
    /// <summary>Copies this mod makes, named so they can be recognised again.</summary>
    private const string Prefix = "SeatMark8P_";

    private static Manager _match;
    private static Transform _group;
    private static bool _done;

    internal static void Tick()
    {
        try
        {
            var m = Manager.Instance;
            if (m == null) { _match = null; return; }

            if (!ReferenceEquals(m, _match))
            {
                _match = m;
                _group = null;
                _done = false;
            }

            if (_done) return;
            if (m.Slots == null || m.Slots.Count <= Limits.VanillaPlayers) { _done = true; return; }

            // The seat ring is laid out at the start of a round; before that the seats are
            // wherever the scene left them and a marking placed now would be in the wrong
            // place. Waiting for the ring costs nothing.
            if (!Centre(m, out Vector3 centre)) return;

            if (!Find()) { _done = true; return; }

            MarkEverySeat(m, centre);
            _done = true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[seatmark] could not mark the seats: {e.Message}");
            _done = true;
        }
    }

    /// <summary>
    /// Make sure there is one marking per seat, each turned to its seat's bearing.
    ///
    /// The shipped discs are left exactly as they are: their bearings already match the
    /// seats they belong to. Only the seats without one get a copy.
    /// </summary>
    private static void MarkEverySeat(Manager m, Vector3 centre)
    {
        var template = FirstDrawn();
        if (template == null)
        {
            Plugin.Log.LogInfo("[seatmark] the table has no seat marking to copy - left as it is");
            return;
        }

        int added = 0, already = 0;

        for (int slot = 0; slot < m.Slots.Count; slot++)
        {
            var seat = m.Slots[slot];
            if (seat == null) continue;

            var d = seat.position - centre;
            if (new Vector2(d.x, d.z).sqrMagnitude < 1e-4f) continue;
            float bearing = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;

            if (HasMarkAt(bearing)) { already++; continue; }

            string name = Prefix + slot;
            Transform mark = null;
            for (int i = 0; i < _group.childCount; i++)
            {
                var c = _group.GetChild(i);
                if (c != null && c.gameObject.name == name) { mark = c; break; }
            }

            if (mark == null)
            {
                var copy = UnityEngine.Object.Instantiate(template.gameObject, _group);
                if (copy == null) continue;
                copy.name = name;
                mark = copy.transform;
            }

            mark.position = template.position;
            mark.localScale = template.localScale;
            mark.rotation = Quaternion.Euler(0f, bearing, 0f);
            mark.gameObject.SetActive(true);
            added++;
        }

        Plugin.Log.LogInfo(
            $"[seatmark] {already} of the table's seat markings were already in place and {added} " +
            $"were added, so all {m.Slots.Count} seats are marked as the game marks its own four");
    }

    /// <summary>Is one of the existing markings already turned to this bearing?</summary>
    private static bool HasMarkAt(float bearing)
    {
        for (int i = 0; i < _group.childCount; i++)
        {
            var c = _group.GetChild(i);
            if (c == null || c.gameObject.name.StartsWith(Prefix)) continue;
            if (Mathf.Abs(Mathf.DeltaAngle(c.rotation.eulerAngles.y, bearing)) < 5f)
            {
                if (!c.gameObject.activeSelf) c.gameObject.SetActive(true);
                return true;
            }
        }
        return false;
    }

    /// <summary>A shipped marking that actually draws something, to copy from.</summary>
    private static Transform FirstDrawn()
    {
        for (int i = 0; i < _group.childCount; i++)
        {
            var c = _group.GetChild(i);
            if (c == null || c.gameObject.name.StartsWith(Prefix)) continue;
            var r = c.GetComponentInChildren<Renderer>(true);
            if (r != null) return c;
        }
        return null;
    }

    private static bool Centre(Manager m, out Vector3 centre)
    {
        centre = Vector3.zero;
        int n = 0;
        foreach (var s in m.Slots) { if (s == null) continue; centre += s.position; n++; }
        if (n == 0) return false;
        centre /= n;

        // Before the ring is laid out the seats are not evenly spaced; every seat being the
        // same distance from the middle is what says the layout has happened.
        float first = -1f;
        foreach (var s in m.Slots)
        {
            if (s == null) continue;
            var d = s.position - centre;
            float r = new Vector2(d.x, d.z).magnitude;
            if (first < 0f) first = r;
            else if (Mathf.Abs(r - first) > 0.15f) return false;
        }
        return first > 0.1f;
    }

    private static bool Find()
    {
        if (_group != null) return true;
        try
        {
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(true))
            {
                if (t == null || t.gameObject == null) continue;
                if (t.gameObject.name != "TurnArrows" || t.childCount == 0) continue;
                _group = t;
                if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                return true;
            }
            Plugin.Log.LogInfo("[seatmark] this table has no seat markings to extend");
            return false;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[seatmark] could not find the seat markings: {e.Message}");
            return false;
        }
    }
}
