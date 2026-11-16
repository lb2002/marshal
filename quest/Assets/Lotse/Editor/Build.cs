using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Lotse
{
    // Command-line build entry point:
    //   unity build <project> --target Android --execute-method Lotse.Build.Android
    // Writes quest/Builds/lotse.apk unless -buildOutput <path> is passed.
    public static class Build
    {
        public static void Android()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string output = ArgValue("-buildOutput") ?? Path.Combine(projectRoot, "Builds", "lotse.apk");
            Directory.CreateDirectory(Path.GetDirectoryName(output));

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            EditorUserBuildSettings.buildAppBundle = false;

            string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0)
                throw new InvalidOperationException("No enabled scenes in Build Settings.");

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = output,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary s = report.summary;
            Debug.Log($"[Lotse.Build] result={s.result} size={s.totalSize} bytes time={s.totalTime} " +
                      $"errors={s.totalErrors} warnings={s.totalWarnings} output={output}");

            if (s.result != BuildResult.Succeeded && Application.isBatchMode)
                EditorApplication.Exit(1);
        }

        static string ArgValue(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
