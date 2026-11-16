using System;
using Lotse.App;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    /// <summary>U12: the pairing viewfinder follows the head with a soft lag, upright, and stays put while frozen.</summary>
    public class ViewFollowTests
    {
        static readonly Vec3 Head = new Vec3(0f, 1.2f, 0f);
        static readonly Vec3 Ahead = new Vec3(0f, 0f, 1f);

        static Vec3 Dir(float yawDegrees, float pitchDownDegrees = 0f)
        {
            var y = yawDegrees * Math.PI / 180.0;
            var p = pitchDownDegrees * Math.PI / 180.0;
            return new Vec3((float)(Math.Sin(y) * Math.Cos(p)), (float)-Math.Sin(p), (float)(Math.Cos(y) * Math.Cos(p)));
        }

        static void Run(ViewFollow f, float seconds, Vec3 head, Vec3 forward, bool frozen = false)
        {
            for (var t = 0f; t < seconds; t += 1f / 72f) f.Step(1f / 72f, head, forward, frozen);
        }

        [Test]
        public void TheFirstStepPutsItAMetreAheadInTheMiddleOfTheView()
        {
            var f = new ViewFollow();
            Assert.IsFalse(f.Placed);
            f.Step(1f / 72f, Head, Dir(30f, 20f), false);
            Assert.IsTrue(f.Placed);
            Assert.Less(Vec3.Distance(Head + Dir(30f, 20f) * 1f, f.Position), 1e-5f, "at once, on the gaze");
            Assert.AreEqual(30f, f.YawDegrees, 1e-3f, "facing along the gaze's yaw, upright");
        }

        [Test]
        public void ItFollowsWithALagAndArrives()
        {
            var f = new ViewFollow();
            f.Step(1f / 72f, Head, Ahead, false);
            var start = f.Position;
            // the head turns 40 degrees to the right and looks 25 degrees down (to a laptop)
            var gaze = Dir(40f, 25f);
            var target = Head + gaze * ViewFollow.Distance;
            f.Step(1f / 72f, Head, gaze, false);
            Assert.Greater(Vec3.Distance(f.Position, target), 0.9f * Vec3.Distance(start, target), "one frame: hardly moved (a lag, no jump)");
            Assert.Less(Vec3.Distance(f.Position, target), Vec3.Distance(start, target), "but on its way");
            Run(f, ViewFollow.LagSeconds, Head, gaze);
            var after = Vec3.Distance(f.Position, target) / Vec3.Distance(start, target);
            Assert.That(after, Is.InRange(0.25f, 0.45f), "after one time constant about two thirds of the way are done");
            Run(f, 2f, Head, gaze);
            Assert.Less(Vec3.Distance(f.Position, target), 0.005f, "it arrives in the middle of the view");
            Assert.AreEqual(40f, f.YawDegrees, 0.5f, "turned with the head (yaw only: it stays upright when the head looks down)");
            // the head moves: it comes along
            var moved = Head + new Vec3(0.3f, 0f, 0.1f);
            Run(f, 2f, moved, gaze);
            Assert.Less(Vec3.Distance(f.Position, moved + gaze * ViewFollow.Distance), 0.005f);
        }

        [Test]
        public void FrozenItStaysExactlyWhereItIsAndGoesOnFromThereAfterwards()
        {
            var f = new ViewFollow();
            f.Step(1f / 72f, Head, Ahead, false);
            var gaze = Dir(-60f, 10f);
            Run(f, 0.1f, Head, gaze);
            var at = f.Position;
            var yaw = f.YawDegrees;
            Run(f, 3f, Head, gaze, frozen: true);
            Assert.Less(Vec3.Distance(at, f.Position), 1e-7f, "a ray on a control, a pinch, or the question: it does not move");
            Assert.AreEqual(yaw, f.YawDegrees, 1e-6f);
            Run(f, 3f, Head + new Vec3(1f, 0f, 0f), Dir(120f), frozen: true);
            Assert.Less(Vec3.Distance(at, f.Position), 1e-7f, "whatever the head does");
            // released: it eases on from where it stood, no jump
            f.Step(1f / 72f, Head, gaze, false);
            Assert.Less(Vec3.Distance(at, f.Position), 0.06f, "no jump when the freeze ends");
            Run(f, 2f, Head, gaze);
            Assert.Less(Vec3.Distance(f.Position, Head + gaze * ViewFollow.Distance), 0.005f);
        }

        [Test]
        public void TheYawTurnsTheShortWayAndKeepsItsValueWhenLookingStraightDown()
        {
            var f = new ViewFollow();
            f.Step(1f / 72f, Head, Dir(170f), false);
            f.Step(1f / 72f, Head, Dir(-170f), false);
            Assert.That(f.YawDegrees > 170f || f.YawDegrees < -170f, "across 180 degrees it takes the 20 degree way, not the 340 degree one");
            Run(f, 2f, Head, Dir(-170f));
            Assert.AreEqual(-170f, f.YawDegrees, 0.5f);
            Run(f, 3f, Head, new Vec3(0f, -1f, 0f));
            Assert.AreEqual(-170f, f.YawDegrees, 0.5f, "straight down has no yaw: the one it had stays");
            Assert.Less(Vec3.Distance(f.Position, Head + new Vec3(0f, -1f, 0f)), 0.01f, "the position still follows");
            // no direction at all, or no time: nothing happens
            var at = f.Position;
            f.Step(1f / 72f, Head, new Vec3(0f, 0f, 0f), false);
            f.Step(0f, Head, Ahead, false);
            Assert.Less(Vec3.Distance(at, f.Position), 1e-7f);
            f.Reset();
            f.Step(1f / 72f, Head, Ahead, false);
            Assert.Less(Vec3.Distance(Head + Ahead, f.Position), 1e-6f, "after Reset it is placed at once again");
        }
    }
}
