using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Player names and chat at a table this copy hosts: every name is the host's to keep tidy, and
/// a chat line always carries the name of the player who really sent it.
///
/// <b>Names.</b> When a player's copy tells the host its name, the host keeps one tidy form of it
/// (<see cref="Canon"/>): one line, no markup, nothing invisible, no longer than the game shows
/// at the table, and never the same as another person's name in the room, so every seat keeps a
/// name of its own wherever the game looks a seat up by name. A name that clashes gets a number
/// (" 2", " 3"). Bots already step aside for people (<see cref="BotManager"/>), so they are not
/// counted here. The tidy name is set in the same frame, so only it ever goes out to the table.
///
/// <b>Chat.</b> A chat line from another player's copy is shown under that player's own name as
/// the host keeps it, whatever name came with the line, so nobody can speak as somebody else.
/// A burst far faster than anyone types is dropped quietly.
///
/// Neither ever removes a player: a strange name or a fast typist is not a cheat. The host's
/// own name and lines, and the bots', are left exactly as they are.
/// </summary>
[FairPatch("names stay tidy and distinct, and chat carries the sender's real name")]
[HarmonyPatch]
internal static class FairNames
{
    /// <summary>The longest name the game shows at the table, and in chat.</summary>
    internal const int NameMax = 11;

    /// <summary>The game's own word for a player with no name.</summary>
    internal const string NoName = "Player";

    /// <summary>More chat lines than this inside <see cref="ChatSpan"/> seconds is more than anybody types.</summary>
    internal const int ChatBurst = 10;
    internal const float ChatSpan = 5f;

    // ------------------------------------------------------------------ the one tidy form of a name

    /// <summary>
    /// The one form of a name the host keeps and shows: invisible and line-breaking characters
    /// become spaces, angle brackets go (no markup), runs of spaces become one, the ends are
    /// trimmed, and it is cut to <see cref="NameMax"/> without splitting a two-part character.
    /// Nothing left means <see cref="NoName"/>. Shared by the name check and the chat check.
    /// </summary>
    internal static string Canon(string raw) => Cut(Squeeze(raw), NameMax);

    private static string Squeeze(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(Math.Min(raw.Length, 64));
        bool space = false;
        foreach (char c in raw)
        {
            if (sb.Length >= 64) break;   // far past anything shown; the cut below decides the rest
            bool blank;
            switch (char.GetUnicodeCategory(c))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.SpaceSeparator:
                    blank = true;
                    break;
                default:
                    blank = char.IsWhiteSpace(c);
                    break;
            }
            if (blank)
            {
                space = sb.Length > 0;
                continue;
            }
            if (c == '<' || c == '>') continue;
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Cut(string s, int max)
    {
        if (s.Length > max)
        {
            int n = max;
            if (n > 0 && char.IsHighSurrogate(s[n - 1])) n--;
            s = s.Substring(0, n);
        }
        // A lone half of a two-part character shows as a box; it goes.
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[i + 1]); i++; }
                continue;
            }
            if (char.IsLowSurrogate(c)) continue;
            sb.Append(c);
        }
        s = sb.ToString().Trim();
        return s.Length == 0 ? NoName : s;
    }

    /// <summary>
    /// The tidy name made distinct from the names already taken (compared without regard to
    /// case): the first of "name", "name 2", "name 3" ... that is free, cut so it still fits.
    /// </summary>
    internal static string Distinct(string canon, ICollection<string> taken)
    {
        if (!Taken(canon, taken)) return canon;
        for (int n = 2; n < 100; n++)
        {
            string tail = " " + n.ToString(CultureInfo.InvariantCulture);
            string head = canon.Length + tail.Length > NameMax ? Cut(canon, NameMax - tail.Length) : canon;
            string candidate = head + tail;
            if (!Taken(candidate, taken)) return candidate;
        }
        return canon;   // a room of a hundred namesakes: leave it, nothing better is free
    }

    private static bool Taken(string name, ICollection<string> taken)
    {
        foreach (var t in taken)
            if (string.Equals(t, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The names of the other people in the room: everybody but this player and the bots.</summary>
    private static List<string> OthersNames(PlayerObjectController me)
    {
        var names = new List<string>();
        var nm = Fair.Net;
        if (nm == null || nm.GamePlayers == null) return names;
        foreach (var p in nm.GamePlayers)
        {
            if (p == null || BotManager.IsBot(p)) continue;
            if (me != null && p.netId == me.netId) continue;
            if (!string.IsNullOrEmpty(p.PlayerName)) names.Add(p.PlayerName);
        }
        return names;
    }

    // ------------------------------------------------------------------ names

    /// <summary>
    /// A player's copy has told the host its name and the game has stored it: the host replaces
    /// it with the tidy, distinct form at once, before anything is sent to the table. The name's
    /// body is compiled into Mirror's handler, so the handler is what is patched.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerObjectController), nameof(PlayerObjectController.InvokeUserCode_CmdSetPlayerName__String))]
    private static void NameSet(NetworkBehaviour obj, NetworkConnectionToClient senderConnection)
    {
        try
        {
            if (!Fair.Hosting) return;
            PlayerObjectController poc = null;
            try { poc = obj != null ? obj.TryCast<PlayerObjectController>() : null; } catch { }
            var conn = FairLegal.RemoteOwner(poc, senderConnection);
            if (conn == null || BotManager.IsBot(poc)) return;

            string had = poc.PlayerName;
            string keep = Distinct(Canon(had), OthersNames(poc));
            if (string.Equals(had, keep, StringComparison.Ordinal)) return;

            poc.NetworkPlayerName = keep;
            Plugin.Log.LogInfo($"{Fair.Tag} connection {conn.connectionId}: name kept in its tidy, distinct form");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"{Fair.Tag} could not tidy a player's name: {e.Message}"); }
    }

    // ------------------------------------------------------------------ chat

    private static readonly Dictionary<int, Queue<float>> _lines = new();

    /// <summary>Whether one more line from this connection, now, is part of a burst nobody types.</summary>
    internal static bool Burst(int connectionId, float now)
    {
        if (!_lines.TryGetValue(connectionId, out var q)) _lines[connectionId] = q = new Queue<float>();
        while (q.Count > 0 && q.Peek() <= now - ChatSpan) q.Dequeue();
        if (q.Count >= ChatBurst) return true;
        q.Enqueue(now);
        return false;
    }

    /// <summary>The name the host shows on a chat line from this player: its own name, as the host keeps it.</summary>
    internal static string SenderName(ChatNetwork chat, NetworkConnectionToClient conn)
    {
        PlayerObjectController poc = null;
        try { poc = chat != null ? chat.GetComponent<PlayerObjectController>() : null; } catch { }
        if (poc == null || poc.connectionToClient == null || poc.connectionToClient.connectionId != conn.connectionId)
            poc = FairLegalTest.PlayerOf(conn);
        if (poc != null && !string.IsNullOrEmpty(poc.PlayerName)) return Canon(poc.PlayerName);

        var seat = FairLegalTest.SeatOf(conn);
        return Canon(seat != null ? seat.PlayerName : null);
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    [HarmonyPatch(typeof(ChatNetwork), nameof(ChatNetwork.UserCode_CmdSendMessage__String__String))]
    private static bool ChatLine(ChatNetwork __instance, ref string playerName)
    {
        try
        {
            if (!Fair.Hosting) return true;
            var conn = Fair.Sender;
            if (!Fair.IsRemote(conn)) return true;

            if (Burst(conn.connectionId, Time.unscaledTime))
            {
                Fair.Refused($"connection {conn.connectionId}: chat lines faster than anybody types");
                return false;
            }
            playerName = SenderName(__instance, conn);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"{Fair.Tag} chat check failed, the game's own handling runs: {e.Message}");
            return true;
        }
    }

    // ------------------------------------------------------------------ upkeep and tests

    private static void Register()
    {
        Fair.OnTick("chat record", () => { if (!NetworkServer.active && _lines.Count > 0) _lines.Clear(); });
        Fair.OnDrive("name", TestNames);
        Fair.OnDrive("chat", TestChat);
    }

    /// <summary>
    /// "fair name": the tidy form and the numbering against a table of made-up names.
    /// "fair name room": whether every person's name in the room is already in its tidy,
    /// distinct form (connection ids only, never the names).
    /// </summary>
    private static void TestNames(string rest)
    {
        var (words, _) = FairLegalTest.Parse(rest);
        const string T = "name";
        if (FairLegalTest.Has(words, "room")) { Room(); return; }

        int failed = 0;
        FairLegalTest.Expect(T, "plain name", Canon("Zed"), "Zed", ref failed);
        FairLegalTest.Expect(T, "markup removed", Canon("<b>Zed</b>"), "bZed/b", ref failed);
        FairLegalTest.Expect(T, "line breaks and tabs become one space", Canon("Ann\n\tLee"), "Ann Lee", ref failed);
        FairLegalTest.Expect(T, "invisible characters go", Canon("Z​e‮d"), "Z e d", ref failed);
        FairLegalTest.Expect(T, "ends trimmed", Canon("   Zed   "), "Zed", ref failed);
        FairLegalTest.Expect(T, "cut to the length shown", Canon("ExampleLongPlayerName_X"), "ExampleLong", ref failed);
        FairLegalTest.Expect(T, "empty becomes the game's word", Canon(" ​ "), NoName, ref failed);
        FairLegalTest.Expect(T, "null becomes the game's word", Canon(null), NoName, ref failed);
        FairLegalTest.Expect(T, "a two-part character is never split", Canon("ABCDEFGHIJ\U0001F600"), "ABCDEFGHIJ", ref failed);
        FairLegalTest.Expect(T, "already tidy stays the same", Canon(Canon("Ann\n\tLee")), "Ann Lee", ref failed);

        var room = new List<string> { "Zed", "zed 2", "ExampleLong" };
        FairLegalTest.Expect(T, "a free name is kept", Distinct("Ann", room), "Ann", ref failed);
        FairLegalTest.Expect(T, "a clash is numbered past the taken numbers", Distinct("ZED", room), "ZED 3", ref failed);
        FairLegalTest.Expect(T, "a long clash is cut to fit its number", Distinct("ExampleLong", room), "ExampleLo 2", ref failed);

        Fair.Result(T, failed == 0, failed == 0 ? "every name came out as expected" : $"{failed} name(s) came out wrong");
    }

    private static void Room()
    {
        const string T = "name room";
        if (!Fair.Hosting) { Fair.Result(T, false, $"not hosting ({Fair.Why})"); return; }
        var nm = Fair.Net;
        if (nm == null || nm.GamePlayers == null) { Fair.Result(T, false, "no players"); return; }

        int people = 0, untidy = 0, clash = 0;
        var seen = new List<string>();
        foreach (var p in nm.GamePlayers)
        {
            if (p == null || BotManager.IsBot(p)) continue;
            people++;
            string name = p.PlayerName ?? "";
            bool remote = Fair.IsRemote(p.connectionToClient);
            if (remote && !string.Equals(Canon(name), name, StringComparison.Ordinal))
            {
                untidy++;
                Plugin.Log.LogInfo($"{Fair.Tag} test: connection {p.connectionToClient.connectionId} has a name not in its tidy form");
            }
            if (Taken(name, seen)) clash++;
            seen.Add(name);
        }
        Fair.Result(T, untidy == 0 && clash == 0, $"{people} people, {untidy} remote name(s) not tidy, {clash} clash(es)");
    }

    /// <summary>
    /// "fair chat": the burst limit and the shown name against made-up cases.
    /// "fair chat seat [conn]": whether the name the host would show on that player's chat line
    /// is their own name as the host keeps it (the name itself is never logged).
    /// </summary>
    private static void TestChat(string rest)
    {
        var (words, _) = FairLegalTest.Parse(rest);
        if (words.Count > 0 && words[0].Equals("seat", StringComparison.OrdinalIgnoreCase))
        {
            const string S = "chat seat";
            if (!Fair.Hosting) { Fair.Result(S, false, $"not hosting ({Fair.Why})"); return; }
            var conn = Fair.TestConnection(FairLegalTest.ConnArg(words, 1));
            if (conn == null) { Fair.Result(S, false, "no remote player connected"); return; }
            var poc = FairLegalTest.PlayerOf(conn);
            string shown = SenderName(null, conn);
            bool own = poc != null && shown == Canon(poc.PlayerName);
            Fair.Result(S, own, $"connection {conn.connectionId}: a chat line would carry {(own ? "its own name" : "a name that is not its own")}");
            return;
        }

        int failed = 0;
        const string T = "chat";
        const int fake = -900;   // a connection id no real connection has
        _lines.Remove(fake);
        bool burst = false;
        for (int i = 0; i < ChatBurst; i++) burst |= Burst(fake, 100f + i * 0.1f);
        FairLegalTest.Expect(T, $"{ChatBurst} lines in a second are let through", burst, false, ref failed);
        FairLegalTest.Expect(T, "one more in the same second is dropped", Burst(fake, 101.05f), true, ref failed);
        FairLegalTest.Expect(T, "a line after the burst has passed is let through", Burst(fake, 100f + ChatSpan + 1f), false, ref failed);
        _lines.Remove(fake);

        FairLegalTest.Expect(T, "a line's name is tidied the same way as a player's name", Canon("Ann\n<i>Lee</i>"), "Ann iLee/i", ref failed);
        Fair.Result(T, failed == 0, failed == 0 ? "every case came out as expected" : $"{failed} case(s) came out wrong");
    }
}
