using System.IO;
using UnityEditor.Android;
using UnityEngine;

namespace Lotse
{
    /// <summary>
    /// The APK is ARM64 only (U13: Meta's upload check warned about 32-bit libraries). The player's own libraries already
    /// are (target architecture ARM64, abiFilters "arm64-v8a"); but the Meta XR core package ships a few logging
    /// libraries as plain assets under assets/lib/armeabi-v7a, which no ABI filter touches. They are left out here: the
    /// generated Gradle projects' asset-ignore pattern gets one more entry for directories named armeabi-v7a.
    /// </summary>
    public sealed class Arm64Only : IPostGenerateGradleAndroidProject
    {
        const string Entry = ":!<dir>armeabi-v7a";

        public int callbackOrder => 1000;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            // `path` is the unityLibrary module; the launcher module is its sibling
            foreach (var module in new[] { path, Path.Combine(Path.GetDirectoryName(path) ?? path, "launcher") })
            {
                var file = Path.Combine(module, "build.gradle");
                if (!File.Exists(file)) continue;
                var text = File.ReadAllText(file);
                if (text.Contains(Entry)) continue;
                const string key = "ignoreAssetsPattern = \"";
                var at = text.IndexOf(key, System.StringComparison.Ordinal);
                if (at < 0)
                {
                    Debug.LogWarning("[Lotse.Build] no ignoreAssetsPattern in " + file + ": the 32-bit asset libraries stay in the APK");
                    continue;
                }
                var end = text.IndexOf('"', at + key.Length);
                text = text.Insert(end, Entry);
                File.WriteAllText(file, text);
                Debug.Log("[Lotse.Build] ARM64 only: assets under armeabi-v7a are left out (" + Path.GetFileName(module) + ")");
            }
        }
    }
}
