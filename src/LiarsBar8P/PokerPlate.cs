using System;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiarsBar8P;

/// <summary>
/// The Poker tab's plate in the lobby reads LIAR'S POKER, the released game's name.
///
/// The plate is a picture, and the picture reads TEXAS, the name Liar's Poker had before it came
/// out; the game's 14 Nov 2026 update still shows it. So a copy of the plate is laid over it, its
/// black band blacked out again under the name, in the plate's own colours and the player's
/// language. It sits inside the Poker tab's own panel, so it shows only while that tab is chosen,
/// and below the tab's help button, which stays on top and answers as before.
///
/// Built for each lobby screen, on every copy. It only changes what this player sees, so it is up
/// at any table, modded or not, on the host and on a client. Anything that fails leaves the game's
/// own plate. Up to v3.0.2 this was part of the Two-Card Poker lobby, which went with that table
/// in the game's update.
/// </summary>
internal static class PokerPlate
{
    // The released game's name. The game has it only inside its loading tips, not as a text
    // anything can read, so the table of words carries it, in the tips' own wording.
    private const string ReleasedName = "LIAR'S POKER";

    // The lobby's pieces. The panel is the first child of that name, before the tab's button.
    private const string Panel = "GameMode Button/Poker";
    private const string Plate = "GameMode Button/Poker/Image";
    private const string Label = "Alt/LobbyID (1)";
    private const string Name = "LB8P_LiarsPokerPlate";

    // The plate (180 x 46): the black band inside the frame, where the name goes, leaving some of
    // the band's pattern showing at each end.
    private const float NameWide = 132f;
    private const float NameHigh = 29f;
    private const float NameSize = 22f;

    // The game's own colours, read off its plate.
    private static readonly Color Band = new(0.01f, 0.01f, 0.02f, 1f);
    private static readonly Color PlateWords = new(1f, 0.945f, 0.588f, 1f);

    private static RectTransform _plate;
    private static TextMeshProUGUI _name;
    private static int _wordsFor = -1;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.Start))]
    private static void Started(LobbyController __instance)
    {
        try { Make(__instance); }
        catch (Exception e)
        {
            Drop();
            Plugin.Log.LogWarning($"[pokerplate] the Poker tab keeps the game's own plate: {e.Message}");
        }
    }

    /// <summary>A plate that failed half way goes at once, rather than at the end of the frame.</summary>
    private static void Drop()
    {
        if (_plate != null)
        {
            _plate.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(_plate.gameObject);
        }
        _plate = null;
        _name = null;
    }

    private static void Make(LobbyController room)
    {
        _plate = null;
        _name = null;
        _wordsFor = -1;
        if (room == null || RankedGuard.Near) return;

        var label = room.LoobyIdText;
        var canvas = label != null && label.canvas != null ? label.canvas.rootCanvas : null;
        var root = canvas != null ? canvas.transform : null;
        var panel = root != null ? root.Find(Panel) : null;
        var plate = root != null ? root.Find(Plate) : null;
        var words = root != null ? root.Find(Label) : null;
        if (panel == null || plate == null || words == null)
        {
            Plugin.Log.LogWarning("[pokerplate] the lobby's Poker tab is not laid out as expected - it keeps the game's own plate");
            return;
        }

        _plate = MakePlate(plate, panel, words, Name, out _name);
        if (_plate == null)
        {
            Plugin.Log.LogWarning("[pokerplate] the Poker tab's plate could not be copied cleanly - it keeps the game's own");
            return;
        }
        Plugin.Log.LogInfo("[pokerplate] the Poker tab's plate reads LIAR'S POKER");
    }

    /// <summary>A copy's box, or the copy destroyed and null if it has none.</summary>
    private static RectTransform BoxOf(GameObject copy)
    {
        var box = copy.GetComponent<RectTransform>();
        if (box == null) UnityEngine.Object.Destroy(copy);
        return box;
    }

    /// <summary>A copy of the plate on top of it, its band blacked out under the name.</summary>
    private static RectTransform MakePlate(Transform plate, Transform panel, Transform words, string name, out TextMeshProUGUI text)
    {
        text = null;
        var copy = UiClone.Make(plate.gameObject, panel, name);
        if (copy == null) return null;

        var box = BoxOf(copy);
        if (box == null) return null;
        copy.SetActive(false);
        Same(box, plate.GetComponent<RectTransform>());
        box.SetSiblingIndex(plate.GetSiblingIndex() + 1);
        foreach (var part in copy.GetComponentsInChildren<Graphic>(true)) part.raycastTarget = false;

        var size = box.sizeDelta;
        var band = Square(box, "band", (size.x - NameWide) / 2f, (size.y - NameHigh) / 2f, NameWide, NameHigh, Band);
        text = Text(words, band, "name", NameSize);
        if (text == null)
        {
            UnityEngine.Object.Destroy(copy);
            return null;
        }
        Fill(text.rectTransform, 3f);
        text.color = PlateWords;
        text.fontStyle = FontStyles.Bold;
        text.enableAutoSizing = true;
        text.fontSizeMax = NameSize;
        text.fontSizeMin = 10f;
        return box;
    }

    private static void Same(RectTransform box, RectTransform like)
    {
        box.anchorMin = like.anchorMin;
        box.anchorMax = like.anchorMax;
        box.pivot = like.pivot;
        box.sizeDelta = like.sizeDelta;
        box.anchoredPosition = like.anchoredPosition;
        box.localScale = like.localScale;
        box.localRotation = like.localRotation;
    }

    /// <summary>A plain rectangle of one colour, placed from its parent's top left.</summary>
    private static RectTransform Square(Transform parent, string name, float x, float y, float w, float h, Color colour)
    {
        var go = new GameObject(name) { layer = parent.gameObject.layer };
        var box = go.AddComponent<RectTransform>();
        box.SetParent(parent, false);
        box.anchorMin = box.anchorMax = box.pivot = new Vector2(0f, 1f);
        box.sizeDelta = new Vector2(w, h);
        box.anchoredPosition = new Vector2(x, -y);
        var image = go.AddComponent<Image>();
        image.color = colour;
        image.raycastTarget = false;
        return box;
    }

    private static void Fill(RectTransform box, float inset)
    {
        box.anchorMin = Vector2.zero;
        box.anchorMax = Vector2.one;
        box.pivot = new Vector2(0.5f, 0.5f);
        box.sizeDelta = new Vector2(-2f * inset, -2f * inset);
        box.anchoredPosition = Vector2.zero;
    }

    private static TextMeshProUGUI Text(Transform like, Transform parent, string name, float size)
    {
        var copy = UiClone.Make(like.gameObject, parent, name);
        var text = copy != null ? copy.GetComponent<TextMeshProUGUI>() : null;
        if (text == null) return null;

        text.fontSize = size;
        text.alignment = TextAlignmentOptions.Center;
        text.enableWordWrapping = false;
        text.richText = false;
        text.raycastTarget = false;
        return text;
    }

    // ------------------------------------------------------------------ from ModTicker

    /// <summary>
    /// Four times a second: up while the lobby is, and in the player's language. Inside the Poker
    /// tab's panel, so it is only seen while that tab is chosen.
    /// </summary>
    internal static void Tick()
    {
        if (_plate == null) return;

        bool up = LobbyController.Instance != null && Manager.Instance == null && !RankedGuard.Near;
        if (_plate.gameObject.activeSelf != up) _plate.gameObject.SetActive(up);

        // The words follow a change of language made in the settings while the lobby is up.
        if (_wordsFor != Words.Generation && _name != null)
        {
            _wordsFor = Words.Generation;
            _name.text = Words.T(ReleasedName);
        }
    }

    // ------------------------------------------------------------------ for the test driver

    internal static string Describe()
    {
        if (_plate == null || _name == null) return "no plate on this lobby";
        string state = _plate.gameObject.activeInHierarchy ? "showing"
                     : _plate.gameObject.activeSelf ? "up, its tab not chosen" : "off";
        return $"plate {state}, '{_name.text}' at size {_name.fontSize:0.0} " +
               $"in {(_name.font != null ? _name.font.name : "no font")}";
    }
}
