using System;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// The developer panel: the debug commands as buttons, plus a live readout of the things
/// that go wrong above four players — how many players, which seats, whose turn, how big
/// the deck is.
///
/// Drawn top right so it never overlaps the version readout. Hidden until F8, and absent
/// entirely unless developer mode is on, because none of this belongs in a real game.
/// Every command is also on a function key, since clicking during a round steals input
/// from the game.
/// </summary>
internal sealed class DevHud : MonoBehaviour
{
    public DevHud(IntPtr ptr) : base(ptr) { }

    private static bool _open;
    private GUIStyle _label;
    private GUIStyle _header;
    private Texture2D _panel;

    private void Update()
    {
        if (!Dev.Enabled) return;
        try
        {
            BotManager.Tick();
            DevAutoTest.Tick();

            if (Input.GetKeyDown(KeyCode.F8)) { _open = !_open; Dev.Log("hud", _open ? "panel open" : "panel closed"); }
            if (!_open) return;

            if (Input.GetKeyDown(KeyCode.F1)) BotManager.AddBot();
            if (Input.GetKeyDown(KeyCode.F2)) BotManager.RemoveBot();
            if (Input.GetKeyDown(KeyCode.F3)) BotManager.FillToMax();
            if (Input.GetKeyDown(KeyCode.F4)) DevCommands.PrintPlayerList();
            if (Input.GetKeyDown(KeyCode.F5)) DevCommands.PrintSeats();
            if (Input.GetKeyDown(KeyCode.F6)) DevCommands.PrintDeck();
            if (Input.GetKeyDown(KeyCode.F7)) DevCommands.PrintTurn();
            if (Input.GetKeyDown(KeyCode.F9)) DevCommands.SkipTurn();
            if (Input.GetKeyDown(KeyCode.F10)) DevCommands.PrintPodiums();
            if (Input.GetKeyDown(KeyCode.F11)) DevCommands.StartMatch();
        }
        catch (Exception e) { Dev.Warn("hud", $"input failed: {e.Message}"); }
    }

    private void OnGUI()
    {
        if (!Dev.Enabled) return;
        try
        {
            EnsureStyles();

            if (!_open)
            {
                GUI.Label(new Rect(Screen.width - 150f, 8f, 150f, 20f), "F8  dev panel", _label);
                return;
            }

            const float w = 250f;
            float x = Screen.width - w - 8f;
            float y = 8f;

            GUI.DrawTexture(new Rect(x - 6f, y - 4f, w + 12f, 400f), _panel);

            GUI.Label(new Rect(x, y, w, 20f), "DEVELOPER PANEL   (F8 hides)", _header);
            y += 22f;

            foreach (string line in Status())
            {
                GUI.Label(new Rect(x, y, w, 18f), line, _label);
                y += 16f;
            }
            y += 6f;

            y = Button(x, y, w, "F1   add a bot", BotManager.AddBot);
            y = Button(x, y, w, "F2   remove a bot", BotManager.RemoveBot);
            y = Button(x, y, w, $"F3   fill up to {Limits.Max}", BotManager.FillToMax);
            y += 4f;
            y = Button(x, y, w, "F4   print player list", DevCommands.PrintPlayerList);
            y = Button(x, y, w, "F5   print seat assignments", DevCommands.PrintSeats);
            y = Button(x, y, w, "F6   print deck state", DevCommands.PrintDeck);
            y = Button(x, y, w, "F7   print current turn", DevCommands.PrintTurn);
            y = Button(x, y, w, "F10  print lobby podiums", DevCommands.PrintPodiums);
            y += 4f;
            y = Button(x, y, w, "F9   skip this turn", DevCommands.SkipTurn);
            y = Button(x, y, w, "F11  start the match", DevCommands.StartMatch);

            GUI.Label(new Rect(x, y + 4f, w, 18f), "everything prints to LogOutput.log", _label);
        }
        catch { /* a debug panel must never be what breaks the game */ }
    }

    private float Button(float x, float y, float w, string text, Action action)
    {
        if (GUI.Button(new Rect(x, y, w, 22f), text))
        {
            try { action(); }
            catch (Exception e) { Dev.Warn("hud", $"{text} failed: {e.Message}"); }
        }
        return y + 24f;
    }

    /// <summary>The four numbers worth watching continuously while a round runs.</summary>
    private string[] Status()
    {
        try
        {
            var m = Dev.Mgr;
            int lobby = Dev.LobbyPlayers().Count;
            int bots = BotManager.Count;

            if (m == null)
                return new[]
                {
                    $"lobby players : {lobby}  ({bots} bots)",
                    "match         : not started",
                    $"deck size     : {DeckSizePatch.CurrentSize}",
                };

            int alive = 0;
            string turn = "nobody";
            foreach (var p in Dev.TablePlayers())
            {
                if (p == null) continue;
                if (!p.Dead) alive++;
                if (p.HaveTurn) turn = $"{p.PlayerName} (seat {p.Slot})";
            }

            return new[]
            {
                $"lobby players : {lobby}  ({bots} bots)",
                $"seated        : {Dev.TablePlayers().Count}, {alive} alive",
                $"seats/plates  : {(m.Slots == null ? -1 : m.Slots.Count)}/{(m.NameTexts == null ? -1 : m.NameTexts.Count)}",
                $"active slot   : {m.ActivePlayerSlot}   count={m.StartPlayerCount}",
                $"turn          : {turn}",
                $"deck size     : {DeckSizePatch.CurrentSize}",
            };
        }
        catch (Exception e) { return new[] { $"status failed: {e.Message}" }; }
    }

    private void EnsureStyles()
    {
        if (_label != null) return;

        _label = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.UpperLeft, richText = false };
        _label.normal.textColor = new Color(1f, 1f, 1f, 0.85f);

        _header = new GUIStyle(_label) { fontSize = 13, fontStyle = FontStyle.Bold };
        _header.normal.textColor = new Color(0.6f, 0.9f, 1f, 1f);

        _panel = new Texture2D(1, 1);
        _panel.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.78f));
        _panel.Apply();
    }
}
