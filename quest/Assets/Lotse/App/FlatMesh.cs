using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lotse.App
{
    /// <summary>
    /// Flat marks drawn in one vertex-coloured mesh with one shared opaque material (the orbs' flat Figure shader), so many
    /// small shapes cost one draw call (U6: the checklist's icons, a column's tint and progress bar, the rail's gaze line).
    /// A see-through colour on the panel is mixed with the panel's graphite first (an opaque mark in front of the panel
    /// looks the same). Shapes are wound to face the user (-z); x right, y up, in the owner's local metres.
    /// </summary>
    /// <summary>A FlatMesh's mesh is its own: it goes when its object goes (a block built again does not leak one).</summary>
    public sealed class FlatMeshOwner : MonoBehaviour
    {
        public Mesh Mesh;

        void OnDestroy()
        {
            if (Mesh != null) Destroy(Mesh);
        }
    }

    public sealed class FlatMesh
    {
        static Material _material;

        readonly List<Vector3> _verts = new List<Vector3>();
        readonly List<Color> _colors = new List<Color>();
        readonly List<int> _tris = new List<int>();
        readonly List<Vector3> _normals = new List<Vector3>();
        readonly Mesh _mesh;

        public MeshRenderer Renderer { get; }
        public int VertexCount => _verts.Count;

        static Material Material
        {
            get
            {
                if (_material != null) return _material;
                _material = new Material(Resources.Load<Shader>("LotseFigure")) { name = "Lotse Marks" };
                _material.SetFloat("_Shade", 0f);
                return _material;
            }
        }

        public FlatMesh(Transform parent, string name, float z)
        {
            var go = Ui.Child(parent, name, new Vector3(0f, 0f, z));
            var f = go.AddComponent<MeshFilter>();
            _mesh = new Mesh { name = name };
            _mesh.MarkDynamic();
            f.sharedMesh = _mesh;
            Renderer = go.AddComponent<MeshRenderer>();
            Renderer.sharedMaterial = Material;
            Renderer.shadowCastingMode = ShadowCastingMode.Off;
            Renderer.receiveShadows = false;
            go.AddComponent<FlatMeshOwner>().Mesh = _mesh;
        }

        /// <summary>`c` at alpha `a` over the panel's graphite, as the mock-up (sRGB) blends it.</summary>
        public static Color OnPanel(Rgb c, float a) => new Color(
            a * c.R + (1f - a) * Look.Graphite.R, a * c.G + (1f - a) * Look.Graphite.G, a * c.B + (1f - a) * Look.Graphite.B, 1f);

        public static Color Solid(Rgb c) => new Color(c.R, c.G, c.B, 1f);

        /// <summary>
        /// One layer's depth: shapes that overlap in one mesh must not share a plane (they are opaque and write depth:
        /// coplanar ones flicker in wedges). A shape `n` layers behind the marks is drawn at z = n x Layer.
        /// </summary>
        public const float Layer = 0.0003f;

        public void Clear()
        {
            _verts.Clear();
            _colors.Clear();
            _tris.Clear();
        }

        /// <summary>Uploads what was added (the mesh is rebuilt; call only when the marks change, or for a small mesh).</summary>
        public void Apply()
        {
            _mesh.Clear();
            _mesh.SetVertices(_verts);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_tris, 0);
            // the lists are kept, so a mesh redrawn every frame (breathing lights, a travelling dot) allocates nothing
            while (_normals.Count < _verts.Count) _normals.Add(Vector3.back);
            if (_normals.Count > _verts.Count) _normals.RemoveRange(_verts.Count, _normals.Count - _verts.Count);
            _mesh.SetNormals(_normals);
            _mesh.RecalculateBounds();
        }

        int Add(Vector3 v, Color c)
        {
            _verts.Add(v);
            _colors.Add(c);
            return _verts.Count - 1;
        }

        /// <summary>Vertex colours are not converted by the engine: in linear colour space the sRGB colour goes in as linear.</summary>
        static Color Vertex(Color srgb) => QualitySettings.activeColorSpace == ColorSpace.Linear ? srgb.linear : srgb;

        public void Rect(Rect r, Color color, float z = 0f)
        {
            var c = Vertex(color);
            var i = Add(new Vector3(r.xMin, r.yMin, z), c);
            Add(new Vector3(r.xMin, r.yMax, z), c);
            Add(new Vector3(r.xMax, r.yMax, z), c);
            Add(new Vector3(r.xMax, r.yMin, z), c);
            _tris.Add(i);
            _tris.Add(i + 1);
            _tris.Add(i + 2);
            _tris.Add(i);
            _tris.Add(i + 2);
            _tris.Add(i + 3);
        }

        /// <summary>A rounded rectangle (corner arcs of `radius`).</summary>
        public void RoundedRect(Rect r, float radius, Color color, float z = 0f)
        {
            const int arc = 5;
            radius = Mathf.Min(radius, Mathf.Min(r.width, r.height) * 0.5f);
            var c = Vertex(color);
            var centre = Add(new Vector3(r.center.x, r.center.y, z), c);
            Outline(r, radius, 0f, c, z, arc);
            var n = _verts.Count - centre - 1;
            for (var i = 0; i < n; i++)
            {
                _tris.Add(centre);
                _tris.Add(centre + 1 + i);
                _tris.Add(centre + 1 + (i + 1) % n);
            }
        }

        /// <summary>The outline of a rounded rectangle, `grow` outside it: 4 x (arc + 1) vertices, clockwise from the top left corner.</summary>
        void Outline(Rect r, float radius, float grow, Color c, float z, int arc)
        {
            for (var k = 0; k < 4; k++)
            {
                var cx = k == 0 || k == 3 ? r.xMin + radius : r.xMax - radius;
                var cy = k < 2 ? r.yMax - radius : r.yMin + radius;
                var start = 180f - 90f * k;
                for (var i = 0; i <= arc; i++)
                {
                    var a = (start - 90f * i / arc) * Mathf.Deg2Rad;
                    Add(new Vector3(cx + Mathf.Cos(a) * (radius + grow), cy + Mathf.Sin(a) * (radius + grow), z), c);
                }
            }
        }

        /// <summary>
        /// A soft halo around a rounded rectangle (U13: the glow of a stop-bar light): `inner` at the rectangle's edge
        /// fading to `outer` (the surface behind it) `spread` beyond the edge. Opaque like every mark: it belongs behind
        /// the light and whatever else it must not hide.
        /// </summary>
        public void Halo(Rect r, float radius, float spread, Color inner, Color outer, float z = 0f)
        {
            const int arc = 4;
            radius = Mathf.Min(radius, Mathf.Min(r.width, r.height) * 0.5f);
            var first = _verts.Count;
            Outline(r, radius, 0f, Vertex(inner), z, arc);
            Outline(r, radius, spread, Vertex(outer), z, arc);
            var n = 4 * (arc + 1);
            for (var i = 0; i < n; i++)
            {
                var a = first + i;
                var b = first + (i + 1) % n;
                _tris.Add(a);
                _tris.Add(a + n);
                _tris.Add(b + n);
                _tris.Add(a);
                _tris.Add(b + n);
                _tris.Add(b);
            }
        }

        /// <summary>`c` at alpha `a` over `under`, as the mock-up (sRGB) blends it.</summary>
        public static Color Over(Rgb c, float a, Rgb under) => new Color(
            a * c.R + (1f - a) * under.R, a * c.G + (1f - a) * under.G, a * c.B + (1f - a) * under.B, 1f);

        public void Disc(Vector2 centre, float radius, Color color, float z = 0f)
        {
            const int n = 20;
            var c = Vertex(color);
            var first = Add(new Vector3(centre.x, centre.y, z), c);
            for (var i = 0; i < n; i++)
            {
                var a = 2f * Mathf.PI * i / n;
                Add(new Vector3(centre.x + Mathf.Sin(a) * radius, centre.y + Mathf.Cos(a) * radius, z), c);
            }
            for (var i = 0; i < n; i++)
            {
                _tris.Add(first);
                _tris.Add(first + 1 + i);
                _tris.Add(first + 1 + (i + 1) % n);
            }
        }

        /// <summary>
        /// An arc of an annulus, clockwise from `startDegrees` (0 = 12 o'clock) over `sweepDegrees`. Returns the index of its
        /// first vertex (a turning ring rewrites its vertices in place, see Arc's twin RotateArc).
        /// </summary>
        public int Arc(Vector2 centre, float inner, float outer, float startDegrees, float sweepDegrees, Color color, float z = 0f)
        {
            var segments = Mathf.Max(2, Mathf.CeilToInt(28 * sweepDegrees / 360f));
            var c = Vertex(color);
            var first = _verts.Count;
            for (var i = 0; i <= segments; i++)
            {
                var a = (startDegrees + sweepDegrees * i / segments) * Mathf.Deg2Rad;
                var d = new Vector3(Mathf.Sin(a), Mathf.Cos(a), 0f);
                Add(new Vector3(centre.x, centre.y, z) + d * inner, c);
                Add(new Vector3(centre.x, centre.y, z) + d * outer, c);
            }
            for (var i = 0; i < segments; i++)
            {
                var v = first + i * 2;
                _tris.Add(v);
                _tris.Add(v + 1);
                _tris.Add(v + 3);
                _tris.Add(v);
                _tris.Add(v + 3);
                _tris.Add(v + 2);
            }
            return first;
        }

        /// <summary>The vertices of an Arc added at `first`, turned to start at `startDegrees` (the mesh is re-uploaded by the caller).</summary>
        public void TurnArc(int first, Vector2 centre, float inner, float outer, float startDegrees, float sweepDegrees, float z = 0f)
        {
            var segments = Mathf.Max(2, Mathf.CeilToInt(28 * sweepDegrees / 360f));
            for (var i = 0; i <= segments; i++)
            {
                var a = (startDegrees + sweepDegrees * i / segments) * Mathf.Deg2Rad;
                var d = new Vector3(Mathf.Sin(a), Mathf.Cos(a), 0f);
                _verts[first + i * 2] = new Vector3(centre.x, centre.y, z) + d * inner;
                _verts[first + i * 2 + 1] = new Vector3(centre.x, centre.y, z) + d * outer;
            }
        }

        /// <summary>Uploads only the vertex positions (a turning ring).</summary>
        public void ApplyPositions() => _mesh.SetVertices(_verts);

        /// <summary>A straight stroke of `width` from a to b (a check's two strokes).</summary>
        public void Stroke(Vector2 a, Vector2 b, float width, Color color, float z = 0f)
        {
            var d = (b - a).normalized;
            var n = new Vector2(-d.y, d.x) * (width * 0.5f);
            var c = Vertex(color);
            // wound clockwise as seen from -z whichever way the stroke points
            var p0 = a - n;
            var p1 = a + n;
            var p2 = b + n;
            var p3 = b - n;
            var cross = (p1.x - p0.x) * (p2.y - p0.y) - (p1.y - p0.y) * (p2.x - p0.x);
            if (cross > 0f)
            {
                var t = p1;
                p1 = p3;
                p3 = t;
            }
            var i = Add(new Vector3(p0.x, p0.y, z), c);
            Add(new Vector3(p1.x, p1.y, z), c);
            Add(new Vector3(p2.x, p2.y, z), c);
            Add(new Vector3(p3.x, p3.y, z), c);
            _tris.Add(i);
            _tris.Add(i + 1);
            _tris.Add(i + 2);
            _tris.Add(i);
            _tris.Add(i + 2);
            _tris.Add(i + 3);
        }
    }
}
