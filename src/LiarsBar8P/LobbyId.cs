using System;
using System.Globalization;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// The lobby ID, at both ends.
///
/// Every lobby already shows LOBBY ID and the number in the bar along the bottom, with a small
/// copy icon, and a click on it is meant to copy the number. The game copies through an IMGUI
/// text editor, outside the IMGUI pass that editor belongs to, and says nothing either way, so a
/// player could not tell whether anything had happened. <see cref="Copy"/> puts the number on the
/// clipboard itself and the icon's own hint says so for a moment, in the hint's own type.
///
/// At the other end the JOIN WITH ID box took anything. A number of five digits or fewer joined
/// lobby 0 and left the box up, and a minus sign or too many digits threw and did nothing at all.
/// <see cref="Check"/> says why in the game's own box instead, and joins nothing.
/// </summary>
internal static class LobbyId
{
    // Every word a player sees from this class.
    private const string Copied = "(Copied)";
    private const string Empty = "Type or paste a lobby ID first";
    private const string NotAnId = "That is not a lobby ID\nIt is the number after LOBBY ID in a lobby";

    /// <summary>The hint over the copy icon. The game shows it while the pointer is on the icon.</summary>
    private const string HintName = "Text (TMP) (4)";

    /// <summary>The panel the ID is typed into, over the lobby list.</summary>
    private const string PanelName = "JoinWithId";

    private const float CopiedSeconds = 2f;
    private const float SaySeconds = 4f;

    /// <summary>
    /// What the top twelve bits of a Steam lobby's id hold: the account type for chat, and the
    /// flag that marks a chat as a lobby. A player's own Steam id, or any other run of digits,
    /// does not have them.
    /// </summary>
    private const ulong ChatType = 8;
    private const ulong LobbyFlag = 0x40000;

    private static TextMeshProUGUI _hint;
    private static string _hintWas;
    private static bool _hintWasOn;
    private static float _copiedUntil;

    // ------------------------------------------------------------------ copying it

    [HarmonyPatch]
    internal static class Copy
    {
        /// <summary>
        /// The icon's only caller is a saved click. Taken whole at a modded table: the game's own
        /// copy is the one in doubt, and doing both could leave either on the clipboard. Anywhere
        /// else the game's own runs as it ships.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.CopyText))]
        private static bool Clicked(LobbyController __instance)
        {
            try
            {
                if (__instance == null || RankedGuard.Near || !Table.Modded) return true;

                ulong id = __instance.CurrentLobbyID;
                if (id == 0) return true;

                GUIUtility.systemCopyBuffer = id.ToString(CultureInfo.InvariantCulture);
                Acknowledge(__instance);
                Plugin.Log.LogInfo("[lobbyid] the lobby ID is on the clipboard");
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[lobbyid] could not copy the lobby ID, leaving it to the game: {e.Message}");
                return true;
            }
        }
    }

    /// <summary>The hint over the icon says it is done, for a couple of seconds.</summary>
    private static void Acknowledge(LobbyController room)
    {
        var label = room.LoobyIdText;
        var bar = label != null ? label.transform.parent : null;
        var found = bar != null ? bar.Find(HintName) : null;
        var hint = found != null ? found.GetComponent<TextMeshProUGUI>() : null;
        if (hint == null) return;

        // A second click while it already says so must not remember "copied" as the original.
        if (_copiedUntil <= 0f || _hint == null || _hint.Pointer != hint.Pointer)
        {
            Restore();
            _hint = hint;
            _hintWas = hint.text;
            _hintWasOn = hint.gameObject.activeSelf;
        }

        hint.text = Words.T(Copied);
        hint.gameObject.SetActive(true);
        _copiedUntil = Time.unscaledTime + CopiedSeconds;
    }

    /// <summary>From <see cref="ModTicker"/>. The hint goes back to what it said once the moment is over.</summary>
    internal static void Tick()
    {
        if (_copiedUntil <= 0f) return;

        if (_hint == null)
        {
            _copiedUntil = 0f;     // the lobby went, and the hint with it
            return;
        }

        if (Time.unscaledTime < _copiedUntil) return;
        Restore();
    }

    private static void Restore()
    {
        _copiedUntil = 0f;
        if (_hint == null) return;

        _hint.text = _hintWas;

        // Shown for the copy only: a click always comes with the pointer on the icon, and then
        // the game's own pointer-exit hides it as before.
        if (!_hintWasOn) _hint.gameObject.SetActive(false);
        _hint = null;
    }

    // ------------------------------------------------------------------ joining with it

    [HarmonyPatch]
    internal static class Check
    {
        /// <summary>
        /// JOIN WITH ID, from the mouse and from the mod's own wiring of the button alike. Not tied
        /// to the modded pool: the box works the same with <c>ShowUnmoddedLobbies</c> on.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.JoinLobbyWithId))]
        private static bool Typed()
        {
            try
            {
                var list = LobbyListManager.instance;
                var box = list != null ? list.LobbyIdInput : null;
                if (box == null) return true;

                string why = WhyNot(box.text);
                if (why == null) return true;

                Plugin.Log.LogInfo("[lobbyid] JOIN WITH ID refused: " + (why == Empty ? "the box is empty" : "not a lobby ID"));

                // The game's box has no backing of its own and is laid out to sit over the list, so
                // over the ID panel the two read as one. The panel shuts the way its own X shuts it,
                // and keeps what was typed for when it is opened again.
                var panel = box.transform.parent;
                if (panel != null && panel.name == PanelName) panel.gameObject.SetActive(false);

                Matchmaking.Say(why, SaySeconds);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[lobbyid] could not check the lobby ID, leaving it to the game: {e.Message}");
                return true;
            }
        }
    }

    /// <summary>Why this is not a lobby this box could join, in words for the player, or null if it could be.</summary>
    internal static string WhyNot(string typed)
    {
        string text = typed != null ? typed.Trim() : "";
        if (text.Length == 0) return Empty;

        // Digits only: the box drops letters as they are typed, but keeps a leading minus.
        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)) return NotAnId;

        ulong type = (id >> 52) & 0xF;
        ulong instance = (id >> 32) & 0xFFFFF;
        return type == ChatType && (instance & LobbyFlag) != 0 ? null : NotAnId;
    }
}
