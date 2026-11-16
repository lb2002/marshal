using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using TMPro;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Management;

namespace Lotse.EditorTools
{
    // Reproducible project setup for the Quest app. Run in a resident Editor with
    //   unity command script eval "Lotse.EditorTools.ProjectSetup.Run();"
    // or headless with -executeMethod Lotse.EditorTools.ProjectSetup.Run.
    // Each step is idempotent.
    //
    // Order: ProjectSetup first, then SliceSetup (Lotse.EditorTools.SliceSetup.Run), which adds LotseApp to
    // Main.unity and sets the hand skeleton overrides. ProjectSetup.Run therefore leaves an existing Main.unity
    // alone: rebuilding it would drop SliceSetup's work. To start the scene from scratch, run
    // ProjectSetup.RunForced and then SliceSetup.Run again.
    public static class ProjectSetup
    {
        public const string PackageId = "com.treemontstudio.lotse";
        public const string ScenePath = "Assets/Lotse/Scenes/Main.unity";
        const string NscPath = "Assets/Lotse/Android/network_sec_config.xml";
        const string ManifestPath = "Assets/Plugins/Android/AndroidManifest.xml";
        const string MetaXRFeatureSetId = "com.meta.openxr.featureset.metaxr";
        const string AndroidNs = "http://schemas.android.com/apk/res/android";

        /// <summary>Everything; an existing Main.unity is kept.</summary>
        public static void Run() => Run(forceScene: false);

        /// <summary>Everything, Main.unity rebuilt from scratch: run SliceSetup.Run afterwards.</summary>
        public static void RunForced() => Run(forceScene: true);

        static void Run(bool forceScene)
        {
            Log("start");
            SwitchToAndroid();
            ImportTmpEssentials();
            ConfigureXR();
            ConfigureProjectConfig();
            ConfigurePlayer();
            FixProjectSetupIssues();
            FixOpenXRValidation();
            ConfigurePlayer(); // our values win over anything the fixes changed
            if (forceScene || !File.Exists(ScenePath)) BuildScene();
            else Log($"{ScenePath} exists: not rebuilt (SliceSetup's LotseApp and hand overrides stay). RunForced rebuilds it; run SliceSetup.Run after that.");
            WriteNetworkSecurityConfig();
            WriteManifest();
            AssetDatabase.SaveAssets();
            Report();
            Log("done");
        }

        // ---------- steps ----------

        public static void SwitchToAndroid()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            Log($"build target {EditorUserBuildSettings.activeBuildTarget}");
        }

        public static void ImportTmpEssentials()
        {
            if (File.Exists("Assets/TextMesh Pro/Resources/TMP Settings.asset"))
            {
                Log("TMP essentials present");
                return;
            }
            TMP_PackageResourceImporter.ImportResources(true, false, false);
            AssetDatabase.Refresh();
            Log($"TMP essentials imported: {File.Exists("Assets/TextMesh Pro/Resources/TMP Settings.asset")}");
        }

        public static void ConfigureXR()
        {
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.settingsKey, out XRGeneralSettingsPerBuildTarget perTarget) || perTarget == null)
            {
                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                Directory.CreateDirectory("Assets/XR");
                AssetDatabase.CreateAsset(perTarget, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.settingsKey, perTarget, true);
            }

            foreach (var group in new[] { BuildTargetGroup.Android, BuildTargetGroup.Standalone })
            {
                if (!perTarget.HasSettingsForBuildTarget(group))
                    perTarget.CreateDefaultSettingsForBuildTarget(group);
                if (!perTarget.HasManagerSettingsForBuildTarget(group))
                    perTarget.CreateDefaultManagerSettingsForBuildTarget(group);

                var general = perTarget.SettingsForBuildTarget(group);
                general.InitManagerOnStart = true;
                var manager = perTarget.ManagerSettingsForBuildTarget(group);
                bool assigned = XRPackageMetadataStore.AssignLoader(manager, "UnityEngine.XR.OpenXR.OpenXRLoader", group);
                EditorUtility.SetDirty(general);
                EditorUtility.SetDirty(manager);

                var featureSet = OpenXRFeatureSetManager.GetFeatureSetWithId(group, MetaXRFeatureSetId);
                if (featureSet != null)
                {
                    featureSet.isEnabled = true;
                    OpenXRFeatureSetManager.SetFeaturesFromEnabledFeatureSets(group);
                }
                Log($"XR {group}: OpenXR loader assigned={assigned}, loaders=[{string.Join(", ", manager.activeLoaders.Select(l => l.GetType().Name))}], Meta XR feature set={(featureSet != null ? "enabled" : "NOT FOUND")}");
            }
            EditorUtility.SetDirty(perTarget);
        }

        public static void ConfigureProjectConfig()
        {
            var config = OVRProjectConfig.CachedProjectConfig;
            config.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.HandsOnly;
            config.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Required;
            config.securityXmlPath = NscPath;
            config.enableNSCConfig = true;
            // the Quest system keyboard (with dictation) for replies: TouchScreenKeyboard shows it once the manifest
            // declares oculus.software.overlay_keyboard (OVRManifestPreprocessor writes it from this flag)
            config.requiresSystemKeyboard = true;
            OVRProjectConfig.CommitProjectConfig(config);
            Log($"OVRProjectConfig: hands={config.handTrackingSupport}, passthrough={config.insightPassthroughSupport}, nsc={config.securityXmlPath}, keyboard={config.requiresSystemKeyboard}");
        }

        public static void ConfigurePlayer()
        {
            PlayerSettings.productName = "Marshal"; // the app label in the headset (U5b); package id and code names stay Lotse
            PlayerSettings.companyName = "Treemont Studio";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, PackageId);
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)34;
            PlayerSettings.Android.forceInternetPermission = true;
            PlayerSettings.Android.preferredInstallLocation = AndroidPreferredInstallLocation.Auto;
            Log($"Player: {PlayerSettings.productName} {PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android)} " +
                $"backend={PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android)} arch={PlayerSettings.Android.targetArchitectures} " +
                $"min={PlayerSettings.Android.minSdkVersion} target={PlayerSettings.Android.targetSdkVersion} internet={PlayerSettings.Android.forceInternetPermission} " +
                $"gfx=[{string.Join(",", PlayerSettings.GetGraphicsAPIs(BuildTarget.Android))}] colorSpace={PlayerSettings.colorSpace}");
        }

        // Meta Project Setup Tool "Fix All" for Required and Recommended items on Android.
        public static void FixProjectSetupIssues()
        {
            for (int pass = 1; pass <= 3; pass++)
            {
                var open = OpenSetupTasks(BuildTargetGroup.Android)
                    .Where(t => t.fixable && (t.level == "Required" || t.level == "Recommended"))
                    .Where(t => !t.message.Contains("Simulator")) // would download and install a package
                    .ToList();
                Log($"setup tool pass {pass}: {open.Count} fixable Required/Recommended open");
                if (open.Count == 0)
                    break;
                foreach (var t in open)
                {
                    bool ok;
                    try { ok = (bool)t.task.GetType().GetMethod("Fix").Invoke(t.task, new object[] { BuildTargetGroup.Android }); }
                    catch (Exception e) { ok = false; Log($"  fix threw: {e.InnerException?.Message ?? e.Message}"); }
                    Log($"  fix [{t.level}] {t.message} -> {ok}");
                }
                AssetDatabase.SaveAssets();
            }
        }

        // OpenXR's own build validation (it fails the build on errors), e.g. the Meta hand data
        // source feature needs the Hand Tracking Subsystem feature.
        public static void FixOpenXRValidation()
        {
            var issues = new List<UnityEngine.XR.OpenXR.Features.OpenXRFeature.ValidationRule>();
            for (int pass = 1; pass <= 3; pass++)
            {
                UnityEditor.XR.OpenXR.OpenXRProjectValidation.GetCurrentValidationIssues(issues, BuildTargetGroup.Android);
                var fixable = issues.Where(i => i.error && i.fixIt != null && i.fixItAutomatic).ToList();
                Log($"OpenXR validation pass {pass}: {issues.Count} issues, {fixable.Count} auto-fixable errors");
                if (fixable.Count == 0)
                    break;
                foreach (var i in fixable)
                {
                    i.fixIt();
                    Log($"  fixed: {i.message}");
                }
            }
            foreach (var i in issues)
                Log($"  OpenXR {(i.error ? "error" : "warning")}: {i.message}");
        }

        public static void BuildScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Camera rig
            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(FindPackageAsset("OVRCameraRig.prefab", "com.meta.xr.sdk.core/Prefabs"));
            var rigGo = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab, scene);
            rigGo.name = "OVRCameraRig";
            var rig = rigGo.GetComponent<OVRCameraRig>();
            var manager = rigGo.GetComponent<OVRManager>() ?? rigGo.AddComponent<OVRManager>();
            manager.isInsightPassthroughEnabled = true;
            manager.trackingOriginType = OVRManager.TrackingOrigin.FloorLevel;
            rig.EnsureGameObjectIntegrity();

            var cam = rig.centerEyeAnchor.GetComponent<Camera>();
            cam.tag = "MainCamera";
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
            cam.nearClipPlane = 0.05f;
            cam.allowHDR = false;

            // Passthrough underlay
            var ptGo = new GameObject("Passthrough");
            SceneManager.MoveGameObjectToScene(ptGo, scene);
            // SDK 209 always composites this layer as an underlay (no overlayType field any more).
            var pt = ptGo.AddComponent<OVRPassthroughLayer>();

            // Hands (same configuration as the Hand Tracking building block)
            var handPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(FindPackageAsset("OVRHandPrefab.prefab", "com.meta.xr.sdk.core/Prefabs"));
            var skeletonVersion = OVRRuntimeSettings.Instance.HandSkeletonVersion;
            foreach (var (hand, anchor, name) in new[]
                     {
                         (OVRHand.Hand.HandLeft, rig.leftHandAnchor, "LeftHand"),
                         (OVRHand.Hand.HandRight, rig.rightHandAnchor, "RightHand"),
                     })
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(handPrefab, scene);
                go.name = name;
                go.transform.SetParent(anchor, false);
                // These members are internal to the SDK, so set the serialized fields.
                SetEnum(go.GetComponent<OVRHand>(), "HandType", (int)hand);
                SetEnum(go.GetComponent<OVRSkeleton>(), "_skeletonType", (int)hand.AsSkeletonType(skeletonVersion));
                SetEnum(go.GetComponent<OVRMesh>(), "_meshType", (int)hand.AsMeshType(skeletonVersion));
            }

            // Floating text
            var textGo = new GameObject("LotseText");
            SceneManager.MoveGameObjectToScene(textGo, scene);
            textGo.transform.position = new Vector3(0f, 1.2f, 0.8f);
            var tmp = textGo.AddComponent<TextMeshPro>();
            tmp.text = "Lotse";
            tmp.fontSize = 1.2f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.rectTransform.sizeDelta = new Vector2(1.0f, 0.3f);
            textGo.AddComponent<Lotse.PlaceInFront>();

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Log($"scene saved: {ScenePath}, font={(tmp.font ? tmp.font.name : "null")}");
        }

        public static void WriteNetworkSecurityConfig()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NscPath));
            File.WriteAllText(NscPath,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<!-- Replaces the Meta SDK default (cleartext off): the app talks ws:// to the Lotse bridge on the LAN. -->\n" +
                "<network-security-config>\n" +
                "    <base-config cleartextTrafficPermitted=\"true\"></base-config>\n" +
                "</network-security-config>\n");
            AssetDatabase.ImportAsset(NscPath);
            Log($"wrote {NscPath}");
        }

        public static void WriteManifest()
        {
            OVRManifestPreprocessor.GenerateOrUpdateAndroidManifest(true);
            if (!File.Exists(ManifestPath))
                throw new FileNotFoundException("Meta manifest generation did not create " + ManifestPath);

            var doc = new XmlDocument();
            doc.Load(ManifestPath);
            var nsm = new XmlNamespaceManager(doc.NameTable);
            nsm.AddNamespace("android", AndroidNs);
            var manifest = doc.DocumentElement;
            var app = (XmlElement)manifest.SelectSingleNode("application");
            app.SetAttribute("usesCleartextTraffic", AndroidNs, "true");
            app.SetAttribute("allowBackup", AndroidNs, "false"); // no session token in a device backup

            // Unity must not ask for every dangerous manifest permission at start-up: the spatial data
            // permission is asked for by desk placement itself, when it is needed (MrukDeskRaycaster)
            const string skipDialog = "unityplayer.SkipPermissionsDialog";
            var skip = (XmlElement)app.SelectSingleNode($"meta-data[@android:name='{skipDialog}']", nsm);
            if (skip == null)
            {
                skip = doc.CreateElement("meta-data");
                skip.SetAttribute("name", AndroidNs, skipDialog);
                app.AppendChild(skip);
            }
            skip.SetAttribute("value", AndroidNs, "true");

            const string boundaryless = "com.oculus.feature.BOUNDARYLESS_APP";
            if (manifest.SelectSingleNode($"uses-feature[@android:name='{boundaryless}']", nsm) == null)
            {
                var feature = doc.CreateElement("uses-feature");
                feature.SetAttribute("name", AndroidNs, boundaryless);
                feature.SetAttribute("required", AndroidNs, "true");
                manifest.InsertBefore(feature, app);
            }

            var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
            using (var w = XmlWriter.Create(ManifestPath, settings))
                doc.Save(w);
            AssetDatabase.ImportAsset(ManifestPath);
            Log($"manifest written: {ManifestPath}");
        }

        // ---------- reporting ----------

        public static void Report()
        {
            var tasks = OpenSetupTasks(BuildTargetGroup.Android).ToList();
            Log($"setup tool, Android, still open: {tasks.Count}");
            foreach (var t in tasks.OrderByDescending(t => t.level))
                Log($"  open [{t.level}]{(t.fixable ? "" : " (manual)")} {t.message}");
        }

        struct OpenTask
        {
            public object task;
            public string level;
            public string message;
            public bool fixable;
        }

        // OVRProjectSetup's registry is internal, so read it through reflection.
        static IEnumerable<OpenTask> OpenSetupTasks(BuildTargetGroup group)
        {
            var setupType = typeof(OVRProjectSetup);
            var getTasks = setupType.GetMethod("GetTasks", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var tasks = (IEnumerable)getTasks.Invoke(null, new object[] { group });
            foreach (var task in tasks)
            {
                var type = task.GetType();
                bool valid = Eval<bool>(type.GetProperty("Valid").GetValue(task), group);
                if (!valid)
                    continue;
                var isDone = (Func<BuildTargetGroup, bool>)type.GetProperty("IsDone").GetValue(task);
                bool ignored = (bool)type.GetMethod("IsIgnored").Invoke(task, new object[] { group });
                if (ignored || isDone(group))
                    continue;
                var level = Eval<OVRProjectSetup.TaskLevel>(type.GetProperty("Level").GetValue(task), group);
                if (level == OVRProjectSetup.TaskLevel.Hidden)
                    continue;
                yield return new OpenTask
                {
                    task = task,
                    level = level.ToString(),
                    message = Eval<string>(type.GetProperty("Message").GetValue(task), group),
                    fixable = type.GetProperty("FixAction").GetValue(task) != null,
                };
            }
        }

        // OptionalLambdaType<BuildTargetGroup, T>.GetValue(group)
        static T Eval<T>(object optionalLambda, BuildTargetGroup group)
        {
            var m = optionalLambda.GetType().GetMethod("GetValue", new[] { typeof(BuildTargetGroup) });
            return (T)m.Invoke(optionalLambda, new object[] { group });
        }

        static void SetEnum(UnityEngine.Object target, string field, int value)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(field) ?? throw new MissingFieldException(target.GetType().Name, field);
            prop.intValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static string FindPackageAsset(string fileName, string folder)
        {
            string path = $"Packages/{folder}/{fileName}";
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) == null)
                throw new FileNotFoundException(path);
            return path;
        }

        static void Log(string msg) =>
            Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "[Lotse.Setup] {0}", msg);
    }
}
