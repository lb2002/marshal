#nullable enable
using System.Collections.Generic;

namespace Lotse.App
{
    /// <summary>
    /// One fingertip's dwell (U5b): the tip stays within 1 cm of where it came to rest for 0.8 s, and that is a tap at
    /// that point. After a tap the tip must move 2 cm away before it can tap again (holding still never taps twice).
    /// Progress fills the ring at the fingertip. Pure, time injected.
    /// </summary>
    public sealed class TapDwell
    {
        public const float StillMeters = 0.01f;
        public const double DwellMs = 800;
        public const float RearmMeters = 0.02f;

        bool _resting;
        Vec3 _start;
        double _since;
        Vec3? _tapped;

        /// <summary>0..1 while the tip rests (the ring), else 0.</summary>
        public float Progress { get; private set; }
        /// <summary>Where the tip rests now (the ring's place), or null.</summary>
        public Vec3? RestingAt => _resting ? _start : (Vec3?)null;

        /// <summary>One frame; `tip` null: no fingertip (untracked, or near a button). Returns the tapped point, or null.</summary>
        public Vec3? Step(double nowMs, Vec3? tip)
        {
            if (tip == null)
            {
                _resting = false;
                Progress = 0f;
                return null;
            }
            var t = tip.Value;
            if (_tapped != null)
            {
                if (Vec3.Distance(t, _tapped.Value) < RearmMeters)
                {
                    Progress = 0f;
                    return null;
                }
                _tapped = null;
            }
            if (!_resting || Vec3.Distance(t, _start) > StillMeters)
            {
                _resting = true;
                _start = t;
                _since = nowMs;
                Progress = 0f;
                return null;
            }
            Progress = (float)System.Math.Min(1.0, (nowMs - _since) / DwellMs);
            if (nowMs - _since < DwellMs) return null;
            _tapped = _start;
            _resting = false;
            Progress = 0f;
            return _start;
        }

        /// <summary>Forget the rest (and the tap): a fresh start.</summary>
        public void Reset()
        {
            _resting = false;
            _tapped = null;
            Progress = 0f;
        }

        /// <summary>The other hand tapped: this one's rest starts again (one tap, never two at once).</summary>
        public void Restart() => _resting = false;
    }

    public enum SetupPhase { Off, Corners, Side, Done, Cancelled }

    public enum SetupEvent { None, Tapped, Rejected, Measured }

    /// <summary>
    /// The manual display setup (U5b): three taps (top left, top right, bottom right; either hand, a dwell each), then the
    /// side. "Zurück" removes the last point; "Abbrechen" leaves (the previous dock stays). Three points that are no display
    /// (ScreenDock's rules) are dropped and the user is asked again. Pure, time injected.
    /// </summary>
    public sealed class ScreenSetupFlow
    {
        readonly TapDwell _left = new TapDwell();
        readonly TapDwell _right = new TapDwell();
        readonly List<Vec3> _points = new List<Vec3>();

        public SetupPhase Phase { get; private set; } = SetupPhase.Off;
        public IReadOnlyList<Vec3> Points => _points;
        /// <summary>The display, once measured.</summary>
        public ScreenRect Screen { get; private set; }
        /// <summary>Why the last three points were not taken (None: they were, or none yet).</summary>
        public ScreenRejection LastRejection { get; private set; }
        /// <summary>The side picked (Phase Done).</summary>
        public DockSide Side { get; private set; }
        public bool Active => Phase == SetupPhase.Corners || Phase == SetupPhase.Side;

        /// <summary>The ring: the dwelling hand's progress and where its tip rests.</summary>
        public float Progress => System.Math.Max(_left.Progress, _right.Progress);
        public Vec3? RingAt => _left.Progress >= _right.Progress ? _left.RestingAt ?? _right.RestingAt : _right.RestingAt ?? _left.RestingAt;

        public void Begin()
        {
            _points.Clear();
            _left.Reset();
            _right.Reset();
            LastRejection = ScreenRejection.None;
            Phase = SetupPhase.Corners;
        }

        /// <summary>A display found another way (MRUK): straight to the side.</summary>
        public void BeginWith(ScreenRect screen)
        {
            Begin();
            Screen = screen;
            Phase = SetupPhase.Side;
        }

        /// <summary>"Zurück": the last point goes (from the side choice: back to the third corner).</summary>
        public void Back()
        {
            if (Phase == SetupPhase.Side) Phase = SetupPhase.Corners;
            if (Phase != SetupPhase.Corners) return;
            if (_points.Count > 0) _points.RemoveAt(_points.Count - 1);
            _left.Reset();
            _right.Reset();
            LastRejection = ScreenRejection.None;
        }

        public void Cancel()
        {
            if (!Active) return;
            Phase = SetupPhase.Cancelled;
        }

        /// <summary>The side picked: the setup is done.</summary>
        public void Pick(DockSide side)
        {
            if (Phase != SetupPhase.Side) return;
            Side = side;
            Phase = SetupPhase.Done;
        }

        /// <summary>The result was taken (the dock is made): the setup is off.</summary>
        public void Close() => Phase = SetupPhase.Off;

        /// <param name="head">Where the head is (the display faces it).</param>
        public SetupEvent Step(double nowMs, Vec3? leftTip, Vec3? rightTip, Vec3 head)
        {
            if (Phase != SetupPhase.Corners)
            {
                _left.Step(nowMs, null);
                _right.Step(nowMs, null);
                return SetupEvent.None;
            }
            var l = _left.Step(nowMs, leftTip);
            var r = l == null ? _right.Step(nowMs, rightTip) : null;
            if (l != null) _right.Restart();
            if (r != null) _left.Restart();
            var tap = l ?? r;
            if (tap == null) return SetupEvent.None;
            _points.Add(tap.Value);
            if (_points.Count < 3) return SetupEvent.Tapped;
            LastRejection = ScreenDock.FromCorners(_points[0], _points[1], _points[2], head, out var screen);
            if (LastRejection != ScreenRejection.None)
            {
                _points.Clear();
                return SetupEvent.Rejected;
            }
            Screen = screen;
            Phase = SetupPhase.Side;
            return SetupEvent.Measured;
        }
    }

    /// <summary>
    /// What is kept about the docked display besides its spatial anchor (U5b): its width and height and the side the
    /// workspace stands on. Invariant culture; anything unreadable counts as nothing saved. Pure.
    /// </summary>
    public sealed class ScreenRecord
    {
        public const string AnchorKey = "lotse.screen.anchor";
        public const string Key = "lotse.screen.dock";
        public const string OfferedKey = "lotse.screen.offered";

        readonly IKeyValueStore _store;

        public ScreenRecord(IKeyValueStore store) => _store = store;

        /// <summary>
        /// U10: also where the head was at setup time, in the display anchor's own frame, and the window's preferred
        /// distance from there (the window stands on the rays from that seat, see DockSlots.Projected).
        /// </summary>
        public void Save(float width, float height, DockSide side, Vec3? headLocal = null, float distance = 0f)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var s = string.Format(inv, "{0:0.####};{1:0.####};{2}", width, height, side);
            if (headLocal.HasValue)
                s += string.Format(inv, ";{0:0.####};{1:0.####};{2:0.####};{3:0.###}", headLocal.Value.X, headLocal.Value.Y, headLocal.Value.Z, distance);
            _store.Set(Key, s);
        }

        public bool TryLoad(out float width, out float height, out DockSide side) => TryLoad(out width, out height, out side, out _, out _);

        /// <summary>`headLocal` is null for a record of before U10 (the window then stands in the display's plane).</summary>
        public bool TryLoad(out float width, out float height, out DockSide side, out Vec3? headLocal, out float distance)
        {
            width = height = distance = 0f;
            side = DockSide.Right;
            headLocal = null;
            var s = _store.Get(Key);
            if (s == null) return false;
            var parts = s.Split(';');
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            const System.Globalization.NumberStyles F = System.Globalization.NumberStyles.Float;
            var ok = (parts.Length == 3 || parts.Length == 7) &&
                     float.TryParse(parts[0], F, inv, out width) &&
                     float.TryParse(parts[1], F, inv, out height) &&
                     System.Enum.TryParse(parts[2], out side) && System.Enum.IsDefined(typeof(DockSide), side) &&
                     width >= ScreenDock.MinSize && width <= ScreenDock.MaxSize && height >= ScreenDock.MinSize && height <= ScreenDock.MaxSize;
            if (!ok || parts.Length == 3) return ok;
            if (float.TryParse(parts[3], F, inv, out var x) && float.TryParse(parts[4], F, inv, out var y) && float.TryParse(parts[5], F, inv, out var z) &&
                float.TryParse(parts[6], F, inv, out var d) && !float.IsNaN(x + y + z + d) && !float.IsInfinity(x + y + z + d) &&
                d >= WindowPlacement.MinDistance - 1e-3f && d <= WindowPlacement.MaxDistance + 1e-3f)
            {
                headLocal = new Vec3(x, y, z);
                distance = d;
            }
            return true;
        }

        public void Clear() => _store.Delete(Key);

        /// <summary>The offer after the first desk placement is made once.</summary>
        public bool Offered
        {
            get => _store.Get(OfferedKey) == "1";
            set
            {
                if (value) _store.Set(OfferedKey, "1");
                else _store.Delete(OfferedKey);
            }
        }
    }
}
