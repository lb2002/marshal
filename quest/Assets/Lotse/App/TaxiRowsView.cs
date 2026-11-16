using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>What an agent's line says about it.</summary>
    public enum TaxiState
    {
        /// <summary>Working: the dot sends out a ring.</summary>
        Moving,
        /// <summary>It needs the user: the row is raised, a stop bar stands ahead of the dot.</summary>
        Holding,
        /// <summary>Done: the whole line in green.</summary>
        Landed,
        /// <summary>Everything is halted: a stop bar, not raised.</summary>
        Stopped,
        /// <summary>Neither moving nor asking (failed): the dot stands.</summary>
        Idle,
    }

    /// <summary>How a row is drawn: a full line in the overview, a thin line next to an open row, or the open row itself.</summary>
    public enum TaxiRowMode { Full, Thin, Open }

    /// <summary>What one row shows (built by the controller from the store). Title, vendor and step are plain text.</summary>
    public sealed class TaxiRowData
    {
        public string Sid;
        public string Title;
        /// <summary>The vendor line ("Codex in shop-web").</summary>
        public string Vendor;
        public Rgb VendorColor;
        /// <summary>The plan's steps (0: no plan, a line without marks).</summary>
        public int Steps;
        /// <summary>The step the dot stands at (0-based).</summary>
        public int At;
        /// <summary>The text above the dot: the current step, the latest activity, "All 4 steps done".</summary>
        public string Now;
        /// <summary>"3 of 5", "Holding", "Landed", "Stopped", "no plan".</summary>
        public string Status;
        public TaxiState State;

        public string Key => Sid + "|" + Title + "|" + Vendor + "|" + Steps + "|" + At + "|" + Now + "|" + Status + "|" + State;
    }

    /// <summary>
    /// The window's body (U13): one taxi line per agent, five per page, in the agent list's order. A row is the agent's
    /// identity (title up to two lines, the vendor line after a colour dot), its line (the way done painted yellow, the
    /// way ahead dashed, a mark at every step, one white dot where it is, the step written above the dot) and its status
    /// at the right. A row that needs the user is raised and has a stop bar of red lights across the line just ahead of
    /// the dot. Rows fill the height equally. One row can be open: it grows to the height its content needs (the owner
    /// puts the content on it, see OpenBox) and the others shrink to thin lines, in 0.6 s with the one ease-out curve.
    /// A press on a row only reports it; nothing here approves or denies anything, and the lights are not a control.
    /// Origin: the body's top left corner (the content's left edge); the face looks along the parent's -z.
    /// </summary>
    public sealed class TaxiRowsView : MonoBehaviour
    {
        public const int PerPage = 5;
        const float U = Theme.U;
        /// <summary>A row's box reaches this far beyond the content on both sides.</summary>
        public const float Bleed = 20f * U;
        const float Gap = 6f * U;
        const float IdWidth = 350f * U;
        const float ColumnGap = 34f * U;
        const float StatWidth = 104f * U;
        /// <summary>A thin line is never lower than this.</summary>
        public const float ThinMin = 44f * U;
        const float FullMax = 150f * U;
        const float BottomLine = 34f * U;
        /// <summary>The raised row and the open block stand this far in front of the window's plane.</summary>
        public const float RaisedDepth = 0.006f;

        float _width;
        float _area;
        readonly Row[] _rows = new Row[PerPage];
        readonly List<TaxiRowData> _shown = new List<TaxiRowData>();
        TextMeshPro _empty;
        TextMeshPro _incident;
        string _incidentShown;
        string _open;
        float _openHeight;
        float _p = 1f;
        bool _snap = true;
        string _layoutKey;
        string _movedFor;

        public IReadOnlyList<TaxiRowData> Shown => _shown;
        /// <summary>The open row's agent, or null.</summary>
        public string OpenSid => _open;
        /// <summary>The opening, closing or switching motion has finished: only then can an open request be answered.</summary>
        public bool Settled => _p >= 1f;
        /// <summary>The open row is (nearly) at its height: its content shows.</summary>
        public bool ContentShown => _open != null && IndexOf(_open) >= 0 && Taxi.Ease(_p) >= 0.97f;
        public string EmptyShown => _empty.gameObject.activeSelf ? _empty.text : null;
        public string IncidentShown => _incident.gameObject.activeSelf ? _incidentShown : null;
        /// <summary>Counts every layout change (tests).</summary>
        public int Version { get; private set; }

        public static TaxiRowsView Create(Transform parent, Vector3 localTopLeft, float width, float height, Action<string> onRow)
        {
            var v = Ui.Child(parent, "Rows", localTopLeft).AddComponent<TaxiRowsView>();
            v._width = width;
            v._area = height;
            for (var i = 0; i < PerPage; i++)
            {
                var index = i;
                v._rows[i] = new Row(v.transform, i, width, () =>
                {
                    var sid = v._rows[index].Sid;
                    if (sid != null) onRow?.Invoke(sid);
                });
            }
            v._empty = Ui.Text(v.transform, "Empty", Headline.NoAgentsHint, new Vector3(width * 0.5f, 0f, -0.001f), width, Theme.Body, Theme.Muted);
            v._empty.gameObject.SetActive(false);
            v._incident = Ui.Text(v.transform, "Incident", "", Vector3.zero, width, Theme.Small, Theme.Muted, TextAlignmentOptions.Left);
            Ui.Middle(v._incident, width, BottomLine, TextAlignmentOptions.Left);
            v._incident.gameObject.SetActive(false);
            return v;
        }

        /// <summary>The height the rows have (the header's sentence takes one or two lines above them).</summary>
        public void SetArea(float height)
        {
            if (Mathf.Approximately(height, _area)) return;
            _area = height;
            Retarget();
        }

        /// <summary>This page's rows, top to bottom, and which of them is open (null: none).</summary>
        public void Show(IReadOnlyList<TaxiRowData> rows, string openSid)
        {
            var n = Mathf.Min(PerPage, rows.Count);
            if (n != _shown.Count) _snap = true;
            _shown.Clear();
            for (var i = 0; i < n; i++) _shown.Add(rows[i]);
            _open = openSid;
            for (var i = 0; i < PerPage; i++) _rows[i].Set(i < n ? rows[i] : null);
            var on = n == 0;
            if (_empty.gameObject.activeSelf != on) _empty.gameObject.SetActive(on);
            Retarget();
        }

        /// <summary>The height the open row's content needs (clamped to the room there is).</summary>
        public void SetOpenHeight(float height)
        {
            if (Mathf.Approximately(height, _openHeight)) return;
            _openHeight = height;
            Retarget();
        }

        /// <summary>The most an open row can have: the area less the thin lines beside it.</summary>
        public float OpenRoom => _area - (Mathf.Max(1, _shown.Count) - 1) * (ThinMin + Gap);

        /// <summary>The newest incident's line under the rows (null: none); it steps aside while a row is open.</summary>
        public void SetIncident(string line)
        {
            if (line == _incidentShown) return;
            _incidentShown = line;
            if (!string.IsNullOrEmpty(line)) _incident.text = Ui.OneLine(_incident, line, _width);
            Retarget();
        }

        int IndexOf(string sid)
        {
            for (var i = 0; i < _shown.Count; i++)
                if (_shown[i].Sid == sid) return i;
            return -1;
        }

        /// <summary>The pressable area of `sid`'s row, or null (also for the open row: its content is pressed, not the row).</summary>
        public PokeButton RowOf(string sid)
        {
            var i = IndexOf(sid);
            return i >= 0 && _rows[i].Area.gameObject.activeSelf ? _rows[i].Area : null;
        }

        public TaxiRowMode? ModeOf(string sid)
        {
            var i = IndexOf(sid);
            return i >= 0 ? _rows[i].Mode : (TaxiRowMode?)null;
        }

        /// <summary>What `sid`'s row shows as text: its title, the step above the dot, its status (null: not on this page).</summary>
        public string TitleShown(string sid) => IndexOf(sid) >= 0 ? _rows[IndexOf(sid)].TitleShown : null;
        public string NowShown(string sid) => IndexOf(sid) >= 0 ? _rows[IndexOf(sid)].NowShown : null;
        public string StatusShown(string sid) => IndexOf(sid) >= 0 ? _rows[IndexOf(sid)].StatusShown : null;
        public string VendorShown(string sid) => IndexOf(sid) >= 0 ? _rows[IndexOf(sid)].VendorShown : null;
        /// <summary>How many red lights stand across `sid`'s line (0: no stop bar).</summary>
        public int LightsOf(string sid) => IndexOf(sid) >= 0 ? _rows[IndexOf(sid)].Lights : 0;
        public bool RaisedOf(string sid) => IndexOf(sid) >= 0 && _rows[IndexOf(sid)].Raised;
        /// <summary>`sid`'s dot sends out its ring now (a working agent; never with Theme.Still).</summary>
        public bool PingShown(string sid) => IndexOf(sid) >= 0 && _rows[IndexOf(sid)].Pinging;
        /// <summary>Where `sid`'s dot stands along its line now (0..1), and its row's height now (metres).</summary>
        public float DotOf(string sid) => IndexOf(sid) >= 0 ? _rows[IndexOf(sid)].DotAt : 0f;
        public float HeightOf(string sid) => IndexOf(sid) >= 0 ? _rows[IndexOf(sid)].Height : 0f;
        /// <summary>The marks drawn on `sid`'s line and how many of them are painted as done.</summary>
        public (int marks, int done) MarksOf(string sid) => IndexOf(sid) >= 0 ? _rows[IndexOf(sid)].Marks : (0, 0);
        /// <summary>The dashes of `sid`'s way ahead as drawn (from, to), in metres along the row.</summary>
        public IReadOnlyList<(float from, float to)> DashesOf(string sid) => IndexOf(sid) >= 0 ? _rows[IndexOf(sid)].DashesDrawn : null;

        /// <summary>The open row's box now, in this view's coordinates (x right, y up from the body's top left corner).</summary>
        public Rect OpenBox
        {
            get
            {
                var i = _open != null ? IndexOf(_open) : -1;
                if (i < 0) return default;
                var r = _rows[i];
                return new Rect(-Bleed, r.CentreY - r.Height * 0.5f, _width + 2f * Bleed, r.Height);
            }
        }

        void Retarget()
        {
            var n = _shown.Count;
            var open = _open != null ? IndexOf(_open) : -1;
            var incident = !string.IsNullOrEmpty(_incidentShown) && open < 0 && n > 0;
            if (_incident.gameObject.activeSelf != incident) _incident.gameObject.SetActive(incident);
            var area = _area - (incident ? BottomLine : 0f);
            var key = n + "|" + open + "|" + _openHeight.ToString("0.00000") + "|" + area.ToString("0.00000");
            if (key == _layoutKey) return;
            _layoutKey = key;
            Version++;
            var gaps = Mathf.Max(0, n - 1) * Gap;
            for (var i = 0; i < n; i++)
            {
                float target;
                TaxiRowMode mode;
                if (open < 0)
                {
                    target = Mathf.Min(FullMax, (area - gaps) / n);
                    mode = TaxiRowMode.Full;
                }
                else if (i == open)
                {
                    target = Mathf.Clamp(_openHeight, ThinMin, Mathf.Max(ThinMin, area - gaps - (n - 1) * ThinMin));
                    mode = TaxiRowMode.Open;
                }
                else
                {
                    var openH = Mathf.Clamp(_openHeight, ThinMin, Mathf.Max(ThinMin, area - gaps - (n - 1) * ThinMin));
                    target = Mathf.Min(FullMax * 0.5f, (area - gaps - openH) / Mathf.Max(1, n - 1));
                    mode = TaxiRowMode.Thin;
                }
                _rows[i].Target(target, mode);
            }
            _incident.transform.localPosition = new Vector3(_width * 0.5f, -_area + BottomLine * 0.5f, -0.001f);
            // motion answers an action: a row that opens, closes or switches moves (0.6 s); any other change of the
            // layout (the header's sentence takes another line, the open row's content needs another height, another
            // agent came) is placed at once, or joins the motion that runs
            var navigated = _open != _movedFor;
            _movedFor = _open;
            if (_snap || Theme.Still) _p = 1f;
            else if (navigated) _p = 0f;
            _snap = false;
            if (_p >= 1f)
                for (var i = 0; i < n; i++) _rows[i].Arrive();
            Place();
        }

        void OnEnable()
        {
            // coming back on screen (the window was hidden or folded): nothing is half way
            _p = 1f;
            if (_rows[0] == null) return;
            // texts cannot be measured while hidden: everything is drawn again now
            foreach (var r in _rows) r.Redraw();
            Place();
        }

        void Update()
        {
            if (_p < 1f)
            {
                _p = Theme.Still ? 1f : Mathf.Min(1f, _p + Time.deltaTime / Taxi.OpenSeconds);
                Place();
            }
            var t = Time.time;
            for (var i = 0; i < _shown.Count; i++) _rows[i].Tick(t, Time.deltaTime);
        }

        void Place()
        {
            var e = Taxi.Ease(_p);
            var y = 0f;
            for (var i = 0; i < _shown.Count; i++)
            {
                var r = _rows[i];
                r.Place(y, e);
                y += r.Height + Gap;
            }
        }

        /// <summary>One agent's row: its texts, one mesh for its line, one for its lights, one ring.</summary>
        sealed class Row
        {
            const float TrackX = IdWidth + ColumnGap;
            const float LineW = Taxi.LineWidth * U;
            const float DotR = Taxi.Dot * 0.5f * U;
            const float NowUp = 26.5f * U;
            const float NowBox = 22f * U;
            const float TitlePitch = 25f * 1.15f * U;
            const float VendorRow = 19f * U;
            const float VendorDot = 9f * U;
            const float L = FlatMesh.Layer;

            readonly GameObject _go;
            readonly float _width;
            readonly float _trackW;
            readonly FlatMesh _mesh;
            readonly FlatMesh _lights;
            readonly Transform _ping;
            readonly MeshRenderer _pingRenderer;
            readonly TextMeshPro _title;
            readonly TextMeshPro _vendor;
            readonly TextMeshPro _now;
            readonly TextMeshPro _status;
            readonly List<(float from, float to)> _dashes = new List<(float, float)>();
            readonly List<(float from, float to)> _drawn = new List<(float, float)>();

            TaxiRowData _data;
            string _key;
            float _from;
            float _to;
            float _at; // where the dot is drawn (0..1 along the line)
            float _atFrom;
            float _atTo;
            float _travel = 1f;
            int _hover = -1;
            bool _dirty = true;
            bool _textDirty = true;
            float _vendorY;
            float _breath = 1f;
            int _marks;
            int _marksDone;

            public PokeButton Area { get; }
            public string Sid => _data?.Sid;
            public TaxiRowMode Mode { get; private set; }
            public float Height { get; private set; }
            public float CentreY { get; private set; }
            public string TitleShown => _title.gameObject.activeSelf ? _title.text : null;
            public string NowShown => _now.gameObject.activeSelf ? _now.text : null;
            public string StatusShown => _status.gameObject.activeSelf ? _status.text : null;
            public string VendorShown => _vendor.gameObject.activeSelf ? _vendor.text : null;
            public int Lights { get; private set; }
            public float DotAt => _at;
            public bool Pinging => _ping.gameObject.activeInHierarchy;
            public (int, int) Marks => (_marks, _marksDone);
            public IReadOnlyList<(float from, float to)> DashesDrawn => _drawn;
            /// <summary>Raised: it needs the user, or it is the open row.</summary>
            public bool Raised => _data != null && (Mode == TaxiRowMode.Open || _data.State == TaxiState.Holding);

            public Row(Transform parent, int index, float width, Action onPress)
            {
                _width = width;
                _trackW = width - IdWidth - 2f * ColumnGap - StatWidth;
                _go = Ui.Child(parent, "Row " + (index + 1), Vector3.zero);
                var t = _go.transform;
                _mesh = new FlatMesh(t, "Line", 0f);
                _lights = new FlatMesh(t, "Lights", -0.0002f);
                var ring = Ui.Ring(t, "Ping", new Vector3(0f, 0f, -0.0004f), DotR + 4.25f * U, DotR + 5.75f * U, 1f, Theme.Ink);
                _ping = ring.transform;
                _pingRenderer = ring.GetComponent<MeshRenderer>();
                _ping.gameObject.SetActive(false);
                _title = Ui.Text(t, "Title", "", Vector3.zero, IdWidth, Theme.Title, Theme.Text);
                _title.characterSpacing = -2f;
                _vendor = Ui.Text(t, "Vendor", "", Vector3.zero, IdWidth - 2f * VendorDot, Theme.Small, Theme.Muted, TextAlignmentOptions.Left);
                Ui.Middle(_vendor, IdWidth - 2f * VendorDot, VendorRow, TextAlignmentOptions.Left);
                _now = Ui.Text(t, "Now", "", Vector3.zero, _trackW, Theme.Small, Theme.Text, TextAlignmentOptions.Left);
                Ui.Middle(_now, _trackW, NowBox, TextAlignmentOptions.Left);
                _status = Ui.Text(t, "Status", "", Vector3.zero, StatWidth, Theme.Small, Theme.Muted, TextAlignmentOptions.Right);
                Ui.Middle(_status, StatWidth, NowBox, TextAlignmentOptions.Right);
                _status.transform.localPosition = new Vector3(width - StatWidth * 0.5f, 0f, -0.001f);
                Area = PokeButton.CreateArea(t, "Row " + (index + 1), new Vector3(width * 0.5f, 0f, -0.003f), new Vector2(width + 2f * Bleed, ThinMin), onPress);
                Area.OwnerDrawsHover = true; // the row's own mesh draws its hover
                Area.HitPad = Vector2.zero; // rows touch: no padding between them
                _go.SetActive(false);
            }

            public void Set(TaxiRowData data)
            {
                var on = data != null;
                if (_go.activeSelf != on) _go.SetActive(on);
                var sid = data?.Sid;
                var same = sid != null && sid == _data?.Sid;
                if (!same) Area.ResetPress(); // a press in progress was aimed at another agent
                var key = data?.Key;
                if (key == _key && same) return;
                _key = key;
                var before = _atTo;
                _data = data;
                if (!on) return;
                _atTo = Where(data);
                // the dot travels to its new step (about 1 s, ease-out); a row that shows another agent now starts there
                if (same && !Mathf.Approximately(before, _atTo) && !Theme.Still)
                {
                    _atFrom = _at;
                    _travel = 0f;
                }
                else
                {
                    _at = _atTo;
                    _travel = 1f;
                }
                _textDirty = true;
                _dirty = true;
            }

            /// <summary>Where the dot of `d` stands, 0..1 along the line: at its step; at the end when landed.</summary>
            static float Where(TaxiRowData d)
            {
                if (d.State == TaxiState.Landed) return 1f - TaxiLine.Inset;
                return TaxiLine.At(d.At, d.Steps);
            }

            public void Redraw()
            {
                _textDirty = true;
                _dirty = true;
            }

            public void Target(float height, TaxiRowMode mode)
            {
                _from = Height > 0f ? Height : height;
                _to = height;
                if (mode != Mode)
                {
                    Mode = mode;
                    _textDirty = true;
                }
                _dirty = true;
            }

            /// <summary>No motion runs: the row is at its target at once.</summary>
            public void Arrive() => _from = _to;

            /// <summary>The row at `top` below the body's top edge, its height `e` of the way to its target.</summary>
            public void Place(float top, float e)
            {
                var h = Mathf.Lerp(_from, _to, e);
                if (!Mathf.Approximately(h, Height))
                {
                    Height = h;
                    _dirty = true;
                }
                CentreY = -(top + h * 0.5f);
                _go.transform.localPosition = new Vector3(0f, CentreY, Raised ? -RaisedDepth : 0f);
                Area.Resize(new Vector2(_width + 2f * Bleed, h));
                var pressable = Mode != TaxiRowMode.Open;
                if (Area.gameObject.activeSelf != pressable) Area.gameObject.SetActive(pressable);
                if (_data != null && (_textDirty || _dirty)) Draw();
            }

            /// <summary>Every frame: the hover, the dot's travel, the ring of a working agent, the stop bar's slow breathing.</summary>
            public void Tick(float time, float dt)
            {
                if (_data == null) return;
                var hover = Area.gameObject.activeSelf ? Area.AreaState : 0;
                if (hover != _hover)
                {
                    _hover = hover;
                    _dirty = true;
                }
                if (_travel < 1f)
                {
                    _travel = Theme.Still ? 1f : Mathf.Min(1f, _travel + dt / Taxi.TravelSeconds);
                    _at = Mathf.Lerp(_atFrom, _atTo, Taxi.Ease(_travel));
                    _dirty = true;
                    _textDirty = true;
                }
                if (_dirty || _textDirty) Draw();

                var still = Theme.Still;
                // the ring: one thin ring every 2.6 s, from 0.6 to 1.7 of its size, fading out by 70 % of the time
                var pings = !still && Mode != TaxiRowMode.Open && _data.State == TaxiState.Moving;
                if (_ping.gameObject.activeSelf != pings) _ping.gameObject.SetActive(pings);
                if (pings)
                {
                    var k = Mathf.Repeat(time, Taxi.PingSeconds) / Taxi.PingSeconds;
                    var grow = Taxi.Ease(Mathf.Clamp01(k / 0.7f));
                    _ping.localPosition = new Vector3(DotX, 0f, -0.0004f);
                    _ping.localScale = Vector3.one * Mathf.Lerp(0.6f, 1.7f, grow);
                    Ui.SetColor(_pingRenderer, new Color(Look.Ink.R, Look.Ink.G, Look.Ink.B, 0.7f * (1f - grow)));
                }
                // the lights breathe (2.2 s, down to half) only where the user is needed
                var breath = !still && _data.State == TaxiState.Holding && Lights > 0
                    ? 0.8f + 0.2f * Mathf.Cos(time * 2f * Mathf.PI / 2.2f) : 1f;
                if (!Mathf.Approximately(breath, _breath))
                {
                    _breath = breath;
                    DrawLights();
                }
            }

            float DotX => TrackX + _trackW * _at;

            Rgb Surface => Raised ? Look.Raised : Look.Asphalt;

            void Draw()
            {
                if (_textDirty) Texts();
                _textDirty = false;
                _dirty = false;
                var d = _data;
                var m = _mesh;
                var h = Height;
                m.Clear();
                _drawn.Clear();
                _marks = _marksDone = 0;
                var box = new Rect(-Bleed, -h * 0.5f, _width + 2f * Bleed, h);
                var surface = Surface;
                var lift = _hover == 2 ? 0.09f : _hover == 1 ? 0.03f : 0f;
                if (Raised) m.RoundedRect(box, Theme.RowRadius, FlatMesh.Over(Look.Hex(0xFFFFFF), lift, Look.Raised), 6f * L);
                else if (lift > 0f) m.RoundedRect(box, Theme.RowRadius, FlatMesh.Over(Look.Hex(0xFFFFFF), lift, Look.Asphalt), 6f * L);
                if (Mode == TaxiRowMode.Open)
                {
                    m.Apply();
                    DrawLights();
                    return;
                }
                var landed = d.State == TaxiState.Landed;
                var paint = FlatMesh.Solid(landed ? Look.Go : Look.Taxi);
                var faint = FlatMesh.Solid(Look.Faint);
                var x0 = TrackX;
                var dot = DotX;
                var first = x0 + _trackW * TaxiLine.Inset;
                var last = x0 + _trackW * (1f - TaxiLine.Inset);
                // the way done: painted from the line's start to the dot
                if (dot > x0 + 1e-5f && (d.Steps > 0 || landed)) m.RoundedRect(new Rect(x0, -LineW * 0.5f, dot - x0, LineW), LineW * 0.5f, paint, 3f * L);
                // the way ahead: dashes fitted per stretch, each stretch starting with a dash at its mark
                if (!landed)
                {
                    var n = Mathf.Max(1, d.Steps - 1);
                    for (var i = 0; i < n; i++)
                    {
                        var a = d.Steps > 1 ? x0 + _trackW * TaxiLine.At(i, d.Steps) : first;
                        var b = d.Steps > 1 ? x0 + _trackW * TaxiLine.At(i + 1, d.Steps) : last;
                        if (b <= dot + 1e-5f) continue; // behind the dot: painted
                        TaxiLine.Dashes(a, b, Taxi.DashPeriod * U, Taxi.DashShare, _dashes);
                        foreach (var (from, to) in _dashes)
                        {
                            // a dash under the dot's ring is not drawn, and no sliver of one beside it
                            var f = Mathf.Max(from, dot + DotR + Taxi.DotRing * U);
                            if (to - f < 4f * U) continue;
                            m.Rect(new Rect(f, -LineW * 0.5f, to - f, LineW), faint, 3f * L);
                            _drawn.Add((f, to));
                        }
                    }
                }
                // a mark at every step, painted up to the dot
                var mw = Taxi.MarkWidth * U;
                var mh = Taxi.MarkHeight * U;
                for (var i = 0; i < d.Steps; i++)
                {
                    var x = x0 + _trackW * TaxiLine.At(i, d.Steps);
                    var done = landed || x <= dot + 1e-5f;
                    m.RoundedRect(new Rect(x - mw * 0.5f, -mh * 0.5f, mw, mh), mw * 0.5f, done ? paint : faint, 2f * L);
                    _marks++;
                    if (done) _marksDone++;
                }
                // the dot, with a ring in the surface's colour so the line does not touch it
                m.Disc(new Vector2(dot, 0f), DotR + Taxi.DotRing * U, FlatMesh.Solid(surface), L);
                m.Disc(new Vector2(dot, 0f), DotR, FlatMesh.Solid(landed ? Look.Go : Look.Ink), 0f);
                // the vendor's colour: only this dot
                if (Mode == TaxiRowMode.Full)
                    m.Disc(new Vector2(VendorDot * 0.5f, _vendorY), VendorDot * 0.5f, FlatMesh.Solid(d.VendorColor), 0f);
                m.Apply();
                DrawLights();
            }

            /// <summary>The stop bar across the line: seven red lights (three on a thin line) just ahead of the dot, with their glow.</summary>
            void DrawLights()
            {
                var d = _data;
                var m = _lights;
                m.Clear();
                var bar = Mode != TaxiRowMode.Open && (d.State == TaxiState.Holding || d.State == TaxiState.Stopped);
                Lights = 0;
                if (bar)
                {
                    var thin = Mode == TaxiRowMode.Thin;
                    var count = thin ? 3 : Taxi.RowLights;
                    var w = (thin ? 12f : Taxi.RowLightWidth) * U;
                    var lh = (thin ? 7f : Taxi.RowLightHeight) * U;
                    var gap = (thin ? 4f : Taxi.RowLightGap) * U;
                    var x = DotX + Taxi.RowBarAhead * U;
                    var total = count * lh + (count - 1) * gap;
                    var surface = Surface;
                    var k = _breath;
                    var glow = FlatMesh.Over(Look.Stop, 0.20f * k, surface);
                    var column = new Rect(x, -total * 0.5f, w, total);
                    // the glow: one soft halo around the whole bar, behind the lights (nothing else glows)
                    m.RoundedRect(column, Theme.LightRadius, glow, 2f * L);
                    m.Halo(column, Theme.LightRadius, 8f * U, glow, FlatMesh.Solid(surface), 2f * L);
                    var red = FlatMesh.Over(Look.Stop, k, surface);
                    for (var i = 0; i < count; i++)
                        m.RoundedRect(new Rect(x, total * 0.5f - lh - i * (lh + gap), w, lh), Theme.LightRadius, red, 0f);
                    Lights = count;
                }
                m.Apply();
                m.Renderer.enabled = bar;
            }

            void Texts()
            {
                var d = _data;
                var open = Mode == TaxiRowMode.Open;
                var full = Mode == TaxiRowMode.Full;
                Show(_title, !open);
                Show(_vendor, full);
                Show(_now, full);
                Show(_status, !open);
                if (open) return;

                // the title: 25 units SemiBold on up to two lines; on a thin line 19 units Medium, muted, one line
                _title.rectTransform.pivot = new Vector2(0.5f, 1f);
                _title.rectTransform.sizeDelta = new Vector2(IdWidth, 0.01f);
                _title.lineSpacing = 0f;
                if (full)
                {
                    _title.font = Fonts.SemiBold;
                    _title.fontSize = Theme.Title;
                    _title.color = Theme.Text;
                    _title.textWrappingMode = TextWrappingModes.Normal;
                    _title.alignment = TextAlignmentOptions.TopLeft;
                    _title.text = Ui.TwoLines(_title, d.Title ?? "");
                    var lines = Ui.Rows(_title, TitlePitch, out var shift);
                    var block = lines * TitlePitch + 5f * U + VendorRow;
                    _title.transform.localPosition = new Vector3(IdWidth * 0.5f, block * 0.5f + shift, -0.001f);
                    _vendorY = block * 0.5f - lines * TitlePitch - 5f * U - VendorRow * 0.5f;
                    _vendor.text = Ui.OneLine(_vendor, d.Vendor ?? "", IdWidth - 2f * VendorDot);
                    _vendor.transform.localPosition = new Vector3(2f * VendorDot + (IdWidth - 2f * VendorDot) * 0.5f, _vendorY, -0.001f);
                }
                else
                {
                    _title.font = Fonts.Medium;
                    _title.fontSize = Theme.Body;
                    _title.color = Theme.Muted;
                    _title.text = Ui.OneLine(_title, d.Title ?? "", IdWidth);
                    Ui.Middle(_title, IdWidth, NowBox, TextAlignmentOptions.Left);
                    _title.transform.localPosition = new Vector3(IdWidth * 0.5f, 0f, -0.001f);
                }

                var red = d.State == TaxiState.Holding || d.State == TaxiState.Stopped;
                _status.font = red ? Fonts.Medium : Fonts.Regular;
                _status.color = red ? Theme.Stop : Theme.Muted;
                _status.text = d.Status ?? "";

                if (!full) return;
                // the step above the dot: left-anchored at the dot; right-anchored when the dot is far along or a bar
                // stands ahead of it; behind the bar when the dot is near the start
                var bar = red;
                var anchor = TaxiLine.Anchor(_at, bar);
                var dot = DotX;
                float left, right;
                if (anchor == NowAnchor.Left)
                {
                    left = dot - DotR;
                    right = TrackX + _trackW;
                }
                else if (anchor == NowAnchor.Right)
                {
                    left = TrackX - ColumnGap * 0.5f;
                    right = dot + DotR;
                }
                else
                {
                    left = dot + 62f * U - DotR;
                    right = TrackX + _trackW;
                }
                var room = Mathf.Max(0.01f, right - left);
                _now.color = d.State == TaxiState.Landed || d.State == TaxiState.Stopped ? Theme.Muted : Theme.Text;
                _now.text = Ui.OneLine(_now, d.Now ?? "", room);
                Ui.Middle(_now, room, NowBox, anchor == NowAnchor.Right ? TextAlignmentOptions.Right : TextAlignmentOptions.Left);
                _now.transform.localPosition = new Vector3(left + room * 0.5f, NowUp, -0.001f);
            }

            static void Show(TextMeshPro t, bool on)
            {
                if (t.gameObject.activeSelf != on) t.gameObject.SetActive(on);
            }
        }
    }
}
