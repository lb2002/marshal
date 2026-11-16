#nullable enable
using System;

namespace Lotse.App
{
    /// <summary>The app's sounds (U14), in rising priority: when two are asked for at once, the later one in this list plays.</summary>
    public enum Sfx
    {
        /// <summary>A key of the desk keyboard.</summary>
        Key,
        /// <summary>A select: a button, a row; a done step of the tour.</summary>
        Select,
        /// <summary>A request arrives: two soft rising notes.</summary>
        Request,
        /// <summary>"Deny": one lower, short note.</summary>
        Denied,
        /// <summary>A completed hold, the bar cleared: one clear higher note with a slight tail.</summary>
        Cleared,
        /// <summary>The halt is released: the stop sound reversed in pitch.</summary>
        Continue,
        /// <summary>Stop fired: a low, firm double note.</summary>
        Stop,
    }

    /// <summary>
    /// Which sound plays, if any (U14). Sounds are asked for during a frame and one is chosen at its end: the most
    /// important one asked for, and none at all within GapMs of the last sound, so nothing ever stacks or rattles.
    /// A burst of requests makes one sound: the request notes play at most once per RequestGapMs. Pure.
    /// </summary>
    public sealed class SoundMixer
    {
        /// <summary>Never more than one sound per this long.</summary>
        public const double GapMs = 150;
        /// <summary>Requests arriving within this long of the last request sound make no second one.</summary>
        public const double RequestGapMs = 1500;

        int _pending = -1;
        double _last = double.NegativeInfinity;
        double _lastRequest = double.NegativeInfinity;

        /// <summary>A sound is asked for in this frame.</summary>
        public void Ask(Sfx sound)
        {
            if ((int)sound > _pending) _pending = (int)sound;
        }

        /// <summary>The end of a frame: the sound to play now, or null. What was asked for and not played is dropped, never played late.</summary>
        public Sfx? Flush(double nowMs)
        {
            if (_pending < 0) return null;
            var sound = (Sfx)_pending;
            _pending = -1;
            if (nowMs - _last < GapMs) return null;
            if (sound == Sfx.Request)
            {
                if (nowMs - _lastRequest < RequestGapMs) return null;
                _lastRequest = nowMs;
            }
            _last = nowMs;
            return sound;
        }

        public void Reset()
        {
            _pending = -1;
            _last = double.NegativeInfinity;
            _lastRequest = double.NegativeInfinity;
        }
    }

    /// <summary>One note of a generated sound: a soft tone with a fast attack and a smooth decay.</summary>
    public readonly struct SoundNote
    {
        public readonly float Hz;
        /// <summary>When it begins, in seconds from the sound's start.</summary>
        public readonly float At;
        /// <summary>How fast it dies away (per second, the exponent's rate): larger is shorter.</summary>
        public readonly float Decay;
        public readonly float Level;

        public SoundNote(float hz, float at, float decay, float level)
        {
            Hz = hz;
            At = at;
            Decay = decay;
            Level = level;
        }
    }

    /// <summary>
    /// What each sound is made of (U14): one family, a sine with a little of its octave, 6 ms attack, exponential decay.
    /// No audio files. Stop is E4 then A3 (down a fifth, firm); Continue is the same two notes the other way round;
    /// a request is A4 then E5, soft; cleared is one A5 with a tail; denied one short C4. Pure.
    /// </summary>
    public static class SoundShapes
    {
        public const int SampleRate = 48000;
        public const float Attack = 0.006f;
        const float A3 = 220f, C4 = 261.63f, E4 = 329.63f, A4 = 440f, E5 = 659.25f, A5 = 880f;

        public static SoundNote[] Notes(Sfx sound)
        {
            switch (sound)
            {
                case Sfx.Request: return new[] { new SoundNote(A4, 0f, 16f, 0.42f), new SoundNote(E5, 0.13f, 13f, 0.42f) };
                case Sfx.Cleared: return new[] { new SoundNote(A5, 0f, 7f, 0.5f) };
                case Sfx.Denied: return new[] { new SoundNote(C4, 0f, 26f, 0.6f) };
                case Sfx.Stop: return new[] { new SoundNote(E4, 0f, 15f, 0.7f), new SoundNote(A3, 0.15f, 11f, 0.75f) };
                case Sfx.Continue: return new[] { new SoundNote(A3, 0f, 15f, 0.7f), new SoundNote(E4, 0.15f, 11f, 0.65f) };
                default: return Array.Empty<SoundNote>();
            }
        }

        /// <summary>The length of `notes` in seconds: until the last note has died away to a thousandth.</summary>
        public static float Seconds(SoundNote[] notes)
        {
            var end = 0f;
            foreach (var n in notes) end = Math.Max(end, n.At + 6.9f / n.Decay);
            return Math.Min(end, 1.2f);
        }

        /// <summary>The samples of `notes` (mono, SampleRate). Never above 1 in level; it ends in silence.</summary>
        public static float[] Samples(SoundNote[] notes)
        {
            var count = (int)(SampleRate * Seconds(notes));
            var data = new float[count];
            foreach (var n in notes)
            {
                var from = (int)(n.At * SampleRate);
                for (var i = from; i < count; i++)
                {
                    var t = (float)(i - from) / SampleRate;
                    var attack = Math.Min(1f, t / Attack);
                    var env = attack * (float)Math.Exp(-t * n.Decay);
                    var w = 2.0 * Math.PI * n.Hz * t;
                    data[i] += n.Level * env * (float)(Math.Sin(w) + 0.22 * Math.Sin(2.0 * w) + 0.06 * Math.Sin(3.0 * w));
                }
            }
            // a short fade at the very end, so a cut tail never clicks
            var fade = Math.Min(count, SampleRate / 100);
            for (var i = 0; i < fade; i++) data[count - 1 - i] *= (float)i / fade;
            for (var i = 0; i < count; i++) data[i] = Math.Max(-1f, Math.Min(1f, data[i]));
            return data;
        }
    }
}
