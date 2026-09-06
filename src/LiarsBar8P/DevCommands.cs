using System;
using System.Text;

namespace LiarsBar8P;

/// <summary>
/// The developer commands, each one a question that was previously answered by reading a
/// log after the fact: who is here, where are they sitting, whose turn is it, what is in
/// the deck. They print rather than return, because the log is what gets read back.
/// </summary>
internal static class DevCommands
{
    internal static void PrintPlayerList()
    {
        var lobby = Dev.LobbyPlayers();
        var table = Dev.TablePlayers();

        var sb = new StringBuilder();
        sb.AppendLine($"players: {lobby.Count} in the lobby, {table.Count} at the table");

        for (int i = 0; i < lobby.Count; i++)
            sb.AppendLine($"  lobby[{i}] {Dev.Describe(lobby[i])}");

        for (int i = 0; i < table.Count; i++)
            sb.AppendLine($"  table[{i}] {Dev.Describe(table[i])}");

        if (lobby.Count == 0 && table.Count == 0) sb.AppendLine("  (nobody)");
        Dev.Log("cmd", sb.ToString().TrimEnd());
    }

    internal static void PrintSeats()
    {
        var sb = new StringBuilder();
        try
        {
            var m = Dev.Mgr;
            if (m == null) { Dev.Log("cmd", "seats: not in a match"); return; }

            int slots = m.Slots != null ? m.Slots.Count : -1;
            int plates = m.NameTexts != null ? m.NameTexts.Count : -1;
            sb.AppendLine($"seats: {slots} seat transforms, {plates} nameplates, " +
                          $"StartPlayerCount={m.StartPlayerCount}, ActivePlayerSlot={m.ActivePlayerSlot}");

            if (m.Slots != null)
                for (int i = 0; i < m.Slots.Count; i++)
                {
                    var t = m.Slots[i];
                    if (t == null) { sb.AppendLine($"  seat[{i}] <null>"); continue; }
                    string who = "empty";
                    foreach (var p in Dev.TablePlayers())
                        if (p != null && p.Slot == i) { who = Dev.Describe(p); break; }
                    sb.AppendLine($"  seat[{i}] {t.position.ToString("F2")} active={t.gameObject.activeSelf} :: {who}");
                }

            // A seat nobody occupies, or two players on one seat, is the shape of every
            // turn-order bug this mod has had.
            var seen = new System.Collections.Generic.Dictionary<int, int>();
            foreach (var p in Dev.TablePlayers())
            {
                if (p == null) continue;
                seen.TryGetValue(p.Slot, out int c);
                seen[p.Slot] = c + 1;
            }
            foreach (var kv in seen)
                if (kv.Value > 1) sb.AppendLine($"  !! seat {kv.Key} holds {kv.Value} players");
        }
        catch (Exception e) { sb.AppendLine($"  failed: {e.Message}"); }

        Dev.Log("cmd", sb.ToString().TrimEnd());
    }

    internal static void PrintPodiums()
    {
        var sb = new StringBuilder();
        try
        {
            var lc = Dev.Lobby;
            if (lc == null || lc.SpawnSlots == null) { Dev.Log("cmd", "podiums: not in a lobby"); return; }

            sb.AppendLine($"podiums: {lc.SpawnSlots.Count}");
            for (int i = 0; i < lc.SpawnSlots.Count; i++)
            {
                var s = lc.SpawnSlots[i];
                if (s == null) { sb.AppendLine($"  [{i}] <null>"); continue; }
                string who = "-";
                foreach (var p in Dev.LobbyPlayers())
                    if (p != null && p.SlotName == s.gameObject.name) { who = p.PlayerName; break; }
                sb.AppendLine($"  [{i}] {s.gameObject.name} taken={s.Dolu} ready={s.Ready} " +
                              $"name='{s.PlayerName}' :: {who}");
            }
        }
        catch (Exception e) { sb.AppendLine($"  failed: {e.Message}"); }

        Dev.Log("cmd", sb.ToString().TrimEnd());
    }

    internal static void PrintTurn()
    {
        var sb = new StringBuilder();
        try
        {
            var m = Dev.Mgr;
            if (m == null) { Dev.Log("cmd", "turn: not in a match"); return; }

            sb.AppendLine($"turn: ActivePlayerSlot={m.ActivePlayerSlot} " +
                          $"StartPlayerCount={m.StartPlayerCount} countdown={m.CountDown:F1}");

            bool anyone = false;
            foreach (var p in Dev.TablePlayers())
            {
                if (p == null || !p.HaveTurn) continue;
                anyone = true;
                sb.AppendLine($"  has the turn: {Dev.Describe(p)}");
                if (p.Slot != m.ActivePlayerSlot)
                    sb.AppendLine($"  !! that player is on seat {p.Slot} but the active slot is " +
                                  $"{m.ActivePlayerSlot} - the indicator and the turn disagree");
            }
            if (!anyone) sb.AppendLine("  nobody currently holds the turn");
        }
        catch (Exception e) { sb.AppendLine($"  failed: {e.Message}"); }

        Dev.Log("cmd", sb.ToString().TrimEnd());
    }

    internal static void PrintDeck()
    {
        var sb = new StringBuilder();
        try
        {
            var d = Dev.Deck;
            if (d == null) { Dev.Log("cmd", "deck: not in a Liar's Deck match"); return; }

            sb.AppendLine($"deck: MasaCards={Count(d.MasaCards)} ResetCards={Count(d.ResetCards)} " +
                          $"OpenCards={Count(d.OpenCards)} ExtraCards={Count(d.ExtraCards)}");
            sb.AppendLine($"      round card={d.RoundCard} patched deck size={DeckSizePatch.CurrentSize}");

            foreach (var p in Dev.TablePlayers())
            {
                if (p == null) continue;
                var gp = p.GetComponent<DeckGameplay>();
                if (gp == null) continue;
                sb.AppendLine($"  {p.PlayerName}: {Count(gp.cardTypes)} cards {Types(gp.cardTypes)} " +
                              $"haveCards={gp.HaveCards}");
            }
        }
        catch (Exception e) { sb.AppendLine($"  failed: {e.Message}"); }

        Dev.Log("cmd", sb.ToString().TrimEnd());
    }

    /// <summary>
    /// Hand the turn on without playing. For getting past a bot or a player that has
    /// stalled, so a round can be pushed to the next thing worth watching.
    /// </summary>
    internal static void SkipTurn()
    {
        try
        {
            if (!Dev.IsServer) { Dev.Warn("cmd", "skip turn: only the host can move the turn on"); return; }

            var m = Dev.Mgr;
            if (m == null) { Dev.Warn("cmd", "skip turn: not in a match"); return; }

            int before = m.ActivePlayerSlot;
            foreach (var p in Dev.TablePlayers())
                if (p != null && p.HaveTurn) p.NetworkHaveTurn = false;

            m.GiveTurn();
            Dev.Log("cmd", $"skip turn: {before} -> {m.ActivePlayerSlot}");
        }
        catch (Exception e) { Dev.Warn("cmd", $"skip turn failed: {e.Message}"); }
    }

    /// <summary>
    /// Start the match from here rather than by clicking, so a test run is one keypress and
    /// always takes the game's own start path.
    /// </summary>
    internal static void StartMatch()
    {
        try
        {
            var lc = Dev.Lobby;
            if (lc == null) { Dev.Warn("cmd", "start: not in a lobby"); return; }
            if (!Dev.IsServer) { Dev.Warn("cmd", "start: only the host can start"); return; }

            Dev.Log("cmd", $"starting the match with {Dev.LobbyPlayers().Count} players " +
                           $"({BotManager.Count} bots)");
            lc.StartGameOrReady();
        }
        catch (Exception e) { Dev.Warn("cmd", $"start failed: {e.Message}"); }
    }

    private static int Count<T>(Il2CppSystem.Collections.Generic.List<T> list) => list == null ? -1 : list.Count;

    private static string Types(Il2CppSystem.Collections.Generic.List<int> list)
    {
        if (list == null) return "";
        var sb = new StringBuilder("[");
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(Name(list[i]));
        }
        return sb.Append(']').ToString();
    }

    /// <summary>Card types as the game encodes them: 1 Ace, 2 King, 3 Queen, 4 Joker.</summary>
    private static string Name(int type) => type switch
    {
        1 => "A", 2 => "K", 3 => "Q", 4 => "J", _ => type.ToString()
    };
}
