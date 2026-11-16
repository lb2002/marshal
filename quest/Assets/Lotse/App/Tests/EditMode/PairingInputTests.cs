#nullable enable
using Lotse.App;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    /// <summary>HostBook (last three addresses, token per address) and KeypadEntry (what the poke keypads type).</summary>
    public class PairingInputTests
    {
        [Test]
        public void RecentHostsAreTheLastThreeNewestFirstWithoutDuplicates()
        {
            var store = new MemoryStore();
            var book = new HostBook(store);
            CollectionAssert.IsEmpty(book.Recent);
            book.Remember("10.0.0.1");
            book.Remember("10.0.0.2");
            book.Remember("10.0.0.3");
            book.Remember("10.0.0.2");
            CollectionAssert.AreEqual(new[] { "10.0.0.2", "10.0.0.3", "10.0.0.1" }, book.Recent);
            book.Remember("10.0.0.4");
            CollectionAssert.AreEqual(new[] { "10.0.0.4", "10.0.0.2", "10.0.0.3" }, new HostBook(store).Recent, "persisted");
        }

        [Test]
        public void ABrokenStoredListKeepsOnlyValidAddresses()
        {
            var store = new MemoryStore();
            store.Set(HostBook.HostsKey, "10.0.0.1\nnot-an-ip\n\n10.0.0.1\n10.0.0.9\n10.0.0.8\n10.0.0.7");
            CollectionAssert.AreEqual(new[] { "10.0.0.1", "10.0.0.9", "10.0.0.8" }, new HostBook(store).Recent);
        }

        [Test]
        public void SessionsAreKeptPerHost()
        {
            var book = new HostBook(new MemoryStore());
            book.SaveSession("10.0.0.1", "a");
            book.SaveSession("10.0.0.2", "b");
            Assert.AreEqual("a", book.SessionFor("10.0.0.1"));
            Assert.AreEqual("b", book.SessionFor("10.0.0.2"));
            book.ForgetSession("10.0.0.1");
            Assert.IsNull(book.SessionFor("10.0.0.1"));
            Assert.AreEqual("b", book.SessionFor("10.0.0.2"));
            Assert.IsNull(book.SessionFor("10.0.0.3"));
        }

        [Test]
        public void ARefusedSessionIsMarkedUntilANewOneIsSaved()
        {
            var book = new HostBook(new MemoryStore());
            book.SaveSession("10.0.0.1", "a");
            Assert.IsFalse(book.SessionRefused("10.0.0.1"));
            book.MarkSessionRefused("10.0.0.1");
            Assert.IsTrue(book.SessionRefused("10.0.0.1"));
            Assert.IsFalse(book.SessionRefused("10.0.0.2"));
            book.SaveSession("10.0.0.1", "b");
            Assert.IsFalse(book.SessionRefused("10.0.0.1"));
            book.MarkSessionRefused("10.0.0.1");
            book.ForgetSession("10.0.0.1");
            Assert.IsFalse(book.SessionRefused("10.0.0.1"));
        }

        [TestCase("192.168.1.20", true)]
        [TestCase("10.0.0.1", true)]
        [TestCase("0.0.0.0", true)]
        [TestCase("255.255.255.255", true)]
        [TestCase("256.0.0.1", false)]
        [TestCase("192.168.1", false)]
        [TestCase("192.168.1.20.", false)]
        [TestCase("192..1.20", false)]
        [TestCase("192.168.01.20", false)]
        [TestCase("1234.1.1.1", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        [TestCase("localhost", false)]
        public void OnlyDottedIpv4AddressesAreValid(string? host, bool valid)
        {
            Assert.AreEqual(valid, HostBook.IsValidHost(host));
        }

        [Test]
        public void TheCodePadTakesSixDigitsAndNoDot()
        {
            var e = KeypadEntry.ForCode();
            foreach (var c in "12.3456789") e.Type(c);
            Assert.AreEqual("123456", e.Text);
            Assert.IsFalse(e.Type('0'), "full");
            e.Backspace();
            Assert.AreEqual("12345", e.Text);
            Assert.IsTrue(LiveSource.IsPairingCode("123456"));
            Assert.IsFalse(LiveSource.IsPairingCode(e.Text));
        }

        [Test]
        public void TheAddressPadTakesDigitsAndDots()
        {
            var e = KeypadEntry.ForHost();
            foreach (var c in "192.168.100.200x9") e.Type(c);
            Assert.AreEqual("192.168.100.200", e.Text, "x ignored, 15 characters at most");
            e.Clear();
            Assert.AreEqual("", e.Text);
            e.Backspace();
            Assert.AreEqual("", e.Text, "back on empty does nothing");
        }

        [TestCase("needs-two-hand", "Please confirm with both hands.")]
        [TestCase("needs-pinch-hold", "The bridge refused.")]
        [TestCase("unknown", "The request has already ended.")]
        [TestCase("bad-message", "The bridge refused.")]
        [TestCase("", "The bridge refused.")]
        public void BridgeRefusalsAreGerman(string error, string text)
        {
            Assert.AreEqual(text, Texts.Refused(error));
        }

        [TestCase("allow", "Allowed")]
        [TestCase("deny", "Denied")]
        [TestCase("timeout", "Timed out. Please answer in the terminal.")]
        [TestCase("hook-gone", "Not taken. The terminal asks by itself.")]
        [TestCase("client-disconnected", "Not taken. The terminal asks by itself.")]
        public void AClosedCardSaysWhatTheBridgeDid(string reason, string text)
        {
            Assert.AreEqual(text, Texts.Closed(reason, null));
        }

        [Test]
        public void CodeAndHostLinesForThePanel()
        {
            Assert.AreEqual("Code: ___ ___", Texts.CodeLine(""));
            Assert.AreEqual("Code: 123 4__", Texts.CodeLine("1234"));
            Assert.AreEqual("Address: 10.0.0.1", Texts.HostLine("10.0.0.1"));
            Assert.AreEqual("Bridge not reachable. Trying again in 2 s.", Texts.Unreachable(2000));
        }
    }
}
