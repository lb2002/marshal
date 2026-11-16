#nullable enable
using Lotse.App;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    public class PokeLogicTests
    {
        const float HalfW = 0.05f;
        const float HalfH = 0.02f;

        PokeLogic _poke = null!;
        double _now;

        [SetUp]
        public void SetUp()
        {
            _poke = new PokeLogic();
            _now = 0;
        }

        bool At(float depth, float x = 0, float y = 0, bool present = true)
        {
            _now += 14;
            return _poke.Update(_now, present, x, y, depth, HalfW, HalfH);
        }

        [Test]
        public void ApproachFromTheFrontAndCrossingPressesOnce()
        {
            Assert.IsFalse(At(-0.06f));
            Assert.IsFalse(At(-0.03f));
            Assert.IsTrue(_poke.Hover);
            Assert.IsFalse(At(-0.01f));
            Assert.IsTrue(At(0.002f), "crossing the face presses");
            Assert.IsFalse(At(0.01f), "staying in does not repeat");
            Assert.IsFalse(At(0.02f));
        }

        [Test]
        public void PullBackAndPressAgainAfterTheCooldown()
        {
            At(-0.06f);
            At(-0.02f);
            Assert.IsTrue(At(0.001f));
            Assert.IsFalse(At(-0.005f), "not pulled back far enough to release");
            Assert.IsFalse(At(0.001f));
            At(-0.02f);
            Assert.IsFalse(At(0.001f), "within the cooldown");
            _now += PokeLogic.CooldownMs;
            At(-0.02f);
            Assert.IsTrue(At(0.001f));
        }

        [Test]
        public void ATipComingFromBehindNeverPresses()
        {
            Assert.IsFalse(At(0.08f));
            Assert.IsFalse(At(0.03f));
            Assert.IsFalse(At(0.001f));
        }

        [Test]
        public void AButtonAppearingUnderTheFingerDoesNotPress()
        {
            // the first sample is already through the face: never armed
            Assert.IsFalse(At(0.005f));
            Assert.IsFalse(At(0.01f));
        }

        [Test]
        public void ATipAlreadyJustInFrontWhenTheButtonAppearsDoesNotPress()
        {
            // rising edge: the first sight is inside the arm zone, so the tip was there before the button
            Assert.IsFalse(At(-0.01f));
            Assert.IsFalse(At(0.002f));
            Assert.IsFalse(At(-0.02f));
            Assert.IsFalse(At(0.002f), "still never seen clear");
            // once seen clear (far in front), it works
            At(-0.06f);
            At(-0.02f);
            Assert.IsTrue(At(0.002f));
        }

        [Test]
        public void SeenAbsentOrOutsideCountsAsClear()
        {
            At(-0.01f);
            At(-0.01f, present: false);
            At(-0.02f);
            Assert.IsTrue(At(0.002f), "absent then approach");

            var other = new PokeLogic();
            Assert.IsFalse(other.Update(0, true, 0.2f, 0f, -0.01f, HalfW, HalfH));
            Assert.IsFalse(other.Update(10, true, 0f, 0f, -0.01f, HalfW, HalfH));
            Assert.IsTrue(other.Update(20, true, 0f, 0f, 0.002f, HalfW, HalfH), "outside then slid in, in front");
        }

        [Test]
        public void OutsideTheRectangleNothingHappens()
        {
            Assert.IsFalse(At(-0.02f, x: 0.08f));
            Assert.IsFalse(At(0.001f, x: 0.08f));
            Assert.IsFalse(_poke.Hover);
        }

        [Test]
        public void SlidingInFromTheSideAlreadyThroughDoesNotPress()
        {
            At(-0.02f, x: 0.08f);
            Assert.IsFalse(At(0.002f, x: 0.0f));
        }

        [Test]
        public void LosingTheTipDisarms()
        {
            At(-0.06f);
            At(-0.02f);
            At(-0.02f, present: false);
            Assert.IsFalse(At(0.002f));
        }
    }
}
