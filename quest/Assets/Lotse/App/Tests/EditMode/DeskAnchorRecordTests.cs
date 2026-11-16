#nullable enable
using System;
using Lotse.App;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    public class DeskAnchorRecordTests
    {
        static readonly Guid A = new Guid("11111111-2222-3333-4444-555555555555");
        static readonly Guid B = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        static readonly Guid C = new Guid("99999999-8888-7777-6666-555555555555");

        MemoryStore _store = null!;
        DeskAnchorRecord _r = null!;

        [SetUp]
        public void SetUp()
        {
            _store = new MemoryStore();
            _r = new DeskAnchorRecord(_store);
        }

        string? Stored => _store.Get(DeskAnchorRecord.Key);

        [Test]
        public void NothingStoredMeansNoSavedAnchor()
        {
            Assert.IsFalse(_r.HasSaved);
            Assert.IsFalse(_r.TryGetSaved(out _));
        }

        [TestCase("not a uuid")]
        [TestCase("00000000-0000-0000-0000-000000000000")]
        [TestCase("")]
        public void AnUnusableValueIsNoSavedAnchor(string value)
        {
            _store.Set(DeskAnchorRecord.Key, value);
            Assert.IsFalse(_r.HasSaved);
        }

        [Test]
        public void TheFirstSaveStoresOnlyTheUuidAndErasesNothing()
        {
            Assert.IsNull(_r.Saved(_r.BeginSave(), A));
            Assert.AreEqual(1, _store.Values.Count, "one key, nothing else");
            Assert.AreEqual(A.ToString(), Stored);
            Assert.IsTrue(_r.TryGetSaved(out var uuid));
            Assert.AreEqual(A, uuid);
        }

        [Test]
        public void ANewSaveReplacesTheOldOneWhichIsToBeErased()
        {
            _r.Saved(_r.BeginSave(), A);
            Assert.AreEqual(A, _r.Saved(_r.BeginSave(), B));
            Assert.AreEqual(B.ToString(), Stored);
            Assert.IsNull(_r.Saved(_r.BeginSave(), B), "saving the same anchor again erases nothing");
        }

        [Test]
        public void AFailedSaveForgetsTheOldAnchorAndHandsItOverForErasing()
        {
            _r.Saved(_r.BeginSave(), A);
            Assert.AreEqual(A, _r.SaveFailed(_r.BeginSave()), "the orphaned old anchor is erased from the device");
            Assert.IsFalse(_r.HasSaved);
            Assert.AreEqual(0, _store.Values.Count);
        }

        [Test]
        public void AFailedFirstSaveHasNothingToForget()
        {
            Assert.IsNull(_r.SaveFailed(_r.BeginSave()));
            Assert.IsFalse(_r.HasSaved);
        }

        [Test]
        public void AnOlderAttemptThatFailsAfterANewerOneSavedLeavesTheNewerOneAlone()
        {
            _r.Saved(_r.BeginSave(), A);
            var older = _r.BeginSave();
            var newer = _r.BeginSave();
            Assert.AreEqual(A, _r.Saved(newer, B));
            Assert.IsNull(_r.SaveFailed(older), "nothing to forget or erase");
            Assert.AreEqual(B.ToString(), Stored, "the newer anchor stays saved");
        }

        [Test]
        public void AnOlderAttemptThatFailsBeforeTheNewerOneEndsDoesNotForgetTheKey()
        {
            _r.Saved(_r.BeginSave(), A);
            var older = _r.BeginSave();
            var newer = _r.BeginSave();
            Assert.IsNull(_r.SaveFailed(older), "a newer attempt owns the key now");
            Assert.AreEqual(A.ToString(), Stored);
            Assert.AreEqual(A, _r.Saved(newer, B));
        }

        [Test]
        public void AnOlderAttemptThatSucceedsLateErasesItsOwnAnchorAndKeepsTheNewerOne()
        {
            var older = _r.BeginSave();
            var newer = _r.BeginSave();
            Assert.IsNull(_r.Saved(newer, B));
            Assert.AreEqual(C, _r.Saved(older, C), "the stale anchor is erased right away");
            Assert.AreEqual(B.ToString(), Stored);
        }

        [Test]
        public void AFailedSaveDoesNotForgetAKeySomethingElseWroteMeanwhile()
        {
            _r.Saved(_r.BeginSave(), A);
            var attempt = _r.BeginSave();
            _store.Set(DeskAnchorRecord.Key, B.ToString());
            Assert.IsNull(_r.SaveFailed(attempt));
            Assert.AreEqual(B.ToString(), Stored);
        }

        [Test]
        public void AnAnchorTheDeviceNoLongerHasIsForgotten()
        {
            _r.Saved(_r.BeginSave(), A);
            _r.NotFound();
            Assert.IsFalse(_r.HasSaved);
        }
    }
}
