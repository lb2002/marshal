using System.Collections;
using System.Linq;
using Lotse.App;
using Lotse.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Lotse.App.Tests.WorkspaceHarness;

namespace Lotse.App.Tests
{
    /// <summary>
    /// The workspace's right column through AppRoot (U5a; the U4b live view's tests, re-expressed): selecting an agent
    /// subscribes to its feed and switching unsubscribes it; entries are de-duplicated and the feed follows the end until
    /// the user scrolls; a drag scrolls it; the input bar's Senden follows the U3/U4b rules (only an explicit press, a
    /// non-blank draft, the system keyboard closed, an agent that waits); the two keyboards never show together; replies
    /// wait while the agent has an open request; nothing typed is ever logged; the summary line is plain and capped.
    /// </summary>
    public class ConversationTest
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

        int Replies() => _h.Socket.Sent.Count(m => m.Contains("\"agent_reply\""));

        InputBar Bar => _h.Conversation.Input;

        [UnityTest]
        public IEnumerator SelectingSubscribesSwitchingUnsubscribesAndTheFeedFollowsTheEnd()
        {
            yield return _h.GoLive();
            Assert.AreEqual("s1", _h.Root.SelectedAgent);
            Assert.AreEqual(1, _h.Socket.Sent.Count(m => m == "{\"type\":\"feed_subscribe\",\"sid\":\"s1\"}"));
            Assert.IsTrue(_h.Conversation.Feed.Shown.Contains(Texts.FeedLoading), "loading until the snapshot");
            Assert.AreEqual("s1", _h.Desk.OpenSid, "its row is open");
            StringAssert.StartsWith("Claude Code", _h.Root.Workspace.Header.Said, "the vendor as text, no logo");

            _h.Socket.Receive("{\"type\":\"feed_snapshot\",\"sid\":\"s1\",\"entries\":[" + Entries(0, 30) + "]}");
            yield return Frames(2);
            var ids = _h.Conversation.Feed.Ids;
            Assert.GreaterOrEqual(ids.Count, 3, "the last lines in view");
            Assert.AreEqual("e29", ids.Last(), "newest at the bottom");

            _h.Socket.Receive("{\"type\":\"feed_append\",\"sid\":\"s1\",\"entries\":[" + Entry("e29", "text", "sagt", "Eintrag 29") + "," +
                              Entry("e30", "error", "error", "Test fehlgeschlagen") + "]}");
            yield return Frames(2);
            Assert.AreEqual(31, _h.Desk.Feeds.Entries("s1").Count, "de-duplicated by id");
            Assert.AreEqual("e30", _h.Conversation.Feed.Ids.Last(), "it followed the end");

            // scrolled up: it stays put and says what came below; "▼ n neue" brings it back
            _h.Desk.OnScroll(-3); // (U13: no scroll buttons; a drag scrolls, see the next test)
            yield return Frames(2);
            Assert.IsFalse(_h.Desk.Scroll.FollowsEnd);
            var last = _h.Conversation.Feed.Ids.Last();
            _h.Socket.Receive("{\"type\":\"feed_append\",\"sid\":\"s1\",\"entries\":[" + Entries(31, 33) + "]}");
            yield return Frames(2);
            Assert.AreEqual(last, _h.Conversation.Feed.Ids.Last(), "the user scrolled up: it does not jump");
            Assert.AreEqual(2, _h.Desk.Scroll.NewBelow);
            _h.Desk.OnScrollToEnd();
            yield return Frames(2);
            Assert.AreEqual("e32", _h.Conversation.Feed.Ids.Last());

            // another agent: s1 unsubscribed, s2 subscribed; the feed's text is never logged
            yield return _h.PokeRow("s2");
            Assert.AreEqual("s2", _h.Root.SelectedAgent);
            var unsub = _h.Socket.Sent.IndexOf("{\"type\":\"feed_unsubscribe\",\"sid\":\"s1\"}");
            var sub = _h.Socket.Sent.IndexOf("{\"type\":\"feed_subscribe\",\"sid\":\"s2\"}");
            Assert.GreaterOrEqual(unsub, 0, "s1 unsubscribed");
            Assert.Greater(sub, unsub, "then s2 subscribed");
            Assert.IsFalse(_h.Lotse.Any(m => m.Contains("Eintrag") || m.Contains("fehlgeschlagen")), "feed text is never logged");
            Assert.AreEqual(0, _h.Root.Session.Decisions.Count, "a feed decides nothing");
        }

        [UnityTest]
        public IEnumerator ADragOnTheFeedScrollsItWithTheHand()
        {
            yield return _h.GoLive();
            _h.Socket.Receive("{\"type\":\"feed_snapshot\",\"sid\":\"s1\",\"entries\":[" + Entries(0, 30) + "]}");
            yield return Frames(2);
            var rect = _h.Conversation.Feed.Rect;
            var centre = new Vector3(rect.Center.X, rect.Center.Y, rect.Center.Z);
            // the window is scaled (1.45 by default): the hand moves the entries' size in the world
            var up = _h.Root.Workspace.transform.up * _h.Root.Workspace.transform.lossyScale.y;
            // (U13) the log is a few lines high: from near its top, three entries down
            var from = centre + up * (_h.Conversation.Feed.Height * 0.4f);
            _h.Hands.Right = Hand(rayAt: from);
            yield return null;
            for (var i = 0; i <= 7; i++)
            {
                _h.Hands.Right = Hand(1f, rayAt: from - up * (FeedListView.EntryStep * 0.5f * i));
                yield return null;
            }
            _h.Hands.Right = Hand();
            yield return Frames(2);
            Assert.IsFalse(_h.Desk.Scroll.FollowsEnd, "dragged up the list");
            Assert.LessOrEqual(_h.Desk.Scroll.Bottom, 27);
        }

        [UnityTest]
        public IEnumerator NoDragWhileABlockIsShown()
        {
            yield return _h.GoLive();
            _h.Socket.Receive("{\"type\":\"feed_snapshot\",\"sid\":\"s1\",\"entries\":[" + Entries(0, 30) + "]}");
            _h.Socket.Receive(PermOpen("r1", "s1"));
            yield return Frames(2);
            Assert.IsTrue(_h.Desk.BlockShown);
            var rect = _h.Conversation.Feed.Rect;
            var centre = new Vector3(rect.Center.X, rect.Center.Y, rect.Center.Z);
            for (var i = 0; i <= 8; i++)
            {
                _h.Hands.Right = Hand(i == 0 ? 0f : 1f, pinchAt: centre - _h.Root.Workspace.transform.up * (FeedListView.EntryStep * 0.5f * i));
                yield return null;
            }
            _h.Hands.Right = Hand();
            yield return null;
            Assert.IsTrue(_h.Desk.Scroll.FollowsEnd, "a pinch near the block belongs to its hold, not to a drag");
        }

        [UnityTest]
        public IEnumerator AnUnknownAgentSaysNoFeedAndItsErrorIsNoRefusal()
        {
            yield return _h.GoLive();
            // s1's feed and detail are answered; then s2's subscription is the oldest question
            _h.Socket.Receive("{\"type\":\"feed_snapshot\",\"sid\":\"s1\",\"entries\":[]}");
            _h.Socket.Receive("{\"type\":\"agent_detail\",\"sid\":\"s1\",\"title\":\"t\",\"summary\":\"s\",\"recent\":[],\"source\":\"rules\"}");
            yield return Frames(2);
            yield return _h.PokeRow("s2");
            _h.Socket.Receive("{\"type\":\"error\",\"error\":\"unknown-agent\"}");
            yield return Frames(2);
            Assert.IsTrue(_h.Conversation.Feed.Shown.Contains(Texts.FeedUnavailable));
            Assert.AreNotEqual(Texts.Refused("unknown-agent"), _h.Root.Banner.Line, "the feed's error is no refusal banner");
        }

        [UnityTest]
        public IEnumerator TypedKeysAndCodeDigitsAreNeverLogged()
        {
            yield return _h.GoLive(withSession: false);
            Assert.IsNotNull(_h.Root.Pairing, "the pairing panel, waiting for a code");
            const string code = "135792";
            foreach (var digit in code)
                yield return _h.Poke(PokeButton.All.First(b => b.Label == digit.ToString() && b.transform.IsChildOf(_h.Root.Pairing.transform)));
            PokeButton.All.First(b => b.Label == Texts.Ok && b.transform.IsChildOf(_h.Root.Pairing.transform)).Click();
            yield return Frames(2);
            _h.Socket = _h.Sockets.Last();
            _h.Socket.Open();
            Assert.AreEqual("{\"type\":\"hello\",\"code\":\"" + code + "\"}", _h.Socket.Sent.First(), "the code was typed");
            _h.Socket.Receive("{\"type\":\"snapshot\",\"agents\":[" + Agent("s1", "Agent 1", "waiting") + "],\"pending\":[],\"explains\":{}}");
            yield return Frames(3);
            yield return _h.OpenRow("s1");

            yield return _h.Poke(Bar.KeyboardButton);
            const string word = "Quiz";
            foreach (var letter in word) yield return _h.Poke(_h.Root.Desk.Key(letter.ToString().ToUpperInvariant()));
            Assert.AreEqual(word, Bar.TextShown);

            var presses = _h.Lotse.Where(m => m.Contains("pressed")).ToList();
            Assert.AreEqual(code.Length + 1 + word.Length, presses.Count(m => m.StartsWith("[Lotse] key pressed")),
                "every key press (the pad's OK too) is logged without its key");
            foreach (var m in presses.Where(m => m.StartsWith("[Lotse] key pressed")))
                foreach (var c in code + word.ToLowerInvariant() + word.ToUpperInvariant())
                    Assert.AreEqual(-1, m.IndexOf(c), "a key press line names no typed character: " + m);
            Assert.IsFalse(_h.Lotse.Any(m => m.Contains(code) || m.Contains(word)), "neither the code nor the word is in the log");
        }

        [UnityTest]
        public IEnumerator TheDeskKeyboardTypesAndItsSendIsTheExplicitSend()
        {
            yield return _h.GoLive();
            Assert.IsFalse(_h.Root.Desk.Shown);
            yield return _h.Poke(Bar.KeyboardButton);
            Assert.IsTrue(_h.Root.Desk.Shown, "Tastatur: the desk keyboard comes out");
            Assert.AreEqual(KeyboardStatus.Closed, _h.Keyboard.Status);
            var board = _h.Root.Desk.transform;
            Assert.Less(board.position.y, _h.Root.Workspace.transform.position.y, "no desk here: below the workspace");
            var send = _h.Root.Desk.Key(Texts.ReplySend);
            Assert.IsFalse(send.Interactable, "nothing to send yet");
            yield return _h.Poke(_h.Root.Desk.Key("J"));
            yield return _h.Poke(_h.Root.Desk.Key("A"));
            Assert.AreEqual("Ja", Bar.TextShown, "a capital first, then lower case");
            Assert.AreEqual(0, Replies(), "typing never sends");
            yield return _h.Poke(_h.Root.Desk.Key("X"), slow: false);
            Assert.AreEqual("Ja", Bar.TextShown, "brushed, not pressed");
            yield return _h.Poke(Bar.KeyboardButton);
            Assert.IsFalse(_h.Root.Desk.Shown);
            Assert.AreEqual(0, Replies(), "no send on keyboard close");
            yield return _h.Poke(Bar.KeyboardButton);
            yield return _h.Poke(_h.Root.Desk.Key(Texts.ReplySend));
            Assert.AreEqual("{\"type\":\"agent_reply\",\"sid\":\"s1\",\"text\":\"Ja\"}", _h.Socket.Sent.Last());
            Assert.IsFalse(_h.Lotse.Any(m => m.Contains("\"Ja\"") || m.EndsWith(" Ja")), "the text is never logged");
        }

        [UnityTest]
        public IEnumerator ToAnAgentThatWorksSendWaitsAndSaysWhy()
        {
            yield return _h.GoLive();
            yield return _h.PokeRow("s2");
            yield return _h.Poke(Bar.KeyboardButton);
            yield return _h.Poke(_h.Root.Desk.Key("O"));
            yield return _h.Poke(_h.Root.Desk.Key("K"));
            Assert.AreEqual("Ok", Bar.TextShown);
            Assert.AreEqual(Texts.ReplyAgentBusy, Bar.StatusShown);
            Assert.IsFalse(_h.Root.Desk.Key(Texts.ReplySend).Interactable);
            Assert.IsFalse(Bar.SendButton.Interactable);
            _h.Root.Desk.Key(Texts.ReplySend).Click(); // even a forced press is refused
            yield return null;
            Assert.AreEqual(0, Replies(), "B3 delivers only to waiting agents");
        }

        [UnityTest]
        public IEnumerator TheTwoKeyboardsNeverShowTogetherAndClosingOneNeverSends()
        {
            yield return _h.GoLive();
            yield return _h.Poke(Bar.KeyboardButton);
            Assert.IsTrue(_h.Root.Desk.Shown);
            yield return _h.Poke(Bar.EditButton); // U9: there is no microphone button; the text field opens the system keyboard
            Assert.AreEqual(KeyboardStatus.Visible, _h.Keyboard.Status);
            Assert.IsFalse(_h.Root.Desk.Shown, "never both");
            _h.Keyboard.Text = "Diktiert";
            yield return Frames(2);
            _h.Keyboard.Status = KeyboardStatus.Done;
            yield return Frames(2);
            Assert.AreEqual("Diktiert", Bar.TextShown);
            Assert.AreEqual(0, Replies(), "the keyboard's Done never sends");
            yield return _h.Poke(Bar.EditButton);
            Assert.AreEqual(KeyboardStatus.Visible, _h.Keyboard.Status);
            Assert.IsFalse(Bar.KeyboardButton.Interactable, "not while the system keyboard holds the focus");
            Assert.IsFalse(Bar.SendButton.Interactable, "Senden waits while the keyboard is up");
            _h.Keyboard.Status = KeyboardStatus.Canceled;
            yield return Frames(2);
            yield return _h.Poke(Bar.KeyboardButton);
            Assert.IsTrue(_h.Root.Desk.Shown);
            Assert.AreEqual("Diktiert", Bar.TextShown, "a cancelled keyboard keeps the draft");
            Assert.AreEqual(0, Replies());
            yield return _h.Poke(Bar.SendButton);
            Assert.AreEqual("{\"type\":\"agent_reply\",\"sid\":\"s1\",\"text\":\"Diktiert\"}", _h.Socket.Sent.Last(), "only Senden sends");
            _h.Socket.Receive("{\"type\":\"agent_reply_ack\",\"sid\":\"s1\",\"delivered\":true}");
            yield return Frames(2);
            Assert.AreEqual(Texts.ReplyDelivered, Bar.StatusShown);
        }

        [UnityTest]
        public IEnumerator RepliesWaitWhileTheAgentHasAnOpenRequestAndTheDraftIsKept()
        {
            yield return _h.GoLive();
            yield return _h.Poke(Bar.KeyboardButton);
            yield return _h.Poke(_h.Root.Desk.Key("N"));
            Assert.IsTrue(_h.Root.Desk.Shown);
            _h.Socket.Receive(PermOpen("r1", "s1"));
            yield return Frames(2);
            Assert.IsTrue(_h.Desk.BlockShown);
            Assert.IsFalse(_h.Root.Desk.Shown, "the request comes first: the desk keyboard goes");
            Assert.IsNull(_h.Root.Desk.Key("N"), "none of its keys can be pressed");
            Assert.AreEqual(Texts.ReplyAfterRequest, Bar.StatusShown);
            Assert.IsFalse(Bar.SendButton.Interactable);
            Bar.SendButton.Click();
            yield return null;
            Assert.AreEqual(0, Replies());
            Assert.AreEqual(0, _h.Root.Session.Decisions.Count, "and nothing was decided");
            _h.Socket.Receive(PermClosed("r1", "timeout"));
            yield return Frames(3);
            Assert.AreEqual("N", Bar.TextShown, "the draft was kept");
            Assert.IsTrue(Bar.KeyboardButton.Interactable);
        }

        [UnityTest]
        public IEnumerator DemoTheAgentReactsToAReply()
        {
            yield return _h.StartDemo();
            var s = _h.Root.Session;
            // approve every request (the pill selects its agent) until Agent 1 is done and nothing waits
            var guard = 0;
            while (guard++ < 8000)
            {
                var a1 = s.Store.Agents.FirstOrDefault(a => a.Sid == "demo-a1");
                if (a1 != null && a1.Status == "done" && s.Store.Pending.Count == 0) break;
                _h.ReleaseHands();
                if (s.Card == null && s.Store.Pending.Count > 0 && _h.Rows.RowOf(s.Store.Pending[0].Sid) != null)
                    _h.Rows.RowOf(s.Store.Pending[0].Sid).Click();
                if (s.Card != null && s.Mode == CardMode.Hold) yield return _h.PinchHold(s.Card.NeedsTwoHand, s.Card.NeedsTwoHand ? 17 : 10);
                else yield return null;
            }
            _h.ReleaseHands();
            Assert.AreEqual("done", s.Store.Agents.First(a => a.Sid == "demo-a1").Status);
            var decisions = s.Decisions.Count;
            yield return _h.PokeRow("demo-a1");
            Assert.AreEqual("demo-a1", _h.Root.SelectedAgent);
            yield return _h.Poke(Bar.EditButton);
            _h.Keyboard.Text = "Bitte noch Tests für den Badge";
            _h.Keyboard.Status = KeyboardStatus.Done;
            yield return Frames(2);
            Assert.IsTrue(Bar.SendButton.Interactable, "a finished Claude agent can be answered");
            yield return _h.Poke(Bar.SendButton);
            Assert.AreEqual(Texts.ReplyDelivered, Bar.StatusShown, "the demo acknowledges at once");
            yield return Frames(2);
            Assert.IsTrue(_h.Conversation.Feed.Shown.Contains("Bitte noch Tests für den Badge"), "the reply shows in the feed");
            Assert.AreEqual("working", s.Store.Agents.First(a => a.Sid == "demo-a1").Status, "the agent works again");
            Assert.AreEqual(decisions, s.Decisions.Count, "a reply decides nothing");
        }
    }
}
