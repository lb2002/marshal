#nullable enable
using System;
using System.Collections.Generic;
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>Hold: the block waits for its gesture. Sent: the decision went out, the block waits for the bridge's perm_closed.</summary>
    public enum CardMode { None, Hold, Sent }

    /// <summary>
    /// One run: a message source (the offline pack, or the live bridge) feeds the store; the gesture machine decides on
    /// the request the workspace's approval block shows. Pure, time injected through Step, so a whole run can be tested.
    ///
    /// U5a: nothing opens by itself. The block (`Card`) is the selected agent's oldest open request, and only while the
    /// workspace says it is on screen and "Später" has not hidden it (ApprovalFocus). Selecting another agent, "Später",
    /// the request closing, the window going away, a lost connection: the block changes or goes, and the gesture machine
    /// with it, so a running hold is cancelled and a new hold needs a fresh pinch. The only allow path is a
    /// GestureMachine Confirmed for the id of the block that is shown in Hold mode, and the hold must have begun on that
    /// very block. The only deny path is Deny(id) (the block's "Ablehnen" button) for that same block; the palm-forward
    /// stop no longer denies (it still cancels a hold inside the gesture machine).
    ///
    /// The InterruptionScheduler now only decides when a request gets its notice (the pill above the window and the
    /// chime): NoticeRaised, once per request, never while another notice shows, never right after a gesture.
    ///
    /// A sent decision puts its block in Sent mode until the source's perm_closed closes the request (at once for the
    /// pack, later for the bridge); it stays Sent when the user selects another agent and comes back. A lost connection
    /// drops every request without sending anything. Replies never go to an agent with an open request.
    ///
    /// Pinches pass the HoldGate first (head facing the block, hands raised in front). Every incoming message is capped
    /// and its control characters shown (Untrusted) before the store sees it; a request cut that way, or whose subject
    /// the block had to cut, needs both hands. Incidents are only reported (IncidentRaised): they never decide.
    /// </summary>
    public sealed class DemoSession : IFeedSource
    {
        /// <summary>How long a notice (the pill) stands.</summary>
        public const double NoticeMs = 6000;

        readonly IMessageSource _source;
        readonly GestureMachine _gestures = new GestureMachine();
        readonly InterruptionScheduler _scheduler = new InterruptionScheduler();
        readonly HoldGate _gate = new HoldGate();
        readonly ApprovalFocus _focus = new ApprovalFocus();
        readonly List<ClientDecision> _decisions = new List<ClientDecision>();
        // the open requests by id (for RequestClosed), when each was first seen (the countdown), what was sent for each
        readonly Dictionary<string, PermRequest> _open = new Dictionary<string, PermRequest>();
        readonly Dictionary<string, double> _firstSeen = new Dictionary<string, double>();
        readonly Dictionary<string, ClientDecision> _sentFor = new Dictionary<string, ClientDecision>();
        // requests whose subject the block had to cut: they need both hands for good
        readonly HashSet<string> _cut = new HashSet<string>();
        readonly HashSet<string> _noticed = new HashSet<string>();
        readonly List<PermRequest> _unnoticed = new List<PermRequest>();
        readonly List<string> _scratch = new List<string>();
        double _lastGestureMs = double.NegativeInfinity;
        double _noticeUntil = double.NegativeInfinity;
        string? _lastSentId;
        string _closeReason = "";
        bool _viewShown;

        public DemoSession(DemoPack pack) : this(new PackSource(pack))
        {
        }

        public DemoSession(IMessageSource source)
        {
            _source = source;
            Store.Changed += OnStoreChanged;
        }

        public AgentStore Store { get; } = new AgentStore();
        public Stats Stats { get; } = new Stats();

        /// <summary>Session clock: the sum of Step deltas (the gesture machine and scheduler run on it).</summary>
        public double NowMs { get; private set; }

        /// <summary>The request the approval block shows (the only one a gesture can approve), or null.</summary>
        public PermRequest? Card { get; private set; }
        public CardMode Mode { get; private set; } = CardMode.None;
        public Explanation? CardExplain => Card == null ? null : Store.ExplainFor(Card.Id);
        /// <summary>
        /// The facts row of the shown block (U7): only for a request that needs both hands; null otherwise. Display only:
        /// it never changes the risk or the gesture. Its arriving or changing cancels a running hold (Refocus).
        /// </summary>
        public ImpactFacts? CardFacts => Card != null && Card.NeedsTwoHand ? ImpactFacts.For(Store.ImpactFor(Card.Id), NowMs - _cardShownAt) : (ImpactFacts?)null;
        double _cardShownAt;
        string? _factsKey;
        /// <summary>The selected agent's other open requests ("+2 weitere").</summary>
        public int MoreRequests { get; private set; }

        /// <summary>The agent whose conversation the workspace shows, or null.</summary>
        public string? Selected => _focus.Selected;

        /// <summary>
        /// Every agent is halted, as the bridge's last halt_state (or snapshot) says; never from a local guess. While
        /// halted there is no block (the bridge denies every request) and no notice.
        /// </summary>
        public bool Halted { get; private set; }
        /// <summary>When this session saw the halt begin (its own clock, for "Halted for 12 s").</summary>
        public double HaltedAtMs { get; private set; }
        /// <summary>Per source with sessions, what the bridge enforces while halted ("every-step", "requests-only", "none").</summary>
        public IReadOnlyDictionary<string, string> Enforcement => _enforcement;
        readonly Dictionary<string, string> _enforcement = new Dictionary<string, string>();

        /// <summary>The bridge's halt state changed (on or off, or what it enforces).</summary>
        public event Action? HaltChanged;
        /// <summary>"Später" hid the block until the next selection.</summary>
        public bool PutOff => _focus.PutOff;
        /// <summary>The workspace says the conversation (and so the block's place) is on screen.</summary>
        public bool ViewShown => _viewShown;

        /// <summary>0..1 hold progress for the block's ring.</summary>
        public float HoldProgress => Mode == CardMode.Hold ? _gestures.Progress : 0f;
        public GestureState GestureState => _gestures.State;
        /// <summary>Why the gate holds a pinch back, while the block waits in Hold mode and no hold runs; else None.</summary>
        public HoldCue HoldCue => Mode == CardMode.Hold && HoldProgress <= 0f ? _gate.Cue : HoldCue.None;

        /// <summary>The share of the block's time left until the bridge gives up (1..0); null without a block or a span.</summary>
        public double? CardTimeLeft => Card == null ? null : Countdown.Fraction(Card, FirstSeen(Card.Id), NowMs);

        /// <summary>The decisions in order, as sent to the source (the bridge in live mode).</summary>
        public IReadOnlyList<ClientDecision> Decisions => _decisions;

        /// <summary>The source is done (the pack is played out; never for live) and nothing is pending.</summary>
        public bool Finished => _source.Finished && Store.Pending.Count == 0;

        /// <summary>An agent message was applied; the string is its previous status (null for a new agent).</summary>
        public event Action<AgentState, string?>? AgentUpdated;
        /// <summary>An agent finished with a summary (for the log).</summary>
        public event Action<string>? ReviewLine;
        /// <summary>The block opened, closed, changed mode, got a new explanation or a different "+n weitere".</summary>
        public event Action? CardChanged;
        /// <summary>A decision was sent (the pack has closed the request by then; the bridge closes it later).</summary>
        public event Action<ClientDecision>? Decided;
        /// <summary>A gesture or decision was dropped (wrong id, block not in Hold mode, not sent); for the log.</summary>
        public event Action<string>? DecisionDropped;
        /// <summary>The bridge connection is gone: every request is dropped without sending anything.</summary>
        public event Action? ConnectionLost;
        /// <summary>A request wants the user: show its notice (the pill) and chime. Display only.</summary>
        public event Action<PermRequest>? NoticeRaised;
        /// <summary>An agent's detail arrived (capped): for the summary line. Display only, it never decides anything.</summary>
        public event Action<AgentDetailMessage>? AgentDetailArrived;
        /// <summary>Feed entries of an agent arrived (snapshot or append), already capped. Display only (U4b).</summary>
        public event Action<string, IReadOnlyList<FeedEntry>>? FeedArrived;
        /// <summary>An agent's feed cannot be had (unknown agent, an older bridge). Display only.</summary>
        public event Action<string>? FeedFailed;
        /// <summary>The bridge refused an agent's detail request: "Keine Details". Display only.</summary>
        public event Action<string>? AgentDetailFailed;

        /// <summary>The bridge refused a reply (sid, its error code): display only.</summary>
        public event Action<string, string>? ReplyFailed;

        /// <summary>The bridge (or the pack) acknowledged a reply: whether the waiting agent got it.</summary>
        public event Action<string, bool>? ReplyAcked;

        /// <summary>The bridge answered with an error (e.g. it refused a decision).</summary>
        public event Action<string>? BridgeError;
        /// <summary>
        /// The shown block's request closed: the request, the reason (the bridge's or the pack's perm_closed reason;
        /// "gone" when a snapshot no longer has it) and the decision sent for it, if any. Raised before CardChanged.
        /// </summary>
        public event Action<PermRequest, string, ClientDecision?>? CardClosed;
        /// <summary>Any open request closed (shown or not), with its reason and what was sent for it: for its conversation.</summary>
        public event Action<PermRequest, string, ClientDecision?>? RequestClosed;

        public string AgentName(string sid)
        {
            foreach (var a in Store.Agents)
                if (a.Sid == sid) return a.Name;
            return sid;
        }

        /// <summary>
        /// An incident arrived (collision, budget): for the conversation line and the notice. It never decides anything.
        /// </summary>
        public event Action<Incident>? IncidentRaised;

        /// <summary>
        /// The block could not show the request's subject in full (the view had to cut it). Like a request the bridge
        /// cut, it then needs the two-hand hold, for good: what the user cannot see is never approved with one hand.
        /// Only for the block shown in Hold mode, once; true when the block has to be drawn again.
        /// </summary>
        public bool SubjectCut(string id)
        {
            if (Card == null || Card.Id != id || Mode != CardMode.Hold || Card.Truncated) return false;
            var wasTwoHand = Card.NeedsTwoHand;
            Card.Truncated = true;
            _cut.Add(id);
            if (!wasTwoHand) _gestures.OpenCard(Card); // the pinch-hold no longer counts: a fresh two-hand hold
            CardChanged?.Invoke();
            return true;
        }

        /// <summary>
        /// One frame: play the source, show the selected agent's block, run the gestures on it, maybe raise a notice.
        /// The pinches only reach the gesture machine through the HoldGate: the head has to face the block and the
        /// hands have to be raised.
        /// </summary>
        public void Step(double dtMs, HandSample left, HandSample right, in HoldPose pose)
        {
            _gate.Filter(pose, ref left, ref right);
            NowMs += dtMs;
            var msgs = _source.Poll(dtMs);
            for (var i = 0; i < msgs.Count; i++) Apply(msgs[i]);
            Refocus();

            HandleGesture(_gestures.Update(NowMs, left, right));
            Refocus();
            Notice();
        }

        /// <summary>Test seam (internal): a frame without the HoldGate, as if every pinch were meant.</summary>
        internal void Step(double dtMs, HandSample left, HandSample right) => Step(dtMs, left, right, HoldPose.Open);

        /// <summary>The user selected an agent (a row, the pill, the first one at the start), or none: its block, if any, shows.</summary>
        public void Select(string? sid)
        {
            _focus.Select(sid);
            Refocus();
        }

        /// <summary>
        /// Resets the gesture on the shown block as a block change does (U8: the header menu opened or closed over it): a
        /// running hold is cancelled, and a pinch held across it never counts; both hands must be seen open first.
        /// </summary>
        public void Rearm()
        {
            if (Card != null && Mode == CardMode.Hold) _gestures.OpenCard(Card);
        }

        /// <summary>"Später": the block goes until the agent is selected again. Sends nothing.</summary>
        public void Later()
        {
            if (Card == null) return;
            _focus.Later();
            Refocus();
        }

        /// <summary>Whether the conversation is on screen (the workspace is shown and nothing stands in front of it).</summary>
        public void SetViewShown(bool shown)
        {
            if (shown == _viewShown) return;
            _viewShown = shown;
            Refocus();
        }

        /// <summary>
        /// "Ablehnen" on the block: a deny for exactly the request the block shows in Hold mode. False (nothing sent)
        /// for any other id, a hidden block or one already sent.
        /// </summary>
        public bool Deny(string id)
        {
            if (Card == null || Mode != CardMode.Hold || Card.Id != id)
            {
                DecisionDropped?.Invoke($"deny for {id} dropped: the block shows {Card?.Id ?? "nothing"} in mode {Mode}");
                return false;
            }
            _lastGestureMs = NowMs;
            Decide(Card, ClientDecision.Deny(Card.Id));
            Refocus();
            return true;
        }

        /// <summary>Whether `sid` has an open request.</summary>
        public bool HasRequest(string sid)
        {
            var pending = Store.Pending;
            for (var i = 0; i < pending.Count; i++)
                if (pending[i].Sid == sid) return true;
            return false;
        }

        /// <summary>
        /// Asks the source for an agent's detail (the bridge live, the pack's `agents` offline). False when it cannot
        /// be asked; an answer comes through AgentDetailArrived (at once from the pack).
        /// </summary>
        public bool RequestDetail(string sid)
        {
            if (!_source.RequestDetail(sid, out var answer)) return false;
            if (answer != null) Apply(answer);
            return true;
        }

        /// <summary>
        /// Starts one agent's live feed. False when it cannot (no live link, an older bridge, no pack entry); the entries
        /// come through FeedArrived (at once from the pack). Display only: nothing in a feed touches a request.
        /// </summary>
        public bool SubscribeFeed(string sid)
        {
            if (!_source.SubscribeFeed(sid, out var answer)) return false;
            if (answer != null) Apply(answer);
            return true;
        }

        public void UnsubscribeFeed(string sid) => _source.UnsubscribeFeed(sid);

        /// <summary>
        /// Asks the source to halt every agent. False when it cannot be sent (no live link: nothing is halted). The
        /// state shown changes only with the answer (halt_state), never here. Halt can only make things stricter.
        /// </summary>
        public bool RequestHalt() => SendHalt(true);

        /// <summary>Asks the source to release the halt (after the user's "Continue" and its confirmation). False when not sent.</summary>
        public bool RequestRelease() => SendHalt(false);

        bool SendHalt(bool on)
        {
            var msgs = _source.SendHalt(on);
            if (msgs == null) return false;
            for (var i = 0; i < msgs.Count; i++) Apply(msgs[i]); // the pack answers at once; the bridge through Poll
            return true;
        }

        void SetHalt(HaltStateMessage? h)
        {
            var on = h != null && h.On;
            var same = on == Halted && SameEnforcement(h);
            if (on && !Halted) HaltedAtMs = NowMs;
            Halted = on;
            _enforcement.Clear();
            if (h != null) foreach (var kv in h.Enforcement) _enforcement[kv.Key] = kv.Value;
            Refocus(); // halted: the block goes, and a running hold with it
            if (!same) HaltChanged?.Invoke();
        }

        bool SameEnforcement(HaltStateMessage? h)
        {
            var count = h?.Enforcement.Count ?? 0;
            if (count != _enforcement.Count) return false;
            if (h == null) return true;
            foreach (var kv in h.Enforcement)
                if (!_enforcement.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
            return true;
        }

        /// <summary>
        /// Sends the user's own reply to an agent. Refused while that agent has an open request (the request comes first:
        /// text typed into a permission prompt could answer it); it never approves, denies or touches a request. False
        /// when it could not be sent.
        /// </summary>
        public bool SendReply(string sid, string text)
        {
            if (HasRequest(sid)) return false;
            var msgs = _source.SendReply(sid, text);
            if (msgs == null) return false;
            for (var i = 0; i < msgs.Count; i++) Apply(msgs[i]); // the pack's ack and the agent's reaction
            return true;
        }

        /// <summary>What Step does with the gesture machine's event; a test seam for events it cannot produce.</summary>
        internal void HandleGesture(GestureEvent ev)
        {
            if (ev.Kind == GestureEventKind.Confirmed) OnConfirmed(ev);
            // a palm stop only cancels the hold (inside the gesture machine): it never denies (U5a)
            else if (ev.Kind == GestureEventKind.Stop) _lastGestureMs = NowMs;
        }

        void OnConfirmed(GestureEvent ev)
        {
            _lastGestureMs = NowMs;
            // only the block that is shown, in Hold mode, with exactly this id
            if (Card == null || Mode != CardMode.Hold || ev.Confirm == null)
            {
                DecisionDropped?.Invoke($"confirm for {ev.Id} dropped: block {Card?.Id ?? "none"} is in mode {Mode}");
                return;
            }
            if (ev.Id != Card.Id)
            {
                DecisionDropped?.Invoke($"confirm for {ev.Id} dropped: the block shown is {Card.Id}");
                return;
            }
            // the gesture must be the one the shown block's risk asks for (fail closed; the bridge checks too)
            var needed = Card.NeedsTwoHand ? "two-hand" : "pinch-hold";
            if (ev.Confirm != needed)
            {
                DecisionDropped?.Invoke($"confirm {ev.Confirm} for {ev.Id} dropped: the block needs {needed}");
                if (Mode == CardMode.Hold) _gestures.OpenCard(Card);
                return;
            }
            Decide(Card, ClientDecision.Allow(Card.Id, ev.Confirm));
        }

        void Decide(PermRequest req, ClientDecision decision)
        {
            var allow = decision.Behavior == "allow";
            var msgs = _source.Send(decision);
            if (msgs == null)
            {
                // not the source's pending request, or no connection: nothing happens; a fresh hold can try again
                DecisionDropped?.Invoke($"{decision.Behavior} for {decision.Id} not sent");
                if (Card == req && Mode == CardMode.Hold) _gestures.OpenCard(req);
                return;
            }
            Stats.RecordDecision(req, allow);
            _decisions.Add(decision);
            _sentFor[req.Id] = decision;
            _lastSentId = req.Id;
            if (Card == req) Mode = CardMode.Sent;
            _gestures.Reset(); // one decision per request
            foreach (var m in msgs) Apply(m); // the pack's perm_closed closes the request at once
            if (Card == req && Mode == CardMode.Sent) CardChanged?.Invoke(); // the bridge's comes later
            Decided?.Invoke(decision);
        }

        /// <summary>The block follows the selection, "Später", the window and the open requests.</summary>
        void Refocus()
        {
            // halted: no block at all (the bridge denies every request meanwhile)
            var next = _focus.Pick(Store.Pending, _viewShown && !Halted, out var more);
            var changed = more != MoreRequests;
            var moreChanged = changed;
            MoreRequests = more;
            if (next != null && _cut.Contains(next.Id)) next.Truncated = true; // a fresh copy (snapshot) of a cut request
            var newBlock = next?.Id != Card?.Id;
            if (newBlock)
            {
                Card = next;
                _cardShownAt = NowMs;
                if (next == null)
                {
                    Mode = CardMode.None;
                    _gestures.Reset(); // no block: nothing can be approved
                }
                else if (_sentFor.ContainsKey(next.Id))
                {
                    Mode = CardMode.Sent;
                    _gestures.Reset();
                }
                else
                {
                    Mode = CardMode.Hold;
                    _gestures.OpenCard(next); // a running hold is cancelled; both hands must be seen open first
                }
                changed = true;
            }
            else if (next != null && !ReferenceEquals(next, Card))
            {
                // the same request, a fresh copy (a re-sent perm_open, a snapshot): if anything it shows or that decides its
                // gesture changed (tool, risk, paths, truncated, ...: RequestContent), it is a new block, exactly as a new
                // request: drawn again, and a running hold is cancelled
                var differs = !RequestContent.Same(next, Card);
                Card = next;
                if (differs)
                {
                    if (Mode == CardMode.Hold) _gestures.OpenCard(next); // the gesture follows the new risk
                    changed = true;
                }
            }
            // the facts row arriving or changing (an impact, or "Checking effects" giving way) is a visible change of the
            // shown request: a running hold is cancelled, like every other change
            // (U14, review of U13) so is "+n more" appearing, changing or going on the shown request: the block is drawn again
            if (!newBlock && Card != null && moreChanged && Mode == CardMode.Hold) _gestures.OpenCard(Card);
            var facts = CardFacts?.Key;
            if (!newBlock && Card != null && facts != _factsKey)
            {
                if (Mode == CardMode.Hold) _gestures.OpenCard(Card);
                changed = true;
            }
            _factsKey = facts;
            if (changed) CardChanged?.Invoke();
        }

        /// <summary>The scheduler picks the next request that has had no notice yet; a block already on screen needs none.</summary>
        void Notice()
        {
            if (Halted) return; // no notice while halted
            _unnoticed.Clear();
            var pending = Store.Pending;
            for (var i = 0; i < pending.Count; i++)
            {
                var p = pending[i];
                if (_noticed.Contains(p.Id)) continue;
                if (Card != null && Card.Id == p.Id)
                {
                    _noticed.Add(p.Id);
                    continue;
                }
                _unnoticed.Add(p);
            }
            var pick = _scheduler.Pick(NowMs, NowMs < _noticeUntil, _lastGestureMs, _unnoticed);
            if (pick == null) return;
            _noticed.Add(pick.Id);
            _noticeUntil = NowMs + NoticeMs;
            NoticeRaised?.Invoke(pick);
        }

        /// <summary>
        /// The notice just raised was not shown (U10: the window already says it, in the overview or in the shown agent's
        /// conversation): the next request's notice does not wait for it.
        /// </summary>
        public void NoticeNotShown() => _noticeUntil = NowMs;

        double FirstSeen(string id) => _firstSeen.TryGetValue(id, out var t) ? t : NowMs;

        void Apply(ServerMessage msg)
        {
            Untrusted.Message(msg); // every field capped and its control characters shown, before anything sees it
            if (msg is FeedSnapshotMessage fs)
            {
                FeedArrived?.Invoke(fs.Sid, fs.Entries); // display only: no store, no request, no decision
                return;
            }
            if (msg is FeedAppendMessage fa)
            {
                FeedArrived?.Invoke(fa.Sid, fa.Entries);
                return;
            }
            if (msg is FeedFailedMessage ff)
            {
                FeedFailed?.Invoke(ff.Sid);
                return;
            }
            if (msg is AgentDetailFailedMessage failed)
            {
                AgentDetailFailed?.Invoke(failed.Sid); // display only
                return;
            }
            if (msg is AgentReplyFailedMessage replyFailed)
            {
                ReplyFailed?.Invoke(replyFailed.Sid, replyFailed.Error); // display only
                return;
            }
            if (msg is AgentReplyAckMessage ack)
            {
                ReplyAcked?.Invoke(ack.Sid, ack.Delivered); // display only: no store, no request, no decision
                return;
            }
            if (msg is AgentDetailMessage detail)
            {
                AgentDetailArrived?.Invoke(detail); // display only: no store, no request, no decision
                return;
            }
            if (msg is IncidentMessage incident)
            {
                Store.Apply(msg);
                IncidentRaised?.Invoke(incident.Incident);
                return;
            }
            if (msg is HaltStateMessage halt)
            {
                SetHalt(halt);
                return;
            }
            if (msg is ConnectionLostMessage)
            {
                OnConnectionLost();
                return;
            }
            if (msg is ErrorMessage err)
            {
                OnBridgeError(err.Error);
                return;
            }
            if (msg is SnapshotMessage snap)
            {
                // the bridge's snapshot (live, on every (re)connect) carries agents too: show them like agent messages
                var before = new Dictionary<string, string>();
                foreach (var a in Store.Agents) before[a.Sid] = a.Status;
                Store.Apply(msg);
                foreach (var a in snap.Agents) AgentUpdated?.Invoke(a, before.TryGetValue(a.Sid, out var p) ? p : null);
                // a snapshot is the bridge's whole state: without `halt` it is not halted (an older bridge cannot halt)
                SetHalt(snap.Halt);
                return;
            }
            if (msg is PermClosedMessage closed) _closeReason = closed.Reason; // for the close events
            if (!(msg is AgentMessage am))
            {
                Store.Apply(msg);
                return;
            }

            string? prev = null;
            foreach (var a in Store.Agents)
                if (a.Sid == am.Agent.Sid) prev = a.Status;
            Store.Apply(msg);
            AgentUpdated?.Invoke(am.Agent, prev);
            if (am.Agent.Status == "done" && prev != "done" && !string.IsNullOrEmpty(am.Agent.LastAction))
                ReviewLine?.Invoke(Texts.Review(am.Agent.Name, am.Agent.LastAction));
        }

        void OnStoreChanged(StoreChange change)
        {
            switch (change.Kind)
            {
                case StoreChangeKind.PermOpened:
                    Track();
                    break;
                case StoreChangeKind.PermClosed:
                    Closed(change.Id, _closeReason);
                    break;
                case StoreChangeKind.Snapshot:
                    // what the snapshot no longer has is gone; what it brings is new
                    _scratch.Clear();
                    foreach (var id in _open.Keys)
                        if (!IsPending(id)) _scratch.Add(id);
                    foreach (var id in _scratch) Closed(id, "gone");
                    Track();
                    break;
                case StoreChangeKind.PermExplain when Card != null && change.Id == Card.Id:
                    // the block's headline and detail change: every visible change cancels a hold (U5a review 2)
                    if (Mode == CardMode.Hold) _gestures.OpenCard(Card);
                    CardChanged?.Invoke();
                    break;
            }
        }

        /// <summary>Remembers every open request and when it was first seen (the countdown runs from there).</summary>
        void Track()
        {
            var pending = Store.Pending;
            for (var i = 0; i < pending.Count; i++)
            {
                var p = pending[i];
                _open[p.Id] = p;
                if (!_firstSeen.ContainsKey(p.Id)) _firstSeen[p.Id] = NowMs;
            }
        }

        /// <summary>A request closed: its conversation hears why; the block, if it showed it, closes.</summary>
        void Closed(string id, string reason)
        {
            if (!_open.TryGetValue(id, out var req)) return;
            _sentFor.TryGetValue(id, out var sent);
            _open.Remove(id);
            _firstSeen.Remove(id);
            _sentFor.Remove(id);
            _noticed.Remove(id);
            _cut.Remove(id);
            RequestClosed?.Invoke(req, reason, sent);
            if (Card != null && Card.Id == id) CloseCard(reason, sent);
        }

        /// <summary>
        /// The link is gone. If the bridge noticed, it has answered every pending request with "no decision"
        /// and the terminal asks by itself; if it did not (the socket was only replaced, or it is still
        /// waiting for its heartbeat), the requests stay open there and come back with the snapshot after
        /// the reconnect. Either way drop them all here for now; nothing is sent.
        /// </summary>
        void OnConnectionLost()
        {
            ConnectionLost?.Invoke();
            var ids = new List<string>();
            foreach (var p in Store.Pending) ids.Add(p.Id);
            foreach (var id in ids) Apply(new PermClosedMessage { Id = id, Reason = "client-disconnected" });
            if (Card != null) CloseCard("client-disconnected", null);
        }

        /// <summary>The bridge refused the last decision (it carries no id): that request goes back to waiting for one.</summary>
        void OnBridgeError(string error)
        {
            BridgeError?.Invoke(error);
            if (_lastSentId == null || !_sentFor.Remove(_lastSentId)) return;
            var id = _lastSentId;
            _lastSentId = null;
            if (Card == null || Card.Id != id || Mode != CardMode.Sent) return;
            Mode = CardMode.Hold;
            _gestures.OpenCard(Card); // a fresh hold is needed: rising edge
            CardChanged?.Invoke();
        }

        bool IsPending(string id)
        {
            foreach (var p in Store.Pending)
                if (p.Id == id) return true;
            return false;
        }

        void CloseCard(string reason, ClientDecision? sent)
        {
            var req = Card!;
            Card = null;
            Mode = CardMode.None;
            _gestures.Reset();
            CardClosed?.Invoke(req, reason, sent);
            CardChanged?.Invoke();
        }
    }
}
