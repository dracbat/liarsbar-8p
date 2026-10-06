using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace LiarsBar8P;

/// <summary>
/// Your own character in the lobby lights up under the mouse and changes when clicked, once
/// five or more are in it - the way it does with four.
///
/// The game's select target - left click for the next character, right click for the one
/// before, and the outline that tells you it is there - is part of your name plate group in
/// the lobby canvas. In the shipped four-player shot that group hangs over your character.
/// Once a fifth player joins, <see cref="LobbyPodiums"/> pulls the camera back and lifts every
/// name plate above its player's head so the names can still be read, and the select target
/// goes up with it. Measured with five copies of the game: a click on your own character then
/// had nothing of the game's under it at all. Players found the spot by hunting around above
/// the front row, and a player on one of the extra podiums reported not being able to change
/// character at all.
///
/// So while the wider shot is held, the mouse over your own character does what it does with
/// four: the outline comes on (the game's own <c>ShowOutline</c>), and a click is the game's
/// own click - the same <c>SetSkin</c> / <c>SetSkinBack</c> the select target calls, with the
/// same sound. It only ever affects your own character, because all of those act on the local
/// player whatever the mouse is over. With four or fewer the shot is the game's own and this
/// does nothing.
///
/// The game's own select target is switched off for as long as the wider shot is held. Left
/// on, it still answered the mouse from up by the name plate - lighting the character and
/// changing it from a spot over somebody else's head - so there were two places to click, one
/// of them nowhere near the character. It stops answering the mouse and nothing else: it is
/// still drawn, and it comes back the moment the lobby is down to four.
///
/// A real control under the cursor (a button, the mode arrows, the chat box) keeps its click;
/// this stands aside for it.
/// </summary>
internal static class LobbySkinClick
{
    /// <summary>What the game names the local player's lobby object.</summary>
    private const string LocalPlayer = "LocalGamePlayer";

    /// <summary>The box a character fills on its podium, in metres: feet to head, and across.</summary>
    private const float Height = 1.9f;
    private const float HalfWidth = 0.45f;

    private static GameObject _local;
    private static bool _lit;
    private static bool _reported;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.Update))]
    private static void Tick(LobbyController __instance)
    {
        try
        {
            if (__instance == null) return;

            if (!LobbyPodiums.PulledBack)
            {
                // Back to four: the game's own target is over the character again.
                if (_lit) Light(__instance, false, Vector2.zero);
                Mute(__instance, false);
                return;
            }

            Mute(__instance, true);

            var cam = LobbyPodiums.HeldCamera;
            if (_local == null) _local = GameObject.Find(LocalPlayer);
            if (cam == null || _local == null) return;

            Vector2 mouse = Input.mousePosition;
            bool onBody = OnBody(cam, _local.transform.position, mouse);
            Light(__instance, onBody, mouse);

            bool next = Input.GetMouseButtonDown(0);
            bool back = Input.GetMouseButtonDown(1);
            if (!onBody || (!next && !back)) return;

            var top = TopHit(mouse);
            if (IsSelectTarget(top) || IsControl(top)) return;

            var player = _local.GetComponent<PlayerObjectController>();
            if (player == null) return;

            var sound = UnityEngine.Object.FindObjectOfType<SkinChangerLobby>();
            if (sound != null && sound.audioSource != null) sound.audioSource.Play();

            if (next) player.SetSkin();
            else player.SetSkinBack();

            if (!_reported)
            {
                _reported = true;
                Plugin.Log.LogInfo("[skinclick] changed character from a click on it - with five or more " +
                                   "in the lobby the game's own select target is up with the name plate");
            }
        }
        catch (Exception e) { Plugin.Log.LogError($"[skinclick] {e.Message}"); }
    }

    /// <summary>
    /// Switch the outline on or off when the mouse moves on to or off the character - only on
    /// the change, because the game does it by walking the whole character and re-layering it.
    /// Coming off the character on to the game's own select target leaves the outline alone:
    /// the game lit it for that target and will put it out itself.
    /// </summary>
    private static void Light(LobbyController lobby, bool on, Vector2 mouse)
    {
        if (on == _lit) return;

        LobbySlot any = null;
        if (lobby.SpawnSlots != null)
            foreach (var s in lobby.SpawnSlots)
                if (s != null) { any = s; break; }
        if (any == null) return;

        _lit = on;
        if (on) any.ShowOutline();
        else if (!IsSelectTarget(TopHit(mouse))) any.HideOutline();
    }

    /// <summary>The select targets switched off here, and how to put each back.</summary>
    private static readonly System.Collections.Generic.Dictionary<int, (CanvasGroup Group, bool Added, bool Was)> _muted = new();
    private static float _nextMute;

    /// <summary>
    /// Stop the game's own select targets answering the mouse, or let them again.
    ///
    /// A canvas group that does not block raycasts takes a target and everything under it out
    /// of the pointer's reach - its click and its hover outline both - while leaving it drawn
    /// exactly as before. The group is added only where the target has none of its own, and
    /// taken away again afterwards; one that was already there gets its own setting back.
    /// Checked twice a second rather than once, because the extra podiums' targets appear as
    /// their podiums are built.
    /// </summary>
    private static void Mute(LobbyController lobby, bool mute)
    {
        if (!mute)
        {
            if (_muted.Count == 0) return;
            foreach (var kv in _muted)
            {
                var g = kv.Value.Group;
                if (g == null) continue;   // went with the lobby it belonged to
                if (kv.Value.Added) UnityEngine.Object.Destroy(g);
                else g.blocksRaycasts = kv.Value.Was;
            }
            _muted.Clear();
            return;
        }

        if (Time.unscaledTime < _nextMute || lobby.SpawnSlots == null) return;
        _nextMute = Time.unscaledTime + 0.5f;

        foreach (var s in lobby.SpawnSlots)
        {
            var target = s != null ? s.SelectChar : null;
            if (target == null) continue;

            int id = target.GetInstanceID();
            if (_muted.TryGetValue(id, out var held))
            {
                if (held.Group != null && held.Group.blocksRaycasts) held.Group.blocksRaycasts = false;
                continue;
            }

            var group = target.GetComponent<CanvasGroup>();
            bool added = group == null;
            if (added) group = target.AddComponent<CanvasGroup>();
            _muted[id] = (group, added, added || group.blocksRaycasts);
            group.blocksRaycasts = false;
        }
    }

    /// <summary>
    /// Is the cursor over the character standing at <paramref name="feet"/>, as the camera
    /// sees it? The box is the space a character takes up on a podium, projected to the
    /// screen, which is generous on purpose - it is the player's own character, and missing
    /// it was the complaint.
    /// </summary>
    private static bool OnBody(Camera cam, Vector3 feet, Vector2 mouse)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        int seen = 0;

        for (int i = 0; i < 8; i++)
        {
            var corner = feet + new Vector3(
                (i & 1) == 0 ? -HalfWidth : HalfWidth,
                (i & 2) == 0 ? 0f : Height,
                (i & 4) == 0 ? -HalfWidth : HalfWidth);

            var p = cam.WorldToScreenPoint(corner);
            if (p.z <= 0f) continue;   // behind the camera

            seen++;
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
        }

        return seen > 0 && mouse.x >= minX && mouse.x <= maxX && mouse.y >= minY && mouse.y <= maxY;
    }

    /// <summary>The first thing the game's own pointer events would reach at this point, if any.</summary>
    private static GameObject TopHit(Vector2 mouse)
    {
        var events = EventSystem.current;
        if (events == null) return null;

        var pointer = new PointerEventData(events) { position = mouse };
        var hits = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
        events.RaycastAll(pointer, hits);
        return hits.Count > 0 ? hits[0].gameObject : null;
    }

    private static bool IsSelectTarget(GameObject go) =>
        go != null && go.GetComponentInParent<SkinChangerLobby>() != null;

    /// <summary>A button, the mode arrows, the chat box - anything that owns its own clicks.</summary>
    private static bool IsControl(GameObject go) =>
        go != null && go.GetComponentInParent<UnityEngine.UI.Selectable>() != null;
}
