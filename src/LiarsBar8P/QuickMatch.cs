using System;
using System.Collections.Generic;
using HarmonyLib;
using Mirror;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LiarsBar8P;

/// <summary>
/// QUICK JOIN, for a small pool: find a table with room, or be the table somebody else finds.
///
/// The game's own button asks Steam for five lobbies and tries them in the order they came, five
/// seconds each. Nothing is written for the case that matters most to a handful of players
/// spread round the world: nobody hosting. It searches once more without listening for the
/// answer, stops, and leaves "Connecting. Please Wait..." on screen for good, with no way to
/// cancel it. Two people pressing it at once would both sit there looking at that forever. Its
/// retry cannot run either: it walks off the end of the list and dies before reaching it.
///
/// So the button runs this instead. It looks, joins the fullest lobby that has a seat, and if
/// there is nothing it looks once more a moment later and then opens a public lobby of its own
/// through the game's own <c>HostLobby</c>, where the next person to press the button finds it.
/// Every join goes through the game's own <c>JoinLobby</c>, which is the only route with the
/// connecting screen, the time limit and "Failed To Connect"; a lobby that turns out to be full
/// or dead by then is noted and the next one tried. Nobody is ever left spinning: the whole
/// thing ends in a lobby, or says why not.
///
/// <b>Two people at once.</b> Opening a lobby is not enough by itself. A lobby only lists once
/// its host has loaded the lobby scene, which takes several seconds, so two people pressing
/// within a few seconds of each other both find nothing, and both open a lobby. No waiting on
/// a random timer fixes that without making everybody wait far longer than the search does: the
/// common case is one person alone, and they would pay for it every time.
///
/// So a lobby opened here keeps looking for a little while, as long as its owner is still alone
/// in it. If another lobby turns up that the player's boxes allow, and it has people in it or is
/// alone too with the lower id, the host says so, leaves and quick joins again. Which of two
/// lone hosts moves is settled by the ids, which both sides can see, so exactly one of them
/// does, and the two end up at one table. A lobby somebody hosted on purpose is never left: only
/// one made by this button moves, and only once.
///
/// Back stops all of this while it is looking or opening a lobby. What is already on its way to
/// Steam still lands, so an answer the player has walked away from is dropped when it arrives.
/// During a join the game's own backdrop covers Back, and the join gives up by itself.
///
/// With <c>ShowUnmoddedLobbies</c> on the button is the game's own again.
/// </summary>
internal static class QuickMatch
{
    private enum Step { Idle, Search, Waiting, Joining, Hosting }

    private const string ButtonPath = "footer/QuickJoin";
    private const string IdButtonPath = "JoinWithId/Button";

    private const string Looking = "Looking for a lobby";
    private const string Opening = "No lobbies yet. Opening one";
    private const string CouldNotOpen = "Could not open a lobby";
    internal const string StillConnecting = "Still connecting\nTry again in a moment";
    private const string WentWrong = "Quick Join stopped\nTry again";
    private const string FoundPlayers = "Found players. Moving you there";

    /// <summary>How many times the pool is looked at before deciding nobody is there.</summary>
    private const int Looks = 2;

    /// <summary>How many lobbies to try joining before opening one instead.</summary>
    private const int MostJoins = 4;

    /// <summary>The second look, spread out so two people who pressed together do not stay in step.</summary>
    private const float LookGapLeast = 1.5f, LookGapMost = 4f;

    /// <summary>Steam answers an empty search through the game's one-second fallback, usually well inside this.</summary>
    private const float ListWait = 8f;

    /// <summary>Longer than the game's own twenty-second limit on a join, so that always speaks first.</summary>
    private const float JoinWait = 25f;

    private const float HostWait = 20f;

    /// <summary>After a failed join, long enough for the game's "Failed To Connect" to be read.</summary>
    private const float AfterFailure = 1.5f;

    /// <summary>Long enough for two lobbies opened together to have both listed.</summary>
    private const float StayLooking = 45f;
    private const float LookEvery = 5f;

    /// <summary>Long enough to read why the lobby is about to go.</summary>
    private const float BeforeLeaving = 2f;

    /// <summary>Leaving takes the game a few seconds, and then the whole menu scene has to load.</summary>
    private const float BackAtMenuWait = 45f;
    private const float BackAtMenuSettle = 1.5f;

    /// <summary>Steam gives up on any call well inside this, so an answer dropped late is never a new one.</summary>
    private const float DropWait = 30f;

    private static int _wired;
    private static int _wiredLobby;
    private static SteamLobby _listening;
    private static Step _step;
    private static int _looks;
    private static float _at;
    private static bool _listIn;
    private static bool _moving;
    private static bool _noList;
    private static bool _opening;
    private static bool _forcePublic;
    private static readonly HashSet<ulong> _tried = new();

    private static ulong _joining;
    private static bool _entered;

    private static bool _deck, _dice, _poker, _spin;

    private static float _dropUntil;
    private static bool _dropHost;
    private static ulong _dropped;

    private static ulong _ownLobby;
    private static float _lookUntil;
    private static float _nextLook;
    private static bool _lookInFlight;
    private static ulong _leaving;
    private static float _leaveAt;
    private static float _backUntil;
    private static float _backAt;

    /// <summary>A join is under way, and this class is watching how it ends.</summary>
    internal static bool Joining => _step == Step.Joining;

    // ------------------------------------------------------------------ the button

    /// <summary>
    /// The button also switches the "Connecting" box on, after this returns, so the box is
    /// already what the player sees. It is the same box every line here is said in.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.StartMatchMaking))]
    private static bool Pressed()
    {
        try
        {
            if (!Matchmaking.Active) return true;
            Press();
            return false;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[matchmaking] quick join could not start, leaving it to the game: {e.Message}");
            _step = Step.Idle;
            return true;
        }
    }

    /// <summary>
    /// One <c>SteamLobby</c> hearing from Steam, not one per trip to the menu. The game never
    /// takes back the callbacks a <c>SteamLobby</c> signs up for. Leaving a lobby ends the
    /// network manager it sits on, the menu brings a new one which signs up again, and from then
    /// on every Steam answer goes to the dead object first and throws, then to the live one. A
    /// player's log fills with errors, and an answer has to arrive once to be dealt with once.
    /// </summary>
    private static void RetireOldListener()
    {
        var sl = SteamLobby.Instance;
        if (sl == null || sl == _listening) return;

        var old = _listening;
        _listening = sl;
        if (old is null) return;

        old.LobbyCreated?.Dispose();
        old.JoinRequest?.Dispose();
        old.LobbyEntered?.Dispose();
        old.LobbyList?.Dispose();
        old.LobbyDataUpdated?.Dispose();
    }

    /// <summary>
    /// Make the buttons work on any menu, not only the one the game started in.
    ///
    /// Their clicks are saved in the menu scene, aimed at the <c>SteamLobby</c> on that scene's
    /// own copy of the network manager. Normally that is the one in use: leaving a lobby ends the
    /// old manager, so the menu's copy takes over. But the game keeps a manager that is still
    /// there and destroys the menu's copy instead (<c>NetworkManager.InitializeSingleton</c>,
    /// <c>SteamLobby.Start</c>), and Unity skips a saved click on a destroyed object without a
    /// word. The box would come up and nothing would ever be searched. That is the case where
    /// the same <c>SteamLobby</c> is still here on a new menu, and only then does a button need a
    /// listener: with a live saved click as well, each would run twice.
    /// </summary>
    private static void WireMenu()
    {
        var list = LobbyListManager.instance;
        var sl = SteamLobby.Instance;
        if (list == null || sl == null) return;

        int id = list.GetInstanceID();
        if (id == _wired) return;
        _wired = id;

        int lobby = sl.GetInstanceID();
        bool savedCallsDead = lobby == _wiredLobby;
        _wiredLobby = lobby;
        if (!savedCallsDead) return;

        Plugin.Log.LogInfo("[matchmaking] the menu's own SteamLobby is gone - wiring its buttons by hand");

        var panel = ListPanel(list);
        var quick = panel != null ? panel.Find(ButtonPath) : null;
        var button = quick != null ? quick.GetComponent<UnityEngine.UI.Button>() : null;
        if (button == null)
        {
            Plugin.Log.LogWarning("[matchmaking] the QUICK JOIN button is not where it was");
            return;
        }

        button.onClick.AddListener(new Action(Clicked));
        WireTheRest(list, panel);
    }

    /// <summary>By itself, so nothing wrong here can reach Quick Join.</summary>
    private static void WireTheRest(LobbyListManager list, Transform panel)
    {
        try
        {
            // A join by ID has no clock, so one that went wrong can leave this set, which would
            // silently stop the next. Everything from the last menu is over by now.
            var sl = SteamLobby.Instance;
            if (sl != null && !NetworkClient.active && !NetworkServer.active) sl.JoinLocked = false;

            var idJoin = panel.Find(IdButtonPath);
            var idButton = idJoin != null ? idJoin.GetComponent<UnityEngine.UI.Button>() : null;
            if (idButton != null) idButton.onClick.AddListener(new Action(IdClicked));
            else Plugin.Log.LogWarning("[matchmaking] the JOIN WITH ID button is not where it was");

            Listen(list.DeckFilter);
            Listen(list.DiceFilter);
            Listen(list.PokerFilter);
            Listen(list.SpinFilter);
        }
        catch (Exception e) { Plugin.Log.LogError($"[matchmaking] could not wire the lobby list: {e.Message}"); }
    }

    private static void Listen(UnityEngine.UI.Toggle box)
    {
        if (box != null) box.onValueChanged.AddListener(new Action<bool>(BoxChanged));
    }

    private static void BoxChanged(bool on)
    {
        try
        {
            var sl = SteamLobby.Instance;
            if (Matchmaking.Active && sl != null && _step == Step.Idle) sl.GetLobbiesList();
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[matchmaking] could not refresh the list: {e.Message}"); }
    }

    private static void IdClicked()
    {
        try
        {
            // Not tied to the pool: the ID box works the same with ShowUnmoddedLobbies on.
            var sl = SteamLobby.Instance;
            if (sl != null) sl.JoinLobbyWithId();
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[matchmaking] JOIN WITH ID failed: {e.Message}"); }
    }

    private static void Clicked()
    {
        try
        {
            if (Matchmaking.Active) Press();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[matchmaking] quick join could not start: {e.Message}");
            _step = Step.Idle;
        }
    }

    /// <summary>
    /// A second press changes nothing. So does a press with a connection still going: the game
    /// gives up on a join at twenty seconds and takes its box away, and the transport can go on
    /// trying after that. Starting on top of it could end in two lobbies at once.
    /// </summary>
    private static void Press()
    {
        if (_step != Step.Idle) return;

        if (NetworkClient.active || NetworkServer.active)
        {
            Matchmaking.Say(StillConnecting, 3f);
            return;
        }

        Begin(false);
    }

    private static void Begin(bool moving)
    {
        _moving = moving;
        _looks = 0;
        _tried.Clear();
        _at = 0f;
        _step = Step.Search;

        if (!moving)
        {
            _noList = false;
            ReadBoxes(LobbyListManager.instance);
        }

        Matchmaking.Say(Looking);
        Plugin.Log.LogInfo($"[matchmaking] quick join started{(moving ? " (moving to another lobby)" : "")}");
    }

    private static void End()
    {
        _step = Step.Idle;
        _opening = false;
        _noList = false;
    }

    // --------------------------------------------------------------------- the steps

    /// <summary>From <see cref="ModTicker"/>, four times a second. Little more than a few checks while nothing is under way.</summary>
    internal static void Tick()
    {
        if (Loopback.Active) return;

        try
        {
            RetireOldListener();
            WireMenu();
            if (_dropUntil > 0f && Time.unscaledTime > _dropUntil) DropLapsed();
            if (_leaveAt > 0f && Time.unscaledTime >= _leaveAt) Leave();
            if (_ownLobby != 0) KeepLooking();
            if (_backUntil > 0f) ComeBack();
            if (_step != Step.Idle) Advance(Time.unscaledTime);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[matchmaking] quick join stopped: {e.Message}");
            End();
            _ownLobby = 0;
            _backUntil = 0f;
            _leaveAt = 0f;
            try { Matchmaking.Say(WentWrong, 4f); } catch { }
        }
    }

    private static void Advance(float now)
    {
        // In a lobby: the join or the hosting worked. NetworkClient.active will not do for this.
        // It goes true when the game starts the client, which is before anything has answered,
        // so a dead host would count as a lobby.
        if (NetworkServer.active || NetworkClient.isConnected)
        {
            Plugin.Log.LogInfo("[matchmaking] quick join finished");
            End();
            return;
        }

        var sl = SteamLobby.Instance;
        var list = LobbyListManager.instance;
        if (sl == null || list == null)
        {
            Matchmaking.HideBox();
            End();
            return;
        }

        // An invite landed while this was looking, and that has the player now.
        if (NetworkClient.active && _step != Step.Joining)
        {
            Plugin.Log.LogInfo("[matchmaking] quick join stopped - the player is joining another way");
            Matchmaking.HideBox();
            End();
            return;
        }

        // Backed out of the lobby list, which takes the box with it.
        if (!_noList && !ListIsOpen(list))
        {
            Cancel(sl, list);
            return;
        }

        switch (_step)
        {
            case Step.Search:
                // Not waiting on JoinLocked here: the lobby ID box sets it and nothing clears
                // it, so that would be a wait with no end.
                if (now >= _at) Search(sl, now);
                break;

            case Step.Waiting:
                if (_listIn || now >= _at) Decide(sl, list, now);
                break;

            case Step.Joining:
                if (sl.CurrentLobbyID == _joining) _entered = true;

                // Steam let the player in and the connection to the host then failed, or the
                // game gave up: it clears the lock when Steam refuses, or when its own time
                // limit runs out, and says so itself.
                if ((_entered && !NetworkClient.active) || !sl.JoinLocked || now >= _at)
                {
                    Plugin.Log.LogInfo("[matchmaking] quick join: that lobby did not take - looking again");
                    Abandon(sl, list);
                    _step = Step.Search;
                    _at = now + AfterFailure;
                }
                break;

            case Step.Hosting:
                if (now >= _at)
                {
                    Plugin.Log.LogWarning("[matchmaking] quick join: Steam never made the lobby");
                    DropNext(true);
                    CouldNotHost(sl);
                }
                break;
        }
    }

    internal static Transform ListPanel(LobbyListManager list)
    {
        var box = list.ConnectingScreen;
        return box != null ? box.transform.parent : null;
    }

    private static bool ListIsOpen(LobbyListManager list)
    {
        var panel = ListPanel(list);
        return panel != null && panel.gameObject.activeInHierarchy;
    }

    /// <summary>
    /// Open LOBBY LIST from wherever the menu is, by the call the panel's own buttons end in.
    /// The box every word here is said in lives on that panel, so with it shut nothing can be
    /// said at all. False leaves the player where they were.
    /// </summary>
    internal static bool OpenList(LobbyListManager list)
    {
        try
        {
            var panel = ListPanel(list);
            if (panel == null) return false;
            if (panel.gameObject.activeInHierarchy) return true;

            var state = panel.GetComponent<StateController>();
            var states = panel.parent != null ? panel.parent.GetComponent<StateManager>() : null;
            if (state == null || states == null) return false;

            states.GoToNextCanvas(state.canvasType);
            return panel.gameObject.activeInHierarchy;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[matchmaking] could not open the lobby list: {e.Message}");
            return false;
        }
    }

    private static void Search(SteamLobby sl, float now)
    {
        Matchmaking.Say(Looking);

        _listIn = false;
        _step = Step.Waiting;
        _at = now + ListWait;

        // Only this player's own version: nobody is choosing here, so a row saying "needs
        // another version" would be no use to anyone. Steam adds filters up until the request,
        // so this joins the game's own, which the method below goes on to set.
        SteamMatchmaking.AddRequestLobbyListStringFilter(
            VersionCheck.Key, Plugin.Version, ELobbyComparison.k_ELobbyComparisonEqual);
        sl.GetLobbiesListMatchmaking();
    }

    /// <summary>The game has a finished list, from the search or from its fallback.</summary>
    internal static void ListArrived() => _listIn = true;

    /// <summary>
    /// Fullest first, because that is what gets a table to eight, then the lower id, the same
    /// way two lone hosts settle it. Only lobbies the mode boxes would show: the search itself
    /// no longer looks at them, so the list's own test decides.
    /// </summary>
    private static void Decide(SteamLobby sl, LobbyListManager list, float now)
    {
        var ids = sl.lobbiesIDs;
        int found = ids != null ? ids.Count : 0;

        CSteamID best = default;
        int bestMembers = -1;
        for (int i = 0; i < found; i++)
        {
            var id = ids[i];
            if (_tried.Contains(id.m_SteamID) || !Matchmaking.Seatable(id)) continue;
            if (!list.PassModeLocal(SteamMatchmaking.GetLobbyData(id, Matchmaking.ModeKey))) continue;

            int members = SteamMatchmaking.GetNumLobbyMembers(id);
            if (members > bestMembers || (members == bestMembers && id.m_SteamID < best.m_SteamID))
            {
                best = id;
                bestMembers = members;
            }
        }

        Plugin.Log.LogInfo($"[matchmaking] quick join: {found} lobbies found, " +
                           (bestMembers >= 0 ? $"best has {bestMembers} in it" : "none with a seat"));

        if (bestMembers >= 0 && _tried.Count < MostJoins)
        {
            _tried.Add(best.m_SteamID);
            _joining = best.m_SteamID;
            _entered = false;
            _step = Step.Joining;
            _at = now + JoinWait;
            sl.JoinLobby(best);
            return;
        }

        if (++_looks < Looks)
        {
            _step = Step.Search;
            _at = now + UnityEngine.Random.Range(LookGapLeast, LookGapMost);
            return;
        }

        Matchmaking.Say(Opening);
        _step = Step.Hosting;
        _at = now + HostWait;
        _opening = !_moving;     // one move is all anybody gets
        HostPublic(sl);
    }

    // -------------------------------------------------------------------- backing out

    /// <summary>
    /// Undo a join that has not worked, leaving nothing behind: no game clock still running, no
    /// connection still trying, and no seat held in a Steam lobby nobody is sitting at. The
    /// clock matters most. The game starts a new one per join and they do not know about each
    /// other, so the old one would run out in the middle of the next join and fail that instead.
    /// </summary>
    private static void Abandon(SteamLobby sl, LobbyListManager list)
    {
        if (sl.JoinLocked)
        {
            sl.JoinLocked = false;
            sl.StopMatchMaking();    // only ever StopAllCoroutines, and this copy is not hosting
            if (list.ConnectionBG != null) list.ConnectionBG.SetActive(false);
        }

        if (NetworkClient.active && sl.manager != null) sl.manager.StopClient();
        Matchmaking.LeaveCurrent(sl);
    }

    /// <summary>
    /// Back was pressed. The state machine can stop at once, but anything already sent to Steam
    /// still gets its answer, and the game acts on that answer whether anybody wants it or not.
    /// </summary>
    private static void Cancel(SteamLobby sl, LobbyListManager list)
    {
        Plugin.Log.LogInfo("[matchmaking] quick join cancelled - the player left the lobby list");

        if (_step == Step.Hosting)
        {
            DropNext(true);
        }
        else if (_step == Step.Joining)
        {
            if (!_entered && sl.CurrentLobbyID != _joining) DropNext(false);
            Abandon(sl, list);
        }

        Matchmaking.HideBox();
        End();
    }

    private static void DropNext(bool hosting)
    {
        _dropUntil = Time.unscaledTime + DropWait;
        _dropHost = hosting;
    }

    /// <summary>Steam never answered what the player backed out of, and that answer is what puts HOST GAME back.</summary>
    private static void DropLapsed()
    {
        _dropUntil = 0f;
        if (_dropHost) UndoHostLobby(SteamLobby.Instance);
    }

    /// <summary>
    /// Is this answer from Steam one the player has walked away from? Creating a lobby brings
    /// two answers and joining one brings one, in an order nothing fixes, so the first answer
    /// leaves the lobby and anything more about that lobby is ignored by its id.
    /// </summary>
    private static bool Unwanted(ulong lobby, bool worked)
    {
        if (lobby != 0 && lobby == _dropped) return true;

        if (_dropUntil <= 0f) return false;
        bool inTime = Time.unscaledTime <= _dropUntil;
        _dropUntil = 0f;
        if (!inTime) return false;

        if (worked)
        {
            SteamMatchmaking.LeaveLobby(new CSteamID(lobby));
            Plugin.Log.LogInfo($"[matchmaking] dropped lobby {lobby} - the player had backed out");
        }

        if (_dropHost)
        {
            _dropped = worked ? lobby : 0;
            UndoHostLobby(SteamLobby.Instance);
        }
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.OnLobbyCreated))]
    private static bool NotCreated(LobbyCreated_t callback)
    {
        try
        {
            bool worked = callback.m_eResult == EResult.k_EResultOK;
            return !Unwanted(worked ? callback.m_ulSteamIDLobby : 0, worked);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[matchmaking] {e.Message}");
            return true;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.OnLobbyEntered))]
    private static bool NotEntered(LobbyEnter_t callback)
    {
        try
        {
            bool worked = callback.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess;
            return !Unwanted(callback.m_ulSteamIDLobby, worked);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[matchmaking] {e.Message}");
            return true;
        }
    }

    // -------------------------------------------------------------------- hosting

    /// <summary>
    /// Host through the game's own path. The type dropdown may still be on Friends Only from
    /// last time, and a lobby nobody can find is the opposite of the point.
    /// </summary>
    internal static void HostPublic(SteamLobby sl)
    {
        // Only ever cleared by a successful entry, so one failed lobby would otherwise leave
        // HostLobby returning at its first line for the rest of the session.
        sl.lobyhosted = false;

        _forcePublic = true;
        try { sl.HostLobby(); }
        finally { _forcePublic = false; }
    }

    /// <summary>What <c>HostLobby</c> leaves behind when no lobby comes of it: the flag, and HOST GAME greyed out.</summary>
    private static void UndoHostLobby(SteamLobby sl)
    {
        if (sl != null) sl.lobyhosted = false;

        var list = LobbyListManager.instance;
        var button = list != null && list.HostButton != null ? list.HostButton.GetComponent<UnityEngine.UI.Button>() : null;
        if (button != null) button.interactable = true;
    }

    private static void CouldNotHost(SteamLobby sl)
    {
        UndoHostLobby(sl);
        Matchmaking.Say(CouldNotOpen, 4f);
        End();
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.CreateLobby))]
    private static void ForcePublic(ref ELobbyType eLobbyType)
    {
        if (_forcePublic) eLobbyType = ELobbyType.k_ELobbyTypePublic;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.OnLobbyCreated))]
    private static void Created(LobbyCreated_t callback)
    {
        try
        {
            if (_step == Step.Hosting && callback.m_eResult != EResult.k_EResultOK)
            {
                Plugin.Log.LogWarning($"[matchmaking] quick join: Steam would not make the lobby: {callback.m_eResult}");
                CouldNotHost(SteamLobby.Instance);
                return;
            }

            if (!_opening || callback.m_eResult != EResult.k_EResultOK) return;

            _opening = false;
            _ownLobby = callback.m_ulSteamIDLobby;
            _lookUntil = Time.unscaledTime + StayLooking;
            _nextLook = 0f;
            Plugin.Log.LogInfo($"[matchmaking] quick join opened lobby {_ownLobby} - it keeps looking for {StayLooking:F0}s");
        }
        catch (Exception e) { Plugin.Log.LogError($"[matchmaking] {e.Message}"); }
    }

    // ---------------------------------------------------------------------- the boxes

    private static void ReadBoxes(LobbyListManager list)
    {
        _deck = _dice = _poker = _spin = true;
        if (list == null) return;

        if (list.DeckFilter != null) _deck = list.DeckFilter.isOn;
        if (list.DiceFilter != null) _dice = list.DiceFilter.isOn;
        if (list.PokerFilter != null) _poker = list.PokerFilter.isOn;
        if (list.SpinFilter != null) _spin = list.SpinFilter.isOn;
    }

    /// <summary>
    /// A new menu starts with all four boxes ticked, so a player who ticked only DICE would
    /// lose that on the way through a move. Without the notify, because each box's change would
    /// otherwise run a search on top of the one that is about to start.
    /// </summary>
    private static void PutBoxesBack(LobbyListManager list)
    {
        if (list.DeckFilter != null) list.DeckFilter.SetIsOnWithoutNotify(_deck);
        if (list.DiceFilter != null) list.DiceFilter.SetIsOnWithoutNotify(_dice);
        if (list.PokerFilter != null) list.PokerFilter.SetIsOnWithoutNotify(_poker);
        if (list.SpinFilter != null) list.SpinFilter.SetIsOnWithoutNotify(_spin);
    }

    /// <summary>
    /// The game's <c>PassModeLocal</c>, for the boxes as they were when the button was pressed.
    /// A lobby has no list to ask: the boxes went with the menu.
    /// </summary>
    private static bool ModeTicked(string mode)
    {
        int on = (_deck ? 1 : 0) + (_dice ? 1 : 0) + (_poker ? 1 : 0) + (_spin ? 1 : 0);
        if (on == 0 || on == 4) return true;
        if (string.IsNullOrEmpty(mode)) return false;

        return (_deck && mode == "0") || (_dice && mode == "1") || (_poker && mode == "5") || (_spin && mode == "6");
    }

    // --------------------------------------------- two lone hosts finding each other

    private static void StopLooking()
    {
        _ownLobby = 0;
        _lookInFlight = false;
    }

    /// <summary>
    /// While this lobby is new and its owner is alone in it, keep asking Steam who else is
    /// there. The search is made directly rather than through the game's method, which starts
    /// by clearing the lobby list that went away with the menu.
    /// </summary>
    private static void KeepLooking()
    {
        float now = Time.unscaledTime;

        if (!NetworkServer.active || Manager.Instance != null)
        {
            StopLooking();
            return;
        }

        // Nothing lists until the lobby scene has run, so there is no point asking before that.
        var sl = SteamLobby.Instance;
        var room = LobbyController.Instance;
        if (sl == null || room == null || !room.setted || sl.CurrentLobbyID != _ownLobby)
        {
            if (now > _lookUntil) StopLooking();
            return;
        }

        if (now > _lookUntil)
        {
            Plugin.Log.LogInfo("[matchmaking] nobody else turned up - this lobby stays open for them");
            StopLooking();
            return;
        }

        if (SteamMatchmaking.GetNumLobbyMembers(new CSteamID(_ownLobby)) > 1)
        {
            Plugin.Log.LogInfo("[matchmaking] somebody found this lobby - it stays");
            StopLooking();
            return;
        }

        string pool = Matchmaking.PoolValue;
        if (now < _nextLook || string.IsNullOrEmpty(pool)) return;
        _nextLook = now + LookEvery;

        _lookInFlight = true;
        SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
        SteamMatchmaking.AddRequestLobbyListStringFilter(
            Matchmaking.StateKey, Matchmaking.InTheLobby, ELobbyComparison.k_ELobbyComparisonEqual);
        SteamMatchmaking.AddRequestLobbyListStringFilter(
            Matchmaking.VersionKey, pool, ELobbyComparison.k_ELobbyComparisonEqual);
        SteamMatchmaking.AddRequestLobbyListStringFilter(
            VersionCheck.Key, Plugin.Version, ELobbyComparison.k_ELobbyComparisonEqual);
        SteamMatchmaking.RequestLobbyList();
    }

    /// <summary>
    /// The answer to a search made from a lobby. The game's handler is skipped: it would ask
    /// for every lobby's data and then try to fill the list in a menu that is not there.
    ///
    /// The filters have already settled the version, the heartbeat and that each one is sitting
    /// in its lobby. Seats and the boxes are this player's own to check. A lobby whose
    /// <c>Mode</c> has not arrived yet fails the boxes if only some are ticked, which at worst
    /// costs the merge.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.OnGetLobbyList))]
    private static bool AnswerToOwnSearch(LobbyMatchList_t result)
    {
        if (!_lookInFlight) return true;

        _lookInFlight = false;
        try
        {
            ulong mine = _ownLobby;
            if (mine == 0) return false;

            int found = (int)Math.Min(result.m_nLobbiesMatching, 50u);
            for (int i = 0; i < found; i++)
            {
                var id = SteamMatchmaking.GetLobbyByIndex(i);
                if (id.m_SteamID == 0 || id.m_SteamID == mine) continue;

                int seats = SteamMatchmaking.GetLobbyMemberLimit(id);
                if (seats > 0 && seats != Limits.LobbySeats) continue;
                if (!ModeTicked(SteamMatchmaking.GetLobbyData(id, Matchmaking.ModeKey))) continue;

                if (SteamMatchmaking.GetNumLobbyMembers(id) > 1 || id.m_SteamID < mine)
                {
                    Plugin.Log.LogInfo($"[matchmaking] lobby {id.m_SteamID} is the one to be at - moving there");
                    Matchmaking.HostNote(FoundPlayers, BeforeLeaving + 1f);
                    StopLooking();
                    _leaving = mine;
                    _leaveAt = Time.unscaledTime + BeforeLeaving;
                    break;
                }
            }
        }
        catch (Exception e) { Plugin.Log.LogError($"[matchmaking] could not read the search: {e.Message}"); }
        return false;
    }

    /// <summary>Out through the game's own leave button, unless somebody has arrived while the player was being told.</summary>
    private static void Leave()
    {
        _leaveAt = 0f;
        ulong from = _leaving;
        _leaving = 0;

        var room = LobbyController.Instance;
        if (room == null || from == 0 || !NetworkServer.active || Manager.Instance != null) return;

        if (SteamMatchmaking.GetNumLobbyMembers(new CSteamID(from)) > 1)
        {
            Plugin.Log.LogInfo("[matchmaking] somebody found this lobby - it stays");
            return;
        }

        _backUntil = Time.unscaledTime + BackAtMenuWait;
        _backAt = 0f;
        room.ReturnMenu();
    }

    /// <summary>
    /// Back at the menu after leaving: quick join again, and the lobby just left is gone from
    /// the list. The list is opened first, with the player's boxes back as they were, so this
    /// runs like any other press: the box is on screen and Back stops it. Should the list not
    /// open, the join still runs, because the player has already been moved out of their lobby.
    /// </summary>
    private static void ComeBack()
    {
        float now = Time.unscaledTime;
        if (now > _backUntil)
        {
            Plugin.Log.LogWarning("[matchmaking] never got back to the menu - not joining the other lobby");
            _backUntil = 0f;
            return;
        }

        if (NetworkServer.active || NetworkClient.active) return;

        var list = LobbyListManager.instance;
        if (list == null || SceneManager.GetActiveScene().name != Matchmaking.Menu) return;
        if (!SteamManager.Initialized) return;

        if (_backAt <= 0f) { _backAt = now + BackAtMenuSettle; return; }
        if (now < _backAt) return;

        _backUntil = 0f;
        if (_step != Step.Idle) return;

        PutBoxesBack(list);
        _noList = !OpenList(list);
        Begin(true);
    }
}
