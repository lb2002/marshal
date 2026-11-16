using System.Collections.Generic;
using System.Linq;
using Lotse.Core;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// The window, wired (U5a, U6; U13's layout): the agents as rows in AgentOrder's order (five per page), the header's
    /// one sentence, and at most one open row: the selected agent's request (its oldest open one) or its work: its feed
    /// (feed_subscribe on select, feed_unsubscribe on switching away, again after a reconnect; merged by FeedLog,
    /// scrolled by FeedScroll), its plan, and the input bar (the ReplyController's; replies wait while the agent has an
    /// open request). There are no tabs: WindowMode's "Overview" means no row is open, "Conversation" that the selected
    /// agent's row is. A row opens only by the user (a row, a sign of the rail, the pill); "All agents", "Later", a
    /// deny from here and an allow (after the cleared bar stood a moment) close it. Folded it is the rail: "Fold away",
    /// or by itself after 20 s with the head away (CollapseTimer, unless a draft, a keyboard, a hold or a touch keeps
    /// it open); it opens by a poke or a ray click on the rail only, never by itself.
    /// Display and selection only: what is approved is decided by the session (DemoSession), on the request this
    /// controller reports as on screen. That is only the open row's, once its content shows (ConversationOnScreen);
    /// and the session sees the hands only once the row's motion has finished (OpenSettled). Opening, closing and
    /// switching rows tell the session at once, so a running hold is cancelled, and coming back needs a fresh pinch.
    /// </summary>
    public sealed class WorkspaceController
    {
        const float DragPinch = 0.8f;
        const float DragNear = 0.04f;
        /// <summary>An incident's line stays at the top of the agents' conversations this long.</summary>
        public const double IncidentLineMs = 5 * 60 * 1000;
        /// <summary>"Freigegeben" and "Abgelehnt" stand in the block's place this long.</summary>
        public const double DecidedNoteMs = 3000;
        /// <summary>A note that waits for the next feed entry goes after this at the latest.</summary>
        public const double NoteMaxMs = 120000;
        /// <summary>The status lines are written again at least this often ("fertig vor 3 Min.").</summary>
        const double RowsEveryMs = 10000;

        sealed class Note
        {
            public string Text;
            public Color Color;
            public double Until;
            public bool UntilFeed;
            public int FeedVersion = int.MinValue;
        }

        readonly WorkspaceView _view;
        readonly ReplyController _reply;
        readonly AgentOrder _order = new AgentOrder();
        readonly ListScroll _list = new ListScroll(TaxiRowsView.PerPage);
        readonly AgentDetails _details = new AgentDetails();
        readonly FeedLog _feeds = new FeedLog();
        readonly FeedScroll _scroll = new FeedScroll();
        readonly FeedSubscription _feed;
        readonly Dictionary<string, int> _palette = new Dictionary<string, int>();
        readonly Dictionary<string, double> _doneAt = new Dictionary<string, double>();
        readonly Dictionary<string, (string line, double at)> _incidents = new Dictionary<string, (string, double)>();
        readonly Dictionary<string, Note> _notes = new Dictionary<string, Note>();
        readonly List<TaxiRowData> _rows = new List<TaxiRowData>();
        readonly WindowMode _mode = new WindowMode();
        readonly ColumnPages _pages = new ColumnPages();
        readonly ActivityTrail _trail = new ActivityTrail();
        readonly CollapseTimer _collapseTimer = new CollapseTimer();
        readonly List<RailSlot> _railSlots = new List<RailSlot>();
        (string line, double at)? _newestIncident;
        bool _planOpen;
        bool _overviewDirty = true;
        // the order, the waiting state or the page changed: drawn at once; agent data streaming in: at most every 250 ms
        bool _overviewUrgent = true;
        bool _railDirty = true;
        double _railAt = double.NegativeInfinity;
        /// <summary>Agent messages redraw the overview at most this often (U6 review 1); the last state is always shown.</summary>
        public const double OverviewMinMs = 250;
        bool _headerDirty = true;
        int _pendingCount;
        double _overviewAt = double.NegativeInfinity;
        int _shownModeVersion = -1;
        DemoSession _session;
        string _selected;
        int _lastCount;
        bool _autoPending;
        bool _summaryOpen;
        bool _rowsDirty = true;
        bool _blockDirty = true;
        bool _replyBlocked;
        double _rowsAt = double.NegativeInfinity;
        double _nowMs;
        int _shownFeedVersion = -1;
        int _shownBottom = -1;
        bool _shownFollows;
        float _shownFeedHeight = -1f;
        string _shownBlock;
        int _dragHand;
        float _dragY;
        string _sentence;
        string _clearedSid;
        double _clearedUntil;
        /// <summary>The cleared bar ("Cleared. Codex is moving again") stands this long, then the row closes.</summary>
        public const double ClearedMs = 1000;
        /// <summary>
        /// The green "Cleared" stands in the block's place (U14): whatever the session's next request is, it is not on
        /// screen yet, so nothing may be approved.
        /// </summary>
        public bool ClearedShowing => _clearedSid != null && _view.Conversation.Block.ClearedShown;

        public WorkspaceController(WorkspaceView view, ReplyController reply)
        {
            _view = view;
            _reply = reply;
            _feed = new FeedSubscription(_feeds);
            // "Continue", then "Really continue? Yes": only then the release goes out (it has no gesture)
            _view.Halt.OnRelease = Release;
        }

        /// <summary>A drag scrolls the list or the feed (the stop gesture waits meanwhile).</summary>
        public bool Dragging => _dragHand != 0;

        string _enforcementLine = "";
        long _haltSecondShown = -1;
        string _haltSince = "";
        bool _haltAsking;

        void Release()
        {
            if (_session == null || !_session.Halted) return;
            var sent = _session.RequestRelease();
            // (U14) the stop sound reversed in pitch
            if (sent) Sounds.Play(Sfx.Continue, _view.transform.position);
            Debug.Log(sent ? "[Lotse] halt: release sent (Continue, confirmed)" : "[Lotse] halt: release not sent (no connection)");
        }

        void OnHaltChanged()
        {
            _enforcementLine = Texts.Enforcement(_session.Enforcement);
            _overviewDirty = true;
            _railDirty = true;
            _rowsDirty = true;
            Debug.Log(_session.Halted ? "[Lotse] halt: the bridge says halted" : "[Lotse] halt: the bridge says released");
        }

        /// <summary>The stop gesture's progress (0: no bar): along the window's top edge, or down the rail when folded.</summary>
        public void SetStopProgress(float progress) => _view.Halt.SetBar(progress, _view.PanelRect, _view.Folded, _view.RailOnRight);

        /// <summary>Halted (the bridge's state only): the band and its line, the pause badges, no pill.</summary>
        void ShowHalt(double nowMs)
        {
            var halted = _session != null && _session.Halted;
            if (halted != _view.Halted) _headerDirty = true;
            _view.SetHalted(halted);
            if (!halted)
            {
                _view.Halt.ShowHalted(false, "", "");
                return;
            }
            if (_view.Pill.IsShown) _view.Pill.Hide();
            _deferred = null;
            var second = (long)System.Math.Floor((_session.NowMs - _session.HaltedAtMs) / 1000.0);
            if (second != _haltSecondShown)
            {
                _haltSecondShown = second;
                _haltSince = Texts.HaltedFor(_session.NowMs - _session.HaltedAtMs);
            }
            _view.Halt.ShowHalted(true, _haltSince, _enforcementLine);
            if (_view.Halt.Asking != _haltAsking)
            {
                _haltAsking = _view.Halt.Asking;
                _headerDirty = true;
            }
        }

        public WorkspaceView View => _view;
        public AgentDetails Details => _details;
        public FeedLog Feeds => _feeds;
        public FeedScroll Scroll => _scroll;
        public ListScroll ListScroll => _list;
        public AgentOrder Order => _order;
        /// <summary>The agent whose conversation is shown, or null.</summary>
        public string Selected => _selected;
        /// <summary>The agent whose feed is subscribed now, or null.</summary>
        public string Subscribed => _feed.Sid;
        public ApprovalBlockView Block => _view.Conversation.Block;
        /// <summary>The block shows a request (the hold gate checks the head faces its centre).</summary>
        public bool BlockShown => Block.IsShown && Block.RequestId != null && ConversationOnScreen;

        /// <summary>
        /// The open row's motion has finished (U13): only then may the session see the hands. A request can be approved
        /// only when its row is open and the opening has finished; while the row still opens, closes or switches, no
        /// hold can start or continue (AppRoot keeps the hands from the session and resets the gesture on the change).
        /// </summary>
        public bool OpenSettled => ConversationOnScreen && _view.OpenSettled;

        /// <summary>The open row's agent (null: no row is open).</summary>
        public string OpenSid => _mode.Tab == WorkspaceTab.Conversation && !_mode.Collapsed ? _selected : null;

        /// <summary>The tab and the rail (U6).</summary>
        public WindowMode Mode => _mode;
        public ColumnPages Pages => _pages;
        public ActivityTrail Trail => _trail;
        public CollapseTimer CollapseTimer => _collapseTimer;
        /// <summary>The setting "Automatisch einklappen" (on by default).</summary>
        public bool AutoCollapse { get; set; } = true;

        /// <summary>
        /// The approval block's place is on screen: Unterhaltung, not collapsed, the window fully open. The only state in
        /// which the session may show a block (AppRoot: DemoSession.SetViewShown).
        /// </summary>
        public bool ConversationOnScreen => _session != null && _mode.ConversationShown && _selected != null && _view.ContentShown &&
                                            _view.Rows.OpenSid == _selected;

        /// <summary>
        /// The window's state: no row open (Overview) or the selected agent's row open (Conversation). Any change hides
        /// the request at once: a running hold is cancelled.
        /// </summary>
        public void ShowTab(WorkspaceTab tab)
        {
            if (tab == WorkspaceTab.Conversation && _selected == null) return; // only an agent's row can be open
            if (!_mode.Show(tab)) return;
            Debug.Log("[Lotse] tab " + tab);
            _session?.SetViewShown(false);
            DropBlock();
            if (tab == WorkspaceTab.Overview) Ev.Emit("row close");
            Moved();
        }

        /// <summary>The row closes or shows another agent: the request that stood on it goes at once, with its buttons.</summary>
        void DropBlock()
        {
            _view.Conversation.Block.Hide();
            _shownBlock = null;
            _blockDirty = true;
        }

        /// <summary>"All agents", "Later", or a decision made: the open row closes. A running hold is cancelled.</summary>
        public void CloseRow()
        {
            if (_mode.Collapsed) return;
            ShowTab(WorkspaceTab.Overview);
        }

        /// <summary>A row pressed: it opens in place (another one open: it switches to this one).</summary>
        public void OpenRow(string sid) => OpenAgent(sid);

                /// <summary>"Einklappen" (or the gaze timer): the rail. The block goes at once, a hold with it; the draft stays.</summary>
        public void Collapse()
        {
            if (!_mode.Collapse()) return;
            Debug.Log("[Lotse] collapsed");
            _session?.SetViewShown(false);
            _view.Pill.Hide();
            _deferred = null;
            Moved();
        }

        /// <summary>A press on the rail, or the gaze: open into the tab that was open.</summary>
        public void ExpandFromRail()
        {
            if (!_mode.Collapsed) return;
            _mode.Expand();
            Debug.Log("[Lotse] expanded into " + _mode.Tab);
            Moved();
        }

        /// <summary>A column of the overview, an orb of the rail, or the pill: that agent's conversation (Unterhaltung).</summary>
        public void OpenAgent(string sid)
        {
            if (_session == null || AgentOf(sid) == null) return;
            // the row opens, or switches: the request's place is off screen until the motion has finished, a hold with it
            _session.SetViewShown(false);
            if (sid != _selected || _mode.Tab != WorkspaceTab.Conversation) DropBlock();
            Select(sid);
            if (_mode.Collapsed) _mode.Expand(WorkspaceTab.Conversation);
            else _mode.Show(WorkspaceTab.Conversation);
            _pages.Update(_order.Sids.Count);
            for (var i = 0; i < _order.Sids.Count; i++)
                if (_order.Sids[i] == sid) _pages.Reveal(i);
            Debug.Log("[Lotse] row open " + Texts.LogSid(sid));
            var index = 0;
            for (var i = 0; i < _session.Store.Agents.Count; i++)
                if (_session.Store.Agents[i].Sid == sid) index = i;
            Ev.Emit("row open", ("id", index), ("kind", _session.HasRequest(sid) ? "request" : "working"));
            _clearedSid = null;
            Moved();
        }

        public void PreviousPage()
        {
            if (OpenSid != null) CloseRow(); // the open row's agent would leave the page
            _pages.Previous();
            _overviewDirty = true;
            _overviewUrgent = true;
            _railDirty = true;
            _headerDirty = true;
        }

        public void NextPage()
        {
            if (OpenSid != null) CloseRow();
            _pages.Next();
            _overviewDirty = true;
            _overviewUrgent = true;
            _railDirty = true;
            _headerDirty = true;
        }

        void Moved()
        {
            _headerDirty = true;
            _collapseTimer.Reset();
            _rowsDirty = true;
            _blockDirty = true;
            _overviewDirty = true;
            _overviewUrgent = true;
            _railDirty = true;
            _shownFeedVersion = -1;
            EndDrag();
        }

        void TogglePlan() => _planOpen = !_planOpen;

        /// <summary>A new session (or none): everything shown and known goes.</summary>
        public void Reset(DemoSession session)
        {
            _feed.Close();
            _reply.Detach();
            _feed.Use(session);
            _details.Clear();
            _feeds.Clear();
            _scroll.Reset();
            _order.Clear();
            _palette.Clear();
            _doneAt.Clear();
            _incidents.Clear();
            _notes.Clear();
            _rows.Clear();
            _mode.Reset();
            _pages.Reset();
            _trail.Clear();
            _collapseTimer.Reset();
            _newestIncident = null;
            _planOpen = false;
            _overviewDirty = true;
            _overviewUrgent = true;
            _railDirty = true;
            _headerDirty = true;
            _selected = null;
            _lastCount = 0;
            _autoPending = false;
            _summaryOpen = false;
            _rowsDirty = true;
            _blockDirty = true;
            _shownFeedVersion = -1;
            _dragHand = 0;
            _view.Pill.Hide();
            _deferred = null;
            _view.Conversation.Block.Hide();
            _view.Conversation.DropInput();
            _sentence = null;
            _clearedSid = null;
            if (_session != null)
            {
                _session.AgentUpdated -= OnAgent;
                _session.CardChanged -= OnCardChanged;
                _session.RequestClosed -= OnRequestClosed;
                _session.AgentDetailArrived -= OnDetail;
                _session.AgentDetailFailed -= OnDetailFailed;
                _session.FeedArrived -= OnFeed;
                _session.FeedFailed -= OnFeedFailed;
                _session.HaltChanged -= OnHaltChanged;
            }
            _session = session;
            _enforcementLine = "";
            _haltSecondShown = -1;
            ShowHalt(0);
            if (session == null) return;
            session.HaltChanged += OnHaltChanged;
            session.AgentUpdated += OnAgent;
            session.CardChanged += OnCardChanged;
            session.RequestClosed += OnRequestClosed;
            session.AgentDetailArrived += OnDetail;
            session.AgentDetailFailed += OnDetailFailed;
            session.FeedArrived += OnFeed;
            session.FeedFailed += OnFeedFailed;
        }

        /// <summary>A row or the pill: the agent's conversation shows (its feed subscribed, its detail asked for, its input bar built).</summary>
        public void Select(string sid)
        {
            if (_session == null) return;
            var agent = AgentOf(sid);
            if (sid != null && agent == null) return;
            Debug.Log("[Lotse] agent selected " + Texts.LogSid(sid));
            var fresh = sid != _selected;
            _selected = sid;
            _autoPending = false;
            _session.Select(sid); // selecting again also brings back a block "Später" hid
            if (fresh)
            {
                _summaryOpen = false;
                _planOpen = false;
                _scroll.Reset();
                _shownFeedVersion = -1;
                if (sid == null) _feed.Close();
                else Debug.Log(_feed.Open(sid) ? "[Lotse] feed subscribed " + Texts.LogSid(sid) : "[Lotse] feed not available for " + Texts.LogSid(sid));
                if (agent != null) _reply.Attach(agent, _view.Conversation.BuildInput, _nowMs);
                else _reply.Detach();
            }
            if (sid != null && _details.TryRequest(sid, _nowMs))
            {
                var asked = _session.RequestDetail(sid);
                if (!asked) _details.NoAnswer(sid);
            }
            _rowsDirty = true;
            _blockDirty = true;
            _overviewDirty = true;
            _overviewUrgent = true;
            _headerDirty = true;
        }

        /// <summary>A new connection to the bridge (it ends every feed on disconnect): the shown agent subscribes again.</summary>
        public void OnReconnected()
        {
            if (_selected != null) _feed.Reconnected();
        }

        /// <summary>A request wants the user: the pill above the window, for its agent (when the window is hidden: once it shows).</summary>
        public void ShowNotice(PermRequest req)
        {
            var headline = Texts.Headline(_session?.Store.ExplainFor(req.Id), req);
            Pill(Texts.Notice(AgentOf(req.Sid), req.Sid, headline), req.Sid, true, req.Id);
            if (PillRedundant(req.Sid)) _session?.NoticeNotShown();
        }

        (string line, string sid, bool needsYou, string requestId)? _deferred;

        void Pill(string line, string sid, bool needsYou, string requestId)
        {
            // halted: no pill (no request waits for the user: the bridge denies them)
            if (_session != null && _session.Halted) return;
            // collapsed: no pill (the orb pulses and the chime sounds); it is not kept for later either
            if (_mode.Collapsed) return;
            // U10: no pill where the window already says it: in the overview (the red card, the incident line), and for
            // the agent whose conversation is shown (its block, its incident line)
            if (PillRedundant(sid)) return;
            if (!_view.IsShown || !_view.Open)
            {
                _deferred = (line, sid, needsYou, requestId);
                return;
            }
            _deferred = null;
            _view.Pill.Show(line, sid, needsYou, (float)(DemoSession.NoticeMs / 1000.0));
        }

        bool PillRedundant(string sid) => _mode.Tab == WorkspaceTab.Overview || (sid != null && sid == _selected);

        /// <summary>An incident: one muted line at the top of each affected agent's conversation, and the pill.</summary>
        public void ShowIncident(Incident incident)
        {
            var show = IncidentLook.For(incident);
            foreach (var sid in show.Sids) _incidents[sid] = (show.Line, _nowMs);
            if (show.Line.Length > 0) _newestIncident = (show.Line, _nowMs);
            _overviewDirty = true;
            if (show.Line.Length > 0 && show.Sids.Count > 0) Pill(show.Line, show.Sids[0], false, null);
        }

        /// <summary>
        /// Each frame: the window shown or not, the list in order, the conversation of the selected agent, the block, the
        /// reply's step (blocked while placing, outside the playing phase, or while the agent has an open request), drags.
        /// </summary>
        public void Step(double nowMs, bool shown, bool blocked, in HandFrame left, in HandFrame right) =>
            Step(nowMs, shown, blocked, left, right, Vector3.zero, Vector3.zero, false);

        /// <param name="head">The centre eye (the gaze timers; zero forward: no gaze).</param>
        /// <param name="holding">A hold runs (it keeps the window open).</param>
        public void Step(double nowMs, bool shown, bool blocked, in HandFrame left, in HandFrame right, Vector3 head, Vector3 headForward, bool holding)
        {
            _nowMs = nowMs;
            _view.Show(shown && _session != null);
            // a row is open only while its agent is selected (the selection can go with the agent)
            if (_mode.Tab == WorkspaceTab.Conversation && _selected == null && !_mode.Collapsed) _mode.Show(WorkspaceTab.Overview);
            _view.SetMode(_mode.Tab, _mode.Collapsed);
            ShowHalt(nowMs);
            if (_session == null)
            {
                _reply.Step(nowMs, true);
                return;
            }
            var agents = _session.Store.Agents;
            // (U14) while a row stands open, and until its closing motion has finished, the rows keep their places
            var keepPlaces = !_mode.Collapsed && _view.IsShown && (OpenSid != null || !_view.Rows.Settled);
            if (_order.Update(agents, _session.Store.Pending, keepPlaces) || _session.Store.Pending.Count != _pendingCount)
            {
                _pendingCount = _session.Store.Pending.Count;
                _rowsDirty = true;
                _overviewDirty = true;
                _overviewUrgent = true;
                _railDirty = true;
                _headerDirty = true;
            }
            for (var i = 0; i < agents.Count; i++)
            {
                var a = agents[i];
                if (!_palette.ContainsKey(a.Sid)) _palette[a.Sid] = _palette.Count;
                if (a.Status == "done") { if (!_doneAt.ContainsKey(a.Sid)) _doneAt[a.Sid] = nowMs; }
                else _doneAt.Remove(a.Sid);
            }
            // an empty window that gets its first agent shows it: only when the list goes from no agent to one, never
            // otherwise (with one agent there is nothing else to show); nothing was selected, so no hold can be running
            if (agents.Count != _lastCount)
            {
                _autoPending = _lastCount == 0 && agents.Count == 1;
                _lastCount = agents.Count;
            }
            if (_autoPending && shown && _selected == null && agents.Count == 1)
            {
                Debug.Log("[Lotse] the first agent in an empty window is shown");
                Select(agents[0].Sid);
            }
            if (_selected != null && AgentOf(_selected) == null)
            {
                Debug.Log("[Lotse] the selected agent is gone");
                Select(null);
            }

            // the reply: blocked while placing, in another phase, or while this agent has an open request
            var asks = _selected != null && _session.HasRequest(_selected);
            // the reply needs its input bar on screen: not in Übersicht, not collapsed (the draft is kept)
            var replyBlocked = blocked || asks || !ConversationOnScreen;
            if (!replyBlocked && _replyBlocked && _selected != null && AgentOf(_selected) != null)
                _reply.Attach(AgentOf(_selected), _view.Conversation.BuildInput, nowMs);
            _replyBlocked = replyBlocked;
            _reply.Step(nowMs, replyBlocked);
            var bar = _view.Conversation.Input;
            if (asks && bar != null) bar.SetStatus(Texts.ReplyAfterRequest, Theme.Muted, false, false);
            // the cleared bar stood its moment: the row closes and the dot moves on (unless the agent asks again)
            if (_clearedSid != null && nowMs >= _clearedUntil)
            {
                var sid = _clearedSid;
                _clearedSid = null;
                _blockDirty = true;
                if (sid == OpenSid && !_session.HasRequest(sid)) CloseRow();
            }

            // nothing is drawn while the window is hidden (text objects measure only when active); all of it on its return
            if (!_view.IsShown)
            {
                _rowsDirty = true;
                _blockDirty = true;
                _shownFeedVersion = -1;
                EndDrag();
                return;
            }
            if (_deferred.HasValue)
            {
                var d = _deferred.Value;
                if (d.requestId == null || _session.Store.Pending.Any(p => p.Id == d.requestId)) Pill(d.line, d.sid, d.needsYou, d.requestId);
                _deferred = null;
            }
            Gaze(nowMs, shown, left, right, head, headForward, holding);
            if (_view.Pill.IsShown && PillRedundant(_view.Pill.Sid)) _view.Pill.Hide(); // the tab or the selection changed under it
            if (_mode.Version != _shownModeVersion)
            {
                _shownModeVersion = _mode.Version;
                Moved();
            }
            // the rail is drawn from the moment the fold starts: its height (what its orbs need) is where the fold ends
            if (_mode.Collapsed && (_railDirty || nowMs - _railAt >= RowsEveryMs)) DrawRail(nowMs);
            if (_view.Folded)
            {
                EndDrag();
                return;
            }
            if (!_view.Open)
            {
                EndDrag();
                return;
            }
            if (_headerDirty || nowMs - _headerAt >= OverviewMinMs) DrawHeader(nowMs);
            if (_overviewUrgent || (_overviewDirty && nowMs - _overviewAt >= OverviewMinMs) || nowMs - _overviewAt >= RowsEveryMs)
                DrawRows(nowMs);
            if (_mode.Tab == WorkspaceTab.Overview || !_view.ContentShown || _selected == null)
            {
                _blockDirty = true;
                _shownFeedVersion = -1;
                EndDrag();
                return;
            }
            DrawConversation(nowMs, left, right);
        }

        double _headerAt = double.NegativeInfinity;

        /// <summary>The vendor's name of `a` (our own table).</summary>
        static string Vendor(AgentState a) => Texts.SourceWord(a.Source);

        /// <summary>It needs the user: an open request, or it waits for the user's word.</summary>
        bool Holds(AgentState a) => AgentOrder.GroupOf(a, _session.HasRequest(a.Sid)) == AgentGroup.NeedsYou;

        /// <summary>
        /// The header's one sentence: halted; the open row's request ("Codex wants to delete a folder, for good.") or its
        /// agent's work ("Codex is on step 3 of 5."); else the overview's (Headline). A changed sentence is a visible
        /// change: a running hold is cancelled.
        /// </summary>
        void DrawHeader(double nowMs)
        {
            _headerDirty = false;
            _headerAt = nowMs;
            var agents = _session.Store.Agents;
            string sentence;
            var open = OpenSid != null ? AgentOf(OpenSid) : null;
            if (_session.Halted) sentence = Headline.Stopped;
            else if (open != null)
            {
                var request = OldestRequest(open.Sid);
                if (request != null) sentence = Texts.Headline(_session.Store.ExplainFor(request.Id), request);
                else if (open.Status == "done") sentence = Headline.Landed(Vendor(open));
                else if (Holds(open)) sentence = Headline.Waiting(Vendor(open));
                else
                {
                    var plan = open.Plan;
                    var current = plan != null ? PlanLines.Current(plan) : -1;
                    sentence = Headline.Working(Vendor(open), current + 1, plan != null && current >= 0 ? PlanLines.Total(plan) : 0);
                }
            }
            else
            {
                int holding = 0, landed = 0;
                string vendor = null;
                for (var i = 0; i < agents.Count; i++)
                {
                    if (agents[i].Status == "done") landed++;
                    else if (Holds(agents[i]))
                    {
                        holding++;
                        vendor = Vendor(agents[i]);
                    }
                }
                sentence = Headline.Overview(false, agents.Count, holding, holding == 1 && vendor != "Agent" ? vendor : null, landed);
            }
            if (sentence != _sentence)
            {
                // the headline changing counts as a visible change (U13): a hold never runs across it
                if (_sentence != null) _session.Rearm();
                _sentence = sentence;
            }
            _pages.Update(_order.Sids.Count);
            _view.Header.Show(sentence, open != null, _pages.Page, _pages.Pages, _view.Halt.Reserve, _session.Halted);
        }

        /// <summary>The sentence the header shows now.</summary>
        public string Sentence => _sentence;

                /// <summary>
        /// The gaze timers: open, the window folds after 20 s with the head away (unless a draft, a keyboard, a hold or a ray
        /// or poke on the window keeps it open); folded, it opens after 0.8 s of the head on the rail, never while a hand
        /// pinches. Nothing here opens by itself on a request.
        /// </summary>
        void Gaze(double nowMs, bool shown, in HandFrame left, in HandFrame right, Vector3 head, Vector3 headForward, bool holding)
        {
            var gaze = headForward.sqrMagnitude > 1e-6f;
            var open = shown && _view.Open && !_mode.Collapsed;
            var windowAngle = gaze ? HeadAngle.Degrees(Ui.ToVec(head), Ui.ToVec(headForward), Ui.ToVec(_view.Background.transform.position)) : 0f;
            var draft = _selected != null && !string.IsNullOrWhiteSpace(_reply.Logic.DraftOf(_selected));
            // a block waiting for its decision keeps the window open too (U6 review 1, Minor 1 ruling)
            var keepOpen = draft || _reply.Typing || holding || BlockShown || Touches(left) || Touches(right);
            if (_collapseTimer.Step(nowMs, open && AutoCollapse && gaze, windowAngle, keepOpen))
            {
                Debug.Log("[Lotse] the head was away for 20 s: collapsing");
                Collapse();
            }
        }

        /// <summary>A hand pinches, or is half way into a pinch: no gaze opens the rail meanwhile.</summary>
        static bool Pinching(HandSample s) => s.Usable && s.PinchStrength >= GestureMachine.PinchOff;

        /// <summary>A ray on the window, or a fingertip within 5 cm in front of it.</summary>
        bool Touches(in HandFrame f)
        {
            if (!f.Sample.Usable) return false;
            var rect = _view.Rect;
            if (f.RayValid && RayPick.HitsRect(Ui.ToVec(f.RayOrigin), Ui.ToVec(f.RayDirection), rect, 0f, Layout.RayMaxMeters, out _)) return true;
            if (!f.TipValid) return false;
            var rel = Ui.ToVec(f.Tip) - rect.Center;
            var normal = Vec3.Cross(rect.Right, rect.Up);
            return System.Math.Abs(Vec3.Dot(rel, normal)) <= 0.05f && System.Math.Abs(Vec3.Dot(rel, rect.Right)) <= rect.HalfWidth &&
                   System.Math.Abs(Vec3.Dot(rel, rect.Up)) <= rect.HalfHeight;
        }

        /// <summary>The rows: this page's agents as taxi lines, which of them is open, the newest incident.</summary>
        void DrawRows(double nowMs)
        {
            _overviewDirty = false;
            _overviewUrgent = false;
            _rowsDirty = false;
            _overviewAt = nowMs;
            _rowsAt = nowMs;
            var sids = _order.Sids;
            _pages.Update(sids.Count);
            _rows.Clear();
            for (var i = _pages.First; i < _pages.First + _pages.OnPage; i++)
            {
                var a = AgentOf(sids[i]);
                if (a != null) _rows.Add(RowFor(a));
            }
            _view.Rows.Show(_rows, OpenSid);
            _view.Rows.SetIncident(_newestIncident.HasValue && nowMs - _newestIncident.Value.at < IncidentLineMs ? _newestIncident.Value.line : null);
        }

        /// <summary>What `a`'s line shows: where its dot is, the step above it, its status at the right.</summary>
        TaxiRowData RowFor(AgentState a)
        {
            var plan = a.Plan;
            var landed = a.Status == "done";
            var state = landed ? TaxiState.Landed : _session.Halted ? TaxiState.Stopped : Holds(a) ? TaxiState.Holding
                : a.Status == "failed" ? TaxiState.Idle : TaxiState.Moving;
            var d = new TaxiRowData
            {
                Sid = a.Sid, Title = Texts.AgentTitle(a), Vendor = Texts.SourceLine(a), VendorColor = Look.Vendor(a.Source), State = state,
            };
            if (plan != null && plan.Steps.Count > 0)
            {
                var total = PlanLines.Total(plan);
                var current = PlanLines.Current(plan);
                var at = landed || current < 0 ? plan.Steps.Count - 1 : current;
                // a mark per step; a very long plan is drawn with 16 marks, the dot at its share of the way
                d.Steps = Mathf.Min(total, 16);
                d.At = total <= 16 ? at : Mathf.RoundToInt(at * 15f / Mathf.Max(1, total - 1));
                d.Now = landed ? OverviewText.AllDone(total) : plan.Steps[at].Text;
                d.Status = OverviewText.StepOf(landed || current < 0 ? total : at + 1, total);
            }
            else
            {
                var trail = _trail.Lines(a.Sid);
                d.Now = landed ? OverviewText.LandedNoPlan : !string.IsNullOrWhiteSpace(a.LastAction) ? a.LastAction
                    : trail != null && trail.Count > 0 ? trail[trail.Count - 1] : "";
                d.Status = OverviewText.NoPlan;
            }
            if (state == TaxiState.Stopped) d.Status = OverviewText.Stopped;
            else if (state == TaxiState.Holding) d.Status = OverviewText.Holding;
            else if (state == TaxiState.Landed) d.Status = OverviewText.Landed;
            else if (state == TaxiState.Idle) d.Status = OverviewText.Failed;
            return d;
        }

        /// <summary>The rail: one sign per agent in the list's order.</summary>
        void DrawRail(double nowMs)
        {
            _railDirty = false;
            _railAt = nowMs;
            _railSlots.Clear();
            foreach (var sid in _order.Sids)
            {
                var a = AgentOf(sid);
                if (a == null) continue;
                var sign = a.Status == "done" ? RailSign.Landed : _session.Halted || Holds(a) ? RailSign.Lights : RailSign.Dot;
                _railSlots.Add(new RailSlot(sid, sign));
            }
            _view.Rail.Show(_railSlots);
        }

        PermRequest OldestRequest(string sid)
        {
            foreach (var p in _session.Store.Pending)
                if (p.Sid == sid) return p;
            return null;
        }

        void DrawConversation(double nowMs, in HandFrame left, in HandFrame right)
        {
            var conv = _view.Conversation;
            var agent = AgentOf(_selected);
            if (agent == null) return;
            var asks = _session.HasRequest(agent.Sid);
            conv.SetAgent(agent);
            conv.SetIncident(_incidents.TryGetValue(agent.Sid, out var inc) && nowMs - inc.at < IncidentLineMs ? inc.line : null);
            var card = _session.Card != null && _session.Card.Sid == agent.Sid ? _session.Card : null;
            // the plan at the right: held at its step by a request (a red light), or working there (a white dot)
            conv.SetPlan(agent.Plan, asks);

            // the block: built once per shown request (and again only when what it shows changes); a closing note stands
            // in its place only while no request of this agent is shown
            var note = NoteFor(agent.Sid, nowMs);
            // (U14, review of U13) the green "Cleared" stands its second also when the agent already asks again: the next
            // request takes the block only then (the app lets nothing be approved meanwhile: ClearedShowing)
            var cleared = _clearedSid == agent.Sid && conv.Block.ClearedShown;
            var key = cleared ? _shownBlock : card != null ? BlockKey(card) : note != null ? "note|" + note.Text : "";
            if (key != _shownBlock || _blockDirty)
            {
                _blockDirty = false;
                if (key != _shownBlock)
                {
                    _shownBlock = key;
                    if (card != null)
                    {
                        _clearedSid = null;
                        var cut = conv.Block.Show(card, _session.CardExplain ?? new Explanation(), _session.Mode, _session.MoreRequests, StripRepo,
                            _session.CardFacts, Texts.DenyOnly(agent.Source));
                        // what the block had to cut needs both hands; the session marks it, and the next frame draws it again
                        if (cut && _session.SubjectCut(card.Id)) Debug.Log($"[Lotse] block {card.Id}: subject cut for display, two hands needed");
                    }
                    else if (note != null) conv.Block.ShowNote(note.Text, note.Color);
                    else conv.Block.Hide();
                    BlockBuilds++;
                }
                conv.Layout();
            }
            if (_session.Card != null && _session.Mode == CardMode.Hold && !cleared)
            {
                conv.Block.SetProgress(_session.HoldProgress);
                conv.Block.SetCue(_session.HoldCue);
            }
            conv.Block.SetTimeLeft(_session.Card != null && !cleared ? _session.CardTimeLeft : null);
            // the header menu stands over the block (U8): dimmed and not answerable until it closes
            conv.Block.SetCovered(card != null && !cleared && _view.Asking, _view.Question.IsShown ? Texts.AnswerQuestionFirst : Texts.CloseMenuToAnswer);

            if (_session.Card == null && !conv.RequestShown) Drag(left, right);
            else EndDrag();
            DrawFeed(agent.Sid);
        }

        void DrawFeed(string sid)
        {
            var entries = _feeds.Entries(sid);
            _scroll.Update(entries.Count);
            var version = _feeds.VersionOf(sid);
            var feed = _view.Conversation.Feed;
            if (version == _shownFeedVersion && _scroll.Bottom == _shownBottom && _scroll.FollowsEnd == _shownFollows &&
                Mathf.Approximately(feed.Height, _shownFeedHeight) && feed.ScrollButtons == !BlockShown) return;
            if (version != _shownFeedVersion) _rowsDirty = true; // its newest kind may change its orb and status line
            feed.ScrollButtons = !BlockShown;
            _shownFeedVersion = version;
            _shownBottom = _scroll.Bottom;
            _shownFollows = _scroll.FollowsEnd;
            _shownFeedHeight = feed.Height;
            feed.Show(entries, _feeds.StateOf(sid), _scroll);
        }

        /// <summary>How often the block was built (a shown request is built once, not per frame).</summary>
        public int BlockBuilds { get; private set; }

        /// <summary>What the block shows: rebuilt only when one of these changes.</summary>
        string BlockKey(PermRequest card)
        {
            // every displayed or gesture-relevant field of the request (RequestContent), the mode, the count of more
            // requests and the explanation: only to avoid rebuilding per frame; any change draws the block again
            var e = _session.CardExplain;
            // (U14) and the room the block may take: with less of it the block is cut to fewer lines, so it is built again
            return _view.Conversation.Block.MaxHeight.ToString("0.0000") + "#" + _view.Conversation.Block.MinTop.ToString("0.0000") + "#" + RequestContent.Key(card) + "#" + _session.Mode + "#" + _session.MoreRequests + "#" +
                   (e != null ? e.Text.Length + ":" + e.Text + "|" + e.AiText + "|" + e.Source : "") + "#" + _session.CardFacts?.Key + "#" + Texts.DenyOnly(AgentOf(card.Sid)?.Source);
        }

        /// <summary>The offline demo strips "&lt;repo&gt;/" from subjects; live, the bridge's text is shown as it is.</summary>
        public bool StripRepo { get; set; }

        void ToggleSummary()
        {
            _summaryOpen = !_summaryOpen;
        }

        Note NoteFor(string sid, double nowMs)
        {
            if (!_notes.TryGetValue(sid, out var note)) return null;
            if (note.UntilFeed && _feed.Sid == sid && _feeds.StateOf(sid) == FeedState.Ready)
            {
                var v = _feeds.VersionOf(sid);
                if (note.FeedVersion == int.MinValue) note.FeedVersion = v; // the first look at its feed is the baseline
                else if (v != note.FeedVersion) note.Until = 0; // the next feed entry came
            }
            if (nowMs < note.Until) return note;
            _notes.Remove(sid);
            return null;
        }

        /// <summary>A pinch held on the feed or the list (at it, or its ray on it) drags it; the content follows the hand.</summary>
        void Drag(in HandFrame left, in HandFrame right)
        {
            var feedRect = _view.Conversation.Feed.Rect;
            if (_dragHand == 0)
            {
                for (var h = 1; h <= 2; h++)
                {
                    var f = h == 1 ? left : right;
                    if (!f.Sample.Usable || f.Sample.PinchStrength < DragPinch) continue;
                    if (Point(f, feedRect, out var p))
                    {
                        _dragHand = h;
                        _dragY = _view.Conversation.Feed.LocalY(p);
                        return;
                    }
                }
                return;
            }
            var frame = _dragHand == 1 ? left : right;
            if (!frame.Sample.Usable || frame.Sample.PinchStrength < DragPinch || !Point(frame, feedRect, out var at))
            {
                EndDrag();
                return;
            }
            var y = _view.Conversation.Feed.LocalY(at);
            // the content follows the hand: a hand moving down (dy < 0) brings older entries in
            _scroll.Drag(y - _dragY, FeedListView.EntryStep);
            _dragY = y;
        }

        void EndDrag()
        {
            if (_dragHand == 0) return;
            _dragHand = 0;
            _scroll.EndDrag();
            _list.EndDrag();
        }

        /// <summary>Where a hand works on a rect: its pinch at it (within 4 cm), else where its ray meets it.</summary>
        static bool Point(in HandFrame f, in PokeRect rect, out Vector3 point)
        {
            point = default;
            if (f.PinchValid)
            {
                var rel = Ui.ToVec(f.PinchPoint) - rect.Center;
                var normal = Vec3.Cross(rect.Right, rect.Up);
                if (System.Math.Abs(Vec3.Dot(rel, normal)) <= DragNear && System.Math.Abs(Vec3.Dot(rel, rect.Right)) <= rect.HalfWidth &&
                    System.Math.Abs(Vec3.Dot(rel, rect.Up)) <= rect.HalfHeight)
                {
                    point = f.PinchPoint;
                    return true;
                }
            }
            if (f.RayValid && RayPick.HitsRect(Ui.ToVec(f.RayOrigin), Ui.ToVec(f.RayDirection), rect, 0f, Layout.RayMaxMeters, out var t))
            {
                point = f.RayOrigin + f.RayDirection.normalized * t;
                return true;
            }
            return false;
        }

        /// <summary>The feed's scroll requests (its ▲/▼ and "▼ n neue").</summary>
        public void OnScroll(int entries) => _scroll.Scroll(entries);

        public void OnScrollToEnd() => _scroll.ScrollToEnd();

        string LastFeedKind(string sid)
        {
            if (_feed.Sid != sid) return null;
            var e = _feeds.Entries(sid);
            return e.Count > 0 ? e[e.Count - 1].Kind : null;
        }

        int PaletteOf(string sid) => _palette.TryGetValue(sid, out var i) ? i : 0;

        AgentState AgentOf(string sid)
        {
            if (sid == null || _session == null) return null;
            var agents = _session.Store.Agents;
            for (var i = 0; i < agents.Count; i++)
                if (agents[i].Sid == sid) return agents[i];
            return null;
        }

        void OnAgent(AgentState agent, string previous)
        {
            _rowsDirty = true;
            _overviewDirty = true;
            _railDirty = true;
            _headerDirty = true;
            _trail.Note(agent.Sid, agent.LastAction);
        }

        void OnCardChanged()
        {
            _blockDirty = true;
            _rowsDirty = true;
            _overviewDirty = true;
            _railDirty = true;
            _overviewUrgent = true; // the waiting state may have changed
            _headerDirty = true;
        }

        /// <summary>A request closed: its conversation says how, in the block's place (allow and deny for 3 s, the rest until the next feed entry).</summary>
        void OnRequestClosed(PermRequest req, string reason, ClientDecision sent)
        {
            var text = Texts.Closed(reason, sent);
            var note = new Note { Text = text };
            var agent = AgentOf(req.Sid);
            // allowed on the open row: the bar turns green and says so for a moment, then the row closes
            if (reason == "allow" && req.Sid == OpenSid && Block.RequestId == req.Id &&
                Block.ShowCleared(Headline.Cleared(agent != null && agent.Source != null ? Vendor(agent) : null)))
            {
                _clearedSid = req.Sid;
                _clearedUntil = _nowMs + ClearedMs;
            }
            // denied from here: the row closes at once (unless the agent asks more)
            else if (reason == "deny" && sent != null && req.Sid == OpenSid && !_session.HasRequest(req.Sid)) CloseRow();
            if (reason == "allow")
            {
                note.Color = Theme.GoodText;
                note.Until = _nowMs + DecidedNoteMs;
            }
            else if (reason == "deny")
            {
                note.Color = Theme.Muted;
                note.Until = _nowMs + DecidedNoteMs;
            }
            else
            {
                note.Color = text == Texts.SentUnclear ? Theme.WarnText : Theme.Muted;
                note.Until = _nowMs + NoteMaxMs;
                note.UntilFeed = true;
            }
            _notes[req.Sid] = note;
            _blockDirty = true;
            _rowsDirty = true;
            _overviewDirty = true;
            _railDirty = true;
            _headerDirty = true;
            _overviewUrgent = true; // the waiting state may have changed
            Debug.Log($"[Lotse] request {req.Id} closed: {reason} (sent {sent?.Behavior ?? "nothing"})");
        }

        void OnDetail(AgentDetailMessage d)
        {
            _details.Put(d);
            Debug.Log("[Lotse] agent detail for " + Texts.LogSid(d.Sid));
        }

        void OnDetailFailed(string sid)
        {
            _details.NoAnswer(sid);
            Debug.Log("[Lotse] agent detail refused for " + Texts.LogSid(sid));
        }

        void OnFeed(string sid, IReadOnlyList<FeedEntry> entries)
        {
            var added = _feed.Arrived(sid, entries); // the text itself is never logged
            if (added > 0) Debug.Log($"[Lotse] feed {Texts.LogSid(sid)}: {added} new");
        }

        void OnFeedFailed(string sid)
        {
            _feed.Failed(sid);
            Debug.Log("[Lotse] feed refused for " + Texts.LogSid(sid));
        }
    }
}
