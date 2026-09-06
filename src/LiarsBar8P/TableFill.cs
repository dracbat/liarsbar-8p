using System;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;

namespace LiarsBar8P;

/// <summary>
/// Seats the players <c>Manager.StartGame</c> never got to.
///
/// StartGame walks the lobby roster spawning a seated character for each player, and it
/// throws a NullReferenceException partway through. With four players that goes unnoticed,
/// because by then it has done its job. With more, it stops early: in an eight player test
/// it seated four, gave a fifth the same seat as the host, and simply lost the other three
/// — they were in the lobby, they were in the roster, and they never appeared at the table.
///
/// The exception itself is not reachable from here; it is inside compiled code and every
/// collection it could be indexing has already been grown. What is reachable is the job it
/// left unfinished, and the game has a method for exactly that job:
/// <c>SpawnPlayerwithskin(player, slot, skin)</c>, which is what the game itself uses when
/// a player has to be seated outside the initial sweep. So anyone missing is seated with
/// it, on the lowest free seat.
///
/// This runs as a finalizer because a postfix never runs when the original throws — which
/// is the entire situation being handled. It passes the exception on rather than
/// swallowing it, so the diagnostics that report it still do.
/// </summary>
internal static class TableFill
{
    /// <summary>Players this match has already tried to seat, so a failure cannot loop.</summary>
    private static readonly HashSet<string> _attempted = new();

    /// <summary>
    /// When <c>Manager.StartGame</c> ran for this match, and the match it ran for.
    ///
    /// Nothing here may act before that. A five player game showed why: the match scene
    /// loads, the lobby roster already holds five, and the table roster is still empty
    /// because StartGame has not been called yet. Seeing five in the lobby and none at the
    /// table, this seated all five itself — and then StartGame ran and seated them properly
    /// on top. Ten bodies, ten roster entries, eight seats, and everybody standing inside
    /// somebody else. The log shows it plainly: five "StartGame missed" lines *before* the
    /// line that StartGame's own prefix writes.
    ///
    /// So the question this asks is not "is the table short of the lobby" but "is the table
    /// still short several seconds after the game finished seating people".
    /// </summary>
    private static float _startedAt;
    private static Manager _startedFor;
    private static int _lastCount = -1;
    private static float _lastChangedAt;

    /// <summary>How long the table has to stop changing before it is believed.</summary>
    private const float SettleSeconds = 2f;

    /// <summary>
    /// Has the game had its own turn at seating everyone, and finished?
    ///
    /// Finished is not a fixed wait: the characters StartGame spawns join the roster over
    /// several frames, so what is watched for is the roster holding still. Two seconds
    /// without the count changing, and no sooner than two seconds after StartGame itself,
    /// means the table is what the game intends it to be. Only then is a gap real.
    /// </summary>
    private static bool GameHasSeated(Manager m)
    {
        if (m == null || !ReferenceEquals(m, _startedFor)) return false;
        if (_startedAt <= 0f || Time.time - _startedAt < SettleSeconds) return false;

        int count = m.Players != null ? m.Players.Count : 0;
        if (count != _lastCount)
        {
            _lastCount = count;
            _lastChangedAt = Time.time;
            return false;
        }

        return Time.time - _lastChangedAt >= SettleSeconds;
    }

    /// <summary>
    /// Grow the per-player lists StartGame walks, before it walks them.
    ///
    /// This is the likeliest cause of the exception itself. StartGame sets up each player's
    /// turn-order icon from <c>OrderSprtes</c>, and that list ships with **three** entries.
    /// It is grown with the rest of the per-player lists, but not until a round is being
    /// set up — which is after StartGame has already run and thrown. Three entries is also
    /// exactly consistent with what was observed: a handful of players seated, then a
    /// NullReferenceException, then nothing.
    ///
    /// Growing them here costs nothing if the guess is wrong, and the lists have to be the
    /// right size by the round anyway.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(Manager), nameof(Manager.StartGame))]
    private static void GrowBeforeSeating(Manager __instance)
    {
        try
        {
            _attempted.Clear();          // a new match starts with a clean slate
            _startedAt = Time.time;      // nothing may seat anybody before this moment
            _startedFor = __instance;
            _lastCount = -1;
            int players = ExpectedPlayers(__instance);
            if (players <= Limits.VanillaPlayers) return;

            var d = __instance != null ? __instance.DeckGamePlayManager : null;
            if (d == null) return;

            int before = d.OrderSprtes != null ? d.OrderSprtes.Count : -1;
            DeckFix.GrowPerPlayerLists(d, players);
            int after = d.OrderSprtes != null ? d.OrderSprtes.Count : -1;

            Plugin.Log.LogInfo(
                $"[tablefill] readying the per-player lists for {players} before seating " +
                $"(turn-order icons {before} -> {after})");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[tablefill] could not ready the per-player lists: {e.Message}");
        }
    }

    /// <summary>
    /// Called at the start of a round, once the seated players actually exist.
    ///
    /// Doing this straight after StartGame does not work: the characters it spawns add
    /// themselves to the roster on a later frame, so at that moment the table looks empty
    /// and everyone gets seated a second time. An eight player test produced nine seats,
    /// two of them the host. By the time a round is being set up the roster is real.
    /// </summary>
    internal static void EnsureEveryoneSeated(Manager m)
    {
        try { Fill(m); }
        catch (Exception e) { Plugin.Log.LogError($"[tablefill] failed: {e.Message}"); }
    }

    /// <summary>
    /// Checked twice a second while a match is running, because seating is not immediate
    /// either: a character spawned by the game's own method joins the roster on a later
    /// frame too. Doing this only once, at any single moment, always catches the table
    /// mid-population and gets the count wrong.
    /// </summary>
    internal static void Tick()
    {
        var m = Manager.Instance;
        if (m == null || m.Players == null) { _startedAt = 0f; _startedFor = null; return; }
        if (!Mirror.NetworkServer.active) return;

        // Before the game has had its own turn at seating people, an empty table means
        // nothing at all - and acting on it seats everybody twice.
        if (!GameHasSeated(m)) return;

        var nm = UnityEngine.Object.FindObjectOfType<CustomNetworkManager>();
        if (nm == null || nm.GamePlayers == null) return;

        // Nothing to do until the table is short of the lobby.
        if (m.Players.Count >= nm.GamePlayers.Count) return;

        EnsureEveryoneSeated(m);
    }

    /// <summary>
    /// How many players this match is actually for.
    ///
    /// The table roster fills in over several frames, so anything sized from it alone gets
    /// sized from a half-built table — seats trimmed away just before the players who
    /// needed them arrive, and a deck dealt for five at an eight player table. The lobby
    /// roster is complete before the match starts, so it is the honest number, and the
    /// larger of the two is safe in every case.
    /// </summary>
    internal static int ExpectedPlayers(Manager m)
    {
        int table = 0;
        try { table = m != null && m.Players != null ? m.Players.Count : 0; } catch { }

        int lobby = 0;
        try
        {
            var nm = UnityEngine.Object.FindObjectOfType<CustomNetworkManager>();
            if (nm != null && nm.GamePlayers != null) lobby = nm.GamePlayers.Count;
        }
        catch { }

        return Math.Max(table, lobby);
    }

    private static void Fill(Manager m)
    {
        if (m == null || m.Players == null) return;
        if (!Mirror.NetworkServer.active) return;      // seating is the host's job

        var nm = UnityEngine.Object.FindObjectOfType<CustomNetworkManager>();
        if (nm == null || nm.GamePlayers == null) return;

        // Never before the game has seated people itself. Whatever calls this - the tick or
        // the start of a round - an empty table before StartGame has run is not a table
        // missing anybody, it is a table that has not been filled in yet.
        if (!GameHasSeated(m)) return;

        // Adopt anyone already standing at the table but missing from the roster.
        //
        // The game's own SpawnPlayerwithskin creates the character and leaves it at that -
        // only StartGame's sweep adds to Manager.Players. Without this, a player seated
        // here never counts as seated, so the next check seats them again, and again:
        // a spawn every half second forever.
        Adopt(m);

        // Who is already at the table, and on which seats.
        var seatedIds = new HashSet<ulong>();
        var seatedNames = new HashSet<string>();
        var usedSeats = new HashSet<int>();
        foreach (var ps in m.Players)
        {
            if (ps == null) continue;
            if (ps.Player_Id != 0) seatedIds.Add(ps.Player_Id);
            if (!string.IsNullOrEmpty(ps.PlayerName)) seatedNames.Add(ps.PlayerName);
            usedSeats.Add(ps.Slot);
        }

        int missing = 0;
        foreach (var p in nm.GamePlayers)
        {
            if (p == null) continue;

            bool present = (p.PlayerSteamID != 0 && seatedIds.Contains(p.PlayerSteamID))
                           || (!string.IsNullOrEmpty(p.PlayerName) && seatedNames.Contains(p.PlayerName));
            if (present) continue;

            // One attempt per player per match. If seating them does not take, saying so
            // once is useful; saying so twice a second forever is a spawn loop.
            string key = p.PlayerSteamID != 0 ? p.PlayerSteamID.ToString() : p.PlayerName;
            if (string.IsNullOrEmpty(key) || _attempted.Contains(key)) continue;
            _attempted.Add(key);

            int seat = 0;
            while (usedSeats.Contains(seat)) seat++;
            if (m.Slots != null && seat >= m.Slots.Count)
            {
                Plugin.Log.LogWarning(
                    $"[tablefill] '{p.PlayerName}' has nowhere to sit - {m.Slots.Count} seats for " +
                    $"{nm.GamePlayers.Count} players");
                break;
            }

            try
            {
                m.SpawnPlayerwithskin(p, seat, p.PlayerSkin);
                usedSeats.Add(seat);
                if (!string.IsNullOrEmpty(p.PlayerName)) seatedNames.Add(p.PlayerName);
                if (p.PlayerSteamID != 0) seatedIds.Add(p.PlayerSteamID);
                missing++;
                Plugin.Log.LogWarning(
                    $"[tablefill] StartGame missed '{p.PlayerName}' - seated them on seat {seat}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[tablefill] could not seat '{p.PlayerName}': {e.Message}");
            }
        }

        Separate(m);

        if (missing > 0)
            Plugin.Log.LogWarning(
                $"[tablefill] {missing} player(s) were left out of the table and have been seated; " +
                $"{m.Players.Count} now seated");
    }

    /// <summary>
    /// Put any seated character that is not in the roster into it.
    ///
    /// <c>Manager.Players</c> is only added to by StartGame's own sweep, so a player seated
    /// any other way exists at the table, holds a seat and can be dealt to, while every
    /// count in the game says they are not there.
    /// </summary>
    private static void Adopt(Manager m)
    {
        PlayerStats[] all;
        try { all = UnityEngine.Object.FindObjectsOfType<PlayerStats>(); }
        catch { return; }
        if (all == null) return;

        // Who is already in the roster, as people rather than as objects. A person with two
        // characters at the table must still be one entry: the roster is what the deal, the
        // turn order and the seat ring are all counted from, and an extra entry there is
        // what puts two players on one seat.
        var ids = new HashSet<ulong>();
        var names = new HashSet<string>();
        foreach (var seated in m.Players)
        {
            if (seated == null) continue;
            if (seated.Player_Id != 0) ids.Add(seated.Player_Id);
            if (!string.IsNullOrEmpty(seated.PlayerName)) names.Add(seated.PlayerName);
        }

        foreach (var ps in all)
        {
            if (ps == null) continue;

            bool known = false;
            foreach (var known_ in m.Players) if (known_ == ps) { known = true; break; }
            if (known) continue;

            bool sameFace = (ps.Player_Id != 0 && ids.Contains(ps.Player_Id))
                            || (!string.IsNullOrEmpty(ps.PlayerName) && names.Contains(ps.PlayerName));
            if (sameFace)
            {
                Plugin.Log.LogWarning(
                    $"[tablefill] a second character for '{ps.PlayerName}' is at the table - " +
                    "left out of the roster so they are only counted once");
                continue;
            }

            if (ps.Player_Id != 0) ids.Add(ps.Player_Id);
            if (!string.IsNullOrEmpty(ps.PlayerName)) names.Add(ps.PlayerName);

            m.Players.Add(ps);
            Plugin.Log.LogWarning(
                $"[tablefill] '{ps.PlayerName}' was at the table but not in the roster - added " +
                $"({m.Players.Count} seated)");
        }
    }

    /// <summary>
    /// Two players on one seat is as bad as a missing one: turn order visits a seat, and
    /// whichever of them is found first acts for both. StartGame produces this above four
    /// players, so any duplicate is moved to a free seat.
    /// </summary>
    private static void Separate(Manager m)
    {
        var taken = new Dictionary<int, PlayerStats>();
        var twins = new List<PlayerStats>();

        foreach (var ps in m.Players)
        {
            if (ps == null) continue;
            if (!taken.ContainsKey(ps.Slot)) { taken[ps.Slot] = ps; continue; }

            // Two entries for the same person is not two players sharing a seat, and giving
            // the second one a seat of its own is how one player became two at opposite
            // sides of the table. Drop it instead.
            var sitting = taken[ps.Slot];
            bool sameFace = (ps.Player_Id != 0 && ps.Player_Id == sitting.Player_Id)
                            || (!string.IsNullOrEmpty(ps.PlayerName) && ps.PlayerName == sitting.PlayerName);
            if (sameFace)
            {
                Plugin.Log.LogWarning(
                    $"[tablefill] '{ps.PlayerName}' is in the roster twice on seat {ps.Slot} - " +
                    "the second entry is dropped rather than given a seat of its own");
                twins.Add(ps);
                continue;
            }

            int seat = 0;
            while (taken.ContainsKey(seat)) seat++;
            if (m.Slots != null && seat >= m.Slots.Count) continue;

            Plugin.Log.LogWarning(
                $"[tablefill] '{ps.PlayerName}' shared seat {ps.Slot} with " +
                $"'{taken[ps.Slot].PlayerName}' - moved to seat {seat}");
            ps.NetworkSlot = seat;
            taken[seat] = ps;
        }

        // Removed after the walk, not during it - the roster is being iterated.
        foreach (var twin in twins)
        {
            try { m.Players.Remove(twin); }
            catch (Exception e) { Plugin.Log.LogError($"[tablefill] could not drop a duplicate: {e.Message}"); }
        }
    }
}
