using System;
using System.Collections.Generic;
using System.Text;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Developer tests for hidden hands, reached as "fair hands &lt;word&gt;". Every one reads the host's
/// own record of what it sent and what its own lists and screen hold; none of them sends,
/// changes or invents anything. The deal, throw and reveal checks also run by themselves after
/// each real deal, throw and reveal while developer mode is on.
///
///   fair hands            what is on, and the counts so far
///   fair hands payload    the payload reader and rewriter against hand-made payloads (no game needed)
///   fair hands deal       every hidden send of this match: real values to the owner and the host only
///   fair hands throw      the cards thrown since the last reveal are plain in the synced lists
///   fair hands draw       every card in a hand the host does not own is drawn alike on its screen
///   fair hands all        all of the above
/// </summary>
internal static class HandsTests
{
    internal static void Drive(string rest)
    {
        string word = string.IsNullOrEmpty(rest) ? "state" : rest.Trim().ToLowerInvariant();
        switch (word)
        {
            case "state": State(); break;
            case "payload": PayloadTest(); break;
            case "deal": DealTest(); break;
            case "throw": ThrowTest(); break;
            case "draw": DrawTest(); break;
            case "all":
                State(); PayloadTest(); DealTest(); ThrowTest(); DrawTest();
                break;
            default:
                Plugin.Log.LogWarning($"{Fair.Tag} test: hands: no such test '{word}' (state, payload, deal, throw, draw, all)");
                break;
        }
    }

    private static void State()
    {
        var d = HiddenThrow.For(HiddenThrow.Table.Deck);
        var c = HiddenThrow.For(HiddenThrow.Table.Chaos);
        Plugin.Log.LogInfo($"{Fair.Tag} test: hands hosting={Fair.Hosting} hide={Fair.HideHands} match={Fair.Match} " +
                           $"targeted={HiddenDeal.Targeted} fellback={HiddenDeal.FellBack} " +
                           $"deck thrown hidden={d.Hidden} revealed={d.Revealed} pending={d.Pending} " +
                           $"chaos thrown hidden={c.Hidden} revealed={c.Revealed} pending={c.Pending} " +
                           $"cards drawn alike={HiddenCards.HiddenCount}");
    }

    // ------------------------------------------------------------------ payload

    private static byte[] Ints(params int[] v)
    {
        var b = new byte[4 * v.Length];
        for (int i = 0; i < v.Length; i++) Payload.Put(b, 4 * i, v[i]);
        return b;
    }

    private static byte[] Join(params byte[][] parts)
    {
        var all = new List<byte>();
        foreach (var p in parts) all.AddRange(p);
        return all.ToArray();
    }

    private static HiddenDeal.Call CallOf(HiddenDeal.Shape shape, int ints = 0)
    {
        foreach (var c in HiddenDeal.Calls) if (c.Shape == shape && (ints == 0 || c.Ints == ints)) return c;
        return null;
    }

    private static void PayloadTest()
    {
        int bad = 0;
        var sb = new StringBuilder();
        void Expect(string what, bool ok) { if (!ok) { bad++; sb.Append(what).Append("; "); } }

        // A hand: count, then the cards.
        var hand = CallOf(HiddenDeal.Shape.Hand);
        var deal = Join(Ints(5), Ints(0, 2, -1, 3, 1));
        Expect("hand read", Payload.Read(deal, hand, out var at, out var v) && v.Length == 5 && v[2] == -1 && at[0] == 4);
        if (at != null)
        {
            var hidden = Payload.Hidden(deal, hand, at);
            Expect("hand same length", hidden.Length == deal.Length);
            Expect("hand count kept", Payload.Int(hidden, 0) == 5);
            bool allPlain = true;
            foreach (int p in at) allPlain &= Payload.Int(hidden, p) == HiddenDeal.DecoyCard;
            Expect("hand all plain", allPlain);
        }
        Expect("hand with a stray byte refused", !Payload.Read(Join(deal, new byte[1]), hand, out _, out _));
        Expect("no hand refused", !Payload.Read(Ints(-1), hand, out _, out _));
        Expect("one-byte count refused", !Payload.Read(Join(new byte[] { 6 }, Ints(0, 2, -1, 3, 1)), hand, out _, out _));

        // A hand put back after a migration: hand, the places still held, a flag.
        var restore = CallOf(HiddenDeal.Shape.Restore);
        var back = Join(Ints(3, 0, -1, 2), Ints(2, 0, 2), new byte[] { 1 });
        Expect("restore read", Payload.Read(back, restore, out at, out v) && v.Length == 3 && v[1] == -1);
        if (at != null)
        {
            var hidden = Payload.Hidden(back, restore, at);
            bool tailSame = true;
            for (int i = 16; i < back.Length; i++) tailSame &= hidden[i] == back[i];
            Expect("restore keeps the places and flag", tailSame);
        }

        // Plain ints: Two-Card Poker's two cards, the older table's five and seven.
        foreach (int n in new[] { 2, 5, 7 })
        {
            var c = CallOf(HiddenDeal.Shape.Ints, n);
            if (c == null) { Expect($"{n}-card call listed", false); continue; }
            var vals = new int[n];
            for (int i = 0; i < n; i++) vals[i] = i + 2;
            var p = Ints(vals);
            Expect($"{n} cards read", Payload.Read(p, c, out at, out v) && v.Length == n && v[n - 1] == n + 1);
            if (at != null)
            {
                var hidden = Payload.Hidden(p, c, at);
                bool allPlain = true;
                for (int i = 0; i < n; i++) allPlain &= Payload.Int(hidden, 4 * i) == HiddenDeal.DecoyCard;
                Expect($"{n} cards all plain", allPlain);
            }
            Expect($"{n} cards wrong length refused", !Payload.Read(Ints(1), c, out _, out _));
        }

        // A Texas swap: which card stays as it is, the card that came in becomes no card.
        var swap = CallOf(HiddenDeal.Shape.Swap);
        var sw = Ints(1, 37);
        Expect("swap read", Payload.Read(sw, swap, out at, out v) && v.Length == 1 && v[0] == 37);
        if (at != null)
        {
            var hidden = Payload.Hidden(sw, swap, at);
            Expect("swap keeps which card", Payload.Int(hidden, 0) == 1);
            Expect("swap hides the new card", Payload.Int(hidden, 4) == HiddenDeal.NoCard);
        }

        Fair.Result("hands payload", bad == 0, bad == 0 ? "every layout read and rewritten as expected" : sb.ToString());
    }

    // ------------------------------------------------------------------ deal

    private static void DealTest()
    {
        int n = 0, ok = 0;
        foreach (var s in HiddenDeal.LastDeals)
        {
            n++;
            if (Check(s, out _)) ok++;
        }
        if (!Fair.HideHands)
        {
            Fair.Result("hands deal", n == 0, n == 0
                ? "hidden hands off this match: every deal went out as the game sends it"
                : $"hidden hands off but {n} hidden sends recorded this match");
            return;
        }
        if (n == 0) { Fair.Result("hands deal", false, "no hidden deal yet this match (deal a hand first)"); return; }
        Fair.Result("hands deal", ok == n, $"{ok} of {n} seats' last sends correct (see the per-send lines above for any that are not)");
    }

    /// <summary>After each hidden send, in developer mode.</summary>
    internal static void CheckDeal(HiddenDeal.Sent s)
    {
        bool pass = Check(s, out string detail);
        Fair.Result($"hands deal {s.Call} seat {s.Seat}", pass, detail);
    }

    private static bool Check(HiddenDeal.Sent s, out string detail)
    {
        var sb = new StringBuilder();
        bool pass = true;
        void Fail(string what) { pass = false; sb.Append(what).Append("; "); }

        if (s.To.Count == 0) Fail("sent to nobody");
        if (s.Decoy == null || s.Decoy.Length != s.Original.Length) Fail("other players' copy has another length");

        // Only the private values differ, and each is plain.
        var priv = new HashSet<int>();
        foreach (int p in s.Private) for (int k = 0; k < 4; k++) priv.Add(p + k);
        int want = s.Shape == HiddenDeal.Shape.Swap ? HiddenDeal.NoCard : HiddenDeal.DecoyCard;
        if (s.Decoy != null && s.Decoy.Length == s.Original.Length)
        {
            for (int i = 0; i < s.Original.Length; i++)
                if (!priv.Contains(i) && s.Decoy[i] != s.Original[i]) { Fail($"byte {i} outside the cards changed"); break; }
            foreach (int p in s.Private)
                if (Payload.Int(s.Decoy, p) != want) { Fail($"value at {p} is not plain"); break; }
        }
        for (int i = 0; i < s.Private.Length; i++)
            if (Payload.Int(s.Original, s.Private[i]) != s.Truth[i]) { Fail("recorded values differ from the game's"); break; }

        int real = 0, plain = 0;
        bool ownerGot = s.Owner < 0;
        var seen = new HashSet<int>();
        foreach (var (conn, isReal, payload) in s.To)
        {
            if (!seen.Add(conn)) Fail($"connection {conn} sent to twice");
            bool local = IsLocal(conn);
            bool shouldBeReal = local || conn == s.Owner;
            if (isReal != shouldBeReal) Fail($"connection {conn} got the {(isReal ? "real" : "plain")} copy");
            if (isReal && !Same(payload, s.Original)) Fail($"connection {conn} real copy differs from the game's");
            if (!isReal && !Same(payload, s.Decoy)) Fail($"connection {conn} plain copy is not the plain one");
            if (conn == s.Owner) ownerGot = true;
            if (isReal) real++; else plain++;
        }
        if (!ownerGot) Fail($"the owner (connection {s.Owner}) was not sent its own hand");

        detail = pass
            ? $"{s.Truth.Length} values: real to {real} (owner and host), plain to {plain}"
            : sb.ToString();
        return pass;
    }

    private static bool IsLocal(int connectionId)
    {
        if (connectionId == 0) return true;
        try
        {
            var local = NetworkServer.localConnection;
            return local != null && local.connectionId == connectionId;
        }
        catch { return false; }
    }

    private static bool Same(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // ------------------------------------------------------------------ throw and reveal

    private static void ThrowTest()
    {
        if (!NetworkServer.active) { Fair.Result("hands throw", false, "not hosting"); return; }
        bool any = false;
        foreach (var t in new[] { HiddenThrow.Table.Deck, HiddenThrow.Table.Chaos })
        {
            if (!HiddenThrow.Lists(t, out var round, out _) || round == null) continue;
            any = true;
            var rec = HiddenThrow.For(t);
            if (!Fair.HideHands)
            {
                Fair.Result($"hands throw {t}", !rec.Pending, rec.Pending
                    ? "hidden hands off but thrown cards are being held back"
                    : "hidden hands off: thrown cards go to the table as the game sends them");
                continue;
            }
            if (!rec.Pending)
            {
                Fair.Result($"hands throw {t}", true, $"nothing face down right now ({rec.Hidden} hidden throws, {rec.Revealed} reveals this match)");
                continue;
            }
            CheckThrow(t);
        }
        if (!any) Fair.Result("hands throw", false, "no Liar's Deck or Chaos table in this scene");
    }

    /// <summary>After each hidden throw, in developer mode: plain in the lists, right counts.</summary>
    internal static void CheckThrow(HiddenThrow.Table t)
    {
        var sb = new StringBuilder();
        bool pass = true;
        void Fail(string what) { pass = false; sb.Append(what).Append("; "); }

        var rec = HiddenThrow.For(t);
        if (!HiddenThrow.Lists(t, out var round, out var spotOn) || round == null)
        {
            Fair.Result($"hands throw {t}", false, "no lists");
            return;
        }

        if (rec.Round.Count == 0) Fail("the last throw was not recorded");
        else if (rec.Round.Count != round.Count) Fail($"recorded {rec.Round.Count} cards, the table list holds {round.Count}");
        foreach (var kv in rec.Round)
            if (kv.Key >= round.Count || round[kv.Key] != HiddenThrow.DecoyCard) { Fail($"place {kv.Key} of the last throw is not plain"); break; }
        if (spotOn != null)
            foreach (var kv in rec.SpotOn)
                if (kv.Key >= spotOn.Count || spotOn[kv.Key] != HiddenThrow.DecoyCard) { Fail($"spot-on place {kv.Key} is not plain"); break; }

        int shown = Count(t);
        if (shown >= 0 && shown != round.Count) Fail($"the card count shows {shown}, the list holds {round.Count}");

        Fair.Result($"hands throw {t}", pass, pass
            ? $"{round.Count} cards face down, the count shows {round.Count}, {rec.SpotOn.Count} spot-on places held"
            : sb.ToString());
    }

    private static int Count(HiddenThrow.Table t)
    {
        try
        {
            var m = Manager.Instance;
            if (m == null) return -1;
            if (t == HiddenThrow.Table.Deck) return m.DeckGamePlayManager != null ? m.DeckGamePlayManager.LastRoundCount : -1;
            return m.ChaosDeckGame != null ? m.ChaosDeckGame.LastRoundCount : -1;
        }
        catch { return -1; }
    }

    /// <summary>After each reveal, in developer mode: every place holds the real card again.</summary>
    internal static void CheckReveal(HiddenThrow.Table t, Dictionary<int, int> roundTruth, Dictionary<int, int> spotOnTruth)
    {
        bool pass = true;
        string why = "";
        if (!HiddenThrow.Lists(t, out var round, out var spotOn)) { pass = false; why = "no lists"; }
        else
        {
            foreach (var kv in roundTruth)
                if (round == null || kv.Key >= round.Count || round[kv.Key] != kv.Value) { pass = false; why = $"place {kv.Key} is not the real card"; break; }
            if (pass)
                foreach (var kv in spotOnTruth)
                    if (spotOn == null || kv.Key >= spotOn.Count || spotOn[kv.Key] != kv.Value) { pass = false; why = $"spot-on place {kv.Key} is not the real card"; break; }
        }
        Fair.Result($"hands reveal {t}", pass, pass
            ? $"{roundTruth.Count} thrown cards and {spotOnTruth.Count} spot-on places hold the real cards before the reveal reads them"
            : why);
    }

    // ------------------------------------------------------------------ draw

    private static void DrawTest()
    {
        if (!NetworkServer.active) { Fair.Result("hands draw", false, "the host's screen is the one this checks; not hosting"); return; }
        if (!Fair.HideHands) { Fair.Result("hands draw", true, "hidden hands off: the host draws the game's own way"); return; }

        HiddenCards.Build();
        var m = Manager.Instance;
        if (m == null) { Fair.Result("hands draw", false, "no table"); return; }

        int cards = 0, odd = 0, poker = 0;
        Mesh shape = null;
        string face = null;
        Mesh deckPlain = null;
        try { deckPlain = Card.ResolveMesh(HiddenCards.DecoyCard); } catch { }

        foreach (var p in TableHand.Seats(m))
        {
            if (p == null || p.isOwned) continue;
            var deck = p.GetComponent<DeckGameplay>();
            var chaos = p.GetComponent<ChaosDeckGameplay>();
            var list = deck != null ? deck.Cards : chaos != null ? chaos.Cards : null;
            if (list != null)
                foreach (var go in list)
                {
                    if (go == null || !go.activeInHierarchy) continue;
                    if (!HiddenCards.IsHidden(go)) { odd++; continue; }
                    var mf = go.GetComponent<MeshFilter>();
                    if (mf == null) continue;
                    cards++;
                    var mesh = mf.sharedMesh;
                    if (shape == null) shape = mesh;
                    else if (mesh != shape) odd++;
                    if (deck != null && deckPlain != null && mesh != deckPlain) odd++;
                }

            var pk = p.GetComponent<PokerGamePlay>();
            if (pk != null && pk.Cards != null)
                foreach (var go in pk.Cards)
                {
                    if (go == null || !go.activeInHierarchy) continue;
                    var pc = go.GetComponent<PokerCard>();
                    var r = go.GetComponent<Renderer>();
                    if (pc == null || r == null) continue;
                    if (!HiddenCards.IsHidden(pc)) { odd++; continue; }
                    poker++;
                    var mat = r.sharedMaterial;
                    string name = mat != null ? mat.name.Replace(" (Instance)", "") : "";
                    if (face == null) face = name;
                    else if (name != face) odd++;
                }
        }

        if (cards + poker == 0)
        {
            Fair.Result("hands draw", false, "no other seat holds a card on this screen right now (check during a round)");
            return;
        }
        Fair.Result("hands draw", odd == 0, odd == 0
            ? $"{cards + poker} cards in other seats' hands, all drawn alike"
            : $"{odd} of {cards + poker} cards in other seats' hands are drawn differently");
    }
}
