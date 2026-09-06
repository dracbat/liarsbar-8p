using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Developer mode: the switch, the log format, and the handful of lookups every other
/// developer-facing class needs.
///
/// This exists because an eight player round cannot be tested with eight people on hand.
/// Everything under it is off unless <c>DeveloperMode</c> is set, and no part of it is
/// meant to be running in a real game.
/// </summary>
internal static class Dev
{
    internal static bool Enabled => Plugin.DeveloperMode != null && Plugin.DeveloperMode.Value;

    /// <summary>
    /// One line per event, tagged so a session can be read back by grepping a single tag:
    /// join, seat, deal, turn, dead, round, bot.
    /// </summary>
    internal static void Log(string tag, string message)
    {
        if (!Enabled) return;
        Plugin.Log.LogInfo($"[dev:{tag}] {message}");
    }

    internal static void Warn(string tag, string message)
    {
        if (!Enabled) return;
        Plugin.Log.LogWarning($"[dev:{tag}] {message}");
    }

    // ------------------------------------------------------------------- lookups

    internal static Manager Mgr
    {
        get { try { return Manager.Instance; } catch { return null; } }
    }

    internal static CustomNetworkManager Net
    {
        get
        {
            try { return UnityEngine.Object.FindObjectOfType<CustomNetworkManager>(); }
            catch { return null; }
        }
    }

    internal static LobbyController Lobby
    {
        get { try { return LobbyController.Instance; } catch { return null; } }
    }

    internal static bool IsServer
    {
        get { try { return Mirror.NetworkServer.active; } catch { return false; } }
    }

    /// <summary>Lobby-side player objects — one per person (and per bot).</summary>
    internal static List<PlayerObjectController> LobbyPlayers()
    {
        var found = new List<PlayerObjectController>();
        try
        {
            var nm = Net;
            if (nm == null || nm.GamePlayers == null) return found;
            foreach (var p in nm.GamePlayers) if (p != null) found.Add(p);
        }
        catch { }
        return found;
    }

    /// <summary>In-game seated players. Empty outside a match.</summary>
    internal static List<PlayerStats> TablePlayers()
    {
        var found = new List<PlayerStats>();
        try
        {
            var m = Mgr;
            if (m == null || m.Players == null) return found;
            foreach (var p in m.Players) if (p != null) found.Add(p);
        }
        catch { }
        return found;
    }

    internal static DeckGamePlayManager Deck
    {
        get
        {
            try
            {
                var m = Mgr;
                return m != null ? m.DeckGamePlayManager : null;
            }
            catch { return null; }
        }
    }

    /// <summary>Short, stable description of a lobby player for a log line.</summary>
    internal static string Describe(PlayerObjectController p)
    {
        if (p == null) return "<null>";
        try
        {
            string bot = BotManager.IsBot(p) ? " BOT" : "";
            return $"'{p.PlayerName}'{bot} id={p.PlayerIdNumber} conn={p.ConnectionID} " +
                   $"seat={p.InGameSlot} podium={(string.IsNullOrEmpty(p.SlotName) ? "-" : p.SlotName)} " +
                   $"ready={p.Ready}";
        }
        catch (Exception e) { return $"<unreadable: {e.Message}>"; }
    }

    /// <summary>Short description of a seated player for a log line.</summary>
    internal static string Describe(PlayerStats p)
    {
        if (p == null) return "<null>";
        try
        {
            return $"'{p.PlayerName}' seat={p.Slot} hp={p.Health} " +
                   $"{(p.Dead ? "DEAD" : "alive")}{(p.HaveTurn ? " <-TURN" : "")}";
        }
        catch (Exception e) { return $"<unreadable: {e.Message}>"; }
    }
}
