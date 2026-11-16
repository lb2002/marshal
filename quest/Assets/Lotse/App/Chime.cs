using System.Collections.Generic;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>Short generated tones (sine with a soft attack and exponential decay): no audio assets, no licences.</summary>
    public static class Chime
    {
        const int SampleRate = 48000;
        const float Seconds = 0.28f;

        static readonly Dictionary<int, AudioClip> Clips = new Dictionary<int, AudioClip>();

        /// <summary>The sound setting ("Ton: An / Aus"): off, no chime plays.</summary>
        public static bool On { get; set; } = true;

        public static AudioClip Tone(float hz)
        {
            var key = Mathf.RoundToInt(hz);
            if (Clips.TryGetValue(key, out var clip) && clip != null) return clip;

            var n = (int)(SampleRate * Seconds);
            var data = new float[n];
            for (var i = 0; i < n; i++)
            {
                var t = (float)i / SampleRate;
                var attack = Mathf.Clamp01(t / 0.006f);
                var decay = Mathf.Exp(-t * 14f);
                var wave = Mathf.Sin(2f * Mathf.PI * hz * t) + 0.25f * Mathf.Sin(4f * Mathf.PI * hz * t);
                data[i] = 0.6f * attack * decay * wave;
            }
            clip = AudioClip.Create("chime-" + key, n, 1, SampleRate, false);
            clip.SetData(data, 0);
            Clips[key] = clip;
            return clip;
        }

        static AudioClip _tick;

        /// <summary>The lower tone (E4) when a hold completes and when halt fires (U10): different from a select's tick.</summary>
        public const float LowHz = 329.63f;

        /// <summary>
        /// A select (U10: hands give no haptics): a soft 40 ms tick, a short sine with a quick decay. Generated here, no
        /// asset. Quiet: played at a low volume by the button.
        /// </summary>
        public static AudioClip Tick()
        {
            if (_tick != null) return _tick;
            var n = (int)(SampleRate * 0.04f);
            var data = new float[n];
            for (var i = 0; i < n; i++)
            {
                var t = (float)i / SampleRate;
                var attack = Mathf.Clamp01(t / 0.002f);
                data[i] = 0.5f * attack * Mathf.Exp(-t * 110f) * Mathf.Sin(2f * Mathf.PI * 1100f * t);
            }
            _tick = AudioClip.Create("select-tick", n, 1, SampleRate, false);
            _tick.SetData(data, 0);
            return _tick;
        }

        static AudioClip _click;

        /// <summary>A soft key click: a 25 ms burst of filtered noise with a quick decay (deterministic, no assets).</summary>
        public static AudioClip Click()
        {
            if (_click != null) return _click;
            var n = (int)(SampleRate * 0.025f);
            var data = new float[n];
            var seed = 12345u;
            var low = 0f;
            for (var i = 0; i < n; i++)
            {
                seed = seed * 1664525u + 1013904223u;
                var noise = (seed >> 9) / (float)(1 << 23) * 2f - 1f;
                low += (noise - low) * 0.35f; // soften the hiss
                var t = (float)i / SampleRate;
                data[i] = 0.5f * low * Mathf.Exp(-t * 220f) + 0.25f * Mathf.Sin(2f * Mathf.PI * 1800f * t) * Mathf.Exp(-t * 400f);
            }
            _click = AudioClip.Create("key-click", n, 1, SampleRate, false);
            _click.SetData(data, 0);
            return _click;
        }

        /// <summary>A spatial one-shot source on `go` (created once).</summary>
        public static AudioSource SourceOn(GameObject go)
        {
            var s = go.GetComponent<AudioSource>();
            if (s != null) return s;
            s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = 0.3f;
            s.maxDistance = 6f;
            s.volume = 0.35f;
            return s;
        }
    }
}
