using System;
using System.Collections.Generic;
using HarmonyLib;
using Mirror;
using Mirror.RemoteCalls;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// A bot's seat at the table: a real networked object that nobody owns, and the host answering
/// for the machine the seat does not have.
///
/// The game seats a player with <c>NetworkServer.Spawn(seat, lobbyPlayer)</c>, which hands the
/// seat to the lobby player's connection. A bot has no connection, and that overload refuses an
/// owner without one: it logs "Player object is not a player" and returns, leaving the seat a
/// plain copy on the host with no network id. Nothing of it reached a client, so clients saw an
/// empty chair, and every server path in the game, which first asks whether it is running on the
/// server, said no for that seat and did nothing. That one refusal was behind the dice reveal
/// missing the bots' dice, the shake and drink errors, and the shots that never fired.
///
/// So a bot's seat is spawned with no owner instead, which Mirror supports in full: the seat gets
/// a network id, every client spawns the prefab it spawns for a person, the server paths run, and
/// the seat's animator, which normally follows its owner, follows the server when there is none.
///
/// What such a seat still lacks is the conversation an owner has with the server. Commands from
/// the seat are refused for want of authority, and target calls to it have no connection to go
/// to. Both are caught where Mirror sends them and run on the host: a command as the server runs
/// one, a target call as the seat's owner would, by the table in <see cref="Rules"/>. A target
/// call the table does not name is dropped, so a game update fails quietly instead of opening
/// somebody's own screen on the host.
///
/// None of this asks for developer mode: bots are for players. With no bot at the table, both of
/// the call patches return at their first line.
/// </summary>
internal static class BotSeats
{
    /// <summary>One bot's seat in the current match.</summary>
    internal sealed class Seat
    {
        internal NetworkIdentity Identity;
        internal PlayerStats Player;
        internal CharController Body;
        internal DiceGamePlay Dice;
        internal DeckGameplay Deck;
        internal ChaosDeckGameplay Chaos;

        /// <summary>Set once the seat's animator runs whether or not the host's camera sees it.</summary>
        internal bool Animating;

        /// <summary>Set once its win has been said.</summary>
        internal bool Won;

        /// <summary>
        /// Liar's Deck and the Chaos deck: the brain wants the seat's cards raised, as a person holds
        /// the look key while choosing. <see cref="BotPuppet"/> raises them.
        /// </summary>
        internal bool Looking;

        /// <summary>For <see cref="BotTurnGuard"/>: its turn's clock has been seen running, outside a reveal.</summary>
        internal bool ClockRan;

        /// <summary>
        /// For <see cref="BotTurnGuard"/>: since when it has held the turn with its clock under a
        /// second, whatever is being settled; -1 when it has not.
        /// </summary>
        internal float LowSince = -1f;

        /// <summary>
        /// The Chaos deck: when the server last fired this seat's gun, until <see cref="BotPuppet"/>
        /// takes the gun down again; -1 when it is not waiting to.
        /// </summary>
        internal float FiredAt = -1f;
    }

    private static readonly Dictionary<uint, Seat> _seats = new();
    private static int _match;

    /// <summary>Whether the spawn patch went on; without it no bot may be added.</summary>
    internal static bool SpawnOn { get; set; }

    /// <summary>Whether the command and target patches went on.</summary>
    internal static bool CallsOn { get; set; }

    /// <summary>Both: a bot can be seated and answered for. Asked before any bot is added.</summary>
    internal static bool Works => SpawnOn && CallsOn;

    /// <summary>Whether the throw events went on; without them no bot sits at a Liar's Deck or Chaos table.</summary>
    internal static bool ThrowsOn { get; set; }

    /// <summary>Any bot seated at this match. The cheap first test every patch here makes.</summary>
    internal static bool Any => _seats.Count > 0;

    /// <summary>The bots' seats this match, for the brain and the stand-in.</summary>
    internal static List<Seat> Seated()
    {
        var found = new List<Seat>(_seats.Count);
        foreach (var s in _seats.Values)
            if (s.Identity != null && s.Player != null) found.Add(s);
        return found;
    }

    /// <summary>Is this part of a bot's seat? Only the host knows; a client asks <c>BotManager.IsBot</c>.</summary>
    internal static bool Is(NetworkBehaviour part)
    {
        if (_seats.Count == 0 || part == null) return false;
        try { return _seats.ContainsKey(part.netId); }
        catch { return false; }
    }

    // What this match's bots have had done for them, for one line when it ends.
    private static int _commands, _targets, _dropped, _dealsKept;
    private static readonly HashSet<string> _named = new(StringComparer.Ordinal);

    /// <summary>
    /// From <see cref="ModTicker"/>: a new match starts with no bot seats, and the last one's go
    /// with it, along with one line saying what the host did for them.
    /// </summary>
    internal static void Tick()
    {
        Events.Settle();

        var m = Manager.Instance;
        int id = m != null ? m.GetInstanceID() : 0;
        if (id == _match) return;

        if (_seats.Count > 0)
            Plugin.Log.LogInfo($"[bot] the match is over: the host ran {_commands} command(s) and {_targets} target call(s) " +
                               $"for {_seats.Count} bot seat(s), and dropped {_dropped}, {_dealsKept} of them a bot's dice " +
                               "that nobody's screen is meant to show");
        _match = id;
        DropSeats();
        _named.Clear();
        _commands = _targets = _dropped = _dealsKept = 0;
    }

    /// <summary>The last match's seats let go.</summary>
    private static void DropSeats() => _seats.Clear();

    private static void Register(NetworkIdentity identity)
    {
        var m = Manager.Instance;
        int id = m != null ? m.GetInstanceID() : 0;
        if (id != _match) { _match = id; DropSeats(); }

        var seat = new Seat
        {
            Identity = identity,
            Player = identity.GetComponent<PlayerStats>(),
            Body = identity.GetComponent<CharController>(),
            Dice = identity.GetComponent<DiceGamePlay>(),
            Deck = identity.GetComponent<DeckGameplay>(),
            Chaos = identity.GetComponent<ChaosDeckGameplay>(),
        };
        _seats[identity.netId] = seat;
        Plugin.Log.LogInfo($"[bot] a bot's seat is on the network, nobody's to own ({_seats.Count} this match)");
    }

    // ------------------------------------------------------------------ the seat

    /// <summary>
    /// The seat goes on the network with no owner. Both of the game's calls to this overload pass
    /// the lobby player whose seat it is; a person's goes on exactly as before.
    /// </summary>
    [HarmonyPatch]
    internal static class Spawn
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(NetworkServer), nameof(NetworkServer.Spawn), new[] { typeof(GameObject), typeof(GameObject) })]
        private static bool Ownerless(GameObject obj, GameObject ownerPlayer)
        {
            try
            {
                if (obj == null || ownerPlayer == null) return true;
                var lobby = ownerPlayer.GetComponent<PlayerObjectController>();
                if (!BotManager.IsBot(lobby)) return true;

                var identity = obj.GetComponent<NetworkIdentity>();
                if (identity == null) return true;

                NetworkServer.Spawn(obj, (NetworkConnection)null);
                Register(identity);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[bot] a bot's seat could not go on the network: {e.Message}");
                return true;
            }
        }
    }

    /// <summary>Whether the spawn patch is really in place, asked of Harmony after it was applied.</summary>
    internal static bool SpawnPatched()
    {
        try
        {
            var target = AccessTools.Method(typeof(NetworkServer), nameof(NetworkServer.Spawn),
                                            new[] { typeof(GameObject), typeof(GameObject) });
            var info = target != null ? Harmony.GetPatchInfo(target) : null;
            if (info == null) return false;
            foreach (var p in info.Prefixes)
                if (p.owner == Plugin.Guid) return true;
        }
        catch { }
        return false;
    }

    // ------------------------------------------------------------------ its calls

    /// <summary>What becomes of a target call meant for a seat that has no owner.</summary>
    private enum Rule { Unknown, Run, Drop }

    /// <summary>
    /// Every target call a bot's seat can be sent, by class and method as Mirror names them. Run:
    /// the body is the seat's own life (its animator, its counts, its answer to the server), and the
    /// host runs it as the seat's owner. Drop: the body only opens the owner's screen or repeats what
    /// the server already holds, and run on the host it would put a bot's secret on the host's
    /// screen. The dice, Deck and Chaos rows are read from the game's own bodies; the Texas and
    /// Slots rows are the design's and wait for those brains, which refuse bots until then.
    /// </summary>
    private static readonly Dictionary<string, Rule> Rules = new(StringComparer.Ordinal)
    {
        // Liar's Dice: the drink and the death that follows it. The dice are dealt by the server and
        // stay there: sent on, they would show the host a bot's dice as if the host sat there.
        ["DiceGamePlayManager::ReduceDice"] = Rule.Run,
        ["DiceGamePlayManager::OfDrink"] = Rule.Run,
        ["DiceGamePlay::TargetSetDiceValues"] = Rule.Drop,

        // Liar's Deck and the Chaos deck.
        ["ChaosDeckGameplay::ChangeHaveCards"] = Rule.Run,
        ["ChaosDeckGameplay::SetDeadAnim"] = Rule.Run,
        ["ChaosDeckGamePlayManager::ReloadRevolver"] = Rule.Run,
        ["DeckGameplay::SetHaveCardsTarget"] = Rule.Drop,
        ["ChaosDeckGameplay::SetHaveCardsTarget"] = Rule.Drop,
        ["DeckGamePlayManager::RpcDevilsDealRequirementWarning"] = Rule.Drop,

        // Liar's Poker.
        ["TexasGamePlay::RandomCardsTargetRPC"] = Rule.Run,
        ["TexasGamePlay::SetCardsRpc"] = Rule.Run,
        ["TexasGamePlay::RevealCard"] = Rule.Run,
        ["TexasGamePlay::Revolver"] = Rule.Run,
        ["TexasGamePlay::YouLose"] = Rule.Run,
        ["TexasGamePlay::ResetLose"] = Rule.Run,
        ["TexasGamePlay::SetReload"] = Rule.Run,
        ["TexasGamePlay::FoldTargetRpc"] = Rule.Run,
        ["TexasGamePlay::ALLINTargetRpc"] = Rule.Run,
        ["TexasGamePlay::TargetReceiveRevolverOutcome"] = Rule.Run,
        ["TexasGamePlay::ApplySwitchTarget"] = Rule.Run,
        ["TexasGamePlay::SetDeadAnim"] = Rule.Run,

        // Liar's Slots: both only paint the owner's own machine or screen.
        ["SpinGamePlay::TargetRpc_ReceiveSecretSpin"] = Rule.Drop,
        ["LiarsSpinGameplayManager::TargetRpc_InformResultToBettor"] = Rule.Drop,
    };

    private static readonly Dictionary<string, Rule> _ruleFor = new(StringComparer.Ordinal);

    /// <summary>The rule for a call, by the name Mirror passes: "System.Void Class::Method(args)".</summary>
    private static Rule RuleFor(string full, out string name)
    {
        name = full ?? "";
        int space = name.IndexOf(' ');
        int paren = name.IndexOf('(');
        if (paren < 0) paren = name.Length;
        if (space >= 0 && space < paren) name = name.Substring(space + 1, paren - space - 1);

        if (_ruleFor.TryGetValue(full ?? "", out var rule)) return rule;
        rule = Rules.TryGetValue(name, out var known) ? known : Rule.Unknown;
        _ruleFor[full ?? ""] = rule;
        return rule;
    }

    /// <summary>
    /// The host stands in for a bot seat's two missing ends. Both patches cost one count read for
    /// every call sent at a table with no bots.
    /// </summary>
    [HarmonyPatch]
    internal static class Calls
    {
        /// <summary>A command from a bot's seat: run it here, as the server would on arrival.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(NetworkBehaviour), "SendCommandInternal")]
        private static bool Command(NetworkBehaviour __instance, string functionFullName, int functionHashCode, NetworkWriter writer)
        {
            if (_seats.Count == 0) return true;
            try
            {
                if (!NetworkServer.active || !Is(__instance)) return true;

                RuleFor(functionFullName, out string name);
                if (RunHere(__instance, RemoteCallType.Command, functionHashCode, writer, name)) _commands++;
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[bot] a bot's command failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// A target call with nobody to go to: to a bot's seat, or from a table object about one.
        /// Mirror itself falls back to the sending object's owner, so a call from anything that has
        /// one is left alone.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(NetworkBehaviour), "SendTargetRPCInternal")]
        private static bool Target(NetworkBehaviour __instance, NetworkConnection conn, string functionFullName,
                                   int functionHashCode, NetworkWriter writer)
        {
            if (_seats.Count == 0 || conn != null) return true;
            try
            {
                if (!NetworkServer.active || __instance == null || __instance.connectionToClient != null) return true;

                switch (RuleFor(functionFullName, out string name))
                {
                    case Rule.Run:
                        if (Is(__instance)) AsOwner(__instance, () => Run(__instance, functionHashCode, writer, name));
                        else Run(__instance, functionHashCode, writer, name);
                        return false;

                    case Rule.Drop:
                        _dropped++;
                        if (name.EndsWith("::TargetSetDiceValues", StringComparison.Ordinal)) _dealsKept++;
                        return false;

                    default:
                        _dropped++;
                        if (_named.Add(name))
                            Plugin.Log.LogWarning($"[bot] no rule for {name} - not run for a bot's seat");
                        return false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[bot] a target call for a bot failed: {e.Message}");
                return false;
            }
        }

        private static void Run(NetworkBehaviour part, int hash, NetworkWriter writer, string name)
        {
            if (RunHere(part, RemoteCallType.ClientRpc, hash, writer, name)) _targets++;
        }
    }

    /// <summary>
    /// The moments of a throw that the game times by the throwing animation, in Liar's Deck and
    /// the Chaos deck: the cards leaving the hand, and the cards landing on the table. A bot's
    /// animation plays on the host, so for a bot's seat the host acts as the seat's own copy for
    /// the length of each event, and the event's own server branch does the rest. A person's seat
    /// is left alone.
    /// </summary>
    [HarmonyPatch]
    internal static class Events
    {
        // The seats given ownership for an event, so the end of the event takes back only what it gave.
        private static readonly List<NetworkIdentity> _scoped = new();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.OnThrowHideCardsEvent))]
        private static void DeckHideStarts(DeckGameplay __instance) => Own(__instance);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.OnThrowHideCardsEvent))]
        private static void DeckHideEnds() => Disown();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.OnThrowTableCardsEvent))]
        private static void DeckTableStarts(DeckGameplay __instance) => Own(__instance);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.OnThrowTableCardsEvent))]
        private static void DeckTableEnds() => Disown();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.OnThrowHideCardsEvent))]
        private static void ChaosHideStarts(ChaosDeckGameplay __instance) => Own(__instance);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.OnThrowHideCardsEvent))]
        private static void ChaosHideEnds() => Disown();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.OnThrowTableCardsEvent))]
        private static void ChaosTableStarts(ChaosDeckGameplay __instance) => Own(__instance);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.OnThrowTableCardsEvent))]
        private static void ChaosTableEnds() => Disown();

        /// <summary>A bot's seat counts as owned until the event ends; anything else is marked as nothing given.</summary>
        private static void Own(NetworkBehaviour part)
        {
            NetworkIdentity given = null;
            try
            {
                if (_seats.Count > 0 && NetworkServer.active && Is(part))
                {
                    var identity = part.netIdentity;
                    if (identity != null && !identity.isOwned)
                    {
                        identity.isOwned = true;
                        given = identity;
                        _events++;
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[bot] a bot's throw: {e.Message}"); }
            _scoped.Add(given);
        }

        private static void Disown()
        {
            if (_scoped.Count == 0) return;
            var given = _scoped[_scoped.Count - 1];
            _scoped.RemoveAt(_scoped.Count - 1);
            try { if (given != null) given.isOwned = false; }
            catch { }
        }

        /// <summary>
        /// From the tick, between frames, when no event can be running: an event that threw never
        /// reached its end, so whatever it was given is taken back here, said once.
        /// </summary>
        internal static void Settle()
        {
            if (_scoped.Count == 0 && _seats.Count == 0) return;
            _scoped.Clear();
            foreach (var s in _seats.Values)
            {
                try
                {
                    if (s.Identity == null || !s.Identity.isOwned) continue;
                    s.Identity.isOwned = false;
                    if (_named.Add("ownership left on"))
                        Plugin.Log.LogWarning("[bot] a bot's seat was still marked as owned after a throw - taken back");
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// The Chaos deck: the server firing a seat's gun. A seat's own copy takes its gun down once
    /// the shot's picture has played there, the only thing that ever takes it down, so a bot's stayed
    /// up after it had fired: still aiming on every screen, and still counted as holding the gun.
    /// The moment it fires is noted here for <see cref="BotPuppet"/>, which takes it down the way
    /// the server does for any seat a moment later.
    /// </summary>
    [HarmonyPatch]
    internal static class GunDown
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ChaosDeckGameplay), "ServerResolveFire")]
        private static void Fired(ChaosDeckGameplay __instance)
        {
            if (_seats.Count == 0) return;
            try
            {
                if (__instance == null || !_seats.TryGetValue(__instance.netId, out var seat)) return;
                seat.FiredAt = UnityEngine.Time.time;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[bot] a bot's shot: {e.Message}"); }
        }
    }

    private static int _events, _pulls;

    /// <summary>How many throw events the host has played as a bot's own copy this session, for the test driver.</summary>
    internal static int EventsRun => _events;

    /// <summary>How many times a bot's seat was let pull its trigger in Liar's Deck this session, for the test driver.</summary>
    internal static int PullsLet => _pulls;

    /// <summary>Whether the trigger patch went on; without it no bot sits at a Liar's Deck table.</summary>
    internal static bool TriggerOn { get; set; }

    /// <summary>Whether the gun-down patch went on; without it no bot sits at a Chaos deck table.</summary>
    internal static bool GunDownOn { get; set; }

    /// <summary>
    /// Liar's Deck checks, before every trigger pull and every devil card's volley, that the seat is
    /// there to pull it. A bot's seat failed that check, so a liar call it lost, or the devil card's
    /// volley, passed it by: seen in the first match of batch B2, eight seats under HARSH's three
    /// chambers, eight calls and not one pull. A living bot's seat passes it now, and the game's own
    /// pull runs on the host.
    /// </summary>
    [HarmonyPatch]
    internal static class Trigger
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.IsPlayerAvailableForRevolver))]
        private static void There(GameObject player, ref bool __result)
        {
            if (__result || _seats.Count == 0) return;
            try
            {
                if (player == null || !NetworkServer.active) return;
                var p = player.GetComponent<PlayerStats>();
                if (p == null || p.Dead || !Is(p)) return;
                __result = true;
                _pulls++;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[bot] a bot's trigger: {e.Message}"); }
        }
    }

    /// <summary>
    /// Run a call's body on this machine from what was written for it, the way Mirror runs one that
    /// arrives: the number, the kind and the part all have to match, so a stale number runs nothing
    /// rather than the wrong thing. A target call is filed with Mirror as a client call.
    /// </summary>
    private static bool RunHere(NetworkBehaviour part, RemoteCallType kind, int hash, NetworkWriter writer, string name)
    {
        var reader = NetworkReaderPool.Get(writer.ToArray());
        try
        {
            if (RemoteProcedureCalls.Invoke((ushort)hash, kind, reader, part, null)) return true;
            if (_named.Add(name))
                Plugin.Log.LogError($"[bot] Mirror would not run {name} for a bot's seat: its number does not match the part");
            return false;
        }
        finally { NetworkReaderPool.Return(reader); }
    }

    /// <summary>
    /// The seat counts as its owner's for the length of one call, for the bodies that check. Only
    /// what runs before this returns sees it: a coroutine the body starts runs later, unowned.
    /// </summary>
    private static void AsOwner(NetworkBehaviour part, Action body)
    {
        var identity = part.netIdentity;
        bool was = identity.isOwned;
        identity.isOwned = true;
        try { body(); }
        finally { identity.isOwned = was; }
    }
}
