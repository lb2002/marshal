#nullable enable
using Lotse.App;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    public class HandMappingTests
    {
        [Test]
        public void TrackedConfidentHandPassesItsValuesThrough()
        {
            var s = HandMapping.ToSample(isTracked: true, systemGesture: false, highConfidence: true, indexPinch: 0.9f, palmForward: true);
            Assert.IsTrue(s.Tracked);
            Assert.IsTrue(s.HighConfidence);
            Assert.AreEqual(0.9f, s.PinchStrength);
            Assert.IsTrue(s.PalmForward);
            Assert.IsTrue(s.Usable);
        }

        [Test]
        public void SystemGestureMakesTheHandUntracked()
        {
            var s = HandMapping.ToSample(true, systemGesture: true, highConfidence: true, indexPinch: 1f, palmForward: true);
            Assert.IsFalse(s.Tracked);
            Assert.IsFalse(s.Usable);
            Assert.AreEqual(0f, s.PinchStrength);
            Assert.IsFalse(s.PalmForward);
        }

        [Test]
        public void UntrackedHandHasNoPinchNoPalmNoConfidence()
        {
            var s = HandMapping.ToSample(false, false, highConfidence: true, indexPinch: 1f, palmForward: true);
            Assert.IsFalse(s.Tracked);
            Assert.IsFalse(s.HighConfidence);
            Assert.AreEqual(0f, s.PinchStrength);
            Assert.IsFalse(s.PalmForward);
        }

        [Test]
        public void LowConfidenceIsNotUsableAndHasNoPalm()
        {
            var s = HandMapping.ToSample(true, false, highConfidence: false, indexPinch: 1f, palmForward: true);
            Assert.IsTrue(s.Tracked);
            Assert.IsFalse(s.Usable);
            Assert.IsFalse(s.PalmForward);
        }

        [TestCase(true, false, true, true, true)]
        [TestCase(true, false, false, true, false)]  // low confidence: no poking
        [TestCase(true, true, true, true, false)]    // system gesture
        [TestCase(false, false, true, true, false)]  // not tracked
        [TestCase(true, false, true, false, false)]  // bones not ready
        public void TipNeedsATrackedConfidentHandWithBones(bool tracked, bool systemGesture, bool high, bool bonesReady, bool expected)
        {
            var s = HandMapping.ToSample(tracked, systemGesture, high, 0f, false);
            Assert.AreEqual(expected, HandMapping.TipUsable(s, bonesReady));
        }

        [TestCase(true, false, true, true, true)]
        [TestCase(true, false, true, false, false)]  // the system's aim pose is not valid
        [TestCase(true, false, false, true, false)]  // low confidence
        [TestCase(true, true, true, true, false)]    // system gesture (palm up and pinch): the OS's, never ours
        [TestCase(false, false, true, true, false)]  // not tracked
        public void TheRayNeedsAUsableHandAndAValidAimPose(bool tracked, bool systemGesture, bool high, bool poseValid, bool expected)
        {
            var s = HandMapping.ToSample(tracked, systemGesture, high, 0f, false);
            Assert.AreEqual(expected, HandMapping.RayUsable(s, poseValid));
        }
    }
}
