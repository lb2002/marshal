using System.Collections;
using System.Collections.Generic;
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
    /// U14: every event plays its sound once (select, a request arriving, the bar cleared, denied, stop, continue, a
    /// done step of the tour); the "Sounds" setting silences all of them; never two sounds within 150 ms.
    /// </summary>
    public class SoundPlayTest
    {
        WorkspaceHarness _h;
        readonly List<Sfx> _played = new List<Sfx>();
        readonly List<double> _at = new List<double>();

        void OnPlayed(Sfx s)
        {
            _played.Add(s);
            _at.Add(Time.timeAsDouble * 1000.0);
        }

        [SetUp]
        public void SetUp()
        {
            _h = new WorkspaceHarness();
            _h.SetUp();
            _played.Clear();
            _at.Clear();
            Sounds.Played += OnPlayed;
        }

        [TearDown]
        public void TearDown()
        {
            Sounds.Played -= OnPlayed;
            _h.TearDown();
        }

        int Count(Sfx s) => _played.Count(x => x == s);

        HandFrame StopHand(float x)
        {
            var head = _h.Camera.transform;
            var at = head.position + head.forward * 0.4f + head.right * x - Vector3.up * 0.1f;
            var f = Hand(0f, pinchAt: at);
            f.PalmValid = true;
            f.PalmCenter = at;
            f.PalmNormal = head.forward;
            f.FingersValid = true;
            f.IndexStraight = f.MiddleStraight = f.RingStraight = f.LittleStraight = 0.98f;
            return f;
        }

        IEnumerator StopGesture()
        {
            _h.Hands.Left = StopHand(-0.2f);
            _h.Hands.Right = StopHand(0.2f);
            yield return Frames(22);
            _h.ReleaseHands();
            yield return Frames(2);
        }

        /// <summary>The whole round: a request arrives, is approved, the next is denied.</summary>
        IEnumerator Round()
        {
            _h.Socket.Receive(PermOpen("r1", "s1"));
            yield return Frames(5);
            yield return _h.PinchHold(false, 12);
            _h.ReleaseHands();
            _h.Socket.Receive(PermClosed("r1", "allow"));
            yield return Frames(20);
            _h.Socket.Receive(PermOpen("r2", "s1"));
            yield return Frames(5);
            if (_h.Root.Desktop.OpenSid != "s1") yield return _h.OpenRow("s1");
            yield return Frames(5);
            yield return _h.Poke(_h.Block.DenyButton);
            _h.Socket.Receive(PermClosed("r2", "deny"));
            yield return Frames(5);
        }

        /// <summary>U14b: every clip is generated at the app's start, none at the first request or approval.</summary>
        [UnityTest]
        public IEnumerator AllClipsAreBuiltAtTheStartAndPlayingBuildsNone()
        {
            Sounds.Unload();
            var before = Sounds.Built;
            yield return _h.GoLive();
            var generated = System.Enum.GetValues(typeof(Sfx)).Cast<Sfx>().Count(s => s != Sfx.Select && s != Sfx.Key);
            Assert.AreEqual(before + generated, Sounds.Built, "every generated clip is made when the app starts");
            var built = Sounds.Built;
            yield return Frames(5);
            _played.Clear();
            _h.Socket.Receive(PermOpen("r1", "s1"));
            yield return Frames(20);
            CollectionAssert.Contains(_played, Sfx.Request);
            yield return _h.PinchHold(false, 12);
            _h.ReleaseHands();
            CollectionAssert.Contains(_played, Sfx.Cleared);
            Assert.AreEqual(built, Sounds.Built, "the first request and the first approval generate nothing");
            foreach (Sfx s in System.Enum.GetValues(typeof(Sfx))) Assert.IsNotNull(Sounds.Clip(s));
            Assert.AreEqual(built, Sounds.Built);
        }

        /// <summary>U14b: with sounds off the clips are built all the same; switching sounds on later costs nothing.</summary>
        [UnityTest]
        public IEnumerator SoundsOffStillBuildsTheClipsAndPlaysNothing()
        {
            Sounds.Unload();
            yield return _h.GoLive();
            Chime.On = false; // the "Sounds" setting
            var built = Sounds.Built;
            yield return Frames(5);
            _played.Clear();
            _h.Socket.Receive(PermOpen("r1", "s1"));
            yield return Frames(20);
            Assert.IsEmpty(_played, "sounds off: nothing plays");
            Assert.AreEqual(built, Sounds.Built);
            Chime.On = true;
            Sounds.Play(Sfx.Stop, Vector3.zero);
            yield return Frames(3);
            CollectionAssert.Contains(_played, Sfx.Stop);
            Assert.AreEqual(built, Sounds.Built, "nothing is generated when sounds are switched on");
        }

        [UnityTest]
        public IEnumerator EachEventPlaysItsSoundOnce()
        {
            yield return _h.GoLive();
            yield return Frames(5);
            _played.Clear();
            // a request arrives: two soft rising notes, once
            _h.Socket.Receive(PermOpen("r1", "s1"));
            yield return Frames(20);
            CollectionAssert.AreEqual(new[] { Sfx.Request }, _played, "a request arrives: its sound, once, however long it waits");
            Assert.AreSame(Sounds.Clip(Sfx.Request), Sounds.Clip(Sfx.Request), "one generated clip, made once");
            // hold progress: no sound; the bar cleared: one note
            _played.Clear();
            yield return _h.PinchHold(false, 6);
            Assert.IsEmpty(_played, "a hold in progress makes no sound");
            yield return Frames(6);
            _h.ReleaseHands();
            CollectionAssert.AreEqual(new[] { Sfx.Cleared }, _played, "the bar cleared: its note, once");
            _h.Socket.Receive(PermClosed("r1", "allow"));
            yield return Frames(20);
            CollectionAssert.AreEqual(new[] { Sfx.Cleared }, _played, "the row closing adds nothing");
            // denied: the lower note instead of the tick of the button
            _h.Socket.Receive(PermOpen("r2", "s1"));
            yield return Frames(20);
            if (_h.Root.Desktop.OpenSid != "s1") yield return _h.OpenRow("s1");
            yield return Frames(5);
            _played.Clear();
            yield return _h.Poke(_h.Block.DenyButton);
            yield return Frames(5);
            CollectionAssert.AreEqual(new[] { Sfx.Denied }, _played, "\"Deny\": the deny note, once, and not the tick as well");
            // a select: the tick
            _played.Clear();
            yield return _h.Poke(_h.Root.Workspace.Header.MenuButton);
            yield return Frames(3);
            CollectionAssert.AreEqual(new[] { Sfx.Select }, _played, "a select: the tick");
        }

        [UnityTest]
        public IEnumerator StopAndContinuePlayTheirDoubleNotes()
        {
            _h.OpenConversation = false;
            yield return _h.StartDemo();
            yield return Frames(5);
            _played.Clear();
            yield return StopGesture();
            Assert.IsTrue(_h.Root.Session.Halted);
            Assert.AreEqual(1, Count(Sfx.Stop), "stop fired: its sound, once");
            yield return Frames(10);
            _played.Clear();
            yield return _h.Poke(_h.Root.Workspace.Halt.ContinueButton);
            yield return Frames(3);
            CollectionAssert.AreEqual(new[] { Sfx.Select }, _played, "\"Continue\" asks first: only the tick");
            _played.Clear();
            yield return _h.Poke(PokeButton.All.First(b => b.Label == Texts.Yes));
            yield return Frames(3);
            Assert.IsFalse(_h.Root.Session.Halted);
            CollectionAssert.AreEqual(new[] { Sfx.Continue }, _played, "released: the stop sound reversed, once");
        }

        [UnityTest]
        public IEnumerator ADoneStepOfTheTourPlaysTheTick()
        {
            yield return _h.Start();
            PokeButton.All.First(b => b.Label == Tour.StartButton).Click();
            yield return Frames(5);
            _played.Clear();
            _h.Hands.Right = Hand(1f); // a pinch ends the first step
            yield return Frames(3);
            Assert.AreEqual("open", _h.Root.Tour.Current.Id);
            Assert.AreEqual(1, Count(Sfx.Select), "the step is done: the select tick");
            Assert.AreEqual(0, _played.Count(s => s != Sfx.Select && s != Sfx.Request));
        }

        [UnityTest]
        public IEnumerator ABurstOfRequestsMakesOneSound()
        {
            yield return _h.GoLive();
            yield return Frames(5);
            _played.Clear();
            _h.Socket.Receive(PermOpen("r1", "s1"));
            _h.Socket.Receive(PermOpen("r2", "s2"));
            _h.Socket.Receive(PermOpen("r3", "s3"));
            yield return Frames(2);
            _h.Socket.Receive(PermOpen("r4", "s2"));
            yield return Frames(3);
            _h.Socket.Receive(PermOpen("r5", "s3"));
            yield return Frames(20);
            Assert.AreEqual(1, Count(Sfx.Request), "five requests within a second: one sound");
            _h.Socket.Receive(PermOpen("r6", "s2"));
            yield return Frames(5);
            Assert.AreEqual(2, Count(Sfx.Request), "a later request sounds again");
        }

        [UnityTest]
        public IEnumerator TheSoundsSettingSilencesAll()
        {
            yield return _h.GoLive();
            yield return _h.OpenSettings();
            yield return _h.Poke(_h.Root.Settings.SoundOffButton);
            yield return Frames(3);
            Assert.IsFalse(Chime.On, "sounds are off");
            typeof(AppRoot).GetMethod("CloseSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(_h.Root, null);
            yield return Frames(3);
            Assert.IsFalse(_h.Root.Settings.IsOpen);
            if (_h.Root.Desktop.OpenSid != "s1") yield return _h.OpenRow("s1");
            _played.Clear();
            yield return Round();
            Assert.AreEqual(2, _h.Decisions(), "the round happened: one approval, one deny");
            yield return _h.Poke(_h.Root.Workspace.Header.MenuButton);
            yield return Frames(3);
            Assert.IsEmpty(_played, "with sounds off nothing plays: no tick, no request, no cleared, no denied");
            // and on again they are back
            Chime.On = true;
            _h.Socket.Receive(PermOpen("r9", "s2"));
            yield return Frames(5);
            Assert.AreEqual(1, Count(Sfx.Request));
        }

        [UnityTest]
        public IEnumerator NeverTwoSoundsWithin150Ms()
        {
            yield return _h.GoLive();
            yield return Frames(5);
            _played.Clear();
            _at.Clear();
            // a select in every frame (frames of 100 ms), with requests arriving in between
            var menu = _h.Root.Workspace.Header.MenuButton;
            for (var i = 0; i < 12; i++)
            {
                menu.Click();
                if (i == 3) _h.Socket.Receive(PermOpen("r1", "s2"));
                if (i == 7) _h.Socket.Receive(PermOpen("r2", "s3"));
                yield return null;
            }
            Assert.That(_played.Count, Is.InRange(4, 7), "about every other frame");
            for (var i = 1; i < _at.Count; i++)
                Assert.GreaterOrEqual(_at[i] - _at[i - 1], SoundMixer.GapMs - 1e-3, "sounds " + (i - 1) + " and " + i + " are at least 150 ms apart");
        }
    }
}
