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

    /// <summary>The fitted ring, kept so the check below can report a bearing.</summary>
    private static Vector2 _centre;
    private static float _radius;

    /// <summary>Where the seats stood before this mod moved any of them.</summary>
    private static Vector3[] _vanilla;

    /// <summary>When to measure where everyone actually ended up. Zero means never.</summary>
    private static float _measureAt;

    /// <summary>
    /// The players at this table, as this machine can see them.
    ///
    /// <c>Manager.Players</c> is the server's roster and is empty on a client, so anything
    /// that walked it did nothing there — including moving bodies onto the re-spaced seats,
    /// which is the whole point. A client has the player objects all the same; it just has
    /// to find them in the scene rather than being handed a list.
    /// </summary>
    private static System.Collections.Generic.List<PlayerStats> PlayersHere(Manager m)
    {
        var found = new System.Collections.Generic.List<PlayerStats>();
        try
        {
            if (m != null && m.Players != null && m.Players.Count > 0)
            {
                for (int i = 0; i < m.Players.Count; i++)
                    if (m.Players[i] != null) found.Add(m.Players[i]);
                return found;
            }

            var all = UnityEngine.Object.FindObjectsOfType<PlayerStats>();
            if (all != null)
                for (int i = 0; i < all.Count; i++)
                    if (all[i] != null) found.Add(all[i]);
        }
        catch { }
        return found;
    }

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
    private static void Space(DeckGamePlayManager __instance, bool first) => Layout();

    /// <summary>
    /// Fit the ring for the number of players and put everyone on it.
    ///
    /// Runs on every machine, not just the host. <c>ResetRound</c> is server-side code and
    /// never executes on a client, so for a long time this ran only on the host: the host
    /// saw an evenly spaced table and everyone else was still looking at the four seats the
    /// game ships with, which is exactly what "they are all on one side" looks like from a
    /// player's seat.
    ///
    /// Every machine computing this for itself is safe because the answer does not depend on
    /// the machine. The ring is fitted from the seats in the level, which are identical in
    /// every copy of the game, and divided by a player count everyone agrees on - so each
    /// peer arrives at the same positions, and a client moving a body is moving it to where
    /// the host has already put it.
    /// </summary>
    private static void Layout()
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
            _centre = c;
            _radius = r;

            // The seats as the game shipped them, captured before anything is moved. The
            // furniture check needs to look where the chairs are, not where this mod has
            // just put the seats.
            if (_vanilla == null)
            {
                _vanilla = new Vector3[baseCount];
                for (int i = 0; i < baseCount; i++) _vanilla[i] = slots[i].position;
            }

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

            // Unused seats go below and outside so nothing is left standing mid-table - but
            // never a seat the fit above reads.
            //
            // The ring is fitted from the first four seats, and this loop starts at the
            // number of players. Below four players those overlap: it parked a seat the fit
            // reads, and next round the fit averaged in a position four metres down. The
            // whole ring sank a metre a round at three players, two at two, taking the
            // players with it, and never recovered. At four and above the two ranges do not
            // meet and this changes nothing.
            for (int i = Mathf.Max(n, baseCount); i < slots.Count; i++)
            {
                var t = slots[i];
                if (t == null) continue;
                float rad = (start + step * (i % Mathf.Max(1, n))) * Mathf.Deg2Rad;
                t.position = new Vector3(
                    c.x + r * 1.9f * Mathf.Cos(rad), y - 4f, c.y + r * 1.9f * Mathf.Sin(rad));
            }

            SnapPlayersToSeats(m);

            // Placing them is not the same as them staying put. Anything the game does to a
            // player after the round starts - a spawn point, an animation root, a network
            // correction - lands after this has run, so the only honest check is to look
            // again once the round is actually under way.
            _measureAt = Time.time + 6f;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[seatring] spacing failed: {e.Message}");
        }
    }

    /// <summary>
    /// Put each player where their seat now is.
    ///
    /// This used to be the host's job alone, on the reasoning that a client moving a
    /// networked player would fight Mirror's synchronisation. It does not, because the ring
    /// is worked out from numbers every copy of the game shares: a client is moving a body
    /// to the position the host has already given it, so there is nothing to disagree with.
    /// Keeping it host-only is what left everyone else looking at the shipped four seats.
    /// </summary>
    private static void SnapPlayersToSeats(Manager m)
    {
        try
        {
            if (m.Slots == null) return;
            var players = PlayersHere(m);

            int moved = 0;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
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

            // One short of the table is the expected count, not a miss: seat 0 anchors the
            // ring and never moves, so whoever sits there is already where they belong.
            if (moved > 0) Plugin.Log.LogInfo($"[seatring] moved {moved} player(s) onto their seats");
        }
        catch (Exception e) { Plugin.Log.LogError($"[seatring] snap failed: {e.Message}"); }
    }

    /// <summary>Keeps a client's table laid out, and runs the delayed check once.</summary>
    internal static void Tick()
    {
        KeepClientInStep();

        if (_measureAt <= 0f || Time.time < _measureAt) return;
        _measureAt = 0f;
        Measure();
    }

    private static float _clientNext;
    private static int _clientLastCount = -1;

    /// <summary>
    /// Lay the table out on a machine that is not the host.
    ///
    /// The host does this when the round resets, but that is server-side code and a client
    /// never runs it, so a client had nothing laying its table out at all. Once a second is
    /// plenty - and only when something has actually changed, so a settled table costs a
    /// comparison and nothing else.
    /// </summary>
    private static void KeepClientInStep()
    {
        try
        {
            var m = Manager.Instance;
            if (m == null || m.Slots == null || m.Slots.Count == 0) { _clientLastCount = -1; return; }

            bool server;
            try { server = m.isServer; } catch { return; }
            if (server) return;

            if (Time.time < _clientNext) return;
            _clientNext = Time.time + 1f;

            int players = PlayerCount();
            if (players < 2) return;

            // Only when the table changes shape. Re-placing bodies every second would fight
            // any animation that moves a player, and there is nothing to fix once it is right.
            if (players == _clientLastCount) return;
            _clientLastCount = players;

            Plugin.Log.LogInfo($"[seatring] laying this client's table out for {players} players");
            Layout();
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[seatring] client layout failed: {e.Message}"); }
    }

    /// <summary>
    /// Report where every player actually is once the round is running: bearing around the
    /// table, distance from the middle, and the gap to the next player round the ring.
    ///
    /// Even spacing is exactly the claim "the gaps are all the same", so the gaps are what
    /// this prints. Reading positions and judging by eye is how a table that looked wrong
    /// got called even and a table that was even got called wrong.
    /// </summary>
    private static void Measure()
    {
        try
        {
            var m = Manager.Instance;
            if (m == null) return;

            var players = PlayersHere(m);
            if (players.Count == 0) return;

            var bearings = new System.Collections.Generic.List<float>();
            var lines = new System.Collections.Generic.List<string>();

            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null) continue;
                var root = p.transform != null ? p.transform.root : null;
                if (root == null) continue;

                Vector3 w = root.position;
                float deg = Mathf.Repeat(
                    Mathf.Atan2(w.z - _centre.y, w.x - _centre.x) * Mathf.Rad2Deg, 360f);
                float dist = Vector2.Distance(new Vector2(w.x, w.z), _centre);

                bearings.Add(deg);
                lines.Add($"  seat {p.Slot} '{p.PlayerName}' at {deg,6:F1}deg  r={dist:F2}  " +
                          $"pos={w.ToString("F2")}");
            }

            if (bearings.Count == 0) return;

            bearings.Sort();
            float widest = 0f, narrowest = 360f;
            for (int i = 0; i < bearings.Count; i++)
            {
                float gap = Mathf.Repeat(
                    bearings[(i + 1) % bearings.Count] - bearings[i], 360f);
                if (bearings.Count == 1) gap = 360f;
                if (gap > widest) widest = gap;
                if (gap < narrowest) narrowest = gap;
            }

            float ideal = 360f / bearings.Count;
            bool even = Mathf.Abs(widest - ideal) < 8f && Mathf.Abs(narrowest - ideal) < 8f;

            Plugin.Log.LogInfo(
                $"[seatcheck] {bearings.Count} players, gaps {narrowest:F1}..{widest:F1}deg " +
                $"(even would be {ideal:F1}) -> {(even ? "EVEN" : "UNEVEN")}, r={_radius:F2}");
            foreach (var l in lines) Plugin.Log.LogInfo(l);

            ProbeFurniture();
        }
        catch (Exception e) { Plugin.Log.LogError($"[seatcheck] failed: {e.Message}"); }
    }

    /// <summary>
    /// List the scenery standing at the seats the game shipped with, once per session.
    ///
    /// Re-spacing moves a seat, and a seat is an invisible marker; the chair, the mat and
    /// whatever else is bolted to that spot in the level do not come with it. If that is
    /// what is happening, players end up sitting beside chairs rather than on them, and
    /// this is the list that says which objects need to travel too.
    /// </summary>
    private static bool _probed;

    private static void ProbeFurniture()
    {
        if (_probed || _vanilla == null) return;
        _probed = true;

        try
        {
            var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            if (renderers == null) return;

            var seen = new System.Collections.Generic.HashSet<string>();
            int shown = 0;

            for (int s = 0; s < _vanilla.Length; s++)
            {
                var seat = new Vector2(_vanilla[s].x, _vanilla[s].z);
                for (int i = 0; i < renderers.Count && shown < 60; i++)
                {
                    var rend = renderers[i];
                    if (rend == null) continue;
                    var t = rend.transform;
                    if (t == null) continue;

                    // Horizontal distance only: a chair and the player on it share a spot
                    // on the floor and differ in height.
                    float d = Vector2.Distance(new Vector2(t.position.x, t.position.z), seat);
                    if (d > 1.1f) continue;

                    var root = t.root;
                    string key = root != null ? root.name : t.name;
                    if (!seen.Add($"{s}:{key}:{t.name}")) continue;

                    Plugin.Log.LogInfo(
                        $"[furniture] near vanilla seat {s} ({d:F2}m): '{t.name}' " +
                        $"under '{key}' at {t.position.ToString("F2")}");
                    shown++;
                }
            }

            if (shown == 0) Plugin.Log.LogInfo("[furniture] nothing found standing at the seats");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[furniture] probe failed: {e.Message}"); }
    }
}
