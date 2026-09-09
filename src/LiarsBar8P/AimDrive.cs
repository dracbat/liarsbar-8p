using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Makes the test harness actually choose somebody and pull the trigger.
///
/// A chaos card stops the round and hands whoever threw it a revolver and a choice. Every run
/// before this one let that choice time out: the harness knew how to throw cards and call liar
/// and nothing else, so the aiming phase opened, sat there, and closed itself. The logs said
/// "chaos aim resolved", which is true and says nothing - it resolved the way it resolves when
/// nobody is playing. The one mechanic that decides who dies had never been exercised at any
/// table size, which is precisely why <see cref="AimRing"/> could be broken for four of eight
/// seats without a single run noticing.
///
/// So this drives it, and it drives it through the keys a person presses. The aim is moved by
/// calling <c>LeftAim</c> and <c>RightAim</c> - the methods bound to the arrow keys - one step
/// at a time, and the shot is taken with <c>TryFire</c>, which is what the fire key calls.
/// Setting the aim field directly would have tested the arithmetic and skipped the input path
/// that the reported bug was actually in.
///
/// It then says, in the log, both halves of the thing being tested: the seat it meant to shoot
/// and the seat the aim resolved to. Those two agreeing at eight players, from every chair, is
/// the claim; <c>ServerApplyShotToTarget</c> reports separately who the server then killed, so
/// the third number can be checked against them.
///
/// The target rotates by seat and by round so that a match works its way round the whole
/// table rather than shooting the same neighbour every time.
///
/// This is a test instrument, and it does nothing at all outside developer mode. Taking a
/// seat's shot needs the server and needs the harness to be standing in for everybody;
/// measuring the ring runs on every machine, because whether the peers agree about who an
/// aim points at is half the question.
/// </summary>
internal static class AimDrive
{
    /// <summary>Long enough for the aiming phase to have finished opening.</summary>
    private const float ThinkSeconds = 1.6f;

    /// <summary>Which aiming phase each seat has already acted in.</summary>
    private static readonly Dictionary<int, int> _acted = new();
    private static readonly Dictionary<int, float> _actAt = new();

    private static int _phase;
    private static bool _wasAiming;

    internal static void RoundStarting()
    {
        _acted.Clear();
        _actAt.Clear();
    }

    internal static void Tick()
    {
        if (!Dev.Enabled) return;

        var m = Dev.Mgr;
        if (m == null || !m.GameStarted) { _wasAiming = false; _probed = null; return; }

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

        // Driving a seat is the server's business, and only while the harness stands in for
        // everybody.
        if (!Dev.IsServer) return;
        if (!(DevAutoTest.Driving || Loopback.Mine == Loopback.Role.Host)) return;
        if (m.Players == null) return;

        // A new phase begins when somebody starts aiming and nobody was. Counting phases is
        // what lets the choice move round the table between chaos cards instead of every
        // player shooting the same neighbour all match.
        bool anyAiming = false;
        foreach (var p in m.Players)
        {
            if (p == null || p.Dead) continue;
            var cc = Play(p);
            if (cc != null && AimRing.Choosing(cc)) { anyAiming = true; break; }
        }

        if (anyAiming && !_wasAiming) { _phase++; _actAt.Clear(); }
        _wasAiming = anyAiming;
        if (!anyAiming) return;

        foreach (var p in m.Players)
        {
            if (p == null || p.Dead) continue;

            var cc = Play(p);
            if (cc == null || !AimRing.Choosing(cc)) { _actAt.Remove(p.Slot); continue; }
            if (_acted.TryGetValue(p.Slot, out int at) && at == _phase) continue;

            if (!_actAt.TryGetValue(p.Slot, out float due))
            {
                _actAt[p.Slot] = Time.time + ThinkSeconds;
                continue;
            }
            if (Time.time < due) continue;

            _actAt.Remove(p.Slot);
            _acted[p.Slot] = _phase;

            try { Choose(m, p, cc, n); }
            catch (Exception e) { Dev.Warn("aim", $"{p.PlayerName} could not take the shot: {e.Message}"); }
        }
    }

    // -------------------------------------------------------------- measuring the whole ring

    /// <summary>Let the round settle before asking it anything.</summary>
    private const float ProbeAfterSeconds = 14f;

    private static Manager _probed;
    private static float _probeAt;

    /// <summary>
    /// Ask, once a match, whether every seat can point at every other seat.
    ///
    /// This exists because the end-to-end test cannot be relied on to happen. Reaching the
    /// aiming phase needs a chaos card to be dealt into somebody's hand and then thrown before
    /// the round ends, and a hundred and fifty second run at five players produced exactly
    /// none - so the run reported nothing about aiming and looked no different from a run
    /// where aiming worked perfectly. A mechanic that is only tested when the deck feels like
    /// it is not tested.
    ///
    /// So this asks the question directly rather than waiting to be given the opportunity. For
    /// every seat it sweeps a wider range of aim values than the game allows and writes down
    /// which players the game says each one points at - using the game's own <c>GetAim</c>,
    /// not the replacement arithmetic, so that what is measured is the answer a shot would
    /// actually resolve to rather than a restatement of the fix.
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
                var t = AskGetAim(cc, a);
                if (t == null) continue;
                if (seen.Add(t.Slot)) reached.Add(t.Slot);
            }

            asked = true;
            reached.Sort();

            var missed = new List<int>();
            foreach (int s in live)
                if (s != p.Slot && !seen.Contains(s)) missed.Add(s);

            string who = string.IsNullOrEmpty(p.PlayerName) ? $"seat {p.Slot + 1}" : p.PlayerName;

            if (missed.Count == 0)
                Plugin.Log.LogWarning(
                    $"[aimring] on {where}, {who} (seat {p.Slot}) can aim at all {live.Count - 1} of the " +
                    $"others - seats {string.Join(", ", reached)}");
            else
            {
                bad++;
                Plugin.Log.LogError(
                    $"[aimring] on {where}, {who} (seat {p.Slot}) can aim at only {reached.Count} of " +
                    $"{live.Count - 1} - never {string.Join(", ", missed)} - reaches {string.Join(", ", reached)}");
            }
        }

        if (!asked) return;

        _probed = m;
        if (bad == 0)
            Plugin.Log.LogWarning($"[aimring] on {where}, every seat at this table of {n} can point at every other");
        else
            Plugin.Log.LogError($"[aimring] on {where}, {bad} seat(s) at this table of {n} cannot reach the whole table");
    }

    private static Manager _probeFor;

    private static readonly Dictionary<string, MethodInfo> _getAim = new();

    /// <summary>
    /// What the game says an aim points at.
    ///
    /// <c>GetAim</c> is private in all three modes, so it is called by reflection. Asking the
    /// game rather than computing the answer here is the point: a probe that used the mod's
    /// own arithmetic would agree with the mod whether or not the mod was right.
    /// </summary>
    private static PlayerStats AskGetAim(CharController cc, int aim)
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

            object[] call = args.Length == 2 ? new object[] { aim, true } : new object[] { aim };
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

        // Rotate through every opponent as the match goes on: offset 1 is the next chair
        // round, n-1 the one before. Seat and phase both feed in so that no two seats pick the
        // same person in the same phase and no seat picks the same person twice running.
        int offset = 1 + (Math.Abs(_phase + me) % Math.Max(1, n - 1));
        int want = (me + offset) % n;

        var wanted = m.GetTargetPlayer(want, true);
        if (wanted == null)
        {
            // The chair this phase called for is empty or its player is out. Anybody still in
            // will do - the point is to fire at somebody, not at that somebody in particular.
            for (int d = 1; d < n && wanted == null; d++)
            {
                want = (me + d) % n;
                wanted = m.GetTargetPlayer(want, true);
            }
            if (wanted == null) return;
        }

        int landed = Walk(cc, m, me, want, n);

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
    /// Move the aim onto a seat using the keys a player would press.
    ///
    /// Which way to walk is decided by trying: the aim value that means a given seat depends
    /// on the ring size, and asking the game which direction to press rather than working it
    /// out here keeps this from being a second copy of the arithmetic under test.
    /// </summary>
    private static int Walk(CharController cc, Manager m, int me, int want, int n)
    {
        int last = Aim(cc);

        for (int i = 0; i < n * 2 + 2; i++)
        {
            var at = Selected(cc);
            if (at != null && at.Slot == want) break;

            // Aim values run the opposite way round the ring from seat offsets, so a target
            // further round than the current one is reached by aiming further left.
            int here = Aim(cc);
            int atOffset = at != null ? (at.Slot - me + n) % n : n / 2;
            int wantOffset = (want - me + n) % n;

            Press(cc, wantOffset > atOffset ? "LeftAim" : "RightAim");

            int now = Aim(cc);
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
        try { return AskGetAim(cc, CurrentAim(cc)); } catch { return null; }
    }

    private static int CurrentAim(CharController cc)
    {
        try { return AimRing.CurrentAim(cc); } catch { return 0; }
    }

    private static int Aim(CharController cc)
    {
        try
        {
            var deck = cc.TryCast<ChaosDeckGameplay>();
            if (deck != null) return deck.Aim;

            var chaos = cc.TryCast<ChaosGamePlay>();
            if (chaos != null) return chaos.Aim;

            var poker = cc.TryCast<PokerGamePlay>();
            if (poker != null) return poker.Aim;
        }
        catch { }
        return 0;
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

    // ------------------------------------------------------------- reaching the input path

    private static readonly Dictionary<string, MethodInfo> _keys = new();

    /// <summary>
    /// Press an aiming key on a seat.
    ///
    /// <c>LeftAim</c> and <c>RightAim</c> are private, so they are reached by reflection. That
    /// is the point rather than a workaround: this has to go through the same method the key
    /// binding calls, because the method is what is being tested.
    /// </summary>
    private static void Press(CharController cc, string key)
    {
        try
        {
            var type = cc.GetIl2CppType();
            string want = type != null ? type.FullName : null;

            MethodInfo mi = null;
            if (cc.TryCast<ChaosDeckGameplay>() != null) mi = Key(typeof(ChaosDeckGameplay), key);
            else if (cc.TryCast<ChaosGamePlay>() != null) mi = Key(typeof(ChaosGamePlay), key);
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
    /// Pull the trigger.
    ///
    /// <c>TryFire</c> branches on who is running it - the server resolves the shot there and
    /// then, a client sends a command - so calling it on the host for a seat the host does not
    /// own takes the server path and works, exactly as throwing a card does. Liar's Poker is
    /// the exception: its <c>TryFire</c> is a message the server sends to a client, so the
    /// command body is called instead.
    /// </summary>
    private static void Fire(CharController cc)
    {
        try
        {
            var deck = cc.TryCast<ChaosDeckGameplay>();
            if (deck != null) { deck.TryFire(); return; }

            var chaos = cc.TryCast<ChaosGamePlay>();
            if (chaos != null) { chaos.TryFire(); return; }

            var poker = cc.TryCast<PokerGamePlay>();
            if (poker != null) poker.UserCode_HitTargetCmd();
        }
        catch (Exception e) { Dev.Warn("aim", $"could not fire: {e.Message}"); }
    }
}
