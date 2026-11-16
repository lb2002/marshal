using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lotse.App
{
    /// <summary>
    /// Builds the slice's pieces in code: text, glass panels and chips (Theme), spheres, rings.
    /// All panels face the user: in the anchor's frame the user is on the -z side, so the content's
    /// front points to -z (TextMeshPro and the Quad primitive both read correctly from there).
    /// </summary>
    public static class Ui
    {
        /// <summary>Body text: about 0.011 m line height (fontSize x 0.1 x 1.15 for Liberation Sans).</summary>
        public const float BodySize = Theme.Body;
        /// <summary>Small text (status lines, captions).</summary>
        public const float SmallSize = Theme.Small;
        /// <summary>Titles.</summary>
        public const float TitleSize = Theme.Title;

        // render queues: glows, then panels before chips before rings, all before the transparent text (3000)
        public const int QueueGlow = 2989;
        public const int QueuePanel = 2990;
        /// <summary>A card that carries controls: after the panel it lies on, before the controls on it.</summary>
        public const int QueueCard = 2991;
        public const int QueueOnPanel = 2992;
        public const int QueueRing = 2993;

        public static Color PanelColor => Theme.Glass;
        public static Color TextColor => Theme.Text;
        public static Color DimText => Theme.Muted;

        static Shader _shader;
        static Material _opaque;
        static readonly System.Collections.Generic.Dictionary<int, Material> Transparent = new System.Collections.Generic.Dictionary<int, Material>();
        static MaterialPropertyBlock _block;
        static readonly int ColorId = Shader.PropertyToID("_Color");

        static Shader Shader
        {
            get
            {
                if (_shader == null) _shader = Resources.Load<Shader>("LotseUnlit");
                return _shader;
            }
        }

        public static Material Opaque
        {
            get
            {
                if (_opaque != null) return _opaque;
                _opaque = new Material(Shader) { name = "Lotse Opaque", renderQueue = (int)RenderQueue.Geometry };
                _opaque.SetFloat("_SrcBlend", (float)BlendMode.One);
                _opaque.SetFloat("_DstBlend", (float)BlendMode.Zero);
                _opaque.SetFloat("_ZWrite", 1f);
                _opaque.SetFloat("_Cull", (float)CullMode.Back);
                _opaque.SetFloat("_Shade", 1f);
                return _opaque;
            }
        }

        /// <summary>Alpha-blended, no depth write, double sided, at the given render queue.</summary>
        public static Material TransparentAt(int queue)
        {
            if (Transparent.TryGetValue(queue, out var m) && m != null) return m;
            m = new Material(Shader) { name = "Lotse Transparent " + queue, renderQueue = queue };
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_Cull", (float)CullMode.Off);
            m.SetFloat("_Shade", 0f);
            Transparent[queue] = m;
            return m;
        }

        public static Color ToColor(Rgb c, float a = 1f) => new Color(c.R, c.G, c.B, a);

        public static Vector3 ToVector(Vec3 v) => new Vector3(v.X, v.Y, v.Z);

        public static Vec3 ToVec(Vector3 v) => new Vec3(v.x, v.y, v.z);

        /// <summary>The colour SetColor gave a renderer.</summary>
        public static Color ColorOf(Renderer r)
        {
            if (_block == null) _block = new MaterialPropertyBlock();
            r.GetPropertyBlock(_block);
            return _block.GetColor(ColorId);
        }

        public static void SetColor(Renderer r, Color c)
        {
            if (_block == null) _block = new MaterialPropertyBlock();
            r.GetPropertyBlock(_block);
            _block.SetColor(ColorId, c);
            r.SetPropertyBlock(_block);
        }

        public static GameObject Child(Transform parent, string name, Vector3 localPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            return go;
        }

        /// <summary>
        /// A rounded rectangle of `size` centred on localPos: a glass panel (QueuePanel), or a pill-shaped chip on a
        /// panel (QueueOnPanel: badges, buttons). Its localScale is its size, so it can be resized by scaling it.
        /// </summary>
        public static MeshRenderer Panel(Transform parent, string name, Vector3 localPos, Vector2 size, Color color, int queue = QueuePanel,
            bool blocksRay = true)
        {
            var glass = queue == QueuePanel;
            var r = Theme.Shape(parent, name, localPos, size, color, glass ? Theme.GlassMaterial : Theme.ChipMaterial);
            // a glass panel stops the hand ray, except a name tag (it is its agent's own target)
            if (glass && blocksRay) r.gameObject.AddComponent<RayBlocker>();
            return r;
        }

        /// <summary>
        /// World-space TextMeshPro with its top-centre at localPos, wrapping at `width`. Rich text is OFF unless
        /// asked for: anything from the bridge or the pack (commands, paths, explanations, replies, agent names,
        /// summaries) must stay plain, so no tag in it can restyle the card. Only our own fixed labels use rich text.
        /// </summary>
        public static TextMeshPro Text(Transform parent, string name, string text, Vector3 localPos, float width,
            float fontSize, Color color, TextAlignmentOptions align = TextAlignmentOptions.TopLeft, bool richText = false)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshPro>();
            // Title is SemiBold, Body Medium (U10: medium weights read best in the headset), Small Regular
            t.font = fontSize >= Theme.Title - 1e-4f ? Fonts.SemiBold : fontSize >= Theme.Body - 1e-4f ? Fonts.Medium : Fonts.Regular;
            t.rectTransform.pivot = new Vector2(0.5f, 1f);
            t.rectTransform.sizeDelta = new Vector2(width, 0.01f);
            go.transform.localPosition = localPos;
            t.richText = richText;
            t.parseCtrlCharacters = richText; // plain text: no \n or \u0041 decoding either
            t.textWrappingMode = TextWrappingModes.Normal;
            t.overflowMode = TextOverflowModes.Overflow;
            t.fontSize = fontSize;
            t.color = color;
            t.alignment = align;
            t.text = richText ? text : Texts.Plain(text);
            FitHeight(t);
            return t;
        }

        /// <summary>The Mono style (U9): Geist Mono at the Body size in the mono colour, for commands and paths.</summary>
        public static TextMeshPro Mono(Transform parent, string name, Vector3 localPos, float width)
        {
            var t = Text(parent, name, "", localPos, width, Theme.Body, Theme.Mono);
            t.font = Fonts.Mono;
            return t;
        }

        /// <summary>
        /// Lays a wrapped text (pivot at its top) on rows `pitch` high: the line spacing that gives that pitch, the rect as
        /// high as its rows. Returns the number of rows; `shift` is what to add to the y of its top so that every line
        /// stands in the middle of its row (measured from the laid-out first line).
        /// </summary>
        public static int Rows(TextMeshPro t, float pitch, out float shift)
        {
            var text = t.text;
            float Baselines(float spacing)
            {
                t.lineSpacing = spacing;
                t.text = "Ag\nAg";
                t.ForceMeshUpdate();
                var i = t.textInfo;
                return i.lineCount >= 2 ? i.lineInfo[0].baseline - i.lineInfo[1].baseline : 0f;
            }
            var p0 = Baselines(0f);
            var p100 = Baselines(100f);
            t.lineSpacing = Mathf.Abs(p100 - p0) < 1e-6f ? 0f : 100f * (pitch - p0) / (p100 - p0);
            t.text = text;
            t.ForceMeshUpdate();
            var info = t.textInfo;
            var lines = Mathf.Max(1, info.lineCount);
            shift = 0f;
            if (info.lineCount > 0) shift = -pitch * 0.5f - (info.lineInfo[0].ascender + info.lineInfo[0].descender) * 0.5f;
            t.rectTransform.sizeDelta = new Vector2(t.rectTransform.sizeDelta.x, lines * pitch);
            return lines;
        }

        /// <summary>One line centred vertically on its own position in a box `height` high (its pivot moves to the middle).</summary>
        public static void Middle(TextMeshPro t, float width, float height, TextAlignmentOptions align)
        {
            t.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            t.rectTransform.sizeDelta = new Vector2(width, height);
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.alignment = align;
        }

        /// <summary>Sets untrusted text on a plain TextMeshPro object (made by Text with richText false).</summary>
        public static void SetPlain(TextMeshPro t, string text) => t.text = Texts.Plain(text);

        /// <summary>What a text cut for space ends with.</summary>
        public const string CutSuffix = "… (shortened)";

        /// <summary>
        /// Cuts a plain text object to at most `maxLines` wrapped lines and `maxHeight` metres; the cut ends in
        /// "… (gekürzt)". Below one line it empties the text. Returns whether anything was cut.
        /// </summary>
        public static bool CapLines(TextMeshPro t, int maxLines, float maxHeight = float.PositiveInfinity)
        {
            var full = t.text;
            if (string.IsNullOrEmpty(full)) return false;
            t.ForceMeshUpdate();
            var info = t.textInfo;
            var lines = info.lineCount;
            if (lines <= 0) return false;
            var lineHeight = t.rectTransform.sizeDelta.y / lines;
            var keep = Mathf.Min(lines, maxLines);
            if (lineHeight > 0f && !float.IsPositiveInfinity(maxHeight))
                keep = Mathf.Min(keep, Mathf.FloorToInt((maxHeight + 1e-5f) / lineHeight));
            if (keep >= lines) return false;
            if (keep <= 0)
            {
                t.text = "";
                FitHeight(t);
                return true;
            }
            // the last character of each line, in the source string (read before the text changes)
            var ends = new int[keep];
            for (var i = 0; i < keep; i++)
                ends[i] = Mathf.Min(full.Length, info.characterInfo[info.lineInfo[i].lastCharacterIndex].index + 1);
            // keep lines, the last one ending in the suffix; a line fewer while it is still too tall (the first
            // line is taller than the average the estimate used)
            for (; keep > 0; keep--)
            {
                var end = ends[keep - 1];
                while (true)
                {
                    if (end > 0 && char.IsHighSurrogate(full[end - 1])) end--;
                    t.text = full.Substring(0, end).TrimEnd() + " " + CutSuffix;
                    t.ForceMeshUpdate();
                    if (t.textInfo.lineCount <= keep || end == 0) break;
                    end = Mathf.Max(0, end - 4);
                }
                if (FitHeight(t) <= maxHeight + 1e-5f) return true;
            }
            t.text = "";
            FitHeight(t);
            return true;
        }

        /// <summary>One line's height in metres at the text object's size, from the font's own metrics.</summary>
        public static float LineHeight(TextMeshPro t)
        {
            var face = t.font.faceInfo;
            return face.lineHeight / face.pointSize * t.fontSize * UiMetrics.EmPerSize;
        }

        /// <summary>
        /// Sets the text's rect to its own height and returns it: from its first line's ascender to its last line's
        /// descender, as laid out. Exact (U11): TextMeshPro's preferred height, used before, is rounded up to hundredths
        /// of a unit, whole centimetres here, which left up to a centimetre of empty space under every text.
        /// </summary>
        public static float FitHeight(TextMeshPro t)
        {
            var h = 0f;
            if (!string.IsNullOrEmpty(t.text))
            {
                t.ForceMeshUpdate(true, false);
                var info = t.textInfo;
                if (info != null && info.lineCount > 0) h = info.lineInfo[0].ascender - info.lineInfo[info.lineCount - 1].descender;
            }
            var size = t.rectTransform.sizeDelta;
            if (!Mathf.Approximately(size.y, h)) t.rectTransform.sizeDelta = new Vector2(size.x, h);
            return h;
        }

        /// <summary>
        /// `text` (plain, untrusted) cut to one line of `width` metres at the text object's size, ending in "…" when cut
        /// (a binary search on its length, measured with the object itself; never splits a surrogate pair).
        /// </summary>
        public static string OneLine(TextMeshPro t, string text, float width)
        {
            var plain = Texts.Plain(text.Replace('\n', ' '));
            if (Wide(t, plain) <= width) return plain;
            int lo = 0, hi = plain.Length;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (Wide(t, Cut(plain, mid) + Ellipsis) <= width) lo = mid;
                else hi = mid - 1;
            }
            return Cut(plain, lo).TrimEnd() + Ellipsis;
        }

        public const string Ellipsis = "…";

        /// <summary>
        /// `text` (plain, untrusted) on at most two lines of `width` at the text object's size, broken between words: the
        /// first line, and the second (null when one line holds it), which ends in "…" when cut (U10: a plan step).
        /// </summary>
        public static (string first, string second) SplitTwo(TextMeshPro t, string text, float width)
        {
            var plain = Texts.Plain(text.Replace('\n', ' ')).Trim();
            if (Wide(t, plain) <= width) return (plain, null);
            var words = plain.Split(' ');
            var first = "";
            var used = 0;
            for (; used < words.Length; used++)
            {
                var next = first.Length == 0 ? words[used] : first + " " + words[used];
                if (Wide(t, next) > width) break;
                first = next;
            }
            string rest;
            if (used == 0)
            {
                // the first word is wider than the line: it breaks inside, as far as the line holds
                int lo = 1, hi = plain.Length;
                while (lo < hi)
                {
                    var mid = (lo + hi + 1) / 2;
                    if (Wide(t, Cut(plain, mid)) <= width) lo = mid;
                    else hi = mid - 1;
                }
                first = Cut(plain, lo);
                rest = plain.Substring(first.Length).TrimStart();
            }
            else rest = string.Join(" ", words, used, words.Length - used);
            return (first, OneLine(t, rest, width));
        }

        /// <summary>
        /// `text` (plain, untrusted) wrapped at the text object's width to at most two lines; the second ends in "…" when
        /// cut (U5b: overview titles). The object's wrapping must be on.
        /// </summary>
        public static string TwoLines(TextMeshPro t, string text)
        {
            var plain = Texts.Plain(text.Replace('\n', ' '));
            var width = t.rectTransform.sizeDelta.x;
            if (Lines(t, plain) <= 2) return plain;
            t.text = plain;
            t.ForceMeshUpdate();
            var info = t.textInfo;
            var end = Mathf.Min(plain.Length, info.characterInfo[info.lineInfo[0].lastCharacterIndex].index + 1);
            var first = plain.Substring(0, end).TrimEnd();
            var rest = plain.Substring(end).TrimStart();
            // the second line cut to the width, a little more while the pair still wraps into a third line
            for (var w = width; w > 0.005f; w -= 0.003f)
            {
                var both = first + " " + OneLine(t, rest, w);
                if (Lines(t, both) <= 2) return both;
            }
            return OneLine(t, plain, width);
        }

        static int Lines(TextMeshPro t, string s)
        {
            t.text = s;
            t.ForceMeshUpdate();
            return t.textInfo.lineCount;
        }

        static string Cut(string s, int n)
        {
            if (n > 0 && n < s.Length && char.IsHighSurrogate(s[n - 1])) n--;
            return s.Substring(0, n);
        }

        static float Wide(TextMeshPro t, string s) => t.GetPreferredValues(s, Mathf.Infinity, Mathf.Infinity).x;

        /// <summary>A sphere primitive without collider, opaque, shaded.</summary>
        public static MeshRenderer Sphere(Transform parent, string name, float diameter, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.Destroy(go.GetComponent<Collider>());
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localScale = Vector3.one * diameter;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return r;
        }

        /// <summary>A flat ring (see RingMesh) as its own object; returns its filter for later updates.</summary>
        public static MeshFilter Ring(Transform parent, string name, Vector3 localPos, float inner, float outer, float fraction, Color color, int queue = QueueRing)
        {
            var go = Child(parent, name, localPos);
            var f = go.AddComponent<MeshFilter>();
            f.sharedMesh = new Mesh { name = name };
            RingMesh.Build(f.sharedMesh, inner, outer, fraction);
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = TransparentAt(queue);
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            SetColor(r, color);
            return f;
        }
    }
}
