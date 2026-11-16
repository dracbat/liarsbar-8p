using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Outfit pieces the game lists for a character but does not have, left out, so its outfit switch
/// stops throwing every frame.
///
/// Every character at a table carries the game's outfit switch (<c>SkinSetter</c>): a list of
/// outfits, each with the pieces it puts on, hats, glasses, coats. Every frame the switch takes
/// every outfit's pieces off and puts the chosen outfit's back on, one piece after another. In the
/// game's files (read from its 14 Nov 2026 build) Toar's body at Liar's Slots lists six outfits
/// whose pieces are all missing (Toar's other tables have them), and its switch is on. So it stops
/// at the first missing piece, every frame, on every copy at the table, for as long as anyone plays
/// Toar there: one error a frame in everybody's log, about 44,000 in a six-minute test, and the time
/// it takes to write each one. Dubai and Rasim carry the same gaps at every table, with their switch
/// off, so theirs never run; no other body has any.
///
/// So once per character, the first time this copy sees it at a table, the missing pieces are taken
/// out of each outfit's lists and nothing else is touched. An outfit left empty puts nothing on,
/// which is all a list of missing pieces could ever do. The outfits stay where they are in the list,
/// because a seat's outfit is chosen by its place there.
///
/// Only this copy's own objects change and nothing is sent, so it needs nobody's agreement, at any
/// table. At a table where nothing is missing it changes nothing.
/// </summary>
internal static class SkinGaps
{
    /// <summary>Once a second is soon enough: seats arrive in the first few seconds of a match.</summary>
    private const float LookEvery = 1f;

    private static float _next;
    private static int _match;
    private static readonly HashSet<int> _seen = new();

    /// <summary>From <see cref="ModTicker"/>.</summary>
    internal static void Tick()
    {
        float now = Time.unscaledTime;
        if (now < _next) return;
        _next = now + LookEvery;

        var m = Manager.Instance;
        if (m == null)
        {
            _match = 0;
            _seen.Clear();
            return;
        }

        int id = m.GetInstanceID();
        if (id != _match)
        {
            _match = id;
            _seen.Clear();
        }

        var setters = UnityEngine.Object.FindObjectsOfType<SkinSetter>();
        if (setters == null) return;
        for (int i = 0; i < setters.Count; i++)
        {
            var s = setters[i];
            if (s == null || !_seen.Add(s.GetInstanceID())) continue;
            try { Tidy(s); }
            catch (Exception e) { Plugin.Log.LogWarning($"[skins] could not look over a character's outfits: {e.Message}"); }
        }
    }

    /// <summary>One character's outfits, its missing pieces taken out. Said once for a character that had any.</summary>
    private static void Tidy(SkinSetter s)
    {
        var skins = s.skins;
        if (skins == null) return;

        int taken = 0, outfits = 0;
        for (int i = 0; i < skins.Count; i++)
        {
            var skin = skins[i];
            if (skin == null) continue;
            int here = Drop(skin.headMeshes) + Drop(skin.bodyMeshes);
            if (here > 0) outfits++;
            taken += here;
        }
        if (taken == 0) return;

        // The body's own name says which character and which table, never who plays it.
        string body = s.gameObject != null ? s.gameObject.name.Replace("(Clone)", "").Trim() : "a body";
        Plugin.Log.LogInfo($"[skins] {body}: {taken} missing outfit piece(s) in {outfits} outfit(s) left out " +
                           $"(its outfit switch {(s.enabled ? "is on, and stopped on them every frame" : "is off")})");
    }

    /// <summary>Take the missing entries out of one list of pieces; how many went.</summary>
    private static int Drop(Il2CppSystem.Collections.Generic.List<GameObject> pieces)
    {
        if (pieces == null) return 0;
        int gone = 0;
        for (int i = pieces.Count - 1; i >= 0; i--)
        {
            if (pieces[i] != null) continue;
            pieces.RemoveAt(i);
            gone++;
        }
        return gone;
    }
}
