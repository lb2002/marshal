#nullable enable
using System.Collections.Generic;
using Lotse.App;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    /// <summary>U5b, the three-tap setup (the dwell, Back, Cancel, a rejected shape) and what is kept about the display.</summary>
    public class ScreenSetupTests
    {
        static readonly Vec3 Head = new Vec3(0f, 1.2f, 0f);
        static readonly Vec3 TL = new Vec3(-0.3f, 1.37f, 0.8f);
        static readonly Vec3 TR = new Vec3(0.3f, 1.37f, 0.8f);
        static readonly Vec3 BR = new Vec3(0.3f, 1.03f, 0.8f);

        sealed class Store : IKeyValueStore
        {
            public readonly Dictionary<string, string> D = new Dictionary<string, string>();
            public string? Get(string key) => D.TryGetValue(key, out var v) ? v : null;
            public void Set(string key, string value) => D[key] = value;
            public void Delete(string key) => D.Remove(key);
        }

        /// <summary>The fingertip rests at `at` for `ms` (frames of 100 ms), then moves away 5 cm.</summary>
        static SetupEvent Dwell(ScreenSetupFlow f, ref double now, Vec3 at, double ms = 900, bool left = false)
        {
            var last = SetupEvent.None;
            for (var t = 0.0; t <= ms; t += 100)
            {
                var e = left ? f.Step(now, at, null, Head) : f.Step(now, null, at, Head);
                if (e != SetupEvent.None) last = e;
                now += 100;
            }
            var away = at + new Vec3(0f, 0f, -0.05f);
            f.Step(now, left ? away : (Vec3?)null, left ? null : away, Head);
            now += 100;
            return last;
        }

        [Test]
        public void ATapIsAFingertipStillWithinOneCentimetreForEightTenthsOfASecond()
        {
            var d = new TapDwell();
            Assert.IsNull(d.Step(0, TL));
            Assert.IsNull(d.Step(400, TL + new Vec3(0.006f, 0f, 0f)), "within 1 cm: the dwell goes on");
            Assert.AreEqual(0.5f, d.Progress, 0.01f, "the ring is half full");
            Assert.IsNull(d.Step(700, TL));
            Assert.IsNotNull(d.Step(800, TL), "0.8 s: a tap");
            Assert.IsNull(d.Step(2000, TL), "holding still never taps twice");
            Assert.IsNull(d.Step(2100, TL + new Vec3(0.015f, 0f, 0f)), "1.5 cm: not yet re-armed");
            // moved more than 1 cm: the dwell starts again
            var e = new TapDwell();
            e.Step(0, TL);
            e.Step(500, TL + new Vec3(0.02f, 0f, 0f));
            Assert.IsNull(e.Step(1000, TL + new Vec3(0.02f, 0f, 0f)), "restarted at 500 ms");
            Assert.IsNotNull(e.Step(1300, TL + new Vec3(0.02f, 0f, 0f)));
            // no fingertip: nothing, and the ring is empty
            Assert.IsNull(e.Step(1400, null));
            Assert.AreEqual(0f, e.Progress);
        }

        [Test]
        public void ThreeTapsWithEitherHandMeasureTheDisplayThenTheSideIsPicked()
        {
            var f = new ScreenSetupFlow();
            f.Begin();
            var now = 0.0;
            Assert.AreEqual(SetupEvent.Tapped, Dwell(f, ref now, TL));
            Assert.AreEqual(SetupEvent.Tapped, Dwell(f, ref now, TR, left: true), "either hand");
            Assert.AreEqual(SetupEvent.Measured, Dwell(f, ref now, BR));
            Assert.AreEqual(SetupPhase.Side, f.Phase);
            Assert.AreEqual(0.6f, f.Screen.Width, 1e-3f);
            Assert.AreEqual(0.34f, f.Screen.Height, 1e-3f);
            f.Pick(DockSide.Left);
            Assert.AreEqual(SetupPhase.Done, f.Phase);
            Assert.AreEqual(DockSide.Left, f.Side);
            Assert.IsFalse(f.Active);
        }

        [Test]
        public void BackRemovesTheLastPointAndCancelLeaves()
        {
            var f = new ScreenSetupFlow();
            f.Begin();
            var now = 0.0;
            Dwell(f, ref now, TL);
            Dwell(f, ref now, new Vec3(0.5f, 1.6f, 0.8f)); // a wrong second corner
            f.Back();
            Assert.AreEqual(1, f.Points.Count, "the last point goes");
            Dwell(f, ref now, TR);
            Assert.AreEqual(SetupEvent.Measured, Dwell(f, ref now, BR));
            f.Back();
            Assert.AreEqual(SetupPhase.Corners, f.Phase, "back from the side choice: the third corner again");
            Assert.AreEqual(2, f.Points.Count);
            f.Cancel();
            Assert.AreEqual(SetupPhase.Cancelled, f.Phase);
            Assert.IsFalse(f.Active);
            Assert.AreEqual(SetupEvent.None, f.Step(now, TL, TL, Head), "nothing after Cancel");
        }

        [Test]
        public void AShapeThatIsNoDisplayIsDroppedAndAskedAgain()
        {
            var f = new ScreenSetupFlow();
            f.Begin();
            var now = 0.0;
            Dwell(f, ref now, TL);
            Dwell(f, ref now, TL + new Vec3(0.08f, 0f, 0f));
            Assert.AreEqual(SetupEvent.Rejected, Dwell(f, ref now, TL + new Vec3(0.08f, -0.05f, 0f)), "8 by 5 cm is no display");
            Assert.AreEqual(ScreenRejection.TooSmall, f.LastRejection);
            Assert.AreEqual(0, f.Points.Count, "the points are dropped");
            Assert.AreEqual(SetupPhase.Corners, f.Phase, "asked again");
            Dwell(f, ref now, TL);
            Dwell(f, ref now, TR);
            Assert.AreEqual(SetupEvent.Measured, Dwell(f, ref now, BR));
        }

        [Test]
        public void TheDisplaysSizeAndSideAreKeptAndAnythingUnreadableIsNothing()
        {
            var s = new Store();
            var r = new ScreenRecord(s);
            Assert.IsFalse(r.TryLoad(out _, out _, out _));
            r.Save(0.6f, 0.34f, DockSide.Left);
            Assert.IsTrue(r.TryLoad(out var w, out var h, out var side));
            Assert.AreEqual(0.6f, w, 1e-4f);
            Assert.AreEqual(0.34f, h, 1e-4f);
            Assert.AreEqual(DockSide.Left, side);
            StringAssert.DoesNotContain(",", s.D[ScreenRecord.Key], "invariant culture");
            foreach (var bad in new[] { "x", "0.6;0.34", "0.6;0.34;Sideways", "5;0.34;Left", "0.6;0.01;Left" })
            {
                s.D[ScreenRecord.Key] = bad;
                Assert.IsFalse(r.TryLoad(out _, out _, out _), bad);
            }
            Assert.IsFalse(r.Offered);
            r.Offered = true;
            Assert.IsTrue(new ScreenRecord(s).Offered, "the offer is made once");
        }
    }
}
