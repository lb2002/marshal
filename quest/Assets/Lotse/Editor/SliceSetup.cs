using System.IO;
using Lotse.App;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Lotse.EditorTools
{
    // Adds the demo slice to Main.unity and enforces what it relies on. Idempotent. Run headless:
    //   unity run <quest> --timeout 900 --log-file <log> --no-tail -- -executeMethod Lotse.EditorTools.SliceSetup.Run
    // - a "LotseApp" object with AppRoot; Task 1's "Lotse" text hidden
    // - both hands on the OpenXR hand skeleton and mesh (XRHandLeft / XRHandRight): OvrHandSource reads
    //   XRHand_* joint ids, which the legacy OVR skeleton does not have
    // - hand tracking frequency HIGH (Fast Motion Mode) for pinch-hold and the two-hand hold, in the
    //   project config and in our own Android manifest
    public static class SliceSetup
    {
        const string ScenePath = "Assets/Lotse/Scenes/Main.unity";
        const string ManifestPath = "Assets/Plugins/Android/AndroidManifest.xml";

        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var app = GameObject.Find("LotseApp");
            if (app == null) app = new GameObject("LotseApp");
            if (app.GetComponent<AppRoot>() == null) app.AddComponent<AppRoot>();

            GameObject text = null;
            foreach (var go in scene.GetRootGameObjects())
                if (go.name == "LotseText") text = go;
            if (text != null) text.SetActive(false);

            var hands = 0;
            foreach (var hand in Object.FindObjectsByType<OVRHand>(FindObjectsInactive.Include))
            {
                var isLeft = hand.GetHand() == OVRPlugin.Hand.HandLeft;
                if (!isLeft && hand.GetHand() != OVRPlugin.Hand.HandRight)
                    throw new System.InvalidOperationException($"{hand.name}: OVRHand without a hand type");
                var skeleton = hand.GetComponent<OVRSkeleton>() ?? throw new System.InvalidOperationException($"{hand.name}: no OVRSkeleton");
                var mesh = hand.GetComponent<OVRMesh>() ?? throw new System.InvalidOperationException($"{hand.name}: no OVRMesh");
                // an explicit override, even where it equals the value OVRHand.OnValidate puts into the prefab in
                // memory: the prefab file itself says 0 (legacy HandLeft), and the build must not depend on OnValidate
                ForceOverride(skeleton, "_skeletonType",
                    (int)(isLeft ? OVRSkeleton.SkeletonType.XRHandLeft : OVRSkeleton.SkeletonType.XRHandRight));
                ForceOverride(mesh, "_meshType", (int)(isLeft ? OVRMesh.MeshType.XRHandLeft : OVRMesh.MeshType.XRHandRight));
                hands++;
                Debug.Log($"[Lotse.SliceSetup] {hand.name}: {hand.GetHand()} skeleton={skeleton.GetSkeletonType()} " +
                          $"mesh={new SerializedObject(mesh).FindProperty("_meshType").intValue}");
            }
            if (hands != 2) throw new System.InvalidOperationException($"expected 2 OVRHands in {ScenePath}, found {hands}");

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new System.InvalidOperationException("could not save " + ScenePath);

            var config = OVRProjectConfig.CachedProjectConfig;
            config.handTrackingFrequency = OVRProjectConfig.HandTrackingFrequency.HIGH;
            OVRProjectConfig.CommitProjectConfig(config);
            var manifest = File.ReadAllText(ManifestPath);
            var patched = manifest.Replace("android:name=\"com.oculus.handtracking.frequency\" android:value=\"LOW\"",
                "android:name=\"com.oculus.handtracking.frequency\" android:value=\"HIGH\"");
            if (!patched.Contains("android:name=\"com.oculus.handtracking.frequency\" android:value=\"HIGH\""))
                throw new System.InvalidOperationException("manifest has no hand tracking frequency entry");
            if (patched != manifest)
            {
                File.WriteAllText(ManifestPath, patched);
                AssetDatabase.ImportAsset(ManifestPath);
            }

            Debug.Log($"[Lotse.SliceSetup] LotseApp with AppRoot in {ScenePath}; LotseText hidden: {text != null && !text.activeSelf}; " +
                      $"hands on OpenXR skeleton: {hands}; hand tracking frequency: {config.handTrackingFrequency}");
        }

        /// <summary>Sets the field on the scene instance and records it as a prefab instance modification.</summary>
        static void ForceOverride(Component c, string field, int value)
        {
            var so = new SerializedObject(c);
            var prop = so.FindProperty(field) ?? throw new System.MissingFieldException(c.GetType().Name, field);
            prop.intValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();

            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(c);
            var source = PrefabUtility.GetCorrespondingObjectFromSource(c);
            if (root == null || source == null) return; // not a prefab instance: the field itself is serialized
            var mods = new System.Collections.Generic.List<PropertyModification>();
            foreach (var m in PrefabUtility.GetPropertyModifications(root) ?? new PropertyModification[0])
                if (!(m.target == source && m.propertyPath == field)) mods.Add(m);
            mods.Add(new PropertyModification { target = source, propertyPath = field, value = value.ToString() });
            PrefabUtility.SetPropertyModifications(root, mods.ToArray());
        }
    }
}
