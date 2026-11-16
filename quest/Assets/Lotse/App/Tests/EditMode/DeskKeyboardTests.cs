#nullable enable
using System.Linq;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    /// <summary>U4b: the desk keyboard's layout and typing, the input bar's send rules, and the keys' dwell.</summary>
    public class DeskKeyboardTests
    {
        static DeskKey K(DeskKeys keys, DeskKeyKind kind) => keys.Rows.SelectMany(r => r).First(k => k.Kind == kind);
        static DeskKey C(DeskKeys keys, string ch) => keys.Rows.SelectMany(r => r).First(k => k.Kind == DeskKeyKind.Char && k.Char == ch);

        [Test]
        public void TheLayoutIsQwertyWithoutUmlautKeysAndOurStandInGlyphs()
        {
            var keys = new DeskKeys();
            var rows = keys.Rows.Select(r => string.Concat(r.Where(k => k.Kind == DeskKeyKind.Char).Select(k => k.Char))).ToList();
            Assert.AreEqual("qwertyuiop-", rows[0]);
            Assert.AreEqual("asdfghjkl'?", rows[1]);
            Assert.AreEqual("zxcvbnm,.", rows[2]);
            Assert.AreEqual(DeskKeyKind.Shift, keys.Rows[2][0].Kind);
            Assert.AreEqual(DeskKeyKind.Backspace, keys.Rows[2].Last().Kind);
            CollectionAssert.AreEqual(new[] { DeskKeyKind.Layer, DeskKeyKind.Space, DeskKeyKind.Send }, keys.Rows[3].Select(k => k.Kind));
            Assert.AreEqual("↑", keys.Label(K(keys, DeskKeyKind.Shift)), "↑ for ⇧, which the font lacks");
            Assert.AreEqual("←", keys.Label(K(keys, DeskKeyKind.Backspace)), "← for ⌫");
            Assert.AreEqual("Y", keys.Label(C(keys, "y")), "key caps in capitals");
            Assert.AreEqual(Texts.ReplySend, keys.Label(K(keys, DeskKeyKind.Send)));
            foreach (var r in keys.Rows) Assert.LessOrEqual(r.Sum(k => k.Width), 12f, "every row fits the 0.30 m board");
        }

        [Test]
        public void TypingStartsWithACapitalShiftsOnceAndDeletes()
        {
            var keys = new DeskKeys();
            keys.Begin("");
            var draft = "";
            Assert.AreEqual(DeskKeyResult.Typed, keys.Press(C(keys, "h"), draft, out draft));
            keys.Press(C(keys, "a"), draft, out draft);
            keys.Press(K(keys, DeskKeyKind.Space), draft, out draft);
            keys.Press(K(keys, DeskKeyKind.Shift), draft, out draft);
            keys.Press(C(keys, "o"), draft, out draft);
            keys.Press(C(keys, "k"), draft, out draft);
            Assert.AreEqual("Ha Ok", draft);
            Assert.AreEqual(DeskKeyResult.Deleted, keys.Press(K(keys, DeskKeyKind.Backspace), draft, out draft));
            Assert.AreEqual("Ha O", draft);
            keys.Begin("schon da");
            Assert.IsFalse(keys.Shift, "a kept draft goes on in lower case");
        }

        [Test]
        public void TheSignLayerTypesDigitsAndComesBack()
        {
            var keys = new DeskKeys();
            keys.Begin("x");
            Assert.AreEqual(DeskKeyResult.Toggled, keys.Press(K(keys, DeskKeyKind.Layer), "x", out var draft));
            Assert.IsTrue(keys.Numbers);
            Assert.AreEqual("ABC", keys.Label(K(keys, DeskKeyKind.Layer)));
            keys.Press(C(keys, "4"), draft, out draft);
            keys.Press(C(keys, "2"), draft, out draft);
            keys.Press(C(keys, "?"), draft, out draft);
            Assert.AreEqual("x42?", draft);
            keys.Press(K(keys, DeskKeyKind.Layer), draft, out draft);
            Assert.IsFalse(keys.Numbers);
        }

        [Test]
        public void TheKeyboardNeverSendsItsSendOnlyAsks()
        {
            var keys = new DeskKeys();
            Assert.AreEqual(DeskKeyResult.Send, keys.Press(K(keys, DeskKeyKind.Send), "Hallo", out var draft));
            Assert.AreEqual("Hallo", draft, "the draft is untouched");
            var draft2 = new string('a', ReplyLogic.MaxChars);
            Assert.AreEqual(DeskKeyResult.None, keys.Press(C(keys, "b"), draft2, out var after), "no typing past the cap");
            Assert.AreEqual(draft2, after);
            Assert.AreEqual(DeskKeyResult.None, keys.Press(K(keys, DeskKeyKind.Backspace), "", out _));
        }

        static AgentState Agent(string status) => new AgentState { Sid = "a1", Name = "Agent 1", Status = status, Source = "claude" };

        [Test]
        public void ADraftMayBeWrittenWhileTheAgentWorksButSendWaitsUntilItWaits()
        {
            var logic = new ReplyLogic();
            var busy = ReplyLogic.Can(Agent("working"), null);
            Assert.AreEqual(ReplyAvailability.NotWaiting, busy);
            Assert.IsFalse(logic.Open("a1", false, busy), "the old rule without the live view");
            Assert.IsTrue(logic.Open("a1", false, busy, editAnyway: true), "the input bar is always there");
            logic.SetText("Bitte die Tests zuerst");
            Assert.AreEqual(SendResult.NotWaiting, logic.Send(0, out var text, busy));
            Assert.AreEqual("", text);
            Assert.AreEqual(ReplyPhase.Editing, logic.Phase, "nothing went out");
            Assert.AreEqual(SendResult.Sent, logic.Send(0, out text, ReplyLogic.Can(Agent("waiting"), null)));
            Assert.AreEqual("Bitte die Tests zuerst", text);
            Assert.IsFalse(logic.Open("a1", true, busy, editAnyway: true), "never while blocked (a card)");
        }

        [Test]
        public void ClosingTheViewKeepsTheDraft()
        {
            var logic = new ReplyLogic();
            logic.Open("a1", false, ReplyAvailability.Available, editAnyway: true);
            logic.SetText("Entwurf");
            logic.Close();
            Assert.IsNull(logic.OpenSid);
            Assert.AreEqual(SendResult.NotOpen, logic.Send(0, out _), "closed: nothing can be sent");
            logic.Open("a1", false, ReplyAvailability.Available, editAnyway: true);
            Assert.AreEqual("Entwurf", logic.Draft);
        }

        [Test]
        public void AKeyNeedsADwellBeforeItPressesAndNoMargin()
        {
            var key = new PokeLogic(220, 0f, 60);
            Assert.IsFalse(key.Update(0, true, 0f, 0f, -0.08f, 0.011f, 0.011f), "seen clear");
            Assert.IsFalse(key.Update(10, true, 0f, 0f, -0.01f, 0.011f, 0.011f), "armed");
            Assert.IsFalse(key.Update(20, true, 0f, 0f, 0.002f, 0.011f, 0.011f), "crossed after 10 ms: a brush, not a press");
            Assert.IsFalse(key.Update(40, true, 0f, 0f, -0.015f, 0.011f, 0.011f), "back in front: armed again");
            Assert.IsFalse(key.Update(80, true, 0f, 0f, -0.01f, 0.011f, 0.011f));
            Assert.IsTrue(key.Update(110, true, 0f, 0f, 0.002f, 0.011f, 0.011f), "after 70 ms armed: a press");
            var edge = new PokeLogic(220, 0f, 60);
            var pressed = false;
            for (var t = 0; t <= 200; t += 10)
                pressed |= edge.Update(t, true, 0.0115f, 0f, t < 100 ? -0.01f : 0.002f, 0.011f, 0.011f);
            Assert.IsFalse(pressed, "half a millimetre beside the key: no margin, so its neighbour takes it");
            var plain = new PokeLogic();
            plain.Update(0, true, 0f, 0f, -0.08f, 0.02f, 0.02f);
            plain.Update(10, true, 0f, 0f, -0.01f, 0.02f, 0.02f);
            Assert.IsTrue(plain.Update(20, true, 0f, 0f, 0.002f, 0.02f, 0.02f), "an ordinary button needs no dwell");
        }
    }
}
