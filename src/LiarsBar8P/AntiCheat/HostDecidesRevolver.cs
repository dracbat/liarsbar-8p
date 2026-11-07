using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// The host's own record of every revolver at a Liar's Deck (Basic, Devil, Deck 2) or Chaos deck
/// table it hosts, and the host's settling of each pull from that record.
///
/// <b>What it does.</b> The host already loads every revolver itself and counts every pull. So when
/// a player's seat takes a pull, the host notes the outcome its own record gives at that moment,
/// and that outcome is the one the table sees: applied through the game's own server handlers, in
/// the order the game always applied them, so every copy animates as it always did. The host
/// settles each pull at the usual moment, and on its own a little later if nothing arrives.
///
/// <b>Who is a suspect.</b> Only a real remote player's seat. A bot's pull, and the host's own,
/// run on the host by the game's own path and are left alone.
///
/// <b>When a player is removed.</b> Only for a message about a pull that no unchanged copy could
/// send, whatever the lag; anything else is refused and the host's outcome stands.
/// </summary>
internal static class HostRevolver
{
    internal const string Tag = Fair.Tag;

    /// <summary>How long a pull takes in the game, from its start to its end.</summary>
    internal const float HonestWait = 7.0f;

    /// <summary>A message this soon after the pull never removes anyone.</summary>
    internal const float EarliestReport = 6.5f;

    /// <summary>
    /// How long the host waits before settling the pull itself: four times
    /// the game's own wait, so lag both ways and a long hitch never change an outcome.
    /// </summary>
    internal const float Deadline = 30f;

    /// <summary>
    /// A pull the host settles as a shot is settled this soon whatever arrives, so a seat that is
    /// out never plays on: the game's own wait plus a margin for lag. A message after it changes nothing.
    /// </summary>
    internal const float ShotDeadline = HonestWait + 3f;

    /// <summary>A pull is forgotten this long after it began.</summary>
    internal const float Forget = 120f;

    /// <summary>The margin before a pull that decides whether a message against the host's record can be a clear cheat.</summary>
    internal const float ShownMargin = 1.0f;

    /// <summary>One pull the host is settling.</summary>
    internal sealed class Pull
    {
        internal float At;
        internal int Match;
        internal bool Fatal;
        internal bool Shown;      // a message against the host's record here is a clear cheat
        internal bool Settled;
        internal bool Reported;
        internal bool TestDropped;
        internal int CurrentBefore;
    }

    /// <summary>One seat's revolver, Liar's Deck or Chaos deck.</summary>
    internal sealed class Gun
    {
        internal readonly NetworkBehaviour Part;
        internal readonly bool Chaos;
        private readonly DeckGameplay _deck;
        private readonly ChaosDeckGameplay _chaos;
        internal int Match;

        // What the table has been sent, and since when the two have been equal.
        internal int WireCurrent = int.MinValue;
        internal int WireBullet = int.MinValue;
        internal float EqualSince = -1f;
        internal int EqualFrame = -1;
        internal int LastWire = int.MinValue;

        /// <summary>A pull of this seat that the host settles as a shot is under way.</summary>
        internal bool Showing;

        internal readonly List<Pull> Pulls = new();

        internal Gun(NetworkBehaviour part, bool chaos)
        {
            Part = part;
            Chaos = chaos;
            if (chaos) _chaos = part.TryCast<ChaosDeckGameplay>();
            else _deck = part.TryCast<DeckGameplay>();
            Match = Fair.Match;
        }

        internal bool Alive => Part != null && (Chaos ? _chaos != null : _deck != null);
        internal int Current => Chaos ? _chaos.currentrevoler : _deck.currentRevolver;
        internal int Bullet => Chaos ? _chaos.revolverbulllet : _deck.revolverBullet;

        /// <summary>Writes the field only, never the network setter: nothing is marked to send.</summary>
        internal void PutBullet(int value)
        {
            if (Chaos) _chaos.revolverbulllet = value;
            else _deck.revolverBullet = value;
        }

        internal ulong CurrentBit => Chaos ? 0x20UL : 0x10UL;
        internal ulong BulletBit => Chaos ? 0x40UL : 0x20UL;

        internal PlayerStats Stats
        {
            get { try { return Part.GetComponent<PlayerStats>(); } catch { return null; } }
        }

        internal int Seat
        {
            get { var s = Stats; return s != null ? s.Slot : -1; }
        }

        internal bool Dead
        {
            get { var s = Stats; return s != null && s.Dead; }
        }

        internal string Mode => Chaos ? "Chaos deck" : "Liar's Deck";

        /// <summary>The game's own server handler for a pull that fired.</summary>
        internal void Fire()
        {
            if (Chaos) _chaos.UserCode_CommandBeDead();
            else _deck.UserCode_CmdKillPlayer();
        }

        /// <summary>The game's own server handler for an empty chamber.</summary>
        internal void Click()
        {
            if (Chaos) _chaos.UserCode_CmdRevolverSurvived();
            else _deck.UserCode_CmdRevolverSurvived();
        }

        /// <summary>After a value goes out: note whether the table now sees the two as equal, and since when.</summary>
        internal void Sent()
        {
            bool equal = WireCurrent != int.MinValue && WireCurrent == WireBullet;
            if (!equal) { EqualSince = -1f; EqualFrame = -1; }
            else if (EqualSince < 0f) { EqualSince = Time.unscaledTime; EqualFrame = Time.frameCount; }
        }

        internal bool HasOpenFiringPull()
        {
            foreach (var p in Pulls) if (p.Fatal && !p.Settled) return true;
            return false;
        }
    }

    private static readonly Dictionary<IntPtr, Gun> _guns = new();

    internal static Gun For(NetworkBehaviour part, bool chaos)
    {
        if (part == null) return null;
        IntPtr key = part.Pointer;
        if (_guns.TryGetValue(key, out var gun) && gun.Chaos == chaos && gun.Match == Fair.Match) return gun;
        gun = new Gun(part, chaos);
        if (!gun.Alive) return null;
        _guns[key] = gun;
        return gun;
    }

    internal static Gun Find(NetworkBehaviour part)
    {
        if (part == null) return null;
        return _guns.TryGetValue(part.Pointer, out var gun) && gun.Match == Fair.Match ? gun : null;
    }

    // ------------------------------------------------------------------ what the table is shown

    /// <summary>
    /// The value the table is shown for a seat's revolver when hidden hands are on. Chosen so that
    /// every unchanged copy shows each pull exactly as the host settles it.
    /// </summary>
    internal static int StandIn(Gun gun)
    {
        int current = gun.Current;
        if (gun.Showing) return current;
        return Math.Max(HouseRules.Chambers - 1, current);
    }

    // ------------------------------------------------------------------ a pull begins

    /// <summary>
    /// From the game's own start of a pull, before the seat is told: the host notes the outcome its
    /// own record gives, for a real remote player's seat only.
    /// </summary>
    internal static void Pulling(NetworkBehaviour part, bool chaos, NetworkConnectionToClient cn)
    {
        if (!NetworkServer.active || !Fair.Hosting || !Fair.IsRemote(cn)) return;
        var gun = For(part, chaos);
        if (gun == null) return;

        // A pull of this seat still waiting from before: settle it first, by the host's record.
        foreach (var old in gun.Pulls)
            if (!old.Settled) Settle(gun, old, "a newer pull began first");

        int current = gun.Current;
        bool fatal = current == gun.Bullet;
        bool shownEqual = gun.EqualSince >= 0f && gun.WireCurrent == current && gun.WireBullet == current;
        bool shown = fatal && shownEqual && gun.EqualFrame < Time.frameCount
                     && Time.unscaledTime - gun.EqualSince >= ShownMargin;

        if (fatal)
        {
            gun.Showing = true;
            // In the order every unchanged copy needs to show this pull as the host settles it.
            if (Fair.HideHands && !shownEqual) part.SetSyncVarDirtyBit(gun.BulletBit);
        }

        gun.Pulls.Add(new Pull { At = Time.unscaledTime, Match = Fair.Match, Fatal = fatal, Shown = shown, CurrentBefore = current });
        Plugin.Log.LogInfo($"{Tag} {gun.Mode} seat {gun.Seat} takes a revolver pull - the host settles it from its own record");
    }

    // ------------------------------------------------------------------ settling a pull

    internal enum Verdict { Agrees, Overruled, Caught }

    /// <summary>
    /// How the host settles a pull against its own record. Pure, so the test driver can walk every
    /// case. A player is removed only in a case no unchanged game could produce whatever the lag:
    /// see the class comment. A pull the host had already settled never removes anyone.
    /// </summary>
    internal static Verdict Judge(bool fatal, bool shown, bool saysFired, float age, int waiting, bool settledBefore = false)
    {
        if (saysFired == fatal) return Verdict.Agrees;
        if (fatal && !saysFired && shown && waiting == 1 && age >= EarliestReport && !settledBefore) return Verdict.Caught;
        return Verdict.Overruled;
    }

    /// <summary>
    /// A pull of this seat: the host's own record settles it either way. Returns true to let the
    /// game's own handler run (not a remote player's seat, or not hosting), false when the host has
    /// settled it instead.
    /// </summary>
    internal static bool Report(NetworkBehaviour obj, NetworkConnectionToClient sender, bool chaos, bool saysFired)
    {
        bool handled = false;
        try
        {
            if (!Fair.Hosting || !Fair.IsRemote(sender) || obj == null) return true;
            var owner = obj.connectionToClient;
            if (owner == null || owner.connectionId != sender.connectionId) return true;
            var gun = For(obj, chaos);
            if (gun == null) return true;
            handled = true;
            int id = sender.connectionId;

            Pull pull = null;
            int waiting = 0;
            foreach (var p in gun.Pulls)
                if (!p.Reported) { waiting++; pull ??= p; }

            if (Tests.Take(Tests.Drop, id))
            {
                if (pull != null) pull.TestDropped = true;
                Plugin.Log.LogInfo($"{Tag} test: connection {id}'s revolver message dropped by the host; its deadline settles the pull");
                return false;
            }
            bool flipped = Tests.Take(Tests.Flip, id);
            if (flipped) saysFired = !saysFired;

            if (pull == null)
            {
                Fair.Refused($"connection {id}: a {gun.Mode} revolver message with no pull waiting");
                if (flipped) Fair.Result("revolver flip", false, "no pull was waiting for that seat; arm the test before a pull");
                return false;
            }

            float age = Time.unscaledTime - pull.At;
            bool settledBefore = pull.Settled;   // by the deadline, or by a newer pull
            var verdict = Judge(pull.Fatal, pull.Shown, saysFired, age, waiting, settledBefore);
            pull.Reported = true;
            int caughtBefore = Fair.CaughtCount;
            Settle(gun, pull, "the seat's message arrived");

            if (verdict == Verdict.Overruled)
                Fair.Refused($"connection {id}: {gun.Mode} seat {gun.Seat}'s revolver message differs from the host's record; the host's outcome stands");
            else if (verdict == Verdict.Caught)
                Fair.Caught(sender, $"a {gun.Mode} revolver message no unchanged game could send");

            if (flipped)
            {
                bool applied = Applied(gun, pull);
                bool wantKick = pull.Fatal && !saysFired && pull.Shown && waiting == 1 && age >= EarliestReport && !settledBefore;
                bool kicked = Fair.CaughtCount > caughtBefore || verdict == Verdict.Caught;
                Fair.Result("revolver flip", applied && kicked == wantKick,
                            $"seat {gun.Seat}: the host applied its own outcome={applied}, verdict {verdict}" +
                            $" ({(pull.Shown ? "a clear case" : "a close case")})");
            }
            Prune(gun);
            return false;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"{Tag} a revolver message could not be checked: {e.Message}");
            return !handled;
        }
    }

    /// <summary>Apply the host's outcome for one pull, once, through the game's own server handlers.</summary>
    internal static void Settle(Gun gun, Pull pull, string why)
    {
        if (pull.Settled) return;
        pull.Settled = true;
        if (pull.Match != Fair.Match || !gun.Alive || gun.Dead) return;
        if (pull.Fatal) SettleDue(gun);

        Fair.AsSentBy(null, pull.Fatal ? gun.Fire : gun.Click);
        if (!pull.Fatal && !gun.HasOpenFiringPull()) gun.Showing = false;
        Plugin.Log.LogInfo($"{Tag} {gun.Mode} seat {gun.Seat}'s pull settled by the host ({why}): " +
                           (pull.Fatal ? "the shot fired" : "an empty chamber"));
    }

    /// <summary>
    /// A seat goes out: every other seat's shot from the same moment that is already due lands
    /// with it, so no seat outlives another it fell together with.
    /// </summary>
    internal static void SettleDue(Gun except)
    {
        if (_guns.Count == 0) return;
        float now = Time.unscaledTime;
        _due.Clear();
        foreach (var gun in _guns.Values)
        {
            if (gun == except || !gun.Alive || gun.Match != Fair.Match) continue;
            foreach (var p in gun.Pulls)
                if (p.Fatal && !p.Settled && now - p.At >= HonestWait - 0.5f) _due.Add((gun, p));
        }
        foreach (var (gun, p) in _due.ToArray()) Settle(gun, p, "a seat from the same moment went out");
    }

    private static readonly List<(Gun, Pull)> _due = new();

    /// <summary>The seat has a pull under way that the host settles as a shot: it is as good as out.</summary>
    internal static bool Falling(Component part)
    {
        if (part == null || _guns.Count == 0) return false;
        try
        {
            var go = part.gameObject;
            foreach (var gun in _guns.Values)
                if (gun.Alive && gun.Match == Fair.Match && gun.Part.gameObject == go && gun.HasOpenFiringPull()) return true;
        }
        catch { }
        return false;
    }

    /// <summary>A seat that the game itself puts out (a bot's pull, the host's own): settle the shots due with it.</summary>
    internal static void GoingOut(NetworkBehaviour part)
    {
        try { if (NetworkServer.active && Fair.Hosting) SettleDue(Find(part)); }
        catch (Exception e) { Plugin.Log.LogError($"{Tag} shots due with a seat going out could not be settled: {e.Message}"); }
    }

    private static bool Applied(Gun gun, Pull pull) =>
        pull.Fatal ? gun.Dead : gun.Current == pull.CurrentBefore + 1;

    private static void Prune(Gun gun)
    {
        var pulls = gun.Pulls;
        if (pulls.Count == 0) return;
        float now = Time.unscaledTime;
        for (int i = pulls.Count - 1; i >= 0; i--)
        {
            var p = pulls[i];
            if ((p.Reported && p.Settled) || now - p.At >= Forget) pulls.RemoveAt(i);
        }
    }

    // ------------------------------------------------------------------ the deadline

    private static readonly List<IntPtr> _gone = new();

    /// <summary>Each frame: settle any pull the host has waited on long enough.</summary>
    private static void Tick()
    {
        if (_guns.Count == 0) return;
        if (!Fair.Hosting) { _guns.Clear(); return; }

        float now = Time.unscaledTime;
        _gone.Clear();
        foreach (var kv in _guns)
        {
            var gun = kv.Value;
            if (!gun.Alive || gun.Match != Fair.Match) { _gone.Add(kv.Key); continue; }
            foreach (var pull in gun.Pulls)
            {
                if (pull.Settled || now - pull.At < (pull.Fatal ? ShotDeadline : Deadline)) continue;
                Settle(gun, pull, "the seat's game said nothing in time");
                if (pull.TestDropped)
                    Fair.Result("revolver drop", Applied(gun, pull),
                                $"{gun.Mode} seat {gun.Seat}: the host settled the pull on its own ({(pull.Fatal ? "fired" : "empty")})");
            }
            Prune(gun);
        }
        foreach (var key in _gone) _guns.Remove(key);
    }

    // ------------------------------------------------------------------ developer tests

    /// <summary>One-shot test switches, per connection id. Each changes only what the host does with a message it has received.</summary>
    internal static class Tests
    {
        internal static readonly HashSet<int> Drop = new();
        internal static readonly HashSet<int> Flip = new();
        internal static bool Take(HashSet<int> set, int id) => set.Count > 0 && Fair.TestsAllowed && set.Remove(id);
    }

    internal static void Register()
    {
        Fair.OnTick("revolver deadline", Tick);
        Fair.OnDrive("revolver", Drive);
    }

    /// <summary>
    /// "fair revolver drop [conn]": that seat's next pull must be settled by the deadline with the
    /// host's outcome.
    /// "fair revolver flip [conn]": that seat's next pull, seen the other way round, must still end
    /// with the host's outcome, and only a clear case may remove the player.
    /// "fair revolver rules": every case of the settling rules and the stand-in chamber, offline.
    /// </summary>
    private static void Drive(string rest)
    {
        string[] words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string what = words.Length > 0 ? words[0] : "rules";
        string which = words.Length > 1 ? words[1] : "";

        if (what == "rules") { Rules(); return; }

        var conn = Fair.TestConnection(which);
        if (conn == null) { Fair.Result($"revolver {what}", false, "no remote player connected"); return; }
        switch (what)
        {
            case "drop":
                Tests.Drop.Add(conn.connectionId);
                Plugin.Log.LogInfo($"{Tag} test: connection {conn.connectionId}'s next revolver message will be dropped by the host");
                break;
            case "flip":
                Tests.Flip.Add(conn.connectionId);
                Plugin.Log.LogInfo($"{Tag} test: connection {conn.connectionId}'s next revolver message will be read as the opposite by the host");
                break;
            default:
                Plugin.Log.LogWarning($"{Tag} test: revolver drop|flip|rules [connection]");
                break;
        }
    }

    private static void Rules()
    {
        void Case(string name, Verdict want, bool fatal, bool shown, bool saysFired, float age, int waiting, bool settled = false)
        {
            var got = Judge(fatal, shown, saysFired, age, waiting, settled);
            Fair.Result("revolver rules", got == want, $"{name}: {got}");
        }

        Case("a firing pull, message matching the record", Verdict.Agrees, true, true, true, 7.2f, 1);
        Case("an empty chamber, message matching the record", Verdict.Agrees, false, false, false, 7.2f, 1);
        Case("a message against the record, clear case", Verdict.Caught, true, true, false, 7.2f, 1);
        Case("a message against the record, close case", Verdict.Overruled, true, false, false, 7.2f, 1);
        Case("a message against the record, an older pull waiting", Verdict.Overruled, true, true, false, 7.2f, 2);
        Case("a message against the record, too soon after the pull", Verdict.Overruled, true, true, false, 2.0f, 1);
        Case("a message against the record, the other way", Verdict.Overruled, false, false, true, 7.2f, 1);
        Case("a message against the record, after the host settled", Verdict.Overruled, true, true, false, 12.0f, 1, settled: true);

        // The stand-in never misstates how a pull ends.
        bool ok = true;
        foreach (int n in new[] { 6, 4, 3 })
            for (int fired = 0; fired < n; fired++)
            {
                int quiet = Math.Max(n - 1, fired);
                bool lastChamber = fired == n - 1;
                if ((quiet == fired) != lastChamber) ok = false;
            }
        Fair.Result("revolver rules", ok, "the stand-in never misstates how a pull ends");
    }
}
