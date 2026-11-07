using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// The tally behind the end-of-match awards: who lied best, who caught the most liars, who
/// survived the most shots, and so on, shown on every screen as the win screen opens.
///
/// <b>Counted on the host, and only there.</b> Part of what an award needs only the host ever
/// knows: whether a Liar's Dice bid or a Liar's Slots claim was a lie when it was made (the host
/// holds every die and every reel until the reveal), and whether a shot nobody else saw was
/// survived. One answer from the host also means no two screens can disagree. Each fact is
/// counted at a method that a person's key press and the test harness both reach: several of the
/// game's commands are written inline into their network wrapper, and a hook on one of those
/// would never run for a real player.
///
/// <b>A call is counted when it is settled, not when it is made.</b> A liar call can be undone
/// when somebody leaves partway through it, so it waits until the game hands somebody the gun or
/// a drink, and is forgotten if the round is reset first. Who ends up with the gun is the game's
/// own verdict, so that is what decides whether the call was right.
///
/// <b>Sent once</b>, just before the game opens its win screen, over <see cref="ModNet"/>: award,
/// seat and number, never a name. Each copy puts its own names to the seats (<see cref="AwardPanel"/>).
/// A copy without the channel, an older build at the table, gets nothing and sees the game's own
/// win screen. Nobody who has left can win one, and nobody holds more than two.
///
/// Never after a host migration (the new host saw only part of the match), never at a table that
/// is not modded (<see cref="Table.Modded"/>), never near ranked. Every hook only reads, and each
/// mode's hooks are a class of their own, so a game update that renames one costs that mode only.
/// </summary>
internal static class MatchStats
{
    /// <summary>An award, by the number that travels. Never renumber one or use a number twice.</summary>
    internal enum Award : byte
    {
        BestLiar = 1,
        WorstBluffer = 2,
        LieDetector = 3,
        CriedWolf = 4,
        IronNerves = 5,
        Luckiest = 6,
        Honest = 7,
        DevilsFavourite = 8,
        SoldOut = 9,
        Sharpshooter = 10,
        AgentOfChaos = 11,
        SpotOn = 12,
        HighRoller = 13,
        CardShark = 14,
        AllIn = 15,
        LuckyShot = 16,
        BestHand = 17,
        Coward = 18,
        Swapper = 19,
        DeadSpin = 20,
        SlotJunkie = 21,
        Executioner = 22,
    }

    /// <summary>The table an award list was worked out for, as it travels.</summary>
    internal enum Mode : byte { None = 0, Deck = 1, Devil = 2, Deck2 = 3, Chaos = 4, Dice = 5, Poker = 6, Slots = 7 }

    /// <summary>What travels: the first byte of every award message, raised if its layout ever changes.</summary>
    internal const byte Layout = 1;

    internal const int MostAwards = 5;
    internal const int MostEach = 2;

    /// <summary>How long the win screen stays up at a modded table. The game's own is ten seconds.</summary>
    private const float WinScreenSeconds = 15f;

    /// <summary>The card that matches any round card, in both deck games, and the devil card.</summary>
    private const int Joker = 4;
    private const int DevilCard = -1;

    /// <summary>The Chaos deck's own card. The round card there is only ever 1 or 2.</summary>
    private const int ChaosCard = 3;

    // ------------------------------------------------------------------ the tally

    private enum Fact
    {
        Throws, Lies, Caught, Right, Wrong, Survived, Odds, Kills, Chaos, Devil, DevilCards,
        SpotOn, HighBid, Won, AllIns, Folds, Swaps, BestHand, Saves, Spins, Executions, Bullets,
        Count,
    }

    private sealed class Seat
    {
        internal readonly int[] Count = new int[(int)Fact.Count];

        /// <summary>When each count last went up, so a tie goes to whoever got there first.</summary>
        internal readonly long[] When = new long[(int)Fact.Count];
    }

    /// <summary>A liar call waiting for the game to say who pays for it.</summary>
    private sealed class Call
    {
        internal int Caller, Accused;
        internal bool Spot, Auto;
    }

    private static readonly Dictionary<int, Seat> _seats = new();
    private static readonly Dictionary<int, int> _shotWith = new();   // a shot taken, waiting to see if it killed: seat -> bullets
    private static Call _pending;
    private static long _seq;
    private static bool _auto;
    private static Manager _for, _sentFor;

    /// <summary>True while this copy hosts a match it may give awards for.</summary>
    internal static bool Live { get; private set; }

    private static Seat Get(int seat)
    {
        if (!_seats.TryGetValue(seat, out var s)) _seats[seat] = s = new Seat();
        return s;
    }

    private static void Add(int seat, Fact fact, int by = 1)
    {
        if (seat < 0 || by == 0) return;
        var s = Get(seat);
        s.Count[(int)fact] += by;
        s.When[(int)fact] = ++_seq;
    }

    private static void Raise(int seat, Fact fact, int value)
    {
        if (seat < 0) return;
        var s = Get(seat);
        if (value <= s.Count[(int)fact]) return;
        s.Count[(int)fact] = value;
        s.When[(int)fact] = ++_seq;
    }

    private static int SeatOf(Component c)
    {
        var p = c != null ? c.GetComponent<PlayerStats>() : null;
        return p != null ? p.Slot : -1;
    }

    private static int SeatOf(GameObject go)
    {
        var p = go != null ? go.GetComponent<PlayerStats>() : null;
        return p != null ? p.Slot : -1;
    }

    // ------------------------------------------------------------------ calls

    private static void Called(int caller, int accused, bool spot)
    {
        if (caller < 0 || accused < 0 || caller == accused) { _pending = null; return; }
        _pending = new Call { Caller = caller, Accused = accused, Spot = spot, Auto = _auto };
    }

    /// <summary>
    /// The game has decided: <paramref name="paying"/> takes the gun or the drink. The accused
    /// paying means the call was right; the caller paying means it was wrong. A call the game made
    /// by itself after the last card was thrown is nobody's, so only the accused's side counts.
    /// </summary>
    private static void Settled(int paying)
    {
        var call = _pending;
        _pending = null;
        if (call == null || paying < 0) return;

        if (paying == call.Accused)
        {
            Add(call.Accused, Fact.Caught);
            if (!call.Auto) Add(call.Caller, Fact.Right);
        }
        else if (paying == call.Caller && !call.Auto)
            Add(call.Caller, Fact.Wrong);
    }

    // ------------------------------------------------------------------ the match

    private static void Begin(Manager m)
    {
        Clear();
        _for = m;

        var nm = NetworkManager.singleton != null ? NetworkManager.singleton.TryCast<CustomNetworkManager>() : null;
        string why = !NetworkServer.active ? "not the host"
                   : RankedGuard.Near || nm == null || nm.Matchmaking ? "ranked"
                   : nm.isVelvetRoom ? "not a table"
                   : MigratedOnce() || m.migrated ? "a migrated table"
                   : !Table.Modded ? "not a modded table"
                   : null;

        Live = why == null;
        Plugin.Log.LogInfo($"[awards] match started - {(Live ? "keeping the tally" : $"no awards ({why})")}");
    }

    private static void Clear()
    {
        _seats.Clear();
        _shotWith.Clear();
        _pending = null;
        _auto = false;
        _seq = 0;
        Live = false;
        _for = null;
    }

    private static bool MigratedOnce()
    {
        try
        {
            var hm = HostMigration.Instance;
            return hm != null && hm.migratedonce;
        }
        catch { return false; }
    }

    /// <summary>From <see cref="ModTicker"/>: forget a match that is over, and stop counting one a migration has broken.</summary>
    internal static void Tick()
    {
        var m = Manager.Instance;
        if (m == null)
        {
            if (_for != null || _sentFor != null) { Clear(); _sentFor = null; }
            return;
        }

        if (!Live) return;
        var nm = NetworkManager.singleton != null ? NetworkManager.singleton.TryCast<CustomNetworkManager>() : null;
        if (!ReferenceEquals(m, _for) || m.migrated || MigratedOnce() || (nm != null && nm.MigrationStarted) || !NetworkServer.active)
        {
            Live = false;
            Plugin.Log.LogInfo("[awards] the host changed or the match moved on - no awards this match");
        }
    }

    /// <summary>Developer test switch: behave as after a host migration.</summary>
    internal static void Taint()
    {
        Live = false;
        Plugin.Log.LogInfo("[awards] tally switched off for this match (test switch)");
    }

    /// <summary>
    /// The moment before the win screen. The longer win screen first, so a failure below cannot
    /// cost it; then the awards, once.
    /// </summary>
    private static void Finish(Manager m)
    {
        if (!NetworkServer.active || m == null || ReferenceEquals(m, _sentFor)) return;
        _sentFor = m;

        var nm = NetworkManager.singleton != null ? NetworkManager.singleton.TryCast<CustomNetworkManager>() : null;
        bool modded = Table.Modded && !RankedGuard.Near && nm != null && !nm.Matchmaking && !nm.isVelvetRoom;
        if (modded && !m.migrated && !MigratedOnce())
            m.NetworkCountDownLobby = WinScreenSeconds;

        if (!Live) { Plugin.Log.LogInfo("[awards] none this match"); return; }
        Live = false;
        if (!modded) { Plugin.Log.LogInfo("[awards] none: the table stopped counting as modded"); return; }

        SettleShots();
        var mode = ModeOf(m);
        var picked = Pick(mode);

        if (picked.Count == 0)
        {
            Plugin.Log.LogInfo($"[awards] nothing in this {mode} match earned one");
            return;
        }

        var body = new ModWriter().U8(Layout).U8((byte)mode).U8((byte)picked.Count);
        foreach (var (id, seat, value) in picked) body.U8((byte)id).U8((byte)seat).I32(value);

        bool sent = ModNet.SendToAll(ModKind.Awards, body);
        Plugin.Log.LogInfo($"[awards] {(sent ? "sent" : "NOT sent (no channel)")} for {mode}, {Describe(picked)}");
    }

    internal static string Describe(List<(Award Id, int Seat, int Value)> list)
    {
        var sb = new StringBuilder();
        sb.Append(list.Count);
        sb.Append(':');
        foreach (var (id, seat, value) in list) sb.Append($" {id} seat {seat} = {value};");
        return sb.ToString();
    }

    private static Mode ModeOf(Manager m)
    {
        switch (TableHand.Playing())
        {
            case TableHand.Kind.Deck:
                var d = m.DeckGamePlayManager;
                if (d == null) return Mode.Deck;
                return d.DeckMode == DeckGamePlayManager.deckmode.Devil ? Mode.Devil
                     : d.DeckMode == DeckGamePlayManager.deckmode.Deck2 ? Mode.Deck2 : Mode.Deck;
            case TableHand.Kind.ChaosDeck: return Mode.Chaos;
            case TableHand.Kind.Dice:      return Mode.Dice;
            case TableHand.Kind.Texas:     return Mode.Poker;
            case TableHand.Kind.Spin:      return Mode.Slots;
            default:                       return Mode.None;
        }
    }

    // ------------------------------------------------------------------ choosing

    private sealed class Rule
    {
        internal Award Id;
        internal Func<Seat, int> Score;
        internal Func<Seat, long> When;
        internal int Least = 1;
        internal Func<Seat, int> Shown;
    }

    private static Rule Most(Award id, Fact fact, int least = 1) => new()
    {
        Id = id, Least = least,
        Score = s => s.Count[(int)fact],
        When = s => s.When[(int)fact],
        Shown = s => s.Count[(int)fact],
    };

    private static readonly Rule BestLiar = new()
    {
        Id = Award.BestLiar,
        Score = s => s.Count[(int)Fact.Lies] - s.Count[(int)Fact.Caught],
        When = s => s.When[(int)Fact.Lies],
        Shown = s => s.Count[(int)Fact.Lies] - s.Count[(int)Fact.Caught],
    };

    /// <summary>The worst odds a seat pulled the trigger at and lived: 1 in 6 on the first pull, 1 in 2 on the fifth.</summary>
    private static readonly Rule Luckiest = new()
    {
        Id = Award.Luckiest, Least = 2,
        Score = s => s.Count[(int)Fact.Odds],
        When = s => s.When[(int)Fact.Odds],
        Shown = s => 7 - s.Count[(int)Fact.Odds],
    };

    private static readonly Rule Honest = new()
    {
        Id = Award.Honest, Least = 3,
        Score = s => s.Count[(int)Fact.Lies] == 0 ? s.Count[(int)Fact.Throws] : 0,
        When = s => s.When[(int)Fact.Throws],
        Shown = s => s.Count[(int)Fact.Throws],
    };

    private static readonly Rule BestHand = new()
    {
        Id = Award.BestHand, Least = 3,
        Score = s => s.Count[(int)Fact.BestHand],
        When = s => s.When[(int)Fact.BestHand],
        Shown = s => s.Count[(int)Fact.BestHand] - 1,
    };

    /// <summary>Each table's awards, in the order they are given out. The first five that anybody earned are shown.</summary>
    private static List<Rule> RulesFor(Mode mode)
    {
        var caught = Most(Award.WorstBluffer, Fact.Caught);
        var right = Most(Award.LieDetector, Fact.Right);
        var wrong = Most(Award.CriedWolf, Fact.Wrong);
        var survived = Most(Award.IronNerves, Fact.Survived);

        switch (mode)
        {
            case Mode.Deck:
            case Mode.Deck2:
                return new() { BestLiar, right, survived, Luckiest, caught, wrong, Honest };
            case Mode.Devil:
                return new() { BestLiar, right, Most(Award.DevilsFavourite, Fact.Devil), Most(Award.SoldOut, Fact.DevilCards),
                               survived, Luckiest, caught, wrong, Honest };
            case Mode.Chaos:
                return new() { BestLiar, right, Most(Award.Sharpshooter, Fact.Kills), Most(Award.AgentOfChaos, Fact.Chaos),
                               survived, caught, wrong, Honest };
            case Mode.Dice:
                return new() { BestLiar, right, Most(Award.SpotOn, Fact.SpotOn), Most(Award.HighRoller, Fact.HighBid, 5), caught, wrong };
            case Mode.Poker:
                return new() { Most(Award.CardShark, Fact.Won), BestHand, Most(Award.AllIn, Fact.AllIns),
                               Most(Award.LuckyShot, Fact.Bullets, 4), Most(Award.Swapper, Fact.Swaps, 2), Most(Award.Coward, Fact.Folds, 2) };
            case Mode.Slots:
                return new() { BestLiar, right, Most(Award.DeadSpin, Fact.Saves), Most(Award.Executioner, Fact.Executions),
                               Most(Award.SlotJunkie, Fact.Spins, 3), caught, wrong };
            default:
                return new();
        }
    }

    /// <summary>
    /// The awards, in order: for each one, the best of everybody still at the table who does not
    /// hold two already, a tie going to whoever got there first. Nobody who has left can win.
    /// </summary>
    private static List<(Award Id, int Seat, int Value)> Pick(Mode mode)
    {
        var picked = new List<(Award, int, int)>();
        var here = new HashSet<int>();
        foreach (var p in UnityEngine.Object.FindObjectsOfType<PlayerStats>())
            if (p != null) here.Add(p.Slot);

        var held = new Dictionary<int, int>();
        foreach (var rule in RulesFor(mode))
        {
            if (picked.Count >= MostAwards) break;

            int best = -1, bestScore = 0;
            long bestWhen = 0;
            foreach (var pair in _seats)
            {
                if (!here.Contains(pair.Key) || pair.Key > byte.MaxValue) continue;
                if (held.TryGetValue(pair.Key, out int has) && has >= MostEach) continue;

                int score = rule.Score(pair.Value);
                if (score < rule.Least) continue;
                long when = rule.When(pair.Value);
                if (best >= 0 && (score < bestScore || (score == bestScore && when >= bestWhen))) continue;

                best = pair.Key;
                bestScore = score;
                bestWhen = when;
            }

            if (best < 0) continue;
            picked.Add((rule.Id, best, rule.Shown(_seats[best])));
            held[best] = held.TryGetValue(best, out int had) ? had + 1 : 1;
        }
        return picked;
    }

    /// <summary>Shots taken that did not kill by the time it matters were survived.</summary>
    private static void SettleShots()
    {
        if (_shotWith.Count == 0) return;
        var alive = new HashSet<int>();
        foreach (var p in UnityEngine.Object.FindObjectsOfType<PlayerStats>())
            if (p != null && !p.Dead) alive.Add(p.Slot);

        foreach (var pair in _shotWith)
        {
            if (!alive.Contains(pair.Key)) continue;
            Add(pair.Key, Fact.Survived);
            if (pair.Value > 0) Raise(pair.Key, Fact.Bullets, pair.Value);
        }
        _shotWith.Clear();
    }

    private static int _complaints;

    private static void Fail(string what, Exception e)
    {
        if (_complaints >= 10) return;
        _complaints++;
        Plugin.Log.LogWarning($"[awards] could not count {what}: {e.Message}");
    }

    // ------------------------------------------------------------------ the hooks

    /// <summary>The start and end of every match, and a liar call undone by somebody leaving.</summary>
    [HarmonyPatch]
    internal static class CoreHooks
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Manager), nameof(Manager.StartGame))]
        private static void Started(Manager __instance)
        {
            try { if (NetworkServer.active) Begin(__instance); }
            catch (Exception e) { Live = false; Fail("the start of the match", e); }
        }

        /// <summary>
        /// Every peer's win screen follows this call, and it has two callers: the end of an ordinary
        /// match and the one after a migration. It always runs: this only adds to it.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Manager), nameof(Manager.EndGameRpc))]
        private static void Ending(Manager __instance)
        {
            try { Finish(__instance); }
            catch (Exception e) { Fail("the end of the match", e); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.AbortLiarResolveAndResetRound))]
        private static void Undone() => _pending = null;
    }

    /// <summary>Liar's Deck: Basic, Devil and Deck 2 are one game with one set of methods.</summary>
    [HarmonyPatch]
    internal static class DeckHooks
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.ServerThrowCards))]
        private static void Threw(DeckGameplay __instance, Il2CppSystem.Collections.Generic.List<int> types, bool __runOriginal)
        {
            if (!__runOriginal || !Live || types == null) return;
            try
            {
                var d = Manager.Instance != null ? Manager.Instance.DeckGamePlayManager : null;
                if (d == null) return;

                int seat = SeatOf(__instance), round = d.RoundCard;
                bool lied = false;
                int devils = 0;
                for (int i = 0; i < types.Count; i++)
                {
                    int c = types[i];
                    if (c == DevilCard) devils++;
                    else if (c != round && c != Joker) lied = true;
                }

                Add(seat, Fact.Throws);
                if (lied) Add(seat, Fact.Lies);
                Add(seat, Fact.DevilCards, devils);
            }
            catch (Exception e) { Fail("a Liar's Deck throw", e); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.TryAutoCallLiarAfterFinalThrow))]
        private static void AutoStarts() => _auto = true;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.TryAutoCallLiarAfterFinalThrow))]
        private static void AutoEnds() => _auto = false;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.CallLiar))]
        private static void Call(DeckGamePlayManager __instance, GameObject callingPlayer)
        {
            if (!Live) return;
            try { Called(SeatOf(callingPlayer), SeatOf(__instance.LastBetPlayer), false); }
            catch (Exception e) { Fail("a Liar's Deck call", e); }
        }

        /// <summary>
        /// A liar call's shot is the one with the voice line: the bidder's if they lied, the
        /// caller's if not. The devil card's volley at everyone else has none, and settles nothing.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.StartRevolverProcesses))]
        private static void Shot(DeckGameplay __instance, bool playVoice)
        {
            if (!Live) return;
            try
            {
                if (playVoice) Settled(SeatOf(__instance));
                else _pending = null;
            }
            catch (Exception e) { Fail("a Liar's Deck shot", e); }
        }

        /// <summary>The devil spares this seat and takes the table instead.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.StartDevilProcesses))]
        private static void Devil(DeckGameplay __instance)
        {
            if (!Live) return;
            try
            {
                _pending = null;
                Add(SeatOf(__instance), Fact.Devil);
            }
            catch (Exception e) { Fail("the devil", e); }
        }

        /// <summary>
        /// The end of a pull: false is survived. By then the game has already counted this pull,
        /// so the pulls survived before it are one fewer, and the odds were 1 in six less those.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.RpcSetDeadState))]
        private static void Pulled(DeckGameplay __instance, bool value)
        {
            if (!Live || value) return;
            try
            {
                int seat = SeatOf(__instance);
                int before = Math.Max(0, __instance.CurrentRevolver - 1);
                Add(seat, Fact.Survived);
                Raise(seat, Fact.Odds, Math.Min(before, 5) + 1);
            }
            catch (Exception e) { Fail("a Liar's Deck pull", e); }
        }
    }

    /// <summary>The Chaos deck: its own manager and its own gun, aimed by whoever is handed it.</summary>
    [HarmonyPatch]
    internal static class ChaosHooks
    {
        /// <summary>
        /// A throw with the chaos card in it is no claim: the game turns it face up for the table
        /// and hands the thrower the gun, and nobody can call it. So it is never a lie, which would
        /// stay "never caught" for good. Its chaos is counted when the gun is handed over.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.ServerThrowCards))]
        private static void Threw(ChaosDeckGameplay __instance, Il2CppSystem.Collections.Generic.List<int> types, bool __runOriginal)
        {
            if (!__runOriginal || !Live || types == null) return;
            try
            {
                var c = Manager.Instance != null ? Manager.Instance.ChaosDeckGame : null;
                if (c == null) return;

                int seat = SeatOf(__instance), round = c.RoundCard;
                bool lied = false, chaos = false;
                for (int i = 0; i < types.Count; i++)
                {
                    int t = types[i];
                    if (t == ChaosCard) chaos = true;
                    else if (t != round && t != Joker) lied = true;
                }

                Add(seat, Fact.Throws);
                if (lied && !chaos) Add(seat, Fact.Lies);
            }
            catch (Exception e) { Fail("a Chaos deck throw", e); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChaosDeckGamePlayManager), nameof(ChaosDeckGamePlayManager.TryAutoCallLiarAfterFinalThrow))]
        private static void AutoStarts() => _auto = true;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ChaosDeckGamePlayManager), nameof(ChaosDeckGamePlayManager.TryAutoCallLiarAfterFinalThrow))]
        private static void AutoEnds() => _auto = false;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChaosDeckGamePlayManager), nameof(ChaosDeckGamePlayManager.CallLiar))]
        private static void Call(ChaosDeckGamePlayManager __instance, GameObject callingPlayer)
        {
            if (!Live) return;
            try
            {
                SettleShots();
                Called(SeatOf(callingPlayer), SeatOf(__instance.LastBetPlayer), false);
            }
            catch (Exception e) { Fail("a Chaos deck call", e); }
        }

        /// <summary>
        /// The gun is handed over. To the caller: the lie was caught. To the bidder: they held the
        /// card that beats a call, which the game checks first, so the call was wrong.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.StartMasterProcesses))]
        private static void Armed(ChaosDeckGameplay __instance)
        {
            if (!Live || _pending == null) return;
            try
            {
                var call = _pending;
                int seat = SeatOf(__instance);
                if (seat == call.Caller) Settled(call.Accused);
                else if (seat == call.Accused) Settled(call.Caller);
                else _pending = null;
            }
            catch (Exception e) { Fail("a Chaos deck gun", e); }
        }

        /// <summary>A wrong call: the caller turns the gun on themselves. Whether they lived is seen later.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.StartRevolverProcesses))]
        private static void Shot(ChaosDeckGameplay __instance)
        {
            if (!Live) return;
            try
            {
                int seat = SeatOf(__instance);
                if (_pending != null && seat == _pending.Caller) Settled(seat);
                if (seat >= 0) _shotWith[seat] = 0;
            }
            catch (Exception e) { Fail("a Chaos deck shot", e); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.ServerBeDeadFromRevolver))]
        private static void Died(ChaosDeckGameplay __instance)
        {
            if (!Live) return;
            try { _shotWith.Remove(SeatOf(__instance)); }
            catch (Exception e) { Fail("a Chaos deck death", e); }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.ServerApplyShotToTarget))]
        private static void Hit(ChaosDeckGameplay __instance, PlayerStats player, bool __result)
        {
            if (!Live || !__result) return;
            try
            {
                int shooter = SeatOf(__instance);
                if (player != null && player.Slot != shooter) Add(shooter, Fact.Kills);
            }
            catch (Exception e) { Fail("a Chaos deck kill", e); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChaosDeckGamePlayManager), nameof(ChaosDeckGamePlayManager.BeginChaosFromThrow))]
        private static void Chaos(GameObject thrower)
        {
            if (!Live) return;
            try { Add(SeatOf(thrower), Fact.Chaos); }
            catch (Exception e) { Fail("a chaos card", e); }
        }
    }

    /// <summary>Liar's Dice: bids, liar and spot-on calls, and who drinks for them.</summary>
    [HarmonyPatch]
    internal static class DiceHooks
    {
        /// <summary>Whether the bid is more than the table holds, decided before it lands.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DiceGamePlay), nameof(DiceGamePlay.UserCode_PlaceBet__Int32__Int32))]
        private static void Bidding(int count, int dice, out bool __state, bool __runOriginal)
        {
            __state = false;
            if (!__runOriginal || !Live) return;
            try
            {
                var d = Manager.Instance != null ? Manager.Instance.DiceGame : null;
                __state = d != null && count > d.CalculateAllDice(dice);
            }
            catch (Exception e) { Fail("a Liar's Dice bid", e); }
        }

        /// <summary>Counted only if it became the standing bid.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(DiceGamePlay), nameof(DiceGamePlay.UserCode_PlaceBet__Int32__Int32))]
        private static void Bid(DiceGamePlay __instance, int count, int dice, bool __state, bool __runOriginal)
        {
            if (!__runOriginal || !Live) return;
            try
            {
                var d = Manager.Instance != null ? Manager.Instance.DiceGame : null;
                int seat = SeatOf(__instance);
                if (d == null || d.LastCount != count || d.LastDice != dice || d.LastBidPlayer == null || d.LastBidPlayer.Slot != seat) return;

                Add(seat, Fact.Throws);
                if (__state) Add(seat, Fact.Lies);
                Raise(seat, Fact.HighBid, count);
            }
            catch (Exception e) { Fail("a Liar's Dice bid", e); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DiceGamePlayManager), nameof(DiceGamePlayManager.CallLier))]
        private static void Liar(DiceGamePlayManager __instance, PlayerStats callingplayer)
        {
            if (!Live) return;
            try { Called(callingplayer != null ? callingplayer.Slot : -1, __instance.LastBidPlayer != null ? __instance.LastBidPlayer.Slot : -1, false); }
            catch (Exception e) { Fail("a Liar's Dice call", e); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DiceGamePlayManager), nameof(DiceGamePlayManager.CallSpotOn))]
        private static void Spot(DiceGamePlayManager __instance, PlayerStats callingplayer)
        {
            if (!Live) return;
            try { Called(callingplayer != null ? callingplayer.Slot : -1, __instance.LastBidPlayer != null ? __instance.LastBidPlayer.Slot : -1, true); }
            catch (Exception e) { Fail("a Liar's Dice spot-on", e); }
        }

        /// <summary>
        /// Somebody drinks. After a liar call it is the bidder if the bid was too high, else the
        /// caller. After a spot-on it is the caller if the count was off; anybody else drinking
        /// means the count was exact.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DiceGamePlayManager), nameof(DiceGamePlayManager.ReduceDice))]
        private static void Drinks(PlayerStats player)
        {
            if (!Live || _pending == null || player == null) return;
            try
            {
                var call = _pending;
                if (!call.Spot) { Settled(player.Slot); return; }

                _pending = null;
                if (player.Slot == call.Caller) Add(call.Caller, Fact.Wrong);
                else Add(call.Caller, Fact.SpotOn);
            }
            catch (Exception e) { Fail("a Liar's Dice drink", e); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DiceGamePlayManager), nameof(DiceGamePlayManager.StopRevealFlowAndRecoverRound))]
        private static void Recovered() => _pending = null;
    }

    /// <summary>Liar's Poker, which the lobby calls Texas: hands, bullets, folds and swaps.</summary>
    [HarmonyPatch]
    internal static class PokerHooks
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.UserCode_CMDRised__Boolean))]
        private static void Raised(TexasGamePlay __instance, bool AllInN, bool __runOriginal)
        {
            if (!__runOriginal || !Live || !AllInN) return;
            try { Add(SeatOf(__instance), Fact.AllIns); }
            catch (Exception e) { Fail("a Liar's Poker all-in", e); }
        }

        /// <summary>
        /// A fold is a shot at your own bullets, and a fold that kills marks the seat dead before the
        /// fold is counted. So a seat already dead here folded into the shot that killed it: a fold,
        /// nothing survived.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.SetLastTurn))]
        private static void Folded(TexasGamePlay __instance, bool fold)
        {
            if (!Live || !fold) return;
            try
            {
                int seat = SeatOf(__instance);
                Add(seat, Fact.Folds);

                var p = __instance.GetComponent<PlayerStats>();
                if (p == null || p.Dead) return;
                Add(seat, Fact.Survived);
                Raise(seat, Fact.Bullets, __instance.Bullets);
            }
            catch (Exception e) { Fail("a Liar's Poker fold", e); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.UserCode_SwitchCmd__Int32__Int32))]
        private static void Swapped(TexasGamePlay __instance)
        {
            if (!Live) return;
            try { Add(SeatOf(__instance), Fact.Swaps); }
            catch (Exception e) { Fail("a Liar's Poker swap", e); }
        }

        /// <summary>A showdown loser takes a shot. Whether it killed is known by the next showdown, or the end.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.Revolver))]
        private static void Shot(TexasGamePlay __instance)
        {
            if (!Live) return;
            try
            {
                int seat = SeatOf(__instance);
                if (seat >= 0) _shotWith[seat] = __instance.Bullets;
            }
            catch (Exception e) { Fail("a Liar's Poker shot", e); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(TexasGamePlayManager), nameof(TexasGamePlayManager.SetVersusMenu))]
        private static void Showdown(GameObject player)
        {
            if (!Live) return;
            try
            {
                SettleShots();
                var hand = player != null ? player.GetComponent<TexasGamePlay>() : null;
                if (hand != null) Raise(SeatOf(player), Fact.BestHand, (int)hand.Rank + 1);
            }
            catch (Exception e) { Fail("a Liar's Poker showdown", e); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(TexasGamePlayManager), nameof(TexasGamePlayManager.SetWinnersMenu))]
        private static void Won(PlayerStats winner, Il2CppSystem.Collections.Generic.List<PlayerStats> draws)
        {
            if (!Live) return;
            try
            {
                if (winner != null) Add(winner.Slot, Fact.Won);
                if (draws == null) return;
                for (int i = 0; i < draws.Count; i++)
                    if (draws[i] != null && (winner == null || draws[i].Slot != winner.Slot)) Add(draws[i].Slot, Fact.Won);
            }
            catch (Exception e) { Fail("a Liar's Poker hand", e); }
        }
    }

    /// <summary>Liar's Slots, which the lobby calls Spin: spins, claims, calls, dead spins and executions.</summary>
    [HarmonyPatch]
    internal static class SlotsHooks
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SpinGamePlay), nameof(SpinGamePlay.UserCode_Cmd_RequestSecretSpin))]
        private static void Spun(SpinGamePlay __instance) => Spin(__instance);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SpinGamePlay), nameof(SpinGamePlay.UserCode_Cmd_RequestDoubleSpin))]
        private static void SpunTwice(SpinGamePlay __instance) => Spin(__instance);

        /// <summary>A spin out of turn does nothing in the game, so it counts for nothing here.</summary>
        private static void Spin(SpinGamePlay gp)
        {
            if (!Live) return;
            try
            {
                var p = gp != null ? gp.GetComponent<PlayerStats>() : null;
                if (p != null && p.HaveTurn) Add(p.Slot, Fact.Spins);
            }
            catch (Exception e) { Fail("a Liar's Slots spin", e); }
        }

        /// <summary>Whether the claim is more than the reels show, decided before anybody can call it.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SpinGamePlay), nameof(SpinGamePlay.UserCode_RiseCmd__Int32))]
        private static void Claimed(SpinGamePlay __instance, int count, bool __runOriginal)
        {
            if (!__runOriginal || !Live) return;
            try
            {
                int seat = SeatOf(__instance);
                Add(seat, Fact.Throws);
                if (count > __instance.CorrectSlotCount()) Add(seat, Fact.Lies);
            }
            catch (Exception e) { Fail("a Liar's Slots claim", e); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(LiarsSpinGameplayManager), nameof(LiarsSpinGameplayManager.CallLiar))]
        private static void Call(LiarsSpinGameplayManager __instance, GameObject callingplayer)
        {
            if (!Live) return;
            try { Called(SeatOf(callingplayer), SeatOf(__instance.LastBetPlayer), false); }
            catch (Exception e) { Fail("a Liar's Slots call", e); }
        }

        /// <summary>The game's verdict on a call: true, the claim was a lie and the claimant spins for it.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(LiarsSpinGameplayManager), nameof(LiarsSpinGameplayManager.DoSpinRoulette))]
        private static void Verdict(bool isLie)
        {
            if (!Live || _pending == null) return;
            try { Settled(isLie ? _pending.Accused : _pending.Caller); }
            catch (Exception e) { Fail("a Liar's Slots verdict", e); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SpinGamePlay), nameof(SpinGamePlay.RpcPlaySaveAnim))]
        private static void Saved(SpinGamePlay __instance)
        {
            if (!Live) return;
            try { Add(SeatOf(__instance), Fact.Saves); }
            catch (Exception e) { Fail("a dead spin survived", e); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SpinGamePlay), nameof(SpinGamePlay.UserCode_KillCmd))]
        private static void Executed(SpinGamePlay __instance, bool __runOriginal)
        {
            if (!__runOriginal || !Live) return;
            try { Add(SeatOf(__instance), Fact.Executions); }
            catch (Exception e) { Fail("an execution", e); }
        }
    }

    // ------------------------------------------------------------------ for the test driver

    /// <summary>Every seat's counts, by seat number. No names: there are none here to give.</summary>
    internal static string Tally()
    {
        if (_seats.Count == 0) return $"live={Live}, nothing counted";
        var sb = new StringBuilder($"live={Live}");
        foreach (var pair in _seats)
        {
            sb.Append($" | seat {pair.Key}:");
            for (int f = 0; f < (int)Fact.Count; f++)
                if (pair.Value.Count[f] != 0) sb.Append($" {(Fact)f}={pair.Value.Count[f]}");
        }
        if (_pending != null) sb.Append($" | call pending {_pending.Caller}->{_pending.Accused}");
        return sb.ToString();
    }
}
