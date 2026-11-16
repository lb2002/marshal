#nullable enable
using Lotse.App;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    /// <summary>U1: the wrist menu shows above a palm turned up toward the head, after 0.4 s, never with a card open.</summary>
    public class WristMenuLogicTests
    {
        const double Frame = 20;

        // seated, looking down and ahead at the desk
        static readonly Vec3 Head = new Vec3(0f, 1.2f, 0f);
        static readonly Vec3 View = new Vec3(0f, -0.5f, 0.866f);
        static readonly Vec3 Up = new Vec3(0f, 1f, 0f);
        static readonly Palm LeftUp = new Palm(new Vec3(-0.1f, 0.95f, 0.3f), Up);
        static readonly Palm RightUp = new Palm(new Vec3(0.1f, 0.95f, 0.3f), Up);
        static readonly Palm LeftDown = new Palm(new Vec3(-0.1f, 0.95f, 0.3f), new Vec3(0f, -1f, 0f));
        static readonly Palm RightDown = new Palm(new Vec3(0.1f, 0.95f, 0.3f), new Vec3(0f, -1f, 0f));

        WristMenuLogic _menu = null!;
        double _now;

        [SetUp]
        public void SetUp()
        {
            _menu = new WristMenuLogic();
            _now = 0;
        }

        MenuHand Run(double ms, Palm left, Palm right, bool allowed = true)
        {
            var shown = _menu.Shown;
            for (double t = 0; t < ms - 1e-6; t += Frame)
            {
                _now += Frame;
                shown = _menu.Update(_now, allowed, left, right, Head, View);
            }
            return shown;
        }

        [Test]
        public void ThePalmUpGeometry()
        {
            Assert.IsTrue(WristMenuLogic.PalmUp(LeftUp, Head, View));
            Assert.IsTrue(WristMenuLogic.PalmUp(new Palm(LeftUp.Center, new Vec3(0.3f, 0.95f, 0f).Normalized), Head, View), "a little tilted");
            Assert.IsFalse(WristMenuLogic.PalmUp(new Palm(LeftUp.Center, new Vec3(0f, 0.6f, 0.8f)), Head, View), "normal · up 0.6");
            Assert.IsFalse(WristMenuLogic.PalmUp(LeftDown, Head, View), "palm down");
            Assert.IsFalse(WristMenuLogic.PalmUp(new Palm(new Vec3(-0.45f, 1.0f, 0.05f), Up), Head, View), "beside the head, out of view");
            Assert.IsFalse(WristMenuLogic.PalmUp(Palm.None, Head, View), "no palm");
            Assert.AreEqual(0.7f, Layout.MenuPalmUpMin);
            Assert.AreEqual(45f, Layout.MenuViewDegrees);
            Assert.AreEqual(400.0, Layout.MenuShowMs);
        }

        [Test]
        public void APalmTurnedUpShowsTheMenuAfterFourHundredMilliseconds()
        {
            Assert.AreEqual(MenuHand.None, Run(100, LeftDown, RightDown));
            Assert.AreEqual(MenuHand.None, Run(380, LeftUp, RightDown), "0.38 s");
            Assert.AreEqual(MenuHand.Left, Run(40, LeftUp, RightDown), "0.42 s");
            Assert.AreEqual(MenuHand.Left, Run(1000, LeftUp, RightDown), "stays while the palm is up");
        }

        [Test]
        public void EitherHandWorksAndTheFirstKeepsIt()
        {
            Run(100, LeftDown, RightDown);
            Assert.AreEqual(MenuHand.Right, Run(500, LeftDown, RightUp));
            Assert.AreEqual(MenuHand.Right, Run(1000, LeftUp, RightUp), "the left palm up as well: still the right");
        }

        [Test]
        public void TurningThePalmAwayHidesIt()
        {
            Run(100, LeftDown, RightDown);
            Assert.AreEqual(MenuHand.Left, Run(500, LeftUp, RightDown));
            Assert.AreEqual(MenuHand.None, Run(Frame, LeftDown, RightDown), "hidden at once");
            Assert.AreEqual(MenuHand.None, Run(Frame, LeftUp, RightDown), "back up: the 0.4 s start again");
            Assert.AreEqual(MenuHand.Left, Run(400, LeftUp, RightDown));
            Assert.AreEqual(MenuHand.None, Run(Frame, Palm.None, RightDown), "a lost hand hides it too");
        }

        [Test]
        public void NeverWithACardOpenOrWhilePlacing()
        {
            Run(100, LeftDown, RightDown);
            Assert.AreEqual(MenuHand.Left, Run(500, LeftUp, RightDown));
            Assert.AreEqual(MenuHand.None, Run(Frame, LeftUp, RightDown, allowed: false), "a card opened: hidden at once");
            Assert.AreEqual(MenuHand.None, Run(2000, LeftUp, RightDown, allowed: false));
        }

        [Test]
        public void APalmAlreadyUpWhenTheMenuIsAllowedAgainNeedsAFreshTurn()
        {
            Run(100, LeftDown, RightDown);
            Run(1000, LeftUp, RightDown, allowed: false); // up the whole time a card was open
            Assert.AreEqual(MenuHand.None, Run(1000, LeftUp, RightDown), "the card closed: not by itself");
            Run(Frame, LeftDown, RightDown);
            Assert.AreEqual(MenuHand.Left, Run(420, LeftUp, RightDown), "a fresh turn shows it");
        }

        [Test]
        public void APalmUpAtTheStartNeedsAFreshTurnToo()
        {
            Assert.AreEqual(MenuHand.None, Run(1000, LeftUp, RightDown));
            Run(Frame, LeftDown, RightDown);
            Assert.AreEqual(MenuHand.Left, Run(420, LeftUp, RightDown));
        }
    }
}
