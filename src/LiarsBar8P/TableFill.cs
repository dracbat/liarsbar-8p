using System;
using System.Collections.Generic;
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
        if (m == null || m.Players == null) return;
        if (!Mirror.NetworkServer.active) return;

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

        foreach (var ps in all)
        {
            if (ps == null) continue;

            bool known = false;
            foreach (var known_ in m.Players) if (known_ == ps) { known = true; break; }
            if (known) continue;

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
        foreach (var ps in m.Players)
        {
            if (ps == null) continue;
            if (!taken.ContainsKey(ps.Slot)) { taken[ps.Slot] = ps; continue; }

            int seat = 0;
            while (taken.ContainsKey(seat)) seat++;
            if (m.Slots != null && seat >= m.Slots.Count) continue;

            Plugin.Log.LogWarning(
                $"[tablefill] '{ps.PlayerName}' shared seat {ps.Slot} with " +
                $"'{taken[ps.Slot].PlayerName}' - moved to seat {seat}");
            ps.NetworkSlot = seat;
            taken[seat] = ps;
        }
    }
}
