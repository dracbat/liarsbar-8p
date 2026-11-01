using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using Mirror;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LiarsBar8P;

/// <summary>
/// A pool of Steam lobbies of its own for players who have the mod, so an eight-player game no
/// longer takes seven friends.
///
/// The game's own lobby list could not do this, for reasons read from <c>GetLobbiesList</c>:
///
///   - It only asks Steam for lobbies holding fewer than four players, so a modded lobby left
///     every list - everybody's, modded players' included - the moment it reached four. The
///     fifth to eighth seats could only ever be filled by invitation.
///   - It only asks for lobbies whose date keys say they were written in the last fifteen
///     minutes of the same UTC day, which a host waiting for seven strangers can fall out of.
///   - It showed modded lobbies under four players to people without the mod, who could walk
///     in and desync the table.
///
/// There is no server and nothing is hosted by anyone: this is only Steam lobbies, and the
/// game's own browser and Quick Join.
///
/// <b>The tag.</b> A lobby's <c>version</c> key is the game's own list filter and nothing else:
/// one write in <c>OnLobbyCreated</c>, one filter in each of the two searches, and no reader
/// anywhere, joining included. So a host here publishes that value with <see cref="PoolSuffix"/>
/// on the end, and a search here asks for the same. Steam then does all the separating: players
/// without the mod are never sent these lobbies, and players with it are never sent theirs. Both
/// are rewritten at the call rather than from a stored copy of the game's version, so a game
/// update still splits the pool the way it splits the game's own. It is deliberately not in the
/// lobby name, which the host's word filter rewrites and the list's similarity rule compares.
///
/// Older releases of the mod publish the plain value, so they simply do not appear.
///
/// <b>Versions.</b> Everyone at a table has to run the same build, so the host also publishes
/// its mod version as lobby data (<see cref="VersionCheck.Key"/>, the name the per-member copy
/// has always used). A lobby on another version is shown with its version and refused with a
/// reason, rather than filtered out. With few players an empty list reads as "nobody plays
/// this", and the week after a release half the pool would have vanished in silence. A row that
/// says which version it needs tells a player exactly what to do about it. Quick Join is the
/// exception: nobody is choosing there, so it only ever looks at the player's own version.
///
/// <b>Lobbies whose host has gone.</b> Nothing in the game makes a client leave a Steam lobby
/// when it loses the host: only the leave and kick paths call <c>LeaveLobby</c>. So a host that
/// crashes leaves a lobby behind, and without the game's date filters nothing would ever expire
/// it. Two things deal with that here: <see cref="AliveKey"/>, a heartbeat every search filters
/// on, and every copy leaving a lobby it has no connection to (<see cref="LeaveDeadLobby"/>).
///
/// <b>The hosts' side.</b> Once strangers can find a lobby a few things the game lets slide
/// start to matter, so <see cref="Tick"/> keeps them true on the lobby's owner: see
/// <see cref="HostUpkeep"/>.
///
/// <b>Lobby data is written by strangers.</b> Every string in it comes from another player's
/// copy of the game, which can write anything of any length. <see cref="Clean"/> is what stands
/// between that and the screen, and everything reading it here is inside a try/catch that
/// leaves the game's own behaviour running.
///
/// None of this is applied in the loopback harness, where there is no Steam lobby at all.
/// <c>ShowUnmoddedLobbies</c> puts the game's own searches back; the lobbies this copy hosts
/// stay tagged either way, because a host with the mod is one a vanilla player must not join.
/// </summary>
internal static class Matchmaking
{
    /// <summary>On the end of the game's own value of <c>version</c>. Letters, digits and dots only, like the value itself.</summary>
    internal const string PoolSuffix = ".lb8p";

    internal const string VersionKey = "version";
    internal const string StateKey = "InGame";
    internal const string ModeKey = "Mode";
    internal const string InTheLobby = "lobby";
    internal const string Menu = "SteamTest";

    /// <summary>The host's heartbeat: a whole minute of Steam's own clock, filterable because it is a number.</summary>
    internal const string AliveKey = "lb8p_alive";

    private const string Off = "off";

    /// <summary>How long a refusal stays up, in the box that normally says "Connecting".</summary>
    private const float RefusalSeconds = 5f;

    /// <summary>How often lobbies are looked after.</summary>
    private const float HostEvery = 1f;

    /// <summary>How often the heartbeat is written.</summary>
    private const float AliveEvery = 60f;

    /// <summary>
    /// How many minutes old a heartbeat may be and still be listed. Several writes, so one that
    /// Steam was slow to pass on does not drop a lobby, and short enough that a lobby whose host
    /// has died is gone before many people have walked into it.
    /// </summary>
    private const int AliveWindow = 5;

    /// <summary>The most Steam will return, and what the game's own list already asks for.</summary>
    private const int ListLimit = 50;

    /// <summary>
    /// How long <c>InGame</c> has to sit at "off" in a running lobby before it is corrected. A
    /// host on the way out writes the same value and takes a few seconds to go.
    /// </summary>
    private const float OffGrace = 10f;

    /// <summary>
    /// How long somebody connected to this lobby has to show the mod before they are refused.
    /// A modded client publishes its version before it connects, so this is only there for
    /// Steam being slow to pass that on; being late here costs nothing but a few seconds.
    /// </summary>
    private const float ProveModSeconds = 12f;

    /// <summary>Between telling the room why somebody is being sent back and sending them.</summary>
    private const float ReadBeforeKick = 5f;

    /// <summary>How long Start waits for somebody who is in the Steam lobby but has no player yet.</summary>
    private const float ArrivingLimit = 15f;

    /// <summary>
    /// How long a connection can sit in a running match with no player object. Long, because
    /// the match scene can take most of a minute to load on a slower PC, and a connection
    /// dropped while it is still loading never reaches the menu: Mirror will not start a second
    /// scene load, and the player is left with nothing but closing the game.
    /// </summary>
    private const float StraySeconds = 120f;

    /// <summary>How long a line for the host stays up. The audit cannot touch it there.</summary>
    private const float NoteSeconds = 8f;

    private const string WhyHeldProving = "Checking who has the mod";
    private const string WhyHeldArriving = "A player is still arriving";
    private const string ToTheRoom = "This table needs the 8 Player Mod";
    private const string InviteFailed = "Could not join that lobby\nIt may be full or in a match";

    private static float _next;
    private static float _hideAt;
    private static float _offSince;
    private static float _nextAlive;
    private static float _noteUntil;
    private static float _comingSince;
    private static int _coming;
    private static bool? _open;
    private static bool _saidSearch;
    private static string _note;
    private static string _hold;
    private static ulong _invited;
    private static ulong _entered;
    private static readonly Dictionary<ulong, float> _unproven = new();
    private static readonly HashSet<ulong> _told = new();
    private static readonly HashSet<ulong> _seen = new();
    private static readonly Dictionary<int, float> _strays = new();

    /// <summary>
    /// Whether searches belong to the modded pool. False when the player asked for the game's
    /// normal list back, in the loopback harness, and in the self test that proves the normal
    /// list cannot see these lobbies.
    /// </summary>
    internal static bool Active =>
        !Loopback.Active && !MatchmakingSelfTest.PlainList &&
        (Plugin.ShowUnmoddedLobbies == null || !Plugin.ShowUnmoddedLobbies.Value);

    /// <summary>The whole value of <c>version</c> this copy publishes, once it has seen the game's own.</summary>
    internal static string PoolValue { get; private set; }

    private static string InPool(string gameValue) =>
        gameValue.EndsWith(PoolSuffix, StringComparison.Ordinal) ? gameValue : gameValue + PoolSuffix;

    /// <summary>
    /// Whether Steam is running, asked of the field itself. <c>SteamManager.Initialized</c> is
    /// not a question that can be asked from a ticker: when there is no manager it makes one,
    /// and the ranked scene destroys every manager on purpose, shutting Steam down each time.
    /// </summary>
    private static bool SteamUp
    {
        get
        {
            var steam = SteamManager.s_instance;
            return steam != null && steam.m_bInitialized;
        }
    }

    /// <summary>
    /// The current minute by Steam's clock. Both ends of the heartbeat read the same clock, so
    /// it does not matter what the time is on anybody's PC.
    /// </summary>
    private static int AliveMinute => (int)(SteamUtils.GetServerRealTime() / 60u);

    private static void Heartbeat(CSteamID lobby)
    {
        SteamMatchmaking.SetLobbyData(lobby, AliveKey, AliveMinute.ToString(CultureInfo.InvariantCulture));
        _nextAlive = Time.unscaledTime + AliveEvery;
    }

    // ------------------------------------------------------------------- the tag

    /// <summary>
    /// Every write of lobby data comes through here, about one every couple of seconds, and
    /// only the single <c>version</c> write in <c>OnLobbyCreated</c> is changed.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.SetLobbyData))]
    private static void TagVersion(string pchKey, ref string pchValue)
    {
        try
        {
            if (pchKey != VersionKey || string.IsNullOrEmpty(pchValue)) return;
            pchValue = InPool(pchValue);
            PoolValue = pchValue;
        }
        catch (Exception e) { Plugin.Log.LogError($"[matchmaking] could not tag the lobby: {e.Message}"); }
    }

    /// <summary>
    /// The mod's version and the first heartbeat beside the tag, from the one moment the lobby
    /// is certainly ours and certainly new. The callback's own id, because
    /// <c>CurrentLobbyID</c> is not set yet. No server means the game's handler was skipped:
    /// the player backed out of this lobby, and <see cref="QuickMatch"/> has already left it.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.OnLobbyCreated))]
    private static void Published(LobbyCreated_t callback)
    {
        try
        {
            if (callback.m_eResult != EResult.k_EResultOK || !NetworkServer.active) return;

            var lobby = new CSteamID(callback.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(lobby, VersionCheck.Key, Plugin.Version);
            Heartbeat(lobby);

            Plugin.Log.LogInfo($"[matchmaking] hosting lobby {callback.m_ulSteamIDLobby}: " +
                               $"{VersionKey}={SteamMatchmaking.GetLobbyData(lobby, VersionKey)} " +
                               $"{VersionCheck.Key}={Plugin.Version} " +
                               $"{AliveKey}={SteamMatchmaking.GetLobbyData(lobby, AliveKey)} " +
                               $"seats={SteamMatchmaking.GetLobbyMemberLimit(lobby)}");
        }
        catch (Exception e) { Plugin.Log.LogError($"[matchmaking] could not publish the lobby: {e.Message}"); }
    }

    // --------------------------------------------------------------- the searches

    /// <summary>
    /// Ask for the tagged value instead of the game's. Both searches call this, and so does the
    /// search a lobby runs for itself, so nothing has to know which search is running. It is
    /// also the one call they all share, so the heartbeat filter goes on here.
    ///
    /// The game's <c>Mode</c> filter is dropped. It forgets SLOTS and narrows any other pair of
    /// ticked boxes down to one, so it hides lobbies the boxes say are wanted. Quick Join tests
    /// each lobby against the boxes itself, the same way the list does.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.AddRequestLobbyListStringFilter))]
    private static bool SearchThePool(string pchKeyToMatch, ref string pchValueToMatch)
    {
        try
        {
            if (!Active) return true;
            if (pchKeyToMatch == ModeKey) return false;
            if (pchKeyToMatch != VersionKey || string.IsNullOrEmpty(pchValueToMatch)) return true;

            pchValueToMatch = InPool(pchValueToMatch);
            PoolValue = pchValueToMatch;

            SteamMatchmaking.AddRequestLobbyListNumericalFilter(
                AliveKey, AliveMinute - AliveWindow, ELobbyComparison.k_ELobbyComparisonGreaterThan);

            if (!_saidSearch)
            {
                _saidSearch = true;
                Plugin.Log.LogInfo($"[matchmaking] searches ask for {VersionKey}={pchValueToMatch}");
            }
        }
        catch (Exception e) { Plugin.Log.LogError($"[matchmaking] could not change the search: {e.Message}"); }
        return true;
    }

    /// <summary>
    /// Leave out the filters that make no sense for a small pool on a table of eight.
    ///
    /// <c>playercount</c> is "fewer than four", which hid every lobby at four to seven. Steam
    /// never returns a full lobby anyway, so nothing replaces it, and a number here would be
    /// wrong for a host with a different <c>MaxPlayers</c>.
    ///
    /// The three date filters are how the game expires a lobby whose host has gone, because
    /// Steam will not: a lobby lasts until its last member leaves, and nothing makes the others
    /// leave. They also fail across midnight or month end, and whenever two players' clocks
    /// disagree, so <see cref="AliveKey"/> does that job here instead.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.AddRequestLobbyListNumericalFilter))]
    private static bool DropNarrowFilters(string pchKeyToMatch)
    {
        try
        {
            if (!Active) return true;
            return pchKeyToMatch != "playercount" && pchKeyToMatch != "date" &&
                   pchKeyToMatch != "dateday" && pchKeyToMatch != "datemonth";
        }
        catch { return true; }
    }

    /// <summary>
    /// Quick Join asks for five lobbies and ranks only those, so a fuller lobby could be missed,
    /// and five lobbies the boxes reject would look like nobody hosting.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.AddRequestLobbyListResultCountFilter))]
    private static void SearchEverything(ref int cMaxResults)
    {
        try
        {
            if (Active && cMaxResults < ListLimit) cMaxResults = ListLimit;
        }
        catch (Exception e) { Plugin.Log.LogError($"[matchmaking] could not change the result count: {e.Message}"); }
    }

    // -------------------------------------------------------------------- joining

    /// <summary>
    /// Why this copy cannot sit at this lobby, in words for the player, or null if it can.
    /// Everything read here came from another player.
    /// </summary>
    internal static string WhyNot(CSteamID lobby)
    {
        string theirs = SteamMatchmaking.GetLobbyData(lobby, VersionCheck.Key);
        if (string.IsNullOrEmpty(theirs))
            return "This lobby does not say\nwhich mod version it runs";
        if (theirs != Plugin.Version)
            return $"This lobby runs mod v{Clean(theirs, LobbyRows.VersionLength)}\nYou have v{Plugin.Version}";

        // The config has always said every player must match, and nothing ever checked.
        int seats = SteamMatchmaking.GetLobbyMemberLimit(lobby);
        if (seats > 0 && seats != Limits.LobbySeats)
            return $"This lobby seats {seats}\nYours are set to {Limits.LobbySeats}";

        return null;
    }

    /// <summary>Could this copy take a seat at this lobby right now?</summary>
    internal static bool Seatable(CSteamID lobby)
    {
        if (SteamMatchmaking.GetLobbyData(lobby, StateKey) != InTheLobby) return false;
        if (WhyNot(lobby) != null) return false;

        int seats = SteamMatchmaking.GetLobbyMemberLimit(lobby);
        return seats <= 0 || SteamMatchmaking.GetNumLobbyMembers(lobby) < seats;
    }

    /// <summary>
    /// Stop a join to a lobby on another build, and say why. The row's button and a controller's
    /// Submit both end here. Invites and the lobby ID box do not, and are meant not to: they
    /// keep working as they always have, with the mismatch banner as the warning.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.JoinLobby))]
    private static bool RefuseOtherBuilds(CSteamID lobbyID)
    {
        try
        {
            if (!Active) return true;

            // The game would enter the lobby on Steam and then not start a second client.
            if (NetworkClient.active)
            {
                Say(QuickMatch.StillConnecting, RefusalSeconds);
                return false;
            }

            string why = WhyNot(lobbyID);
            if (why == null)
            {
                // A refusal still up becomes the game's "Connecting", which should move.
                if (_hideAt > 0f) ShowDots(true);
                _hideAt = 0f;
                return true;
            }

            Plugin.Log.LogInfo($"[matchmaking] refused lobby {lobbyID.m_SteamID}: {why.Replace('\n', ' ')}");
            Say(why, RefusalSeconds);
            return false;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[matchmaking] join check failed, leaving it to the game: {e.Message}");
            return true;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.OnJoinRequest))]
    private static void RememberInvite(GameLobbyJoinRequested_t callback)
    {
        _invited = callback.m_steamIDLobby.m_SteamID;
    }

    /// <summary>
    /// Say so when Steam turns down an invite. An invite goes straight to Steam without the
    /// lock the game's failure path looks for, so the game says nothing at all, and a lobby in
    /// a match is shut. The box only shows with the list open, so the list is opened for it.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.OnLobbyEntered))]
    private static void SayWhyNotInvited(LobbyEnter_t callback)
    {
        try
        {
            if (_invited == 0 || callback.m_ulSteamIDLobby != _invited) return;
            _invited = 0;

            if (callback.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess) return;

            var list = LobbyListManager.instance;
            if (list == null || SceneManager.GetActiveScene().name != Menu) return;

            Plugin.Log.LogInfo($"[matchmaking] an invite was turned down: response {callback.m_EChatRoomEnterResponse}");
            QuickMatch.OpenList(list);
            Say(InviteFailed, RefusalSeconds);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[matchmaking] could not explain a failed invite: {e.Message}"); }
    }

    /// <summary>
    /// Say something in the game's own box, the one that normally reads "Connecting. Please
    /// Wait...". With a time it goes away by itself; without one, whoever asked hides it.
    /// </summary>
    internal static void Say(string text, float seconds = 0f)
    {
        var list = LobbyListManager.instance;
        if (list == null) return;

        if (list.ConnectingScreen != null) list.ConnectingScreen.SetActive(true);

        var info = list.ConnectingScreenInfo;
        if (info != null)
        {
            // The box's moving dots rebuild the text from this every half second or so, so
            // setting the text alone lasts until the next dot. A timed line is a reason, not
            // something under way, so it does not get the dots.
            var dots = info.GetComponent<WaitingAnim>();
            if (dots != null)
            {
                dots.baseText = text;
                dots.enabled = seconds <= 0f;
            }
            info.text = text;
        }

        _hideAt = seconds > 0f ? Time.unscaledTime + seconds : 0f;
    }

    internal static void HideBox()
    {
        _hideAt = 0f;
        var list = LobbyListManager.instance;
        if (list == null) return;

        if (list.ConnectingScreen != null) list.ConnectingScreen.SetActive(false);
        if (list.ConnectionBG != null) list.ConnectionBG.SetActive(false);
        ShowDots(true);
    }

    private static void ShowDots(bool on)
    {
        var info = LobbyListManager.instance != null ? LobbyListManager.instance.ConnectingScreenInfo : null;
        var dots = info != null ? info.GetComponent<WaitingAnim>() : null;
        if (dots != null) dots.enabled = on;
    }

    /// <summary>
    /// Say something to the host on their own lobby screen, for a few seconds, in the label that
    /// otherwise reads "Lobby is not Visible". Only hosts see that label.
    /// </summary>
    internal static void HostNote(string text, float seconds)
    {
        _note = text;
        _noteUntil = Time.unscaledTime + seconds;
    }

    /// <summary>
    /// What <see cref="HostUpkeep"/> has decided, applied every frame after the game's own
    /// pass. The game rewrites the label and relights Start each frame, so a change made once
    /// would be gone at once. An unlit Start stops every route to starting: the mouse has
    /// nothing to press, and <c>StartGameOrReady</c> does nothing unless the button is lit. A
    /// veto in <c>StartGame</c> could not do this, because the gamepad's route has already sent
    /// everybody to the loading screen by the time it gets there.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.Update))]
    private static void HostScreen(LobbyController __instance)
    {
        try
        {
            bool note = _note != null && Time.unscaledTime < _noteUntil;
            if ((!note && _hold == null) || !NetworkServer.active) return;

            if (_hold != null)
            {
                if (__instance.StartButtonActive != null) __instance.StartButtonActive.SetActive(false);
                if (__instance.StartButtonPassive != null) __instance.StartButtonPassive.SetActive(true);
            }

            var label = __instance.lobbyvisible;
            if (label != null) label.text = note ? _note : _hold;
        }
        catch (Exception e)
        {
            _hold = null;
            _note = null;
            Plugin.Log.LogWarning($"[matchmaking] could not update the host's screen: {e.Message}");
        }
    }

    // ---------------------------------------------------------- names on the screen

    /// <summary>
    /// Make a string somebody else chose safe to show: one line, no markup, nothing invisible,
    /// and short. The name in a list row has rich text off in the game's own prefab, but the
    /// name in the lobby and the box refusals appear in have it on, so a name could otherwise
    /// resize or recolour the screen for everybody who joined.
    /// </summary>
    internal static string Clean(string raw, int max)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        var sb = new StringBuilder(Math.Min(raw.Length, max) + 1);
        foreach (char c in raw)
        {
            if (sb.Length >= max)
            {
                // Half of a two-part character is worse than the end of the name.
                if (char.IsHighSurrogate(sb[sb.Length - 1])) sb.Length--;
                sb.Append('~');
                break;
            }

            switch (char.GetUnicodeCategory(c))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                    sb.Append(' ');
                    continue;
            }

            if (c != '<' && c != '>') sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>The lobby's name on the way into a lobby, in the old-style text the game shows there.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.OnLobbyEntered))]
    private static void CleanEnteredName(SteamLobby __instance)
    {
        try
        {
            var text = __instance.LobbyText;
            if (text != null) text.text = Clean(text.text, LobbyRows.NameLength);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[matchmaking] could not tidy the lobby name: {e.Message}"); }
    }

    /// <summary>The lobby's name over the lobby itself, read straight from Steam by the game.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.UpdateLobbyName))]
    private static void CleanLobbyName(LobbyController __instance)
    {
        try
        {
            var text = __instance.LobbyNameText;
            if (text == null) return;

            string shown = text.text;
            string clean = Clean(shown, LobbyRows.NameLength);
            if (clean != shown) text.text = clean;
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[matchmaking] could not tidy the lobby name: {e.Message}"); }
    }

    // --------------------------------------------------------------- looking after a lobby

    /// <summary>From <see cref="ModTicker"/>. The box goes on time; the rest once a second.</summary>
    internal static void Tick()
    {
        if (Loopback.Active) return;

        float now = Time.unscaledTime;
        if (_hideAt > 0f && now >= _hideAt) HideBox();

        if (now < _next) return;
        _next = now + HostEvery;

        try { Upkeep(now); }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[matchmaking] upkeep failed: {e.Message}");
            _hold = null;
            _next = now + 30f;   // a broken pass must not repeat every second
        }
    }

    /// <summary>
    /// Runs on every copy. Nothing here touches Steam until the guards have said there is a
    /// lobby to talk about, so the menus, the ranked scene and a match on Telepathy get no
    /// further than the first lines.
    /// </summary>
    private static void Upkeep(float now)
    {
        _hold = null;
        LeaveDeadLobby();

        var sl = SteamLobby.Instance;
        if (sl == null || sl.CurrentLobbyID == 0 || !NetworkServer.active)
        {
            Forget();
            return;
        }

        var nm = sl.manager;
        if (nm == null || nm.Matchmaking || nm.isVelvetRoom || !SteamUp) return;

        HostUpkeep(sl, nm, now);
    }

    /// <summary>
    /// The Steam lobby this copy is in, kept here because <c>CurrentLobbyID</c> cannot be relied
    /// on when it matters. It lives on the <c>SteamLobby</c>, which a lost connection destroys on
    /// the way back to the menu, so the copy of it there starts at nothing. Only an entry the
    /// game acted on counts: one refused, or dropped because the player backed out, does not.
    ///
    /// The lobby this replaces is left alone. A seated player who accepts an invite enters the
    /// new lobby on Steam and stays at their table, because the client is already running, and
    /// leaving the old lobby would make that table's host think they had no mod.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.OnLobbyEntered))]
    private static void RememberLobby(LobbyEnter_t callback)
    {
        try
        {
            var sl = SteamLobby.Instance;
            if (sl != null && sl.CurrentLobbyID == callback.m_ulSteamIDLobby) _entered = callback.m_ulSteamIDLobby;
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[matchmaking] could not note the lobby: {e.Message}"); }
    }

    /// <summary>Every way out of a lobby, the game's or this mod's, comes through here.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.LeaveLobby))]
    private static void Left(CSteamID steamIDLobby)
    {
        if (steamIDLobby.m_SteamID == _entered) _entered = 0;
    }

    /// <summary>Leave a Steam lobby and forget it. Leaving one already left does nothing.</summary>
    internal static void LeaveCurrent(SteamLobby sl)
    {
        if (sl.CurrentLobbyID == 0) return;

        SteamMatchmaking.LeaveLobby(new CSteamID(sl.CurrentLobbyID));
        sl.CurrentLobbyID = 0;
    }

    /// <summary>
    /// Back at the menu with no connection, still holding a lobby. The game leaves the Steam
    /// lobby on its leave and kick paths and on no other: a client that loses the host, or
    /// never reached it, stays a member until the game closes. That keeps a dead lobby alive,
    /// makes it look fuller, and a table of seven can read as full because of somebody who never
    /// sat down.
    ///
    /// A join by lobby ID has no clock of its own, so nothing else would take its box away or
    /// clear its lock, and the next ID join would do nothing at all. <c>WaitforJoin</c> is the
    /// game's own failure path: it says "Failed To Connect", clears the lock and hides the box.
    /// Stopping the coroutines first means a row's clock cannot fail a later join. Quick Join
    /// looks after its own joins.
    /// </summary>
    private static void LeaveDeadLobby()
    {
        if (_entered == 0 || NetworkServer.active || NetworkClient.active) return;
        if (QuickMatch.Joining || SceneManager.GetActiveScene().name != Menu || !SteamUp) return;

        Plugin.Log.LogInfo($"[matchmaking] no connection to lobby {_entered} any more - left it on Steam");
        SteamMatchmaking.LeaveLobby(new CSteamID(_entered));

        var sl = SteamLobby.Instance;
        if (sl == null) return;

        sl.CurrentLobbyID = 0;
        if (sl.JoinLocked)
        {
            sl.StopMatchMaking();
            sl.WaitforJoin();
        }
    }

    /// <summary>
    /// What the owner of a lobby has to keep true once strangers can find it. All of it is
    /// already on the host's own machine, so none of it needs a round trip to anybody.
    /// </summary>
    private static void HostUpkeep(SteamLobby sl, CustomNetworkManager nm, float now)
    {
        var lobby = new CSteamID(sl.CurrentLobbyID);
        if (SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID != SteamUser.GetSteamID().m_SteamID) return;

        // Through a match as well, so the lobby is listed the moment the table is back.
        if (now >= _nextAlive && !MatchmakingSelfTest.NoHeartbeat) Heartbeat(lobby);

        var room = LobbyController.Instance;
        bool atLobby = room != null && Manager.Instance == null;
        string state = SteamMatchmaking.GetLobbyData(lobby, StateKey);

        // A Steam lobby stays open during a match unless told otherwise, and a row on somebody's
        // screen stays there until they refresh. Joining one lands you in a match with no
        // player object and no way to play, so it is shut for as long as the lobby is not
        // sitting in the lobby. That stops new Steam entries and no more: somebody already in
        // the Steam lobby is still connecting, which is what the hold on Start and
        // DropStrays are for. The state is empty until the lobby scene first runs, and nothing
        // has started then.
        bool open = atLobby && state == InTheLobby;
        if (!string.IsNullOrEmpty(state) && open != _open && SteamMatchmaking.SetLobbyJoinable(lobby, open))
        {
            _open = open;
            Plugin.Log.LogInfo($"[matchmaking] lobby {(open ? "open" : "closed")} to joiners");
        }

        if (!atLobby)
        {
            DropStrays(nm, now);
            return;
        }

        // The game says "Lobby is not Visible" fifteen minutes after this was read. Here that
        // is false: nothing searches on the date, so a lobby lists for as long as it is alive.
        room.starttime = DateTime.UtcNow.Hour * 60 + DateTime.UtcNow.Minute;

        Relist(lobby, room, state, now);
        RefuseUnmodded(lobby, room, nm, now);

        // Both of these end by themselves: a player who shows no mod is sent away, and the wait
        // for one still arriving has a limit, because a member can fail to arrive for good.
        _hold = _unproven.Count > 0 ? WhyHeldProving : Arriving(lobby, nm, now) ? WhyHeldArriving : null;
    }

    /// <summary>
    /// Is somebody in the Steam lobby who has no player yet? Start would leave them behind with
    /// no player object, and strangers arrive at moments friends would not.
    /// </summary>
    private static bool Arriving(CSteamID lobby, CustomNetworkManager nm, float now)
    {
        if (MatchmakingSelfTest.PretendArriving) return true;

        var players = nm.GamePlayers;
        if (players == null) return false;

        int here = 0;
        foreach (var p in players)
            if (p != null && !BotManager.IsBot(p)) here++;

        int coming = SteamMatchmaking.GetNumLobbyMembers(lobby) - here;
        if (coming <= 0)
        {
            _coming = 0;
            return false;
        }

        if (coming > _coming) _comingSince = now;   // somebody new, so a fresh wait
        _coming = coming;
        return now - _comingSince < ArrivingLimit;
    }

    /// <summary>
    /// Put a lobby back on the list after a match.
    ///
    /// Back in the lobby scene two things write <c>InGame</c> once each with nothing ordering
    /// them: the new <c>LobbyController</c> writes "lobby", and each returning player's
    /// <c>Update</c> writes "off". When "off" lands last, the lobby never lists again.
    /// </summary>
    private static void Relist(CSteamID lobby, LobbyController room, string state, float now)
    {
        if (state != Off || !room.setted)
        {
            _offSince = 0f;
            return;
        }

        if (_offSince <= 0f) { _offSince = now; return; }
        if (now - _offSince < OffGrace) return;

        _offSince = 0f;
        SteamMatchmaking.SetLobbyData(lobby, StateKey, InTheLobby);
        Plugin.Log.LogInfo("[matchmaking] back from a match - the lobby is listed again");
    }

    /// <summary>
    /// Turn away somebody without the mod, with the game's own kick: it leaves the Steam lobby
    /// and drops the connection on their side, and they land back at the menu. The game's kick
    /// says nothing, so the room is told why first, through the game's own chat.
    ///
    /// They get there by invitation or a lobby ID, because no search of theirs can return this
    /// lobby. Every copy of the mod publishes its version the moment it enters a lobby, so a
    /// member still publishing nothing after <see cref="ProveModSeconds"/> has no mod. Another
    /// version of the mod is left alone: that is a person who can read the banner.
    ///
    /// The connection is matched to the Steam member by the address the transport itself
    /// holds, not <c>PlayerSteamID</c>, which the game fills by position in a list that public
    /// lobbies can put out of step.
    /// </summary>
    private static void RefuseUnmodded(CSteamID lobby, LobbyController room, CustomNetworkManager nm, float now)
    {
        var players = nm.GamePlayers;
        if (players == null || room.SpawnSlots == null || room.SpawnSlots.Count == 0) return;

        _seen.Clear();
        foreach (var p in players)
        {
            if (p == null || p.isOwned || p.ConnectionID < 0 || BotManager.IsBot(p)) continue;

            var conn = p.connectionToClient;
            if (conn == null) continue;

            // Anything but a Steam id is not a connection this can say anything about.
            if (!ulong.TryParse(conn.address, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)) continue;

            if (!string.IsNullOrEmpty(SteamMatchmaking.GetLobbyMemberData(lobby, new CSteamID(id), VersionCheck.Key)))
                continue;

            _seen.Add(id);

            if (!_unproven.TryGetValue(id, out float since)) { _unproven[id] = now; continue; }
            if (now - since < ProveModSeconds) continue;

            string name = Clean(p.PlayerName, 16);
            if (_told.Add(id))
            {
                HostNote($"{name} needs the mod", NoteSeconds);
                TellTheRoom(ToTheRoom);
                continue;
            }
            if (now - since < ProveModSeconds + ReadBeforeKick) continue;

            _told.Remove(id);
            _unproven.Remove(id);

            Plugin.Log.LogWarning($"[matchmaking] refused '{name}': connection {p.ConnectionID} has shown no mod " +
                                  $"after {ProveModSeconds:F0}s - a player without it desyncs the table");

            // Sent through the first podium whoever it is for. The podium is only the object
            // the call goes out on: the mod's extra podiums have no network identity, so a
            // player standing on one could not be reached through their own.
            room.SpawnSlots[0].KickRPC(conn);
        }

        // Showing the mod, or leaving before the time ran out, ends the wait.
        if (_unproven.Count == _seen.Count) return;
        foreach (ulong id in new List<ulong>(_unproven.Keys))
        {
            if (_seen.Contains(id)) continue;
            _unproven.Remove(id);
            _told.Remove(id);
        }
    }

    /// <summary>
    /// A line in the game's own chat, on every screen in the lobby. It is the only thing that
    /// reaches a copy without the mod, because the chat call is the game's own. It is only ever
    /// a courtesy: the kick follows whether or not anybody saw it. Guarded on the host's own
    /// chat panel, because a client without one would throw on the call, and Mirror can drop
    /// a client for that.
    /// </summary>
    private static void TellTheRoom(string line)
    {
        try
        {
            if (ChatArayuz.instance == null) return;

            var chat = UnityEngine.Object.FindObjectOfType<ChatNetwork>();
            if (chat != null) chat.RpcHandleMessage(line);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[matchmaking] could not tell the room: {e.Message}"); }
    }

    /// <summary>
    /// A connection in a running match with no player object: somebody who had entered the
    /// Steam lobby when the match started, and finished connecting too late to be spawned,
    /// because the game only spawns players in the lobby scene. They are looking at a table
    /// with no way to play and no way to leave. Dropping them sends them to the menu, and
    /// <see cref="LeaveDeadLobby"/> then takes them out of the Steam lobby. They have no game
    /// manager of their own in that scene, so the drop does not read as a host migration to them.
    ///
    /// A host migration reconnects everybody, so nothing here runs during one, and the game has
    /// no path that takes a player object away from a connection in a match.
    /// </summary>
    private static void DropStrays(CustomNetworkManager nm, float now)
    {
        if (Manager.Instance == null || nm.MigrationStarted)
        {
            _strays.Clear();
            return;
        }

        List<NetworkConnectionToClient> drop = null;
        foreach (var pair in NetworkServer.connections)
        {
            var conn = pair.Value;
            if (conn == null || conn.identity != null || !ulong.TryParse(conn.address, NumberStyles.None,
                    CultureInfo.InvariantCulture, out _))
            {
                _strays.Remove(pair.Key);
                continue;
            }

            if (!_strays.TryGetValue(pair.Key, out float since)) { _strays[pair.Key] = now; continue; }
            if (now - since < StraySeconds) continue;

            _strays.Remove(pair.Key);
            (drop ??= new()).Add(conn);
        }

        // Outside the walk: a disconnect can take the connection out of the list straight away.
        if (drop == null) return;
        foreach (var conn in drop)
        {
            Plugin.Log.LogWarning($"[matchmaking] connection {conn.connectionId} reached a running match " +
                                  "with no player - sending it back to the menu");
            conn.Disconnect();
        }
    }

    private static void Forget()
    {
        _open = null;
        _offSince = 0f;
        _nextAlive = 0f;
        _coming = 0;
        _unproven.Clear();
        _told.Clear();
        _strays.Clear();
    }
}
