using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Lotse.App
{
    /// <summary>
    /// Desk placement, driven once per frame by AppRoot: load the saved desk anchor (used only when it is within
    /// reach: 1.5 m from the head, inside a 70° cone ahead, see AnchorReach), or ask for a tap on the
    /// desk with the right index finger. Each frame it casts DeskSurface's eight rays straight down around the
    /// tip (never at the tip, which the finger hides), finds the desk plane away from the joints of both hands,
    /// shows the reticle there and lets DeskTouch decide on the touch and PlacementFlow on the fallback. On an accepted touch it creates the
    /// anchor on the plane under the tip, turned so that its forward points away from the user (the content
    /// faces the user), as the head-relative anchor does.
    /// Logged for the device check: each ray status the first time it occurs in a placement, and one
    /// diagnostic ray from head height once the raycast is ready.
    /// </summary>
    public sealed class DeskPlacer
    {
        public const float RayMeters = 1f;
        const float DiagnosticForward = 0.45f;
        const float DiagnosticMeters = 2f;

        readonly IDeskRaycaster _ray;
        readonly IDeskAnchors _anchors;
        readonly Transform _head;
        readonly PlacementView _view;
        readonly PlacementFlow _flow = new PlacementFlow();
        readonly DeskTouch _touch = new DeskTouch();
        readonly List<DeskHit> _hits = new List<DeskHit>(8);
        readonly List<Vec3> _joints = new List<Vec3>(64);
        readonly HashSet<DeskRayStatus> _seenStatus = new HashSet<DeskRayStatus>();
        bool _diagnosed;

        int _load;          // the load in flight; an answer to an older one is dropped
        bool _loadAnswered;
        Transform _loaded;

        /// <summary>The anchor object to follow (loaded or new), or null and why: then the caller keeps what it has.</summary>
        public event Action<Transform, PlacementFallback> Finished;

        public bool Active => _flow.Active;
        public PlacementState State => _flow.State;
        public PlacementView View => _view;

        public DeskPlacer(IDeskRaycaster ray, IDeskAnchors anchors, Transform head, PlacementView view)
        {
            _ray = ray;
            _anchors = anchors;
            _head = head;
            _view = view;
        }

        /// <summary>
        /// Starts (over): from the saved anchor if `trySaved` and there is one, otherwise with the tap. A re-place
        /// (`trySaved` false) was pressed with the finger that will tap, right above the desk, so that finger
        /// only counts once it has moved away or 0.6 s have passed.
        /// </summary>
        public void Begin(double nowMs, bool trySaved)
        {
            DropLoad();
            if (trySaved) _touch.Reset();
            else _touch.Rearm(nowMs);
            _seenStatus.Clear();
            _diagnosed = false;
            _view.Hide();
            var load = trySaved && _anchors.HasSaved;
            _flow.Begin(nowMs, load);
            if (!load)
            {
                EnterPlacing();
                return;
            }
            Debug.Log("[Lotse] placement: loading the saved desk anchor");
            var id = _load;
            _anchors.LoadSaved(t => OnLoaded(id, t));
        }

        public void Step(double nowMs, in HandFrame left, in HandFrame right)
        {
            if (_flow.State == PlacementState.LoadingSaved) StepLoading(nowMs);
            else if (_flow.State == PlacementState.Placing) StepPlacing(nowMs, left, right);
        }

        /// <summary>Stops whatever runs, without an outcome (the app goes away).</summary>
        public void Stop()
        {
            DropLoad();
            _view.Hide();
            _ray.End();
        }

        void OnLoaded(int id, Transform anchor)
        {
            if (id != _load || _flow.State != PlacementState.LoadingSaved)
            {
                if (anchor != null) Object.Destroy(anchor.gameObject); // too late: placement went on without it
                return;
            }
            _loadAnswered = true;
            _loaded = anchor;
        }

        void DropLoad()
        {
            _load++;
            _loadAnswered = false;
            if (_loaded != null) Object.Destroy(_loaded.gameObject);
            _loaded = null;
        }

        void StepLoading(double nowMs)
        {
            var outOfReach = false;
            if (_loadAnswered)
            {
                // a saved anchor the hands cannot get to (another table, behind the user) is not used
                outOfReach = _loaded != null && !AnchorReach.InReach(Ui.ToVec(_loaded.position), Ui.ToVec(_head.position), Ui.ToVec(_head.forward));
                if (outOfReach)
                    Debug.Log($"[Lotse] placement: the saved desk anchor at {_loaded.position} is out of reach " +
                              $"({Vector3.Distance(_loaded.position, _head.position):0.00} m from the head)");
                _flow.SavedLoaded(nowMs, _loaded != null && !outOfReach);
            }
            else _flow.Tick(nowMs, RaycastAvailability.Ready, false, false); // only the 3 s count here

            if (_flow.State == PlacementState.LoadedSaved)
            {
                var anchor = _loaded;
                _loaded = null;
                Debug.Log($"[Lotse] placement: saved desk anchor loaded at {anchor.position}");
                Finish(anchor, PlacementFallback.None);
            }
            else if (_flow.State == PlacementState.Placing)
            {
                if (!outOfReach) Debug.Log("[Lotse] placement: the saved desk anchor did not localise within 3 s");
                DropLoad();
                EnterPlacing();
            }
        }

        void EnterPlacing()
        {
            _ray.Begin();
            _view.ShowHint(_head);
            Debug.Log("[Lotse] placement: hint shown, waiting for a tap on the desk");
        }

        void StepPlacing(double nowMs, in HandFrame left, in HandFrame right)
        {
            _view.FollowHead(_head, Time.deltaTime);
            var availability = _ray.Availability;
            var ready = availability == RaycastAvailability.Ready;
            if (ready && !_diagnosed) Diagnose();
            Vec3? tip = ready && right.TipValid ? Ui.ToVec(right.Tip) : (Vec3?)null;
            DeskPlane? plane = null;
            if (tip != null)
            {
                var head = Ui.ToVec(_head.position);
                _hits.Clear();
                foreach (var origin in DeskSurface.RayOrigins(tip.Value, head, Ui.ToVec(right.TipDir)))
                {
                    var status = _ray.Raycast(Ui.ToVector(origin), Vector3.down, RayMeters, out var point, out var normal);
                    Note(status);
                    if (status == DeskRayStatus.Hit) _hits.Add(new DeskHit(Ui.ToVec(point), Ui.ToVec(normal)));
                }
                _joints.Clear();
                AddJoints(left);
                AddJoints(right);
                if (DeskSurface.TryPlane(tip.Value, head, _hits, _joints, out var found)) plane = found;
            }

            var accepted = _touch.Update(nowMs, tip, plane);
            if (tip == null || plane == null) _view.HideReticle();
            else
            {
                var p = plane.Value;
                _view.ShowReticle(new Vector3(right.Tip.x, p.Y, right.Tip.z), Ui.ToVector(p.Normal), DeskTouch.IsTouch(tip.Value, p), _touch.Progress);
            }

            // only a desk plane counts as a hit for the 8 s: raw hits on hands or clutter keep nothing alive
            _flow.Tick(nowMs, availability, plane != null, accepted);
            if (_flow.State == PlacementState.Placed) PlaceOnDesk();
            else if (_flow.State == PlacementState.Fallback)
            {
                Debug.Log($"[Lotse] placement: fallback ({_flow.Fallback})");
                Finish(null, _flow.Fallback);
            }
        }

        void AddJoints(in HandFrame hand)
        {
            if (hand.Joints == null) return;
            foreach (var j in hand.Joints) _joints.Add(Ui.ToVec(j));
        }

        void Note(DeskRayStatus status)
        {
            if (_seenStatus.Add(status)) Debug.Log($"[Lotse] placement: ray status {status} (first this placement)");
        }

        /// <summary>
        /// Once the raycast is ready, before any tip is seen: one ray straight down from head height, 45 cm in
        /// front of the user, logged, to see what the depth raycast gives at the desk.
        /// </summary>
        void Diagnose()
        {
            _diagnosed = true;
            var forward = Vector3.ProjectOnPlane(_head.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            var origin = _head.position + forward.normalized * DiagnosticForward;
            var status = _ray.Raycast(origin, Vector3.down, DiagnosticMeters, out var point, out var normal);
            Debug.Log($"[Lotse] placement: diagnostic ray from head height {status}, head y {origin.y:0.000}, hit y {point.y:0.000}, normal {normal}");
        }

        void PlaceOnDesk()
        {
            var hit = _touch.Accepted;
            var point = Ui.ToVector(hit.Point);
            var forward = Vector3.ProjectOnPlane(point - _head.position, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.ProjectOnPlane(_head.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            var anchor = _anchors.Create(point, Quaternion.LookRotation(forward.normalized, Vector3.up));
            Debug.Log($"[Lotse] placement: desk touched at {point} (normal {Ui.ToVector(hit.Normal)}), anchor created");
            Finish(anchor, PlacementFallback.None);
        }

        void Finish(Transform anchor, PlacementFallback reason)
        {
            _view.Hide();
            _ray.End();
            Finished?.Invoke(anchor, reason);
        }
    }
}
