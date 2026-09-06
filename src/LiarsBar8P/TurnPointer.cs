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
            }

            // At four players the game's own arrows line up with the seats.
            if (m.Slots == null || m.Slots.Count <= Limits.VanillaPlayers) return;

            if (!Find()) return;

            int slot = m.ActivePlayerSlot;
            bool playing = HasTurn(m, slot);

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

        // A shipped arrow for the seat at bearing 180 stands at yaw 180, so the arrow's own
        // heading is the bearing of the seat it points at, with nothing added.
        float bearing = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;

        _arrow.rotation = Quaternion.Euler(0f, bearing, 0f);
        if (!_arrow.gameObject.activeSelf) _arrow.gameObject.SetActive(true);
        if (_group != null && !_group.gameObject.activeSelf) _group.gameObject.SetActive(true);

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

            _arrow = _group.GetChild(0);
            if (_arrow == null) { _group = null; return false; }

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
