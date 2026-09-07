using System;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Points the arrow on the table at the player whose turn it actually is.
///
/// The table carries a group called <c>TurnArrows</c> holding four arrows, all standing at
/// the middle of the table and each turned to face one seat: they sit at 180, 90, 0 and 270
/// degrees, ninety apart, because the game was built for four players. The one belonging to
/// the player to play is switched on and the others off.
///
/// Eight players sit forty-five degrees apart, so those same four arrows land on seats 0, 2,
/// 4 and 6 — every other seat. Showing arrow number <c>n</c> for the player on seat
/// <c>n</c> therefore points at the seat one place beyond them, which is what was reported:
/// the arrow indicating one player while the turn belonged to their neighbour.
///
/// Rather than add four more arrows and hope the game picks the right one from a list whose
/// shape is not known, one arrow is aimed directly. Where a seat lies is already known — the
/// seats are placed in a ring by this mod — so the arrow is simply turned to that bearing,
/// which is exactly what the shipped arrows do for the four seats they were made for. The
/// others are kept switched off so only one arrow is ever showing.
///
/// Only tables larger than the game's own four are touched; at four the shipped arrows are
/// already right and are left alone.
/// </summary>
internal static class TurnPointer
{
    private static Manager _match;
    private static Transform _group;
    private static Transform _arrow;
    private static int _lastAimed = int.MinValue;
    private static bool _searched;

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
                _arrow = null;
                _searched = false;
                _lastAimed = int.MinValue;
                _offsetKnown = false;
            }

            // At four players the game's own indicator lines up with the seats.
            if (m.Slots == null || m.Slots.Count <= Limits.VanillaPlayers) return;

            int slot = m.ActivePlayerSlot;
            bool playing = HasTurn(m, slot);

            // Watch first. The thing people can see has to be identified before anything is
            // moved: the group called TurnArrows turned out to hold four circles that are
            // never drawn at all, and driving one of those showed nothing while switching on
            // an object the game deliberately keeps off.
            if (playing && slot != _lastSeen)
            {
                _lastSeen = slot;
                Study(m, slot);
            }

            if (!Find()) return;

            if (!playing)
            {
                // Between turns - a round ending, a revolver being fired - leave the table
                // as the game has it rather than holding an arrow on nobody.
                if (_lastAimed != int.MinValue)
                {
                    _lastAimed = int.MinValue;
                    _arrow.gameObject.SetActive(false);
                }
                return;
            }

            if (!Aim(m, slot)) return;

            if (slot != _lastAimed)
            {
                _lastAimed = slot;
                Dev.Log("arrow", $"pointing the table arrow at seat {slot}");
                if (Centre(m, out Vector3 mid)) ReportArrows(m, mid);
                DevShots.Take($"arrow_seat{slot}");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[arrow] could not point the table arrow: {e.Message}");
            _searched = true;
            _group = null;
        }
    }

    /// <summary>Turn the arrow to the seat, and make sure it is the only one showing.</summary>
    private static bool Aim(Manager m, int slot)
    {
        if (slot < 0 || m.Slots == null || slot >= m.Slots.Count) return false;
        var seat = m.Slots[slot];
        if (seat == null) return false;

        if (!Centre(m, out Vector3 centre)) return false;

        var d = seat.position - centre;
        if (new Vector2(d.x, d.z).sqrMagnitude < 1e-4f) return false;

        float bearing = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;

        // Switched on before it is measured: a renderer that has never been drawn has no
        // meaningful bounds, and the offset is taken from the bounds.
        if (_group != null && !_group.gameObject.activeSelf) _group.gameObject.SetActive(true);
        if (!_arrow.gameObject.activeSelf) _arrow.gameObject.SetActive(true);

        // The arrow's pivot is at the middle of the table but the chevron is drawn out near
        // the rim, so where it *appears* is not where its forward axis points. Assuming the
        // two were the same put it a long way from the player whose turn it was. The offset
        // between them is measured once, from the drawn mesh itself, and taken off here.
        _arrow.rotation = Quaternion.Euler(0f, bearing - MeshOffset(centre), 0f);

        // The game switches its own arrows on as the turn moves; with one arrow aimed
        // properly, any other showing at the same time is a second arrow on the table.
        if (_group != null)
            for (int i = 0; i < _group.childCount; i++)
            {
                var c = _group.GetChild(i);
                if (c == null || ReferenceEquals(c, _arrow)) continue;
                if (c.gameObject.activeSelf) c.gameObject.SetActive(false);
            }

        return true;
    }

    /// <summary>
    /// How far the drawn chevron sits round the table from the way its pivot faces.
    ///
    /// Measured from the mesh rather than assumed: the middle of what is actually drawn is
    /// taken, its bearing from the middle of the table worked out, and the pivot's own
    /// heading subtracted. Whatever the model's orientation happens to be, aiming then puts
    /// the chevron itself at the seat rather than the pivot's forward axis.
    ///
    /// Measured once and kept, because it is a fact about the model, not about the turn.
    /// </summary>
    private static float MeshOffset(Vector3 centre)
    {
        if (_offsetKnown) return _meshOffset;

        try
        {
            var r = _arrow.GetComponentInChildren<Renderer>();
            if (r == null)
            {
                _offsetKnown = true;
                _meshOffset = 0f;
                Plugin.Log.LogWarning("[arrow] the turn arrow has nothing drawn on it - aiming its pivot instead");
                return 0f;
            }

            var d = r.bounds.center - centre;
            if (new Vector2(d.x, d.z).sqrMagnitude < 0.01f)
            {
                // Drawn at the middle of the table: the pivot's heading is all there is.
                _offsetKnown = true;
                _meshOffset = 0f;
                return 0f;
            }

            float drawnAt = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            float pivotAt = _arrow.rotation.eulerAngles.y;

            _meshOffset = Mathf.DeltaAngle(pivotAt, drawnAt);
            _offsetKnown = true;

            Plugin.Log.LogInfo(
                $"[arrow] the chevron is drawn {_meshOffset:F1} degrees round from the way its pivot " +
                $"faces (pivot {pivotAt:F1}, chevron {drawnAt:F1}) - aiming allows for that");
            return _meshOffset;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[arrow] could not measure the arrow: {e.Message}");
            _offsetKnown = true;
            _meshOffset = 0f;
            return 0f;
        }
    }

    private static float _meshOffset;
    private static bool _offsetKnown;

    /// <summary>
    /// Where each of the table's arrows is actually drawn, once per match.
    ///
    /// If the wrong one of the four is being driven, this is what says so: the one that is
    /// switched on and drawn out at the rim is the one people can see.
    /// </summary>
    private static void ReportArrows(Manager m, Vector3 centre)
    {
        if (!Dev.Enabled || _group == null) return;
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"the table's {_group.childCount} arrows, table centre {centre.ToString("F2")}:");

            for (int i = 0; i < _group.childCount; i++)
            {
                var c = _group.GetChild(i);
                if (c == null) continue;
                var r = c.GetComponentInChildren<Renderer>();
                string drawn = "nothing drawn";
                if (r != null)
                {
                    var d = r.bounds.center - centre;
                    float bearing = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                    if (bearing < 0f) bearing += 360f;
                    drawn = $"drawn at bearing {bearing:F1}, {new Vector2(d.x, d.z).magnitude:F2} from the middle, " +
                            $"visible={r.isVisible} enabled={r.enabled}";
                }
                sb.AppendLine($"  [{i}] '{c.name}' on={c.gameObject.activeSelf} " +
                              $"yaw={c.rotation.eulerAngles.y:F1} {drawn}" +
                              (ReferenceEquals(c, _arrow) ? "   <- the one being driven" : ""));
            }

            Dev.Log("arrow", sb.ToString().TrimEnd());
        }
        catch (Exception e) { Dev.Warn("arrow", $"could not survey the arrows: {e.Message}"); }
    }

    private static bool HasTurn(Manager m, int slot)
    {
        try
        {
            if (slot < 0 || m.Players == null) return false;
            foreach (var p in m.Players)
                if (p != null && p.HaveTurn && !p.Dead) return true;
        }
        catch { }
        return false;
    }

    private static bool Centre(Manager m, out Vector3 centre)
    {
        centre = Vector3.zero;
        int n = 0;
        foreach (var s in m.Slots) { if (s == null) continue; centre += s.position; n++; }
        if (n == 0) return false;
        centre /= n;
        return true;
    }

    private static int _lastSeen = int.MinValue;
    private static readonly System.Collections.Generic.Dictionary<string, float> _wasAt = new();

    /// <summary>
    /// Work out what the indicator on the table actually is, by watching what moves.
    ///
    /// Guessing has been wrong twice. Nothing in the game's code aims anything at a player,
    /// the group named TurnArrows holds four circles that are never drawn, and yet a pale
    /// chevron plainly slides round the table as the turn passes. So instead of guessing:
    /// every drawn thing lying on the tabletop is measured — where it is round the table —
    /// and compared with where it was on the previous turn. Whatever swung round by roughly
    /// the angle between the two seats is the indicator, and it is named here.
    /// </summary>
    private static void Study(Manager m, int slot)
    {
        if (!Dev.Enabled) return;

        try
        {
            if (!Centre(m, out Vector3 centre)) return;

            float want = SeatBearing(m, slot);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"seat {slot} is to play, which lies at bearing {want:F1}. " +
                          "What is drawn on the table, and where it was last turn:");

            int shown = 0;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled || r.gameObject == null) continue;
                if (!r.gameObject.activeInHierarchy) continue;

                var d = r.bounds.center - centre;
                float flat = new Vector2(d.x, d.z).magnitude;

                // On the table: within its rim, at about its surface, and off-centre enough
                // for a bearing to mean something.
                if (flat < 0.25f || flat > 1.5f) continue;
                if (d.y < 0.2f || d.y > 1.2f) continue;

                // Anything held by a player moves because they move, not because of the turn.
                if (r.GetComponentInParent<PlayerStats>() != null) continue;

                float at = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                if (at < 0f) at += 360f;

                string key = Path(r.transform);
                float moved = _wasAt.TryGetValue(key, out float before) ? Mathf.DeltaAngle(before, at) : float.NaN;
                _wasAt[key] = at;

                if (float.IsNaN(moved) || Mathf.Abs(moved) < 5f) continue;   // it did not swing

                if (++shown > 20) break;
                sb.AppendLine($"  '{key}' now at bearing {at:F1} (moved {moved:F1}), " +
                              $"{flat:F2} from the middle, {Mathf.Abs(Mathf.DeltaAngle(at, want)):F1} off the seat in play");
            }

            if (shown == 0) sb.AppendLine("  nothing drawn on the table swung round this turn");
            Dev.Log("arrow", sb.ToString().TrimEnd());
        }
        catch (Exception e) { Dev.Warn("arrow", $"could not study the table: {e.Message}"); }
    }

    private static float SeatBearing(Manager m, int slot)
    {
        try
        {
            if (!Centre(m, out Vector3 c) || m.Slots == null || slot < 0 || slot >= m.Slots.Count) return 0f;
            var d = m.Slots[slot].position - c;
            float b = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            return b < 0f ? b + 360f : b;
        }
        catch { return 0f; }
    }

    private static string Path(Transform t)
    {
        var parts = new System.Collections.Generic.List<string>();
        var cur = t;
        int guard = 0;
        while (cur != null && guard++ < 6) { parts.Insert(0, cur.name); cur = cur.parent; }
        return string.Join("/", parts);
    }

    /// <summary>Find the arrow group once per match, and pick the arrow to drive.</summary>
    private static bool Find()
    {
        if (_arrow != null) return true;
        if (_searched) return false;
        _searched = true;

        try
        {
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(true))
            {
                if (t == null || t.gameObject == null) continue;
                if (t.gameObject.name != "TurnArrows") continue;
                if (t.childCount == 0) continue;
                _group = t;
                break;
            }

            if (_group == null)
            {
                Plugin.Log.LogWarning("[arrow] the table has no TurnArrows group - " +
                                      "the turn arrow is left as the game draws it");
                return false;
            }

            // Only drive something that is actually drawn, and drawn away from the middle of
            // the table. The four children of this group turned out to be circles centred on
            // the pivot with their renderers never drawn: turning one of those moves nothing
            // a player can see, while switching it on overrides a decision the game made.
            for (int i = 0; i < _group.childCount && _arrow == null; i++)
            {
                var c = _group.GetChild(i);
                if (c == null) continue;
                var r = c.GetComponentInChildren<Renderer>(true);
                if (r == null || !r.enabled) continue;

                var extent = r.bounds.extents;
                if (extent.x < 0.02f && extent.z < 0.02f) continue;   // nothing of any size
                _arrow = c;
            }

            if (_arrow == null)
            {
                _group = null;
                Plugin.Log.LogInfo("[arrow] the table's TurnArrows group holds nothing that is drawn - " +
                                   "the turn indicator is left to the game");
                return false;
            }

            Plugin.Log.LogInfo($"[arrow] driving '{_arrow.gameObject.name}' of the table's " +
                               $"{_group.childCount} turn arrows, aimed at the seat in play");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[arrow] could not find the table's turn arrows: {e.Message}");
            return false;
        }
    }
}
