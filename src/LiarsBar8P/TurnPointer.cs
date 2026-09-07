using System;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Takes the markings off the table.
///
/// The table carries a group the game calls <c>TurnArrows</c>: four discs, each drawing a
/// pale chevron on the tabletop in front of one of the four seats the game shipped with.
/// The name is misleading and it cost a night to establish that they are not a turn
/// indicator at all. In a four player round all four are switched on at once, on every turn;
/// nothing drawn on the tabletop changes angle when the turn moves, at four players or at
/// eight; and nothing in the game's code aims anything at a player. They are static seat
/// markings, and the game simply leaves them on.
///
/// Which is why they mislead at eight. The seats sit forty-five degrees apart on the same
/// circle, so four markings land on every *other* seat: the chevron nearest whoever is
/// playing usually belongs to their neighbour. Reading it as "whose turn is it" gives the
/// wrong answer half the time, and that is precisely what was reported.
///
/// Giving every seat its own marking was tried and it works, but it does not help: eight
/// identical static chevrons say no more about the turn than four did. So they are removed,
/// and whose turn it is is stated in words instead - see <see cref="TurnHud"/>.
/// </summary>
internal static class TurnPointer
{
    /// <summary>Copies an earlier build of this mod may have left on the table.</summary>
    private const string Prefix = "SeatMark8P_";

    private static Manager _match;
    private static Transform[] _groups;
    private static bool _said;

    /// <summary>
    /// Consecutive failures in the current match, rather than a permanent latch.
    ///
    /// This was a bool, set on any exception and never cleared. One transient failure - a
    /// stale reference caught during a scene change, say - switched the whole thing off for
    /// the rest of the process, so every later match in that session showed the four shipped
    /// chevrons on an eight seat table again. Those chevrons sit in front of every other
    /// seat at eight players, so the one nearest whoever is playing belongs to their
    /// neighbour: exactly the misreading this class exists to stop, silently back, with one
    /// line in the log from an hour earlier as the only clue.
    /// </summary>
    private const int GiveUpAfter = 5;
    private static int _failures;

    internal static void Tick()
    {
        try
        {
            var m = Manager.Instance;
            if (m == null) { _match = null; _groups = null; return; }

            // A new match gets a clean slate, failures included.
            if (!ReferenceEquals(m, _match))
            {
                _match = m;
                _groups = null;
                _said = false;
                _failures = 0;
            }

            if (_failures >= GiveUpAfter) return;

            // Held off rather than switched off once. The first attempt cleared them at the
            // start of the match and they came back part way through - the game switches its
            // own markings on again as a round begins, so this has to keep up with it.
            if (_groups == null) _groups = FindGroups();
            Clear();
            _failures = 0;
        }
        catch (Exception e)
        {
            _failures++;
            _groups = null;      // most likely a stale reference; look them up again
            Plugin.Log.LogError(
                $"[seatmark] could not clear the table markings ({_failures}/{GiveUpAfter}): {e.Message}");
            if (_failures >= GiveUpAfter)
                Plugin.Log.LogWarning(
                    "[seatmark] giving up for this match - the table's own seat markings will be " +
                    "visible, and at more than four players they sit in front of every other seat, " +
                    "so the marking nearest the player acting may belong to their neighbour");
        }
    }

    /// <summary>
    /// The marking groups, found once per match.
    ///
    /// Searching the whole scene every quarter second to switch off four objects would be
    /// silly; the transforms do not move, so they are looked up once and kept.
    /// </summary>
    private static Transform[] FindGroups()
    {
        var found = new System.Collections.Generic.List<Transform>();
        foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(true))
        {
            if (t == null || t.gameObject == null) continue;
            string n = t.gameObject.name;

            // The group itself, and every disc of the same artwork anywhere on the table.
            // The one proven to draw the chevron is called "CirclePoker (2)" and lives in
            // the group; the others are its siblings, and one of those kept drawing after
            // the group was switched off.
            if (n == "TurnArrows" || n == "Circle" || n.StartsWith("CirclePoker")) found.Add(t);
        }
        return found.ToArray();
    }

    /// <summary>
    /// Switch off every marking, including any this mod added in an earlier version.
    ///
    /// Switched off rather than destroyed: these are the game's own scene objects, and a
    /// mod that deletes scene objects has nothing to give back if it turns out to be wrong.
    /// </summary>
    private static void Clear()
    {
        if (_groups == null || _groups.Length == 0) return;

        int off = 0, removed = 0;

        foreach (var t in _groups)
        {
            if (t == null || t.gameObject == null) continue;

            // Children are only ever switched off inside the TurnArrows group, where every
            // child is a marking. The sibling discs are NOT walked: one of them is the
            // parent of "kartdon", card furniture the table needs, and switching off every
            // child of every disc took that with it - a quarter of a second into any round,
            // repeatedly, so the game could never put it back.
            bool markingsOnly = t.gameObject.name == "TurnArrows";

            if (markingsOnly)
                for (int i = t.childCount - 1; i >= 0; i--)
                {
                    var c = t.GetChild(i);
                    if (c == null) continue;

                    if (c.gameObject.name.StartsWith(Prefix))
                    {
                        // A copy this mod made. That one really can go.
                        UnityEngine.Object.Destroy(c.gameObject);
                        removed++;
                        continue;
                    }

                    if (c.gameObject.activeSelf) { c.gameObject.SetActive(false); off++; }
                }

            // The disc's own renderer, rather than the object. One of these discs is the
            // parent of card furniture ("kartdon"), and switching the object off would take
            // that with it; switching off only what it draws leaves everything underneath
            // working and simply stops the marking appearing.
            var own = t.GetComponent<Renderer>();
            if (own != null && own.enabled) { own.enabled = false; off++; }

            // A group with nothing but markings under it can go entirely.
            if (markingsOnly && t.gameObject.activeSelf)
            {
                t.gameObject.SetActive(false);
                off++;
            }
        }

        if ((off > 0 || removed > 0) && !_said)
        {
            _said = true;
            Plugin.Log.LogInfo(
                $"[seatmark] the table's seat markings are switched off ({off} objects" +
                (removed > 0 ? $", {removed} added by an earlier build removed" : "") +
                ") and held off - whose turn it is is shown in the corner instead");
        }
    }

    /// <summary>
    /// Name anything still drawn on the tabletop, so a marking that survives can be found.
    ///
    /// Switching off the group the chevron demonstrably belongs to should be the end of it,
    /// but it has come back once already. If any of it is still showing, this says what and
    /// where rather than leaving it to another round of screenshots.
    /// </summary>
    internal static void ReportLeftovers(Manager m)
    {
        if (!Dev.Enabled) return;

        try
        {
            if (m == null || m.Slots == null || m.Slots.Count == 0) return;

            Vector3 centre = Vector3.zero;
            int n = 0;
            foreach (var s in m.Slots) { if (s == null) continue; centre += s.position; n++; }
            if (n == 0) return;
            centre /= n;

            var sb = new System.Text.StringBuilder("still drawn on the tabletop:\n");
            int shown = 0;

            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled || r.gameObject == null) continue;
                if (!r.gameObject.activeInHierarchy) continue;
                if (r.GetComponentInParent<PlayerStats>() != null) continue;

                // Whether the drawn thing *overlaps* the tabletop, rather than whether its
                // middle happens to sit there. A decal or a wide flat mesh has its middle at
                // the table centre or off it entirely, and the first version of this missed
                // exactly that.
                var b = r.bounds;
                if (b.min.y > centre.y + 1.2f || b.max.y < centre.y + 0.5f) continue;
                var d = b.center - centre;
                float flat = new Vector2(d.x, d.z).magnitude - new Vector2(b.extents.x, b.extents.z).magnitude;
                if (flat > 1.6f) continue;

                var path = r.transform;
                var parts = new System.Collections.Generic.List<string>();
                int guard = 0;
                while (path != null && guard++ < 5) { parts.Insert(0, path.name); path = path.parent; }

                if (++shown > 30) break;
                sb.AppendLine($"  '{string.Join("/", parts)}' reaches {flat:F2} from the rim, " +
                              $"height {d.y:F2}, size {b.size.ToString("F2")}");
            }

            if (shown == 0) sb.AppendLine("  no drawn object overlaps the tabletop");

            // Decals are not renderers, so nothing above would ever find one.
            int decals = 0;
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (t == null || t.gameObject == null || !t.gameObject.activeInHierarchy) continue;
                var dd = t.position - centre;
                if (new Vector2(dd.x, dd.z).magnitude > 2f) continue;

                foreach (var comp in t.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    string cn = comp.GetIl2CppType().Name;
                    if (cn.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) < 0 &&
                        cn.IndexOf("Projector", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (++decals > 12) break;
                    sb.AppendLine($"  DECAL '{t.name}' has {cn} at {t.position.ToString("F2")}");
                }
                if (decals > 12) break;
            }
            if (decals == 0) sb.AppendLine("  no decal projectors near the table either");
            Dev.Log("seatmark", sb.ToString().TrimEnd());
        }
        catch { }
    }
}
