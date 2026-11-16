using System.Collections;
using System.Linq;
using Lotse.App;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Lotse.App.Tests.WorkspaceHarness;

namespace Lotse.App.Tests
{
    /// <summary>
    /// The facts row in the approval block (U7) through AppRoot with a fake bridge: shown for a dangerous request with
    /// the "Checked:" line, the counts, where, the verdict chip and the reason; an impact arriving cancels a hold; the
    /// verdict never changes the gesture; within the draw-call budget.
    /// </summary>
    public class ImpactPlayTest
    {
        WorkspaceHarness _h;

        [SetUp]
        public void SetUp()
        {
            _h = new WorkspaceHarness();
            _h.SetUp();
        }

        [TearDown]
        public void TearDown() => _h.TearDown();

        const string Gone = "{\"type\":\"perm_impact\",\"id\":\"r1\",\"impact\":{\"kind\":\"delete\",\"target\":\"~/code/legacy-site\",\"files\":214," +
                            "\"bytes\":38000000,\"outsideProject\":true,\"recoverable\":\"no\",\"reason\":\"Not in a Git project: gone for good.\"}}";

        IEnumerator Dangerous()
        {
            yield return _h.GoLive();
            _h.Socket.Receive(PermOpen("r1", "s1", "destructive", "rm -rf ../legacy-site", "Deletes a folder: rm -rf ../legacy-site"));
            yield return Frames(2);
            Assert.IsTrue(_h.Desk.BlockShown);
        }

        [UnityTest]
        public IEnumerator ADangerousBlockShowsTheFactsRowAndStaysWithinTheBudget()
        {
            yield return Dangerous();
            Assert.AreEqual(Texts.CheckingEffects, _h.Block.VerdictShown, "no impact yet");
            _h.Socket.Receive(Gone);
            yield return Frames(2);
            var b = _h.Block;
            Assert.AreEqual("Checked: ~/code/legacy-site", b.CheckedShown);
            StringAssert.StartsWith("214 files \u00B7 38 MB in size", b.FactsShown);
            StringAssert.EndsWith("Outside this project", b.FactsShown);
            Assert.AreEqual(Texts.FactGone + " " + Texts.FactGoneCaption, b.VerdictShown);
            Assert.AreEqual(Theme.Red, b.VerdictColor);
            Assert.AreEqual("Not in a Git project: gone for good.", b.ReasonShown);
            Assert.AreEqual(Texts.RiskDangerous, b.RiskShown, "the risk word is unchanged");
            Assert.AreEqual(Texts.HintTwoHand, b.HintShown, "the gesture is unchanged");

            var window = _h.Root.Workspace.Window;
            var renderers = RenderProbe.Renderers(window);
            var passes = RenderProbe.Passes(window);
            Debug.Log($"[Lotse] render probe, dangerous block with the facts row: {renderers} renderers, {passes} passes");
            Assert.LessOrEqual(renderers, 48);
            Assert.LessOrEqual(passes, 48);
        }

        [UnityTest]
        public IEnumerator WithoutAnImpactTheBlockSaysEffectsUnknownAfterTwoSeconds()
        {
            yield return Dangerous();
            yield return Frames(22);
            Assert.AreEqual(Texts.FactUnknown + " " + Texts.FactEffects, _h.Block.VerdictShown);
            Assert.AreEqual("", _h.Block.CheckedShown);
        }

        [UnityTest]
        public IEnumerator AnImpactArrivingCancelsAHoldAndTheHeldPinchesNeverApprove()
        {
            yield return Dangerous();
            yield return _h.PinchHold(true, 5);
            Assert.Greater(_h.Root.Session.HoldProgress, 0f, "a two-hand hold runs");
            _h.Socket.Receive(Gone);
            yield return Frames(1);
            Assert.AreEqual(0f, _h.Root.Session.HoldProgress, "the impact arriving cancels it");
            yield return Frames(30);
            Assert.AreEqual(0, _h.DecisionFrames().Count(), "the pinches held across it approve nothing");
            Assert.AreEqual(Texts.FactGone + " " + Texts.FactGoneCaption, _h.Block.VerdictShown);
        }

        [UnityTest]
        public IEnumerator OneHandNeverApprovesWhateverTheVerdict()
        {
            yield return Dangerous();
            _h.Socket.Receive(Gone.Replace("\"no\"", "\"unknown\""));
            yield return Frames(2);
            Assert.AreEqual(Texts.FactUnknown + " " + Texts.FactEffects, _h.Block.VerdictShown);
            yield return _h.PinchHold(false, 40);
            Assert.AreEqual(0, _h.DecisionFrames().Count());
        }
    }
}
