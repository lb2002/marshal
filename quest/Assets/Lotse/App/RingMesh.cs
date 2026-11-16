using System.Collections.Generic;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// A flat annulus in the local XY plane, filled clockwise from 12 o'clock as seen from -z. The hold ring
    /// is rebuilt every frame while a hold runs, so the vertex, normal and index buffers are reused
    /// (main thread only).
    /// </summary>
    public static class RingMesh
    {
        const int FullSegments = 64;

        static readonly List<Vector3> Vertices = new List<Vector3>((FullSegments + 1) * 2);
        static readonly List<Vector3> Normals = new List<Vector3>((FullSegments + 1) * 2);
        static readonly List<int> Triangles = new List<int>(FullSegments * 6);

        public static void Build(Mesh mesh, float inner, float outer, float fraction)
        {
            mesh.Clear();
            fraction = Mathf.Clamp01(fraction);
            if (fraction <= 0f) return;
            var segments = Mathf.Max(1, Mathf.CeilToInt(FullSegments * fraction));

            Vertices.Clear();
            Normals.Clear();
            Triangles.Clear();
            for (var i = 0; i <= segments; i++)
            {
                var a = fraction * 2f * Mathf.PI * i / segments;
                var dir = new Vector3(Mathf.Sin(a), Mathf.Cos(a), 0f);
                Vertices.Add(dir * inner);
                Vertices.Add(dir * outer);
                Normals.Add(Vector3.back);
                Normals.Add(Vector3.back);
            }
            for (var i = 0; i < segments; i++)
            {
                var v = i * 2;
                Triangles.Add(v);
                Triangles.Add(v + 1);
                Triangles.Add(v + 3);
                Triangles.Add(v);
                Triangles.Add(v + 3);
                Triangles.Add(v + 2);
            }
            mesh.SetVertices(Vertices);
            mesh.SetNormals(Normals);
            mesh.SetTriangles(Triangles, 0);
            mesh.RecalculateBounds();
        }

        /// <summary>A dashed annulus: `dashes` equal dashes with gaps of the same length, starting at 12 o'clock.</summary>
        public static void BuildDashed(Mesh mesh, float inner, float outer, int dashes)
        {
            mesh.Clear();
            Vertices.Clear();
            Normals.Clear();
            Triangles.Clear();
            const int perDash = 4;
            var step = 2f * Mathf.PI / dashes;
            for (var d = 0; d < dashes; d++)
            {
                var start = d * step;
                var first = Vertices.Count;
                for (var i = 0; i <= perDash; i++)
                {
                    var a = start + step * 0.5f * i / perDash;
                    var dir = new Vector3(Mathf.Sin(a), Mathf.Cos(a), 0f);
                    Vertices.Add(dir * inner);
                    Vertices.Add(dir * outer);
                    Normals.Add(Vector3.back);
                    Normals.Add(Vector3.back);
                }
                for (var i = 0; i < perDash; i++)
                {
                    var v = first + i * 2;
                    Triangles.Add(v);
                    Triangles.Add(v + 1);
                    Triangles.Add(v + 3);
                    Triangles.Add(v);
                    Triangles.Add(v + 3);
                    Triangles.Add(v + 2);
                }
            }
            mesh.SetVertices(Vertices);
            mesh.SetNormals(Normals);
            mesh.SetTriangles(Triangles, 0);
            mesh.RecalculateBounds();
        }
    }
}
