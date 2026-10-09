using System;
using System.Collections.Generic;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Everybody who arrives in your lobby is marked ready for you, once, so all the host has to
/// do is press Start.
///
/// With four players, waiting for everyone to find the ready button is a moment. With eight
/// it is the longest part of the lobby: the Start button stays greyed out until every single
/// player is ready, and somebody is always reading the mode description or AFK. Hosts asked
/// for the wait to go.
///
/// How the game does ready, read from the dump:
///
///   - The flag is <c>PlayerObjectController.Ready</c>, a SyncVar the server owns. A player's
///     ready button sends <c>SetReadyCMD</c>, and all the server does with it is set
///     <c>NetworkReady</c>. Setting <c>NetworkReady</c> here, on the host, is that same last
///     step - every peer is sent the new value by Mirror exactly as if the player had clicked.
///   - The host's Start button lights only when every lobby player is ready
///     (<c>LobbyController.Update</c>). Nothing else in the lobby reads the flag: choosing the
///     mode, the deck or dice variant and the bar only ask whether you are the host, and
///     changing character (<c>SetSkin</c>, <c>SetSkinBack</c>, <c>CmdSetSkinIndex</c>, the
///     click target) never looks at it. Being ready locks nothing.
///   - The ready button toggles from that same synced flag (<c>setReady</c> sends
///     <c>!Ready</c>), so a player readied this way sees "ready" and one press un-readies them.
///     Opening the character editor un-readies a player too - the game's own doing.
///   - The podium's ready icon follows the player: the host's own code copies the flag onto
///     the podium every frame, and <see cref="LobbyPodiums"/> does the same for the extra
///     podiums on every client. So the icon is right on all of them, fifth seat and beyond.
///   - The host is ready from the start and held there by the game every frame, and coming
///     back from a match the game clears everybody else's flag on purpose.
///
/// It runs on the host, not on each joining player. The host is the only one allowed to set
/// the flag directly, and doing it there means it works whatever build the people joining
/// are on - a player still on last week's version is readied just the same. A version that
/// ran on each client and pressed its own button would do nothing at all for anyone who had
/// not updated, which in practice is whoever needs it most.
///
/// Each player is readied once per visit to the lobby: when they join, and again when the
/// table comes back to the lobby after a match, because the game has just un-readied them and
/// otherwise the host is back to waiting. It is never held: un-ready and you stay un-ready.
/// The host is never touched, and nor are bots - <see cref="BotManager"/> holds those itself.
///
/// "Arrived" means the game has finished with them: they have a podium, they are not still
/// on their way back from a match (the game clears the flag the moment they land, so a ready
/// set before that is simply wiped), and their copy of the game has finished loading the
/// lobby. That has to stay true for a second before they are readied, so nothing the game is
/// still doing on arrival can undo it.
///
/// Ranked matchmaking lobbies count down on their own and the Velvet Room has a lobby of its
/// own; neither is touched.
/// </summary>
internal static class AutoReady
{
    /// <summary>How often the roster is looked at. A join is not a frame-perfect event.</summary>
    private const float Every = 0.25f;

    /// <summary>How long a player must have been settled in the lobby before being readied.</summary>
    private const float Settle = 1f;

    private static int _lobby;
    private static float _next;
    private static CustomNetworkManager _nm;
    private static bool _announced;

    /// <summary>Players already readied (or who readied themselves) on this visit, by netId.</summary>
    private static readonly HashSet<uint> _done = new();

    /// <summary>When each not-yet-readied player was first seen settled, by netId.</summary>
    private static readonly Dictionary<uint, float> _settledSince = new();

    [HarmonyPostfix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.Update))]
    private static void Tick(LobbyController __instance)
    {
        try
        {
            if (__instance == null) return;
            if (Plugin.AutoReadyJoiners == null || !Plugin.AutoReadyJoiners.Value) return;

            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + Every;

            // Only the host owns the flag. On a client this is a cheap no-op.
            if (!NetworkServer.active) return;

            // A new lobby is a new scene: everybody in it has arrived again.
            int lobby = __instance.GetInstanceID();
            if (lobby != _lobby)
            {
                _lobby = lobby;
                _done.Clear();
                _settledSince.Clear();
                _nm = null;
            }

            if (_nm == null) _nm = UnityEngine.Object.FindObjectOfType<CustomNetworkManager>();
            var nm = _nm;
            if (nm == null || nm.GamePlayers == null) return;
            if (nm.Matchmaking || nm.isVelvetRoom) return;

            if (!_announced)
            {
                _announced = true;
                Plugin.Log.LogInfo("[autoready] on - players arriving in this lobby are marked ready once " +
                                   "(they can un-ready with the game's own button; AutoReadyJoiners turns it off)");
            }

            var host = __instance.LocalPlayerController;

            foreach (var p in nm.GamePlayers)
            {
                if (p == null) continue;

                // Never the host: the game keeps the host ready by itself anyway.
                if (p.isOwned || (host != null && p == host)) continue;

                // Bots have no client and are held ready by BotManager every frame.
                if (p.ConnectionID < 0 || BotManager.IsBot(p)) continue;

                if (p.Kicked) continue;

                uint key = p.netId;
                if (key == 0 || _done.Contains(key)) continue;

                if (!Arrived(p)) { _settledSince.Remove(key); continue; }

                // Readied themselves before this got to them: that is their arrival dealt with.
                if (p.Ready) { _done.Add(key); _settledSince.Remove(key); continue; }

                if (!_settledSince.TryGetValue(key, out float since)) { _settledSince[key] = now; continue; }
                if (now - since < Settle) continue;

                _done.Add(key);
                _settledSince.Remove(key);

                // The server's half of SetReadyCMD, word for word.
                p.NetworkReady = true;

                Plugin.Log.LogInfo($"[autoready] readied the player on podium {p.SlotName} " +
                                   $"(connection {p.ConnectionID}) - {CountReady(nm)}/{nm.GamePlayers.Count} ready");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[autoready] {e.Message}");
            _next = Time.unscaledTime + 10f;   // a broken pass must not repeat four times a second
        }
    }

    /// <summary>
    /// Whether the game has finished bringing this player into the lobby.
    ///
    /// <c>Loaded</c> is the game's "has been in a match" mark. Back in the lobby, the host's
    /// code sees it, puts the player back on their podium and clears both it and their ready
    /// flag in one go - so while it is still set, a ready would be wiped. No podium name means
    /// the game has not seated them yet. A connection that is not ready is still loading the
    /// lobby scene, and would not see anything it was sent.
    /// </summary>
    private static bool Arrived(PlayerObjectController p)
    {
        if (p.Loaded) return false;
        if (string.IsNullOrEmpty(p.SlotName)) return false;

        var conn = p.connectionToClient;
        return conn != null && conn.isReady;
    }

    private static int CountReady(CustomNetworkManager nm)
    {
        int n = 0;
        foreach (var p in nm.GamePlayers) if (p != null && p.Ready) n++;
        return n;
    }
}
