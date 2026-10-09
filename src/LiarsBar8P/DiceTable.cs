using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Liar's Dice at a table of more than four: the things on and around the table that the game
/// lays out for exactly four seats.
///
/// Four separate faults, all with the same root. The game places these by the four seats it
/// shipped with, and the seat ring puts players everywhere else:
///
///   - the panel in the middle of the table (whose turn, the total, LIAR!, the loser) is turned
///     to face you by an animation with one pose per vanilla seat, so most of eight seats see it
///     side-on or upside down - <see cref="DiceCentrePanel"/>;
///   - the row of dice shown over your cup during a reveal sits where your neighbour's right
///     hand and poison bottles are once seats are 45-60 degrees apart - <see cref="DiceLabels"/>;
///   - a dead player's spectator cameras were placed in the gaps between the four vanilla
///     seats, which is exactly where the extra seats go, so a spectator looks through the back
///     of somebody's head - <see cref="SpectatorCams"/>;
///   - the panel that lays out the dice of a called bid has room for twenty, and a bid above
///     twenty threw part way through drawing it - <see cref="DiceRevealRows"/>.
///
/// This class is the shared part: one entry point driven every frame from the ticker's
/// LateUpdate (the centre panel has to be turned after the game's animation has written it,
/// which is only true in LateUpdate), a few times a second of bookkeeping, and the facts each
/// part needs - which match, how many seats, which body is this computer's, where the table is.
///
/// Every part leaves a table of four exactly as shipped. All of it is local presentation: no
/// state that other machines read is touched, so each peer can do it for itself.
/// </summary>
internal static class DiceTable
{
    /// <summary>How often the bookkeeping runs. The per-frame work is separate and tiny.</summary>
    private const float HousekeepingSeconds = 0.25f;

    /// <summary>
    /// The bookkeeping's cadence, kept on the managed clock. Unity's own clock is a call into the
    /// game's runtime that boxes its answer on the game's heap, and this is asked every frame.
    /// </summary>
    private static readonly long HousekeepingTicks =
        (long)(System.Diagnostics.Stopwatch.Frequency * (double)HousekeepingSeconds);

    private static long _nextTick;
    private static int _match;
    private static float _matchAt;

    /// <summary>Seats at this table, from the synced count every machine agrees on.</summary>
    internal static int Seats { get; private set; }

    /// <summary>Whether this computer's own player is playing Liar's Dice.</summary>
    internal static bool IsDice { get; private set; }

    /// <summary>The root of this computer's own seated character, or null.</summary>
    internal static Transform Me { get; private set; }

    /// <summary>This computer's own seated character, or null.</summary>
    internal static CharController Local { get; private set; }

    /// <summary>When this match was first seen, for things that should wait for it to settle.</summary>
    internal static float MatchAge => Time.time - _matchAt;

    // ------------------------------------------------------------------ the table

    internal static bool HaveRing { get; private set; }
    internal static Vector2 Centre { get; private set; }
    internal static float StartDeg { get; private set; }

    /// <summary>The seat circle's radius. The vanilla table's, until it has been measured.</summary>
    internal static float RingRadius { get; private set; } = 1.33f;

    private static float _lookFor;
    private static int _failures;

    /// <summary>
    /// Called every frame from <see cref="ModTicker"/>'s LateUpdate. Never throws.
    /// </summary>
    internal static void LateTick()
    {
        long tick = System.Diagnostics.Stopwatch.GetTimestamp();
        if (tick >= _nextTick)
        {
            _nextTick = tick + HousekeepingTicks;
            Housekeeping(Time.time);
        }

        if (DiceCentrePanel.On) DiceCentrePanel.Face();
        if (DiceRevealRows.On) DiceRevealRows.Follow();
    }

    private static void Housekeeping(float now)
    {
        try
        {
            var m = Manager.Instance;
            if (m == null)
            {
                if (_match != 0) Forget();
                return;
            }

            // A new match gets a new Manager. Identity rather than a null gap, so a second
            // match cannot inherit the first one's bodies, table or counts.
            int id = m.GetInstanceID();
            if (id != _match)
            {
                Forget();
                _match = id;
                _matchAt = now;
            }

            Seats = AimRing.Seats(m);
            if (!HaveRing) Measure(m);
            FindLocal(now);
        }
        catch (Exception e)
        {
            // A few lines a match at most: this runs four times a second.
            if (++_failures <= 3) Plugin.Log.LogWarning($"[dicetable] bookkeeping failed: {e.Message}");
            return;
        }

        DiceCentrePanel.Housekeep(now);
        DiceLabels.Housekeep(now);
        SpectatorCams.Housekeep();
    }

    private static void Forget()
    {
        _match = 0;
        Seats = 0;
        IsDice = false;
        Me = null;
        Local = null;
        HaveRing = false;
        RingRadius = 1.33f;
        _lookFor = 0f;
        _failures = 0;

        DiceCentrePanel.Forget();
        DiceLabels.Forget();
        SpectatorCams.Forget();
        DiceRevealRows.Stop();
    }

    /// <summary>
    /// The table's centre and seat 0's bearing, from the first four seats - the ones the game
    /// placed. The same measurement <see cref="SeatRing"/> makes, and it stays true after the
    /// ring is laid out: every seat stays on the circle and seat 0 never moves round it.
    /// </summary>
    private static void Measure(Manager m)
    {
        var slots = m.Slots;
        if (slots == null) return;
        int sample = Mathf.Min(Limits.VanillaPlayers, slots.Count);
        if (sample < 3) return;

        var pts = new Vector2[sample];
        for (int i = 0; i < sample; i++)
        {
            var t = slots[i];
            if (t == null) return;
            pts[i] = new Vector2(t.position.x, t.position.z);
        }

        Geometry.FitCircle(pts, out Vector2 c, out float r);
        if (float.IsNaN(r) || r < 0.5f || r > 5f) return;

        Centre = c;
        RingRadius = r;
        StartDeg = Mathf.Atan2(pts[0].y - c.y, pts[0].x - c.x) * Mathf.Rad2Deg;
        HaveRing = true;
    }

    /// <summary>
    /// The character this computer is playing, found by ownership, once - and again only if it
    /// goes away. A scene search every frame would be absurd for something that changes once a
    /// match at most.
    /// </summary>
    private static void FindLocal(float now)
    {
        if (Me != null && Local != null) return;
        if (now < _lookFor) return;
        _lookFor = now + 1f;

        Me = null;
        Local = null;
        IsDice = false;

        var all = UnityEngine.Object.FindObjectsOfType<CharController>();
        if (all == null) return;

        for (int i = 0; i < all.Count; i++)
        {
            var c = all[i];
            if (c == null) continue;
            try { if (!c.isOwned) continue; }
            catch { continue; }
            if (c.playerStats == null) continue;

            Local = c;
            Me = c.transform.root;
            IsDice = c.TryCast<DiceGamePlay>() != null;
            return;
        }
    }
}

/// <summary>
/// Turns the panel in the middle of a Liar's Dice table to face you, at every seat.
///
/// That panel - the running total, whose turn it is, LIAR! and who lost - is turned towards
/// each player by the game's own animation: every frame your character sets the animator's
/// <c>Slot</c> to your seat number, and the animator has four poses, one per vanilla seat, each
/// a fixed angle. At eight players seat 1 sees it 45 degrees off, seat 2 at right angles, and
/// seats 4 to 7 have no pose at all and see it facing seat 0 - seat 4 reads it upside down.
///
/// The four poses are one rule, written out four times: the panel's roll is 90 degrees minus
/// the bearing of your seat from the panel. So that rule is applied to wherever you actually
/// sit, after the animator has written its pose each frame. At the four vanilla bearings it
/// gives exactly the shipped 180, 90, 0 and 270 - but at exactly four seats this does nothing
/// at all and the game's own animation is left to do it. A table of two or three is turned
/// too: the seat ring spaces those evenly as well, so their seats are no longer where the
/// game's poses point either - at two players seat 1 sits opposite seat 0 and its pose is a
/// quarter turn out.
///
/// The animator is left running rather than switched off: the game sets its parameter every
/// frame and the panel needs nothing else from it, so overwriting its one output is the
/// smallest change. Per frame this is one angle written - no searching, nothing read back and
/// nothing created by the mod. The angle itself is worked out a few times a second with the
/// rest of the bookkeeping: neither the panel nor a seated player moves during play, and a
/// quarter of a second is quick enough for the moment the seat ring settles a body. The
/// player's own character is found once a match.
/// </summary>
internal static class DiceCentrePanel
{
    internal static bool On;

    private static Transform _hud;
    private static float _roll;
    private static bool _said;
    private static bool _checked;
    private static int _failures;
    private const int GiveUpAfter = 5;

    internal static void Forget()
    {
        On = false;
        _hud = null;
        _roll = 0f;
        _said = false;
        _checked = false;
        _failures = 0;
        _lookups = 0;
    }

    internal static void Housekeep(float now)
    {
        try
        {
            if (!DiceTable.IsDice || DiceTable.Me == null) { On = false; return; }

            if (_hud == null)
            {
                var m = Manager.Instance;
                var dice = m != null ? m.DiceGame : null;
                var anim = dice != null ? dice.Totaling : null;
                _hud = anim != null ? anim.transform : null;
                if (_hud == null) { On = false; return; }
            }

            // Fewer than two is a count that has not arrived yet, not a table: a client reads
            // nothing until the synced count does.
            int seats = DiceTable.Seats;
            bool turn = Turns(seats) && _failures < GiveUpAfter;

            // Before anything is written this frame, so the check reads the game's own angle.
            CheckAgainstTheGame(seats);

            if (!turn) { On = false; return; }

            _roll = Wanted(DiceTable.Me.position, _hud.position);
            On = true;

            if (!_said)
            {
                _said = true;
                Plugin.Log.LogInfo(
                    $"[dicetable] the centre panel is turned to face this seat at {seats} players - " +
                    "the game's animation only knows where the four vanilla seats were");
            }
        }
        catch (Exception e)
        {
            On = false;
            if (++_lookups <= 3) Plugin.Log.LogWarning($"[dicetable] centre panel lookup failed: {e.Message}");
        }
    }

    private static int _lookups;

    /// <summary>
    /// Whether a table this size is turned: any table the seat ring has re-spaced, which is
    /// every one but the four the game was built for.
    /// </summary>
    private static bool Turns(int seats) => seats >= 2 && seats != Limits.VanillaPlayers;

    /// <summary>
    /// Every frame while on: write the angle the bookkeeping worked out over the one the
    /// animator has just written. Nothing is read from the game here - not even whether the
    /// panel still exists, since asking costs as much as the write. A panel that has gone with
    /// its scene throws, which is caught, and the bookkeeping finds the next one.
    /// </summary>
    internal static void Face()
    {
        try
        {
            var roll = default(Vector3);
            roll.z = _roll;
            _hud.localEulerAngles = roll;
        }
        catch (Exception e)
        {
            On = false;

            // Gone with its scene is not a failure; the next match's panel is looked up afresh.
            bool gone;
            try { gone = _hud == null; } catch { gone = true; }
            if (gone) { _hud = null; return; }

            _failures++;
            Plugin.Log.LogWarning($"[dicetable] could not turn the centre panel ({_failures}/{GiveUpAfter}): {e.Message}");
        }
    }

    /// <summary>
    /// The roll the game's four poses encode: 90 degrees minus the seat's bearing. Plain
    /// arithmetic on the vectors' fields rather than Unity's maths helpers, which in this build
    /// are calls into the game's runtime.
    /// </summary>
    private static float Wanted(Vector3 seat, Vector3 panel) =>
        90f - (float)(Math.Atan2(seat.z - panel.z, seat.x - panel.x) * (180.0 / Math.PI));

    /// <summary>
    /// Developer mode only: once a match, the angle the game's animation chose next to the one
    /// the rule gives. At four players the two must agree (180, 90, 0, 270 for seats 0-3) -
    /// that is the test that the rule is the game's own. At any other size they differ, and the
    /// second is what the panel is turned to. Read in LateUpdate before this class writes, so
    /// the first number is the game's.
    /// </summary>
    private static void CheckAgainstTheGame(int seats)
    {
        if (!Dev.Enabled || _checked || seats < 2 || DiceTable.MatchAge < 6f) return;
        _checked = true;

        try
        {
            var local = DiceTable.Local;
            int slot = local != null && local.playerStats != null ? local.playerStats.Slot : -1;
            float game = _hud.localEulerAngles.z;
            float rule = Mathf.Repeat(Wanted(DiceTable.Me.position, _hud.position), 360f);

            Plugin.Log.LogInfo(
                $"[dicetable] centre panel check, seat {slot} of {seats}: the game's animation set " +
                $"{game:F1}deg, the rule gives {rule:F1}deg " +
                (Turns(seats) ? "(the mod turns it to the rule)" : "(four seats: left as shipped, these should match)"));
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[dicetable] centre panel check failed: {e.Message}"); }
    }
}

/// <summary>
/// Lifts each player's row of dice labels clear of the neighbour's arm at six or more seats.
///
/// Every character carries its own labels next to its own dice: the row of dice shown over your
/// cup to everybody else during a reveal, and the one you see when you look at your own dice.
/// They sit on the table to the character's left. The neighbour on that side keeps its right
/// hand and its two poison bottles a little to its right, and at four seats, ninety degrees
/// apart, there is half a metre between the two. At six the gap is 60 degrees and a bottle sits
/// a few centimetres from the labels; at eight it is on top of them, which is what "a hand
/// covers the dice" in a report of an eight player game most likely was.
///
/// So at six seats or more the two labels are moved a short way up and towards the middle of
/// the table, out from behind the neighbour's things. Only the labels: the dice and the cup are
/// never moved, because moving them could put somebody's hidden dice where another player can
/// see them.
///
/// Measured in the character's own frame, so it does not matter whether the body has reached
/// its seat yet: every character faces the middle of the table wherever it sits. Done once per
/// body - and again if the table's size turns out different from what it first read - always
/// from where the label started, so doing it again changes nothing; a body that is used again
/// at a smaller table is put back. Nothing in the game moves these labels - their own
/// animation moves only the dice icons inside them - so a move made once stays made.
/// </summary>
internal static class DiceLabels
{
    /// <summary>The smallest table where the neighbour's bottles reach the labels.</summary>
    private const int FromSeats = 6;

    /// <summary>Metres towards the middle of the table, and up. Enough to clear a bottle.</summary>
    private const float Inward = 0.15f;
    private const float Raise = 0.08f;

    private sealed class Home
    {
        public Transform T;
        public Vector3 Local;
        public Vector3 Offset;
    }

    /// <summary>
    /// Each body whose labels have been placed, and whether they were placed lifted - so a body
    /// first seen while the table still read small is lifted once the real count arrives.
    /// </summary>
    private static readonly Dictionary<int, bool> _bodies = new();
    private static readonly Dictionary<int, Home> _home = new();
    private static float _next;
    private static bool _said;

    internal static void Forget()
    {
        _bodies.Clear();
        _next = 0f;
        _said = false;
        _errors = 0;

        // Labels whose characters have gone with the last match are forgotten; any that are
        // still alive keep their starting point, so a move can never be applied on top of a move.
        if (_home.Count == 0) return;
        var dead = new List<int>();
        foreach (var kv in _home)
            if (kv.Value.T == null) dead.Add(kv.Key);
        foreach (var k in dead) _home.Remove(k);
    }

    internal static void Housekeep(float now)
    {
        if (!DiceTable.IsDice || now < _next) return;

        // Fewer than two is a count that has not arrived yet, not a small table: a client reads
        // nothing until the synced count does. Ask again soon rather than settle on it.
        int seats = DiceTable.Seats;
        if (seats < 2) { _next = now + 1f; return; }

        bool lift = seats >= FromSeats;

        // A table that has never been lifted is not even looked at: four players see exactly
        // what the game ships. Deciding that is free, so it is decided again each second - the
        // host can read a roster that is still filling before the synced count is set.
        if (!lift && _home.Count == 0) { _next = now + 1f; return; }

        try
        {
            var bodies = UnityEngine.Object.FindObjectsOfType<DiceGamePlay>();
            int seen = 0, moved = 0;

            for (int i = 0; bodies != null && i < bodies.Count; i++)
            {
                var gp = bodies[i];
                if (gp == null) continue;
                seen++;

                int id = gp.GetInstanceID();
                if (_bodies.TryGetValue(id, out bool lifted) && lifted == lift) continue;

                var root = gp.transform.root;
                moved += Place(gp.showText, root, lift);
                moved += Place(gp.ZarText, root, lift);
                _bodies[id] = lift;
            }

            if (moved > 0 && lift && !_said)
            {
                _said = true;
                Plugin.Log.LogInfo(
                    $"[dicetable] {seats} seats: dice labels lifted {Raise * 100f:F0}cm and moved " +
                    $"{Inward * 100f:F0}cm towards the middle, clear of the neighbour's hand and bottles");
            }

            // Bodies arrive over a few seconds, and on a client the bots never arrive at all, so
            // keep looking for a while; once everybody is here, now and then, in case a body is
            // replaced.
            _next = now + (seen >= seats ? 5f : 2f);
        }
        catch (Exception e)
        {
            _next = now + 5f;
            if (++_errors <= 3) Plugin.Log.LogWarning($"[dicetable] could not place the dice labels: {e.Message}");
        }
    }

    private static int _errors;

    /// <summary>
    /// Put one label where it belongs for this table size. Returns 1 if it was touched.
    ///
    /// The offset is worked out in the character's frame and applied to the label's own
    /// position. Those are the same directions: in every Liar's Dice character the label's
    /// parent sits square to the character, unrotated and unscaled.
    /// </summary>
    private static int Place(GameObject label, Transform root, bool lift)
    {
        if (label == null || root == null) return 0;
        var t = label.transform;
        int id = t.GetInstanceID();

        if (!_home.TryGetValue(id, out var home))
        {
            if (!lift) return 0;

            // Towards the middle of the table from where the label is: the table's centre is
            // straight ahead of every character, one seat radius away.
            Vector3 at = root.InverseTransformPoint(t.position);
            var inward = new Vector3(-at.x, 0f, DiceTable.RingRadius - at.z);
            if (inward.sqrMagnitude < 0.0001f) inward = Vector3.forward;
            inward.Normalize();

            home = new Home { T = t, Local = t.localPosition, Offset = inward * Inward + Vector3.up * Raise };
            _home[id] = home;
        }

        t.localPosition = lift ? home.Local + home.Offset : home.Local;
        return 1;
    }
}

/// <summary>
/// Moves a dead player's spectator cameras out from behind the extra seats.
///
/// When you are knocked out you watch from one of a handful of cameras the bar provides, and
/// switch between them. The close ones were placed on the diagonals between the four vanilla
/// seats, a metre or two behind the table, looking down at it. Those diagonals are exactly
/// where the seat ring puts the extra players: at eight seats the default view sits a metre
/// behind seat 5's head and another is within a degree of seat 1, so the view is the back of
/// somebody's head from close up. That is most likely what the reported "dice camera is
/// sometimes very close" was - it would depend on which seats are filled and which camera you
/// are on - though no screenshot has confirmed it yet.
///
/// So at more than four seats each close camera that sits in line with a seat is swung round
/// the table, about its centre, into the middle of the nearest gap between two seats. It keeps
/// its height, its distance and its aim at the table - it only changes which gap it looks
/// through. Cameras far across the bar are left alone: they were already behind somebody at
/// four seats, a seated player is a small part of their view, and swinging a camera seven
/// metres out round the table could put it inside a wall.
///
/// Every mode shares these cameras, so this is done in every mode, once per match, from where
/// each camera started - doing it again changes nothing. Four seats or fewer: untouched.
/// </summary>
internal static class SpectatorCams
{
    /// <summary>A camera this close to the table, in seat radii, counts as close.</summary>
    private const float CloseRadii = 3f;

    /// <summary>In line with a seat: within this fraction of the gap between two seats.</summary>
    private const float InLine = 0.35f;

    private sealed class Home
    {
        public Transform T;
        public Vector3 Pos;
        public Quaternion Rot;
    }

    private static readonly Dictionary<int, Home> _home = new();
    private static int _placedFor;
    private static int _failures;

    internal static void Forget()
    {
        _placedFor = 0;
        _failures = 0;

        if (_home.Count == 0) return;
        var dead = new List<int>();
        foreach (var kv in _home)
            if (kv.Value.T == null) dead.Add(kv.Key);
        foreach (var k in dead) _home.Remove(k);
    }

    internal static void Housekeep()
    {
        int seats = DiceTable.Seats;
        if (seats < 2 || seats == _placedFor || _failures >= 3) return;

        // Four or fewer and nothing ever moved: the cameras are the game's.
        bool move = seats > Limits.VanillaPlayers;
        if (!move && _home.Count == 0) { _placedFor = seats; return; }
        if (!DiceTable.HaveRing) return;

        try
        {
            var m = Manager.Instance;
            var parent = m != null ? m.SpectatorCameraParrent : null;
            if (parent == null) return;

            Place(parent.transform, seats, move);
            _placedFor = seats;
        }
        catch (Exception e)
        {
            _failures++;
            Plugin.Log.LogWarning($"[speccam] could not place the spectator cameras: {e.Message}");
        }
    }

    private static void Place(Transform parent, int seats, bool move)
    {
        float step = 360f / seats;
        Vector2 c = DiceTable.Centre;
        float close = CloseRadii * DiceTable.RingRadius;
        int moved = 0;

        // One line per camera is for working on this; normal play gets the summary alone.
        bool detail = Dev.Enabled || (Plugin.Verbose != null && Plugin.Verbose.Value);

        for (int i = 0; i < parent.childCount; i++)
        {
            var cam = parent.GetChild(i);
            if (cam == null) continue;

            int id = cam.GetInstanceID();
            if (!_home.TryGetValue(id, out var home))
            {
                if (!move) continue;
                home = new Home { T = cam, Pos = cam.position, Rot = cam.rotation };
                _home[id] = home;
            }

            // Always from where it started, so a second pass is the same as the first.
            cam.SetPositionAndRotation(home.Pos, home.Rot);
            if (!move) continue;

            float dx = home.Pos.x - c.x, dz = home.Pos.z - c.y;
            float r = Mathf.Sqrt(dx * dx + dz * dz);
            float bearing = Mathf.Atan2(dz, dx) * Mathf.Rad2Deg;

            int k = Mathf.RoundToInt((bearing - DiceTable.StartDeg) / step);
            float off = Mathf.DeltaAngle(DiceTable.StartDeg + k * step, bearing);
            int seat = ((k % seats) + seats) % seats;

            string verdict;
            if (r > close)
                verdict = "far from the table, left alone";
            else if (Mathf.Abs(off) >= step * InLine)
                verdict = "already between seats, left alone";
            else
            {
                // Into the middle of the gap on the side it already leans towards. In Unity a
                // positive turn about up lowers the bearing, hence the minus.
                float side = off >= 0f ? 1f : -1f;
                float turn = side * step * 0.5f - off;
                cam.RotateAround(new Vector3(c.x, home.Pos.y, c.y), Vector3.up, -turn);
                verdict = $"moved to {bearing + turn:F1}deg, between seats {seat} and {((seat + (int)side) % seats + seats) % seats}";
                moved++;
            }

            if (detail)
                Plugin.Log.LogInfo(
                    $"[speccam] '{cam.name}' {r:F2}m out at {bearing:F1}deg, {Mathf.Abs(off):F1}deg from seat {seat}: {verdict}");
        }

        Plugin.Log.LogInfo(
            move
                ? $"[speccam] {seats} seats: {moved} close spectator camera(s) moved out from behind a seat"
                : "[speccam] four seats or fewer: spectator cameras put back where the game had them");
    }
}

/// <summary>
/// Lets the reveal panel draw a called bid of more than twenty dice.
///
/// When somebody calls liar, the panel at the top of the screen lays the bid out as dice -
/// "twenty-five threes" is twenty-five little threes - and lights them up as each player's dice
/// are counted. It was built for four players, twenty dice at most: two rows of ten slots, and
/// the routine that fills them takes slot after slot from a list of twenty. A bid of twenty-one
/// asked for a slot that does not exist, threw part way through drawing, and left the panel
/// half drawn. The round itself went on.
///
/// The rows are plain positioned slots, so the layout extends cleanly: more rows of ten, copied
/// from the second row, each the same distance below the one before as the second is below the
/// first, filled in the same middle-outwards order the game uses. Up to forty dice, five for
/// each of eight players. The panel's backing strip is drawn for two rows, so the third and
/// fourth hang below it.
///
/// The game animates each of its twenty slots by name as the panel opens and as the result is
/// shown; the copies are not in those animations, so every frame each copy takes the size,
/// turn and place its twin in the second row has just been given, and each extra row follows
/// the second row as it slides out. The count of dice found, which those animations put just
/// below the second row - where the third row now is - is moved down by the extra rows in
/// use, after the animator has placed it, so it stays below the last row instead of over the
/// third. A bid past forty - only possible with more than eight players configured - draws
/// the first forty instead of throwing.
///
/// This acts only when a bid is larger than the slots the game made, which cannot happen at
/// four players. It is the routine's first step that is patched: the method that starts it was
/// compiled into the network handler that calls it, so it cannot be patched itself, but the
/// routine's steps are always called through Unity, and its first step reads the slot list.
/// </summary>
internal static class DiceRevealRows
{
    /// <summary>Five dice for each of eight players.</summary>
    internal const int MaxDrawn = 40;

    internal static bool On;

    private sealed class Rows
    {
        public int PanelId;
        public int Base;
        public bool Broken;
        public GameObject Panel;
        public RectTransform A, B;
        public readonly List<RectTransform> Extra = new();
        public readonly List<int> ExtraIds = new();
        public readonly List<RectTransform> Copy = new();
        public readonly List<RectTransform> Twin = new();
        public readonly List<GameObject> TwinGo = new();
        public int PerRow;
        public int Showing;
        public int RowsInUse;
        public bool WasOpen;
        public float ClosedAt = -1f;

        /// <summary>
        /// The found-count text, and its height as the animator last placed it and as this
        /// last wrote it - so a frame the animator does not write it is not moved twice.
        /// </summary>
        public RectTransform Found;
        public bool FoundMoved;
        public float FoundBase, FoundWritten;
    }

    private static Rows _rows;
    private static int _clamped;
    private static int _failures;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(DicePanel._SpawnDiceCoroutine_d__29), nameof(DicePanel._SpawnDiceCoroutine_d__29.MoveNext))]
    private static void BeforeFirstStep(DicePanel._SpawnDiceCoroutine_d__29 __instance)
    {
        try
        {
            if (__instance == null || __instance.__1__state != 0) return;

            var panel = __instance.__4__this;
            var slots = panel != null ? panel.dices : null;
            if (slots == null) return;

            int want = __instance.diceCount;
            var rows = For(panel, slots.Count);

            if (want <= rows.Base)
            {
                // A bid the game's own slots hold. The copies are reset with the rest by the
                // game, and nothing of this runs.
                rows.Showing = 0;
                rows.RowsInUse = 0;
                Stop();
                return;
            }

            if (want > slots.Count && !rows.Broken)
                Extend(rows, panel, slots, Mathf.Min(want, MaxDrawn));

            int have = slots.Count;
            rows.Showing = Mathf.Clamp(Mathf.Min(want, have) - rows.Base, 0, rows.Copy.Count);
            rows.RowsInUse = rows.PerRow > 0
                ? Math.Min((rows.Showing + rows.PerRow - 1) / rows.PerRow, rows.Extra.Count)
                : 0;
            rows.WasOpen = false;
            rows.ClosedAt = -1f;
            if (rows.Showing > 0) On = true;
            else Stop();

            if (want > have)
            {
                // Drawn short rather than not at all: the routine would otherwise throw at the
                // first missing slot, exactly as it did at twenty-one.
                __instance.diceCount = have;
                if (++_clamped <= 3)
                    Plugin.Log.LogWarning(
                        $"[dicerows] a bid of {want}: the reveal panel draws at most {have} dice, " +
                        $"so it shows {have}; the bid itself is unchanged");
            }
        }
        catch (Exception e)
        {
            if (++_failures <= 3) Plugin.Log.LogError($"[dicerows] {e.Message}");
        }
    }

    /// <summary>The bookkeeping for this panel - one per bar, so a new bar starts afresh.</summary>
    private static Rows For(DicePanel panel, int count)
    {
        int id = panel.GetInstanceID();
        if (_rows == null || _rows.PanelId != id)
            _rows = new Rows { PanelId = id, Base = count };
        return _rows;
    }

    /// <summary>
    /// Add rows until the list holds <paramref name="target"/> slots. Whole rows, copied from the
    /// game's second row, appended to the list in the second row's own order - so a bid of
    /// twenty-five fills the third row from the middle out, the way the game fills the second.
    /// </summary>
    private static void Extend(Rows rows, DicePanel panel,
                               Il2CppSystem.Collections.Generic.List<GameObject> slots, int target)
    {
        try
        {
            if (rows.Base < 2) { rows.Broken = true; return; }

            var first = slots[0];
            var last = slots[rows.Base - 1];
            var a = first != null ? first.transform.parent : null;
            var b = last != null ? last.transform.parent : null;
            var host = b != null ? b.parent : null;

            rows.A = a != null ? a.TryCast<RectTransform>() : null;
            rows.B = b != null ? b.TryCast<RectTransform>() : null;

            if (rows.A == null || rows.B == null || host == null || a.GetInstanceID() == b.GetInstanceID())
            {
                rows.Broken = true;
                Plugin.Log.LogWarning("[dicerows] the reveal panel is not laid out in rows as expected - " +
                                      "bids past its slots will be drawn short instead");
                return;
            }

            rows.Panel = host.gameObject;

            // The game's second row, in the order the game fills it.
            int bId = b.GetInstanceID();
            var order = new List<GameObject>();
            for (int i = 0; i < rows.Base; i++)
            {
                var g = slots[i];
                if (g == null) continue;
                var p = g.transform.parent;
                if (p != null && p.GetInstanceID() == bId) order.Add(g);
            }
            if (order.Count == 0) { rows.Broken = true; return; }

            rows.PerRow = order.Count;
            if (rows.Found is null) rows.Found = FoundText(panel, host);

            int before = slots.Count;
            Vector2 aPos = rows.A.anchoredPosition, bPos = rows.B.anchoredPosition;

            while (slots.Count < target)
            {
                int k = rows.Extra.Count + 1;

                var copy = UnityEngine.Object.Instantiate(b.gameObject, host, false);
                copy.name = $"View {2 + k}";
                copy.transform.SetSiblingIndex(b.GetSiblingIndex() + k);

                var row = copy.GetComponent<RectTransform>();

                // Find every slot before adding any, so a copy that is not what was expected is
                // thrown away whole rather than half used.
                var found = new List<Transform>();
                foreach (var twin in order)
                {
                    var c = copy.transform.Find(twin.name);
                    if (c == null) break;
                    found.Add(c);
                }

                if (row == null || found.Count != order.Count)
                {
                    UnityEngine.Object.Destroy(copy);
                    rows.Broken = true;
                    Plugin.Log.LogWarning("[dicerows] could not copy a row of the reveal panel - " +
                                          "bids past its slots will be drawn short instead");
                    return;
                }

                row.anchoredPosition = bPos + (bPos - aPos) * k;
                rows.Extra.Add(row);
                rows.ExtraIds.Add(copy.transform.GetInstanceID());

                for (int j = 0; j < found.Count && slots.Count < MaxDrawn; j++)
                {
                    slots.Add(found[j].gameObject);
                    rows.Copy.Add(found[j].TryCast<RectTransform>());
                    rows.Twin.Add(order[j].transform.TryCast<RectTransform>());
                    rows.TwinGo.Add(order[j]);
                }

                if (slots.Count >= MaxDrawn) break;
            }

            Plugin.Log.LogInfo(
                $"[dicerows] the reveal panel has {rows.Base} slots and a bid needs more: added " +
                $"{rows.Extra.Count} row(s) of {order.Count} below the game's two, {slots.Count} now " +
                $"(was {before})");
        }
        catch (Exception e)
        {
            rows.Broken = true;
            Plugin.Log.LogError($"[dicerows] extending the reveal panel failed: {e.Message}");
        }
    }

    /// <summary>
    /// The text that counts the dice found, if it sits beside the rows - the only place an
    /// offset measured in rows means anything. Null otherwise, and the text is left alone.
    /// </summary>
    private static RectTransform FoundText(DicePanel panel, Transform host)
    {
        try
        {
            var text = panel != null ? panel.totalFindText : null;
            var t = text != null ? text.transform : null;
            var p = t != null ? t.parent : null;
            if (p == null || p.GetInstanceID() != host.GetInstanceID()) return null;
            return t.TryCast<RectTransform>();
        }
        catch { return null; }
    }

    /// <summary>
    /// Stop following the reveal, and leave the count text where the game last put it. Safe to
    /// call at any time, including after the panel has gone with its scene.
    /// </summary>
    internal static void Stop()
    {
        On = false;
        PutBackFound(_rows);
    }

    /// <summary>
    /// Every frame while a reveal is using the extra rows: keep each row the same step below the
    /// one above as the second row is below the first (the second slides out as the panel
    /// opens), give each copy its twin's animated size, turn and place, and keep the count text
    /// below the last row in use. A copy whose twin is hidden is hidden too. Stops by itself
    /// once the panel has opened and closed again.
    ///
    /// Field arithmetic rather than Unity's vector operators, which in this build are calls into
    /// the game's runtime; nothing here is created on the managed side. The clock is only read
    /// once the panel has closed.
    /// </summary>
    internal static void Follow()
    {
        var r = _rows;
        try
        {
            if (r == null || r.Panel == null || r.A == null || r.B == null) { Stop(); return; }

            // Closed for good once it has been open and then stayed shut a moment - not on a
            // single hidden frame, in case an animation blinks it.
            if (!r.Panel.activeInHierarchy)
            {
                if (r.WasOpen)
                {
                    float now = Time.time;
                    if (r.ClosedAt < 0f) r.ClosedAt = now;
                    else if (now - r.ClosedAt > 1.5f) { r.WasOpen = false; Stop(); }
                }
                return;
            }
            r.WasOpen = true;
            r.ClosedAt = -1f;

            Vector2 a = r.A.anchoredPosition, b = r.B.anchoredPosition;
            float sx = b.x - a.x, sy = b.y - a.y;
            for (int i = 0; i < r.Extra.Count; i++)
            {
                var at = default(Vector2);
                at.x = b.x + sx * (i + 1);
                at.y = b.y + sy * (i + 1);
                r.Extra[i].anchoredPosition = at;
            }

            for (int j = 0; j < r.Showing; j++)
            {
                var copy = r.Copy[j];
                if (!r.TwinGo[j].activeSelf) { copy.localScale = default(Vector3); continue; }

                var twin = r.Twin[j];
                copy.localScale = twin.localScale;
                copy.localRotation = twin.localRotation;
                copy.anchoredPosition = twin.anchoredPosition;
            }

            KeepFoundBelow(r, sy);
        }
        catch (Exception e)
        {
            Stop();
            if (++_failures <= 3) Plugin.Log.LogWarning($"[dicerows] could not keep the extra rows in step: {e.Message}");
        }
    }

    /// <summary>
    /// Move the count text down by the extra rows in use, from where the animator has just put
    /// it. Every reveal animation places this text each frame it plays, so the offset is put
    /// on a fresh value and never builds up - and on a frame it was not placed (it still reads
    /// what was written here last time), the height the animator last gave it is used instead
    /// of moving it twice.
    /// </summary>
    private static void KeepFoundBelow(Rows r, float rowStep)
    {
        var t = r.Found;
        if (t is null || r.RowsInUse <= 0) return;

        var p = t.anchoredPosition;
        float placed = r.FoundMoved && p.y == r.FoundWritten ? r.FoundBase : p.y;
        p.y = placed + rowStep * r.RowsInUse;
        t.anchoredPosition = p;

        r.FoundBase = placed;
        r.FoundWritten = p.y;
        r.FoundMoved = true;
    }

    /// <summary>
    /// Put the count text back at the height the animator last gave it - unless the animator
    /// has placed it again since, in which case it is already the game's.
    /// </summary>
    private static void PutBackFound(Rows r)
    {
        if (r == null || !r.FoundMoved) return;
        r.FoundMoved = false;

        try
        {
            var t = r.Found;
            if (t == null) return;
            var p = t.anchoredPosition;
            if (p.y != r.FoundWritten) return;
            p.y = r.FoundBase;
            t.anchoredPosition = p;
        }
        catch { /* gone with its scene: nothing to put back */ }
    }

    /// <summary>Which extra row a slot belongs to: 1 for the third row, 2 for the fourth, else 0.</summary>
    internal static int ExtraRowOf(GameObject go)
    {
        var r = _rows;
        if (r == null || r.ExtraIds.Count == 0 || go == null) return 0;
        var p = go.transform.parent;
        if (p == null) return 0;
        int id = p.GetInstanceID();
        for (int i = 0; i < r.ExtraIds.Count; i++)
            if (r.ExtraIds[i] == id) return i + 1;
        return 0;
    }
}

/// <summary>
/// Keeps the reveal panel's extra rows in reading order.
///
/// The game re-sorts the dice it has drawn after each one, by row and then left to right, and
/// lights them up in that order as dice are counted - the whole first row, then the second.
/// It knows its rows by name: "View" is first, "View 2" second, and anything else last. The
/// third and fourth rows <see cref="DiceRevealRows"/> adds would therefore share "last", and a
/// count past twenty would light them alternately, a column at a time, instead of filling the
/// third row before the fourth. This gives them their own places after the second.
///
/// Kept apart from the rows themselves so that if this ever fails to attach, the rows still
/// work and only the lighting order suffers.
/// </summary>
internal static class DiceRowOrder
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(DicePanel.__c), nameof(DicePanel.__c._SortSpawnedDiceVisualAndList_b__32_0))]
    private static void RowOf(GameObject __0, ref int __result)
    {
        try
        {
            if (__result != 2) return;
            int extra = DiceRevealRows.ExtraRowOf(__0);
            if (extra > 0) __result = 1 + extra;
        }
        catch { /* the game's own order is a fine fallback */ }
    }
}
