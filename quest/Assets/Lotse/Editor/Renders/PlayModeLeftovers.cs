using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lotse.EditorTools
{
    /// <summary>
    /// Play mode in this project writes files the renders must not leave behind: TextMesh Pro adds the glyphs it
    /// draws to the dynamic fallback font asset, and the Meta XR SDK writes RuntimeActionBindings.json into the
    /// project and StreamingAssets. Snapshot before Play mode, Restore after it: changed files get their old bytes
    /// back (and are reloaded, so nothing dirty is saved on quit), new files are deleted. The snapshot sits in
    /// the project's Temp folder, so it survives a domain reload.
    /// </summary>
    static class PlayModeLeftovers
    {
        const string Dir = "Temp/LotseRenders";
        const string Manifest = Dir + "/manifest.txt";
        static readonly string[] Bindings =
        {
            "RuntimeActionBindings.json",
            "Assets/StreamingAssets/RuntimeActionBindings.json",
            "Assets/StreamingAssets/RuntimeActionBindings.json.meta",
        };

        static IEnumerable<string> Watched() =>
            AssetDatabase.FindAssets("t:TMP_FontAsset").Select(AssetDatabase.GUIDToAssetPath).Concat(Bindings).Distinct();

        public static void Snapshot()
        {
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
            Directory.CreateDirectory(Dir);
            var lines = new List<string>();
            var i = 0;
            foreach (var path in Watched())
            {
                var existed = File.Exists(path);
                if (existed) File.Copy(path, $"{Dir}/{i}", true);
                lines.Add($"{i}\t{(existed ? 1 : 0)}\t{path}");
                i++;
            }
            File.WriteAllLines(Manifest, lines);
        }

        public static void Restore()
        {
            if (!File.Exists(Manifest)) return;
            foreach (var line in File.ReadAllLines(Manifest))
            {
                var parts = line.Split('\t');
                var copy = $"{Dir}/{parts[0]}";
                var existed = parts[1] == "1";
                var path = parts[2];
                if (existed)
                {
                    // the changes may still be only in memory: drop them, or the Editor saves them on quit
                    if (path.StartsWith("Assets/"))
                        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                            if (o != null) EditorUtility.ClearDirty(o);
                    var changed = !File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(copy));
                    if (changed) File.Copy(copy, path, true);
                    if (path.StartsWith("Assets/")) AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                    if (changed) Debug.Log("[Renders] restored " + path);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                    Debug.Log("[Renders] removed " + path);
                }
            }
            // a scanner may still hold the folder for a moment: leaving it behind must not keep the Editor from quitting
            try { Directory.Delete(Dir, true); }
            catch (IOException e) { Debug.LogWarning("[Renders] could not remove " + Dir + ": " + e.Message); }
            catch (System.UnauthorizedAccessException e) { Debug.LogWarning("[Renders] could not remove " + Dir + ": " + e.Message); }
        }
    }
}
