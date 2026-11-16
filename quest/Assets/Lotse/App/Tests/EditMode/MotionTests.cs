#nullable enable
using Lotse.App;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    /// <summary>U2: panels scale and fade in over 180 ms (ease-out), cards rise 3 cm, and nothing moves during a hold.</summary>
    public class MotionTests
    {
        [Test]
        public void EaseOutIsFastFirstAndClamped()
        {
            Assert.AreEqual(0f, Motion.EaseOut(0f), 1e-6f);
            Assert.AreEqual(1f, Motion.EaseOut(1f), 1e-6f);
            Assert.AreEqual(0.875f, Motion.EaseOut(0.5f), 1e-6f, "cubic: 1 - (1 - t)^3");
            Assert.AreEqual(0f, Motion.EaseOut(-1f), 1e-6f);
            Assert.AreEqual(1f, Motion.EaseOut(2f), 1e-6f);
        }

        [Test]
        public void APanelAppearsOverOneHundredEightyMilliseconds()
        {
            Assert.AreEqual(180.0, Motion.AppearMs);
            Assert.AreEqual(0f, Motion.Appear(0, still: false), 1e-6f);
            Assert.AreEqual(0.875f, Motion.Appear(90, still: false), 1e-6f);
            Assert.AreEqual(1f, Motion.Appear(180, still: false), 1e-6f);
            Assert.AreEqual(1f, Motion.Appear(5000, still: false), 1e-6f);

            Assert.AreEqual(Motion.AppearFromScale, Motion.Scale(0f), 1e-6f);
            Assert.AreEqual(1f, Motion.Scale(1f), 1e-6f);
            Assert.AreEqual(0f, Motion.Alpha(0f), 1e-6f);
            Assert.AreEqual(1f, Motion.Alpha(1f), 1e-6f);
        }

        [Test]
        public void ACardRisesThreeCentimetresIntoPlace()
        {
            Assert.AreEqual(-0.03f, Motion.Rise(0f), 1e-6f);
            Assert.AreEqual(0f, Motion.Rise(1f), 1e-6f);
            Assert.Less(Motion.Rise(0.5f), 0f);
        }

        [Test]
        public void NothingMovesDuringAHold()
        {
            Assert.AreEqual(1f, Motion.Appear(0, still: true), "a panel that appears during a hold stands at its end at once");
            Assert.AreEqual(1f, Motion.Scale(Motion.Appear(10, still: true)));
            Assert.AreEqual(0f, Motion.Rise(Motion.Appear(10, still: true)));
        }
    }
}
