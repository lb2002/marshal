using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.LowLevel;

namespace Lotse.EditorTools
{
    /// <summary>
    /// Offscreen renders of every screen of the app, from a seated user's eye, for visual checks before anyone wears
    /// the headset. `Renders.All` (batch mode WITH graphics, and without -quit: see quest/Renders/README.md) opens an
    /// empty scene, enters Play mode, runs the real app with scripted hands and fakes (RenderScenes), writes
    /// quest/Renders/*.png and quits the Editor: exit code 0 when every PNG was written and is not black.
    /// Editor-only: none of this is in the APK.
    /// </summary>
    [InitializeOnLoad]
    public static class Renders
    {
        const string ActiveKey = "Lotse.Renders.Active";
        const string ExitKey = "Lotse.Renders.ExitCode";
        const string VideoKey = "Lotse.Renders.Video";
        const double WallClockLimitSeconds = 2400;

        static IEnumerator _script;
        static readonly Stack<IEnumerator> Stack = new Stack<IEnumerator>();
        static int _lastFrame = -1;
        static double _startedAt;
        static PlayerLoopSystem _originalLoop;
        static bool _hooked;

        /// <summary>The output folder: quest/Renders.</summary>
        public static string OutDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Renders"));

        static Renders()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        /// <summary>The frame sequences for the video (U14): quest/Captures/video-seq/&lt;name&gt;/0001.png …</summary>
        public static string VideoDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Captures", "video-seq"));

        /// <summary>The CLI entry for the video's frame sequences: -executeMethod Lotse.EditorTools.Renders.Video (same rules as All).</summary>
        public static void Video() => Start(true);

        /// <summary>The CLI entry: -executeMethod Lotse.EditorTools.Renders.All</summary>
        public static void All() => Start(false);

        static void Start(bool video)
        {
            if (Environment.GetCommandLineArgs().Any(a => a.Equals("-quit", StringComparison.OrdinalIgnoreCase)))
            {
                // -quit ends the Editor as soon as this method returns, before Play mode can start
                Debug.LogError("[Renders] started with -quit: the renders need Play mode, which -quit never lets run. " +
                               "Start the Editor without -quit (see quest/Renders/README.md).");
                EditorApplication.Exit(2);
                return;
            }
            if (Application.isBatchMode && SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Debug.LogError("[Renders] started with -nographics: there is no GPU to render with.");
                EditorApplication.Exit(2);
                return;
            }
            if (video) Directory.CreateDirectory(VideoDir);
            else
            {
                Directory.CreateDirectory(OutDir);
                foreach (var old in Directory.GetFiles(OutDir, "*.png")) File.Delete(old);
            }
            SessionState.SetBool(VideoKey, video);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            PlayModeLeftovers.Snapshot();
            SessionState.SetBool(ActiveKey, true);
            SessionState.EraseInt(ExitKey);
            EditorApplication.EnterPlaymode();
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(ActiveKey, false))
            {
                SessionState.EraseBool(ActiveKey);
                var video = SessionState.GetBool(VideoKey, false);
                SessionState.EraseBool(VideoKey);
                Begin(video ? new RenderScenes(VideoDir).RunVideo() : new RenderScenes(OutDir).Run());
            }
            else if (change == PlayModeStateChange.EnteredEditMode && SessionState.GetInt(ExitKey, -1) >= 0)
            {
                var code = SessionState.GetInt(ExitKey, 1);
                SessionState.EraseInt(ExitKey);
                PlayModeLeftovers.Restore();
                if (Application.isBatchMode) EditorApplication.Exit(code);
            }
        }

        /// <summary>Steps the script once per frame from the player loop (after LateUpdate, before the frame renders).</summary>
        static void Begin(IEnumerator script)
        {
            _script = script;
            Stack.Clear();
            Stack.Push(script);
            _startedAt = EditorApplication.timeSinceStartup;
            _originalLoop = PlayerLoop.GetCurrentPlayerLoop();
            var loop = _originalLoop;
            var systems = loop.subSystemList;
            for (var i = 0; i < systems.Length; i++)
            {
                if (systems[i].type != typeof(UnityEngine.PlayerLoop.PostLateUpdate)) continue;
                var sub = new List<PlayerLoopSystem>(systems[i].subSystemList ?? Array.Empty<PlayerLoopSystem>());
                sub.Insert(0, new PlayerLoopSystem { type = typeof(Renders), updateDelegate = Tick });
                systems[i].subSystemList = sub.ToArray();
            }
            loop.subSystemList = systems;
            PlayerLoop.SetPlayerLoop(loop);
            _hooked = true;
        }

        static void Tick()
        {
            if (_script == null || Time.frameCount == _lastFrame) return;
            _lastFrame = Time.frameCount;
            if (EditorApplication.timeSinceStartup - _startedAt > WallClockLimitSeconds)
            {
                Finish(1, $"[Renders] gave up after {WallClockLimitSeconds} s");
                return;
            }
            try
            {
                while (Stack.Count > 0)
                {
                    var top = Stack.Peek();
                    if (!top.MoveNext())
                    {
                        Stack.Pop();
                        continue;
                    }
                    if (top.Current is IEnumerator nested)
                    {
                        Stack.Push(nested);
                        continue;
                    }
                    return; // null: the next frame
                }
            }
            catch (Exception e)
            {
                Finish(1, "[Renders] failed: " + e);
                return;
            }
            Finish(RenderScenes.ExitCode, "[Renders] done");
        }

        static void Finish(int code, string message)
        {
            if (code == 0) Debug.Log(message);
            else Debug.LogError(message);
            _script = null;
            Stack.Clear();
            if (_hooked) PlayerLoop.SetPlayerLoop(_originalLoop);
            _hooked = false;
            RenderScenes.CleanUp();
            SessionState.SetInt(ExitKey, code);
            EditorApplication.delayCall += EditorApplication.ExitPlaymode;
        }
    }
}
