using System;
using HarmonyLib;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Shares the table out evenly between however many are playing, and moves the players onto
/// their seats.
///
/// The seat list is expanded to the configured maximum so the indices exist, but laying the
/// ring out for that maximum left five players occupying seats 0-4 of an eight seat ring -
/// half the table, bunched together with empty gaps opposite.
///
/// Re-spacing alone is not enough: bodies are placed when they spawn, using wherever the seat
/// was at that moment, so moving a seat transform afterwards leaves the character behind.
/// Players are therefore moved with their seats.
///
/// Two rules hold this together, and both were learned the hard way:
///
/// **Every seat stays on the table's own circle.** Not below it, not outside it, not parked
/// somewhere out of the way. There is nothing to gain by moving an empty seat away, and
/// everything to lose: an empty seat is only empty until the count this code is working from
/// turns out to be wrong, and then somebody is standing wherever it was put. Dropping them
/// four metres put players under the map; pushing them outside the ring scattered them across
/// the bar. The one thing that hangs off a seat is a name plate, and this switches off the
/// ones belonging to seats nobody is in - the host's roster does that for itself, but it does
/// it in server-side code, so every other machine was left showing them.
///
/// **The circle is measured once and never measured again.** It used to be re-fitted from the
/// live seat transforms every pass, which is fine right up until one of the seats it reads has
/// been moved by this same code - then each pass fits a bigger circle than the last. With
/// seats pushed out to 2.4x the radius the table inflated by exactly that factor per round:
/// 1.33m, 3.19m, 7.66m, 18.38m, players strewn across the level. Reading a stored circle makes
/// that impossible by construction rather than by careful ordering.
/// </summary>
internal static class SeatRing
{
    // ------------------------------------------------------------------ the table

    /// <summary>
    /// The table's circle, measured once per match from the seats as the game and the seat
    /// expansion left them, before this class has moved anything.
    /// </summary>
    private static Manager _ringFor;
    private static Vector2 _centre;
    private static float _radius;
    private static float _height;
    private static float _startDeg;
    private static bool _haveRing;

    /// <summary>How many ways the ring was last divided, so the check can judge against it.</summary>
    private static int _ringSeats;

    private static int _lastCount = -1;

    /// <summary>When to check where everyone actually ended up. Zero means never.</summary>
    private static float _measureAt;

    /// <summary>
    /// Measure the table, once per match.
    ///
    /// Fitted from the first four seats, which are the ones the game itself placed and which
    /// the seat expansion arranged into a circle - a genuine measurement of the furniture.
    /// Everything afterwards is computed from the result, so no later pass can feed this
    /// class's own output back into it.
    /// </summary>
    private static bool HaveRing(Manager m)
    {
        if (_haveRing && ReferenceEquals(_ringFor, m)) return true;

        try
        {
            var slots = m.Slots;
            if (slots == null || slots.Count == 0) return false;

            int sample = Mathf.Min(Limits.VanillaPlayers, slots.Count);
            if (sample < 3) return false;      // three points define a circle; two do not

            var pts = new Vector2[sample];
            float y = 0f;
            for (int i = 0; i < sample; i++)
            {
                var t = slots[i];
                if (t == null) return false;
                pts[i] = new Vector2(t.position.x, t.position.z);
                y += t.position.y;
            }
            y /= sample;

            Geometry.FitCircle(pts, out Vector2 c, out float r);

            // A fit that produces nonsense is worse than no fit at all: it would move every
            // player to a nonsense position. Refuse it and leave the table alone.
            if (!IsSane(c) || r <= 0.1f || r > 10f || float.IsNaN(r))
            {
                Plugin.Log.LogWarning(
                    $"[seatring] the table measured as centre ({c.x:F2}, {c.y:F2}) r={r:F2}, " +
                    "which cannot be right - leaving the seats alone");
                return false;
            }

            _centre = c;
            _radius = r;
            _height = y;
            _startDeg = Mathf.Atan2(slots[0].position.z - c.y, slots[0].position.x - c.x) * Mathf.Rad2Deg;
            _ringFor = m;
            _haveRing = true;
            _lastCount = -1;

            Plugin.Log.LogInfo(
                $"[seatring] table measured once: centre ({c.x:F2}, {c.y:F2}) r={r:F2} " +
                $"height {y:F2}, seat 0 at {_startDeg:F1}deg");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[seatring] could not measure the table: {e.Message}");
            return false;
        }
    }

    private static bool IsSane(Vector2 v) =>
        !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.x) && !float.IsInfinity(v.y);

    /// <summary>Where seat <paramref name="index"/> sits when <paramref name="n"/> are playing.</summary>
    private static Vector3 SeatPosition(int index, int n)
    {
        float rad = (_startDeg + (360f / n) * index) * Mathf.Deg2Rad;
        return new Vector3(
            _centre.x + _radius * Mathf.Cos(rad), _height, _centre.y + _radius * Mathf.Sin(rad));
    }

    private static Quaternion SeatFacing(int index, int n)
    {
        float rad = (_startDeg + (360f / n) * index) * Mathf.Deg2Rad;
        return Quaternion.Euler(
            0f, Mathf.Atan2(-Mathf.Cos(rad), -Mathf.Sin(rad)) * Mathf.Rad2Deg, 0f);
    }

    // ------------------------------------------------------------------ who is here

    /// <summary>
    /// The players at this table, as this machine can see them.
    ///
    /// <c>Manager.Players</c> is the server's roster and is empty on a client, so anything
    /// that walked it did nothing there - including moving bodies onto the re-spaced seats,
    /// which is the whole point. A client has the player objects all the same; it just has to
    /// find them in the scene rather than being handed a list.
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
    /// So the synced count comes first and the server's own roster second. <c>Manager.Players</c>
    /// fills in over several frames on the host and is empty on a client for the whole match,
    /// so preferring it meant the host sized the ring from a half-filled roster while clients
    /// sized it from something else entirely.
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
    /// How many ways the ring is divided: the synced player count, and nothing else.
    ///
    /// This briefly also took the highest seat index anybody was seen holding, so that a count
    /// arriving late could not leave somebody out of the arrangement. That was a mistake, and
    /// a bad one, because it is a *locally observed* number: the host reads the server roster,
    /// a client scans the scene and reads a <c>Slot</c> SyncVar that may not have caught up
    /// yet. The two peers then divide the table differently - 60 degrees apart on one screen
    /// and 72 on another - and since positions are not synced, nothing corrects it. Every input
    /// to the layout has to be something all peers already agree on.
    ///
    /// Nobody is stranded by dropping it, because spare seats are on the ring too: a body
    /// holding a slot past the count is still placed at a real seat on the table.
    /// </summary>
    private static int RingSize(int players, int seatCount) =>
        Mathf.Clamp(players, 2, seatCount);

    // ------------------------------------------------------------------ laying out

    [HarmonyPrefix]
    [HarmonyPriority(Priority.Last)]    // after the roster is corrected and seats compacted
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.ResetRound))]
    private static void Space(DeckGamePlayManager __instance, bool first)
    {
        _retries = 0;          // a new round gets its full allowance of attempts
        Layout();
    }

    /// <summary>
    /// Put every seat on the table's circle and every player on their seat.
    ///
    /// Runs on every machine, not just the host. <c>ResetRound</c> is server-side code and
    /// never executes on a client, so for a long time this ran only on the host: the host saw
    /// an evenly spaced table and everyone else was still looking at the four seats the game
    /// ships with, which is exactly what "they are all on one side" looks like from a player's
    /// seat, and could not be seen from the screen it was being judged on.
    ///
    /// Every machine computing this for itself is safe because nothing in it depends on the
    /// machine: a circle measured from level geometry that is identical in every copy of the
    /// game, divided by a synced count. A client is moving a body to the position the host has
    /// already given it.
    ///
    /// Idempotent, deliberately. Running it twice changes nothing, so it can be re-run freely
    /// whenever the table looks wrong.
    /// </summary>
    private static void Layout()
    {
        try
        {
            var m = Manager.Instance;
            if (m == null || m.Slots == null || m.Slots.Count == 0) return;
            if (!HaveRing(m)) return;

            int players = PlayerCount();
            if (players < 2) return;

            var slots = m.Slots;
            int n = RingSize(players, slots.Count);
            _ringSeats = n;

            if (n != _lastCount)
            {
                _lastCount = n;
                Plugin.Log.LogInfo(
                    $"[seatring] {players} players -> {n} seats {360f / n:F1}deg apart on the table");
            }

            for (int i = 0; i < slots.Count; i++)
            {
                var t = slots[i];
                if (t == null) continue;

                // Seats in play get their share of the circle. Spare seats go on the same
                // circle, halfway between two of them - out of everybody's way, still at the
                // table, and impossible to strand anyone who turns out to be sitting in one.
                // An empty seat is invisible; the only thing hanging off it is a name plate,
                // which the roster hides.
                if (i < n)
                {
                    t.position = SeatPosition(i, n);
                    t.rotation = SeatFacing(i, n);
                }
                else
                {
                    // Spread the spares evenly right round the circle, offset by half a step so
                    // they land between the seats in play rather than on top of one. The first
                    // attempt stepped them by the *playing* count, which stacked several on one
                    // spot at small tables - four spare seats sharing two positions at a two
                    // player table.
                    int spares = slots.Count - n;
                    float deg = _startDeg + (360f / n) * 0.5f + (360f / spares) * (i - n);
                    float rad = deg * Mathf.Deg2Rad;
                    t.position = new Vector3(
                        _centre.x + _radius * Mathf.Cos(rad), _height,
                        _centre.y + _radius * Mathf.Sin(rad));
                    t.rotation = Quaternion.Euler(
                        0f, Mathf.Atan2(-Mathf.Cos(rad), -Mathf.Sin(rad)) * Mathf.Rad2Deg, 0f);
                }
            }

            HideSpareNameplates(m, n);
            SnapPlayersToSeats(m);

            // Keep asserting it for a few seconds. Wherever this runs after the game has
            // already seated people - every client, and the host too in the modes that have no
            // round-reset hook - the game puts them back where they were, repeatedly, and a
            // single correction never holds.
            _holdUntil = Time.time + HoldSeconds;

            // Placing them is not the same as them staying put. Anything the game does to a
            // player after this runs - a spawn point, an animation root, a network correction -
            // lands afterwards, so the only honest check is to look again once the round is
            // actually under way.
            //
            // Soon, and more than once, rather than late and once. A client lays its table out
            // before the game has finished seating people, so the first check almost always
            // finds it wrong; checking six seconds later meant six seconds of a visibly wrong
            // table on every screen but the host's. Checking sooner and retrying closes that
            // to about two, and costs nothing because laying out again is idempotent.
            _measureAt = Time.time + 2.5f;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[seatring] laying out the table failed: {e.Message}");
        }
    }

    /// <summary>
    /// Switch off the name plates belonging to seats nobody is playing in.
    ///
    /// The class comment used to claim an empty seat was invisible because "the only thing
    /// hanging off it is a name plate, which the roster hides". That is true on the host, where
    /// RosterFix trims the plate list - and false everywhere else, because RosterFix runs from
    /// the server-side round reset. So the rewrite that moved spare seats onto the ring did
    /// something worse than the parking it replaced: it put live, unhidden name plates on the
    /// table *between* the players, on every client, and nothing on the host would ever show it.
    ///
    /// Purely local presentation - a plate being drawn or not is not shared state - so every
    /// peer does it for itself, and re-does it on each layout so a plate comes back if the
    /// table grows.
    /// </summary>
    private static void HideSpareNameplates(Manager m, int n)
    {
        try
        {
            var plates = m.NameTexts;
            if (plates == null) return;

            for (int i = 0; i < plates.Count; i++)
            {
                var plate = plates[i];
                if (plate == null) continue;
                var go = plate.gameObject;
                if (go == null) continue;

                bool wanted = i < n;
                if (go.activeSelf != wanted) go.SetActive(wanted);
            }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[seatring] name plates: {e.Message}"); }
    }

    /// <summary>
    /// Put each player where their seat now is.
    ///
    /// This used to be the host's job alone, on the reasoning that a client moving a networked
    /// player would fight Mirror's synchronisation. It does not: player positions are not
    /// synced at all, which is why every client logged bodies being moved the first time this
    /// was allowed to run there. Keeping it host-only is what left everyone else looking at
    /// the shipped four seats.
    /// </summary>
    private static void SnapPlayersToSeats(Manager m, bool quiet = false)
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
                    Vector3 from = root.position;
                    root.position = seat.position;
                    root.rotation = seat.rotation;
                    moved++;

                    // Did the write actually take? A body that is not where it was just put,
                    // one line later, is being held by something else - a parent, a character
                    // controller, a rigidbody - and that is a completely different problem from
                    // the seat being in the wrong place.
                    if (quiet) { _quietMoves++; continue; }

                    float landed = Vector3.Distance(root.position, seat.position);
                    if (landed > 0.01f)
                        Plugin.Log.LogWarning(
                            $"[seatring] '{p.PlayerName}' (seat {slot}) would not move: asked for " +
                            $"{seat.position.ToString("F2")}, sits at {root.position.ToString("F2")}; " +
                            $"root '{root.name}' parent " +
                            $"'{(root.parent != null ? root.parent.name : "<none>")}'");
                    else if (_retries > 0)
                        Plugin.Log.LogInfo(
                            $"[seatring] '{p.PlayerName}' (seat {slot}) moved {from.ToString("F2")} " +
                            $"-> {seat.position.ToString("F2")}, root '{root.name}'");
                }
            }

            // One short of the table is the expected count, not a miss: whoever sits in the
            // seat the circle is anchored to is already where they belong.
            if (moved > 0 && !quiet)
                Plugin.Log.LogInfo($"[seatring] moved {moved} player(s) onto their seats");
        }
        catch (Exception e) { Plugin.Log.LogError($"[seatring] seating players failed: {e.Message}"); }
    }

    // ------------------------------------------------------------------ keeping up

    private static float _clientNext;
    private static int _clientLastCount = -1;
    private static int _clientLastBodies = -1;
    private static int _clientMatch;

    /// <summary>How many times the layout has been re-run for the current round.</summary>
    private const int MaxRetries = 4;
    private static int _retries;

    /// <summary>Until when a client keeps re-asserting where the bodies belong.</summary>
    private static float _holdUntil;
    private static int _corrections;

    internal static void Tick()
    {
        KeepTableInStep();
        HoldSeats();

        if (_measureAt <= 0f || Time.time < _measureAt) return;
        _measureAt = 0f;
        Measure();
    }

    /// <summary>
    /// Keep the bodies on their seats for a few seconds after laying a client's table out.
    ///
    /// The host lays out from the round reset, which happens before the game seats anybody, so
    /// the game's own seating starts from the corrected positions and everything agrees. A
    /// client has no such hook - it lays out from the ticker, a second or so after the game has
    /// already put everybody down - and whatever the game does next puts them back where they
    /// were. Re-measuring showed the identical four players moved from the identical wrong
    /// places on every attempt, so a single correction, however well timed, was never going to
    /// hold.
    ///
    /// So it is asserted rather than set: for a short window, and only on a client, and only
    /// for a body more than a few centimetres from where it belongs. Bounded because a fight
    /// that has to be fought forever is the wrong fix, and the count says plainly whether that
    /// is what is happening.
    /// </summary>
    private static void HoldSeats()
    {
        if (_holdUntil <= 0f) return;

        try
        {
            var m = Manager.Instance;
            if (m == null) { _holdUntil = 0f; return; }

            if (Time.time > _holdUntil)
            {
                _holdUntil = 0f;
                if (_corrections > 0)
                    Plugin.Log.LogInfo(
                        $"[seatring] held the table for {HoldSeconds:F0}s and corrected it " +
                        $"{_corrections} time(s); checking shortly whether it stays put");
                _corrections = 0;

                // The whole point of the hold is that something else was moving these bodies.
                // Letting go without looking again would only prove the table was right while
                // being held, which is not the question.
                _measureAt = Time.time + 3f;
                return;
            }

            int before = _quietMoves;
            SnapPlayersToSeats(m, quiet: true);
            _corrections += _quietMoves - before;
        }
        catch { _holdUntil = 0f; }
    }

    private const float HoldSeconds = 12f;
    private static int _quietMoves;

    /// <summary>
    /// Lay the table out, on every machine, whenever the table changes shape.
    ///
    /// The host also has a round-reset hook, which is earlier and better timed - but it only
    /// exists in the deck modes, and a client never runs it at all. This is the trigger that
    /// works everywhere.
    ///
    /// "Changed" has to mean the bodies as well as the count. A client's Manager arrives before
    /// the players do: laying out on the count alone ran the one and only pass while the local
    /// player was the only body in the scene, moved that one, and left everybody who spawned a
    /// few frames later standing at the shipped positions for the rest of the match - on that
    /// screen only, while the host's log happily reported an even table.
    /// </summary>
    private static void KeepTableInStep()
    {
        try
        {
            var m = Manager.Instance;
            if (m == null || m.Slots == null || m.Slots.Count == 0) { ForgetClientTable(); return; }

            // A second match in the same session gets a new Manager. Comparing identity rather
            // than waiting for a null gap means the new table cannot inherit the old one's
            // numbers and skip its own layout.
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
            _retries = 0;

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
    /// Whether a game is actually being played at this table, in any mode.
    ///
    /// This used to ask only about the deck manager, because the host's own trigger is a patch
    /// on <c>DeckGamePlayManager.ResetRound</c> and the two had to agree about when to act.
    /// That made the whole seat ring a Liar's Deck feature by accident: Liar's Dice, Poker and
    /// the rest share this table and the same eight seats, and at five, six or seven players
    /// they were left in the shipped eight-seat arrangement - bunched round one side, which is
    /// the exact complaint this class exists to answer.
    ///
    /// Now every mode counts, and both the host and the clients drive it from the ticker, so
    /// the trigger no longer depends on which game is being played.
    /// </summary>
    private static bool TableIsLive(Manager m)
    {
        try
        {
            if (Alive(m.DeckGamePlayManager)) return true;
            if (Alive(m.DiceGame)) return true;
            if (Alive(m.PokerGame)) return true;
            if (Alive(m.TexasGame)) return true;
            if (Alive(m.ChaosGame)) return true;
            if (Alive(m.ChaosDeckGame)) return true;
            if (Alive(m.BlorfGame)) return true;
            if (Alive(m.BlorfGameMatchMaking)) return true;
        }
        catch { }
        return false;
    }

    private static bool Alive(UnityEngine.Component c)
    {
        try { return c != null && c.gameObject != null && c.gameObject.activeInHierarchy; }
        catch { return false; }
    }

    // ------------------------------------------------------------------ the check

    /// <summary>
    /// Report where every player actually is once the round is running: bearing around the
    /// table, distance from the middle, and the gap to the next player round the ring.
    ///
    /// Even spacing is exactly the claim "the gaps are all the same", so the gaps are what this
    /// prints. Reading positions and judging by eye is how a table that looked wrong got called
    /// even and a table that was even got called wrong.
    ///
    /// It also acts on the answer. Placing bodies and the game placing bodies are a race, and
    /// this loses it often enough to matter, so a table that comes out wrong is laid out again.
    /// That is only safe because the layout is idempotent now - it reads a stored circle, so
    /// re-running it cannot move anything that is already right. When it did re-measure the
    /// circle each pass, this retry is what turned a single mistake into a table fourteen times
    /// too big.
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
            float furthest = 0f;
            float worstOffset = 0f;

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
                if (Mathf.Abs(dist - _radius) > furthest) furthest = Mathf.Abs(dist - _radius);

                // How far this body is from the seat its own slot maps to on the stored circle.
                float offset = -1f;
                if (_ringSeats >= 2 && p.Slot >= 0)
                {
                    Vector3 want = p.Slot < _ringSeats
                        ? SeatPosition(p.Slot, _ringSeats)
                        : root.position;                  // a spare seat; judged by the ring test
                    offset = Vector3.Distance(w, want);
                    if (offset > worstOffset) worstOffset = offset;
                }

                bearings.Add(deg);
                lines.Add($"  seat {p.Slot} '{p.PlayerName}' at {deg,6:F1}deg  r={dist:F2}  " +
                          $"off its seat {offset:F2}m  pos={w.ToString("F2")}");
            }

            if (bearings.Count == 0 || _ringSeats < 2) return;   // nothing to judge; no retry spent

            bearings.Sort();
            float widest = 0f, narrowest = 360f;
            for (int i = 0; i < bearings.Count; i++)
            {
                float gap = bearings.Count == 1
                    ? 360f
                    : Mathf.Repeat(bearings[(i + 1) % bearings.Count] - bearings[i], 360f);
                if (gap > widest) widest = gap;
                if (gap < narrowest) narrowest = gap;
            }

            // The verdict is per player, against the seat their own slot maps to - not against
            // the gaps between the bodies this machine happens to see.
            //
            // Counting bodies was wrong in a way that only showed up with bots: a bot's object
            // is never network-spawned, so a client sees two bodies at an eight player table,
            // measures gaps of 45 and 315 degrees against an "ideal" of 180, calls a perfectly
            // correct table UNEVEN, and burns every retry re-laying out a table that was right
            // the first time. Distance from your own seat needs no agreement about population.
            bool good = worstOffset < 0.25f;

            Plugin.Log.LogInfo(
                $"[seatcheck] {bearings.Count} of {_ringSeats} visible here; furthest from its own " +
                $"seat {worstOffset:F2}m, from the table edge {furthest:F2}m; " +
                $"gaps {narrowest:F1}..{widest:F1}deg -> {(good ? "GOOD" : "WRONG")}, r={_radius:F2}");
            foreach (var l in lines) Plugin.Log.LogInfo(l);

            // When it is wrong, say where the seats themselves are. "The body is not on its
            // seat" has two very different causes - the seat is in the wrong place, or the seat
            // is right and something keeps dragging the body off it - and they need opposite
            // fixes. Printing both ends tells them apart at a glance.
            if (!good)
            {
                try
                {
                    var slots = m.Slots;
                    for (int i = 0; slots != null && i < slots.Count; i++)
                    {
                        var t = slots[i];
                        if (t == null) continue;
                        float sdeg = Mathf.Repeat(
                            Mathf.Atan2(t.position.z - _centre.y, t.position.x - _centre.x) * Mathf.Rad2Deg, 360f);
                        Plugin.Log.LogInfo(
                            $"    seat[{i}] '{t.name}' at {sdeg,6:F1}deg  {t.position.ToString("F2")}" +
                            (i < _ringSeats ? "" : "  (spare)"));
                    }
                }
                catch { }
            }

            if (!good && _retries < MaxRetries)
            {
                _retries++;
                Plugin.Log.LogWarning(
                    $"[seatcheck] laying the table out again (attempt {_retries} of {MaxRetries})");
                Layout();
                return;
            }

            if (!good)
                Plugin.Log.LogError(
                    $"[seatcheck] the table is still wrong after {MaxRetries} attempts - " +
                    "something else is moving the players after this does");
        }
        catch (Exception e) { Plugin.Log.LogError($"[seatcheck] failed: {e.Message}"); }
    }
}
