using System;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// The lobby list, for the modded pool: every lobby the search found gets a row, and the row
/// says what it is.
///
/// <b>Why the list is rebuilt rather than adjusted.</b> The search only decides what Steam sends
/// back. <c>DisplayLobbies</c> then throws away more on the client: any name containing "cc",
/// ".com" or "4466", any name that repeats another, and any name too similar to one already
/// shown, which is decided on letters and digits only. So "rebecca's lobby" never appears, and a
/// default name in Chinese, Japanese, Korean or Arabic comes down to "sloby", which is inside
/// every other default name and hides them all. Those rules read as a defence against adverts
/// in a list of thousands. In a list of six they are lobbies going missing for no reason anybody
/// could work out. They are compiled into one method and cannot be switched off one at a time,
/// so this does what the method does - same prefab, same calls, same order - without them. The
/// advertising those rules were written for would have to be running this mod to appear here.
///
/// What it keeps: a lobby has to say it is in the lobby, and the four mode toggles still work.
///
/// <b>The row.</b> The count needs nothing: it was always the real members over the real limit.
/// But the JOIN button is hidden at exactly four, which the old filter kept out of sight and a
/// lobby of 4/8 would now walk straight into, so the button is settled here by whether there is
/// a seat. The label saying what the lobby is is a copy of the count's own pill.
///
/// If building the list fails the game's own method runs instead, and clears up after this.
/// </summary>
internal static class LobbyRows
{
    internal const int NameLength = 48;
    internal const int VersionLength = 16;

    private const string TagName = "LB8P_Tag";
    private const string RulesName = "LB8P_Rules";

    /// <summary>Wide enough for the label, and clear of JOIN: the row has 640 free to the right of the count.</summary>
    private const float TagWidth = 210f;

    /// <summary>The house rules pill, after the label: QUICK, HARSH or RULES ?, and nothing for a CLASSIC lobby.</summary>
    private const float RulesWidth = 130f;
    private const float TagGap = 16f;
    private const float TextMargin = 6f;
    private const int TagFontSize = 24;

    /// <summary>The same warning colour the version readout uses.</summary>
    private static readonly Color Mismatch = new(1f, 0.45f, 0.35f, 1f);

    private static Sprite _ring;
    private static Sprite _ringFrom;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LobbyListManager), nameof(LobbyListManager.DisplayLobbies))]
    private static bool Display(LobbyListManager __instance,
                                Il2CppSystem.Collections.Generic.List<CSteamID> lobbyids)
    {
        // A search can land after the menu has gone, and the list went with it.
        if (__instance == null) return false;

        QuickMatch.ListArrived();
        MatchmakingSelfTest.Report(lobbyids);

        if (!Matchmaking.Active) return true;
        LobbyCount.Saw(lobbyids);
        if (__instance.isCreateMoment) return false;

        __instance.isCreateMoment = true;
        try
        {
            __instance.ClearLobbies();

            int count = lobbyids != null ? lobbyids.Count : 0;
            for (int i = 0; i < count; i++) AddRow(__instance, lobbyids[i]);

            // Left set, the list would never update again.
            __instance.isCreateMoment = false;
            __instance.StartCoroutine(__instance.SelectFirstLobby());
            return false;
        }
        catch (Exception e)
        {
            __instance.isCreateMoment = false;
            Plugin.Log.LogError($"[matchmaking] could not build the list, leaving it to the game: {e.Message}");
            return true;
        }
    }

    /// <summary>One lobby's row. Whatever is wrong with one lobby stays with that lobby.</summary>
    private static void AddRow(LobbyListManager list, CSteamID id)
    {
        GameObject go = null;
        try
        {
            if (SteamMatchmaking.GetLobbyData(id, Matchmaking.StateKey) != Matchmaking.InTheLobby) return;
            if (BlockList.HostBlocked(id)) return;
            if (!list.PassModeLocal(SteamMatchmaking.GetLobbyData(id, "Mode"))) return;

            go = UnityEngine.Object.Instantiate(list.lobbyDataPrefab);

            var row = go.GetComponent<LobbyDataEntry>();
            row.lobbyID = id;
            row.PlayerCount = SteamMatchmaking.GetNumLobbyMembers(id);
            row.MaxPlayerCount = SteamMatchmaking.GetLobbyMemberLimit(id);
            row.LobbyName = SteamMatchmaking.GetLobbyData(id, "name");

            go.transform.SetParent(list.lobbylistContent.transform, true);
            go.transform.localScale = Vector3.one;

            row.SetLobbyData();
            list.listOfLobbies.Add(go);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[matchmaking] skipped lobby {id.m_SteamID}: {e.Message}");
            if (go != null) UnityEngine.Object.Destroy(go);
        }
    }

    /// <summary>
    /// The name and the numbers, before the game writes them on the row. An empty name makes
    /// the game write "Empty" and skip the count, leaving the prefab's "3/4" on show, so a lobby
    /// is never left without one. The count only falls back to the host's own figure if Steam
    /// has none: it says 0 for a lobby it has no data on.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(LobbyDataEntry), nameof(LobbyDataEntry.SetLobbyData))]
    private static void BeforeRow(LobbyDataEntry __instance)
    {
        try
        {
            if (!Matchmaking.Active) return;

            string name = Matchmaking.Clean(__instance.LobbyName, NameLength);
            __instance.LobbyName = name.Length > 0 ? name : "unnamed lobby";
            if (MatchmakingSelfTest.RowNames != null && LobbyListManager.instance != null)
                __instance.LobbyName = string.Format(MatchmakingSelfTest.RowNames, LobbyListManager.instance.listOfLobbies.Count + 1);

            if (__instance.MaxPlayerCount <= 0) __instance.MaxPlayerCount = Limits.LobbySeats;

            if (MatchmakingSelfTest.RowCount > 0) __instance.PlayerCount = MatchmakingSelfTest.RowCount;
            if (__instance.PlayerCount > 0) return;

            if (int.TryParse(SteamMatchmaking.GetLobbyData(__instance.lobbyID, "playercount"), out int said))
                __instance.PlayerCount = Mathf.Clamp(said, 0, __instance.MaxPlayerCount);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[matchmaking] row data failed: {e.Message}"); }
    }

    /// <summary>The JOIN button and the label, once the game has finished with the row.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(LobbyDataEntry), nameof(LobbyDataEntry.SetLobbyData))]
    private static void AfterRow(LobbyDataEntry __instance)
    {
        try
        {
            if (!Matchmaking.Active) return;

            if (__instance.joinButton != null)
                __instance.joinButton.SetActive(__instance.PlayerCount < __instance.MaxPlayerCount);

            string version = SteamMatchmaking.GetLobbyData(__instance.lobbyID, VersionCheck.Key);
            bool matches = Matchmaking.WhyNot(__instance.lobbyID) == null;

            // Which version is the useful thing to say about a lobby this copy cannot join; the
            // reason in full comes up if the player tries. The seat count is in the pill beside
            // this one, so the label is just the mod's name.
            string label = matches || version == Plugin.Version
                ? Plugin.TagName
                : string.IsNullOrEmpty(version) ? "MOD v?"
                : $"MOD v{Matchmaking.Clean(version, VersionLength)}";

            Tag(__instance, TagName, 0f, TagWidth, label, matches);

            // Drawn only for a lobby with house rules, so an ordinary lobby's row is as it was.
            string rules = HouseRules.RowName(SteamMatchmaking.GetLobbyData(__instance.lobbyID, HouseRules.Key));
            if (rules != null) Tag(__instance, RulesName, TagGap + TagWidth, RulesWidth, rules, true);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[matchmaking] row label failed: {e.Message}"); }
    }

    /// <summary>
    /// A copy of the row's count pill with other words in it, <paramref name="after"/> further
    /// along than the first copy goes. It has no button and no localisation of its own, so the
    /// copy brings no behaviour with it. Rows are destroyed on every refresh, so nothing piles up.
    /// </summary>
    private static void Tag(LobbyDataEntry row, string tagName, float after, float width, string label, bool matches)
    {
        var count = row.playerCountext;
        var inner = count != null ? count.transform.parent : null;
        var pill = inner != null ? inner.parent : null;
        if (pill == null || pill.parent == null) return;

        var existing = pill.parent.Find(tagName);
        var copy = existing != null ? existing.gameObject
                                    : UnityEngine.Object.Instantiate(pill.gameObject, pill.parent, false);

        if (existing == null)
        {
            copy.name = tagName;

            // The pill is anchored by its right edge, so the width goes on to the left of that
            // and the position is where the new right edge wants to be.
            var box = copy.GetComponent<RectTransform>();
            box.anchoredPosition = new Vector2(box.anchoredPosition.x + after + TagGap + width, box.anchoredPosition.y);
            box.sizeDelta = new Vector2(width, box.sizeDelta.y);

            var ring = copy.GetComponentInChildren<UnityEngine.UI.Image>(true);
            if (ring != null) KeepRoundEnds(ring, box.sizeDelta.y);

            // Under the row's hover highlight, which lives in the last child, not on top of it.
            copy.transform.SetSiblingIndex(pill.GetSiblingIndex() + 1 + (after > 0f ? 1 : 0));
        }

        var text = copy.GetComponentInChildren<UnityEngine.UI.Text>(true);
        if (text == null) return;

        // Only the pill's background stretches with it; the words have a box of their own,
        // pivoted on its left edge in the middle of the pill.
        float wide = width - TextMargin;
        var words = text.rectTransform;
        words.sizeDelta = new Vector2(wide, words.sizeDelta.y);
        words.anchoredPosition = new Vector2(-wide / 2f, words.anchoredPosition.y);

        text.supportRichText = false;
        text.fontSize = TagFontSize;
        if (!matches) text.color = Mismatch;
        text.text = label;
    }

    /// <summary>
    /// The ring is one plain picture, so stretched wide its round ends flatten into an oval. The
    /// same picture with its ends marked stretches only in the straight middle, at the height
    /// and line thickness of the count beside it. One copy does every row: it outlives them, and
    /// it goes when the menu does, along with the picture it came from.
    /// </summary>
    private static void KeepRoundEnds(UnityEngine.UI.Image ring, float height)
    {
        var from = ring.sprite;
        if (from == null || from.texture == null || height <= 0f) return;

        if (_ring == null || _ringFrom != from)
        {
            float end = from.rect.height / 2f;
            _ring = Sprite.Create(from.texture, from.rect, new Vector2(0.5f, 0.5f), from.pixelsPerUnit, 0u,
                                  SpriteMeshType.FullRect, new Vector4(end, end, end, end));
            _ringFrom = from;
        }

        ring.sprite = _ring;
        ring.type = UnityEngine.UI.Image.Type.Sliced;
        ring.pixelsPerUnitMultiplier = from.rect.height / (height * ring.pixelsPerUnit);
    }
}
