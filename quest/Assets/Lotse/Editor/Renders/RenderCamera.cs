using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Lotse.EditorTools
{
    /// <summary>
    /// The seated user's eye: one (not stereo) camera that is also the app's head, rendering on demand into a
    /// 1600x1000 texture that is written as a PNG. Behind everything, fixed to the camera, a plane with the real
    /// passthrough photo of the user's desk (RenderBackdrop/desk.png), so the panels are judged against a real room.
    /// </summary>
    sealed class RenderCamera
    {
        public const int Width = 1600;
        public const int Height = 1000;
        /// <summary>Vertical field of view (about 85 degrees across): roughly what a Quest 3 eye sees sharply.</summary>
        public const float FieldOfView = 60f;
        const float BackdropDistance = 12f;
        const string BackdropPath = "Assets/Lotse/Editor/RenderBackdrop/desk.png";

        /// <summary>One written render and what its pixels look like.</summary>
        public readonly struct Shot
        {
            public readonly string Path;
            public readonly float MeanLuminance;
            public readonly float DarkFraction;
            public readonly float Spread;

            public Shot(string path, float mean, float dark, float spread)
            {
                Path = path;
                MeanLuminance = mean;
                DarkFraction = dark;
                Spread = spread;
            }

            /// <summary>Not black: a real image has light pixels and contrast.</summary>
            public bool LooksRendered => MeanLuminance > 0.04f && DarkFraction < 0.9f && Spread > 0.02f;

            public override string ToString() =>
                $"{System.IO.Path.GetFileName(Path)}: mean luminance {MeanLuminance:0.000}, dark {DarkFraction:P1}, spread {Spread:0.000}";
        }

        readonly string _outDir;
        readonly GameObject _backdrop;
        Texture2D _readback;
        int _w = Width;
        int _h = Height;

        /// <summary>
        /// (U14) The video's picture: `width` x `height`, and behind everything the desk photo without the top
        /// `cropTop` of its height (a framed photo of a person stands there) and strongly blurred, so nothing in the
        /// room can be recognised.
        /// </summary>
        public void VideoMode(int width, int height, float cropTop)
        {
            _w = width;
            _h = height;
            Camera.aspect = (float)width / height;
            var full = new Texture2D(2, 2, TextureFormat.RGB24, true);
            if (!full.LoadImage(File.ReadAllBytes(BackdropPath))) throw new IOException("cannot read " + BackdropPath);
            // its third mip level (1/8 of its size), box-blurred three times each way: a smooth blur about 60 px wide
            // in the video's picture
            const int mip = 3;
            int sw = Mathf.Max(1, full.width >> mip), sh = Mathf.Max(1, full.height >> mip);
            var px = full.GetPixels(mip);
            var tmp = new Color[px.Length];
            const int radius = 5;
            for (var pass = 0; pass < 3; pass++)
            {
                for (var y = 0; y < sh; y++)
                    for (var x = 0; x < sw; x++)
                    {
                        var sum = Color.clear;
                        for (var k = -radius; k <= radius; k++) sum += px[y * sw + Mathf.Clamp(x + k, 0, sw - 1)];
                        tmp[y * sw + x] = sum / (2 * radius + 1);
                    }
                for (var y = 0; y < sh; y++)
                    for (var x = 0; x < sw; x++)
                    {
                        var sum = Color.clear;
                        for (var k = -radius; k <= radius; k++) sum += tmp[Mathf.Clamp(y + k, 0, sh - 1) * sw + x];
                        px[y * sw + x] = sum / (2 * radius + 1);
                    }
            }
            var small = new Texture2D(sw, sh, TextureFormat.RGB24, false)
            {
                name = "desk (blurred)", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            };
            small.SetPixels(px);
            small.Apply(false);
            Object.DestroyImmediate(full);
            var mat = _backdrop.GetComponent<MeshRenderer>().sharedMaterial;
            mat.mainTexture = small;
            mat.SetTexture("_BaseMap", small);
            // the lower (1 - cropTop) of the photo, as wide as the picture's shape asks, centred
            var sy = 1f - cropTop;
            var sx = Mathf.Min(1f, sy * ((float)width / height) / ((float)small.width / small.height));
            mat.SetTextureScale("_BaseMap", new Vector2(sx, sy));
            mat.SetTextureOffset("_BaseMap", new Vector2((1f - sx) * 0.5f, 0f));
            var h = 2f * BackdropDistance * Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad);
            _backdrop.transform.localScale = new Vector3(h * width / height, h, 1f);
        }

        /// <summary>(U14) One frame of a sequence, seen from `from`: written to `path`. The head is put back at once.</summary>
        public void Frame(string path, Vector3 from, Quaternion look)
        {
            var position = Head.position;
            var rotation = Head.rotation;
            try
            {
                Head.SetPositionAndRotation(from, look);
                Capture();
                File.WriteAllBytes(path, _readback.EncodeToPNG());
            }
            finally
            {
                Head.SetPositionAndRotation(position, rotation);
            }
        }

        public Camera Camera { get; }
        public Transform Head => Camera.transform;
        public List<Shot> Shots { get; } = new List<Shot>();

        public RenderCamera(string outDir, Vector3 eye)
        {
            _outDir = outDir;
            var go = new GameObject("Head", typeof(Camera)) { tag = "MainCamera" };
            Camera = go.GetComponent<Camera>();
            Camera.enabled = false; // renders only on demand
            Camera.fieldOfView = FieldOfView;
            Camera.aspect = (float)Width / Height;
            Camera.nearClipPlane = 0.03f;
            Camera.farClipPlane = BackdropDistance * 2f;
            Camera.clearFlags = CameraClearFlags.SolidColor;
            Camera.backgroundColor = Color.black;
            go.transform.position = eye;
            _backdrop = MakeBackdrop(go.transform);
        }

        public bool BackdropShown
        {
            get => _backdrop.activeSelf;
            set => _backdrop.SetActive(value);
        }

        /// <summary>A plain mid-grey backdrop instead of the desk photo (U9: the window alone, for the submission).</summary>
        public bool Plain
        {
            get => !_backdrop.activeSelf;
            set
            {
                _backdrop.SetActive(!value);
                Camera.backgroundColor = value ? new Color(0.5f, 0.5f, 0.5f, 1f) : Color.black;
            }
        }

        /// <summary>The head turns (it never moves) to look at a point, upright.</summary>
        public void LookAt(Vector3 point) => Head.rotation = Quaternion.LookRotation(point - Head.position, Vector3.up);

        static GameObject MakeBackdrop(Transform head)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "desk", wrapMode = TextureWrapMode.Clamp };
            if (!tex.LoadImage(File.ReadAllBytes(BackdropPath))) throw new IOException("cannot read " + BackdropPath);
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) throw new IOException("URP Unlit shader not found");
            var mat = new Material(shader) { name = "Backdrop", mainTexture = tex };
            mat.SetTexture("_BaseMap", tex);
            mat.SetColor("_BaseColor", Color.white);
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Backdrop";
            Object.DestroyImmediate(quad.GetComponent<Collider>());
            quad.GetComponent<MeshRenderer>().sharedMaterial = mat;
            quad.transform.SetParent(head, false);
            var h = 2f * BackdropDistance * Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad);
            quad.transform.localPosition = new Vector3(0f, 0f, BackdropDistance);
            quad.transform.localScale = new Vector3(h * Width / Height, h, 1f);
            return quad;
        }

        /// <summary>Renders the camera now into the readback texture.</summary>
        void Capture()
        {
            var rt = new RenderTexture(_w, _h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4, name = "Lotse render" };
            var active = RenderTexture.active;
            try
            {
                Camera.targetTexture = rt;
                Camera.Render();
                RenderTexture.active = rt;
                if (_readback == null || _readback.width != _w || _readback.height != _h) _readback = new Texture2D(_w, _h, TextureFormat.RGB24, false);
                _readback.ReadPixels(new Rect(0, 0, _w, _h), 0, 0);
                _readback.Apply();
            }
            finally
            {
                Camera.targetTexture = null;
                RenderTexture.active = active;
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        /// <summary>Renders the camera now and writes `name`.png; returns its pixel statistics.</summary>
        public Shot Render(string name)
        {
            Capture();
            var path = Path.Combine(_outDir, name + ".png");
            File.WriteAllBytes(path, _readback.EncodeToPNG());
            var shot = Measure(path, _readback.GetPixels32());
            Shots.Add(shot);
            Debug.Log("[Renders] wrote " + shot);
            return shot;
        }

        /// <summary>Renders without writing a file: the fraction of pixels brighter than `threshold` (the URP check).</summary>
        public float LitFraction(float threshold)
        {
            Capture();
            var px = _readback.GetPixels32();
            var lit = 0;
            foreach (var p in px)
                if (Luminance(p) > threshold) lit++;
            return (float)lit / px.Length;
        }

        static float Luminance(Color32 p) => (0.2126f * p.r + 0.7152f * p.g + 0.0722f * p.b) / 255f;

        static Shot Measure(string path, Color32[] px)
        {
            double sum = 0, sq = 0;
            var dark = 0;
            foreach (var p in px)
            {
                var l = Luminance(p);
                sum += l;
                sq += l * l;
                if (l < 0.03f) dark++;
            }
            var mean = sum / px.Length;
            var spread = System.Math.Sqrt(System.Math.Max(0, sq / px.Length - mean * mean));
            return new Shot(path, (float)mean, (float)dark / px.Length, (float)spread);
        }

        /// <summary>
        /// A close-up of one panel: the eye 0.6 m in front of the centre of what it draws, looking straight at it.
        /// The head is put back at once, before the app's next frame, so the app never sees it move.
        /// </summary>
        public Shot CloseUp(string name, GameObject panel, float distance = 0f)
        {
            var bounds = BoundsOf(panel);
            var facing = panel.transform.forward; // the panels' text is on their -z side, facing the user
            if (distance <= 0f)
            {
                // U10: as far as it takes to have all of the panel in the picture (the window is 1.57 m wide now)
                var right = panel.transform.right;
                var up = panel.transform.up;
                var e = bounds.extents;
                var halfW = Mathf.Abs(right.x) * e.x + Mathf.Abs(right.y) * e.y + Mathf.Abs(right.z) * e.z;
                var halfH = Mathf.Abs(up.x) * e.x + Mathf.Abs(up.y) * e.y + Mathf.Abs(up.z) * e.z;
                var tanV = Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad);
                distance = 1.06f * Mathf.Max(halfH / tanV, halfW / (tanV * Width / Height));
            }
            var position = Head.position;
            var rotation = Head.rotation;
            try
            {
                Head.SetPositionAndRotation(bounds.center - facing * distance, Quaternion.LookRotation(facing, Vector3.up));
                return Render(name);
            }
            finally
            {
                Head.SetPositionAndRotation(position, rotation);
            }
        }

        /// <summary>What the active renderers under `go` cover (the panel and its text).</summary>
        public static Bounds BoundsOf(GameObject go)
        {
            var any = false;
            var b = new Bounds(go.transform.position, Vector3.zero);
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (!any) b = r.bounds;
                else b.Encapsulate(r.bounds);
                any = true;
            }
            return b;
        }
    }
}
