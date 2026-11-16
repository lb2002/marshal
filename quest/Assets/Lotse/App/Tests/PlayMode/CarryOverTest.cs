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
    /// U5b section 0, the carry-over fixes from U6: overview titles wrap to two lines, a plan the bridge clears goes, the
    /// summary and plan lines step aside while a block is shown (the block untouched), the rail as high as its content.
    /// </summary>
    public class CarryOverTest
    {
        WorkspaceHarness _h;

        [SetUp]
        public void SetUp()
        {
            _h = new WorkspaceHarness { OpenConversation = false };
            _h.SetUp();
        }

        [TearDown]
        public void TearDown() => _h.TearDown();

        static string WithPlan(string sid, string name, string status, string title) =>
            Agent(sid, name, status, title).TrimEnd('}') +
            ",\"plan\":{\"steps\":[{\"text\":\"Eins\",\"status\":\"done\"},{\"text\":\"Zwei\",\"status\":\"in_progress\"}],\"more\":0,\"updatedAt\":1}}";

        [UnityTest]
        public IEnumerator AnOverviewTitleWrapsToTwoLinesWithTheOrbOnItsFirstLine()
        {
            const string longTitle = "Warenkorb-Badge zeigt die richtige Anzahl nach dem Neuladen";
            yield return _h.GoLive(agents: WithPlan("s1", "A", "working", longTitle) + "," + WithPlan("s2", "B", "working", "Kurz"));
            yield return Frames(3);
            var rows = _h.Rows;
            var title = rows.GetComponentsInChildren<TMPro.TextMeshPro>().First(t => t.name == "Title" && t.text.StartsWith("Warenkorb"));
            title.ForceMeshUpdate();
            Assert.AreEqual(2, title.textInfo.lineCount, "the long title takes two lines");
            StringAssert.StartsWith("Warenkorb-Badge", rows.TitleShown("s1"));
            Assert.Greater(rows.TitleShown("s1").Length, 20, "more than about 13 characters are shown");
            // the vendor line follows below the title
            var source = title.transform.parent.Find("Vendor");
            Assert.Less(source.localPosition.y, title.transform.localPosition.y - title.rectTransform.sizeDelta.y + 0.0005f);
            Assert.AreEqual("Kurz", rows.TitleShown("s2"));
        }

        [UnityTest]
        public IEnumerator APlanTheBridgeClearsGoesFromTheOverviewAndTheConversation()
        {
            yield return _h.GoLive(agents: WithPlan("s1", "A", "working", "Eins") + "," + Agent("s2", "B", "working", "Zwei"));
            yield return Frames(3);
            Assert.AreEqual("2 of 2", _h.Rows.StatusShown("s1"), "the plan's count shows");
            Assert.AreEqual((2, 2), _h.Rows.MarksOf("s1"));
            _h.Socket.Receive("{\"type\":\"agent\",\"agent\":" + Agent("s1", "A", "working", "Eins") + "}");
            yield return Frames(3);
            Assert.IsNull(_h.Root.Session.Store.Agents.First(a => a.Sid == "s1").Plan);
            Assert.AreEqual(OverviewText.NoPlan, _h.Rows.StatusShown("s1"), "U9: no plan, \"no plan\" in the count's place");
            Assert.AreEqual((0, 0), _h.Rows.MarksOf("s1"));
        }

        [UnityTest]
        public IEnumerator WhileABlockIsShownTheSummaryAndPlanLinesStepAsideAndTheBlockStaysBuiltOnce()
        {
            _h.OpenConversation = true;
            yield return _h.GoLive(agents: WithPlan("s1", "A", "working", "Eins") + "," + Agent("s2", "B", "working", "Zwei"));
            _h.Socket.Receive("{\"type\":\"agent_detail\",\"sid\":\"s1\",\"title\":\"t\",\"summary\":\"Baut den Warenkorb um.\",\"recent\":[],\"source\":\"rules\"}");
            yield return Frames(3);
            var conv = _h.Conversation;
            Assert.AreEqual("Eins\nZwei", conv.PlanShown, "the plan");
            Assert.IsTrue(conv.Feed.gameObject.activeInHierarchy, "the log");
            _h.Socket.Receive(PermOpen("r1", "s1", "dangerous", "rm -rf build"));
            yield return Frames(3);
            Assert.IsTrue(_h.Block.IsShown);
            Assert.IsFalse(conv.Feed.gameObject.activeInHierarchy, "the request stands in the log's place");
            Assert.AreEqual("Eins\nZwei", conv.PlanShown, "the plan stays at the right");
            var builds = _h.Desk.BlockBuilds;
            // a hold runs; the detail changes: the block is not rebuilt
            yield return _h.PinchHold(true, 4);
            var progress = _h.Root.Session.HoldProgress;
            Assert.Greater(progress, 0f, "a hold runs");
            _h.Socket.Receive("{\"type\":\"agent_detail\",\"sid\":\"s1\",\"title\":\"t\",\"summary\":\"Neu.\",\"recent\":[],\"source\":\"rules\"}");
            yield return Frames(2);
            Assert.AreEqual(builds, _h.Desk.BlockBuilds, "the block is not rebuilt");
            Assert.GreaterOrEqual(_h.Root.Session.HoldProgress, progress, "the hold goes on");
            _h.ReleaseHands();
            yield return Frames(2);
            // the request goes: the log comes back
            _h.Socket.Receive(PermClosed("r1", "timeout"));
            yield return Frames(3);
            Assert.IsTrue(conv.Feed.gameObject.activeInHierarchy, "the log is back");
            Assert.Greater(conv.Feed.Height, 0f);
        }

        static string SixSteps(string sid, string title) =>
            Agent(sid, "A", "working", title).TrimEnd('}') + ",\"plan\":{\"steps\":[" + string.Join(",", Enumerable.Range(1, 6).Select(i =>
                "{\"text\":\"Schritt " + i + " mit einem langen Text, den der Agent selbst geschrieben hat\",\"status\":\"" +
                (i < 3 ? "done" : i == 3 ? "in_progress" : "pending") + "\"}")) + "],\"more\":0,\"updatedAt\":1}}";

        [UnityTest]
        public IEnumerator AnOpenChecklistClosesWhenABlockComesAndNothingButTheBlockLiesInItsRectangle()
        {
            // U6 review 1, Important 1: agent text never lies over the block (U13: the plan stands beside it)
            _h.OpenConversation = true;
            yield return _h.GoLive(agents: SixSteps("s1", "Eins") + "," + Agent("s2", "B", "working", "Zwei"));
            _h.Socket.Receive("{\"type\":\"agent_detail\",\"sid\":\"s1\",\"title\":\"t\",\"summary\":\"Baut um.\",\"recent\":[],\"source\":\"rules\"}");
            yield return Frames(3);
            var conv = _h.Conversation;
            Assert.AreEqual(5, conv.PlanMarks.Count, "the plan: five lines at most");
            _h.Socket.Receive(PermOpen("r1", "s1", "dangerous", "rm -rf build/cache/" + new string('x', 60) + " && git push --force origin main"));
            yield return Frames(10);
            Assert.IsTrue(_h.Block.IsShown);
            // from the actual bounds: no renderer outside the block reaches into any of the block's own renderers
            var own = _h.Block.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled && r.bounds.size != Vector3.zero).ToList();
            foreach (var r in conv.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled || r.transform.IsChildOf(_h.Block.transform)) continue;
                var b = r.bounds;
                if (b.size == Vector3.zero) continue;
                foreach (var o in own)
                {
                    var block = o.bounds;
                    var overlapX = b.min.x < block.max.x - 0.001f && b.max.x > block.min.x + 0.001f;
                    var overlapY = b.min.y < block.max.y - 0.001f && b.max.y > block.min.y + 0.001f;
                    Assert.IsFalse(overlapX && overlapY, r.name + " (" + r.transform.parent.name + ") lies over the block's " + o.name);
                }
            }
            // the block goes: the log returns
            _h.Socket.Receive(PermClosed("r1", "deny"));
            yield return Frames(3);
            Assert.IsTrue(conv.Feed.gameObject.activeInHierarchy);
        }

        [UnityTest]
        public IEnumerator TheRailIsAsHighAsItsOrbsNeedAndHangsFromTheWindowsTopEdge()
        {
            yield return _h.GoLive(agents: WithPlan("s1", "A", "working", "Eins") + "," + Agent("s2", "B", "working", "Zwei"));
            var view = _h.Root.Workspace;
            yield return _h.Poke(view.Header.CollapseButton);
            yield return Frames(4);
            Assert.IsTrue(view.Folded);
            var rail = view.Rail;
            Assert.Less(rail.Height, WorkspaceView.BaseHeight - 0.05f, "two agents: not the window's full height");
            Assert.GreaterOrEqual(rail.Height, RailView.MinHeight);
            var bg = view.Background.transform;
            Assert.AreEqual(WorkspaceView.Top, bg.localPosition.y + bg.localScale.y * 0.5f, 1e-4f, "the top edge stays");
            Assert.AreEqual(rail.Height, bg.localScale.y, 1e-4f, "the panel is as high as the rail");
            Assert.AreEqual(RailView.MinHeight, RailView.HeightFor(0, false), "no agents: the minimum");
            Assert.Greater(RailView.HeightFor(7, true), RailView.HeightFor(2, false), "more signs, a higher rail");
        }
    }
}
