#nullable enable
using Lotse.App;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    /// <summary>F5: a saved anchor out of reach is not used; the head-following "Neu platzieren" keeps 2 cm clear of other buttons.</summary>
    public class ReachAndClearanceTests
    {
        static readonly Vec3 Head = new Vec3(0f, 1.2f, 0f);
        static readonly Vec3 Ahead = new Vec3(0f, 0f, 1f);

        [Test]
        public void ADeskAnchorInFrontWithinReachIsUsed()
        {
            Assert.IsTrue(AnchorReach.InReach(new Vec3(0.1f, 0.75f, 0.45f), Head, Ahead));
            Assert.IsTrue(AnchorReach.InReach(new Vec3(-0.8f, 0.75f, 0.5f), Head, Ahead), "58° to the left, 1.04 m");
        }

        [Test]
        public void AnAnchorFartherThanOneAndAHalfMetresIsNot()
        {
            Assert.IsFalse(AnchorReach.InReach(new Vec3(0f, 0.75f, 1.6f), Head, Ahead));
            Assert.IsTrue(AnchorReach.InReach(new Vec3(0f, 0.75f, 1.4f), Head, Ahead), "1.47 m");
        }

        [Test]
        public void AnAnchorOutsideTheSeventyDegreeConeIsNot()
        {
            Assert.IsFalse(AnchorReach.InReach(new Vec3(0.6f, 0.75f, 0.15f), Head, Ahead), "76° to the right");
            Assert.IsFalse(AnchorReach.InReach(new Vec3(0f, 0.75f, -0.4f), Head, Ahead), "behind the user");
            Assert.IsTrue(AnchorReach.InReach(new Vec3(0.6f, 0.75f, 0.3f), Head, Ahead), "63° to the right");
        }

        [Test]
        public void TheConeIsAboutWhereTheUserFacesNotWhereTheyLook()
        {
            // looking down at the desk does not put a desk anchor in front of the user outside the cone
            var down = new Vec3(0f, -0.9f, 0.44f).Normalized;
            Assert.IsTrue(AnchorReach.InReach(new Vec3(0.1f, 0.75f, 0.45f), Head, down));
        }

        [Test]
        public void AnAnchorRightUnderTheHeadIsInReach()
        {
            Assert.IsTrue(AnchorReach.InReach(new Vec3(0f, 0.75f, 0f), Head, Ahead));
        }

        static PokeRect Rect(float x, float y, float z, float w, float h) =>
            new PokeRect(new Vec3(x, y, z), new Vec3(1f, 0f, 0f), new Vec3(0f, 1f, 0f), w * 0.5f, h * 0.5f);

        [Test]
        public void TwoButtonsInOnePlaneNeedTwoCentimetresBetweenThem()
        {
            var eye = new Vec3(0f, 0f, 0f);
            var a = Rect(0f, 0f, 0.5f, 0.1f, 0.03f);
            Assert.IsFalse(PokeClearance.Clear(eye, a, Rect(0.11f, 0f, 0.5f, 0.1f, 0.03f)), "1 cm apart");
            Assert.IsTrue(PokeClearance.Clear(eye, a, Rect(0.125f, 0f, 0.5f, 0.1f, 0.03f)), "2.5 cm apart");
            Assert.IsFalse(PokeClearance.Clear(eye, a, Rect(0f, 0.04f, 0.5f, 0.1f, 0.03f)), "1 cm above");
            Assert.IsTrue(PokeClearance.Clear(eye, a, Rect(0f, 0.06f, 0.5f, 0.1f, 0.03f)), "3 cm above");
        }

        [Test]
        public void AButtonFartherAwayCountsWhereItIsSeen()
        {
            var eye = new Vec3(0f, 0f, 0f);
            var near = Rect(0f, 0f, 0.4f, 0.1f, 0.03f);
            Assert.IsFalse(PokeClearance.Clear(eye, near, Rect(0f, 0f, 0.6f, 0.2f, 0.2f)), "right behind it: a reach for the far one goes through the near one");
            Assert.IsTrue(PokeClearance.Clear(eye, near, Rect(0.3f, 0f, 0.6f, 0.2f, 0.2f)), "well to the side");
            Assert.IsFalse(PokeClearance.Clear(eye, near, Rect(0.2f, 0f, 0.6f, 0.2f, 0.2f)), "its edge 5 cm off at 0.6 m is seen 1.7 cm off at 0.4 m");
        }

        [Test]
        public void AButtonBehindTheUserIsClear()
        {
            var eye = new Vec3(0f, 0f, 0f);
            Assert.IsTrue(PokeClearance.Clear(eye, Rect(0f, 0f, 0.4f, 0.1f, 0.03f), Rect(0f, 0f, -0.5f, 0.3f, 0.3f)));
        }
    }
}
