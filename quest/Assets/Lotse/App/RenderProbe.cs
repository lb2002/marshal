using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// The render probe (U4a's measure, kept for U5a's budget): what a piece of the scene costs to draw, counted from the
    /// renderers that draw now. `Renderers`: active, enabled renderers with geometry (an empty text draws nothing).
    /// `Passes`: the same, each counted once per shader pass it runs (a shape with its shadow pass costs two), which is
    /// what the GPU sees as draw calls without batching.
    /// </summary>
    public static class RenderProbe
    {
        static readonly List<Renderer> Scratch = new List<Renderer>();

        public static int Renderers(GameObject root) => Count(root, false);

        public static int Passes(GameObject root) => Count(root, true);

        static int Count(GameObject root, bool passes)
        {
            if (root == null || !root.activeInHierarchy) return 0;
            Scratch.Clear();
            root.GetComponentsInChildren(false, Scratch);
            var n = 0;
            foreach (var r in Scratch)
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy || !Draws(r)) continue;
                if (!passes)
                {
                    n++;
                    continue;
                }
                var m = r.sharedMaterial;
                if (m == null || m.shader == null)
                {
                    n++;
                    continue;
                }
                var count = 0;
                for (var p = 0; p < m.passCount; p++)
                {
                    var name = m.GetPassName(p);
                    if (name == "ShadowCaster" || name == "DepthOnly" || name == "DepthNormals" || name == "Meta") continue;
                    // the shapes' shadow pass (LightMode SRPDefaultUnlit) can be switched off per material
                    if (name == "Shadow" && !m.GetShaderPassEnabled("SRPDefaultUnlit")) continue;
                    count++;
                }
                n += Mathf.Max(1, count);
            }
            Scratch.Clear();
            return n;
        }

        /// <summary>The renderers that draw, by name and parent, for the log.</summary>
        public static string Describe(GameObject root)
        {
            var list = new List<string>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(false))
                if (r.enabled && r.gameObject.activeInHierarchy && Draws(r)) list.Add(r.transform.parent.name + "/" + r.name);
            return string.Join(", ", list);
        }

        static bool Draws(Renderer r)
        {
            // a text object (or its sub-mesh for a fallback font) draws when it has visible characters
            var tmp = r.GetComponent<TMP_Text>() ?? r.GetComponentInParent<TMP_Text>();
            if (tmp != null && r.GetComponent<TMP_SubMesh>() == null) return !string.IsNullOrWhiteSpace(tmp.text);
            var f = r.GetComponent<MeshFilter>();
            if (f != null) return f.sharedMesh != null && f.sharedMesh.vertexCount > 0 && f.sharedMesh.triangles.Length > 0;
            return true;
        }
    }
}
