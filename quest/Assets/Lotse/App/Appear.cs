using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// Lets a panel appear: it scales up from 0.92 and fades in over 180 ms with an ease-out (Motion); a card also
    /// rises 3 cm into its place. Sits on a transform of its own between the panel's parent and its content, so the
    /// panel's real place (the card's spot, which the hold gate reads) never moves. While a hold runs (Theme.Still)
    /// it stands at its end at once. Allocates nothing per frame once its lists have grown.
    /// </summary>
    public sealed class Appear : MonoBehaviour
    {
        static readonly List<Renderer> Renderers = new List<Renderer>();
        static readonly List<TextMeshPro> Labels = new List<TextMeshPro>();

        float _start;
        bool _rise;

        /// <summary>The appearance is still running.</summary>
        public bool Running => enabled;

        /// <summary>Starts (or restarts) the appearance of `go`'s content.</summary>
        public static Appear Play(GameObject go, bool rise = false)
        {
            var a = go.GetComponent<Appear>();
            if (a == null) a = go.AddComponent<Appear>();
            a._start = Time.time;
            a._rise = rise;
            a.enabled = true;
            a.Apply(Motion.Appear(0, Theme.Still));
            return a;
        }

        void Update()
        {
            var p = Motion.Appear((Time.time - _start) * 1000.0, Theme.Still);
            Apply(p);
            if (p >= 1f) enabled = false;
        }

        void Apply(float p)
        {
            transform.localScale = Vector3.one * Motion.Scale(p);
            transform.localPosition = new Vector3(0f, _rise ? Motion.Rise(p) : 0f, 0f);
            var alpha = Motion.Alpha(p);
            GetComponentsInChildren(true, Renderers);
            for (var i = 0; i < Renderers.Count; i++)
            {
                // our shapes, glows and rings (queues below the text's 3000); the text fades through its alpha
                var m = Renderers[i].sharedMaterial;
                if (m != null && m.renderQueue < 3000) Theme.SetFade(Renderers[i], alpha);
            }
            Renderers.Clear();
            // text: through its material property block, so its mesh is not rebuilt every frame
            GetComponentsInChildren(true, Labels);
            for (var i = 0; i < Labels.Count; i++) Theme.SetTextFade(Labels[i].renderer, alpha);
            Labels.Clear();
        }

        void OnDisable()
        {
            // never left half-way (a panel switched off mid-appearance comes back whole)
            if (_start >= 0f) Apply(1f);
        }
    }
}
