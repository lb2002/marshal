using System;
using System.Collections.Generic;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// The app's sounds (U14): generated in code (SoundShapes; the select tick and the key click are Chime's), quiet,
    /// one family. Anything may ask for a sound during a frame (Play); the app flushes once at the frame's end
    /// (Flush), and the mixer lets at most one through per 150 ms. The "Sounds" setting (Chime.On) silences all of it.
    /// </summary>
    public static class Sounds
    {
        static readonly SoundMixer Mixer = new SoundMixer();
        static readonly Dictionary<Sfx, AudioClip> Clips = new Dictionary<Sfx, AudioClip>();
        static readonly Vector3[] Where = new Vector3[Enum.GetValues(typeof(Sfx)).Length];

        /// <summary>A sound was played (tests count them; nothing else listens).</summary>
        public static event Action<Sfx> Played;

        /// <summary>Asks for `sound` at `at`; whether it plays is decided at the frame's end. Nothing while sounds are off.</summary>
        public static void Play(Sfx sound, Vector3 at)
        {
            if (!Chime.On) return;
            Where[(int)sound] = at;
            Mixer.Ask(sound);
        }

        /// <summary>Once at the end of every frame: plays the one sound the mixer lets through, if any.</summary>
        public static void Flush(double nowMs)
        {
            var sound = Mixer.Flush(nowMs);
            if (!sound.HasValue || !Chime.On) return;
            AudioSource.PlayClipAtPoint(Clip(sound.Value), Where[(int)sound.Value], Volume(sound.Value));
            Played?.Invoke(sound.Value);
        }

        /// <summary>A new app (a test's next run): nothing pending, no memory of the last sound.</summary>
        public static void Reset() => Mixer.Reset();

        /// <summary>How many clips were generated so far (tests: none is made when a sound plays).</summary>
        public static int Built { get; private set; }

        /// <summary>
        /// Once at the app's start (U14b): every clip is generated now, so that the first request, the first approval
        /// play without a hitch on the main thread. Also while sounds are off: switching them on costs nothing later.
        /// </summary>
        public static void Warm()
        {
            foreach (Sfx sound in Enum.GetValues(typeof(Sfx))) Clip(sound);
        }

        /// <summary>Quiet: the generated clips are soft already; the stop and the cleared note a little above the tick.</summary>
        public static float Volume(Sfx sound) => sound == Sfx.Stop || sound == Sfx.Continue ? 0.34f : sound == Sfx.Select || sound == Sfx.Key ? 0.25f : 0.3f;

        /// <summary>Forgets the generated clips (tests: the next app start generates them again).</summary>
        public static void Unload()
        {
            foreach (var clip in Clips.Values)
                if (clip != null) UnityEngine.Object.Destroy(clip);
            Clips.Clear();
        }

        public static AudioClip Clip(Sfx sound)
        {
            if (sound == Sfx.Select) return Chime.Tick();
            if (sound == Sfx.Key) return Chime.Click();
            if (Clips.TryGetValue(sound, out var clip) && clip != null) return clip;
            var data = SoundShapes.Samples(SoundShapes.Notes(sound));
            clip = AudioClip.Create("sound-" + sound, data.Length, 1, SoundShapes.SampleRate, false);
            clip.SetData(data, 0);
            Clips[sound] = clip;
            Built++;
            return clip;
        }
    }
}
