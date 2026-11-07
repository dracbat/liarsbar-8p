using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Mirror;
using Steamworks;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Fair play: the host-migration snapshot the host keeps on the Steam lobby.
///
/// <b>What it is.</b> The host-migration snapshot of a Liar's Deck match, so that if the host
/// drops, whoever takes over can carry the match on.
///
/// <b>What changes.</b> At a match where the host hides hands (<see cref="Fair.HideHands"/>, every
/// player runs this same build), the snapshot carries only what every seat already shows (seats,
/// lives, public pull counts), and the new host loads each revolver itself. A new host deals a fresh
/// round after a migration anyway, at every table (the game's own round restore ends with a hard
/// reset and <c>WaitforNextRoundMigration</c>), so nothing about the round in play is kept. Anything the list does not name is left out, so a field a game
/// update adds stays out until someone has looked at it. At any other match nothing here changes.
///
/// A hidden-hands match also starts by clearing whatever snapshot an earlier match left on the
/// lobby, so the lobby only ever holds this match's published snapshot.
///
/// <b>Where.</b> Every chunk the game writes goes through <c>HostMigration.WriteLobbyChunks</c>,
/// which is where the snapshot is replaced. Behind it, a second check on the lobby write itself
/// lets a snapshot chunk through at a hidden-hands match only from inside that replaced write, so
/// if the first hook ever failed to apply, the snapshot is simply not published (the new host then
/// carries on from its own copy, as the game already does when the lobby has none).
/// </summary>
internal static class LobbySnapshot
{
    /// <summary>The game's key prefix for the Deck snapshot chunks.</summary>
    internal const string DeckPrefix = "deck_mig_";

    /// <summary>The chamber value in the snapshot at a hidden-hands match: the new host loads each revolver itself.</summary>
    internal const int UnknownChamber = -1;

    private const string ChamberField = "RevolverBullet";
    private const string SeatsField = "Players";

    // What a new host needs to carry the match on, and nothing more. Matched without case, as the
    // game's JSON reader does.
    private static readonly HashSet<string> TableKept = new(StringComparer.OrdinalIgnoreCase)
    {
        "SnapshotVersion", "Phase", "DeckMode", "RoundCard", "CardsOnTable", "ActivePlayerSlot",
        "CountDown", "LastBetPlayerSlot", "GameStarted", "JokerSettled",
        "FinishWithRemainingPlayer", "FinishTriggered", SeatsField,
    };

    private static readonly HashSet<string> SeatKept = new(StringComparer.OrdinalIgnoreCase)
    {
        "SteamId", "Slot", "PlayerName", "Health", "Dead", "Winner", "HaveTurn", "Fnished",
        "HaveCards", "CurrentRevolver", "LastAnimStateHash", "LastAnimNormalizedTime",
    };

    // The fields the game's restore reads for each seat, which must come through unchanged.
    private static readonly string[] SeatRestored =
        { "SteamId", "Slot", "PlayerName", "Health", "Dead", "Winner", "Fnished", "CurrentRevolver" };

    private static readonly string[] TableRestored =
        { "SnapshotVersion", "GameStarted", "FinishWithRemainingPlayer", "FinishTriggered" };

    private static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    // ------------------------------------------------------------------ the snapshot as published

    /// <summary>
    /// What the host publishes for this snapshot: the game's own text when <paramref name="hide"/>
    /// is off (the same string, untouched), otherwise the cleaned text, or null when it could not
    /// be cleaned, in which case nothing is published.
    /// </summary>
    internal static string Publishable(string json, bool hide, out string why)
    {
        why = "";
        if (!hide) return json;
        if (string.IsNullOrEmpty(json)) return json;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject table) { why = "not a snapshot"; return null; }

            foreach (string key in table.Select(kv => kv.Key).ToList())
                if (!TableKept.Contains(key)) table.Remove(key);

            if (FindKey(table, SeatsField) is string seatsKey && table[seatsKey] is JsonArray seats)
            {
                foreach (var node in seats)
                {
                    if (node is not JsonObject seat) continue;
                    foreach (string key in seat.Select(kv => kv.Key).ToList())
                        if (!SeatKept.Contains(key)) seat.Remove(key);
                    seat[ChamberField] = UnknownChamber;
                }
            }
            else if (FindKey(table, SeatsField) is string other && table[other] != null)
            {
                why = "the seats are not a list";
                return null;
            }

            string clean = table.ToJsonString(Compact);
            if (!Clean(clean, out string left)) { why = left; return null; }
            return clean;
        }
        catch (Exception e)
        {
            why = e.Message;
            return null;
        }
    }

    /// <summary>
    /// True when a published snapshot holds only what <see cref="Publishable"/> keeps, with every
    /// chamber unknown. Otherwise says what else is there. Empty text counts as clean.
    /// </summary>
    internal static bool Clean(string json, out string what)
    {
        what = "";
        if (string.IsNullOrEmpty(json)) return true;
        JsonObject table;
        try { table = JsonNode.Parse(json) as JsonObject; }
        catch (Exception e) { what = $"unreadable ({e.Message})"; return false; }
        if (table == null) { what = "not a snapshot"; return false; }

        foreach (var kv in table)
            if (!TableKept.Contains(kv.Key)) { what = $"table field '{kv.Key}' is not on the kept list"; return false; }

        string seatsKey = FindKey(table, SeatsField);
        if (seatsKey == null || table[seatsKey] == null) return true;
        if (table[seatsKey] is not JsonArray seats) { what = "the seats are not a list"; return false; }

        int n = 0;
        foreach (var node in seats)
        {
            n++;
            if (node == null) continue;
            if (node is not JsonObject seat) { what = $"seat entry {n} is not an object"; return false; }
            foreach (var kv in seat)
            {
                if (string.Equals(kv.Key, ChamberField, StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsUnknownChamber(kv.Value)) { what = $"seat entry {n} has a known chamber"; return false; }
                    continue;
                }
                if (!SeatKept.Contains(kv.Key)) { what = $"seat field '{kv.Key}' is not on the kept list"; return false; }
            }
        }
        return true;
    }

    /// <summary>True when the cleaned snapshot still carries, unchanged, every field the game's restore reads.</summary>
    internal static bool KeepsRestore(string before, string after, out string what)
    {
        what = "";
        try
        {
            var a = JsonNode.Parse(before) as JsonObject;
            var b = JsonNode.Parse(after) as JsonObject;
            if (a == null || b == null) { what = "not a snapshot"; return false; }

            foreach (string f in TableRestored)
                if (!Same(Get(a, f), Get(b, f))) { what = $"table field {f} changed"; return false; }

            var sa = Get(a, SeatsField) as JsonArray;
            var sb = Get(b, SeatsField) as JsonArray;
            if (sa == null || sb == null) { what = sa == null && sb == null ? "" : "the seat list changed"; return sa == null && sb == null; }
            if (sa.Count != sb.Count) { what = $"seat count {sa.Count} -> {sb.Count}"; return false; }

            for (int i = 0; i < sa.Count; i++)
            {
                var x = sa[i] as JsonObject;
                var y = sb[i] as JsonObject;
                if (x == null || y == null) { if (x != y) { what = $"seat entry {i + 1} changed"; return false; } continue; }
                foreach (string f in SeatRestored)
                    if (!Same(Get(x, f), Get(y, f))) { what = $"seat entry {i + 1} field {f} changed"; return false; }
            }
            return true;
        }
        catch (Exception e) { what = e.Message; return false; }
    }

    private static bool IsUnknownChamber(JsonNode v)
    {
        try { return v is JsonValue value && value.TryGetValue(out int n) && n == UnknownChamber; }
        catch { return false; }
    }

    private static string FindKey(JsonObject o, string name)
    {
        foreach (var kv in o)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Key;
        return null;
    }

    private static JsonNode Get(JsonObject o, string name)
    {
        string key = FindKey(o, name);
        return key == null ? null : o[key];
    }

    private static bool Same(JsonNode x, JsonNode y) =>
        (x == null && y == null) || (x != null && y != null && x.ToJsonString() == y.ToJsonString());

    // ------------------------------------------------------------------ the lobby

    /// <summary>Set while the cleaned snapshot is being written, or the old one cleared.</summary>
    private static bool _writing;

    private static int _saidFor = -1;
    private static float _droppedSaid;

    /// <summary>The lobby the game writes its snapshot to, or 0 (the loopback harness has none).</summary>
    internal static ulong LobbyId
    {
        get
        {
            try
            {
                var sl = SteamLobby.Instance;
                return sl == null ? 0 : sl.CurrentLobbyID;
            }
            catch { return 0; }
        }
    }

    /// <summary>What the lobby holds for the Deck snapshot right now, through the game's own reader. Read only.</summary>
    internal static string Published()
    {
        ulong id = LobbyId;
        return id == 0 ? null : HostMigration.ReadLobbyChunks(new CSteamID(id), DeckPrefix);
    }

    /// <summary>Clear an earlier match's snapshot from the lobby, through the game's own clearing. False with no lobby.</summary>
    internal static bool ClearEarlier()
    {
        ulong id = LobbyId;
        if (id == 0) return false;
        _writing = true;
        try { HostMigration.ClearLobbyChunks(new CSteamID(id), DeckPrefix); }
        finally { _writing = false; }
        return true;
    }

    /// <summary>
    /// Whether a lobby write may go out as it is. Only snapshot chunks at a hidden-hands match are
    /// ever held back, and only when they don't come from the cleaned write; clearing (an empty
    /// value) and the chunk count are always fine.
    /// </summary>
    internal static bool Allows(string key, string value, bool hide, bool fromCleanWrite)
    {
        if (key == null || !key.StartsWith(DeckPrefix, StringComparison.Ordinal)) return true;
        if (!hide || fromCleanWrite) return true;
        if (string.IsNullOrEmpty(value)) return true;
        if (key == DeckPrefix + "_n") return true;
        return false;
    }

    private static void Dropped(string why)
    {
        if (Time.unscaledTime < _droppedSaid) return;
        _droppedSaid = Time.unscaledTime + 10f;
        Plugin.Log.LogWarning($"{Fair.Tag} migration snapshot not published this time - {why}");
    }

    // ------------------------------------------------------------------ the hooks

    /// <summary>The game's one writer of snapshot chunks: at a hidden-hands match, publish the cleaned snapshot.</summary>
    [FairPatch("the lobby's migration snapshot carries only what a new host needs")]
    [HarmonyPatch(typeof(HostMigration), nameof(HostMigration.WriteLobbyChunks))]
    internal static class Write
    {
        private static void Register() => Fair.OnDrive("snapshot", Test);

        [HarmonyPrefix]
        private static bool Before(string prefix, ref string json)
        {
            _writing = false;
            bool hide = false;
            try
            {
                hide = Fair.HideHands;
                if (!hide) return true;

                if (prefix != DeckPrefix)
                {
                    Dropped($"a snapshot under '{prefix}' this copy does not know");
                    return false;
                }

                string clean = Publishable(json, true, out string why);
                if (clean == null)
                {
                    Dropped(why);
                    return false;
                }

                json = clean;
                _writing = true;
                if (_saidFor != Fair.Match)
                {
                    _saidFor = Fair.Match;
                    Plugin.Log.LogInfo($"{Fair.Tag} migration snapshot on the lobby: only what a new host needs, chambers left to draw (hidden-hands match)");
                }
                return true;
            }
            catch (Exception e)
            {
                // Hands hidden: rather no snapshot than an uncleaned one. Otherwise the game's own write.
                if (hide) Dropped(e.Message);
                return !hide;
            }
        }

        [HarmonyFinalizer]
        private static Exception After(Exception __exception)
        {
            _writing = false;
            return __exception;
        }
    }

    /// <summary>Behind the hook above: a snapshot chunk at a hidden-hands match goes out only from the cleaned write.</summary>
    [FairPatch("snapshot chunks leave only through the cleaned write")]
    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.SetLobbyData))]
    internal static class Chunk
    {
        [HarmonyPrefix]
        private static bool Before(string pchKey, string pchValue, ref bool __result)
        {
            try
            {
                if (pchKey == null || !pchKey.StartsWith(DeckPrefix, StringComparison.Ordinal)) return true;
                if (Allows(pchKey, pchValue, Fair.HideHands, _writing)) return true;
                Dropped("a snapshot chunk came from outside the cleaned write");
                __result = false;
                return false;
            }
            catch { return true; }
        }
    }

    /// <summary>A hidden-hands match starts with no snapshot from an earlier match on the lobby.</summary>
    [FairPatch("a hidden-hands match starts with no earlier migration snapshot on the lobby")]
    [HarmonyPatch(typeof(Manager), nameof(Manager.StartGame))]
    internal static class Fresh
    {
        [HarmonyPostfix]
        private static void After()
        {
            try
            {
                if (!Fair.HideHands) return;
                if (ClearEarlier()) Plugin.Log.LogInfo($"{Fair.Tag} hidden-hands match: the lobby's migration snapshot from earlier is cleared");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"{Fair.Tag} could not clear the earlier migration snapshot: {e.Message}"); }
        }
    }

    // ------------------------------------------------------------------ developer test

    /// <summary>
    /// "fair snapshot [clear]": the host takes the snapshot it would publish at this match (this
    /// match's live one in a Deck match, otherwise a made-up one), runs it through exactly what
    /// the hook does, and checks the result. All inside the host; it sends nothing to anyone.
    /// With "clear", at a hidden-hands match with a Steam lobby, it also clears the lobby's
    /// snapshot as a match start does and reads it back.
    /// </summary>
    private static void Test(string rest)
    {
        bool hideNow = Fair.HideHands;
        string json = SampleSnapshot(out string source);
        if (string.IsNullOrEmpty(json))
        {
            Fair.Result("snapshot", false, "could not build a snapshot to check");
            return;
        }
        Plugin.Log.LogInfo($"{Fair.Tag} test: snapshot from {source}, {json.Length} characters; hosting={Fair.Hosting} hidden hands={hideNow}");

        // Hands hidden: nothing outside the kept list, every chamber unknown, the restore's fields unchanged.
        string hidden = Publishable(json, true, out string why);
        bool okHidden = hidden != null && Clean(hidden, out why) && KeepsRestore(json, hidden, out why);
        Fair.Result("snapshot-hidden", okHidden, okHidden
            ? $"cleaned {json.Length} -> {hidden.Length} characters, restore fields unchanged"
            : $"{why}");

        // Hands not hidden: the game's own text, the same string.
        string vanilla = Publishable(json, false, out _);
        Fair.Result("snapshot-vanilla", ReferenceEquals(vanilla, json), ReferenceEquals(vanilla, json)
            ? "the game's own snapshot, untouched"
            : "the snapshot was changed with hands not hidden");

        // This match, as the hook decides it now.
        string now = Publishable(json, hideNow, out why);
        bool okNow = hideNow ? now != null && Clean(now, out why) : ReferenceEquals(now, json);
        Fair.Result("snapshot-this-match", okNow, $"hidden hands {(hideNow ? "on" : "off")}: " +
            (okNow ? (hideNow ? "published cleaned" : "published as the game wrote it") : why));

        // The cleaned snapshot still loads through the game's own reader, with every chamber unknown.
        if (hidden != null)
        {
            var back = FromJson(hidden, out why);
            bool loads = back != null && back.SnapshotVersion != 0 && Chambers(back).All(c => c == UnknownChamber);
            Fair.Result("snapshot-loads", loads, loads
                ? $"the game's reader takes it: version {back.SnapshotVersion}, {Chambers(back).Count} seats, every chamber unknown"
                : (back == null ? $"the game's reader refused it ({why})" : "a seat came back with a known chamber"));
        }

        // The guard on the lobby write.
        bool guard = !Allows(DeckPrefix + "_0", "{\"x\":1}", true, false)
                     && Allows(DeckPrefix + "_0", "{\"x\":1}", true, true)
                     && Allows(DeckPrefix + "_0", "", true, false)
                     && Allows(DeckPrefix + "_n", "3", true, false)
                     && Allows(DeckPrefix + "_0", "{\"x\":1}", false, false)
                     && Allows("playercount", "4", true, false);
        Fair.Result("snapshot-guard", guard, guard
            ? "a stray chunk is held back only at a hidden-hands match; clearing, the count and other keys pass"
            : "the lobby-write guard decided wrongly");

        // What the lobby holds now (read only), when there is a Steam lobby.
        if (LobbyId == 0)
        {
            Plugin.Log.LogInfo($"{Fair.Tag} test: no Steam lobby here (loopback harness?) - lobby checks not run");
            return;
        }

        if (rest == "clear")
        {
            if (!hideNow) { Plugin.Log.LogInfo($"{Fair.Tag} test: hidden hands are off this match - clear not run"); }
            else
            {
                ClearEarlier();
                string left = Published();
                Fair.Result("snapshot-clear", string.IsNullOrEmpty(left), string.IsNullOrEmpty(left)
                    ? "the lobby holds no snapshot after the clear"
                    : $"{left.Length} characters still on the lobby");
            }
        }

        string onLobby = Published();
        if (hideNow)
        {
            bool clean = Clean(onLobby, out why);
            Fair.Result("snapshot-lobby", clean, clean
                ? $"the lobby holds {(string.IsNullOrEmpty(onLobby) ? "no snapshot" : $"a cleaned snapshot of {onLobby.Length} characters")}"
                : why);
        }
        else
        {
            Plugin.Log.LogInfo($"{Fair.Tag} test: hidden hands off - the lobby holds the game's own snapshot " +
                               $"({(string.IsNullOrEmpty(onLobby) ? "none" : $"{onLobby.Length} characters")}), as it should");
        }
    }

    private static List<int> Chambers(HostMigration.DeckMigrationData d)
    {
        var list = new List<int>();
        var seats = d.Players;
        if (seats == null) return list;
        for (int i = 0; i < seats.Count; i++)
            if (seats[i] != null) list.Add(seats[i].RevolverBullet);
        return list;
    }

    /// <summary>
    /// The snapshot as the game would serialize it here: this match's live one when the host is in
    /// a Deck match, otherwise a made-up table (every field the game writes, with a known chamber
    /// for each seat). Falls back to hand-written text, with a field that is not on the kept list,
    /// when the game's JSON writer can't be reached.
    /// </summary>
    private static string SampleSnapshot(out string source)
    {
        source = "";
        try
        {
            if (NetworkServer.active)
            {
                var deck = UnityEngine.Object.FindObjectOfType<DeckGamePlayManager>();
                if (deck != null)
                {
                    var live = deck.BuildMigrationSnapshot();
                    string text = live == null ? null : ToJson(live);
                    if (!string.IsNullOrEmpty(text)) { source = "this match's live snapshot"; return text; }
                }
            }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"{Fair.Tag} test: live snapshot unavailable: {e.Message}"); }

        try
        {
            string text = ToJson(MadeUp());
            if (!string.IsNullOrEmpty(text)) { source = "a made-up table, through the game's JSON writer"; return text; }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"{Fair.Tag} test: the game's JSON writer is unavailable: {e.Message}"); }

        source = "a made-up table, written by hand";
        return MadeUpText();
    }

    private static HostMigration.DeckMigrationData MadeUp()
    {
        var d = new HostMigration.DeckMigrationData();
        d.SnapshotVersion = 1;
        d.Phase = 2;
        d.RoundCard = 1;
        d.CardsOnTable = 2;
        d.ActivePlayerSlot = 1;
        d.GameStarted = true;
        d.Players = new Il2CppSystem.Collections.Generic.List<HostMigration.DeckPlayerMigrationData>();
        for (int i = 0; i < 3; i++)
        {
            var p = new HostMigration.DeckPlayerMigrationData();
            p.SteamId = 1000UL + (ulong)i;
            p.Slot = i;
            p.PlayerName = $"seat {i}";
            p.Health = 1;
            p.HaveCards = true;
            p.CurrentRevolver = 1;
            p.RevolverBullet = 3 + i % 2;
            d.Players.Add(p);
        }
        return d;
    }

    private static string MadeUpText() =>
        "{\"SnapshotVersion\":1,\"Phase\":2,\"DeckMode\":0,\"RoundCard\":1,\"CardsOnTable\":2,\"ActivePlayerSlot\":1," +
        "\"CountDown\":10.5,\"LastBetPlayerSlot\":0,\"GameStarted\":true,\"JokerSettled\":false," +
        "\"FinishWithRemainingPlayer\":false,\"FinishTriggered\":false,\"Extra\":[0,2]," +
        "\"Players\":[" +
        "{\"SteamId\":1000,\"Slot\":0,\"PlayerName\":\"seat 0\",\"Health\":1,\"Dead\":false,\"Winner\":false,\"HaveTurn\":false,\"Fnished\":false,\"HaveCards\":true,\"RevolverBullet\":3,\"CurrentRevolver\":1,\"LastAnimStateHash\":0,\"LastAnimNormalizedTime\":0.0,\"Extra\":[0,1,2,3]}," +
        "{\"SteamId\":1001,\"Slot\":1,\"PlayerName\":\"seat 1\",\"Health\":1,\"Dead\":false,\"Winner\":false,\"HaveTurn\":true,\"Fnished\":false,\"HaveCards\":true,\"RevolverBullet\":4,\"CurrentRevolver\":1,\"LastAnimStateHash\":0,\"LastAnimNormalizedTime\":0.0,\"Extra\":[1,1,2]}" +
        "]}";

    // The game's own JSON writer and reader (Newtonsoft, in the game), found by name: the mod has
    // no build reference to it. For the test only; the hooks above work on the text alone.
    private static MethodInfo _serialize, _deserialize;
    private static bool _looked;

    private static void FindJson()
    {
        if (_looked) return;
        _looked = true;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t = null;
            try { t = asm.GetType("Newtonsoft.Json.JsonConvert", false) ?? asm.GetType("Il2CppNewtonsoft.Json.JsonConvert", false); }
            catch { }
            if (t == null) continue;

            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.IsGenericMethodDefinition) continue;
                var ps = m.GetParameters();
                if (m.Name == "SerializeObject" && ps.Length == 1) _serialize ??= m;
                if (m.Name == "DeserializeObject" && ps.Length == 2 && ps[0].ParameterType == typeof(string)
                    && ps[1].ParameterType == typeof(Il2CppSystem.Type)) _deserialize ??= m;
            }
            if (_serialize != null) return;
        }
    }

    private static string ToJson(Il2CppSystem.Object o)
    {
        FindJson();
        if (_serialize == null) throw new InvalidOperationException("JsonConvert.SerializeObject not found");
        return _serialize.Invoke(null, new object[] { o }) as string;
    }

    private static HostMigration.DeckMigrationData FromJson(string json, out string why)
    {
        why = "";
        try
        {
            FindJson();
            if (_deserialize == null) { why = "JsonConvert.DeserializeObject not found"; return null; }
            var o = _deserialize.Invoke(null, new object[] { json, Il2CppType.Of<HostMigration.DeckMigrationData>() }) as Il2CppSystem.Object;
            return o?.TryCast<HostMigration.DeckMigrationData>();
        }
        catch (Exception e) { why = e.InnerException?.Message ?? e.Message; return null; }
    }
}
