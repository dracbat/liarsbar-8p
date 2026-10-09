using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Makes the test harness actually choose somebody, with the keys a player would press.
///
/// A chaos card stops the round and hands whoever threw it a revolver and a choice. Early runs
/// let that choice time out: the harness knew how to throw cards and call liar and nothing
/// else, so the aiming phase opened, sat there, and closed itself. The logs said "chaos aim
/// resolved", which is true and says nothing - it resolved the way it resolves when nobody is
/// playing.
///
/// The next version drove it, and drove it the wrong way. It moved the aim by calling
/// <c>LeftAim</c> and <c>RightAim</c>, believing those were the methods the keys call, and
/// fired with <c>TryFire</c>. In the Chaos deck neither is true: the game's build compiled the
/// key handling straight into <c>UpdateCall</c>, nothing calls those two methods, and nothing
/// calls <c>TryFire</c> either - the shot goes off when the countdown runs out. So the harness
/// exercised a path no player can reach, reported that every seat could aim at every other at
/// eight players, and a player at seven found they could reach only three people. An
/// instrument that goes round the input path cannot see a bug in the input path.
///
/// So in the Chaos deck this now presses keys. A press is handed to <see cref="AimKeys"/>,
/// which picks it up in the seat's next <c>UpdateCall</c> at exactly the point where a real
/// key is read, and from there it goes through every line a keypress goes through - the same
/// step, the same skipping of the dead, the same message to the server. One press at a time,
/// looking at where the aim landed before pressing again, until the aim is on the person
/// chosen. Then nothing: the countdown fires the shot, as it does for a player.
///
/// Each machine presses the keys of the seats it would press them for in a real game - its own
/// player, and on the host the bots, which have no other machine. A loopback client therefore
/// walks its own aim and sends it to the host the way a player's game does, and the host sees
/// only the result. When the shot is resolved the server writes down what it is about to fire
/// at, next to what the keys chose wherever it knows, and whether the chamber was live - an
/// empty chamber kills nobody however well aimed, and that is the game, not a miss.
///
/// The target rotates by seat and by round so that a match works its way round the whole
/// table rather than shooting the same neighbour every time.
///
/// Liar's Poker is still driven through <c>LeftAim</c> / <c>RightAim</c>, because there those
/// really are what the keys call. This is a test instrument, and it does nothing at all
/// outside developer mode.
/// </summary>
internal static class AimDrive
{
    /// <summary>Long enough for the aiming phase to have finished opening.</summary>
    private const float ThinkSeconds = 1.6f;

    /// <summary>How long a press may wait to be picked up before the key path is called broken.</summary>
    private const float PressTimeoutSeconds = 1.5f;

    /// <summary>Which aiming phase each seat has already acted in (Liar's Poker and the Chaos mode).</summary>
    private static readonly Dictionary<int, int> _acted = new();
    private static readonly Dictionary<int, float> _actAt = new();

    private static int _phase;
    private static bool _wasAiming;

    /// <summary>A Chaos deck seat's walk to its target, one key press per tick.</summary>
    private sealed class Walk
    {
        internal int Phase;
        internal float Due;
        internal int Want = -1;
        internal int Presses;
        internal float PressedAt;
        internal bool Done;
    }

    private static readonly Dictionary<int, Walk> _walks = new();

    /// <summary>The seat each seat's keys settled on, for checking against the shot.</summary>
    private static readonly Dictionary<int, int> _chose = new();

    private static bool _clientSeatsReported;

    internal static void RoundStarting()
    {
        _acted.Clear();
        _actAt.Clear();
        _walks.Clear();
        _chose.Clear();
        AimKeys.ClearInjected();
    }

    internal static void Tick()
    {
        if (!Dev.Enabled) return;

        CheckKeysPatched();

        var m = Dev.Mgr;
        if (m == null || !m.GameStarted)
        {
            _wasAiming = false;
            _probed = null;
            _walks.Clear();
            _own = null;
            AimKeys.ClearInjected();
            return;
        }

        int n = AimRing.Seats(m);
        if (n < 2) return;

        // The measurement runs on every machine, not only the server.
        //
        // Which seat an aim points at is worked out separately by every peer - the shooter
        // computes it to send the shot, the server computes it again to resolve it - so the
        // question is not only "can a seat reach everybody" but "does every machine agree
        // about who it reached". That distinction has already cost this project once: the
        // turn order's neighbour table read a roster that is empty on a client, so the host
        // aimed at seat 4 while every client watched the same player aim at seat 0, and
        // nothing on the host could have shown it. Asking only the host would be making the
        // same mistake in a different place.
        Probe(m, n);

        // The host drives while the harness stands in for everybody; a loopback client drives
        // its own seat, because its own keys are the ones that seat answers to.
        bool host = Dev.IsServer && (DevAutoTest.Driving || Loopback.Mine == Loopback.Role.Host);
        bool client = !Dev.IsServer && Loopback.Mine == Loopback.Role.Client;
        if (!host && !client) return;

        var seats = host ? Roster(m) : OwnSeat();
        if (seats.Count == 0) return;

        // A new phase begins when somebody starts aiming and nobody was. Counting phases is
        // what lets the choice move round the table between chaos cards instead of every
        // player shooting the same neighbour all match.
        bool anyAiming = false;
        foreach (var (p, cc) in seats)
            if (AimRing.Choosing(cc)) { anyAiming = true; break; }

        bool ended = !anyAiming && _wasAiming;
        if (anyAiming && !_wasAiming) { _phase++; _actAt.Clear(); }
        _wasAiming = anyAiming;
        if (!anyAiming)
        {
            // Nobody is choosing any more, so a press still waiting would only be picked up
            // at the start of the next choice - by then it is nobody's press.
            if (ended) AimKeys.ClearInjected();
            return;
        }

        foreach (var (p, cc) in seats)
        {
            var deck = cc.TryCast<ChaosDeckGameplay>();
            if (deck != null)
            {
                try { DriveDeck(m, p, deck, n, host); }
                catch (Exception e) { Dev.Warn("aim", $"{Name(p, p.Slot)} could not aim: {e.Message}"); }
                continue;
            }

            // Liar's Poker and the Chaos mode, the way they were driven before: from the host,
            // for every seat at once.
            if (host) DriveByMethod(m, p, cc, n);
        }
    }

    /// <summary>Every seated player and their aiming component - the server's roster.</summary>
    private static List<(PlayerStats, CharController)> Roster(Manager m)
    {
        var found = new List<(PlayerStats, CharController)>();
        if (m.Players == null) return found;
        foreach (var p in m.Players)
        {
            if (p == null || p.Dead) continue;
            var cc = Play(p);
            if (cc != null) found.Add((p, cc));
        }
        return found;
    }

    /// <summary>How long a client waits before looking in the scene again for a seat it did not find.</summary>
    private const float OwnLookupSeconds = 2f;

    private static ChaosDeckGameplay _own;
    private static float _ownAt = -999f;

    /// <summary>
    /// The seat this copy of the game plays, found in the scene - a client's copy of the
    /// roster is empty, so it cannot be looked up there.
    ///
    /// Kept from one tick to the next rather than searched for on every one: this is asked
    /// four times a second for the whole of a match, and the scene search is the expensive
    /// part. Searched for again once the object found has gone - between rounds the player
    /// object is replaced, and a destroyed one is still a non-null reference while being null
    /// to Unity - and, while there is none, no more than every couple of seconds.
    /// </summary>
    private static List<(PlayerStats, CharController)> OwnSeat()
    {
        var found = new List<(PlayerStats, CharController)>();
        try
        {
            if (ReferenceEquals(_own, null) || _own == null)
            {
                _own = null;
                if (Time.time - _ownAt < OwnLookupSeconds) return found;
                _ownAt = Time.time;

                var all = UnityEngine.Object.FindObjectsOfType<ChaosDeckGameplay>();
                if (all == null) return found;
                for (int i = 0; i < all.Count; i++)
                {
                    var d = all[i];
                    if (d == null || !d.isOwned) continue;
                    _own = d;
                    break;
                }
                if (_own == null) return found;
            }

            var p = _own.playerStats;
            if (p == null || p.Dead) return found;
            found.Add((p, _own));
        }
        catch { _own = null; }
        return found;
    }

    // ------------------------------------------------------------- the Chaos deck, by key

    /// <summary>
    /// One step of a seat's walk to its target: wait for the phase to open, choose somebody,
    /// press once, look where the aim landed, and press again next time round - until it is
    /// on them, or it is clear the keys cannot get it there.
    /// </summary>
    private static void DriveDeck(Manager m, PlayerStats p, ChaosDeckGameplay deck, int n, bool host)
    {
        int me = p.Slot;

        // A press the seat never got to pick up is taken back wherever the walk stops, or it
        // would be made on the first frame of the seat's next choice.
        if (!deck.TakingAim) { _walks.Remove(me); AimKeys.Cancel(deck); return; }

        // Only the seats this machine would press keys for in a real game.
        if (!deck.isOwned)
        {
            bool bot = host && deck.connectionToClient == null;
            if (!bot)
            {
                if (!_clientSeatsReported)
                {
                    _clientSeatsReported = true;
                    Plugin.Log.LogWarning("[aim] seats played by a loopback client are aimed by that client's " +
                                          "own copy of the game, through its own keys - not from here");
                }
                return;
            }
        }

        if (!_walks.TryGetValue(me, out var w) || w.Phase != _phase)
        {
            _walks[me] = new Walk { Phase = _phase, Due = Time.time + ThinkSeconds };
            return;
        }
        if (w.Done || Time.time < w.Due) return;

        if (deck.AimLocked)
        {
            w.Done = true;
            AimKeys.Cancel(deck);
            if (w.Want < 0)
                Plugin.Log.LogWarning($"[aim] {Name(p, me)} has a forced shot - nothing to choose");
            else
                Plugin.Log.LogError($"[aim] {Name(p, me)}'s aim locked before the keys reached seat {w.Want} " +
                                    $"(aim {deck.Aim}, {w.Presses} press(es))");
            return;
        }

        if (n <= Limits.VanillaPlayers)
        {
            // At four the shipped keys are in charge, and they cannot be pressed from here -
            // they are compiled into the game. The shipped range already reaches all three.
            w.Done = true;
            Plugin.Log.LogWarning($"[aim] {Name(p, me)} is aiming at a table of {n}; the shipped keys are not " +
                                  "the mod's and cannot be pressed by the harness - left to the countdown");
            return;
        }

        if (AimKeys.Pending(deck))
        {
            if (Time.time - w.PressedAt < PressTimeoutSeconds) return;
            w.Done = true;
            AimKeys.Cancel(deck);
            Plugin.Log.LogError($"[aim] a key press for {Name(p, me)} was never picked up - the aim keys " +
                                "are not running (is AimKeys patched?)");
            return;
        }

        if (w.Want < 0 || m.GetTargetPlayer(w.Want, true) == null)
        {
            w.Want = PickTarget(m, me, n);
            if (w.Want < 0) { w.Done = true; return; }
        }

        int aim = deck.Aim;
        var at = Selected(deck);

        if (at != null && at.Slot == w.Want)
        {
            w.Done = true;
            _chose[me] = w.Want;
            Plugin.Log.LogWarning(
                $"[aim] {Name(p, me)} aims at {Name(at, at.Slot)} with {w.Presses} key press(es) - aim {aim} " +
                $"of a {n} seat ring; the countdown takes the shot");
            return;
        }

        // Every live seat is in the range and the step carries on round past either end, so
        // twice round the table is more than any target needs.
        if (w.Presses >= n * 2)
        {
            w.Done = true;
            Plugin.Log.LogError(
                $"[aim] {Name(p, me)} could not bring the aim round to seat {w.Want} with the keys - " +
                $"{w.Presses} presses, stuck at aim {aim} ({(at != null ? Name(at, at.Slot) : "nobody")})");
            return;
        }

        if (AimKeys.Inject(deck, Direction(me, n, at, aim, w.Want)))
        {
            w.Presses++;
            w.PressedAt = Time.time;
        }
    }

    /// <summary>
    /// Which key to press. Aim values run the opposite way round the ring from seat offsets,
    /// so a target further round than the current one is the left key - unless going the
    /// other way, round past the end, is shorter, which also gets that path exercised.
    /// </summary>
    private static int Direction(int me, int n, PlayerStats at, int aim, int want)
    {
        int atOffset = at != null ? (at.Slot - me + n) % n : AimRing.Offset(n, aim);
        int wantOffset = (want - me + n) % n;

        int plain = wantOffset > atOffset ? -1 : 1;
        int plainPresses = Math.Abs(wantOffset - atOffset);
        int roundPresses = (n - 1) - plainPresses;
        return roundPresses < plainPresses ? -plain : plain;
    }

    /// <summary>
    /// Who to shoot this phase. Seat and phase both feed in so that no two seats pick the
    /// same person in the same phase and no seat picks the same person twice running; when
    /// that chair is empty or out, anybody still in will do.
    /// </summary>
    private static int PickTarget(Manager m, int me, int n)
    {
        int offset = 1 + (Math.Abs(_phase + me) % Math.Max(1, n - 1));
        int want = (me + offset) % n;
        if (m.GetTargetPlayer(want, true) != null) return want;

        for (int d = 1; d < n; d++)
        {
            int s = (me + d) % n;
            if (m.GetTargetPlayer(s, true) != null) return s;
        }
        return -1;
    }

    /// <summary>
    /// On the server, just before a Chaos deck shot is resolved: what it is about to fire at,
    /// whether the chamber is live, and - for a seat whose keys this machine pressed - whether
    /// that is who the keys chose. Called from <see cref="AimKeys"/>.
    /// </summary>
    internal static void Resolving(ChaosDeckGameplay deck, int aim)
    {
        var ps = deck.playerStats;
        if (ps == null) return;
        int me = ps.Slot;

        int n = AimRing.Seats(Dev.Mgr);
        bool live = deck.currentrevoler == deck.revolverbulllet;
        int forced = deck.forcedAimTargetSlot;

        // The order the server asks in: whoever is in the chair, then whoever is still in.
        var target = AskGetAim(deck, aim, false) ?? AskGetAim(deck, aim, true);
        string got = target != null ? Name(target, target.Slot) + (target.Dead ? " (already dead)" : "") : "nobody";
        string chamber = live ? "live chamber" : "empty chamber - nobody dies whoever it is";

        if (!_chose.TryGetValue(me, out int want))
        {
            Plugin.Log.LogInfo(forced >= 0
                ? $"[aim] the server fires {Name(ps, me)}'s forced shot at seat {forced} ({chamber})"
                : $"[aim] the server fires {Name(ps, me)}'s aim {aim} at {got} ({chamber}, {n} seats)");
            return;
        }
        _chose.Remove(me);

        // Worded so tools/matrix-test.ps1 counts them as it counted the shots the harness used to
        // fire itself: "aims at ... and fires" for a shot that went where it was aimed, "meant
        // to shoot" for one that did not. Reworded, both columns would read zero - which looks
        // exactly like a clean run.
        if (forced >= 0)
            Plugin.Log.LogWarning($"[aim] {Name(ps, me)}'s shot is forced at seat {forced}; the keys' choice " +
                                  $"of seat {want} does not apply ({chamber})");
        else if (target != null && target.Slot == want)
            Plugin.Log.LogWarning($"[aim] {Name(ps, me)} aims at {got} with the keys and fires - the server " +
                                  $"fires at the seat the keys chose ({chamber}, aim {aim}, {n} seats)");
        else
            Plugin.Log.LogError($"[aim] {Name(ps, me)} meant to shoot seat {want} with the keys but the server " +
                                $"fires at {got} ({chamber}, aim {aim}, {n} seats)");
    }

    // -------------------------------------------------------------- is the key path there

    private static bool _patchChecked;

    /// <summary>
    /// Say, once, whether the Chaos deck's key handling is actually patched. Without it the
    /// harness's presses are never picked up and a player at five or more can reach only three
    /// people - which is the 1.1.1 bug - so it is worth one line in every developer log.
    /// </summary>
    private static void CheckKeysPatched()
    {
        if (_patchChecked) return;
        _patchChecked = true;

        try
        {
            var original = AccessTools.DeclaredMethod(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.UpdateCall));
            var info = original != null ? Harmony.GetPatchInfo(original) : null;

            bool ours = false;
            if (info != null)
                foreach (var patch in info.Prefixes)
                    if (patch.owner == Plugin.Guid && patch.PatchMethod.DeclaringType == typeof(AimKeys)) ours = true;

            if (ours)
                Plugin.Log.LogInfo("[aim] the Chaos deck aim keys are patched (ChaosDeckGameplay.UpdateCall)");
            else
                Plugin.Log.LogError("[aim] the Chaos deck aim keys are NOT patched - at five or more players only " +
                                    "three seats can be chosen, and every harness key press will go unanswered");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[aim] could not check the aim key patch: {e.Message}"); }
    }

    // -------------------------------------------------------------- measuring the whole ring

    /// <summary>Let the round settle before asking it anything.</summary>
    private const float ProbeAfterSeconds = 14f;

    private static Manager _probed;
    private static float _probeAt;

    /// <summary>
    /// Ask, once a match, whether every seat can point at every other seat - and whether the
    /// keys can get it there.
    ///
    /// This exists because the end-to-end test cannot be relied on to happen. Reaching the
    /// aiming phase needs a chaos card to be dealt into somebody's hand and then thrown before
    /// the round ends, and a hundred and fifty second run at five players produced exactly
    /// none - so the run reported nothing about aiming and looked no different from a run
    /// where aiming worked perfectly. A mechanic that is only tested when the deck feels like
    /// it is not tested.
    ///
    /// So this asks two questions directly rather than waiting to be given the opportunity.
    /// First, for every seat it sweeps a wider range of aim values than the game allows and
    /// writes down which players the game says each one points at - using the game's own
    /// <c>GetAim</c>, not the replacement arithmetic, so that what is measured is the answer a
    /// shot would actually resolve to rather than a restatement of the fix. Second, it starts
    /// from the opening aim and steps left and right the way the keys do, asking the game who
    /// each stop points at: the first question passing while the second fails is exactly what
    /// 1.1.1 shipped, where every aim value meant somebody and the keys could only produce
    /// three of them. For the Chaos deck it also says how many the game's own keys would have
    /// reached on their own, so the size of the gap is in the log.
    ///
    /// A seat that cannot reach somebody is the whole bug, in one line, in every run.
    /// </summary>
    private static void Probe(Manager m, int n)
    {
        if (ReferenceEquals(_probed, m)) return;

        if (_probeAt <= 0f || !ReferenceEquals(_probeFor, m))
        {
            _probeFor = m;
            _probeAt = Time.time + ProbeAfterSeconds;
            return;
        }
        if (Time.time < _probeAt) return;

        // Found in the scene rather than in Manager.Players, because Players is the server's
        // own roster and is empty on a client until something happens to refresh it - and a
        // probe that quietly measured nothing on every machine but the host would report
        // agreement it had never checked.
        var seated = new List<PlayerStats>();
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<PlayerStats>();
            if (all != null)
                for (int i = 0; i < all.Count; i++)
                    if (all[i] != null) seated.Add(all[i]);
        }
        catch { return; }

        // The seats that are actually in - the same filter the game uses when it decides
        // whether an aim points at anybody.
        var live = new List<int>();
        foreach (var p in seated)
            if (p != null && !p.Dead && !p.Fnished) live.Add(p.Slot);

        if (live.Count < 2) return;

        bool asked = false;
        int bad = 0;
        string where = Dev.IsServer ? "host" : "this client";

        foreach (var p in seated)
        {
            if (p == null || p.Dead || p.Fnished) continue;

            var cc = Play(p);
            if (cc == null) continue;

            var reached = new List<int>();
            var seen = new HashSet<int>();

            // Deliberately wider than the range the aim is allowed to take. Sweeping only the
            // permitted range would report a pass whenever the range and the table agreed with
            // each other, including when both were four.
            for (int a = -n - 1; a <= n + 1; a++)
            {
                var t = AskGetAim(cc, a, true);
                if (t == null) continue;
                if (seen.Add(t.Slot)) reached.Add(t.Slot);
            }

            asked = true;
            reached.Sort();

            var missed = new List<int>();
            foreach (int s in live)
                if (s != p.Slot && !seen.Contains(s)) missed.Add(s);

            string who = string.IsNullOrEmpty(p.PlayerName) ? $"seat {p.Slot + 1}" : p.PlayerName;

            bool seatBad = missed.Count != 0;
            if (!seatBad)
                Plugin.Log.LogWarning(
                    $"[aimring] on {where}, {who} (seat {p.Slot}) can aim at all {live.Count - 1} of the " +
                    $"others - seats {string.Join(", ", reached)}");
            else
                Plugin.Log.LogError(
                    $"[aimring] on {where}, {who} (seat {p.Slot}) can aim at only {reached.Count} of " +
                    $"{live.Count - 1} - never {string.Join(", ", missed)} - reaches {string.Join(", ", reached)}");

            // A seat fails if either question fails for it. Counted once per seat, because the
            // summary below says how many seats - and tools/matrix-test.ps1 reads its "seat(s)"
            // wording to fill the aim ring column, which went blank when it said "check(s)".
            if (!ProbeKeys(cc, p, who, where, live)) seatBad = true;
            if (seatBad) bad++;
        }

        if (!asked) return;

        _probed = m;
        if (bad == 0)
            Plugin.Log.LogWarning($"[aimring] on {where}, every seat at this table of {n} can point at every other");
        else
            Plugin.Log.LogError($"[aimring] on {where}, {bad} seat(s) at this table of {n} cannot reach the whole table");
    }

    private static Manager _probeFor;

    /// <summary>
    /// The second question: from the opening aim, which players do the keys' steps reach?
    /// Only above four - at four the shipped keys are the ones in charge. False when somebody
    /// still in cannot be reached.
    /// </summary>
    private static bool ProbeKeys(CharController cc, PlayerStats p, string who, string where, List<int> live)
    {
        // The Chaos mode's keys are compiled into its UpdateCall and nothing of the mod's is
        // on them, so stepping its aim here would claim a reach no player has.
        if (cc.TryCast<ChaosGamePlay>() != null) return true;
        if (!AimRing.Who(cc, out var m, out int me, out int n)) return true;

        var seen = new HashSet<int>();
        int start = AimRing.First(m, me, n);
        Note(cc, start, seen);

        for (int dir = -1; dir <= 1; dir += 2)
        {
            int a = start;
            for (int i = 0; i < n && AimRing.Step(m, me, n, a, dir, false, out int next); i++)
            {
                a = next;
                Note(cc, a, seen);
            }
        }

        var missed = new List<int>();
        foreach (int s in live)
            if (s != me && !seen.Contains(s)) missed.Add(s);

        // The game's own Chaos deck keys, for comparison: 0 -> 1 and -1 -> 0 one way,
        // 1 -> 0 and 0 -> -1 the other, nothing from anywhere else.
        string shipped = "";
        if (cc.TryCast<ChaosDeckGameplay>() != null)
        {
            var own = new HashSet<int>();
            Note(cc, start, own);
            if (start >= -1 && start <= 1)
                for (int a = -1; a <= 1; a++) Note(cc, a, own);
            shipped = $" (the game's own keys would reach {own.Count})";
        }

        if (missed.Count == 0)
        {
            Plugin.Log.LogWarning(
                $"[aimring] on {where}, {who} (seat {me}) can reach all {live.Count - 1} with the keys{shipped}");
            return true;
        }

        Plugin.Log.LogError(
            $"[aimring] on {where}, {who} (seat {me}) can reach only {seen.Count} of {live.Count - 1} with the " +
            $"keys - never {string.Join(", ", missed)}{shipped}");
        return false;
    }

    private static void Note(CharController cc, int aim, HashSet<int> seen)
    {
        var t = AskGetAim(cc, aim, true);
        if (t != null && !t.Dead && !t.Fnished) seen.Add(t.Slot);
    }

    private static readonly Dictionary<string, MethodInfo> _getAim = new();

    /// <summary>
    /// What the game says an aim points at.
    ///
    /// <c>GetAim</c> is private in all three modes, so it is called by reflection. Asking the
    /// game rather than computing the answer here is the point: a probe that used the mod's
    /// own arithmetic would agree with the mod whether or not the mod was right.
    /// </summary>
    private static PlayerStats AskGetAim(CharController cc, int aim, bool excludeFinished)
    {
        try
        {
            Type owner = null;
            Type[] args = null;

            if (cc.TryCast<ChaosDeckGameplay>() != null)
            {
                owner = typeof(ChaosDeckGameplay);
                args = new[] { typeof(int), typeof(bool) };
            }
            else if (cc.TryCast<ChaosGamePlay>() != null) owner = typeof(ChaosGamePlay);
            else if (cc.TryCast<PokerGamePlay>() != null) owner = typeof(PokerGamePlay);
            else return null;

            if (args == null) args = new[] { typeof(int) };

            string id = owner.Name + "/" + args.Length;
            if (!_getAim.TryGetValue(id, out var mi))
            {
                mi = AccessTools.Method(owner, "GetAim", args);
                _getAim[id] = mi;
            }
            if (mi == null) return null;

            object[] call = args.Length == 2 ? new object[] { aim, excludeFinished } : new object[] { aim };
            return mi.Invoke(cc, call) as PlayerStats;
        }
        catch { return null; }
    }

    /// <summary>The aiming component this player is using, if the mode they are in has one.</summary>
    private static CharController Play(PlayerStats p)
    {
        try
        {
            var deck = p.GetComponent<ChaosDeckGameplay>();
            if (deck != null) return deck;

            var chaos = p.GetComponent<ChaosGamePlay>();
            if (chaos != null) return chaos;

            var poker = p.GetComponent<PokerGamePlay>();
            if (poker != null) return poker;
        }
        catch { }
        return null;
    }

    // ------------------------------------------------- Liar's Poker and the Chaos mode, by method

    /// <summary>
    /// Liar's Poker and the standalone Chaos mode: pick somebody, walk the aim to them through
    /// <c>LeftAim</c> / <c>RightAim</c>, and fire - from the host, for each seat once a phase.
    ///
    /// In Liar's Poker those two methods are what the A and D keys call, so this is the key
    /// path there. The Chaos mode is not reachable by players, and its keys are compiled into
    /// its <c>UpdateCall</c> like the Chaos deck's, so for it this proves less than it looks
    /// like; it is kept as it was until that mode can be played.
    /// </summary>
    private static void DriveByMethod(Manager m, PlayerStats p, CharController cc, int n)
    {
        if (!AimRing.Choosing(cc)) { _actAt.Remove(p.Slot); return; }
        if (_acted.TryGetValue(p.Slot, out int at) && at == _phase) return;

        if (!_actAt.TryGetValue(p.Slot, out float due))
        {
            _actAt[p.Slot] = Time.time + ThinkSeconds;
            return;
        }
        if (Time.time < due) return;

        _actAt.Remove(p.Slot);
        _acted[p.Slot] = _phase;

        try { Choose(m, p, cc, n); }
        catch (Exception e) { Dev.Warn("aim", $"{p.PlayerName} could not take the shot: {e.Message}"); }
    }

    /// <summary>
    /// Pick somebody, walk the aim to them a step at a time, and fire.
    ///
    /// The walk is capped at the number of seats: a step that lands nowhere new means the aim
    /// is already at the end of its range, and without the cap a target that cannot be reached
    /// would spin here forever.
    /// </summary>
    private static void Choose(Manager m, PlayerStats p, CharController cc, int n)
    {
        int me = p.Slot;

        int want = PickTarget(m, me, n);
        if (want < 0) return;
        var wanted = m.GetTargetPlayer(want, true);
        if (wanted == null) return;

        int landed = WalkByMethod(cc, me, want, n);

        var hit = Selected(cc);
        string meant = Name(wanted, want);
        string got = hit != null ? Name(hit, hit.Slot) : "nobody";

        if (hit != null && hit.Slot == want)
            Plugin.Log.LogWarning(
                $"[aim] {Name(p, me)} aims at {meant} - aim {landed} of a {n} seat ring - and fires");
        else if (m.GetTargetPlayer(want, true) == null)
            // The chosen player died while the aim was being walked to them. That is the game
            // working, not the ring failing - a round can kill somebody between one tick and
            // the next - and reporting it as a wrong answer put mismatches in cells whose own
            // ring probe had just said every seat could reach every other.
            Plugin.Log.LogWarning(
                $"[aim] {Name(p, me)} was aiming at {meant}, who went out before the shot - " +
                $"not fired (aim {landed}, {n} seats)");
        else
            Plugin.Log.LogError(
                $"[aim] {Name(p, me)} meant to shoot {meant} but the aim resolved to {got} " +
                $"(aim {landed}, {n} seats)");

        Fire(cc);
    }

    /// <summary>
    /// Move the aim onto a seat through <c>LeftAim</c> / <c>RightAim</c>.
    ///
    /// Which way to walk is decided by trying: the aim value that means a given seat depends
    /// on the ring size, and asking the game which direction to press rather than working it
    /// out here keeps this from being a second copy of the arithmetic under test.
    /// </summary>
    private static int WalkByMethod(CharController cc, int me, int want, int n)
    {
        int last = AimRing.CurrentAim(cc);

        for (int i = 0; i < n * 2 + 2; i++)
        {
            var at = Selected(cc);
            if (at != null && at.Slot == want) break;

            // Aim values run the opposite way round the ring from seat offsets, so a target
            // further round than the current one is reached by aiming further left.
            int here = AimRing.CurrentAim(cc);
            int atOffset = at != null ? (at.Slot - me + n) % n : n / 2;
            int wantOffset = (want - me + n) % n;

            Press(cc, wantOffset > atOffset ? "LeftAim" : "RightAim");

            int now = AimRing.CurrentAim(cc);
            if (now == here) break;                 // the range ran out; nothing more to try
            last = now;
        }

        return last;
    }

    /// <summary>
    /// The player the aim currently resolves to, asked of the game rather than computed.
    ///
    /// Asked through the game's own <c>GetAim</c>, not through <see cref="AimRing"/>. The mod
    /// stands aside at four players and below - the shipped table is right for that size - so
    /// asking it there returns nothing, and this reported every single shot at four players as
    /// having resolved to nobody. Five wrong answers in a cell about a mode that was behaving
    /// perfectly, from an instrument measuring itself.
    /// </summary>
    private static PlayerStats Selected(CharController cc)
    {
        try { return AskGetAim(cc, AimRing.CurrentAim(cc), true); } catch { return null; }
    }

    /// <summary>
    /// Name and seat, counted the way every other diagnostic in the mod counts them.
    ///
    /// This printed seats from one while the ring probe and the seat check printed them from
    /// zero, so a single chaos card produced three lines about one player calling them seat 3,
    /// seat 4 and seat 4 again. Nothing was wrong with the game and the log said otherwise -
    /// which is worse than saying nothing. Player-facing text still counts from one, because
    /// people do; logs count from zero, because the code does.
    /// </summary>
    private static string Name(PlayerStats p, int slot)
    {
        try
        {
            string n = p != null ? p.PlayerName : null;
            return string.IsNullOrEmpty(n) ? $"seat {slot}" : $"{n} (seat {slot})";
        }
        catch { return $"seat {slot}"; }
    }

    private static readonly Dictionary<string, MethodInfo> _keys = new();

    /// <summary>
    /// Call <c>LeftAim</c> or <c>RightAim</c> on a seat. Private in the game, so reached by
    /// reflection - and only used for the modes where that is what a key does.
    /// </summary>
    private static void Press(CharController cc, string key)
    {
        try
        {
            var type = cc.GetIl2CppType();
            string want = type != null ? type.FullName : null;

            MethodInfo mi = null;
            if (cc.TryCast<ChaosGamePlay>() != null) mi = Key(typeof(ChaosGamePlay), key);
            else if (cc.TryCast<PokerGamePlay>() != null) mi = Key(typeof(PokerGamePlay), key);

            if (mi == null)
            {
                Dev.Warn("aim", $"no {key} on {want ?? "this mode"} - the aim cannot be moved");
                return;
            }

            mi.Invoke(cc, null);
        }
        catch (Exception e) { Dev.Warn("aim", $"{key} failed: {e.Message}"); }
    }

    private static MethodInfo Key(Type owner, string name)
    {
        string id = owner.Name + "." + name;
        if (_keys.TryGetValue(id, out var cached)) return cached;

        var mi = AccessTools.Method(owner, name, Type.EmptyTypes);
        _keys[id] = mi;
        return mi;
    }

    /// <summary>
    /// Pull the trigger, for the two modes that have a way to. Liar's Poker's <c>TryFire</c> is
    /// a message the server sends to a client, so the command body is called instead.
    /// </summary>
    private static void Fire(CharController cc)
    {
        try
        {
            var chaos = cc.TryCast<ChaosGamePlay>();
            if (chaos != null) { chaos.TryFire(); return; }

            var poker = cc.TryCast<PokerGamePlay>();
            if (poker != null) poker.UserCode_HitTargetCmd();
        }
        catch (Exception e) { Dev.Warn("aim", $"could not fire: {e.Message}"); }
    }
}
