using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LiarsBar8P;

/// <summary>
/// The game's screens at a size wider than 16:9: canvases that keep their height, and the few
/// full-screen pictures that would stretch.
///
/// The game's screen-space canvases scale with the screen and the menus lean towards its height,
/// but the match HUD follows the width alone: at 3440x1440 it is drawn 1.79 times its size on a
/// canvas only 804 units tall, so the Devil card strip, Poker's key hints and Spin's table name fall
/// off the bottom and the top. <see cref="FitCanvases"/> sets every root screen-space canvas that
/// scales with the screen to follow the height while the screen is wide, so each part keeps the
/// size and place 16:9 gives it against the screen's height and the parts tied to an edge go out to
/// the edges, and it puts each one's own setting back when the screen is not wide. The mod's own
/// lines already follow the height, so they line up with the HUD again.
///
/// <see cref="FixArt"/>: the menu's background video and the Velvet Room teaser are cropped top
/// and bottom instead of stretched, the DRAW and GOD SAVED lettering keeps its shape, and Table
/// Stories and the rank page, which are fixed 16:9 pages, get black at their sides instead of the
/// menu showing, and taking clicks, through. The death strokes, eyelids and fades are edge effects
/// and stretch well, so they are left as they are.
///
/// <see cref="FixPanels"/>: Liar's Poker's hand rankings, which hang from the middle at the left
/// edge's distance, are kept that far from the left edge, and its showdown rows are clipped to the
/// middle 16:9, so the parked ones stay out of sight and the shown ones centred; the bars' pause
/// fade, loading panel and credits, plain panels in a fixed 16:9 box, are made the whole canvas's
/// size. Everything is put back as the game had it when the screen is not wide. The host change's
/// picture, a fixed 16:9 box too, is left as it is: it is see-through but for its small middle box,
/// so the bar shows round it at any width, as at 16:9.
///
/// All three look only at objects in loaded scenes, and only at a scene change, a change of size, and
/// every few seconds while the screen is wide. At 16:9 or narrower they change nothing at all. They
/// skip the ranked screen, which never runs with the mod anyway, and each one that fails puts back
/// what it can and stops, leaving the game's own look. Nobody else at the table sees any of it.
/// </summary>
internal static class UltrawideArt
{
    /// <summary>Seconds between looks while wide: the HUD and the pages are in their scene from the start.</summary>
    private const float Every = 3f;

    /// <summary>The match HUD's canvas sits under this in every bar.</summary>
    private const string HudHolder = "GameSettings (1)";
    private static readonly string[] Letterings = { "Draw", "TexasUI/GodSaved" };

    /// <summary>The menu's canvas, and its two fixed 16:9 pages.</summary>
    private const string MenuCanvas = "Canvas (1)";
    private static readonly string[] Pages = { "TableStories", "state_menu/rankpanel" };
    private const string SidesName = "LB8P_Sides";

    /// <summary>A render texture this close to 16:9 is a video made for a 16:9 screen.</summary>
    private const float VideoShape = 0.02f;

    /// <summary>When a piece looks again: a new scene, a new size, or a few seconds while wide.</summary>
    private sealed class Watch
    {
        private int _scene = int.MinValue, _w, _h;
        private int _againAt = -1;
        private float _next;

        /// <summary>Look again two frames from now, once every canvas has caught up with a change of its scaling.</summary>
        internal void Soon() => _againAt = Time.frameCount + 2;

        internal bool Due(bool wide, bool holding)
        {
            int scene = SceneManager.GetActiveScene().handle;
            bool again = _againAt >= 0 && Time.frameCount >= _againAt;
            bool moved = again || scene != _scene || Screen.width != _w || Screen.height != _h;
            float now = Time.unscaledTime;
            if (!moved && (now < _next || (!wide && !holding))) return false;

            if (again) _againAt = -1;
            _scene = scene;
            _w = Screen.width;
            _h = Screen.height;
            _next = now + Every;
            return true;
        }
    }

    // ------------------------------------------------------------------ B: canvases

    private static readonly Watch _fitWatch = new();
    private static readonly Dictionary<int, float> _matched = new();   // canvas scaler, by instance, to its own setting
    private static bool _fitOff;
    private static string _fitSaid;

    /// <summary>From <see cref="ModTicker"/>.</summary>
    internal static void FitCanvases()
    {
        if (_fitOff) return;
        try { Fit(); }
        catch (Exception e)
        {
            _fitOff = true;
            Plugin.Log.LogWarning($"[wide] canvases are left as the game has them from now on: {e.Message}");
            try { PutCanvasesBack(); } catch { }
        }
    }

    private static void Fit()
    {
        bool wide = Ultrawide.ScreenWide;
        if (!_fitWatch.Due(wide, _matched.Count > 0) || Ranked()) return;

        if (!wide)
        {
            int back = _matched.Count > 0 ? PutCanvasesBack() : 0;
            Say(ref _fitSaid, back > 0 ? $"{back} canvases are back to the game's own scaling" : "not wider than 16:9, no canvas changed");
            return;
        }

        var seen = new HashSet<int>();
        var was = new List<string>();
        bool changed = false;
        foreach (var found in Resources.FindObjectsOfTypeAll(Il2CppType.Of<CanvasScaler>()))
        {
            var scaler = found.TryCast<CanvasScaler>();
            if (scaler == null || !scaler.gameObject.scene.IsValid() || !Scales(scaler)) continue;

            int id = scaler.GetInstanceID();
            if (!_matched.TryGetValue(id, out float own))
            {
                own = scaler.matchWidthOrHeight;
                _matched[id] = own;
            }
            if (scaler.matchWidthOrHeight != 1f)
            {
                scaler.matchWidthOrHeight = 1f;
                changed = true;
            }
            seen.Add(id);
            was.Add(own.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        }
        Forget(_matched, seen);

        // The panels across the screen are sized from their canvas, which takes a frame or two to
        // follow its new scaling: the panel pass in this same tick reads the old size, so it runs
        // again once the canvases have caught up.
        if (changed) _panelWatch.Soon();
        Say(ref _fitSaid, $"{seen.Count} canvases follow the height (their own: {string.Join(", ", was)})");
    }

    /// <summary>A root canvas on the screen, scaling with it, between its width and its height.</summary>
    private static bool Scales(CanvasScaler scaler)
    {
        if (scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize ||
            scaler.screenMatchMode != CanvasScaler.ScreenMatchMode.MatchWidthOrHeight) return false;
        var canvas = scaler.GetComponent<Canvas>();
        return canvas != null && canvas.renderMode != RenderMode.WorldSpace && IsRoot(canvas.transform);
    }

    private static int PutCanvasesBack()
    {
        int back = 0;
        foreach (var found in Resources.FindObjectsOfTypeAll(Il2CppType.Of<CanvasScaler>()))
        {
            var scaler = found.TryCast<CanvasScaler>();
            if (scaler == null || !_matched.TryGetValue(scaler.GetInstanceID(), out float own)) continue;
            scaler.matchWidthOrHeight = own;
            back++;
        }
        _matched.Clear();   // the rest went with their scenes
        return back;
    }

    // ------------------------------------------------------------------ C: pictures

    private static readonly Watch _artWatch = new();
    private static readonly Dictionary<int, Rect> _cropped = new();    // raw image, by instance, to its own picture rectangle
    private static readonly Dictionary<int, bool> _shaped = new();     // image, by instance, to its own keep-shape setting
    private static readonly List<GameObject> _sides = new();
    private static bool _artOff;
    private static string _artSaid;

    /// <summary>From <see cref="ModTicker"/>.</summary>
    internal static void FixArt()
    {
        if (_artOff) return;
        try { Art(); }
        catch (Exception e)
        {
            _artOff = true;
            Plugin.Log.LogWarning($"[wide] the full-screen pictures are left as the game draws them from now on: {e.Message}");
            try { Videos(false); } catch { }
            try { Lettering(null, false); } catch { }
            try { Sides(null, false); } catch { }
        }
    }

    private static void Art()
    {
        bool wide = Ultrawide.ScreenWide;
        bool holding = _cropped.Count + _shaped.Count + _sides.Count > 0;
        if (!_artWatch.Due(wide, holding) || Ranked()) return;

        if (!wide && !holding)
        {
            Say(ref _artSaid, "not wider than 16:9, no picture changed");
            return;
        }

        var roots = wide ? RootCanvases() : null;
        int videos = Videos(wide), letters = Lettering(roots, wide), pages = Sides(roots, wide);
        Say(ref _artSaid, wide ? $"{videos} videos cropped, {letters} letterings kept in shape, {pages} pages with black sides"
                               : "every picture is back as the game draws it");
    }

    /// <summary>
    /// C1. A full-screen picture of a 16:9 render texture is a video made for 16:9: show its middle
    /// band at the screen's shape, so round things stay round, instead of stretching it.
    /// </summary>
    private static int Videos(bool wide)
    {
        var seen = new HashSet<int>();
        float band = wide ? 16f / 9f / ((float)Screen.width / Screen.height) : 1f;
        foreach (var found in Resources.FindObjectsOfTypeAll(Il2CppType.Of<RawImage>()))
        {
            var raw = found.TryCast<RawImage>();
            if (raw == null || !raw.gameObject.scene.IsValid()) continue;

            int id = raw.GetInstanceID();
            bool had = _cropped.TryGetValue(id, out var own);
            if (!wide || !IsVideo(raw))
            {
                if (had) { raw.uvRect = own; _cropped.Remove(id); }
                continue;
            }

            if (!had) { own = raw.uvRect; _cropped[id] = own; }
            var want = new Rect(own.x, own.y + own.height * (1f - band) / 2f, own.width, own.height * band);
            if (raw.uvRect != want) raw.uvRect = want;
            seen.Add(id);
        }
        if (wide) Forget(_cropped, seen);
        else _cropped.Clear();
        return seen.Count;
    }

    private static bool IsVideo(RawImage raw)
    {
        var texture = raw.texture != null ? raw.texture.TryCast<RenderTexture>() : null;
        if (texture == null || texture.height <= 0 || Mathf.Abs((float)texture.width / texture.height - 16f / 9f) > VideoShape) return false;

        var root = RootCanvas(raw.transform);
        if (root == null || root.renderMode == RenderMode.WorldSpace) return false;
        Vector2 mine = WorldSize(raw.rectTransform), whole = WorldSize(root.GetComponent<RectTransform>());
        return whole.x > 0f && whole.y > 0f && Mathf.Abs(mine.x / whole.x - 1f) < 0.02f && Mathf.Abs(mine.y / whole.y - 1f) < 0.02f;
    }

    /// <summary>C2. The match's DRAW and Poker's GOD SAVED are lettering across the whole screen: keep their shape.</summary>
    private static int Lettering(List<Canvas> roots, bool wide)
    {
        if (!wide || roots == null)
        {
            if (_shaped.Count > 0)
                foreach (var found in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Image>()))
                {
                    var image = found.TryCast<Image>();
                    if (image != null && _shaped.TryGetValue(image.GetInstanceID(), out bool own)) image.preserveAspect = own;
                }
            _shaped.Clear();
            return 0;
        }

        var seen = new HashSet<int>();
        foreach (var canvas in roots)
        {
            var holder = canvas.transform.parent;
            if (holder == null || holder.name != HudHolder) continue;
            foreach (string path in Letterings)
            {
                var at = canvas.transform.Find(path);
                var image = at != null ? at.GetComponent<Image>() : null;
                if (image == null) continue;

                int id = image.GetInstanceID();
                if (!_shaped.ContainsKey(id)) _shaped[id] = image.preserveAspect;
                if (!image.preserveAspect) image.preserveAspect = true;
                seen.Add(id);
            }
        }
        Forget(_shaped, seen);
        return seen.Count;
    }

    /// <summary>
    /// C3. Black behind each fixed page, the canvas's whole width, as the page's first child so it
    /// opens and closes with the page and draws behind everything on it. It takes the clicks the page's
    /// own art takes in the middle, so the menu behind cannot be pressed through the sides.
    /// </summary>
    private static int Sides(List<Canvas> roots, bool wide)
    {
        for (int i = _sides.Count - 1; i >= 0; i--)
        {
            if (_sides[i] != null && wide) continue;
            if (_sides[i] != null) UnityEngine.Object.Destroy(_sides[i]);
            _sides.RemoveAt(i);
        }
        if (!wide || roots == null) return 0;

        foreach (var canvas in roots)
        {
            if (canvas.name != MenuCanvas || canvas.gameObject.scene.name != Matchmaking.Menu) continue;
            var whole = canvas.GetComponent<RectTransform>();
            foreach (string path in Pages)
            {
                var page = canvas.transform.Find(path);
                if (page == null) continue;

                var sides = page.Find(SidesName);
                if (sides == null)
                {
                    sides = MakeSides(page);
                    _sides.Add(sides.gameObject);
                }
                Cover(sides.Cast<RectTransform>(), whole);
            }
        }
        return _sides.Count;
    }

    private static Transform MakeSides(Transform page)
    {
        var made = new GameObject(SidesName) { layer = page.gameObject.layer };
        var box = made.AddComponent<RectTransform>();
        box.SetParent(page, false);
        box.SetAsFirstSibling();
        var black = made.AddComponent<Image>();
        black.color = Color.black;
        black.raycastTarget = true;
        return box;
    }

    /// <summary>The whole canvas, in the page's own space, with a little over at each edge.</summary>
    private static void Cover(RectTransform sides, RectTransform whole)
    {
        var page = sides.parent;
        var corners = new Il2CppStructArray<Vector3>(4);
        whole.GetWorldCorners(corners);
        Vector3 low = page.InverseTransformPoint(corners[0]), high = page.InverseTransformPoint(corners[2]);

        sides.anchorMin = sides.anchorMax = sides.pivot = new Vector2(0.5f, 0.5f);
        sides.sizeDelta = new Vector2(Mathf.Abs(high.x - low.x) + 4f, Mathf.Abs(high.y - low.y) + 4f);
        sides.localPosition = new Vector3((low.x + high.x) / 2f, (low.y + high.y) / 2f, 0f);
        sides.localScale = Vector3.one;
    }

    // ------------------------------------------------------------------ D: panels at the edges and across the screen

    /// <summary>The bars' pause screen's canvas, beside the match HUD's under the same holder.</summary>
    private const string PauseCanvas = "Canvas (2)";

    /// <summary>
    /// Liar's Poker's hand rankings, parked past the left edge with a strip showing and slid out by
    /// Tab. It hangs from the middle of the canvas at a distance that is the left edge's at 16:9, so
    /// a canvas that grows sideways leaves it half out all match: it is kept that far from the left
    /// edge instead, by its anchors, which its animator does not write (it moves only its place).
    /// </summary>
    private static readonly string[] Docked = { "TexasUI/TabMenu" };

    /// <summary>
    /// Its four showdown rows hang from the middle the same way, parked just past the 16:9 left edge
    /// and slid in to a banner centred under the shared cards. Kept from the edge they would leave
    /// the middle, so instead their holder is clipped to the middle 16:9 of the screen while wide: a
    /// parked row is out of sight as on a 16:9 screen, and a shown one stays centred.
    /// </summary>
    private const string Versus = "TexasUI/VersusMenu";

    /// <summary>The pause screen's fade, loading panel and credits: plain panels in a fixed 16:9 box.</summary>
    private static readonly string[] Wholes = { "Pause/Group/Fade", "Pause/Group/panel_loading", "Pause/Group/state_credits" };

    /// <summary>A rectangle as the game had it, to put back.</summary>
    private readonly struct Own
    {
        internal readonly Vector2 Min, Max, Pivot, Size, At;
        internal readonly Vector3 Scale;

        internal Own(RectTransform box)
        {
            Min = box.anchorMin;
            Max = box.anchorMax;
            Pivot = box.pivot;
            Size = box.sizeDelta;
            At = box.anchoredPosition;
            Scale = box.localScale;
        }

        internal void Back(RectTransform box)
        {
            box.anchorMin = Min;
            box.anchorMax = Max;
            box.pivot = Pivot;
            box.sizeDelta = Size;
            box.anchoredPosition = At;
            box.localScale = Scale;
        }
    }

    private static readonly Watch _panelWatch = new();
    private static readonly Dictionary<int, Own> _docked = new();   // by instance: what was moved to the edge
    private static readonly Dictionary<int, Own> _wholes = new();   // by instance: what was made the canvas's size
    private static readonly List<RectMask2D> _frames = new();
    private static bool _panelsOff;
    private static string _panelsSaid;

    /// <summary>From <see cref="ModTicker"/>.</summary>
    internal static void FixPanels()
    {
        if (_panelsOff) return;
        try { Panels(); }
        catch (Exception e)
        {
            _panelsOff = true;
            Plugin.Log.LogWarning($"[wide] Liar's Poker's edge menus and the pause panels are left as the game has them from now on: {e.Message}");
            try { PutPanelsBack(); } catch { }
        }
    }

    private static void Panels()
    {
        bool wide = Ultrawide.ScreenWide;
        bool holding = _docked.Count + _wholes.Count + _frames.Count > 0;
        if (!_panelWatch.Due(wide, holding) || Ranked()) return;

        if (!wide)
        {
            if (holding) PutPanelsBack();
            Say(ref _panelsSaid, holding ? "Liar's Poker's edge menus and the pause panels are back as the game has them"
                                         : "not wider than 16:9, no panel changed");
            return;
        }

        var seenDock = new HashSet<int>();
        var seenWhole = new HashSet<int>();
        var across = Vector2.zero;
        for (int i = _frames.Count - 1; i >= 0; i--)
            if (_frames[i] == null) _frames.RemoveAt(i);

        foreach (var canvas in RootCanvases())
        {
            var holder = canvas.transform.parent;
            if (holder == null || holder.name != HudHolder) continue;
            var whole = canvas.GetComponent<RectTransform>();

            if (canvas.name == PauseCanvas)
            {
                foreach (string path in Wholes)
                {
                    var box = Box(canvas.transform, path);
                    if (box == null) continue;
                    int id = box.GetInstanceID();
                    if (!_wholes.ContainsKey(id)) _wholes[id] = new Own(box);
                    Cover(box, whole);
                    across = box.sizeDelta;
                    seenWhole.Add(id);
                }
                continue;
            }

            float share = EdgeShare(canvas, out float sixteenNine);
            foreach (string path in Docked)
            {
                var box = Box(canvas.transform, path);
                if (box == null) continue;
                int id = box.GetInstanceID();
                if (!_docked.TryGetValue(id, out var own)) { own = new Own(box); _docked[id] = own; }
                var min = new Vector2(own.Min.x * share, own.Min.y);
                var max = new Vector2(own.Max.x * share, own.Max.y);
                if (box.anchorMin != min) box.anchorMin = min;
                if (box.anchorMax != max) box.anchorMax = max;
                seenDock.Add(id);
            }

            var versus = Box(canvas.transform, Versus);
            if (versus != null && share < 1f)
            {
                var frame = versus.GetComponent<RectMask2D>();
                if (frame == null)
                {
                    frame = versus.gameObject.AddComponent<RectMask2D>();
                    _frames.Add(frame);
                }
                if (_frames.Contains(frame))   // never a clip of the game's own
                {
                    float side = (sixteenNine / share - sixteenNine) / 2f;
                    var padding = new Vector4(side, 0f, side, 0f);
                    if (frame.padding != padding) frame.padding = padding;
                }
            }
        }
        Forget(_docked, seenDock);
        Forget(_wholes, seenWhole);
        Say(ref _panelsSaid, $"{seenDock.Count} of Liar's Poker's menus kept at the left edge, {_frames.Count} showdowns framed at 16:9, " +
                             $"{seenWhole.Count} pause panels across the screen" +
                             (seenWhole.Count > 0 ? $" at {across.x:0}x{across.y:0}" : ""));
    }

    private static RectTransform Box(Transform root, string path)
    {
        var at = root.Find(path);
        return at != null ? at.TryCast<RectTransform>() : null;
    }

    /// <summary>
    /// The part of the canvas's width that 16:9 gives it: an anchor that far across, times this, is
    /// as far from the left edge as at 16:9. Read from the canvas's own scaling and the screen, so it
    /// is right on the frame the size changes, before the canvas itself has caught up. Also the
    /// canvas's 16:9 width, in its own units.
    /// </summary>
    private static float EdgeShare(Canvas canvas, out float sixteenNine)
    {
        sixteenNine = 1920f;
        var scaler = canvas.GetComponent<CanvasScaler>();
        if (scaler == null || scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize) return 1f;
        var reference = scaler.referenceResolution;
        if (reference.x <= 0f || reference.y <= 0f || Screen.width <= 0 || Screen.height <= 0) return 1f;
        sixteenNine = reference.x;
        float follow = scaler.screenMatchMode == CanvasScaler.ScreenMatchMode.MatchWidthOrHeight ? scaler.matchWidthOrHeight : 0f;
        float scale = Mathf.Pow(2f, Mathf.Lerp(Mathf.Log(Screen.width / reference.x, 2f), Mathf.Log(Screen.height / reference.y, 2f), follow));
        float wide = Screen.width / scale;
        return wide > reference.x ? reference.x / wide : 1f;
    }

    private static void PutPanelsBack()
    {
        foreach (var found in Resources.FindObjectsOfTypeAll(Il2CppType.Of<RectTransform>()))
        {
            var box = found.TryCast<RectTransform>();
            if (box == null) continue;
            int id = box.GetInstanceID();
            if (_docked.TryGetValue(id, out var own) || _wholes.TryGetValue(id, out own)) own.Back(box);
        }
        _docked.Clear();
        _wholes.Clear();
        foreach (var frame in _frames) if (frame != null) UnityEngine.Object.Destroy(frame);
        _frames.Clear();
    }

    /// <summary>For the test driver: what this piece holds now.</summary>
    internal static string DescribePanels() =>
        $"{_docked.Count} menus kept at the left edge, {_frames.Count} showdowns framed at 16:9, {_wholes.Count} pause panels across the screen" +
        $"{(_panelsOff ? ", stopped" : "")}";

    // ------------------------------------------------------------------ shared

    private static bool Ranked() => SceneManager.GetActiveScene().name == RankedGuard.Scene;

    private static bool IsRoot(Transform at)
    {
        for (var up = at.parent; up != null; up = up.parent)
            if (up.GetComponent<Canvas>() != null) return false;
        return true;
    }

    /// <summary>The topmost canvas above a part, whether or not it is switched on.</summary>
    private static Canvas RootCanvas(Transform at)
    {
        Canvas top = null;
        for (var up = at; up != null; up = up.parent)
        {
            var canvas = up.GetComponent<Canvas>();
            if (canvas != null) top = canvas;
        }
        return top;
    }

    private static List<Canvas> RootCanvases()
    {
        var roots = new List<Canvas>();
        foreach (var found in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Canvas>()))
        {
            var canvas = found.TryCast<Canvas>();
            if (canvas != null && canvas.gameObject.scene.IsValid() && canvas.renderMode != RenderMode.WorldSpace && IsRoot(canvas.transform))
                roots.Add(canvas);
        }
        return roots;
    }

    private static Vector2 WorldSize(RectTransform box)
    {
        if (box == null) return Vector2.zero;
        var scale = box.lossyScale;
        return new Vector2(box.rect.width * scale.x, box.rect.height * scale.y);
    }

    private static void Forget<T>(Dictionary<int, T> held, HashSet<int> seen)
    {
        if (held.Count == seen.Count) return;
        var gone = new List<int>();
        foreach (int id in held.Keys) if (!seen.Contains(id)) gone.Add(id);
        foreach (int id in gone) held.Remove(id);
    }

    /// <summary>One line when what a piece does changes, or the scene does: the screen, the scene, what was done.</summary>
    private static void Say(ref string said, string what)
    {
        string line = $"{SceneManager.GetActiveScene().name} at {Screen.width}x{Screen.height}: {what}";
        if (line == said) return;
        said = line;
        Plugin.Log.LogInfo($"[wide] {line}");
    }

    /// <summary>For the test driver: what each piece holds now.</summary>
    internal static string Describe() =>
        $"screen {Screen.width}x{Screen.height} ({(Ultrawide.ScreenWide ? "wide" : "not wide")}): {_matched.Count} canvases follow the height, " +
        $"{_cropped.Count} videos cropped, {_shaped.Count} letterings kept in shape, {_sides.Count} pages with black sides" +
        $"{(_fitOff ? ", canvases stopped" : "")}{(_artOff ? ", pictures stopped" : "")}; {DescribePanels()}";
}
