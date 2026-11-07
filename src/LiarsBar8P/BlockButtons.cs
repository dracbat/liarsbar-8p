using System;
using System.Text;
using HarmonyLib;
using Mirror;
using UnityEngine;
using UnityEngine.EventSystems;

namespace LiarsBar8P;

/// <summary>
/// A block button on every name plate in the lobby, and the host's Kick back on podiums five to
/// eight.
///
/// <b>Block.</b> A copy of the plate's own kick cross, in the warning colour so it is never taken
/// for the host's cream one, just right of the player's name. Everyone gets it, on every plate but
/// their own and the bots', on the four podiums the game has and on the mod's extra ones. The
/// cross beside a name is easy to hit by accident, so it takes two clicks: the first turns it
/// gold and says in the chat, on this screen only, to click again; the second, within three
/// seconds, blocks. On a blocked player's plate it stays gold, and one click unblocks. The words
/// go in the game's own chat, locally, with the name as plain text.
///
/// Blocking somebody at a table you host also sends them away, with the game's own kick. The rest
/// is <see cref="BlockList"/>'s: they cannot join your lobbies, and you do not see theirs.
///
/// <b>Whose id.</b> A block is only as good as the Steam id it stores. The host asks its transport
/// who is on the other end of the player's connection, as the refusal at the door does, so the id
/// it keeps always belongs to the connection it sends away. A client only has the id the host put
/// on the player, and a host on a build before this release fills those by position, so somebody
/// who arrived out of order wears another member's (see <see cref="SteamIdFix"/>). An invite or a
/// typed lobby ID still seats a player at such a host, and the table still reads as modded, so a
/// client shows the crosses only once the host has answered on the mod's channel: that came in
/// the same release as the fix. The answer is asked rather than the version string, because a
/// build in development still carries the last release's number.
///
/// <b>Kick.</b> The game shows the host a kick cross on its four podiums. The mod's extra podiums
/// are copies with no network identity, so the game never shows theirs, and the copy kept on them
/// still points at the podium it was copied from. A clean copy goes there instead, for the host,
/// and sends the player standing there away the same way, or a bot away as REMOVE BOT does.
///
/// Both go out through the first podium, which is a real network object: the call only needs one
/// to travel on. Only at a table where everyone runs the mod, never near ranked.
/// </summary>
internal static class BlockButtons
{
    // Every word a player sees from this class. {0} is the player's name, as plain text.
    private const string Again = "Click the cross again to block {0}";
    private const string Done = "Blocked {0}. You won't see their lobbies, and they can't join yours";
    private const string Undone = "Unblocked {0}";

    private const string BlockName = "LB8P_Block";
    private const string KickName = "LB8P_Kick";

    /// <summary>How long the first click waits for the second.</summary>
    private const float ArmSeconds = 3f;

    /// <summary>The gap between the end of a name and the cross, in the plate's own units.</summary>
    private const float Gap = 8f;

    private static string _armedSlot;
    private static ulong _armedId;
    private static float _armedUntil;

    [HarmonyPatch]
    internal static class Follow
    {
        /// <summary>A new lobby screen starts with nothing armed.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.Start))]
        private static void Started()
        {
            _armedSlot = null;
            _armedId = 0;
        }
    }

    // ------------------------------------------------------------------ from ModTicker

    /// <summary>Four times a second: each plate's buttons, made when missing, shown or hidden, coloured.</summary>
    internal static void Tick()
    {
        var room = LobbyController.Instance;
        if (room == null || room.SpawnSlots == null) return;

        var nm = NetworkManager.singleton != null ? NetworkManager.singleton.TryCast<CustomNetworkManager>() : null;
        bool show = !RankedGuard.Near && Table.Modded && Manager.Instance == null &&
                    nm != null && !nm.Matchmaking && !nm.isVelvetRoom && nm.GamePlayers != null;
        bool host = show && NetworkServer.active;

        var template = Template(room, out var kickAt, out bool fromTick);
        if (template == null) return;

        foreach (var slot in room.SpawnSlots)
        {
            if (slot == null || slot.NameText == null) continue;
            var plate = slot.NameText.transform.parent;
            if (plate == null) continue;

            string slotName = slot.gameObject.name;
            var occupant = show ? Occupant(nm, slotName) : null;
            ulong id = occupant != null && !occupant.isOwned && !BotManager.IsBot(occupant) ? SteamIdOf(occupant) : 0;
            bool person = id != 0;

            var block = plate.Find(BlockName);
            if (block == null && person) block = Make(template, plate, BlockName, () => Clicked(slotName));
            if (block != null)
            {
                if (block.gameObject.activeSelf != person) block.gameObject.SetActive(person);
                if (person) Dress(block, slot.NameText, id, slotName);
            }

            // The game's own Kick covers its four podiums. Only the mod's copies need one.
            if (!slotName.StartsWith(LobbyPodiums.Prefix, StringComparison.Ordinal)) continue;

            bool kickable = host && occupant != null && !occupant.isOwned;
            var kick = plate.Find(KickName);
            if (kick == null && kickable)
            {
                kick = Make(template, plate, KickName, () => Kick(slotName));
                var ready = fromTick && slot.ReadyIcon != null ? slot.ReadyIcon.GetComponent<RectTransform>() : null;
                if (kick != null)
                    kick.GetComponent<RectTransform>().anchoredPosition = ready != null ? ready.anchoredPosition + kickAt : kickAt;
            }
            if (kick != null && kick.gameObject.activeSelf != kickable) kick.gameObject.SetActive(kickable);
        }
    }

    /// <summary>
    /// The kick cross to copy: the first of the game's own podiums that has one (the first podium
    /// has none). Clones of it carry nothing of its click. <paramref name="kickAt"/> is where it
    /// sits from that plate's ready tick, which is where it goes beside each extra plate's own: the
    /// last of the four, which the extra plates are copies of, has its cross half over the tick.
    /// </summary>
    private static UnityEngine.UI.Button Template(LobbyController room, out Vector2 kickAt, out bool fromTick)
    {
        kickAt = default;
        fromTick = false;
        foreach (var slot in room.SpawnSlots)
        {
            if (slot == null || slot.KickB == null || slot.gameObject.name.StartsWith(LobbyPodiums.Prefix, StringComparison.Ordinal)) continue;

            var at = slot.KickB.GetComponent<RectTransform>().anchoredPosition;
            var ready = slot.ReadyIcon != null ? slot.ReadyIcon.GetComponent<RectTransform>() : null;
            fromTick = ready != null;
            kickAt = fromTick ? at - ready.anchoredPosition : at;
            return slot.KickB;
        }
        return null;
    }

    private static Transform Make(UnityEngine.UI.Button template, Transform plate, string name, Action click)
    {
        var copy = UiClone.Make(template.gameObject, plate, name, click);
        if (copy == null) return null;
        copy.SetActive(false);
        return copy.transform;
    }

    /// <summary>
    /// Right after the name as it is drawn, level with it, in the warning colour, or gold while
    /// armed or blocked. Placed from the drawn text's own bounds, not its box: the box is wider
    /// than most names, and the text does not sit in its middle. The name is scaled within its
    /// plate, so the end of the text is carried into the plate's own space before the cross is set.
    /// </summary>
    private static void Dress(Transform block, TMPro.TextMeshProUGUI name, ulong id, string slotName)
    {
        var box = block.GetComponent<RectTransform>();
        var words = name.rectTransform;
        box.pivot = new Vector2(0.5f, 0.5f);

        var drawn = name.textBounds;
        var rect = words.rect;
        var end = drawn.size.x > 0f ? new Vector3(drawn.max.x, drawn.center.y, 0f)
                                    : new Vector3(rect.xMax, rect.center.y, 0f);
        var at = block.parent.InverseTransformPoint(words.TransformPoint(end));
        float half = box.rect.width * 0.5f * box.localScale.x;
        box.localPosition = new Vector3(at.x + Gap + half, at.y, at.z);

        bool gold = BlockList.IsBlocked(id) || Armed(slotName, id);
        var image = block.GetComponent<UnityEngine.UI.Image>();
        var want = gold ? ModCanvas.Gold : ModCanvas.Warning;
        if (image != null && image.color != want) image.color = want;
    }

    private static bool Armed(string slotName, ulong id) =>
        _armedSlot == slotName && _armedId == id && Time.unscaledTime < _armedUntil;

    private static PlayerObjectController Occupant(CustomNetworkManager nm, string slotName)
    {
        foreach (var p in nm.GamePlayers)
            if (p != null && p.SlotName == slotName) return p;
        return null;
    }

    /// <summary>
    /// The Steam id a block on this player stores, or 0 when this copy cannot be sure of it, and
    /// then there is no cross. See "Whose id" above.
    /// </summary>
    private static ulong SteamIdOf(PlayerObjectController p)
    {
        if (NetworkServer.active) return BlockList.IdOf(p.connectionToClient);
        return ModNet.HostLevel > 0 ? p.PlayerSteamID : 0;
    }

    // ------------------------------------------------------------------ the clicks

    private static void Clicked(string slotName)
    {
        try
        {
            var nm = NetworkManager.singleton != null ? NetworkManager.singleton.TryCast<CustomNetworkManager>() : null;
            if (nm == null || nm.GamePlayers == null || RankedGuard.Near || !Table.Modded) return;

            var p = Occupant(nm, slotName);
            if (p == null || p.isOwned || BotManager.IsBot(p)) return;

            ulong id = SteamIdOf(p);
            if (id == 0) return;
            string name = Matchmaking.Clean(p.PlayerName, 24);
            string seat = SeatOf(slotName);

            if (BlockList.IsBlocked(id))
            {
                BlockList.Remove(id);
                _armedSlot = null;
                Say(Words.F(Undone, name));
                Plugin.Log.LogInfo($"[block] unblocked the player on {seat}");
                return;
            }

            if (!Armed(slotName, id))
            {
                _armedSlot = slotName;
                _armedId = id;
                _armedUntil = Time.unscaledTime + ArmSeconds;
                Say(Words.F(Again, name));
                Plugin.Log.LogInfo($"[block] first click on {seat} - waiting {ArmSeconds:0} s for the second");
                return;
            }

            _armedSlot = null;
            if (!BlockList.Add(id)) return;
            Say(Words.F(Done, name));
            Plugin.Log.LogInfo($"[block] second click on {seat} - blocked");

            if (NetworkServer.active) SendAway(p, "blocked");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[block] the block button failed: {e.Message}"); }
    }

    private static void Kick(string slotName)
    {
        try
        {
            var nm = NetworkManager.singleton != null ? NetworkManager.singleton.TryCast<CustomNetworkManager>() : null;
            if (nm == null || nm.GamePlayers == null || !NetworkServer.active || RankedGuard.Near) return;

            var p = Occupant(nm, slotName);
            if (p == null || p.isOwned) return;
            if (BotManager.IsBot(p)) { BotManager.Remove(p, "the host's Kick"); return; }
            SendAway(p, "kicked");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[block] the kick button failed: {e.Message}"); }
    }

    /// <summary>The game's own kick: they leave the Steam lobby and their connection, and land at the menu.</summary>
    private static void SendAway(PlayerObjectController p, string why)
    {
        var room = LobbyController.Instance;
        var conn = p.connectionToClient;
        if (room == null || room.SpawnSlots == null || room.SpawnSlots.Count == 0 || conn == null) return;

        room.SpawnSlots[0].KickRPC(conn);
        Plugin.Log.LogInfo($"[block] connection {conn.connectionId} {why} and sent back to the menu");
    }

    /// <summary>On this screen only: the game's chat panel, which nobody else sees this in.</summary>
    private static void Say(string line)
    {
        try
        {
            var chat = ChatArayuz.instance;
            if (chat != null) chat.AddMessage(line);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[block] could not say it in the chat: {e.Message}"); }
    }

    /// <summary>A podium by its number, for the log, never a name.</summary>
    private static string SeatOf(string slotName)
    {
        var room = LobbyController.Instance;
        if (room != null && room.SpawnSlots != null)
            for (int i = 0; i < room.SpawnSlots.Count; i++)
                if (room.SpawnSlots[i] != null && room.SpawnSlots[i].gameObject.name == slotName) return $"podium {i + 1}";
        return "a podium";
    }

    // ------------------------------------------------------------------ for the test driver

    /// <summary>Press a podium's block button through its own click, as the mouse would. Podiums count from 1.</summary>
    internal static string Press(int podium, bool kick)
    {
        var plate = Plate(podium);
        if (plate == null) return "no such podium";
        var button = plate.Find(kick ? KickName : BlockName);
        if (button == null || !button.gameObject.activeInHierarchy) return "no button showing there";
        button.GetComponent<UnityEngine.UI.Button>().onClick.Invoke();
        return "pressed";
    }

    private static Transform Plate(int podium)
    {
        var room = LobbyController.Instance;
        if (room == null || room.SpawnSlots == null || podium < 1 || podium > room.SpawnSlots.Count) return null;
        var slot = room.SpawnSlots[podium - 1];
        return slot != null && slot.NameText != null ? slot.NameText.transform.parent : null;
    }

    /// <summary>Each podium: who stands there (seat kind, never a name), and the buttons showing and their colour.</summary>
    internal static string Describe()
    {
        var room = LobbyController.Instance;
        if (room == null || room.SpawnSlots == null) return "not in a lobby";
        var nm = NetworkManager.singleton != null ? NetworkManager.singleton.TryCast<CustomNetworkManager>() : null;

        var sb = new StringBuilder();
        string ids = NetworkServer.active ? "from the connections" : ModNet.HostLevel > 0 ? "from the host" : "not trusted, no answer on the channel";
        sb.Append($"{BlockList.Count} blocked, ids {ids};");
        for (int i = 0; i < room.SpawnSlots.Count; i++)
        {
            var plate = Plate(i + 1);
            if (plate == null) continue;
            var p = nm != null && nm.GamePlayers != null ? Occupant(nm, room.SpawnSlots[i].gameObject.name) : null;
            string who = p == null ? "empty" : p.isOwned ? "you" : BotManager.IsBot(p) ? "bot" :
                         BlockList.IsBlocked(SteamIdOf(p)) ? $"connection {p.ConnectionID}, blocked" : $"connection {p.ConnectionID}";
            var block = plate.Find(BlockName);
            var kick = plate.Find(KickName);
            string colour = "";
            if (block != null && block.gameObject.activeSelf)
            {
                var image = block.GetComponent<UnityEngine.UI.Image>();
                colour = image != null && image.color == ModCanvas.Gold ? " gold" : " red";
            }
            sb.Append($" {i + 1}: {who}, block {(block != null && block.gameObject.activeSelf ? "on" + colour : "off")}" +
                      $"{(kick != null && kick.gameObject.activeSelf ? ", kick on" : "")};");
        }
        return sb.ToString();
    }

    /// <summary>One plate's geometry in its own units and on screen: the name, its drawn text, the ready tick, the kick and the block cross.</summary>
    internal static string Geometry(int podium)
    {
        var plate = Plate(podium);
        if (plate == null) return "no such podium";
        var slot = LobbyController.Instance.SpawnSlots[podium - 1];
        var canvas = plate.GetComponentInParent<Canvas>();
        var cam = canvas != null && canvas.worldCamera != null ? canvas.worldCamera : Camera.main;

        string Of(Transform t)
        {
            if (t == null) return "none";
            var r = t.TryCast<RectTransform>();
            var at = plate.InverseTransformPoint(t.position);
            var screen = cam != null ? cam.WorldToScreenPoint(t.position) : Vector3.zero;
            string size = r != null ? $" size {r.rect.width:0.#}x{r.rect.height:0.#} pivot {r.pivot.x:0.##},{r.pivot.y:0.##}" : "";
            var kids = new StringBuilder();
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i).TryCast<RectTransform>();
                if (c != null) kids.Append($" [{c.name} at {c.localPosition.x:0.#},{c.localPosition.y:0.#} size {c.rect.width:0.#}x{c.rect.height:0.#} scale {c.localScale.x:0.##}]");
            }
            return $"{t.name} at {at.x:0.#},{at.y:0.#},{at.z:0.#}{size} scale {t.localScale.x:0.##} screen {screen.x:0},{screen.y:0}{kids}";
        }

        var sb = new StringBuilder();
        sb.Append($" name: {Of(slot.NameText.transform)}");
        var b = slot.NameText.textBounds;
        var end = cam != null ? cam.WorldToScreenPoint(slot.NameText.transform.TransformPoint(new Vector3(b.max.x, b.center.y, 0f))) : Vector3.zero;
        sb.Append($"; text {b.min.x:0.#}..{b.max.x:0.#} x {b.min.y:0.#}..{b.max.y:0.#}, ends on screen {end.x:0},{end.y:0}");
        sb.Append($"; ready: {Of(slot.ReadyIcon != null ? slot.ReadyIcon.transform : null)}");
        sb.Append($"; kick: {Of(slot.KickB != null ? slot.KickB.transform : null)}");
        sb.Append($"; block: {Of(plate.Find(BlockName))}");
        return sb.ToString();
    }

    /// <summary>
    /// Whether a click at each showing button's centre would reach it: the screen point the
    /// camera puts it at, through the same raycast the mouse uses, and what is hit first there.
    /// </summary>
    internal static string HitTest()
    {
        var room = LobbyController.Instance;
        var events = EventSystem.current;
        if (room == null || room.SpawnSlots == null || events == null) return "no lobby or no event system";

        var sb = new StringBuilder();
        for (int i = 0; i < room.SpawnSlots.Count; i++)
        {
            var plate = Plate(i + 1);
            if (plate == null) continue;
            foreach (string what in new[] { BlockName, KickName })
            {
                var button = plate.Find(what);
                if (button == null || !button.gameObject.activeInHierarchy) continue;

                var canvas = button.GetComponentInParent<Canvas>();
                var cam = canvas != null && canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
                if (cam == null) { sb.Append($" {i + 1} {what}: no camera;"); continue; }

                Vector3 at = cam.WorldToScreenPoint(button.position);
                var pointer = new PointerEventData(events) { position = new Vector2(at.x, at.y) };
                var hits = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
                events.RaycastAll(pointer, hits);

                string first = hits.Count > 0 && hits[0].gameObject != null ? hits[0].gameObject.name : "nothing";
                bool ours = hits.Count > 0 && hits[0].gameObject != null && hits[0].gameObject.transform.IsChildOf(button);
                sb.Append($" {i + 1} {(what == BlockName ? "block" : "kick")} at {at.x:0},{at.y:0}: {(ours ? "hit" : $"MISSED, first is {first}")};");
            }
        }
        return sb.Length > 0 ? sb.ToString() : "no buttons showing";
    }
}
