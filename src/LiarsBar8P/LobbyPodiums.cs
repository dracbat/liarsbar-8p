using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Gives players beyond the fourth a podium in the pre-game lobby.
///
/// The lobby has four podiums (<c>LobbyController.SpawnSlots</c>), and the host's code
/// leans on that list in two places:
///
///     CmdSetPlayer:  slot = SpawnSlots.Where(s =&gt; !s.Dolu).First()    // throws once all four are taken
///     Update:        slot = SpawnSlots.First(s =&gt; s.name == SlotName)  // every frame until the player is marked Loaded
///
/// A fifth player throws in the first, so they get no podium, no lobby avatar and no
/// <c>SlotName</c>; and because the second runs every frame until it succeeds, they never
/// become Loaded either. That is the reported "player 5 spawns in game but is missing from
/// the lobby".
///
/// A podium is a <c>LobbySlot</c> - a NetworkBehaviour on a scene object - and more of
/// them cannot be spawned through Mirror: runtime spawning needs a build-time assetId a
/// scene object does not have, and duplicating a spawned identity corrupts spawn handling
/// and disconnects everyone (that happened, and cost several sessions).
///
/// What works instead: a podium copy with a *fresh, never-spawned* identity. Mirror
/// ignores it completely (sceneId 0, netId 0), but it is a real, functioning LobbySlot,
/// so every piece of game code that walks <c>SpawnSlots</c> or does
/// <c>GameObject.Find(SlotName)</c> is satisfied. Every peer builds the same copies with
/// the same names at the same positions, computed from the same scene.
///
/// The one thing Mirror will not do for a copy is sync its SyncVars (occupied, name,
/// ready). The host sets them through the game's own code; clients set them themselves
/// from what *is* synced - each player's <c>SlotName</c>, name and ready flag.
/// </summary>
internal static class LobbyPodiums
{
    /// <summary>Podium names this mod creates. Deterministic, so every peer agrees.</summary>
    internal const string Prefix = "Slot8P_";

    private static readonly Dictionary<int, LobbySlot> _podiums = new();
    private static float _nextSweep;
    private static bool _geometryFailed;
    private static bool _reported;

    // --------------------------------------------------------------------- build

    /// <summary>Build every extra podium as soon as the lobby exists, on host and clients alike.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.Start))]
    private static void OnLobbyStart(LobbyController __instance)
    {
        // A new lobby means a new scene: the previous copies are gone with it, and the
        // bar can be a different one with its podiums arranged differently. Nothing
        // learned about the last one should decide anything about this one.
        _podiums.Clear();
        _geometryFailed = false;
        _floorTest = 0;
        BuildAll(__instance);
    }

    private static void BuildAll(LobbyController lobby)
    {
        try
        {
            if (lobby == null || lobby.SpawnSlots == null) return;
            int built = 0;
            for (int i = Limits.VanillaPlayers; i < Limits.Max; i++)
                if (Ensure(i, lobby) != null) built++;

            if (!_reported && built > 0)
            {
                _reported = true;
                Plugin.Log.LogInfo($"[podium] lobby has {lobby.SpawnSlots.Count} podiums for up to {Limits.Max} players");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[podium] build failed: {e.Message}");
        }
    }

    /// <summary>The podium for this index, created and registered on first use. Null if it cannot be built.</summary>
    private static LobbySlot Ensure(int index, LobbyController lobby)
    {
        if (_podiums.TryGetValue(index, out var existing) && existing != null) return existing;
        if (_geometryFailed || lobby == null) return null;

        try
        {
            var slots = lobby.SpawnSlots;
            if (slots == null || slots.Count < 3) { _geometryFailed = true; return null; }

            // A copy this mod made earlier may already be in the list (the scene was kept but
            // the dictionary was not - host migration, for one). Adopt it rather than add twins.
            string name = Prefix + index;
            foreach (var s in slots)
                if (s != null && s.gameObject != null && s.gameObject.name == name)
                {
                    _podiums[index] = s;
                    return s;
                }

            var vanilla = new List<LobbySlot>();
            foreach (var s in slots)
                if (s != null && s.transform != null && !s.gameObject.name.StartsWith(Prefix)) vanilla.Add(s);
            if (vanilla.Count < 3) { _geometryFailed = true; return null; }

            if (!ArcSeat(vanilla, index, out Vector3 pos, out Quaternion rot))
            {
                _geometryFailed = true;
                Plugin.Log.LogWarning("[podium] could not work out where extra podiums go - " +
                                      "players beyond four will only be seated once the match starts");
                return null;
            }

            var clone = Cloning.CloneAsUnspawned(vanilla[vanilla.Count - 1].transform, name);
            if (clone == null) return null;

            clone.position = pos;
            clone.rotation = rot;

            var slot = clone.GetComponent<LobbySlot>();
            if (slot == null)
            {
                Plugin.Log.LogError("[podium] the copied podium lost its LobbySlot component");
                UnityEngine.Object.Destroy(clone.gameObject);
                return null;
            }

            // The template may have been occupied; the copy starts empty.
            slot.NetworkDolu = false;
            slot.NetworkPlayerName = "";
            slot.NetworkReady = false;

            GiveOwnPanel(slot, vanilla, index);

            slots.Add(slot);
            _podiums[index] = slot;
            Plugin.Log.LogInfo($"[podium] built {name} at {pos}");
            return slot;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[podium] could not build podium {index}: {e.Message}");
            _geometryFailed = true;
            return null;
        }
    }

    // ----------------------------------------------------------------- lobby UI

    /// <summary>
    /// Give a copied podium its own name plate.
    ///
    /// A podium has no children: its name text, ready icon, character panel and kick
    /// button live in the shared lobby canvas, one group per podium, and the LobbySlot
    /// only holds references to them. Copying the podium copies those references, so
    /// without this every copy would drive the *same* group as the podium it was copied
    /// from - four slots fighting over one name plate.
    ///
    /// So the canvas group is copied too, placed by continuing the spacing the existing
    /// groups use, and the copy's references are re-pointed into it. If the layout cannot
    /// be worked out, the references are cleared instead: an extra player with no name
    /// plate is a blemish, four players overwriting someone else's is a bug.
    /// </summary>
    private static void GiveOwnPanel(LobbySlot slot, List<LobbySlot> vanilla, int index)
    {
        try
        {
            var template = vanilla[vanilla.Count - 1];
            var templatePanel = template.NameText != null ? template.NameText.transform.parent : null;
            if (templatePanel == null) { ClearUi(slot); return; }

            var panel = UnityEngine.Object.Instantiate(templatePanel.gameObject, templatePanel.parent);
            panel.name = $"Panel8P_{index}";

            if (!PlacePanel(panel.transform, templatePanel, template.transform, slot.transform, vanilla, index))
            {
                UnityEngine.Object.Destroy(panel);
                ClearUi(slot);
                return;
            }

            slot.NameText     = Bind<TMPro.TextMeshProUGUI>(panel.transform, templatePanel, template.NameText != null ? template.NameText.transform : null);
            slot.CharNameText = Bind<TMPro.TextMeshProUGUI>(panel.transform, templatePanel, template.CharNameText != null ? template.CharNameText.transform : null);
            slot.SelectChar   = Find(panel.transform, templatePanel, template.SelectChar != null ? template.SelectChar.transform : null);
            slot.ReadyIcon    = Find(panel.transform, templatePanel, template.ReadyIcon != null ? template.ReadyIcon.transform : null);

            // The kick button's click handler still points at the podium it was copied
            // from, so pressing it would kick the wrong player. It is removed rather than
            // rewired - kicking from the original four panels still works.
            var kick = Find(panel.transform, templatePanel, template.KickB != null ? template.KickB.transform : null);
            if (kick != null) kick.SetActive(false);
            slot.KickB = null;

            Plugin.Log.LogInfo($"[podium] {slot.gameObject.name} has its own name plate ({panel.name})");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[podium] could not give podium {index} its own name plate: {e.Message}");
            ClearUi(slot);
        }
    }

    /// <summary>
    /// Put a copied name plate where the new podium is.
    ///
    /// The lobby's plates hang in the world next to the podium they belong to, so the
    /// right placement is the template plate's offset *from its own podium*, re-applied
    /// to the new one - which follows the curve of the podium arc instead of running off
    /// in a straight line. If the plates turn out to be flat screen overlays instead, the
    /// spacing between the existing ones is continued, which is the only thing that means
    /// anything there.
    /// </summary>
    private static bool PlacePanel(Transform panel, Transform templatePanel,
                                   Transform templatePodium, Transform newPodium,
                                   List<LobbySlot> vanilla, int index)
    {
        var canvas = panel.GetComponentInParent<Canvas>();
        bool world = canvas == null || canvas.renderMode == RenderMode.WorldSpace;

        if (world && templatePodium != null && newPodium != null)
        {
            var local = templatePodium.InverseTransformPoint(templatePanel.position);
            panel.position = newPodium.TransformPoint(local);
            panel.rotation = newPodium.rotation *
                             (Quaternion.Inverse(templatePodium.rotation) * templatePanel.rotation);
            Plugin.Log.LogInfo($"[podium] {panel.name} placed in the world at {panel.position}");
            return true;
        }

        var rects = new List<RectTransform>();
        foreach (var v in vanilla)
        {
            var p = v.NameText != null ? v.NameText.transform.parent : null;
            var rt = p != null ? p.TryCast<RectTransform>() : null;
            if (rt != null && rt.parent == templatePanel.parent) rects.Add(rt);
        }
        if (rects.Count < 2) return false;
        rects.Sort((a, b) => a.GetSiblingIndex().CompareTo(b.GetSiblingIndex()));

        Vector2 step = (rects[rects.Count - 1].anchoredPosition - rects[0].anchoredPosition) / (rects.Count - 1);
        if (step.sqrMagnitude < 0.01f) return false;

        var rect = panel.TryCast<RectTransform>();
        if (rect == null) return false;
        rect.anchoredPosition = rects[rects.Count - 1].anchoredPosition + step * (index - Limits.VanillaPlayers + 1);
        panel.SetSiblingIndex(rects[rects.Count - 1].GetSiblingIndex() + 1 + (index - Limits.VanillaPlayers));
        Plugin.Log.LogInfo($"[podium] {panel.name} placed on screen at {rect.anchoredPosition}");
        return true;
    }

    /// <summary>Leave a copy driving nothing rather than driving somebody else's plate.</summary>
    private static void ClearUi(LobbySlot slot)
    {
        try
        {
            slot.NameText = null;
            slot.CharNameText = null;
            slot.SelectChar = null;
            slot.ReadyIcon = null;
            slot.KickB = null;
            Plugin.Log.LogWarning($"[podium] {slot.gameObject.name} gets no name plate - " +
                                  "the lobby canvas is not laid out the way this expects");
        }
        catch { }
    }

    /// <summary>The matching object inside a copied group, found by its path in the original.</summary>
    private static GameObject Find(Transform copy, Transform originalRoot, Transform target)
    {
        if (copy == null || originalRoot == null || target == null) return null;
        var path = new List<string>();
        var cur = target;
        int guard = 0;
        while (cur != null && cur != originalRoot && guard++ < 32) { path.Insert(0, cur.name); cur = cur.parent; }
        if (cur != originalRoot) return null;                    // not inside the group
        if (path.Count == 0) return copy.gameObject;
        var found = copy.Find(string.Join("/", path));
        return found != null ? found.gameObject : null;
    }

    private static T Bind<T>(Transform copy, Transform originalRoot, Transform target) where T : Component
    {
        var go = Find(copy, originalRoot, target);
        return go != null ? go.GetComponent<T>() : null;
    }

    // ------------------------------------------------------------------ fallback

    /// <summary>
    /// Only reached if the game finds no free podium at all - which, with the copies in
    /// the list, means more players than the configured maximum. Rather than let the
    /// original throw and strand the player, give them a name and a place anyway.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(PlayerObjectController), nameof(PlayerObjectController.CmdSetPlayer))]
    private static bool Assign(PlayerObjectController __instance)
    {
        try
        {
            var lobby = LobbyController.Instance;
            if (lobby == null || lobby.SpawnSlots == null || __instance == null) return true;

            foreach (var s in lobby.SpawnSlots)
                if (s != null && !s.Dolu) return true;      // the game can seat them

            Plugin.Log.LogWarning(
                $"[podium] no podium left for '{__instance.PlayerName}' - {lobby.SpawnSlots.Count} podiums, " +
                $"maximum {Limits.Max}; placing them at the last one");

            if (__instance.isOwned) __instance.NetworkReady = true;
            var last = lobby.SpawnSlots[lobby.SpawnSlots.Count - 1];
            if (last != null)
            {
                __instance.NetworkSlotName = last.gameObject.name;
                Place(__instance.transform, last.transform);
            }
            return false;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[podium] fallback failed: {e.Message}");
            return true;
        }
    }

    // -------------------------------------------------------------------- sweep

    /// <summary>
    /// Keeps the copies' state right on machines Mirror does not update.
    ///
    /// On the host the game's own code marks a copy occupied and names it, exactly as
    /// it does the originals. Those values are SyncVars, and a copy is never synced, so
    /// on every client the copy would stay blank. Each client therefore fills its copies
    /// in from what *is* synced on each player - their SlotName, name and ready flag -
    /// and clears any copy whose player has gone.
    ///
    /// Positions are covered too: the host places a body on its podium when it assigns
    /// it, but only a client with the podium at the same spot can show that. Bodies are
    /// nudged onto the local copy only when visibly away from it, so this never fights
    /// the game's own movement sync.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.Update))]
    private static void Sweep(LobbyController __instance)
    {
        try
        {
            if (Time.time < _nextSweep) return;
            _nextSweep = Time.time + 0.5f;
            if (__instance == null) return;

            if (_podiums.Count == 0) BuildAll(__instance);
            if (_podiums.Count == 0) return;

            var nm = UnityEngine.Object.FindObjectOfType<CustomNetworkManager>();
            if (nm == null || nm.GamePlayers == null) return;

            bool isHost;
            try { isHost = Mirror.NetworkServer.active; }
            catch { isHost = false; }

            // The game writes "<sprite=N>Name" on the originals when skins are shown.
            // Copy whichever form the originals are using right now.
            bool spriteNames = false;
            foreach (var s in __instance.SpawnSlots)
            {
                if (s == null || s.gameObject.name.StartsWith(Prefix)) continue;
                var n = s.PlayerName;
                if (!string.IsNullOrEmpty(n) && n.StartsWith("<sprite=")) { spriteNames = true; break; }
            }

            foreach (var kv in _podiums)
            {
                var slot = kv.Value;
                if (slot == null) continue;
                string name = Prefix + kv.Key;

                PlayerObjectController occupant = null;
                foreach (var p in nm.GamePlayers)
                    if (p != null && p.SlotName == name) { occupant = p; break; }

                if (occupant == null)
                {
                    // The host clears its own copies through the game's leave handling.
                    if (!isHost && slot.Dolu)
                    {
                        slot.NetworkDolu = false;
                        slot.NetworkPlayerName = "";
                        slot.NetworkReady = false;
                    }
                    continue;
                }

                if (!isHost)
                {
                    string shown = spriteNames ? $"<sprite={occupant.PlayerSkin}>{occupant.PlayerName}" : occupant.PlayerName;
                    if (!slot.Dolu) slot.NetworkDolu = true;
                    if (slot.PlayerName != shown) slot.NetworkPlayerName = shown;
                    if (slot.Ready != occupant.Ready) slot.NetworkReady = occupant.Ready;
                }

                var body = occupant.transform;
                if (body != null && slot.transform != null &&
                    Vector3.Distance(body.position, slot.transform.position) > 0.5f)
                    Place(body, slot.transform);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[podium] sweep failed: {e.Message}");
            _nextSweep = Time.time + 30f;   // a broken frame must not be repeated twice a second
        }
    }

    private static void Place(Transform body, Transform podium)
    {
        if (body == null || podium == null) return;
        body.position = podium.position;
        body.rotation = podium.rotation;
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>
    /// Continue the arc the shipped podiums stand on.
    ///
    /// A circle is fitted to the originals and their angles are unwrapped before
    /// sorting - angles wrap at 180 degrees, and sorting the raw values once turned a 22
    /// degree step into 113 and scattered the podiums across the room. New podiums carry
    /// on past the last one at the same spacing, so the originals never move.
    ///
    /// Facing is measured rather than assumed: the angle between each original's forward
    /// and its direction to the centre is averaged and reused, which is right whether the
    /// podiums face inward or all face the camera.
    /// </summary>
    private static bool ArcSeat(List<LobbySlot> vanilla, int index, out Vector3 pos, out Quaternion rot)
    {
        pos = Vector3.zero;
        rot = Quaternion.identity;

        var points = new List<Vector3>();
        foreach (var s in vanilla) points.Add(s.transform.position);
        if (points.Count < 3) return false;

        var flat = new Vector2[points.Count];
        for (int i = 0; i < points.Count; i++) flat[i] = new Vector2(points[i].x, points[i].z);

        Geometry.FitCircle(flat, out Vector2 centre, out float radius);
        if (radius <= 0.01f || float.IsNaN(radius) || float.IsInfinity(radius)) return false;

        var angles = new List<float>();
        foreach (var f in flat)
        {
            float a = Mathf.Atan2(f.y - centre.y, f.x - centre.x) * Mathf.Rad2Deg;
            angles.Add((a % 360f + 360f) % 360f);
        }
        angles.Sort();

        // Start the run just after the widest gap so it never straddles the wrap.
        int startAt = 0;
        float widest = -1f;
        for (int i = 0; i < angles.Count; i++)
        {
            float gap = angles[(i + 1) % angles.Count] - angles[i];
            if (gap < 0) gap += 360f;
            if (gap > widest) { widest = gap; startAt = (i + 1) % angles.Count; }
        }

        var run = new List<float>();
        float previous = angles[startAt];
        run.Add(previous);
        for (int k = 1; k < angles.Count; k++)
        {
            float a = angles[(startAt + k) % angles.Count];
            while (a < previous) a += 360f;
            run.Add(a);
            previous = a;
        }

        float step = (run[run.Count - 1] - run[0]) / (run.Count - 1);
        if (Mathf.Abs(step) < 0.5f) return false;

        float facingOffset = 0f;
        int counted = 0;
        foreach (var s in vanilla)
        {
            Vector3 inward = new Vector3(centre.x - s.transform.position.x, 0f, centre.y - s.transform.position.z);
            if (inward.sqrMagnitude < 1e-4f) continue;
            facingOffset += Vector3.SignedAngle(inward.normalized, Flat(s.transform.forward), Vector3.up);
            counted++;
        }
        if (counted > 0) facingOffset /= counted;

        float height = 0f;
        foreach (var p in points) height += p.y;
        height /= points.Count;

        // Extra podiums are added at alternating ends of the arc rather than all beyond
        // one end: the same eight podiums then occupy half the extra room, which matters
        // because what lies past the shipped arc is unknown. The first extra goes after
        // the last shipped podium, the second before the first, and so on.
        int n = index - Limits.VanillaPlayers;
        int out_ = n / 2 + 1;
        bool after = (n % 2) == 0;
        float first = after ? run[run.Count - 1] + step * out_ : run[0] - step * out_;
        float second = after ? run[0] - step * out_ : run[run.Count - 1] + step * out_;

        // Never let the arc wrap round onto itself.
        if (Mathf.Abs((run[run.Count - 1] - run[0]) + step * 2 * out_) > 350f) return false;

        // Each candidate is checked for floor underneath, with the check calibrated on the
        // podiums the game shipped with: if those do not register floor either, the lobby
        // has no colliders to test against and the check is skipped rather than trusted.
        bool checkFloor = FloorTestWorks(points);

        foreach (float candidate in new[] { first, second })
        {
            float rad = candidate * Mathf.Deg2Rad;
            var at = new Vector3(centre.x + Mathf.Cos(rad) * radius, height, centre.y + Mathf.Sin(rad) * radius);
            if (checkFloor && !HasFloor(at))
            {
                Plugin.Log.LogInfo($"[podium] no floor at {at} - trying the other end of the arc");
                continue;
            }

            Vector3 inward = new Vector3(centre.x - at.x, 0f, centre.y - at.z);
            if (inward.sqrMagnitude < 1e-4f) continue;

            pos = at;
            rot = Quaternion.LookRotation(Quaternion.AngleAxis(facingOffset, Vector3.up) * inward.normalized, Vector3.up);
            return true;
        }

        return false;
    }

    private static int _floorTest;   // 0 unknown, 1 usable, -1 not

    /// <summary>Would the floor check pass for podiums that are definitely standing on floor?</summary>
    private static bool FloorTestWorks(List<Vector3> known)
    {
        if (_floorTest != 0) return _floorTest > 0;

        int ok = 0;
        foreach (var p in known) if (HasFloor(p)) ok++;
        _floorTest = ok == known.Count ? 1 : -1;
        if (_floorTest < 0)
            Plugin.Log.LogInfo($"[podium] floor check reaches only {ok}/{known.Count} of the shipped " +
                               "podiums - the lobby has nothing to test against, so it is not used");
        return _floorTest > 0;
    }

    /// <summary>Is there ground directly under this spot, at about the podium's own height?</summary>
    private static bool HasFloor(Vector3 pos)
    {
        try
        {
            if (!Physics.Raycast(pos + Vector3.up * 2f, Vector3.down, out RaycastHit hit, 6f)) return false;
            return Mathf.Abs(hit.point.y - pos.y) < 1.5f;
        }
        catch { return false; }
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude < 1e-6f ? Vector3.forward : v.normalized;
    }
}
