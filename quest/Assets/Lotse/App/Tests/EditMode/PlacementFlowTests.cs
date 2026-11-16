#nullable enable
using Lotse.App;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    public class PlacementFlowTests
    {
        const RaycastAvailability Ready = RaycastAvailability.Ready;

        static PlacementFlow Placing(double now = 0)
        {
            var f = new PlacementFlow();
            f.Begin(now, loadSaved: false);
            Assert.AreEqual(PlacementState.Placing, f.State);
            return f;
        }

        [Test]
        public void WithoutASavedAnchorItStartsPlacing()
        {
            var f = new PlacementFlow();
            Assert.IsFalse(f.Active);
            f.Begin(0, loadSaved: false);
            Assert.AreEqual(PlacementState.Placing, f.State);
            Assert.IsTrue(f.Active);
        }

        [Test]
        public void AnAcceptedTouchPlacesTheAnchor()
        {
            var f = Placing();
            f.Tick(100, Ready, sawHit: true, accepted: false);
            Assert.AreEqual(PlacementState.Placing, f.State);
            f.Tick(500, Ready, sawHit: true, accepted: true);
            Assert.AreEqual(PlacementState.Placed, f.State);
            Assert.IsFalse(f.Active);
            Assert.AreEqual(PlacementFallback.None, f.Fallback);
        }

        [TestCase(RaycastAvailability.Unsupported, PlacementFallback.Unsupported)]
        [TestCase(RaycastAvailability.PermissionDenied, PlacementFallback.PermissionDenied)]
        public void NoRaycastFallsBackAtOnce(RaycastAvailability availability, PlacementFallback expected)
        {
            var f = Placing();
            f.Tick(16, availability, false, false);
            Assert.AreEqual(PlacementState.Fallback, f.State);
            Assert.AreEqual(expected, f.Fallback);
            Assert.IsFalse(f.Active);
        }

        [Test]
        public void NoHitForEightSecondsFallsBack()
        {
            var f = Placing(1000);
            f.Tick(1000 + PlacementFlow.NoHitMs - 1, Ready, false, false);
            Assert.AreEqual(PlacementState.Placing, f.State);
            f.Tick(1000 + PlacementFlow.NoHitMs, Ready, false, false);
            Assert.AreEqual(PlacementState.Fallback, f.State);
            Assert.AreEqual(PlacementFallback.NoHit, f.Fallback);
        }

        [Test]
        public void EveryHitStartsTheEightSecondsAgain()
        {
            var f = Placing();
            f.Tick(7000, Ready, sawHit: true, accepted: false);
            f.Tick(14000, Ready, false, false);
            Assert.AreEqual(PlacementState.Placing, f.State, "7 s since the last hit");
            f.Tick(15000, Ready, false, false);
            Assert.AreEqual(PlacementFallback.NoHit, f.Fallback);
        }

        [Test]
        public void TheEightSecondsStartOnlyOnceThePermissionIsAnswered()
        {
            var f = Placing();
            f.Tick(12000, RaycastAvailability.AskingPermission, false, false);
            Assert.AreEqual(PlacementState.Placing, f.State, "the user is reading the permission dialog");
            f.Tick(19999, Ready, false, false);
            Assert.AreEqual(PlacementState.Placing, f.State);
            f.Tick(20000, Ready, false, false);
            Assert.AreEqual(PlacementFallback.NoHit, f.Fallback);
        }

        [Test]
        public void PlacingNeverRunsLongerThanTheCap()
        {
            var f = Placing();
            for (var t = 0; t < PlacementFlow.MaxPlacingMs; t += 1000) f.Tick(t, Ready, sawHit: true, accepted: false);
            Assert.AreEqual(PlacementState.Placing, f.State);
            f.Tick(PlacementFlow.MaxPlacingMs, Ready, true, false);
            Assert.AreEqual(PlacementState.Fallback, f.State);
            Assert.AreEqual(PlacementFallback.TimedOut, f.Fallback);
        }

        [Test]
        public void TheCapPausesWhileThePermissionDialogIsUp()
        {
            var f = Placing();
            f.Tick(1000, Ready, sawHit: true, accepted: false);
            for (var t = 2000; t <= 19000; t += 1000) f.Tick(t, RaycastAvailability.AskingPermission, false, false);
            Assert.AreEqual(PlacementState.Placing, f.State, "18 s of dialog do not count");
            for (var t = 20000; t < 48000; t += 1000) f.Tick(t, Ready, sawHit: true, accepted: false);
            Assert.AreEqual(PlacementState.Placing, f.State, "29 s outside the dialog");
            f.Tick(48000, Ready, true, false);
            Assert.AreEqual(PlacementFallback.TimedOut, f.Fallback, "30 s outside the dialog");
        }

        [Test]
        public void ThePermissionDialogGetsTwentySecondsThenItFallsBack()
        {
            var f = Placing();
            for (var t = 1000; t < PlacementFlow.MaxAskingMs; t += 1000) f.Tick(t, RaycastAvailability.AskingPermission, false, false);
            Assert.AreEqual(PlacementState.Placing, f.State, "19 s of dialog");
            f.Tick(PlacementFlow.MaxAskingMs, RaycastAvailability.AskingPermission, false, false);
            Assert.AreEqual(PlacementState.Fallback, f.State);
            Assert.AreEqual(PlacementFallback.NoPermissionAnswer, f.Fallback);
            Assert.AreEqual(20000, PlacementFlow.MaxAskingMs);
        }

        [Test]
        public void TheTwentySecondsCountOnlyTheDialog()
        {
            var f = Placing();
            f.Tick(10000, RaycastAvailability.AskingPermission, false, false);
            f.Tick(11000, Ready, sawHit: true, accepted: false);
            f.Tick(20000, RaycastAvailability.AskingPermission, false, false); // 9 s more: 19 s in all
            Assert.AreEqual(PlacementState.Placing, f.State);
            f.Tick(21000, RaycastAvailability.AskingPermission, false, false);
            Assert.AreEqual(PlacementFallback.NoPermissionAnswer, f.Fallback);
        }

        [Test]
        public void ADismissedDialogIsNotADenial()
        {
            var f = Placing();
            f.Tick(500, RaycastAvailability.PermissionDismissed, false, false);
            Assert.AreEqual(PlacementState.Fallback, f.State);
            Assert.AreEqual(PlacementFallback.PermissionDismissed, f.Fallback);
        }

        [Test]
        public void ADismissedRequestIsAskedAgainOnTheNextPlacement()
        {
            var ask = new PermissionAsk();
            ask.BeginPlacement();
            Assert.IsTrue(ask.ShouldAsk);
            ask.MarkAsked();
            Assert.IsFalse(ask.ShouldAsk, "once per placement");
            Assert.AreEqual(RaycastAvailability.AskingPermission, ask.Availability(granted: false));
            ask.Answered(PermissionAnswer.Dismissed); // the empty result
            Assert.AreEqual(RaycastAvailability.PermissionDismissed, ask.Availability(granted: false));
            ask.BeginPlacement();
            Assert.IsTrue(ask.ShouldAsk, "asked again");
            Assert.AreEqual(RaycastAvailability.AskingPermission, ask.Availability(granted: false));
        }

        [Test]
        public void AnUnansweredRequestIsAskedAgainOnTheNextPlacement()
        {
            var ask = new PermissionAsk();
            ask.BeginPlacement();
            ask.MarkAsked();
            ask.BeginPlacement();
            Assert.IsTrue(ask.ShouldAsk);
        }

        [Test]
        public void AnAnswerLeftOverFromTheLastPlacementIsTakenFirst()
        {
            // the dialog of the previous placement was answered after it fell back
            var ask = new PermissionAsk();
            ask.BeginPlacement();
            ask.MarkAsked();
            ask.BeginPlacement(PermissionAnswer.Denied);
            Assert.IsFalse(ask.ShouldAsk, "a late no stands");
            Assert.AreEqual(RaycastAvailability.PermissionDenied, ask.Availability(granted: false));

            var again = new PermissionAsk();
            again.BeginPlacement();
            again.MarkAsked();
            again.BeginPlacement(PermissionAnswer.Dismissed);
            Assert.IsTrue(again.ShouldAsk, "a late dismissal is asked again");
            Assert.AreEqual(RaycastAvailability.AskingPermission, again.Availability(granted: false));
        }

        [Test]
        public void ADenialStands()
        {
            var ask = new PermissionAsk();
            ask.BeginPlacement();
            ask.MarkAsked();
            ask.Answered(PermissionAnswer.Denied);
            Assert.AreEqual(RaycastAvailability.PermissionDenied, ask.Availability(granted: false));
            ask.BeginPlacement();
            Assert.IsFalse(ask.ShouldAsk, "no nagging after a no");
            Assert.AreEqual(RaycastAvailability.PermissionDenied, ask.Availability(granted: false));
            Assert.AreEqual(RaycastAvailability.Ready, ask.Availability(granted: true), "granted later in the settings");
        }

        [Test]
        public void WhileTheRaycastIsStartingNeitherClockRuns()
        {
            var f = Placing();
            f.Tick(2900, RaycastAvailability.Starting, false, false);
            Assert.AreEqual(PlacementState.Placing, f.State);
            f.Tick(2900 + PlacementFlow.NoHitMs - 1, Ready, false, false);
            Assert.AreEqual(PlacementState.Placing, f.State);
            f.Tick(2900 + PlacementFlow.NoHitMs, Ready, false, false);
            Assert.AreEqual(PlacementFallback.NoHit, f.Fallback);
        }

        [Test]
        public void ASavedAnchorThatLocalisesIsUsed()
        {
            var f = new PlacementFlow();
            f.Begin(0, loadSaved: true);
            Assert.AreEqual(PlacementState.LoadingSaved, f.State);
            Assert.IsTrue(f.Active);
            f.Tick(1000, Ready, false, false);
            f.SavedLoaded(2000, localized: true);
            Assert.AreEqual(PlacementState.LoadedSaved, f.State);
            Assert.IsFalse(f.Active);
        }

        [Test]
        public void ASavedAnchorThatFailsLeadsToPlacing()
        {
            var f = new PlacementFlow();
            f.Begin(0, loadSaved: true);
            f.SavedLoaded(500, localized: false);
            Assert.AreEqual(PlacementState.Placing, f.State);
            f.Tick(500 + PlacementFlow.NoHitMs - 1, Ready, false, false);
            Assert.AreEqual(PlacementState.Placing, f.State, "the eight seconds count from the start of placing");
        }

        [Test]
        public void ASavedAnchorNotLocalisedWithinThreeSecondsLeadsToPlacingAndALateAnswerIsIgnored()
        {
            var f = new PlacementFlow();
            f.Begin(0, loadSaved: true);
            f.Tick(PlacementFlow.LoadMs - 1, Ready, false, false);
            Assert.AreEqual(PlacementState.LoadingSaved, f.State);
            f.Tick(PlacementFlow.LoadMs, Ready, false, false);
            Assert.AreEqual(PlacementState.Placing, f.State);
            f.SavedLoaded(4000, localized: true);
            Assert.AreEqual(PlacementState.Placing, f.State, "too late");
        }

        [Test]
        public void WhileLoadingTheRaycastIsNotConsulted()
        {
            var f = new PlacementFlow();
            f.Begin(0, loadSaved: true);
            f.Tick(100, RaycastAvailability.Unsupported, false, true);
            Assert.AreEqual(PlacementState.LoadingSaved, f.State);
        }

        [Test]
        public void AFinishedFlowIgnoresTicksUntilItBeginsAgain()
        {
            var f = Placing();
            f.Tick(100, RaycastAvailability.Unsupported, false, false);
            f.Tick(200, Ready, true, true);
            Assert.AreEqual(PlacementState.Fallback, f.State);
            f.Begin(300, loadSaved: false);
            Assert.AreEqual(PlacementState.Placing, f.State);
            Assert.AreEqual(PlacementFallback.None, f.Fallback);
            f.Tick(400, Ready, true, true);
            Assert.AreEqual(PlacementState.Placed, f.State);
        }
    }
}
