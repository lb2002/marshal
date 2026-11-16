#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Lotse.App;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    public class DeskSurfaceTests
    {
        const float DeskY = 0.75f;
        static readonly Vec3 Head = new Vec3(0f, 1.2f, 0f);
        static readonly Vec3 Tip = new Vec3(0.1f, 0.765f, 0.45f);
        static readonly Vec3 Up = new Vec3(0, 1, 0);
        static readonly Vec3 Pointing = new Vec3(0.1f, -0.7f, 0.45f).Normalized; // away from the user and down
        static readonly IReadOnlyList<Vec3> NoJoints = new Vec3[0];

        static IReadOnlyList<Vec3> Origins => DeskSurface.RayOrigins(Tip, Head, Pointing);

        /// <summary>A hit at height y under ray origin `n`.</summary>
        static DeskHit At(float y, int n = 0, Vec3? normal = null)
        {
            var o = Origins[n];
            return new DeskHit(new Vec3(o.X, y, o.Z), normal ?? Up);
        }

        static bool Plane(IReadOnlyList<DeskHit> hits, out DeskPlane plane, IReadOnlyList<Vec3>? joints = null) =>
            DeskSurface.TryPlane(Tip, Head, hits, joints ?? NoJoints, out plane);

        static Vec3 Flat(Vec3 v) => new Vec3(v.X, 0f, v.Z);

        static float AngleDeg(Vec3 a, Vec3 b) =>
            (float)(Math.Acos(Math.Max(-1f, Math.Min(1f, Vec3.Dot(a.Normalized, b.Normalized)))) * 180.0 / Math.PI);

        [Test]
        public void EightRaysARingOfSixAtFiveCentimetresAndTwoAheadOfTheFingertip()
        {
            var origins = Origins;
            Assert.AreEqual(8, origins.Count);
            foreach (var o in origins)
                Assert.AreEqual(Tip.Y + DeskSurface.RayStartAbove, o.Y, 1e-5f, "5 cm above the tip");

            var ahead = Flat(Pointing).Normalized;
            var ring = origins.Take(6).Select(o => Flat(o - Tip)).ToList();
            foreach (var r in ring) Assert.AreEqual(DeskSurface.RingRadius, r.Length, 1e-5f, "ring radius 5 cm");
            var angles = ring.Select(r => AngleDeg(r, ahead)).OrderBy(a => a).ToList();
            CollectionAssert.AreEqual(new[] { 30f, 30f, 90f, 90f, 150f, 150f }, angles.Select(a => (float)Math.Round(a)).ToList(),
                "60 degrees apart, none straight along or straight back down the finger");

            var a1 = Flat(origins[6] - Tip);
            var a2 = Flat(origins[7] - Tip);
            Assert.AreEqual(0.03f, a1.Length, 1e-5f);
            Assert.AreEqual(0.06f, a2.Length, 1e-5f);
            Assert.Less(AngleDeg(a1, ahead), 0.1f, "3 cm ahead along the finger");
            Assert.Less(AngleDeg(a2, ahead), 0.1f, "6 cm ahead along the finger");
        }

        [Test]
        public void AFingerPointingStraightDownLooksAwayFromTheUser()
        {
            var origins = DeskSurface.RayOrigins(Tip, Head, new Vec3(0, -1, 0));
            var away = Flat(Tip - Head).Normalized;
            Assert.Less(AngleDeg(Flat(origins[7] - Tip), away), 0.1f);
            var unknown = DeskSurface.RayOrigins(Tip, Head, new Vec3(0, 0, 0));
            Assert.Less(AngleDeg(Flat(unknown[7] - Tip), away), 0.1f, "no finger direction known");
        }

        /// <summary>
        /// A right hand pointing at the desk: index extended 45 degrees down, tip 1.5 cm above the desk, the other
        /// fingers curled into the palm, thumb tucked. Joint positions in a local frame (x right, y up, z along the
        /// finger), in metres, from a typical OpenXR hand pose.
        /// </summary>
        static List<Vec3> PointingHand(Vec3 tip, Vec3 forward)
        {
            var z = Flat(forward).Normalized;
            var x = new Vec3(z.Z, 0f, -z.X); // right of the finger direction
            Vec3 J(float lx, float ly, float lz) => tip + x * lx + new Vec3(0f, ly, 0f) + z * lz;
            var joints = new List<Vec3>
            {
                J(0.02f, 0.075f, -0.10f), // palm
                J(0.015f, 0.085f, -0.15f), // wrist
                // thumb: metacarpal, proximal, distal, tip
                J(-0.03f, 0.065f, -0.12f), J(-0.035f, 0.05f, -0.085f), J(-0.03f, 0.04f, -0.06f), J(-0.022f, 0.035f, -0.045f),
                // index: metacarpal, proximal, intermediate, distal, tip
                J(0.005f, 0.08f, -0.13f), J(0f, 0.064f, -0.064f), J(0f, 0.033f, -0.033f), J(0f, 0.0156f, -0.0156f), J(0f, 0f, 0f),
            };
            // middle, ring, little: curled (metacarpal, proximal, intermediate, distal, tip)
            foreach (var fx in new[] { 0.021f, 0.04f, 0.057f })
            {
                joints.Add(J(fx * 0.7f, 0.08f, -0.13f));
                joints.Add(J(fx, 0.062f, -0.062f));
                joints.Add(J(fx, 0.028f, -0.047f));
                joints.Add(J(fx, 0.022f, -0.068f));
                joints.Add(J(fx, 0.035f, -0.078f));
            }
            return joints;
        }

        /// <summary>What a depth ray straight down sees: the top of a finger (about 1 cm thick) passing under it, else the desk.</summary>
        static DeskHit Cast(Vec3 origin, IReadOnlyList<Vec3> joints)
        {
            var top = float.NegativeInfinity;
            foreach (var j in joints)
                if (j.Y < origin.Y && Flat(j - origin).Length < 0.012f) top = Math.Max(top, j.Y + 0.008f);
            return new DeskHit(new Vec3(origin.X, top > DeskY ? top : DeskY, origin.Z), Up);
        }

        [Test]
        public void ARealPointingHandOverAFlatDeskLeavesAtLeastThreeRays()
        {
            foreach (var forwardDeg in new[] { -30f, 0f, 30f })
            {
                var away = Flat(Tip - Head).Normalized;
                var rad = forwardDeg * Math.PI / 180.0;
                var forward = new Vec3((float)(away.X * Math.Cos(rad) - away.Z * Math.Sin(rad)), -1f,
                    (float)(away.X * Math.Sin(rad) + away.Z * Math.Cos(rad)));
                var joints = PointingHand(Tip, forward);
                var origins = DeskSurface.RayOrigins(Tip, Head, forward);
                var hits = origins.Select(o => Cast(o, joints)).ToList();
                var clear = hits.Count(h => h.Point.Y == DeskY && joints.All(j => Vec3.Distance(h.Point, j) >= DeskSurface.JointClearance));
                Assert.GreaterOrEqual(clear, 3, $"desk hits clear of the hand, finger turned {forwardDeg} degrees");
                Assert.IsTrue(DeskSurface.TryPlane(Tip, Head, hits, joints, out var plane), $"a plane, finger turned {forwardDeg} degrees");
                Assert.AreEqual(DeskY, plane.Y, 1e-5f);
                Assert.IsTrue(DeskTouch.IsTouch(Tip, plane), "the resting tip touches it");
            }
        }

        [Test]
        public void HitsAboveTheTipAreTheHandNotTheDesk()
        {
            var hits = new List<DeskHit> { At(Tip.Y + 0.006f, 0), At(DeskY, 1), At(DeskY, 2), At(DeskY, 3) };
            Assert.IsTrue(Plane(hits, out var plane));
            Assert.AreEqual(DeskY, plane.Y, 1e-5f);

            var twoAbove = new List<DeskHit> { At(Tip.Y + 0.006f, 0), At(Tip.Y + 0.02f, 1), At(DeskY, 2), At(DeskY, 3) };
            Assert.IsFalse(Plane(twoAbove, out _), "two desk hits are not enough");
        }

        [Test]
        public void HitsNearAnyJointOfEitherHandAreDropped()
        {
            var desk = Enumerable.Range(0, 4).Select(n => At(DeskY, n)).ToList();
            var joints = new List<Vec3>
            {
                Tip,
                desk[0].Point + new Vec3(0f, 0.012f, 0f), // a knuckle right above the desk spot
                desk[1].Point + new Vec3(0.02f, 0.01f, 0f),
            };
            Assert.IsFalse(Plane(desk, out _, joints), "two of four hits near joints: two left");
            Assert.IsTrue(Plane(desk.Concat(new[] { At(DeskY, 4) }).ToList(), out _, joints), "a third clear hit makes a plane");
            Assert.IsTrue(Plane(new List<DeskHit> { At(DeskY, 6), At(DeskY, 2), At(DeskY, 3) }, out _, new List<Vec3> { Tip }),
                "3 cm ahead of a tip resting 1.5 cm above the desk is not near its joint");

            var farJoint = new List<Vec3> { desk[0].Point + new Vec3(0f, 0.031f, 0f) };
            Assert.IsTrue(Plane(new List<DeskHit> { desk[0], desk[0], desk[0] }, out _, farJoint), "3.1 cm from the joint is far enough");
        }

        [Test]
        public void HandLevelHitsWithDeskHitsFarBelowAreNoPlane()
        {
            var hits = new List<DeskHit> { At(Tip.Y - 0.003f, 0), At(Tip.Y - 0.004f, 1), At(DeskY - 0.05f, 2), At(DeskY - 0.05f, 3) };
            Assert.IsFalse(Plane(hits, out _));
        }

        [Test]
        public void KeyboardAndDeskMixedMoreThanOneAndAHalfCentimetresApartAreNoPlane()
        {
            var hits = new List<DeskHit> { At(DeskY + 0.02f, 0), At(DeskY + 0.02f, 1), At(DeskY, 2), At(DeskY, 3) };
            Assert.IsFalse(Plane(hits, out _), "two on the keyboard, two on the desk");
            var spread = new List<DeskHit> { At(DeskY, 0), At(DeskY + 0.008f, 1), At(DeskY + 0.016f, 2) };
            Assert.IsFalse(Plane(spread, out _), "1.6 cm from lowest to highest");
            var tight = new List<DeskHit> { At(DeskY, 0), At(DeskY + 0.007f, 1), At(DeskY + 0.014f, 2) };
            Assert.IsTrue(Plane(tight, out var p), "1.4 cm");
            Assert.AreEqual(DeskY + 0.007f, p.Y, 1e-5f);
        }

        [Test]
        public void ThreeOfFourWithinOneAndAHalfCentimetresAreThePlaneTheOutlierIsIgnored()
        {
            var hits = new List<DeskHit> { At(DeskY, 0), At(DeskY + 0.004f, 1), At(0.42f, 2, new Vec3(0, 0, -1)), At(DeskY - 0.004f, 3) };
            Assert.IsTrue(Plane(hits, out var plane));
            Assert.AreEqual(DeskY, plane.Y, 1e-5f, "the floor through a gap is ignored");
            Assert.AreEqual(1f, plane.Normal.Y, 1e-4f, "the odd normal is not used");
        }

        [Test]
        public void ANormalFacingAwayFromTheHeadIsFlipped()
        {
            var down = new Vec3(0, -1, 0);
            var hits = new List<DeskHit> { At(DeskY, 0, down), At(DeskY, 1, down), At(DeskY, 2, Up) };
            Assert.IsTrue(Plane(hits, out var plane));
            Assert.AreEqual(1f, plane.Normal.Y, 1e-4f);
            Assert.AreEqual(1f, DeskSurface.FacingHead(down, new Vec3(0.1f, DeskY, 0.45f), Head).Y, 1e-5f);
            Assert.AreEqual(1f, DeskSurface.FacingHead(Up, new Vec3(0.1f, DeskY, 0.45f), Head).Y, 1e-5f, "already facing the head");
        }

        [Test]
        public void FewerThanThreeHitsAreNoPlane()
        {
            Assert.IsFalse(Plane(new List<DeskHit>(), out _));
            Assert.IsFalse(Plane(new List<DeskHit> { At(DeskY, 0), At(DeskY, 1) }, out _));
        }
    }
}
