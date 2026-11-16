#nullable enable
using Lotse.App;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    public class DeskTouchTests
    {
        const float DeskY = 0.75f;
        static readonly Vec3 Up = new Vec3(0, 1, 0);
        static readonly DeskPlane Desk = new DeskPlane(DeskY, Up);

        static Vec3 TipAt(float gap, float x = 0.1f, float z = 0.45f) => new Vec3(x, DeskY + gap, z);

        [Test]
        public void ATipFromOneCentimetreInsideToThreeCentimetresAboveTheDeskTouchesIt()
        {
            Assert.IsTrue(DeskTouch.IsTouch(TipAt(0f), Desk));
            Assert.IsTrue(DeskTouch.IsTouch(TipAt(0.029f), Desk));
            Assert.IsTrue(DeskTouch.IsTouch(TipAt(-0.009f), Desk), "tracking puts the tip a little inside the desk");
            Assert.IsFalse(DeskTouch.IsTouch(TipAt(0.031f), Desk), "3.1 cm above");
            Assert.IsFalse(DeskTouch.IsTouch(TipAt(-0.011f), Desk), "1.1 cm inside: not this surface");
            Assert.IsFalse(DeskTouch.IsTouch(TipAt(0.2f), Desk), "the finger hovers");
        }

        [Test]
        public void OnlyRoughlyHorizontalSurfacesCount()
        {
            Assert.IsFalse(DeskTouch.IsTouch(TipAt(0.01f), new DeskPlane(DeskY, new Vec3(0.75f, 0.66f, 0))), "tilted about 49 degrees");
            Assert.IsTrue(DeskTouch.IsTouch(TipAt(0.01f), new DeskPlane(DeskY, new Vec3(0.5f, 0.86f, 0))), "tilted 30 degrees");
            Assert.IsTrue(DeskTouch.IsTouch(TipAt(0.01f), new DeskPlane(DeskY, new Vec3(0, 5, 0))), "normalised first");
            Assert.IsFalse(DeskTouch.IsTouch(TipAt(0.01f), new DeskPlane(DeskY, new Vec3(0, 0, 0))), "no normal");
        }

        [Test]
        public void TheTouchIsAcceptedAfterFourHundredMillisecondsAtTheTipOnThePlane()
        {
            var t = new DeskTouch();
            Assert.IsFalse(t.Update(1000, TipAt(0.01f), Desk));
            Assert.AreEqual(0f, t.Progress, 1e-4f);
            Assert.IsFalse(t.Update(1200, TipAt(0.01f), Desk));
            Assert.AreEqual(0.5f, t.Progress, 1e-4f);
            Assert.IsFalse(t.Update(1399, TipAt(0.01f), Desk), "not yet");
            Assert.IsTrue(t.Update(1000 + DeskTouch.HoldMs, TipAt(0.012f, x: 0.11f), Desk));
            Assert.AreEqual(0.11f, t.Accepted.Point.X, 1e-5f, "the tip's x of the accepting frame");
            Assert.AreEqual(DeskY, t.Accepted.Point.Y, 1e-5f, "on the plane, not at the tip");
            Assert.AreEqual(0.45f, t.Accepted.Point.Z, 1e-5f);
            Assert.AreEqual(1f, t.Accepted.Normal.Y, 1e-5f);
        }

        [Test]
        public void LiftingTheFingerLosingThePlaneOrLosingTheTipStartsTheHoldAgain()
        {
            var t = new DeskTouch();
            t.Update(0, TipAt(0.01f), Desk);
            t.Update(300, TipAt(0.01f), Desk);
            Assert.IsFalse(t.Update(350, TipAt(0.05f), Desk), "lifted 5 cm");
            Assert.AreEqual(0f, t.Progress);
            t.Update(400, TipAt(0.01f), Desk);
            Assert.IsFalse(t.Update(700, TipAt(0.01f), null), "no plane this frame");
            t.Update(750, TipAt(0.01f), Desk);
            Assert.IsFalse(t.Update(1000, null, Desk), "the tip is not usable (TipValid false)");
            Assert.IsFalse(t.Update(1050, TipAt(0.01f), Desk));
            Assert.IsFalse(t.Update(1449, TipAt(0.01f), Desk));
            Assert.IsTrue(t.Update(1450, TipAt(0.01f), Desk));
        }

        [Test]
        public void AfterRearmTheFingerAtTheButtonDoesNotCountUntilItMovedEightCentimetres()
        {
            var t = new DeskTouch();
            t.Rearm(0);
            var atButton = TipAt(0.02f);
            for (var ms = 0; ms < 590; ms += 50)
                Assert.IsFalse(t.Update(ms, atButton, Desk), $"still at the button, {ms} ms");
            Assert.AreEqual(0f, t.Progress, "no hold runs while rearming");
            var moved = TipAt(0.01f, x: 0.1f + 0.081f);
            Assert.IsFalse(t.Update(560, moved, Desk), "8.1 cm away before the 0.6 s: the hold starts here");
            Assert.IsFalse(t.Update(560 + DeskTouch.HoldMs - 1, moved, Desk));
            Assert.IsTrue(t.Update(560 + DeskTouch.HoldMs, moved, Desk), "counted from the move, not from 0.6 s");
        }

        [Test]
        public void AfterRearmTheTouchCountsAgainAfterSixHundredMilliseconds()
        {
            var t = new DeskTouch();
            t.Rearm(1000);
            var atButton = TipAt(0.02f);
            Assert.IsFalse(t.Update(1000, atButton, Desk));
            Assert.IsFalse(t.Update(1000 + DeskTouch.RearmMs - 1, TipAt(0.02f, x: 0.15f), Desk), "5 cm is not enough");
            Assert.IsFalse(t.Update(1000 + DeskTouch.RearmMs, atButton, Desk), "released: the hold starts now");
            Assert.IsFalse(t.Update(1000 + DeskTouch.RearmMs + DeskTouch.HoldMs - 1, atButton, Desk));
            Assert.IsTrue(t.Update(1000 + DeskTouch.RearmMs + DeskTouch.HoldMs, atButton, Desk));
        }

        [Test]
        public void ResetForgetsAHoldInProgress()
        {
            var t = new DeskTouch();
            t.Update(0, TipAt(0.01f), Desk);
            t.Reset();
            Assert.IsFalse(t.Update(400, TipAt(0.01f), Desk), "a fresh hold starts here");
            Assert.IsTrue(t.Update(800, TipAt(0.01f), Desk));
        }
    }
}
