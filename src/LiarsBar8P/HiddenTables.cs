using System;
using System.Text;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Liar's Deck 2, the game's own fourth deck table, back on the deck arrows.
///
/// It was never taken out of the game. Its deal of seven, the round that keeps everybody's hand,
/// its card art, its help page and its sound are all still there; only the arrows stop one short
/// of it since the deck was remastered. So this changes nothing but that one step: right from
/// Chaos, or left from Basic, lands on Deck 2, and from there the game's own arrows step off it
/// again. Everything after the choice is the game's, as it was when the table was on the menu.
///
/// Only for the host, only at a table where everybody runs the mod, and never near ranked.
/// Clients are told the variant by the lobby's own synced number and draw it themselves. The
/// lobby's game mode stays plain Liar's Deck, so the lobby list and Quick Join see nothing new.
///
/// Two things the game would remember are kept away from it. LastMode is never written with a
/// hidden value, because the game feeds that back the next time any lobby opens, with or without
/// the mod. The deck variant the game remembers between launches is not written either: it resets
/// a saved Deck 2 to Basic anyway. The host's choice is kept here instead, for the trip back to
/// the lobby after a match, and forgotten when the host leaves or hosts again.
/// </summary>
internal static class HiddenTables
{
    /// <summary>The lobby's number for Liar's Deck 2. The arrows wrap before it.</summary>
    internal const int Deck2 = 3;
    private const int Basic = 0;
    private const int Chaos = 2;

    /// <summary>The game's own Deck 2 sound, the one its arrows played when they still reached it.</summary>
    private const int Deck2Sound = 6;

    /// <summary>The game's Deck 2 rules page, which its round help button slides in.</summary>
    private const string HelpPage = "TutorialDeck2";

    /// <summary>How many cards Liar's Deck 2 deals each player.</summary>
    private const int Deck2Hand = 7;

    /// <summary>How long into a match hands are looked at: everybody is seated well before the first deal.</summary>
    private const float HandsFor = 60f;

    /// <summary>
    /// How long a lobby can sit on Deck 2 without counting as modded before it goes back to Basic.
    /// Longer than the mod's check on a newcomer, so a player still arriving does not undo the
    /// host's choice, and one who never shows the mod has been sent away first.
    /// </summary>
    private const float NotModdedGrace = 20f;

    /// <summary>The session whose last match was Deck 2, or -1.</summary>
    private static int _keptFor = -1;

    private static float _notModdedSince = -1f;
    private static IntPtr _pageFor;
    private static int _round;
    private static IntPtr _roundsOf;
    private static DeckGameplay _mine;
    private static string _handSaid;
    private static IntPtr _handsOf;
    private static float _handsUntil, _nextHands;
    private static bool _saidNetworked;
    private static readonly System.Collections.Generic.HashSet<IntPtr> _roomy = new();

    // ------------------------------------------------------------------ the arrows

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.ChangeGameModeDeckRight))]
    private static bool Right(LobbyController __instance) => Onto(__instance, Chaos, "right arrow");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.ChangeNextDeckOrDiceMode))]
    private static bool PadRight(LobbyController __instance) => Onto(__instance, Chaos, "controller, next");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.ChangeGameModeDeckLeft))]
    private static bool Left(LobbyController __instance) => Onto(__instance, Basic, "left arrow");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.ChangePrevDeckOrDiceMode))]
    private static bool PadLeft(LobbyController __instance) => Onto(__instance, Basic, "controller, previous");

    /// <summary>
    /// The one step the game no longer takes. Taken whole rather than corrected afterwards, so the
    /// game's own step neither plays a second sound nor remembers the variant it would have chosen.
    /// </summary>
    private static bool Onto(LobbyController lobby, int from, string how)
    {
        try
        {
            if (lobby == null || !lobby.isServer || lobby.DeckMode != from || !OnDeck(lobby)) return true;
            if (!Allowed(lobby)) return true;

            Choose(lobby);
            lobby.PlaySes(Deck2Sound);
            Plugin.Log.LogInfo($"[deck2] Liar's Deck 2 chosen ({how})");
            return false;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[deck2] could not step onto Liar's Deck 2, leaving it to the game: {e.Message}");
            return true;
        }
    }

    private static void Choose(LobbyController lobby)
    {
        lobby.NetworkDeckMode = Deck2;

        // Coming from Chaos the manager still says Chaos. The lobby's own pass would put this
        // right on the next frame; doing it here means no frame starts a match on the old one.
        var nm = lobby.manager;
        if (nm != null) nm.Mode = CustomNetworkManager.GameMode.LiarsDeck;
    }

    /// <summary>The lobby is on the deck tab. The controller's pair of steps also serves Dice.</summary>
    private static bool OnDeck(LobbyController lobby) =>
        lobby.Mode == CustomNetworkManager.GameMode.LiarsDeck || lobby.Mode == CustomNetworkManager.GameMode.LiarsChaos;

    /// <summary>A host at a table where everybody runs the mod, and nowhere near ranked.</summary>
    private static bool Allowed(LobbyController lobby)
    {
        if (RankedGuard.Near || !Table.Modded) return false;

        var nm = lobby.manager;
        return nm != null && !nm.Matchmaking && !nm.isVelvetRoom;
    }

    // ------------------------------------------------------------------ keeping it

    /// <summary>
    /// The lobby as it comes back from a match. The game has just put back the deck variant it
    /// remembers between launches, which is never Deck 2, so the host's choice goes back on top:
    /// the same host, the same session, and the lobby on the deck tab.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.Start))]
    private static void BackFromMatch(LobbyController __instance)
    {
        try
        {
            if (_keptFor < 0) return;
            if (_keptFor != Table.Session)
            {
                _keptFor = -1;
                return;
            }
            if (__instance == null || !__instance.isServer || !Allowed(__instance)) return;

            var nm = __instance.manager;
            if (nm == null || nm.Mode != CustomNetworkManager.GameMode.LiarsDeck) return;

            Choose(__instance);
            Plugin.Log.LogInfo("[deck2] back from a Liar's Deck 2 match - the lobby stays on it");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[deck2] could not keep Liar's Deck 2: {e.Message}"); }
    }

    /// <summary>From <see cref="ModTicker"/>.</summary>
    internal static void Tick()
    {
        if (Manager.Instance != null)
        {
            InMatch();
            return;
        }

        _roundsOf = IntPtr.Zero;
        _mine = null;
        _handSaid = null;

        var room = LobbyController.Instance;
        if (room != null) ShowHelpPage(room);

        if (NetworkServer.active) InLobby();
        else
        {
            _keptFor = -1;
            _notModdedSince = -1f;
        }
    }

    /// <summary>
    /// What the host's match is, for the lobby it comes back to. Read all through the match, so
    /// whatever it was at the end is what counts.
    /// </summary>
    private static void InMatch()
    {
        if (NetworkServer.active)
        {
            var net = NetworkManager.singleton;
            var nm = net != null ? net.TryCast<CustomNetworkManager>() : null;
            if (nm != null)
                _keptFor = nm.Mode == CustomNetworkManager.GameMode.LiarsDeck && nm.DeckMode == Deck2 ? Table.Session : -1;
        }

        try { GiveHandsRoom(); }
        catch (Exception e)
        {
            _handsUntil = 0f;
            Plugin.Log.LogWarning($"[deck2] could not make room in a hand for seven cards: {e.Message}");
        }

        if (Dev.Enabled) SayOwnHand();
    }

    /// <summary>
    /// Liar's Deck 2 deals seven, and the characters that came with the remaster hold only five
    /// card objects in their hand - Gerk and Rasim among them - so their players were dealt seven
    /// and shown five. Each such hand gets the two it lacks, copied from its own last card and laid
    /// on along the fan its cards were already making. On every copy, because every copy draws every
    /// hand: the cards are plain objects, never networked, and the deal only ever tells a copy which
    /// of them to switch on and what each one shows.
    /// </summary>
    private static void GiveHandsRoom()
    {
        var m = Manager.Instance;
        float now = Time.unscaledTime;
        if (m.Pointer != _handsOf)
        {
            _handsOf = m.Pointer;
            _handsUntil = now + HandsFor;
            _roomy.Clear();
        }
        if (now > _handsUntil || now < _nextHands) return;
        _nextHands = now + 1f;

        var deck = Dev.Deck;
        if (deck == null || deck.DeckMode != DeckGamePlayManager.deckmode.Deck2 || !Table.Modded) return;

        foreach (var gp in UnityEngine.Object.FindObjectsOfType<DeckGameplay>())
        {
            if (gp == null || _roomy.Contains(gp.Pointer)) continue;
            if (MakeRoom(gp)) _roomy.Add(gp.Pointer);
        }
    }

    /// <summary>True once this hand can show seven cards, or never will be able to.</summary>
    private static bool MakeRoom(DeckGameplay gp)
    {
        var cards = gp.Cards;
        if (cards == null) return false;
        int had = cards.Count;
        if (had >= Deck2Hand) return true;
        if (had < 2) return true;    // no fan to follow

        var last = cards[had - 1];
        var before = cards[had - 2];
        if (last == null || before == null) return false;

        // The same refusal DeckFix makes: copying anything networked is what once broke spawning.
        if (last.GetComponentsInChildren<NetworkIdentity>(true).Length > 0)
        {
            if (!_saidNetworked) Plugin.Log.LogWarning("[deck2] a hand's cards are networked - not copying them");
            _saidNetworked = true;
            return true;
        }

        var a = before.transform;
        var b = last.transform;
        Vector3 step = b.localPosition - a.localPosition;
        Quaternion turn = b.localRotation * Quaternion.Inverse(a.localRotation);
        Vector3 at = b.localPosition;
        Quaternion facing = b.localRotation;

        // Copied while switched off, so nothing on the copy wakes up before it is in its place.
        bool was = last.activeSelf;
        last.SetActive(false);
        try
        {
            while (cards.Count < Deck2Hand)
            {
                at += step;
                facing = turn * facing;
                var copy = UnityEngine.Object.Instantiate(last, b.parent);
                copy.name = $"{last.name}_{cards.Count}";
                var t = copy.transform;
                t.localPosition = at;
                t.localRotation = facing;
                t.localScale = b.localScale;
                cards.Add(copy);
            }
        }
        finally { last.SetActive(was); }

        string body = gp.gameObject.name.StartsWith("Char_", StringComparison.Ordinal) ? gp.gameObject.name : "a character";
        Plugin.Log.LogInfo($"[deck2] {body} held {had} cards - room made for {Deck2Hand}");
        return true;
    }

    /// <summary>
    /// The game switched its Deck 2 rules page off when the table left the arrows, so the round
    /// help button it still shows on Deck 2 slid in nothing. The page goes back on, still out of
    /// sight, the first time a lobby is on Deck 2; the button's own animation then brings it in
    /// and its own close takes it away. On every copy, because the page is each player's own.
    /// </summary>
    private static void ShowHelpPage(LobbyController room)
    {
        if (room.DeckMode != Deck2 || room.Pointer == _pageFor || !Table.Modded) return;
        _pageFor = room.Pointer;

        var label = room.LoobyIdText;
        var canvas = label != null && label.canvas != null ? label.canvas.rootCanvas : null;
        var page = canvas != null ? canvas.transform.Find(HelpPage) : null;
        if (page == null)
        {
            Plugin.Log.LogWarning($"[deck2] the Liar's Deck 2 help page is not in this lobby - its help button will open nothing");
            return;
        }

        if (page.gameObject.activeSelf) return;
        page.gameObject.SetActive(true);
        Plugin.Log.LogInfo("[deck2] the Liar's Deck 2 help page is back behind its help button");
    }

    /// <summary>
    /// Fail closed: a lobby on Deck 2 that has stopped counting as modded goes back to Basic, the
    /// first variant everybody can reach. Not at once, because a player arriving makes the table
    /// look unmodded for a moment, and the host's choice should not be undone by somebody joining.
    /// </summary>
    private static void InLobby()
    {
        var lobby = LobbyController.Instance;
        if (lobby == null || !lobby.isServer || lobby.DeckMode != Deck2 || Allowed(lobby))
        {
            _notModdedSince = -1f;
            return;
        }

        float now = Time.unscaledTime;
        if (_notModdedSince < 0f) { _notModdedSince = now; return; }
        if (now - _notModdedSince < NotModdedGrace) return;

        _notModdedSince = -1f;
        _keptFor = -1;
        lobby.NetworkDeckMode = Basic;
        Plugin.Log.LogInfo($"[deck2] the table no longer counts as modded ({Table.Why}) - back to Liar's Deck Basic");
    }

    // ------------------------------------------------------------------ the rounds

    /// <summary>
    /// A Deck 2 round that keeps everybody's hand. The game deals nothing here, so none of the
    /// mod's work around a deal runs, and a seat that has thrown its last card stays empty. Only
    /// the test bots' turn state starts again: who threw last belongs to the round just ended.
    /// The turn watchdog's wait for the first turn is <see cref="DealTrace"/>'s, on the same call.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.ResetRoundDeck2))]
    private static void KeptHands()
    {
        try
        {
            if (!NetworkServer.active) return;

            BotBehaviour.RoundKeepingHands();
            Plugin.Log.LogInfo($"[deck2] round {NextRound()} keeps the hands, nothing dealt: {Hands()}");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[deck2] could not note a round: {e.Message}"); }
    }

    /// <summary>The other way a Deck 2 round starts: a fresh deal of seven each.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.ResetRound))]
    private static void Dealt(DeckGamePlayManager __instance)
    {
        try
        {
            if (!NetworkServer.active || __instance == null || __instance.DeckMode != DeckGamePlayManager.deckmode.Deck2) return;
            int round = NextRound();
            Plugin.Log.LogInfo($"[deck2] round {round} deals afresh: {Hands()}");
            if (round == 1 && Dev.Enabled) Plugin.Log.LogInfo($"[deck2] card objects each character holds: {Holders()}");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[deck2] could not note a deal: {e.Message}"); }
    }

    /// <summary>Developer only: how many card objects each seat's character has to show a hand with.</summary>
    private static string Holders()
    {
        var sb = new StringBuilder();
        foreach (var p in Dev.TablePlayers())
        {
            var gp = p.GetComponent<DeckGameplay>();
            string body = p.gameObject.name.StartsWith("Char_", StringComparison.Ordinal) ? p.gameObject.name : "player";
            if (sb.Length > 0) sb.Append(", ");
            sb.Append($"seat {p.Slot} {body} {(gp != null && gp.Cards != null ? gp.Cards.Count : -1)}");
        }
        return sb.ToString();
    }

    private static int NextRound()
    {
        var m = Manager.Instance;
        IntPtr match = m != null ? m.Pointer : IntPtr.Zero;
        if (match != _roundsOf)
        {
            _roundsOf = match;
            _round = 0;
        }
        return ++_round;
    }

    /// <summary>Cards held by each seat, as the server sees them, in seat order. Never names.</summary>
    private static string Hands()
    {
        var sb = new StringBuilder();
        foreach (var p in Dev.TablePlayers())
        {
            var gp = p.GetComponent<DeckGameplay>();
            var cards = gp != null ? gp.cardTypes : null;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append($"seat {p.Slot}=").Append(p.Dead ? "out" : (cards != null ? cards.Count : 0).ToString());
        }
        return sb.Length > 0 ? sb.ToString() : "nobody seated";
    }

    /// <summary>
    /// Developer only: what this copy's own player holds, as their own machine sees it, each time
    /// it changes. On a client this is the only account of what a player is actually shown.
    /// </summary>
    private static void SayOwnHand()
    {
        var deck = Dev.Deck;
        if (deck == null || deck.DeckMode != DeckGamePlayManager.deckmode.Deck2) return;

        if (_mine == null)
            foreach (var gp in UnityEngine.Object.FindObjectsOfType<DeckGameplay>())
                if (gp != null && gp.isOwned) { _mine = gp; break; }
        if (_mine == null) return;

        var values = _mine.cardTypes;
        int shown = 0;
        if (_mine.Cards != null)
            foreach (var c in _mine.Cards)
                if (c != null && c.activeSelf) shown++;

        string said = $"{(values != null ? values.Count : 0)} cards dealt to it, {shown} card objects in hand, holding={_mine.HaveCards}";
        if (said == _handSaid) return;
        bool first = _handSaid == null;
        _handSaid = said;
        Plugin.Log.LogInfo($"[deck2] this copy's own hand: {said}");

        // Once a match: where the hand's card objects sit, to compare a character that holds
        // seven with one that holds fewer.
        if (!first || _mine.Cards == null) return;
        var sb = new StringBuilder();
        for (int i = 0; i < _mine.Cards.Count; i++)
        {
            var c = _mine.Cards[i];
            if (c == null) continue;
            var t = c.transform;
            sb.Append($" [{i} {(t.parent != null ? t.parent.name : "-")} p{t.localPosition.x:0.000},{t.localPosition.y:0.000},{t.localPosition.z:0.000} " +
                      $"r{t.localEulerAngles.x:0},{t.localEulerAngles.y:0},{t.localEulerAngles.z:0}]");
        }
        string body = _mine.gameObject.name.StartsWith("Char_", StringComparison.Ordinal) ? _mine.gameObject.name : "player";
        Plugin.Log.LogInfo($"[deck2] this copy's hand objects ({body}):{sb}");
    }
}
