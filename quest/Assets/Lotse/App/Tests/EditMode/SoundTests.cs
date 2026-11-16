#nullable enable
using System.Linq;
using Lotse.App;
using NUnit.Framework;

namespace Lotse.App.Tests
{
    /// <summary>U14: which sound plays (one per 150 ms, the most important of a frame, one for a burst of requests) and what the sounds are made of.</summary>
    public class SoundTests
    {
        [Test]
        public void OfTheSoundsAskedForInOneFrameTheMostImportantPlays()
        {
            var m = new SoundMixer();
            Assert.IsNull(m.Flush(0), "nothing asked, nothing played");
            m.Ask(Sfx.Select);
            m.Ask(Sfx.Denied);
            Assert.AreEqual(Sfx.Denied, m.Flush(1000), "\"Deny\" pressed: the deny note, not the tick");
            Assert.IsNull(m.Flush(1016), "once");
            m.Ask(Sfx.Cleared);
            m.Ask(Sfx.Select);
            Assert.AreEqual(Sfx.Cleared, m.Flush(2000), "a cleared bar and the tick of the tour: the cleared note");
            m.Ask(Sfx.Select);
            m.Ask(Sfx.Stop);
            m.Ask(Sfx.Request);
            Assert.AreEqual(Sfx.Stop, m.Flush(3000));
            m.Ask(Sfx.Select);
            m.Ask(Sfx.Continue);
            Assert.AreEqual(Sfx.Continue, m.Flush(4000));
        }

        [Test]
        public void NeverMoreThanOneSoundPer150MsAndNothingIsPlayedLate()
        {
            var m = new SoundMixer();
            m.Ask(Sfx.Select);
            Assert.AreEqual(Sfx.Select, m.Flush(1000));
            m.Ask(Sfx.Stop);
            Assert.IsNull(m.Flush(1100), "100 ms after the last sound: silent, whatever it is");
            Assert.IsNull(m.Flush(1200), "and what was dropped is not played later");
            m.Ask(Sfx.Select);
            Assert.IsNull(m.Flush(1149));
            m.Ask(Sfx.Select);
            Assert.AreEqual(Sfx.Select, m.Flush(1150), "150 ms on, the next one plays");
            var played = 0;
            for (var t = 2000.0; t < 3000.0; t += 13.9) // a sound asked for in every frame at 72 fps
            {
                m.Ask(Sfx.Key);
                if (m.Flush(t).HasValue) played++;
            }
            Assert.LessOrEqual(played, 7, "at most one per 150 ms");
            Assert.GreaterOrEqual(played, 5);
        }

        [Test]
        public void ABurstOfRequestsMakesOneSound()
        {
            var m = new SoundMixer();
            m.Ask(Sfx.Request);
            m.Ask(Sfx.Request);
            m.Ask(Sfx.Request);
            Assert.AreEqual(Sfx.Request, m.Flush(1000), "three in one frame: one sound");
            m.Ask(Sfx.Request);
            Assert.IsNull(m.Flush(1300), "another one 300 ms later belongs to the burst");
            m.Ask(Sfx.Request);
            Assert.IsNull(m.Flush(2400));
            m.Ask(Sfx.Select);
            Assert.AreEqual(Sfx.Select, m.Flush(2450), "other sounds are not held back by it");
            m.Ask(Sfx.Request);
            Assert.AreEqual(Sfx.Request, m.Flush(2600), "a request after the burst sounds again");
            m.Reset();
            m.Ask(Sfx.Request);
            Assert.AreEqual(Sfx.Request, m.Flush(2610), "a new app remembers nothing");
        }

        [Test]
        public void TheSoundsAreOneSoftFamily()
        {
            foreach (var s in new[] { Sfx.Request, Sfx.Cleared, Sfx.Denied, Sfx.Stop, Sfx.Continue })
            {
                var notes = SoundShapes.Notes(s);
                Assert.IsNotEmpty(notes, s + " has notes");
                var data = SoundShapes.Samples(notes);
                Assert.That(data.Length / (float)SoundShapes.SampleRate, Is.InRange(0.15f, 1.2f), s + " is short");
                Assert.AreEqual(0f, data[0], 1e-6f, s + " starts in silence (no click)");
                Assert.AreEqual(0f, data[data.Length - 1], 1e-3f, s + " ends in silence");
                Assert.LessOrEqual(data.Max(x => System.Math.Abs(x)), 0.95f, s + " is never clipped");
                Assert.GreaterOrEqual(data.Max(x => System.Math.Abs(x)), 0.2f, s + " is audible");
                foreach (var n in notes) Assert.That(n.Hz, Is.InRange(200f, 1000f), s + ": no bass a headset cannot play, no beep");
                // a smooth decay: the last tenth is far below the loudest part
                var tail = data.Skip(data.Length * 9 / 10).Max(x => System.Math.Abs(x));
                Assert.Less(tail, 0.1f, s + " dies away");
            }
            Assert.IsEmpty(SoundShapes.Notes(Sfx.Select), "the select tick stays the one that exists");
        }

        [Test]
        public void EachSoundIsWhatTheBriefAsksFor()
        {
            var request = SoundShapes.Notes(Sfx.Request);
            Assert.AreEqual(2, request.Length, "two notes");
            Assert.Greater(request[1].Hz, request[0].Hz, "rising");
            Assert.Greater(request[1].At, request[0].At);
            var cleared = SoundShapes.Notes(Sfx.Cleared);
            Assert.AreEqual(1, cleared.Length);
            Assert.Greater(cleared[0].Hz, request[1].Hz, "one clear higher note");
            Assert.Greater(SoundShapes.Seconds(cleared), 0.6f, "with a slight tail");
            var denied = SoundShapes.Notes(Sfx.Denied);
            Assert.AreEqual(1, denied.Length);
            Assert.Less(denied[0].Hz, request[0].Hz, "one lower note");
            Assert.Less(SoundShapes.Seconds(denied), 0.3f, "short");
            var stop = SoundShapes.Notes(Sfx.Stop);
            var go = SoundShapes.Notes(Sfx.Continue);
            Assert.AreEqual(2, stop.Length, "a double note");
            Assert.Less(stop.Max(n => n.Hz), 350f, "low");
            Assert.AreEqual(2, go.Length);
            Assert.AreEqual(stop[0].Hz, go[1].Hz, "continue is the stop sound reversed in pitch");
            Assert.AreEqual(stop[1].Hz, go[0].Hz);
            Assert.Greater(stop[0].Hz, stop[1].Hz, "stop falls");
        }
    }
}
