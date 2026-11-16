using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// IDeskAnchors over OVRSpatialAnchor (Meta Core SDK). What is kept about the anchor (its uuid, nothing else)
    /// is decided by DeskAnchorRecord; the anchor itself holds only its pose. A new anchor replaces the old one
    /// in storage: the old uuid is erased from the device once the new one is saved, and forgotten and erased
    /// when the new one cannot be saved. When re-places overlap, only the newest save attempt changes the key. The anchor objects belong to the caller, which destroys the ones it no longer uses.
    /// </summary>
    public sealed class SpatialDeskAnchors : IDeskAnchors
    {
        readonly string _objectName = "LotseDeskAnchor";
        /// <summary>Loading: the caller stops waiting after 3 s, this is only a safety net.</summary>
        const double LocalizeTimeoutSeconds = 10;
        /// <summary>A new anchor that is not localised by then is not saved.</summary>
        const float CreateTimeoutSeconds = 10f;

        readonly DeskAnchorRecord _record;

        public SpatialDeskAnchors(IKeyValueStore store) => _record = new DeskAnchorRecord(store);

        /// <summary>The same over another key and object name (U5b: the display's anchor at its centre).</summary>
        public SpatialDeskAnchors(IKeyValueStore store, string key, string objectName)
        {
            _record = new DeskAnchorRecord(store, key);
            _objectName = objectName;
        }

        public bool HasSaved => _record.HasSaved;

        public async void LoadSaved(Action<Transform> done)
        {
            if (!_record.TryGetSaved(out var uuid))
            {
                done(null);
                return;
            }
            try
            {
                var unbound = new List<OVRSpatialAnchor.UnboundAnchor>();
                var result = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(new[] { uuid }, unbound);
                if (!result.Success || unbound.Count == 0)
                {
                    Debug.Log($"[Lotse] saved desk anchor {uuid} not found ({result.Status})");
                    if (result.Success) _record.NotFound(); // gone from the device: do not look for it again
                    done(null);
                    return;
                }
                var anchor = unbound[0];
                if (!anchor.Localized && !await anchor.LocalizeAsync(LocalizeTimeoutSeconds))
                {
                    Debug.Log($"[Lotse] saved desk anchor {uuid} did not localise");
                    done(null);
                    return;
                }
                if (!anchor.TryGetPose(out var pose))
                {
                    Debug.Log($"[Lotse] saved desk anchor {uuid} has no pose yet: treated as not localised");
                    done(null);
                    return;
                }
                var go = new GameObject(_objectName);
                go.transform.SetPositionAndRotation(pose.position, pose.rotation);
                anchor.BindTo(go.AddComponent<OVRSpatialAnchor>()); // same frame as AddComponent, before its Start
                done(go.transform);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Lotse] loading the saved desk anchor failed: " + e.Message);
                done(null);
            }
        }

        public Transform Create(Vector3 position, Quaternion rotation)
        {
            var go = new GameObject(_objectName);
            go.transform.SetPositionAndRotation(position, rotation);
            SaveWhenLocalized(go.AddComponent<OVRSpatialAnchor>());
            return go.transform;
        }

        async void SaveWhenLocalized(OVRSpatialAnchor anchor)
        {
            // taken now: a later re-place starts a newer attempt, and only the newest may change what is stored
            var attempt = _record.BeginSave();
            try
            {
                var localized = anchor.WhenLocalizedAsync();
                var deadline = Time.realtimeSinceStartup + CreateTimeoutSeconds;
                while (!localized.IsCompleted && Time.realtimeSinceStartup < deadline) await Task.Yield();
                if (!localized.IsCompleted || !localized.GetResult() || anchor == null)
                {
                    // the content stays at the pose, just not world-locked; the stored anchor marks the old spot
                    Debug.LogWarning("[Lotse] desk anchor not created or not localised within 10 s: placed for this run only");
                    Forget(attempt);
                    return;
                }
                var saved = await anchor.SaveAnchorAsync();
                if (!saved.Success)
                {
                    Debug.LogWarning($"[Lotse] desk anchor not saved ({saved.Status}): placed for this run only");
                    Forget(attempt);
                    return;
                }
                var uuid = anchor.Uuid;
                var erase = _record.Saved(attempt, uuid);
                Debug.Log(erase == uuid
                    ? $"[Lotse] desk anchor {uuid} saved, but a newer one was placed meanwhile: erasing it"
                    : $"[Lotse] desk anchor saved: {uuid}");
                if (erase.HasValue) Erase(erase.Value);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Lotse] saving the desk anchor failed: " + e.Message);
                Forget(attempt);
            }
        }

        /// <summary>A failed save: the stored anchor (if this attempt still owns the key) is forgotten and erased.</summary>
        void Forget(DeskAnchorRecord.SaveAttempt attempt)
        {
            var orphan = _record.SaveFailed(attempt);
            if (!orphan.HasValue) return;
            Debug.Log($"[Lotse] saved desk anchor {orphan.Value} forgotten");
            Erase(orphan.Value);
        }

        static async void Erase(Guid uuid)
        {
            try
            {
                var erased = await OVRSpatialAnchor.EraseAnchorsAsync(null, new[] { uuid });
                Debug.Log($"[Lotse] desk anchor {uuid} erased from the device: {erased.Success}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Lotse] erasing desk anchor {uuid} failed: {e.Message}");
            }
        }
    }
}
