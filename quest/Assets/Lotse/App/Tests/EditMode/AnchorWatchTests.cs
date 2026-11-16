#nullable enable
using Lotse.App;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    public class AnchorWatchTests
    {
        static readonly Vec3 Head = new Vec3(0, 1.6f, 0);

        [Test]
        public void ReadyAfterTwoTrackedFramesThatMovedLessThanTwoCentimetres()
        {
            var w = new AnchorWatch();
            Assert.IsFalse(w.Ready(0, Head, tracked: true), "first sight: nothing to compare");
            Assert.IsFalse(w.Ready(14, new Vec3(0, 1.6f, 0.005f), true), "one stable frame");
            Assert.IsTrue(w.Ready(28, new Vec3(0, 1.6f, 0.01f), true), "two stable frames");
        }

        [Test]
        public void MovingOrUntrackedFramesRestartTheCount()
        {
            var w = new AnchorWatch();
            w.Ready(0, Head, true);
            w.Ready(14, Head, true);
            Assert.IsFalse(w.Ready(28, new Vec3(0, 1.6f, 0.05f), true), "moved 5 cm");
            Assert.IsFalse(w.Ready(42, new Vec3(0, 1.6f, 0.05f), true));
            Assert.IsFalse(w.Ready(56, new Vec3(0, 1.6f, 0.05f), false), "untracked");
            Assert.IsFalse(w.Ready(70, new Vec3(0, 1.6f, 0.05f), true));
            Assert.IsFalse(w.Ready(84, new Vec3(0, 1.6f, 0.05f), true));
            Assert.IsTrue(w.Ready(98, new Vec3(0, 1.6f, 0.05f), true));
        }

        [Test]
        public void GivesUpWaitingAfterThreeSeconds()
        {
            var w = new AnchorWatch();
            Assert.IsFalse(w.Ready(1000, Head, false));
            Assert.IsFalse(w.Ready(3999, Head, false));
            Assert.IsTrue(w.Ready(1000 + AnchorWatch.TimeoutMs, Head, false));
        }

        [Test]
        public void RecaptureWhenTheAnchorIsFarFromTheHead()
        {
            Assert.IsFalse(AnchorWatch.NeedsRecapture(new Vec3(0, 1.35f, 0.6f), Head));
            Assert.IsFalse(AnchorWatch.NeedsRecapture(new Vec3(0, 1.6f, 1.49f), Head));
            Assert.IsTrue(AnchorWatch.NeedsRecapture(new Vec3(0, 1.6f, 1.51f), Head));
            Assert.IsTrue(AnchorWatch.NeedsRecapture(new Vec3(0, 0f, 0.6f), new Vec3(0, 1.8f, 0)), "below the floor after a recenter");
        }
    }
}
