using System.Text;
using Lotse.App;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    /// <summary>
    /// U11: the pairing QR code's payload and what a sighting may do. A QR code in the room is untrusted input: the
    /// parser takes exactly what the bridge prints and nothing else, silently; a sighting never connects by itself.
    /// </summary>
    public class PairLinkTests
    {
        const string Good = "marshal://pair?v=1&h=192.168.1.197&p=47821&c=481216";

        static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

        static bool Parses(string s) => PairLink.TryParse(Bytes(s), out _) | PairLink.TryParse(s, out _);

        // ---- the parser: valid ----

        [Test]
        public void ThePayloadTheBridgePrintsParsesToItsAddressPortAndCode()
        {
            Assert.IsTrue(PairLink.TryParse(Bytes(Good), out var p));
            Assert.AreEqual("192.168.1.197", p.Host);
            Assert.AreEqual(47821, p.Port);
            Assert.AreEqual("481216", p.Code);
            Assert.IsTrue(PairLink.TryParse(Good, out var q));
            Assert.AreEqual(p, q);
            StringAssert.DoesNotContain("481216", p.ToString(), "the code is not in its text form");
        }

        [TestCase("marshal://pair?v=1&h=10.0.0.1&p=1024&c=000000")]
        [TestCase("marshal://pair?v=1&h=10.255.255.255&p=65535&c=999999")]
        [TestCase("marshal://pair?v=1&h=172.16.0.1&p=47821&c=123456")]
        [TestCase("marshal://pair?v=1&h=172.31.255.254&p=47821&c=123456")]
        [TestCase("marshal://pair?v=1&h=192.168.0.0&p=47821&c=123456")]
        [TestCase("marshal://pair?c=123456&p=47821&h=192.168.1.20&v=1")] // the four keys once each, in any order
        public void ValidPayloadsParse(string s)
        {
            Assert.IsTrue(PairLink.TryParse(Bytes(s), out _), s);
            Assert.IsTrue(PairLink.TryParse(s, out _), s);
        }

        [Test]
        public void ThePayloadMayBeUpToNinetySixBytesLong()
        {
            Assert.LessOrEqual(Good.Length, PairLink.MaxLength);
            // the longest payload the grammar allows is far below the limit: the limit only cuts off junk early
            var longest = "marshal://pair?v=1&h=192.168.100.100&p=65535&c=123456";
            Assert.IsTrue(Parses(longest));
            Assert.Less(longest.Length, PairLink.MaxLength);
            Assert.IsFalse(Parses(Good + new string('&', PairLink.MaxLength)), "over 96");
        }

        // ---- the parser: every invalid class is simply not a pairing code ----

        // the scheme and the host
        [TestCase("Marshal://pair?v=1&h=192.168.1.197&p=47821&c=481216")]
        [TestCase("MARSHAL://pair?v=1&h=192.168.1.197&p=47821&c=481216")]
        [TestCase("marshals://pair?v=1&h=192.168.1.197&p=47821&c=481216")]
        [TestCase("http://pair?v=1&h=192.168.1.197&p=47821&c=481216")]
        [TestCase("https://example.com/pair?v=1&h=192.168.1.197&p=47821&c=481216")]
        [TestCase("marshal:pair?v=1&h=192.168.1.197&p=47821&c=481216")]
        [TestCase("marshal://Pair?v=1&h=192.168.1.197&p=47821&c=481216")]
        [TestCase("marshal://pairing?v=1&h=192.168.1.197&p=47821&c=481216")]
        [TestCase("marshal://pair/?v=1&h=192.168.1.197&p=47821&c=481216")]
        [TestCase("marshal://pair/x?v=1&h=192.168.1.197&p=47821&c=481216")]
        [TestCase("marshal://user@pair?v=1&h=192.168.1.197&p=47821&c=481216")]
        [TestCase("marshal://pair:80?v=1&h=192.168.1.197&p=47821&c=481216")]
        [TestCase("marshal://pair")]
        [TestCase("marshal://pair?")]
        [TestCase("x marshal://pair?v=1&h=192.168.1.197&p=47821&c=481216")]
        // the keys: exactly v, h, p, c, once each
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821")]
        [TestCase("marshal://pair?h=192.168.1.197&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&c=481216&x=1")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&c=481216&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&h=10.0.0.1&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&v=1&h=192.168.1.197&p=47821")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&C=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&code=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&c=481216&")]
        [TestCase("marshal://pair?&v=1&h=192.168.1.197&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&&h=192.168.1.197&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1;h=192.168.1.197;p=47821;c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&c=481216#x")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&c=")]
        [TestCase("marshal://pair?v=1&h=&p=47821&c=481216")]
        [TestCase("marshal://pair?v&h=192.168.1.197&p=47821&c=481216")]
        // the version
        [TestCase("marshal://pair?v=2&h=192.168.1.197&p=47821&c=481216")]
        [TestCase("marshal://pair?v=01&h=192.168.1.197&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1.0&h=192.168.1.197&p=47821&c=481216")]
        [TestCase("marshal://pair?v=&h=192.168.1.197&p=47821&c=481216")]
        // the address: a dotted private IPv4 literal, nothing else
        [TestCase("marshal://pair?v=1&h=8.8.8.8&p=47821&c=481216")] // public
        [TestCase("marshal://pair?v=1&h=127.0.0.1&p=47821&c=481216")] // loopback
        [TestCase("marshal://pair?v=1&h=169.254.1.1&p=47821&c=481216")] // link-local
        [TestCase("marshal://pair?v=1&h=100.64.0.1&p=47821&c=481216")] // carrier-grade NAT
        [TestCase("marshal://pair?v=1&h=172.15.0.1&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=172.32.0.1&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.169.1.1&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=11.0.0.1&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=0.0.0.0&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=255.255.255.255&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.256&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.1.1&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.01.1&p=47821&c=481216")] // a leading zero
        [TestCase("marshal://pair?v=1&h=192.168.1.001&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=010.0.0.1&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168..1&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.1:80&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.1/x&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.1@evil.example&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=evil.example&p=47821&c=481216")] // a name
        [TestCase("marshal://pair?v=1&h=localhost&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=my-pc.local&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=::1&p=47821&c=481216")] // IPv6
        [TestCase("marshal://pair?v=1&h=[fe80::1]&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=fd00::1&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=0xC0.0xA8.1.1&p=47821&c=481216")] // hex
        [TestCase("marshal://pair?v=1&h=3232235777&p=47821&c=481216")] // one number
        [TestCase("marshal://pair?v=1&h=192.168.1.-1&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.+1&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=192%2E168.1.1&p=47821&c=481216")] // percent-encoding
        [TestCase("marshal://pair?v=1&h=192.168.1.1%00&p=47821&c=481216")]
        // the port
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=1023&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=80&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=0&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=65536&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=99999&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=478210&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=047821&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=04782&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=-47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=+47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=4782x&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=0xBACD&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=4.7e4&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=&c=481216")]
        // the code: exactly six ASCII digits
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&c=48121")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&c=4812160")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&c=48121a")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&c=-48121")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&c=4812.6")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&c=%34%38%31%32%31%36")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&c=٤٨١٢١٦")] // Arabic-Indic digits
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&c=４８１２１６")] // full-width digits
        // whitespace and control characters, anywhere
        [TestCase(" marshal://pair?v=1&h=192.168.1.197&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&c=481216 ")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&c=481216\n")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&c=481216\r\n")]
        [TestCase("marshal://pair?v=1&h=192.168. 1.197&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197\t&p=47821&c=481216")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&c=481216\0")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&c=481216\u007f")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&c=481216 ")]
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&c=481216​")] // zero-width space
        [TestCase("marshal://pair?v=1&h=192.168.1.197&p=47821&c=481216‮")] // a bidi override
        // other things a QR code in the room may say
        [TestCase("")]
        [TestCase("481216")]
        [TestCase("192.168.1.197")]
        [TestCase("WIFI:T:WPA;S:home;P:secret;;")]
        [TestCase("https://example.com/")]
        [TestCase("<color=red>marshal://pair?v=1&h=192.168.1.197&p=47821&c=481216</color>")]
        public void AnythingElseIsNotAPairingCode(string s)
        {
            Assert.IsFalse(PairLink.TryParse(Bytes(s), out var p), "as bytes: " + s);
            Assert.AreEqual(default(PairPayload), p);
            Assert.IsFalse(PairLink.TryParse(s, out _), "as text: " + s);
        }

        [Test]
        public void BytesThatAreNotValidUtf8TextAreNotAPairingCode()
        {
            Assert.IsFalse(PairLink.TryParse((byte[])null, out _));
            Assert.IsFalse(PairLink.TryParse((string)null, out _));
            Assert.IsFalse(PairLink.TryParse(new byte[0], out _));
            var good = Bytes(Good);
            foreach (var bad in new byte[] { 0x80, 0xBF, 0xC0, 0xC1, 0xF5, 0xFF, 0xE2, 0x00, 0x1F })
            {
                // a broken byte at the end, at the start, and in place of a character
                var tail = new byte[good.Length + 1];
                good.CopyTo(tail, 0);
                tail[good.Length] = bad;
                Assert.IsFalse(PairLink.TryParse(tail, out _), "after it: 0x" + bad.ToString("X2"));
                var head = new byte[good.Length + 1];
                good.CopyTo(head, 1);
                head[0] = bad;
                Assert.IsFalse(PairLink.TryParse(head, out _), "before it: 0x" + bad.ToString("X2"));
                var inside = (byte[])good.Clone();
                inside[good.Length - 1] = bad;
                Assert.IsFalse(PairLink.TryParse(inside, out _), "inside it: 0x" + bad.ToString("X2"));
            }
            // an overlong encoding of '1' and a UTF-8 byte order mark
            Assert.IsFalse(PairLink.TryParse(Bytes("marshal://pair?v=").Concat(new byte[] { 0xC0, 0xB1 }, Bytes("&h=192.168.1.197&p=47821&c=481216")), out _));
            Assert.IsFalse(PairLink.TryParse(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(good), out _));
            // 97 bytes of anything
            Assert.IsFalse(PairLink.TryParse(new byte[PairLink.MaxLength + 1], out _));
        }

        // ---- what a sighting may do ----

        const int Port = 47821;
        static readonly byte[] A = Bytes("marshal://pair?v=1&h=192.168.1.197&p=47821&c=481216");
        static readonly byte[] B = Bytes("marshal://pair?v=1&h=192.168.1.197&p=47821&c=777777");

        [Test]
        public void ASightingOnlyAsksAndOnlyConfirmHandsOutWhatToPairWith()
        {
            var q = new QrPairing(Port);
            Assert.AreEqual(QrPhase.Off, q.Phase);
            Assert.IsTrue(q.Start(connected: false));
            Assert.AreEqual(QrPhase.Scanning, q.Phase);
            Assert.IsNull(q.Confirm(), "nothing to confirm before a sighting");
            Assert.IsTrue(q.Seen(A));
            Assert.AreEqual(QrPhase.Asking, q.Phase, "a sighting leads to the question, never to a connection");
            Assert.AreEqual("192.168.1.197", q.Pending.Value.Host);
            var p = q.Confirm();
            Assert.AreEqual(new PairPayload("192.168.1.197", Port, "481216"), p.Value);
            Assert.AreEqual(QrPhase.Attempting, q.Phase);
            Assert.IsNull(q.Confirm(), "once");
            Assert.IsNull(q.Pending);
        }

        [Test]
        public void WhileTheQuestionShowsAnotherCodeChangesNothing()
        {
            var q = new QrPairing(Port);
            q.Start(false);
            Assert.IsTrue(q.Seen(A));
            Assert.IsFalse(q.Seen(B), "the question is about A: B is not looked at");
            Assert.AreEqual("481216", q.Pending.Value.Code);
            Assert.AreEqual("481216", q.Confirm().Value.Code, "what was asked about is what is used");
        }

        [Test]
        public void CancelDoesNothingAndClosesScanMode()
        {
            var q = new QrPairing(Port);
            q.Start(false);
            q.Seen(A);
            q.Cancel();
            Assert.AreEqual(QrPhase.Off, q.Phase);
            Assert.IsNull(q.Pending);
            Assert.IsNull(q.Confirm(), "nothing to pair with after Cancel");
            Assert.IsFalse(q.Seen(A), "and scan mode is closed");
        }

        [Test]
        public void AFailedAttemptClosesScanModeAndItsCodeIsIgnoredUntilTheUserScansAgain()
        {
            var q = new QrPairing(Port);
            q.Start(false);
            q.Seen(A);
            q.Confirm();
            q.AttemptFailed();
            Assert.AreEqual(QrPhase.Off, q.Phase, "scan mode does not reopen by itself");
            Assert.IsTrue(q.Refused);
            for (var i = 0; i < 100; i++) Assert.IsFalse(q.Seen(A), "the failed code is never asked about again by itself");
            Assert.IsFalse(q.Seen(B), "nor any other: scan mode is closed");
            Assert.AreEqual(QrPhase.Off, q.Phase);
            Assert.IsNull(q.Confirm(), "and nothing retries it");
            // the user presses scan again: the earlier refusal is forgotten
            Assert.IsTrue(q.Start(false));
            Assert.IsFalse(q.Refused);
            Assert.IsTrue(q.Seen(A), "scan pressed again: asked about once more");
        }

        [Test]
        public void OutsideScanModeEverySightingIsIgnored()
        {
            var q = new QrPairing(Port);
            Assert.IsFalse(q.Seen(A), "scan mode was never opened");
            Assert.AreEqual(QrPhase.Off, q.Phase);
            q.Start(false);
            q.Seen(A);
            q.Confirm();
            Assert.IsFalse(q.Seen(B), "while the attempt runs");
            q.AttemptSucceeded();
            Assert.AreEqual(QrPhase.Off, q.Phase);
            Assert.IsFalse(q.Seen(B), "after pairing");
            q.Start(false);
            q.Stop(); // the screen closed, the app paused
            Assert.IsFalse(q.Seen(A));
            Assert.IsNull(q.Pending);
        }

        [Test]
        public void WhileConnectedScanModeDoesNotStart()
        {
            var q = new QrPairing(Port);
            Assert.IsFalse(q.Start(connected: true));
            Assert.AreEqual(QrPhase.Off, q.Phase);
            Assert.IsFalse(q.Seen(A));
        }

        // ---- when "Pair" is pressable, and which pinch may press it (U13) ----

        /// <summary>Steps of 0.1 s. Returns whether it is armed after them.</summary>
        static bool Steps(AskArming a, int n, bool lu, bool lp, bool ru, bool rp)
        {
            var armed = a.Armed;
            for (var i = 0; i < n; i++) armed = a.Step(0.1f, lu, lp, ru, rp);
            return armed;
        }

        [Test]
        public void PairIsPressableAfterPointSixSecondsByTimeAlone()
        {
            var a = new AskArming();
            Assert.IsFalse(a.Armed);
            Assert.IsFalse(a.PressAllowed);
            Assert.IsFalse(Steps(a, 5, true, false, true, false), "0.5 s: not yet");
            Assert.IsTrue(a.Step(0.1f, true, false, true, false), "0.6 s");
            Assert.IsTrue(a.JustArmed, "it says so once");
            a.Step(0.1f, true, false, true, false);
            Assert.IsFalse(a.JustArmed);
            Assert.IsTrue(a.PressAllowed, "no pinch at all: a poke counts");
            a.Reset();
            Assert.IsFalse(a.Armed, "a new question starts over");
        }

        [Test]
        public void RepeatedAirPinchesBeforeArmingDoNotDelayIt()
        {
            var a = new AskArming();
            // pinch, release, pinch, release ... for 0.5 s, then one more step
            for (var i = 0; i < 5; i++) Assert.IsFalse(a.Step(0.1f, true, false, true, i % 2 == 0));
            Assert.IsTrue(a.Step(0.1f, true, false, true, false), "armed at 0.6 s whatever the hands did");
            // no hand at all, or a hand held pinched the whole time: the same 0.6 s
            var b = new AskArming();
            Assert.IsTrue(Steps(b, 6, false, false, false, false));
            var c = new AskArming();
            Assert.IsTrue(Steps(c, 6, true, true, true, true));
        }

        [Test]
        public void AHandFlickeringInAndOutOfTrackingAfterArmingDoesNotDisarm()
        {
            var a = new AskArming();
            Assert.IsTrue(Steps(a, 7, true, false, true, false));
            for (var i = 0; i < 20; i++)
            {
                // the left hand drifts in and out of view, the right hand's confidence flickers
                Assert.IsTrue(a.Step(0.1f, i % 2 == 0, false, i % 3 != 0, false), "still armed in step " + i);
                Assert.IsFalse(a.JustArmed);
            }
            Assert.IsTrue(a.PressAllowed);
            // both gone (no input focus) and back, open: still armed, and the next pinch counts
            Steps(a, 5, false, false, false, false);
            Assert.IsTrue(a.Step(0.1f, true, false, true, false));
            a.Step(0.1f, true, false, true, true);
            Assert.IsTrue(a.PressAllowed, "a pinch begun after it came back open");
        }

        [Test]
        public void APinchBegunAfterArmingPairs()
        {
            var a = new AskArming();
            Steps(a, 7, true, false, true, false);
            a.Step(0.1f, true, false, true, true); // the pinch begins, seen, after arming
            Assert.IsNull(a.Ignored);
            Assert.IsTrue(a.PressAllowed, "while it is held");
            a.Step(0.1f, true, false, true, true);
            Assert.IsTrue(a.PressAllowed);
            a.Step(0.1f, true, false, true, false); // released: a ray click fires in this frame
            Assert.IsTrue(a.PressAllowed, "in the frame of its release");
        }

        [Test]
        public void APinchBegunBeforeArmingDoesNotPairUntilItIsReleased()
        {
            var a = new AskArming();
            Steps(a, 3, true, false, true, false);
            a.Step(0.1f, true, false, true, true); // begins at 0.4 s
            Assert.AreEqual(AskArming.BegunBefore, a.Ignored);
            Steps(a, 5, true, false, true, true); // held across the arming
            Assert.IsTrue(a.Armed);
            Assert.IsNull(a.Ignored, "said once, when it began");
            Assert.IsFalse(a.PressAllowed, "held: it does not count");
            a.Step(0.1f, true, false, true, false);
            Assert.IsFalse(a.PressAllowed, "nor in the frame of its release");
            a.Step(0.1f, true, false, true, false);
            a.Step(0.1f, true, false, true, true);
            Assert.IsTrue(a.PressAllowed, "the next pinch, begun after arming, does");
            // a pinch already under way when the question appears
            var b = new AskArming();
            b.Step(0.1f, true, false, true, true);
            Assert.AreEqual(AskArming.BegunBefore, b.Ignored);
            Steps(b, 9, true, false, true, true);
            Assert.IsFalse(b.PressAllowed);
        }

        [Test]
        public void AHandThatReappearsAlreadyPinchedDoesNotPair()
        {
            var a = new AskArming();
            Assert.IsTrue(Steps(a, 7, true, false, false, false), "the right hand is out of view");
            a.Step(0.1f, true, false, true, true); // it comes back pinching: its beginning was not seen
            Assert.AreEqual(AskArming.Unobserved, a.Ignored);
            Assert.IsTrue(a.Armed, "the button stays on");
            Assert.IsFalse(a.PressAllowed);
            Steps(a, 5, true, false, true, true);
            Assert.IsFalse(a.PressAllowed);
            a.Step(0.1f, true, false, true, false);
            Assert.IsFalse(a.PressAllowed, "its release is not a click");
            a.Step(0.1f, true, false, true, true);
            Assert.IsTrue(a.PressAllowed, "released and pinched again, seen: that one counts");
            // tracking lost in the middle of a good pinch: what comes back is a pinch whose beginning was not seen
            a.Step(0.1f, true, false, false, true);
            a.Step(0.1f, true, false, true, true);
            Assert.AreEqual(AskArming.Unobserved, a.Ignored);
            Assert.IsFalse(a.PressAllowed);
            // the other hand's good pinch still counts while this one is held
            a.Step(0.1f, true, true, true, true);
            Assert.IsTrue(a.PressAllowed);
        }

        [Test]
        public void OtherCodesInTheRoomAndCodesForAnotherPortAreIgnoredSilently()
        {
            var q = new QrPairing(Port);
            q.Start(false);
            Assert.IsFalse(q.Seen(Bytes("https://example.com/")));
            Assert.IsFalse(q.Seen(Bytes("WIFI:T:WPA;S:home;P:secret;;")));
            Assert.IsFalse(q.Seen(null));
            Assert.IsFalse(q.Seen(new byte[] { 0xFF, 0xFE }));
            Assert.IsFalse(q.Seen(Bytes("marshal://pair?v=1&h=192.168.1.197&p=47822&c=481216")), "the app connects on one port only");
            Assert.AreEqual(QrPhase.Scanning, q.Phase);
            Assert.IsNull(q.Pending);
        }
        // ---- (U14, review of U13) the arming rule and the ray click see the same pinch ----

        const long PairTarget = 77;

        /// <summary>
        /// One frame of 0.1 s as the app runs it: the arming rule first, then the ray click, the right hand's ray on
        /// "Pair", the left hand open. Returns whether a click on "Pair" fired in a frame in which the press counts.
        /// </summary>
        static bool Frame(AskArming a, RayClickLogic ray, ref double now, bool usable, float strength)
        {
            now += 100;
            a.Step(0.1f, true, 0f, usable, strength);
            ray.Update(now, new Lotse.Core.HandSample(true, true, 0f, false), 0, new Lotse.Core.HandSample(usable, usable, strength, false), PairTarget, false,
                out var click);
            return click == PairTarget && a.PressAllowed;
        }

        [Test]
        public void AHandThatReappearsPinchedAndLetsGoSlowlyNeverPressesPair()
        {
            var a = new AskArming();
            var ray = new RayClickLogic();
            var now = 0.0;
            for (var i = 0; i < 8; i++) Assert.IsFalse(Frame(a, ray, ref now, false, 0f));
            Assert.IsTrue(a.Armed);
            // back already pinched on "Pair"; the release passes through 0.7 (under the start threshold, over the release one)
            Assert.IsFalse(Frame(a, ray, ref now, true, 1f));
            Assert.AreEqual(AskArming.Unobserved, a.Ignored);
            Assert.IsFalse(Frame(a, ray, ref now, true, 1f));
            Assert.IsFalse(Frame(a, ray, ref now, true, 0.7f), "half released: nothing fires");
            Assert.IsFalse(Frame(a, ray, ref now, true, 0.5f), "the click fires in this frame and must not count");
            Assert.IsFalse(Frame(a, ray, ref now, true, 0f));
            // a pinch begun in view presses it, released the same slow way
            Assert.IsFalse(Frame(a, ray, ref now, true, 1f));
            Assert.IsFalse(Frame(a, ray, ref now, true, 0.7f));
            Assert.IsTrue(Frame(a, ray, ref now, true, 0.5f), "a deliberate click still pairs");
        }

        [Test]
        public void APinchBegunInTheArmingFrameAndLetGoSlowlyNeverPressesPair()
        {
            var a = new AskArming();
            var ray = new RayClickLogic();
            var now = 0.0;
            for (var i = 0; i < 5; i++) Assert.IsFalse(Frame(a, ray, ref now, true, 0f));
            Assert.IsFalse(a.Armed);
            Assert.IsFalse(Frame(a, ray, ref now, true, 1f)); // the frame in which it arms
            Assert.IsTrue(a.Armed);
            Assert.AreEqual(AskArming.BegunBefore, a.Ignored);
            Assert.IsFalse(Frame(a, ray, ref now, true, 0.7f));
            Assert.IsFalse(Frame(a, ray, ref now, true, 0.5f), "begun before it counted: the click must not count");
            Assert.IsFalse(Frame(a, ray, ref now, true, 0f));
            Assert.IsFalse(Frame(a, ray, ref now, true, 1f));
            Assert.IsTrue(Frame(a, ray, ref now, true, 0.5f), "the next click pairs");
        }

        [Test]
        public void TheArmingRuleSeesAPinchFromTheClicksReleaseThreshold()
        {
            Assert.AreEqual(Lotse.Core.GestureMachine.PinchOff, AskArming.PinchingFrom, "a click fires below this; until then the pinch is under way");
        }

    }

    static class ByteArrays
    {
        public static byte[] Concat(this byte[] a, params byte[][] more)
        {
            var list = new System.Collections.Generic.List<byte>(a);
            foreach (var m in more) list.AddRange(m);
            return list.ToArray();
        }
    }
}
