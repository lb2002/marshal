using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// Colours parts of one plain TextMeshPro object without rich text (U5a): ranges of the source string get their own
    /// vertex colour every time the text mesh is built (OnPreRenderText), so untrusted text never needs a colour tag and
    /// many lines can share one text object (one draw call: the agent list's status lines, the whole feed). The rest of
    /// the text keeps the object's own colour.
    /// </summary>
    public sealed class LineColors : MonoBehaviour
    {
        readonly struct Span
        {
            public readonly int Start;
            public readonly int End;
            public readonly Color32 Color;

            public Span(int start, int end, Color32 color)
            {
                Start = start;
                End = end;
                Color = color;
            }
        }

        readonly List<Span> _spans = new List<Span>();
        TextMeshPro _text;

        public static LineColors On(TextMeshPro text)
        {
            var c = text.GetComponent<LineColors>();
            if (c != null) return c;
            c = text.gameObject.AddComponent<LineColors>();
            c._text = text;
            text.OnPreRenderText += c.Apply;
            return c;
        }

        /// <summary>The spans go; call before setting a new text.</summary>
        public void Clear() => _spans.Clear();

        /// <summary>Characters [start, end) of the source string in `color`.</summary>
        public void Add(int start, int end, Color color)
        {
            // TMP hands its vertex colours over in linear light (in a linear project): ours go the same way
            if (QualitySettings.activeColorSpace == ColorSpace.Linear) color = color.linear;
            if (end > start) _spans.Add(new Span(start, end, color));
        }

        /// <summary>Sets `text` (already plain) and rebuilds its mesh with the spans.</summary>
        public void Set(string text)
        {
            _text.text = text;
            _text.ForceMeshUpdate();
        }

        void OnDestroy()
        {
            if (_text != null) _text.OnPreRenderText -= Apply;
        }

        void Apply(TMP_TextInfo info)
        {
            if (_spans.Count == 0) return;
            for (var i = 0; i < info.characterCount; i++)
            {
                var ch = info.characterInfo[i];
                if (!ch.isVisible) continue;
                var index = ch.index;
                for (var s = 0; s < _spans.Count; s++)
                {
                    var span = _spans[s];
                    if (index < span.Start || index >= span.End) continue;
                    var colors = info.meshInfo[ch.materialReferenceIndex].colors32;
                    var v = ch.vertexIndex;
                    // keep TMP's own alpha handling: the span's colour, the text's alpha
                    var c = span.Color;
                    colors[v] = c;
                    colors[v + 1] = c;
                    colors[v + 2] = c;
                    colors[v + 3] = c;
                    break;
                }
            }
        }
    }
}
