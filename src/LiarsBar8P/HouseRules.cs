using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Mirror;
using Steamworks;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// House rules: one choice the host makes in the lobby, CLASSIC, QUICK or HARSH, and what each
/// means at the table being played. They shorten the part of an eight-seat match that drags (a
/// shorter cylinder, fewer dice, a faster clock) without changing what the game is.
///
/// Presets and not a list of switches, because three choices across five tables is something one
/// person can test, and ten free switches are a thousand combinations per table.
///
/// <b>Who decides.</b> Every rule's effect happens on the host: the patches in
/// <see cref="RuleEffects"/> only act on the copy that is the table's server, and everything they
/// change reaches the other copies through the game's own network values and calls. A client
/// needs the rule only to print it, and for two cosmetic touches (the chamber count over the other
/// seats, and the drink that kills). So a client that never hears the choice cannot desync.
///
/// <b>Where it lives.</b> In memory, for one lobby. Starting or joining a lobby, and leaving one,
/// clears it, so a new lobby, a lobby Quick Join opens and a restart are all CLASSIC. Nothing is
/// saved. The choice is frozen when the match starts (<see cref="Live"/>) and dropped when the
/// table is back in the lobby, where the host's choice is still standing for the next match.
///
/// <b>Who may play them.</b> Only at a table where everyone runs the mod (<see cref="Table.Modded"/>),
/// and only when everybody runs this very version: somebody on another version, or without the
/// mod, makes the table CLASSIC until they leave, and the lobby says so. Never near ranked, never
/// in the Velvet Room.
///
/// <b>How the table hears.</b> Two ways, for two audiences. People looking at the lobby list read
/// <see cref="Key"/> on the Steam lobby, which the host writes. People at the table get it over
/// <see cref="ModNet"/>; where the channel is shut, a client reads the same lobby key instead.
/// </summary>
internal static class HouseRules
{
    // ------------------------------------------------------------------ every word a player sees

    /// <summary>The presets, in the order the arrows step through them.</summary>
    internal static readonly string[] Names = { "CLASSIC", "QUICK", "HARSH" };

    /// <summary>A lobby that says something this build cannot read.</summary>
    internal const string UnknownName = "RULES ?";

    private const string ToTheRoom = "House rules: {0}";
    private const string ToTheRoomLater = "House rules: {0}, once everyone has this version of the mod";
    private const string RefusedToTheRoom = "House rules are off this match: not everyone has this version of the mod";
    private const string ClassicLine = "The game's own rules";
    private const string NotYetLine = "CLASSIC until everyone has this version of the mod";
    private const string NoRulesHere = "No house rules for this table";
    private const string AlwaysClassic = "This table always plays CLASSIC";
    private const string Joiner = ", ";

    /// <summary>What each rule does, in the words the lobby shows under the choice.</summary>
    private static readonly Dictionary<Rule, string> Says = new()
    {
        { Rule.FastClock,     "15 s turns" },
        { Rule.Cylinder4,     "4-chamber revolver" },
        { Rule.Cylinder3,     "3-chamber revolver" },
        { Rule.ShortHand,     "3 cards each" },
        { Rule.ChaosStorm,    "2 chaos cards in 12" },
        { Rule.ThreeDice,     "3 dice each" },
        { Rule.OneSip,        "one drink and you are out" },
        { Rule.HotTable,      "every hand starts on 1 bullet" },
        { Rule.NoCowardFold,  "no Coward's Fold" },
        { Rule.HeadStart,     "every round starts on 2 points" },
        { Rule.DeadMansSpin,  "the dead spin spares 1 in 7" },
    };

    // ------------------------------------------------------------------ the rules themselves

    internal const int Classic = 0;
    internal const int Quick = 1;
    internal const int Harsh = 2;

    /// <summary>The Steam lobby key: "0", "1" or "2". Missing means CLASSIC, from a build before this.</summary>
    internal const string Key = "lb8p_rules";

    /// <summary>The ModNet body's layout. Raise it if the body ever means something else.</summary>
    private const byte Layout = 1;

    /// <summary>How long Start stays unlit after the host changes the rules, so nobody is started into one they did not read.</summary>
    private const float HoldSeconds = 5f;

    [Flags]
    internal enum Rule
    {
        None = 0,
        FastClock = 1 << 0,
        Cylinder4 = 1 << 1,
        Cylinder3 = 1 << 2,
        ShortHand = 1 << 3,
        ChaosStorm = 1 << 4,
        ThreeDice = 1 << 5,
        OneSip = 1 << 6,
        HotTable = 1 << 7,
        NoCowardFold = 1 << 8,
        HeadStart = 1 << 9,
        DeadMansSpin = 1 << 10,
    }

    /// <summary>
    /// The tables a rule can be about. The lobby's names: Poker is the Texas table, Slots the Spin
    /// one, TwoCard the hidden two-card table (<see cref="TwoCardPoker"/>), which takes no rules.
    /// New ones go at the end: the number travels to the other copies.
    /// </summary>
    internal enum Game : byte { Other, Deck, Deck2, Chaos, Dice, Poker, Slots, TwoCard }

    /// <summary>
    /// What a preset means at one table, as rated in the design: the rules that shorten that table
    /// without changing it. Deck 2 has no short hand (its hands carry over and are seven cards).
    /// </summary>
    private static Rule Wants(int preset, Game game)
    {
        if (preset == Quick)
        {
            switch (game)
            {
                case Game.Deck:  return Rule.FastClock | Rule.Cylinder4 | Rule.ShortHand;
                case Game.Deck2: return Rule.FastClock | Rule.Cylinder4;
                case Game.Chaos: return Rule.FastClock | Rule.Cylinder4;
                case Game.Dice:  return Rule.FastClock | Rule.ThreeDice;
                case Game.Poker: return Rule.FastClock | Rule.HotTable;
                case Game.Slots: return Rule.HeadStart;
            }
        }
        else if (preset == Harsh)
        {
            switch (game)
            {
                case Game.Deck:
                case Game.Deck2: return Rule.FastClock | Rule.Cylinder3;
                case Game.Chaos: return Rule.Cylinder3 | Rule.ChaosStorm;
                case Game.Dice:  return Rule.ThreeDice | Rule.OneSip;
                case Game.Poker: return Rule.HotTable | Rule.NoCowardFold;
                case Game.Slots: return Rule.DeadMansSpin;
            }
        }
        return Rule.None;
    }

    /// <summary>
    /// A client that was never told the table: every rule the preset has anywhere. Only the two
    /// cosmetic touches read this on a client, and each lives in one table's own code.
    /// </summary>
    private static Rule Anywhere(int preset)
    {
        var all = Rule.None;
        foreach (Game g in Enum.GetValues(typeof(Game))) all |= Wants(preset, g);
        return all;
    }

    /// <summary>
    /// The lobby's table, from the game mode and the deck arrows' choice (0 Basic, 1 Devil, 2 Chaos,
    /// 3 Deck 2). The lobby switches its mode to the Chaos one while the Chaos deck is chosen. The
    /// lobby says Liar's Poker for both tables on the Poker tab; mode 3 is only ever seen at the door.
    /// </summary>
    internal static Game GameOf(int mode, int deckMode)
    {
        switch ((CustomNetworkManager.GameMode)mode)
        {
            case CustomNetworkManager.GameMode.LiarsChaos: return Game.Chaos;
            case CustomNetworkManager.GameMode.LiarsDeck:
                return deckMode == 2 ? Game.Chaos : deckMode == HiddenTables.Deck2 ? Game.Deck2 : Game.Deck;
            case CustomNetworkManager.GameMode.LiarsDice: return Game.Dice;
            case CustomNetworkManager.GameMode.LiarsTexas: return TwoCardPoker.Shown ? Game.TwoCard : Game.Poker;
            case CustomNetworkManager.GameMode.LiarsPoker: return Game.TwoCard;
            case CustomNetworkManager.GameMode.LiarsSpin: return Game.Slots;
            default: return Game.Other;
        }
    }

    // ------------------------------------------------------------------ state

    /// <summary>Host: this lobby's choice. CLASSIC whenever a lobby starts.</summary>
    internal static int Picked { get; private set; }

    /// <summary>Any copy: what this lobby plays, as last heard. On the host, the choice once everyone can play it.</summary>
    internal static int Shown { get; private set; }

    /// <summary>What this match plays. Set once, as the match starts, and CLASSIC again in the lobby.</summary>
    internal static int Live { get; private set; }

    /// <summary>The rules in force this match. On a client, only the cosmetic ones are ever read.</summary>
    private static Rule _live;

    /// <summary>The rules whose patches went on. A rule that did not is never claimed, and never switched on.</summary>
    private static Rule _working;

    private static Game _liveGame = Game.Other;
    private static Game _heardGame = Game.Other;
    private static int _session = -1;
    private static int _published = -1;
    private static float _holdUntil;
    private static float _nextCheck;
    private static bool _allowed;
    private static string _whyNot = "not looked yet";
    private static int _keptFor = -1;
    private static bool _heard;
    private static float _nextRead;

    /// <summary>This copy is the table's server, and this rule is in force.</summary>
    internal static bool Host(Rule rule) => (_live & rule) != 0 && NetworkServer.active;

    /// <summary>This rule is in force at the table, on any copy. For the cosmetic touches only.</summary>
    internal static bool On(Rule rule) => (_live & rule) != 0;

    /// <summary>Start stays unlit for a moment after a change. Read by the host's lobby upkeep.</summary>
    internal static bool Holding => NetworkServer.active && Time.unscaledTime < _holdUntil;

    /// <summary>The chambers this match's revolver has: 6 as the game has it, or the rule's.</summary>
    internal static int Chambers => On(Rule.Cylinder3) ? 3 : On(Rule.Cylinder4) ? 4 : 6;

    /// <summary>Developer test switch: refuse rules as if somebody were on another version.</summary>
    internal static bool RefuseForTest { get; set; }

    /// <summary>From <see cref="Plugin"/>: a rule's patches went on.</summary>
    internal static void Works(Rule rule, bool applied)
    {
        if (applied) _working |= rule;
    }

    internal static void Install() => ModNet.On(ModKind.HouseRules, Received);

    // ------------------------------------------------------------------ choosing

    /// <summary>
    /// The host steps the choice. Only at a modded table, never near ranked, and never once the
    /// match is on its way: the lobby's countdown or the load counts as on its way.
    /// </summary>
    internal static bool Pick(int to)
    {
        if (!CanPick(LobbyController.Instance)) return false;

        to = ((to % Names.Length) + Names.Length) % Names.Length;
        if (to == Picked) return true;

        Picked = to;
        _nextCheck = 0f;
        Check();

        _holdUntil = Time.unscaledTime + HoldSeconds;
        Matchmaking.TellTheRoom(string.Format(Shown == Picked ? ToTheRoom : ToTheRoomLater, Names[Picked]));

        // A host who picked rules chose this lobby: Quick Join's merge must not move it.
        QuickMatch.StopLooking();

        Plugin.Log.LogInfo($"[rules] host picked {Names[Picked]} - the table plays {Names[Shown]}" +
                           (Shown != Picked ? $" ({_whyNot})" : ""));
        return true;
    }

    internal static bool CanPick(LobbyController room)
    {
        if (room == null || !NetworkServer.active || RankedGuard.Near || !Table.Modded) return false;
        if (Manager.Instance != null || Locked(room) || TwoCardPoker.Shown) return false;
        var nm = Net();
        return nm != null && !nm.Matchmaking && !nm.isVelvetRoom;
    }

    /// <summary>
    /// The lobby has begun to start the match: nobody may change the rules under it. The loading
    /// panel is always switched on in the lobby and fades in when the match is on its way.
    /// </summary>
    internal static bool Locked(LobbyController room)
    {
        if (room == null) return true;
        if (room.countdownstarted) return true;
        var loading = room.Loading;
        var fade = loading != null && loading.activeInHierarchy ? loading.GetComponent<CanvasGroup>() : null;
        return fade != null && fade.alpha > 0.05f;
    }

    // ------------------------------------------------------------------ the tick

    /// <summary>From <see cref="ModTicker"/>.</summary>
    internal static void Tick()
    {
        var nm = Net();

        // Nothing at all near ranked: no state, no Steam.
        if (RankedGuard.Near || (nm != null && (nm.Matchmaking || nm.isVelvetRoom)))
        {
            Clear();
            return;
        }

        // A new lobby, joining one, or leaving one: CLASSIC again. A host migration in the middle
        // of a match is a new session too, and that match keeps the rules it started with.
        int session = Table.Session;
        if (session != _session)
        {
            _session = session;
            _published = -1;
            _keptFor = -1;
            _heard = false;
            _heardGame = Game.Other;
            Picked = 0;
            Shown = 0;
            if (Manager.Instance == null) SetLive(0, Game.Other);
            else if (NetworkServer.active && Live != Classic)
            {
                // This copy took over as the server: from now on it is the one that applies them.
                SetLive(Live, AtTable());
                Plugin.Log.LogInfo($"[rules] took over the table - {Names[Live]} goes on: {Describe(_live)}");
            }
        }

        // Back in the lobby: the match's rules are over, the host's choice stands.
        if (Manager.Instance == null && Live != 0) SetLive(0, Game.Other);

        if (NetworkServer.active) HostTick();
        else if (NetworkClient.active) ClientTick();
    }

    private static void HostTick()
    {
        // During a match the frozen value is what the table plays, and what has been sent.
        if (Manager.Instance != null) return;

        Check();
        Publish();
    }

    /// <summary>Once a second: may this table play what the host picked?</summary>
    private static void Check()
    {
        float now = Time.unscaledTime;
        if (now >= _nextCheck)
        {
            _nextCheck = now + 1f;
            _allowed = Picked == Classic || Allowed(out _whyNot);
        }
        Shown = Picked != Classic && _allowed && !TwoCardPoker.Shown ? Picked : Classic;
    }

    /// <summary>
    /// Rules are all or nothing: never half on. Every human at the table must run this very
    /// version, because an older build cannot describe a rule it has never heard of. Bots are not
    /// Steam members and never count. In the loopback harness every copy is this build.
    /// </summary>
    internal static bool Allowed(out string why)
    {
        var nm = Net();
        if (RankedGuard.Near || nm == null || nm.Matchmaking) { why = "ranked"; return false; }
        if (nm.isVelvetRoom) { why = "not a table"; return false; }
        if (RefuseForTest && Fair.TestsAllowed) { why = "refused by the test switch"; return false; }
        if (!Table.Modded) { why = $"not a modded table ({Table.Why})"; return false; }
        if (Loopback.Active) { why = ""; return true; }

        if (!Table.Lobby(out var lobby, out why)) return false;

        int members = SteamMatchmaking.GetNumLobbyMembers(lobby);
        for (int i = 0; i < members; i++)
        {
            var member = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
            if (SteamMatchmaking.GetLobbyMemberData(lobby, member, VersionCheck.Key) == Plugin.Version) continue;

            why = $"member {i + 1} of {members} runs another version";
            return false;
        }

        why = "";
        return true;
    }

    /// <summary>The choice on the Steam lobby, for the list, and over the channel, for the table.</summary>
    private static void Publish()
    {
        if (_published != Shown && Table.Hosted(out var lobby) &&
            SteamMatchmaking.SetLobbyData(lobby, Key, Shown.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        {
            _published = Shown;
            Plugin.Log.LogInfo($"[rules] the lobby says {Names[Shown]}");
        }

        if (_keptFor != Shown && Send(Shown, false, Game.Other)) _keptFor = Shown;
    }

    private static bool Send(int rule, bool match, Game game) =>
        ModNet.Keep(ModKind.HouseRules,
                    new ModWriter().U8(Layout).U8((byte)rule).U8(match ? (byte)1 : (byte)0).U8((byte)game));

    /// <summary>
    /// A client with no word over the channel reads the lobby key once a second, as the list does.
    /// Steam is asked only after the ranked check, and only about the table's own lobby.
    /// </summary>
    private static void ClientTick()
    {
        if (_heard || Manager.Instance != null || Loopback.Active) return;

        float now = Time.unscaledTime;
        if (now < _nextRead) return;
        _nextRead = now + 1f;

        if (!Table.Modded || !Table.Lobby(out var lobby, out _)) { Shown = Classic; return; }
        int said = Read(SteamMatchmaking.GetLobbyData(lobby, Key));
        Shown = said > Harsh ? Classic : said;
    }

    /// <summary>A lobby key's value: 0 to 2, or 3 for something this build cannot read. Missing is CLASSIC.</summary>
    internal static int Read(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return Classic;
        return raw == "0" ? Classic : raw == "1" ? Quick : raw == "2" ? Harsh : 3;
    }

    /// <summary>The name a lobby row shows, or null for a CLASSIC lobby, which shows nothing.</summary>
    internal static string RowName(string raw)
    {
        int said = Read(raw);
        return said == Classic ? null : said > Harsh ? UnknownName : Names[said];
    }

    private static void Received(int from, ModReader body)
    {
        if (from != ModNet.FromHost || NetworkServer.active || RankedGuard.Near) return;

        int layout = body.U8();
        int rule = body.U8();
        bool match = body.U8() == 1;
        var game = (Game)body.U8();
        if (body.Failed || layout != Layout || rule > Harsh) return;

        _heard = true;
        Shown = rule;
        _heardGame = game;
        if (match && Manager.Instance != null) SetLive(rule, game);
    }

    // ------------------------------------------------------------------ the match

    [HarmonyPatch]
    internal static class Freeze
    {
        /// <summary>
        /// The match's rules, settled as its manager starts, on every copy and before any rule is
        /// first used: the revolver waits for the start, and dice are rolled and cards dealt later.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Manager), nameof(Manager.Start))]
        private static void Started(Manager __instance)
        {
            try { Begin(__instance); }
            catch (Exception e)
            {
                SetLive(0, Game.Other);
                Plugin.Log.LogWarning($"[rules] could not settle this match's rules, it plays CLASSIC: {e.Message}");
            }
        }
    }

    private static void Begin(Manager m)
    {
        var nm = Net();
        if (m == null || nm == null || RankedGuard.Near || nm.Matchmaking || nm.isVelvetRoom)
        {
            SetLive(0, Game.Other);
            return;
        }

        if (!NetworkServer.active)
        {
            SetLive(Shown, _heardGame);
            if (Live != Classic) Plugin.Log.LogInfo($"[rules] this match plays {Names[Live]}, as the host said");
            return;
        }

        var game = GameOf((int)nm.Mode, nm.DeckMode);
        int live = Classic;
        string why = "";
        if (Picked != Classic && game != Game.TwoCard)
        {
            if (Allowed(out why)) live = Picked;
            else
            {
                Matchmaking.TellTheRoom(RefusedToTheRoom);
                Plugin.Log.LogWarning($"[rules] {Names[Picked]} refused for this match, it plays CLASSIC: {why}");
            }
        }

        SetLive(live, game);
        Send(live, true, game);
        _keptFor = -1;   // the lobby's choice goes out again once the table is back
        Plugin.Log.LogInfo($"[rules] match starts at {game} on {Names[live]}" +
                           (_live != Rule.None ? $": {Describe(_live)}" : ""));
    }

    /// <summary>
    /// The rules this copy obeys. A server that does not know its table plays none: it is the
    /// one copy whose rules change the game. A client that was not told uses the preset's rules
    /// at every table, which only its two cosmetic touches ever read.
    /// </summary>
    private static void SetLive(int rule, Game game)
    {
        Live = rule;
        _liveGame = game;
        _live = rule == Classic ? Rule.None
              : game != Game.Other ? Wants(rule, game) & _working
              : NetworkServer.active ? Rule.None
              : Anywhere(rule) & _working;
        RuleEffects.NewMatch();
    }

    /// <summary>The table being played, read from the seats, for a copy that takes over in a match.</summary>
    private static Game AtTable()
    {
        switch (TableHand.Playing())
        {
            case TableHand.Kind.Deck:
                var deck = Manager.Instance != null ? Manager.Instance.DeckGamePlayManager : null;
                return deck != null && deck.DeckMode == DeckGamePlayManager.deckmode.Deck2 ? Game.Deck2 : Game.Deck;
            case TableHand.Kind.ChaosDeck: return Game.Chaos;
            case TableHand.Kind.Dice: return Game.Dice;
            case TableHand.Kind.Texas: return Game.Poker;
            case TableHand.Kind.Spin: return Game.Slots;
            case TableHand.Kind.Poker: return Game.TwoCard;
            default: return Game.Other;
        }
    }

    private static void Clear()
    {
        Picked = 0;
        Shown = 0;
        _published = -1;
        _keptFor = -1;
        _heard = false;
        if (Live != 0 || _live != Rule.None) SetLive(0, Game.Other);
    }

    // ------------------------------------------------------------------ words

    /// <summary>What a preset does at a table, in one line for the lobby. Only rules that are working are named.</summary>
    internal static string Summary(int preset, Game game)
    {
        if (game == Game.TwoCard) return LiarsBar8P.Words.T(AlwaysClassic);
        if (preset == Classic) return LiarsBar8P.Words.T(ClassicLine);
        var rules = Wants(preset, game) & _working;
        return rules == Rule.None ? LiarsBar8P.Words.T(NoRulesHere) : Words(rules);
    }

    /// <summary>The note under the choice when the host picked rules the table cannot play yet.</summary>
    internal static string Waiting => Picked != Classic && Shown == Classic && NetworkServer.active && !TwoCardPoker.Shown
        ? LiarsBar8P.Words.T(NotYetLine) : null;

    private static string Words(Rule rules)
    {
        var sb = new StringBuilder();
        foreach (var pair in Says)
        {
            if ((rules & pair.Key) == 0) continue;
            if (sb.Length > 0) sb.Append(LiarsBar8P.Words.T(Joiner));
            sb.Append(LiarsBar8P.Words.T(pair.Value));
        }
        return sb.ToString();
    }

    /// <summary>The rule names for the log.</summary>
    internal static string Describe(Rule rules)
    {
        var sb = new StringBuilder();
        foreach (Rule r in Enum.GetValues(typeof(Rule)))
        {
            if (r == Rule.None || (rules & r) == 0) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(r);
        }
        return sb.Length > 0 ? sb.ToString() : "none";
    }

    /// <summary>For the test driver.</summary>
    internal static string Describe() =>
        $"picked {Names[Picked]}, shown {Names[Shown]}, live {Names[Live]} at {_liveGame} ({Describe(_live)}); " +
        $"working: {Describe(_working)}; allowed: {(_allowed ? "yes" : $"no ({_whyNot})")}";

    private static CustomNetworkManager Net() =>
        NetworkManager.singleton != null ? NetworkManager.singleton.TryCast<CustomNetworkManager>() : null;
}
