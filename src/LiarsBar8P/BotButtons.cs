using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Mirror;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LiarsBar8P;

/// <summary>
/// ADD BOT and REMOVE BOT in the lobby, for the host.
///
/// Two copies of the game's own INVITE button, in the column above it at the right of the screen,
/// so they look, light up and sound as INVITE does. Where INVITE has its picture they have the
/// empty ring of the lobby's countdown with a plus or a minus in it, in the game's own font, and
/// their words under it. The overlay does not move with the lobby's camera, and that column is
/// bare at any table size.
///
/// ADD BOT seats one bot. REMOVE BOT, there only while a bot is, sends the newest away; the game's
/// Kick on a bot's podium does that too (<see cref="BotLobby"/>). Both are only for the host of a
/// table where everybody runs the mod (<see cref="BotManager.Offered"/>), so a client never has
/// them. Where a bot cannot sit at the moment, at a table bots do not play yet or a full one, ADD
/// BOT stays where it is and a click says why in the chat, on this screen only, and bots that
/// leave because the table changed to one they do not play say so there too.
/// </summary>
internal static class BotButtons
{
    // Every word a player sees from this class.
    private const string AddWords = "ADD BOT";
    private const string RemoveWords = "REMOVE BOT";
    private const string NotHere = "Bots can't play this table yet";
    private const string Full = "The table is full";
    private const string Left = "Bots can't play this table yet, so they left";
    private const string Plus = "+";
    private const string Minus = "-";

    private const string AddName = "LB8P_AddBot";
    private const string RemoveName = "LB8P_RemoveBot";
    private const string MarkName = "LB8P_Mark";

    /// <summary>From one button to the next up the column, in the canvas's 1080 units: INVITE's height and a gap.</summary>
    private const float Step = 118f;

    /// <summary>The plus and the minus, in the ring where INVITE's picture was.</summary>
    private const float MarkSize = 56f;

    /// <summary>The font's minus is a short dash sitting low: drawn wider and higher, it matches the plus's bar.</summary>
    private const float MinusWide = 1.7f;
    private const float MinusLift = 5f;

    private static GameObject _add, _remove;
    private static string _ring;
    private static int _words = -1;
    private static IntPtr _fitFont;
    private static int _sentAway;

    [HarmonyPatch]
    internal static class Build
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.Start))]
        private static void Started(LobbyController __instance)
        {
            try { Make(__instance); }
            catch (Exception e)
            {
                Drop();
                Plugin.Log.LogWarning($"[bot] no ADD BOT in this lobby: {e.Message}");
            }
        }
    }

    private static void Drop()
    {
        if (_add != null) UnityEngine.Object.Destroy(_add);
        if (_remove != null) UnityEngine.Object.Destroy(_remove);
        _add = _remove = null;
    }

    private static void Make(LobbyController room)
    {
        _add = _remove = null;   // the last lobby's went with its scene
        _sentAway = BotManager.SentAway;
        if (room == null || RankedGuard.Near) return;

        var invite = room.invitebutton;
        if (invite == null)
        {
            Plugin.Log.LogWarning("[bot] the lobby has no INVITE button to copy - no ADD BOT");
            return;
        }

        var ring = Ring(room);
        _ring = ring != null ? ring.name : "none, INVITE's picture kept";
        _add = Copy(invite, AddName, 1, AddWords, Plus, ring, Added);
        _remove = Copy(invite, RemoveName, 2, RemoveWords, Minus, ring, Removed);
        if (_add == null || _remove == null)
        {
            Drop();
            Plugin.Log.LogWarning("[bot] INVITE could not be copied cleanly - no ADD BOT in this lobby");
            return;
        }

        _add.SetActive(false);
        _remove.SetActive(false);
        _words = Words.Generation;
        Fit();
    }

    /// <summary>The countdown's empty ring, which is INVITE's ring without its picture, or null.</summary>
    private static Sprite Ring(LobbyController room)
    {
        try
        {
            var count = room.CountDownText;
            var holder = count != null ? count.transform.parent : null;
            var image = holder != null ? holder.GetComponent<Image>() : null;
            return image != null ? image.sprite : null;
        }
        catch { return null; }
    }

    private static GameObject Copy(GameObject invite, string name, int up, string words, string mark, Sprite ring, Action click)
    {
        var copy = UiClone.Make(invite, invite.transform.parent, name, click);
        if (copy == null) return null;

        var box = copy.GetComponent<RectTransform>();
        var from = invite.GetComponent<RectTransform>();
        if (box != null && from != null) box.anchoredPosition = from.anchoredPosition + new Vector2(0f, Step * up);
        copy.transform.SetSiblingIndex(invite.transform.GetSiblingIndex() + 1);

        var lit = copy.transform.Find("Active");
        var unlit = copy.transform.Find("Passive");
        if (lit == null || unlit == null)
        {
            UnityEngine.Object.Destroy(copy);
            return null;
        }

        // INVITE's words may run past the screen's edge; these are longer, so they stay over the button.
        float wide = box != null ? box.sizeDelta.x : 0f;
        Dress(lit, words, mark, ring, ModCanvas.Gold, wide);
        Dress(unlit, words, mark, ring, Color.white, wide);
        Hover(copy, lit.gameObject, unlit.gameObject);
        Rest(copy);
        return copy;
    }

    /// <summary>One of the two looks, lit or not: its words, and the ring with the mark in place of INVITE's picture.</summary>
    private static void Dress(Transform look, string words, string mark, Sprite ring, Color tint, float wide)
    {
        var label = Label(look);
        if (label != null)
        {
            var size = label.rectTransform.sizeDelta;
            if (wide > 0f && size.x > wide) label.rectTransform.sizeDelta = new Vector2(wide, size.y);
            label.richText = false;
            label.enableWordWrapping = false;
            label.fontSizeMax = label.fontSize;
            label.fontSizeMin = Math.Min(12f, label.fontSize);
            label.enableAutoSizing = true;
            label.text = Words.T(words);
        }

        var picture = look.GetComponentInChildren<Image>(true);
        if (picture == null || ring == null || label == null) return;

        picture.sprite = ring;
        picture.color = tint;

        var copy = UnityEngine.Object.Instantiate(label.gameObject, picture.transform, false);
        copy.name = MarkName;
        var rect = copy.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = Vector2.zero;
        rect.anchoredPosition = mark == Minus ? new Vector2(0f, MinusLift) : Vector2.zero;
        if (mark == Minus) rect.localScale = new Vector3(MinusWide, 1f, 1f);

        var text = copy.GetComponent<TextMeshProUGUI>();
        text.enableAutoSizing = false;
        text.fontSize = MarkSize;
        text.alignment = TextAlignmentOptions.Center;
        text.raycastTarget = false;
        text.text = mark;
    }

    /// <summary>A look's words: its text that is not the mark.</summary>
    private static TextMeshProUGUI Label(Transform look)
    {
        if (look == null) return null;
        foreach (var text in look.GetComponentsInChildren<TextMeshProUGUI>(true))
            if (text != null && text.gameObject.name != MarkName) return text;
        return null;
    }

    /// <summary>
    /// INVITE lights up under the pointer, with a sound, from its own event trigger. The copy keeps
    /// the trigger's sounds only, so the same is done here, on the copy's own two looks.
    /// </summary>
    private static void Hover(GameObject copy, GameObject lit, GameObject unlit)
    {
        var trigger = copy.GetComponent<EventTrigger>();
        if (trigger == null) return;
        var sound = copy.GetComponent<AudioSource>();

        On(trigger, EventTriggerType.PointerEnter, () =>
        {
            lit.SetActive(true);
            unlit.SetActive(false);
            if (sound != null) sound.Play();
        });
        On(trigger, EventTriggerType.PointerExit, () =>
        {
            unlit.SetActive(true);
            lit.SetActive(false);
        });
    }

    private static void On(EventTrigger trigger, EventTriggerType type, Action act)
    {
        if (trigger.triggers == null) trigger.triggers = new Il2CppSystem.Collections.Generic.List<EventTrigger.Entry>();

        EventTrigger.Entry entry = null;
        foreach (var e in trigger.triggers)
            if (e != null && e.eventID == type) { entry = e; break; }
        if (entry == null)
        {
            entry = new EventTrigger.Entry { eventID = type };
            trigger.triggers.Add(entry);
        }
        if (entry.callback == null) entry.callback = new EventTrigger.TriggerEvent();
        entry.callback.AddListener(new Action<BaseEventData>(_ => act()));
    }

    /// <summary>Unlit, as INVITE rests.</summary>
    private static void Rest(GameObject button)
    {
        var lit = button.transform.Find("Active");
        var unlit = button.transform.Find("Passive");
        if (lit != null) lit.gameObject.SetActive(false);
        if (unlit != null) unlit.gameObject.SetActive(true);
    }

    // ------------------------------------------------------------------ the clicks

    private static void Added()
    {
        try
        {
            string line = BotManager.AddFromLobby() switch
            {
                BotManager.Refusal.Table => NotHere,
                BotManager.Refusal.Full => Full,
                _ => null,
            };
            if (line != null) Say(Words.T(line));
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[bot] ADD BOT failed: {e.Message}"); }
    }

    private static void Removed()
    {
        try { BotManager.RemoveBot(); }
        catch (Exception e) { Plugin.Log.LogWarning($"[bot] REMOVE BOT failed: {e.Message}"); }
    }

    /// <summary>On this screen only: the game's chat panel, which nobody else sees this in.</summary>
    private static void Say(string line)
    {
        try
        {
            var chat = ChatArayuz.instance;
            if (chat != null) chat.AddMessage(line);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[bot] could not say it in the chat: {e.Message}"); }
    }

    // ------------------------------------------------------------------ from ModTicker

    /// <summary>Four times a second: which buttons the host has, their words, and the line when bots leave.</summary>
    internal static void Tick()
    {
        if (_add == null || _remove == null) return;

        bool host = NetworkServer.active && LobbyController.Instance != null && Manager.Instance == null && !RankedGuard.Near;
        Show(_add, BotManager.Offered);
        Show(_remove, host && BotManager.Count > 0);

        // The words follow a change of language made in the settings while the lobby is up, and
        // are sized again for it, and again when the game's own font for it arrives.
        if (Words.Generation != _words)
        {
            _words = Words.Generation;
            Relabel(_add, AddWords);
            Relabel(_remove, RemoveWords);
            Fit();
        }
        else if (FontNow() != _fitFont) Fit();

        if (BotManager.SentAway != _sentAway)
        {
            _sentAway = BotManager.SentAway;
            if (host) Say(Words.T(Left));
        }
    }

    private static void Show(GameObject button, bool on)
    {
        if (button.activeSelf == on) return;
        button.SetActive(on);
        if (on) Rest(button);   // one hidden under the pointer comes back unlit
    }

    private static void Relabel(GameObject button, string words)
    {
        foreach (var text in Labels(button)) text.text = Words.T(words);
    }

    private static List<TextMeshProUGUI> Labels(GameObject button)
    {
        var found = new List<TextMeshProUGUI>();
        foreach (var text in button.GetComponentsInChildren<TextMeshProUGUI>(true))
            if (text != null && text.gameObject.name != MarkName) found.Add(text);
        return found;
    }

    private static IntPtr FontNow()
    {
        var labels = Labels(_add);
        var font = labels.Count > 0 ? labels[0].font : null;
        return font != null ? font.Pointer : IntPtr.Zero;
    }

    /// <summary>
    /// Both buttons' words at one size, as the game's own buttons share one: the largest at which
    /// the longest of them still fits over its button, in this language and its font.
    /// </summary>
    private static void Fit()
    {
        var labels = Labels(_add);
        labels.AddRange(Labels(_remove));
        if (labels.Count == 0) return;

        // From INVITE's own size each time: the size a last language was given is where its fitting starts.
        float size = float.MaxValue;
        foreach (var text in labels)
        {
            text.fontSize = text.fontSizeMax;
            text.enableAutoSizing = true;
            text.ForceMeshUpdate(true, true);
            size = Math.Min(size, text.fontSize);
        }
        foreach (var text in labels)
        {
            text.enableAutoSizing = false;
            text.fontSize = size;
        }
        _fitFont = FontNow();
    }

    // ------------------------------------------------------------------ for the test driver

    /// <summary>Press ADD BOT or REMOVE BOT through its own button, as a click would.</summary>
    internal static string Press(bool add)
    {
        var button = add ? _add : _remove;
        if (button == null) return "not built in this lobby";
        if (!button.activeInHierarchy) return "not showing";
        button.GetComponent<Button>().onClick.Invoke();
        return "pressed";
    }

    /// <summary>Each button: shown or not, where on screen in pixels, and its words.</summary>
    internal static string Describe()
    {
        if (_add == null || _remove == null) return "no buttons in this lobby";

        var sb = new StringBuilder($"ring {_ring};");
        foreach (var button in new[] { _add, _remove })
        {
            sb.Append($" {button.name}: ");
            if (!button.activeInHierarchy) { sb.Append("hidden;"); continue; }

            var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
            button.GetComponent<RectTransform>().GetWorldCorners(corners);
            var label = Label(button.transform.Find("Passive"));
            sb.Append($"x {corners[0].x:0}..{corners[2].x:0}, y {corners[0].y:0}..{corners[2].y:0} of {Screen.width}x{Screen.height}, " +
                      $"'{(label != null ? label.text : "?")}' at {(label != null ? label.fontSize : 0f):0.0};");
        }
        return sb.ToString();
    }
}
