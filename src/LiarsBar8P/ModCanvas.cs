using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiarsBar8P;

/// <summary>
/// Where the mod's own lines of text are drawn, so that they read as part of the game: the game's
/// type on a dark strip with a fine gold line, the look of the bar along the bottom of its lobby.
///
/// It is a canvas of its own that outlives every scene, so a line stays where it was put, and it
/// scales with the screen, so a line reads the same on a Steam Deck as on a 4K monitor. It takes
/// no clicks.
///
/// Until it is up <see cref="Ready"/> is false and each of these lines is drawn plainly by whoever
/// owns it, and the same goes for good if anything here ever fails: what the lines say is worth
/// more than how they look.
///
/// Players' names come from everywhere and the game's face is Latin only. The game itself swaps
/// to Noto for Chinese and Japanese names, so that is its fallback here. Nothing in the game has
/// Hangul, Arabic or Thai, so a line holding those is drawn plainly (<see cref="Fits"/>), where
/// the system's fonts can take over: on the turn line a row of boxes would tell nobody anything.
///
/// The faces follow the language (<see cref="Words"/>). Oswald Bold has no Cyrillic, so Russian is
/// drawn in Oswald Regular, the face the game itself uses for Russian, and Regular stands behind
/// Bold for any Cyrillic name. Chinese stays in Bold, with its characters from the game's Noto, so
/// a version number keeps the game's own face. Turkish is in Bold already.
/// </summary>
internal static class ModCanvas
{
    internal const string GoldTag = "#FAD154";
    internal const string FaintTag = "#F2EBD1B0";

    internal static readonly Color Cream = new(0.95f, 0.92f, 0.82f, 1f);
    internal static readonly Color Gold = new(0.98f, 0.82f, 0.33f, 1f);
    internal static readonly Color Warning = new(1f, 0.45f, 0.35f, 1f);

    private const float SideRoom = 24f;

    private const string Face = "Fonts & Materials/Oswald Bold SDF";
    private const string WideFace = "Fonts & Materials/NotoSansSC-Medium SDF";
    private const string RegularFace = "Fonts & Materials/Oswald Regular SDF";

    private static readonly Color Backing = new(0.03f, 0.025f, 0.02f, 0.82f);
    private static readonly Color Rule = new(0.98f, 0.82f, 0.33f, 0.85f);

    private static GameObject _root;
    private static TMP_FontAsset _font;
    private static TMP_FontAsset _regular;
    private static bool _failed;
    private static readonly List<Line> _lines = new();

    internal static bool Ready { get; private set; }

    /// <summary>One line of text on its strip. Off until it has something to say.</summary>
    internal sealed class Line
    {
        internal RectTransform Box;
        internal TextMeshProUGUI Text;
        internal float Height;
        private string _shown;

        internal void Set(string words)
        {
            if (words == _shown) return;
            _shown = words;

            bool any = !string.IsNullOrEmpty(words);
            Box.gameObject.SetActive(any);
            if (!any) return;

            Text.text = words;
            Box.sizeDelta = new Vector2(Text.GetPreferredValues(words, 4000f, Height).x + SideRoom, Height);
        }

        /// <summary>The face for the language now, and the width measured again in it.</summary>
        internal void Refont()
        {
            Text.font = LineFace();
            string words = _shown;
            _shown = null;
            Set(words);
        }
    }

    /// <summary>The face a line is drawn in, for the game's language.</summary>
    private static TMP_FontAsset LineFace() => Words.Now == Tongue.Ru && _regular != null ? _regular : _font;

    /// <summary>From <see cref="Words"/>, when the game's language changes.</summary>
    internal static void OnLanguage()
    {
        if (!Ready) return;
        foreach (var line in _lines)
        {
            try { line.Refont(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[words] a line kept its face: {e.Message}"); }
        }
    }

    /// <summary>From <see cref="ModTicker"/>. True once the canvas is up.</summary>
    internal static bool Build()
    {
        if (Ready) return true;
        if (_failed) return false;

        _font = GameFont();
        if (_font == null) return false;
        CoverOtherScripts();

        _root = new GameObject("LB8P_Canvas") { layer = 5 };
        UnityEngine.Object.DontDestroyOnLoad(_root);

        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 30000;

        var scaler = _root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 1f;

        Ready = true;
        Plugin.Log.LogInfo($"[version] text is in the game's font now ({_font.name})");
        return true;
    }

    /// <summary>Give up for good. Everything goes back to being drawn plainly.</summary>
    internal static void Fail(string what, Exception e)
    {
        _failed = true;
        Ready = false;
        if (_root != null) UnityEngine.Object.Destroy(_root);
        Plugin.Log.LogWarning($"[version] {what} failed, going back to plain text: {e.Message}");
    }

    /// <summary>A font the game keeps in its Resources, so it is there from the first frame, whatever has loaded.</summary>
    internal static TMP_FontAsset Load(string path)
    {
        var found = Resources.Load(path, Il2CppType.Of<TMP_FontAsset>());
        return found != null ? found.TryCast<TMP_FontAsset>() : null;
    }

    /// <summary>The game's own face. Either way of finding it will do.</summary>
    internal static TMP_FontAsset GameFont()
    {
        var face = Load(Face);
        if (face != null) return face;

        var list = LobbyListManager.instance;
        if (list != null && list.ConnectingScreenInfo != null && list.ConnectingScreenInfo.font != null)
            return list.ConnectingScreenInfo.font;

        foreach (var found in Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMP_FontAsset>()))
        {
            var font = found.TryCast<TMP_FontAsset>();
            if (font != null && font.name != null && font.name.Contains("Oswald")) return font;
        }
        return null;
    }

    /// <summary>The game's face for a language, for text drawn outside this canvas: Regular for Russian, Noto for Chinese.</summary>
    internal static TMP_FontAsset FaceFor(Tongue tongue) => tongue switch
    {
        Tongue.Ru => Load(RegularFace) ?? GameFont(),
        Tongue.Zh => Load(WideFace) ?? GameFont(),
        _ => GameFont(),
    };

    private static void CoverOtherScripts()
    {
        _regular = Load(RegularFace);
        if (_regular == _font) _regular = null;
        var wide = Load(WideFace);
        if (wide == _font) wide = null;

        // Bold: Cyrillic from Regular, then Chinese from Noto. Regular: Chinese from Noto, for a
        // Chinese name at a Russian table.
        Behind(_font, _regular);
        Behind(_font, wide);
        Behind(_regular, wide);
    }

    private static void Behind(TMP_FontAsset font, TMP_FontAsset fallback)
    {
        if (font == null || fallback == null) return;
        var table = font.fallbackFontAssetTable ?? new Il2CppSystem.Collections.Generic.List<TMP_FontAsset>();
        if (!table.Contains(fallback)) table.Add(fallback);
        font.fallbackFontAssetTable = table;
    }

    /// <summary>Whether the game's fonts can draw all of this.</summary>
    internal static bool Fits(string words)
    {
        if (string.IsNullOrEmpty(words) || _font == null) return true;

        foreach (char c in words)
            if (c > '~' && !_font.HasCharacter(c, true, true)) return false;
        return true;
    }

    /// <summary>
    /// A new line, placed by the point of the screen it hangs from: (0.5, 1) is the middle of the
    /// top edge. Rich text only for words the mod wrote itself: a player's name goes in as it
    /// is, or a name could restyle the screen for everybody at the table.
    /// </summary>
    internal static Line NewLine(string name, Vector2 anchor, float height, float size, Color colour,
                                 bool rule = false, bool rich = false, TextAlignmentOptions align = TextAlignmentOptions.Center)
    {
        var box = Part(name, _root.transform);
        box.anchorMin = box.anchorMax = box.pivot = anchor;

        var back = box.gameObject.AddComponent<Image>();
        back.color = Backing;
        back.raycastTarget = false;

        if (rule)
        {
            var line = Part("rule", box);
            line.anchorMin = Vector2.zero;
            line.anchorMax = Vector2.right;
            line.pivot = new Vector2(0.5f, 0f);
            line.sizeDelta = new Vector2(0f, 2f);
            var ink = line.gameObject.AddComponent<Image>();
            ink.color = Rule;
            ink.raycastTarget = false;
        }

        var words = Part("words", box);
        words.anchorMin = Vector2.zero;
        words.anchorMax = Vector2.one;
        words.offsetMin = new Vector2(SideRoom / 2f, 0f);
        words.offsetMax = new Vector2(-SideRoom / 2f, 0f);

        var text = words.gameObject.AddComponent<TextMeshProUGUI>();
        text.font = LineFace();
        text.fontSize = size;
        text.color = colour;
        text.alignment = align;
        text.enableWordWrapping = false;
        text.richText = rich;
        text.raycastTarget = false;

        box.gameObject.SetActive(false);
        var made = new Line { Box = box, Text = text, Height = height };
        _lines.Add(made);
        return made;
    }

    private static RectTransform Part(string name, Transform parent)
    {
        var go = new GameObject(name) { layer = 5 };
        var part = go.AddComponent<RectTransform>();
        part.SetParent(parent, false);
        return part;
    }
}
