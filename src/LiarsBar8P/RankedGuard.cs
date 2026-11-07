using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LiarsBar8P;

/// <summary>
/// Keeps a copy with the mod out of ranked.
///
/// Ranked is the one place a ban can reach. The game looks at a player's ban only when a ranked
/// search starts, and nothing played at a Steam lobby table goes anywhere it could be seen. But
/// nothing in the game stops a modded copy entering ranked either, and once there the mod would
/// change what the other players receive (emotes come faster) and put its tag on every clip of
/// the match. So the mod stands ranked down itself, on this copy only, before any ticket exists:
/// ranked carries on as it was for everybody else, and a player who wants it starts the game
/// without the mod.
///
/// There is no setting for this, on purpose. A switch would make a ban something the mod let
/// happen. The only way past it is the developer test switch in <see cref="Allowed"/>.
///
/// Three lines, each able to work without the others, so a game update that renames something
/// costs one of them rather than the block:
///
///   1. The ranked button. The ranked screen is never loaded, and the player is told why in the
///      lobby list's own box, which is a useful place to land anyway.
///   2. The search. No ticket from this copy, whatever route reached the ranked screen: the
///      mouse, a controller, or something nobody has found yet.
///   3. A watchdog from <see cref="ModTicker"/>, needing no patch. If the ranked screen is up
///      anyway, it goes back to the menu by the game's own way out. It looks for the ranked
///      screen's controller rather than the scene's name, so a renamed scene does not lose it.
/// </summary>
internal static class RankedGuard
{
    /// <summary>The ranked screen's scene.</summary>
    internal const string Scene = "MatchMaking";

    // Every word a player sees from this class.
    private const string Refusal = "Ranked is off while the mod is installed\nStart the game without it to play ranked";

    private const string SwitchName = "LIARSBAR8P_RANKED";

    /// <summary>How long the reason stays up.</summary>
    private const float SaySeconds = 8f;

    /// <summary>How often the watchdog looks. Finding an object by type is not cheap.</summary>
    private const float WatchEvery = 1f;

    /// <summary>How long the reason waits for the menu after the watchdog sends a player back.</summary>
    private const float MenuWait = 15f;

    private static bool? _variable;
    private static float _next;
    private static float _owedSince = -1f;
    private static string _note;
    private static string _noteWas;
    private static float _noteUntil;

    /// <summary>
    /// Ranked allowed on this copy: only inside the loopback test harness with developer mode on
    /// (<see cref="Fair.TestsAllowed"/>) and <c>LIARSBAR8P_RANKED=1</c> as well, for testing the
    /// ranked screen. A copy at a real table never gets this, whatever its settings.
    /// </summary>
    internal static bool Allowed => Fair.TestsAllowed && Switched;

    private static bool Switched
    {
        get
        {
            if (_variable == null)
            {
                try { _variable = Environment.GetEnvironmentVariable(SwitchName) == "1"; }
                catch { _variable = false; }
            }
            return _variable.Value;
        }
    }

    /// <summary>Developer test switch: let the button through, so the watchdog has something to catch.</summary>
    internal static bool SkipButtonForTest { get; set; }

    /// <summary>
    /// True on the ranked screen and all through a ranked match, and true when it cannot tell.
    /// The match half reads the game's own ranked flag; the scene's name covers the moment the
    /// network manager is being swapped during the scene change.
    /// </summary>
    internal static bool Near
    {
        get
        {
            try
            {
                var nm = Mirror.NetworkManager.singleton;
                if (nm != null && nm.Matchmaking) return true;
                return SceneManager.GetActiveScene().name == Scene;
            }
            catch { return true; }
        }
    }

    // ------------------------------------------------------------------ line 1: the button

    [HarmonyPatch]
    internal static class ButtonLine
    {
        /// <summary>The ranked button's only call. Nothing else in the game calls it.</summary>
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(LoadingInGame), nameof(LoadingInGame.LoadMatchmakingScene))]
        private static bool Refuse()
        {
            if (Allowed || (SkipButtonForTest && Fair.TestsAllowed)) return true;

            Plugin.Log.LogInfo("[ranked] refused - the ranked screen stays shut while the mod is installed");
            Tell();
            return false;
        }
    }

    // ------------------------------------------------------------------ line 2: the search

    [HarmonyPatch]
    internal static class SearchLine
    {
        /// <summary>Find Match, from the mouse and from a controller alike.</summary>
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(MatchmakerTicketer), nameof(MatchmakerTicketer.OnFindMatch))]
        private static bool NoTicket()
        {
            if (Allowed) return true;

            Plugin.Log.LogWarning("[ranked] a ranked search was stopped before it began");
            return false;
        }
    }

    // ------------------------------------------------------------------ line 3: the watchdog

    /// <summary>From <see cref="ModTicker"/>.</summary>
    internal static void Tick()
    {
        float now = Time.unscaledTime;
        PutNoteBack(now);

        if (Allowed || now < _next) return;
        _next = now + WatchEvery;

        var ranked = UnityEngine.Object.FindObjectOfType<MatchmakerTicketer>();
        if (ranked != null)
        {
            Plugin.Log.LogWarning("[ranked] the ranked screen opened anyway - back to the menu");
            _owedSince = now;
            ranked.ReturnMainMenu();
            return;
        }

        TellOnceBack(now);
    }

    /// <summary>
    /// After the watchdog, the reason, once the menu is back. The lobby list only exists once the
    /// menu has loaded, so this waits for it, and says it on the line under the tag if it never comes.
    /// </summary>
    private static void TellOnceBack(float now)
    {
        if (_owedSince < 0f) return;

        bool menu = SceneManager.GetActiveScene().name == Matchmaking.Menu;
        if (menu && LobbyListManager.instance != null)
        {
            _owedSince = -1f;
            Tell();
        }
        else if (now - _owedSince > MenuWait)
        {
            _owedSince = -1f;
            Note();
        }
    }

    // ------------------------------------------------------------------ saying why

    /// <summary>
    /// In the lobby list's own box, the one that says "Connecting". It only shows with the list
    /// open, so the list is opened for it. Where it cannot be, the line under the tag says it.
    /// </summary>
    private static void Tell()
    {
        try
        {
            var list = LobbyListManager.instance;
            if (list != null && QuickMatch.OpenList(list))
            {
                Matchmaking.Say(Words.T(Refusal), SaySeconds);
                return;
            }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[ranked] could not open the lobby list to say why: {e.Message}"); }

        Note();
    }

    /// <summary>The reason on the updater's line under the tag, for a few seconds, then the line as it was.</summary>
    private static void Note()
    {
        try
        {
            if (_note == null) _noteWas = AutoUpdate.Note;
            _note = Words.T(Refusal).Replace("\n", " - ");
            AutoUpdate.Note = _note;
            _noteUntil = Time.unscaledTime + SaySeconds;
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[ranked] could not say why: {e.Message}"); }
    }

    private static void PutNoteBack(float now)
    {
        if (_note == null || now < _noteUntil) return;
        if (AutoUpdate.Note == _note) AutoUpdate.Note = _noteWas;
        _note = null;
        _noteWas = null;
    }
}
