#nullable enable
using Lotse.App;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    public class PalmPoseTests
    {
        // The head sits at the origin looking along +z; the hand is held up 0.35 m in front of it.
        static readonly Vec3 Head = new Vec3(0, 0, 0);
        const float Z = 0.35f;

        /// <summary>
        /// An open hand, fingers up, in the plane z = Z. `thumbSide` is -1 when the thumb (and index) point to
        /// -x. A right hand with its palm facing away from the viewer has its thumb on the left (-x).
        /// `curl` pulls the finger tips back to the knuckles (a fist).
        /// </summary>
        static HandJoints Hand(float thumbSide, bool curl = false, float zTilt = 0f)
        {
            Vec3 P(float x, float y) => new Vec3(x * thumbSide, y - 0.05f, Z + y * zTilt);
            var tipY = curl ? 0.09f : 0.18f;
            return new HandJoints(
                wrist: P(0, 0),
                indexProximal: P(0.025f, 0.09f), indexTip: P(0.03f, curl ? 0.08f : 0.17f),
                middleProximal: P(0.005f, 0.095f), middleTip: P(0.005f, tipY),
                ringProximal: P(-0.015f, 0.09f), ringTip: P(-0.02f, curl ? 0.08f : 0.17f),
                littleProximal: P(-0.033f, 0.08f));
        }

        // Right hand: palm away from the viewer = thumb on -x. Palm toward the viewer = thumb on +x.
        static HandJoints RightPalmAway(bool curl = false) => Hand(-1f, curl);
        static HandJoints RightPalmToward() => Hand(+1f);
        // Left hand is the mirror image: palm away = thumb on +x.
        static HandJoints LeftPalmAway() => Hand(+1f);
        static HandJoints LeftPalmToward() => Hand(-1f);

        [Test]
        public void RightHandOpenPalmAwayFromHeadIsForward()
        {
            Assert.IsTrue(PalmPose.IsPalmForward(RightPalmAway(), isLeft: false, Head, 0f));
            Assert.Greater(PalmPose.PalmNormal(RightPalmAway(), isLeft: false).Z, 0.9f);
        }

        [Test]
        public void RightHandPalmTowardHeadIsNotForward()
        {
            Assert.IsFalse(PalmPose.IsPalmForward(RightPalmToward(), isLeft: false, Head, 0f));
        }

        [Test]
        public void LeftHandIsTheMirrorImage()
        {
            Assert.IsTrue(PalmPose.IsPalmForward(LeftPalmAway(), isLeft: true, Head, 0f));
            Assert.IsFalse(PalmPose.IsPalmForward(LeftPalmToward(), isLeft: true, Head, 0f));
        }

        [Test]
        public void WrongHandednessFlipsTheNormal()
        {
            // the same joints read as the other hand point the other way: handedness matters
            Assert.IsFalse(PalmPose.IsPalmForward(RightPalmAway(), isLeft: true, Head, 0f));
        }

        [Test]
        public void FistIsNotForward()
        {
            Assert.IsFalse(PalmPose.IsPalmForward(RightPalmAway(curl: true), isLeft: false, Head, 0f));
        }

        [Test]
        public void PinchingHandIsNotForward()
        {
            Assert.IsFalse(PalmPose.IsPalmForward(RightPalmAway(), isLeft: false, Head, 0.5f));
            Assert.IsTrue(PalmPose.IsPalmForward(RightPalmAway(), isLeft: false, Head, PalmPose.MaxPinch));
        }

        [Test]
        public void PalmTiltedFarFromTheViewLineIsNotForward()
        {
            // fingers tilted 60 degrees towards the viewer: the palm faces mostly up
            var tilted = Hand(-1f, zTilt: -1.7f);
            Assert.IsFalse(PalmPose.IsPalmForward(tilted, isLeft: false, Head, 0f));
            // a slight tilt still counts
            Assert.IsTrue(PalmPose.IsPalmForward(Hand(-1f, zTilt: -0.3f), isLeft: false, Head, 0f));
        }

        /// <summary>A right hand lying flat on a desk 0.45 m below the eyes, palm down, fingers pointing away.</summary>
        static HandJoints RightFlatOnDesk()
        {
            Vec3 P(float x, float z) => new Vec3(x, -0.45f, 0.3f + z);
            return new HandJoints(
                wrist: P(0, 0),
                indexProximal: P(-0.025f, 0.09f), indexTip: P(-0.03f, 0.17f),
                middleProximal: P(-0.005f, 0.095f), middleTip: P(-0.005f, 0.18f),
                ringProximal: P(0.015f, 0.09f), ringTip: P(0.02f, 0.17f),
                littleProximal: P(0.033f, 0.08f));
        }

        [Test]
        public void AHandRestingFlatPalmDownIsNotAStop()
        {
            var flat = RightFlatOnDesk();
            Assert.Less(PalmPose.PalmNormal(flat, isLeft: false).Y, -0.9f, "palm faces down");
            Assert.IsFalse(PalmPose.IsPalmForward(flat, isLeft: false, Head, 0f));
        }

        [Test]
        public void ARaisedStopHandAtChestHeightStillCounts()
        {
            // fingers up, palm away, held 0.3 m below the eyes
            Vec3 P(float x, float y) => new Vec3(-x, y - 0.3f, Z);
            var j = new HandJoints(P(0, 0), P(0.025f, 0.09f), P(0.03f, 0.17f), P(0.005f, 0.095f), P(0.005f, 0.18f),
                P(-0.015f, 0.09f), P(-0.02f, 0.17f), P(-0.033f, 0.08f));
            Assert.IsTrue(PalmPose.IsPalmForward(j, isLeft: false, Head, 0f));
        }

        [Test]
        public void FingersPointingSidewaysAreNotAStop()
        {
            // the open hand turned 90 degrees in its plane: fingers point to +x, palm still faces away
            Vec3 P(float x, float y) => new Vec3(y, x, Z);
            var j = new HandJoints(P(0, 0), P(0.025f, 0.09f), P(0.03f, 0.17f), P(0.005f, 0.095f), P(0.005f, 0.18f),
                P(-0.015f, 0.09f), P(-0.02f, 0.17f), P(-0.033f, 0.08f));
            Assert.Greater(System.Math.Abs(PalmPose.PalmNormal(j, isLeft: false).Z), 0.9f);
            Assert.IsFalse(PalmPose.IsPalmForward(j, isLeft: false, Head, 0f));
            Assert.IsFalse(PalmPose.IsPalmForward(j, isLeft: true, Head, 0f));
        }

        [Test]
        public void FingerExtensionNeedsTipWellBeyondTheKnuckle()
        {
            var w = new Vec3(0, 0, 0);
            var k = new Vec3(0, 0.09f, 0);
            Assert.IsTrue(PalmPose.FingerExtended(w, k, new Vec3(0, 0.18f, 0)));
            Assert.IsFalse(PalmPose.FingerExtended(w, k, new Vec3(0, 0.1f, 0)));
            Assert.IsFalse(PalmPose.FingerExtended(w, w, new Vec3(0, 0.18f, 0)), "degenerate knuckle");
        }
    }
}
