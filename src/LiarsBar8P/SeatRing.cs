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

    /// <summary>
    /// Where the first four seats stood the first time this ran - which is after the seat
    /// expansion has already re-laid them out, not where the game shipped them. Only the
    /// furniture list below reads it, and only to have somewhere to look.
    /// </summary>
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

    /// <summary>
    /// How many are at this table, as a number every machine agrees on.
    ///
    /// This is the one input to the layout that has to be identical everywhere, because each
    /// peer divides the ring by it and a disagreement puts the same player in two different
    /// chairs depending on whose screen you look at.
    ///
    /// So the synced count comes first and the server's own roster second, rather than the
    /// other way round. <c>Manager.Players</c> fills in over several frames on the host and is
    /// empty on a client for the whole match, so preferring it meant the host sized the ring
    /// from a half-filled roster while clients sized it from something else entirely.
    /// </summary>
    private static int PlayerCount()
    {
        try
        {
            var m = Manager.Instance;
            if (m == null) return 0;
            if (m.StartPlayerCount > 0) return m.StartPlayerCount;
            if (m.Players != null && m.Players.Count > 0) return m.Players.Count;
        }
        catch { }
        return 0;
    }

    /// <summary>
    /// The highest seat index anybody is actually sitting in.
    ///
    /// The parking loop below drops unused seats through the floor, and an unused seat is
    /// decided by a count. If that count is ever wrong - and on a client it is a synced value
    /// that can arrive late - parking would take a seat somebody is in, along with the
    /// nameplate hanging on it and, for the added seats, that player's own camera. This is
    /// the check that makes a wrong count cost a cosmetic gap instead of putting a player
    /// under the floor looking up at the room.
    /// </summary>
    private static int HighestOccupiedSlot(Manager m)
    {
        int highest = -1;
        try
        {
            var players = PlayersHere(m);
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null) continue;
                if (p.Slot > highest) highest = p.Slot;
            }
        }
        catch { }
        return highest;
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
    /// the machine: the ring is fitted from seats laid out identically in every copy of the
    /// game, and divided by <c>PlayerCount</c>, which reads the synced count precisely so
    /// that peers cannot disagree about it. A client is then moving a body to the position
    /// the host has already given it, so there is nothing to fight over.
    ///
    /// That last part is load-bearing. While the count came from the server-only roster, a
    /// client fell back to a number the host was writing to a SyncVar's backing field and so
    /// never sending - it laid out for four at a table of eight and parked the seats the
    /// extra players were sitting in four metres under the floor.
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

            // Fit the ring from the first four seats. They have already been moved - by the
            // seat expansion at match start, and by this method on previous rounds - so these
            // are not the positions the game shipped. It is stable anyway because the fit is a
            // fixed point: four points taken off a circle fit that same circle, so re-fitting
            // returns the same centre and radius every round rather than drifting.
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

            // Somewhere for the furniture list to look, captured the first time through. The
            // check needs to look where the chairs are, not where this mod has
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
            // ...and never a seat somebody is sitting in, whatever the count says. A count
            // that arrives late or wrong is a cosmetic problem right up until it parks an
            // occupied chair, at which point that player is under the floor.
            int firstFree = Mathf.Max(n, baseCount, HighestOccupiedSlot(m) + 1);
            for (int i = firstFree; i < slots.Count; i++)
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
    private static int _clientLastBodies = -1;
    private static int _clientMatch;

    /// <summary>
    /// Lay the table out on a machine that is not the host.
    ///
    /// The host does this when the round resets, but that is server-side code and a client
    /// never runs it, so a client had nothing laying its table out at all. Once a second is
    /// plenty - and only when something has actually changed, so a settled table costs a
    /// comparison and nothing else.
    ///
    /// "Changed" has to mean the bodies as well as the count, not just the count. A client's
    /// Manager arrives before the players do: laying out on the count alone ran the one and
    /// only pass while the local player was the only body in the scene, moved that one, and
    /// left everybody who spawned a few frames later standing at the shipped eight-seat
    /// positions for the rest of the match - on that screen only, while the host's log
    /// happily reported an even table.
    /// </summary>
    private static void KeepClientInStep()
    {
        try
        {
            var m = Manager.Instance;
            if (m == null || m.Slots == null || m.Slots.Count == 0) { ForgetClientTable(); return; }

            bool server;
            try { server = m.isServer; } catch { return; }
            if (server) return;

            // A second match in the same session gets a new Manager. Comparing identity
            // rather than waiting for a null gap means the new table cannot inherit the old
            // one's numbers and skip its own layout.
            int id = m.GetInstanceID();
            if (id != _clientMatch) { ForgetClientTable(); _clientMatch = id; }

            if (Time.time < _clientNext) return;
            _clientNext = Time.time + 1f;

            if (!TableIsLive(m)) return;

            int players = PlayerCount();
            if (players < 2) return;

            int bodies = PlayersHere(m).Count;
            if (players == _clientLastCount && bodies == _clientLastBodies) return;
            _clientLastCount = players;
            _clientLastBodies = bodies;

            Plugin.Log.LogInfo(
                $"[seatring] laying this client's table out for {players} players ({bodies} in the scene)");
            Layout();
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[seatring] client layout failed: {e.Message}"); }
    }

    private static void ForgetClientTable()
    {
        _clientLastCount = -1;
        _clientLastBodies = -1;
        _clientMatch = 0;
    }

    /// <summary>
    /// Whether this is a table the host is also laying out.
    ///
    /// The host only re-spaces from <c>DeckGamePlayManager.ResetRound</c>, so it only ever
    /// does so in the deck modes. A client that laid out regardless would re-space a Liar's
    /// Dice table the host had left alone, and the two machines would show the same players
    /// in different chairs. Agreeing to do nothing is as important as agreeing where to sit.
    /// </summary>
    private static bool TableIsLive(Manager m)
    {
        try
        {
            var deck = m.DeckGamePlayManager;
            return deck != null && deck.gameObject != null && deck.gameObject.activeInHierarchy;
        }
        catch { return false; }
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
    /// List the scenery standing at the first four seat positions, once per session.
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
                        $"[furniture] near seat {s} ({d:F2}m): '{t.name}' " +
                        $"under '{key}' at {t.position.ToString("F2")}");
                    shown++;
                }
            }

            if (shown == 0) Plugin.Log.LogInfo("[furniture] nothing found standing at the seats");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[furniture] probe failed: {e.Message}"); }
    }
}
