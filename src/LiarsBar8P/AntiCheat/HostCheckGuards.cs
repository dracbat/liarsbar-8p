using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// The host's half of <see cref="HostCheck"/>: at a table this copy hosts, the states the players'
/// copies treat as proof of a rigged table stay impossible, whatever any player's game asks for.
/// So a player can never make an honest host look guilty, and the players' check can be strict.
///
/// Each guard is a no-op in honest play: a Liar's Deck seat never makes more than five pulls and
/// lives, the Liar's Poker revolver never holds more than eight bullets, a Liar's Dice cup never
/// holds more than five dice, and each seat's life follows the host's own record, apart from one
/// restore of a player's own seat when a table moves to a new host, never above what this copy saw
/// of that seat while it was a player. Anything past that is refused quietly (a refusal, not a
/// removal: other checks judge the player).
/// </summary>
internal static class HostCheckGuards
{
    private const string Tag = Fair.Tag;

    /// <summary>The most pulls a Liar's Deck seat can have made and still be alive.</summary>
    internal const int MostPullsAlive = HostCheck.Chambers - 1;

    /// <summary>Developer switch, only for the length of one "fair hostcheat" case.</summary>
    private static bool _bypassForTest;


    // ------------------------------------------------------------------ a table taken over

    private static bool _serving;
    private static bool _tookOver;
    private static float _tookOverAt;

    /// <summary>How long after a takeover the players' copies may restore their own seats.</summary>
    internal const float RestoreFor = 90f;

    private static readonly HashSet<(string, uint)> _restored = new();

    /// <summary>
    /// The restore after a host migration: this copy took the table over, and the takeover is under
    /// way or only just settled. The only time a player's copy speaks for its own seat's state.
    /// </summary>
    internal static bool RestoreWindow =>
        NetworkServer.active && (MigrationNow() || (_tookOver && Time.unscaledTime - _tookOverAt < RestoreFor));

    /// <summary>True the first time this kind of restore comes for this seat while this copy serves.</summary>
    internal static bool FirstRestore(string kind, NetworkBehaviour seat)
    {
        try { return seat != null && _restored.Add((kind, seat.netId)); }
        catch { return false; }
    }

    // What this copy saw of every seat's life while it was a player, by Steam id.
    private const float LifeRecordAge = 120f;
    private static readonly Dictionary<ulong, (int Health, bool Dead, float At)> _lives = new();
    private static float _nextLook;

    private static void LookAtLives()
    {
        float now = Time.unscaledTime;
        if (now < _nextLook) return;
        _nextLook = now + 0.5f;
        if (NetworkServer.active || !NetworkClient.active || !HostDecidesMigration.PlayingAtATable()) return;
        foreach (var ps in UnityEngine.Object.FindObjectsOfType<PlayerStats>())
        {
            if (ps == null || ps.Player_Id == 0) continue;
            _lives[ps.Player_Id] = (ps.Health, ps.Dead, now);
        }
    }

    private static bool SawLife(PlayerStats ps, out int health, out bool dead)
    {
        health = 0; dead = false;
        if (ps == null || ps.Player_Id == 0 || !_lives.TryGetValue(ps.Player_Id, out var r)) return false;
        if (Time.unscaledTime - r.At > LifeRecordAge) return false;
        health = r.Health;
        dead = r.Dead;
        return true;
    }

    /// <summary>
    /// Whether this copy's time as host began with taking the table over in a host migration. The
    /// game's own flags for that are cleared once the migration settles, so this keeps it for as
    /// long as this copy goes on hosting.
    /// </summary>
    internal static bool TookOver => _tookOver;

    private static void Tick()
    {
        bool serving = NetworkServer.active;
        LookAtLives();
        if (serving != _serving)
        {
            _serving = serving;
            _tookOver = false;
            _restored.Clear();
        }
        if (serving && !_tookOver && MigrationNow())
        {
            _tookOver = true;
            _tookOverAt = Time.unscaledTime;
            Plugin.Log.LogInfo($"{Tag} this copy took the table over in a host migration - players may restore their own seats");
        }
    }

    private static bool MigrationNow()
    {
        try
        {
            var nm = Fair.Net;
            if (nm != null && nm.MigrationStarted) return true;
            var hm = HostMigration.Instance;
            if (hm != null && hm.isNewHost) return true;
            var m = Manager.Instance;
            return m != null && m.migrated;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ the limits

    /// <summary>Liar's Deck: a living seat's pull count stops at five.</summary>
    internal static int Pulls(int value) => value > MostPullsAlive ? MostPullsAlive : value;

    /// <summary>Liar's Poker: the revolver holds eight.</summary>
    internal static int Bullets(int value) => value > HostCheck.MostBullets ? HostCheck.MostBullets : value;

    /// <summary>Liar's Dice: a cup holds five.</summary>
    internal static int Dice(int value) => value > HostCheck.MostDice ? HostCheck.MostDice : value;

    /// <summary>
    /// Whether a player's game may set this seat's life: only its own seat, only in the restore
    /// after this copy took the table over, and only once.
    /// </summary>
    internal static bool HealthAllowed(bool ownSeat, bool inRestore, bool first, out string why)
    {
        if (!ownSeat) { why = "a seat's life is the host's to set"; return false; }
        if (!inRestore) { why = "a seat's life is the host's, apart from the restore after a host migration"; return false; }
        if (!first) { why = "a seat's life is restored once"; return false; }
        why = null;
        return true;
    }

    /// <summary>A restored life never above what this copy saw of the seat, and never back from out.</summary>
    internal static void HealthKept(bool saw, int sawHealth, bool sawDead, ref int health, ref bool dead)
    {
        if (!saw) return;
        if (health > sawHealth) health = sawHealth;
        if (sawDead) dead = true;
    }

    [FairPatch("the host keeps a living seat within the cylinder")]
    [HarmonyPatch]
    internal static class Cylinder
    {
        private static void Register()
        {
            Fair.OnTick("host check guards", Tick);
            Fair.OnDrive("hostcheat", Drive);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DeckGameplay), "set_NetworkcurrentRevolver")]
        private static void DeckPulls(ref int value)
        {
            try
            {
                if (value <= MostPullsAlive || _bypassForTest || !Fair.Hosting) return;
                Fair.Refused($"a pull count of {value} (the host keeps it within the cylinder)");
                value = Pulls(value);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"{Tag} pull count guard: {e.Message}"); }
        }
    }

    [FairPatch("the host keeps the poker revolver at eight bullets")]
    [HarmonyPatch]
    internal static class Revolver
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TexasGamePlay), "set_NetworkBullets")]
        private static void PokerBullets(ref int value)
        {
            try
            {
                if (value <= HostCheck.MostBullets || _bypassForTest || !Fair.Hosting) return;
                Fair.Refused($"{value} bullets (the host keeps the revolver at eight)");
                value = Bullets(value);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"{Tag} bullet guard: {e.Message}"); }
        }
    }

    /// <summary>
    /// Liar's Dice, as the host rolls: each seat's cup is held to five dice before the roll, from
    /// the host's own limit. Runs alongside the house rules' roll (Three Dice sets three).
    /// </summary>
    [FairPatch("the host rolls at most five dice a cup")]
    [HarmonyPatch]
    internal static class Cups
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DiceGamePlayManager), nameof(DiceGamePlayManager.AssignAllDiceValues))]
        private static void DiceCups()
        {
            try
            {
                if (!Fair.Hosting) return;
                var m = Manager.Instance;
                if (m == null || m.Players == null) return;
                foreach (var p in m.Players)
                {
                    if (p == null) continue;
                    var hand = p.GetComponent<DiceGamePlay>();
                    if (hand == null || hand.TotalDices <= HostCheck.MostDice) continue;
                    Fair.Refused($"seat {p.Slot}: a cup of {hand.TotalDices} dice (the host rolls at most five)");
                    hand.TotalDices = Dice(hand.TotalDices);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"{Tag} dice guard: {e.Message}"); }
        }
    }

    private static int _healthRefused;

    /// <summary>A player's game restoring a seat's life: its own seat only, once, in the restore after a takeover.</summary>
    [FairPatch("only the host changes a seat's life")]
    [HarmonyPatch]
    internal static class SeatLife
    {
        // The sender, from the command's own handler, so this guard knows it without the shared one.
        private static NetworkConnectionToClient _from;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerObjectController), nameof(PlayerObjectController.InvokeUserCode_SetHealthData__Int32__Boolean__GameObject))]
        private static void From(NetworkConnectionToClient senderConnection) => _from = senderConnection;

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(PlayerObjectController), nameof(PlayerObjectController.InvokeUserCode_SetHealthData__Int32__Boolean__GameObject))]
        private static Exception Done(Exception __exception)
        {
            _from = null;
            return __exception;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerObjectController), nameof(PlayerObjectController.UserCode_SetHealthData__Int32__Boolean__GameObject))]
        private static bool Restore(PlayerObjectController __instance, ref int __0, ref bool __1, GameObject __2)
        {
            try
            {
                if (!Fair.Hosting) return true;
                var conn = _from ?? Fair.Sender;
                if (!Fair.IsRemote(conn) || __instance == null) return true;
                var self = __instance.connectionToClient;
                if (self == null || self.connectionId != conn.connectionId) return true;
                var seat = __2 != null ? __2.GetComponent<PlayerStats>() : null;
                if (seat == null)
                {
                    _healthRefused++;
                    Fair.Refused($"connection {conn.connectionId}: a seat's life for no seat");
                    return false;
                }

                var owner = seat.connectionToClient;
                bool own = owner != null && owner.connectionId == conn.connectionId;
                bool inRestore = RestoreWindow;
                if (HealthAllowed(own, inRestore, own && inRestore && FirstRestore("life", seat), out string why))
                {
                    bool saw = SawLife(seat, out int sawHealth, out bool sawDead);
                    HealthKept(saw, sawHealth, sawDead, ref __0, ref __1);
                    return true;
                }

                _healthRefused++;
                Fair.Refused($"connection {conn.connectionId}: {why}");
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Tag} seat life guard: {e.Message}");
                return true;
            }
        }
    }

    // ------------------------------------------------------------------ developer tests

    /// <summary>
    /// "fair hostcheat guards [connection]" checks the guards inside this host. "fair hostcheat
    /// revolver|bullets|revive|deadwinner [slot]" sets this host's own table into one of the
    /// states the players' check looks for, past the guards, so every modded player at the table
    /// can be seen to leave (a player's "fair hostcheck dry" keeps them seated). That state goes
    /// to every copy at the table, so these run only inside the loopback harness, with developer
    /// mode on (<see cref="Fair.TestsAllowed"/>); nothing any player sends is touched.
    /// </summary>
    private static void Drive(string rest)
    {
        string[] words = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        string what = words.Length > 0 ? words[0] : "";
        string arg = words.Length > 1 ? words[1] : "";

        if (!Fair.TestsAllowed) { Fair.Result("hostcheat " + what, false, "loopback harness only"); return; }
        if (!Fair.Hosting) { Fair.Result("hostcheat " + what, false, $"not hosting ({Fair.Why})"); return; }

        switch (what)
        {
            case "guards": Guards(arg); return;
            case "revolver":
            case "bullets":
            case "revive":
            case "deadwinner":
                Make(what, arg);
                return;
            default:
                Plugin.Log.LogWarning($"{Tag} test: hostcheat guards [connection] | revolver|bullets|revive|deadwinner [slot]");
                return;
        }
    }

    private static void Guards(string which)
    {
        Fair.Result("hostcheat pulls", Pulls(5) == 5 && Pulls(6) == 5 && Pulls(20) == 5 && Pulls(0) == 0, "5 stays, 6 and 20 come back to 5");
        Fair.Result("hostcheat bullets", Bullets(8) == 8 && Bullets(9) == 8 && Bullets(1) == 1, "8 stays, 9 comes back to 8");
        Fair.Result("hostcheat dice", Dice(5) == 5 && Dice(3) == 3 && Dice(9) == 5, "5 and 3 stay, 9 comes back to 5");

        bool a = HealthAllowed(true, true, true, out _);                   // own seat, in the restore after a takeover
        bool b = !HealthAllowed(true, false, true, out _);                 // own seat, outside a restore
        bool c = !HealthAllowed(false, true, true, out _);                 // another seat
        bool d = !HealthAllowed(true, true, false, out _);                 // own seat, a second time
        int h = 9; bool dd = false;
        HealthKept(true, 2, true, ref h, ref dd);
        bool e = h == 2 && dd;                                             // never above what was seen, never back from out
        Fair.Result("hostcheat seat life rules", a && b && c && d && e, $"restore={a} outside={b} other={c} again={d} kept={e}");

        // The guard, live, through the game's own handler with a test sender. The values are that
        // seat's own, so nothing moves either way.
        var conn = Fair.TestConnection(which);
        if (conn == null) { Fair.Result("hostcheat seat life live", true, "skipped: no remote player at the table"); return; }

        PlayerObjectController sender = null;
        try
        {
            var nm = Fair.Net;
            if (nm != null && nm.GamePlayers != null)
                foreach (var poc in nm.GamePlayers)
                {
                    if (poc == null) continue;
                    var owner = poc.connectionToClient;
                    if (owner != null && owner.connectionId == conn.connectionId) { sender = poc; break; }
                }
        }
        catch { }
        PlayerStats other = null;
        var m = Manager.Instance;
        if (m != null)
            foreach (var p in TableHand.Seats(m))
            {
                if (p == null) continue;
                var owner = p.connectionToClient;
                if (owner != null && owner.connectionId == conn.connectionId) continue;
                other = p;
                break;
            }
        if (sender == null || other == null) { Fair.Result("hostcheat seat life live", true, "skipped: needs a match with the player's seat and another"); return; }

        int before = _healthRefused;
        int health = other.Health;
        bool dead = other.Dead;
        Fair.AsSentBy(conn, () => sender.UserCode_SetHealthData__Int32__Boolean__GameObject(health, dead, other.gameObject));
        Fair.Result("hostcheat seat life live", _healthRefused == before + 1 && other.Health == health && other.Dead == dead,
                    $"connection {conn.connectionId} on seat {other.Slot}: refused {_healthRefused - before}, seat unchanged");
    }

    private static void Make(string what, string slotText)
    {
        int.TryParse(slotText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot);
        bool any = string.IsNullOrEmpty(slotText);
        if (!Fair.TestsAllowed) { Fair.Result("hostcheat " + what, false, "loopback harness only"); return; }
        var m = Manager.Instance;
        if (m == null) { Fair.Result("hostcheat " + what, false, "no match"); return; }

        _bypassForTest = true;
        try
        {
            foreach (var p in TableHand.Seats(m))
            {
                if (p == null || (!any && p.Slot != slot)) continue;
                switch (what)
                {
                    case "revolver":
                    {
                        var deck = p.GetComponent<DeckGameplay>();
                        if (deck == null || p.Dead) continue;
                        deck.NetworkcurrentRevolver = HostCheck.Chambers + 1;
                        Fair.Result("hostcheat revolver", deck.NetworkcurrentRevolver == HostCheck.Chambers + 1,
                                    $"seat {p.Slot} alive at {deck.NetworkcurrentRevolver} pulls; modded players should leave within {HostCheck.HeldFor + 1:0} s");
                        return;
                    }
                    case "bullets":
                    {
                        var texas = p.GetComponent<TexasGamePlay>();
                        if (texas == null) continue;
                        texas.NetworkBullets = HostCheck.MostBullets + 1;
                        Fair.Result("hostcheat bullets", texas.NetworkBullets == HostCheck.MostBullets + 1,
                                    $"seat {p.Slot} at {texas.NetworkBullets} bullets; modded players should leave within {HostCheck.HeldFor + 1:0} s");
                        return;
                    }
                    case "revive":
                        if (!p.Dead) continue;
                        p.NetworkDead = false;
                        Fair.Result("hostcheat revive", !p.Dead,
                                    $"seat {p.Slot} back in play; modded players should leave within {HostCheck.AliveFor + 1:0} s if it was out for {HostCheck.DeadFor:0} s");
                        return;
                    case "deadwinner":
                        if (!p.Dead || p.Winner) continue;
                        p.NetworkWinner = true;
                        Fair.Result("hostcheat deadwinner", p.Winner && p.Dead,
                                    $"seat {p.Slot} named winner while out; modded players should leave within {HostCheck.WinnerFor + 1:0} s");
                        return;
                }
            }
            Fair.Result("hostcheat " + what, false, any ? "no seat fits this case" : $"seat {slot} does not fit this case");
        }
        finally { _bypassForTest = false; }
    }
}
