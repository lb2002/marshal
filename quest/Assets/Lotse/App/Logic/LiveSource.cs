#nullable enable
using System;
using System.Collections.Generic;
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>Why the last attempt to connect ended without pairing (the device log's reason; no address in it).</summary>
    public enum LinkFailure
    {
        None,
        /// <summary>Nothing came in time.</summary>
        Timeout,
        /// <summary>The socket closed before the bridge answered (nobody listening, or the network).</summary>
        Closed,
        /// <summary>The bridge said no to the code or the session.</summary>
        Refused,
    }

    public enum LinkState
    {
        /// <summary>Not connected and not trying.</summary>
        Idle,
        /// <summary>Socket opening, hello sent, waiting for the bridge's snapshot.</summary>
        Connecting,
        /// <summary>Paired: the snapshot arrived, messages flow, decisions can be sent.</summary>
        Live,
        /// <summary>Waiting out the backoff before the next attempt.</summary>
        Retrying,
        /// <summary>The bridge refused the code or the session, or a sent code got no answer: only a new code helps. No automatic retry.</summary>
        NeedsCode,
    }

    /// <summary>
    /// The bridge on the user's PC as a message source (bridge/src/server.ts, WebSocket port 47821).
    ///
    /// Pairing: `hello {code}` is answered with the snapshot, then `paired {session}`; the token is stored
    /// per host (an older token for that host is forgotten with the snapshot). The code is single-use: once
    /// its hello has gone out it is never sent again, so a timeout or a close before the snapshot asks for a
    /// new code. A reconnect sends `hello {session}` first; the bridge answers a valid one with the snapshot
    /// and a bad one by closing the socket (NativeWebSocket reports its 4001 as 1004, the same as any other
    /// bridge close code) or, for a malformed frame, with `error`. A close counts as the bridge's verdict only
    /// when the hello actually went out; the token is kept (1004 is ambiguous) until a new pairing replaces it,
    /// but it is marked refused and never resumed again (each refused hello counts towards the bridge's
    /// lock-out of the address).
    ///
    /// Liveness while live: a decision must be answered (perm_closed for its id, or an error) within 5 s,
    /// and a link silent for 15 s gets a `ping`; any frame ends the wait for it. The bridge answers with
    /// `pong` (swallowed here); an older bridge answers an unknown frame with `error bad-message`, which is
    /// swallowed only when it is the first frame after the ping. Silence for 25 s counts as lost.
    ///
    /// A connection that drops or goes silent while live puts a ConnectionLostMessage into the stream and
    /// reconnects with backoff 1, 2, 4, 8, 10, 10 … s. Pure: time comes in through Poll, the socket through
    /// a factory.
    /// </summary>
    public sealed class LiveSource : IMessageSource
    {
        public const int Port = 47821;
        public const double HandshakeTimeoutMs = 8000;
        /// <summary>The one resume attempt after "Connect to my PC" has this long to pair.</summary>
        public const double ResumeOnceMs = 3000;
        public const double FirstBackoffMs = 1000;
        public const double MaxBackoffMs = 10000;
        /// <summary>A sent decision without perm_closed or error after this counts as a lost link.</summary>
        public const double AnswerTimeoutMs = 5000;
        /// <summary>A detail request without an answer after this is given up (the bridge may drop one silently).</summary>
        public const double DetailAnswerTimeoutMs = 5000;
        /// <summary>Silence after which a probe frame is sent.</summary>
        public const double IdleProbeMs = 15000;
        /// <summary>Silence after which the link counts as lost.</summary>
        public const double IdleTimeoutMs = 25000;
        /// <summary>The liveness ping; the bridge answers `pong` (older bridges: `error bad-message`).</summary>
        public const string Probe = "{\"type\":\"ping\"}";
        /// <summary>An older bridge's answer to the ping, which it does not know.</summary>
        const string OldBridgeProbeAnswer = "bad-message";
        /// <summary>What NativeWebSocket makes of the bridge's own close codes (4001 bad credentials, 4029 rate limit, 4008 hello timeout).</summary>
        const int UndefinedClose = 1004;

        readonly HostBook _hosts;
        readonly Func<string, ILinkSocket> _openSocket;
        readonly List<ServerMessage> _inbox = new List<ServerMessage>();
        ILinkSocket? _socket;
        string? _code;
        bool _viaSession;
        bool _helloSent;
        bool _codeSent;
        double _now;
        double _attemptStart;
        double _retryAt;
        int _failures;
        double _lastFrameAt;
        bool _probeOut;
        string? _answerId;
        double _answerDue;
        /// <summary>
        /// When each detail request still waiting for an answer went out (an older bridge answers each with `error
        /// bad-message`). One without an answer for DetailAnswerTimeoutMs is given up, so a request the bridge dropped
        /// never stays pending.
        /// </summary>
        readonly List<Outstanding> _detailsOut = new List<Outstanding>();
        /// <summary>When each reply still waiting for its ack went out (an older bridge answers it with `error bad-message`).</summary>
        readonly List<Outstanding> _repliesOut = new List<Outstanding>();
        /// <summary>When each feed_subscribe still waiting for its snapshot went out (an error may answer it instead).</summary>
        readonly List<Outstanding> _feedsOut = new List<Outstanding>();
        /// <summary>This connection's bridge answered a feed_subscribe with bad-message (an older bridge): not sent again on it.</summary>
        bool _feedUnsupported;
        /// <summary>A detail request or a reply that waits for the bridge's answer.</summary>
        readonly struct Outstanding
        {
            public readonly double At;
            public readonly long Seq;
            public readonly string Sid;

            public Outstanding(double at, long seq, string sid)
            {
                At = at;
                Seq = seq;
                Sid = sid;
            }
        }

        long _sentSeq;

        /// <summary>A request given up for no answer: an error for it may still come; it is dropped until then.</summary>
        double _expiredUntil = double.NegativeInfinity;

        void Expire(List<Outstanding> list)
        {
            while (list.Count > 0 && _now - list[0].At >= DetailAnswerTimeoutMs)
            {
                list.RemoveAt(0);
                _expiredUntil = _now + DetailAnswerTimeoutMs;
            }
        }

        static void RemoveSid(List<Outstanding> list, string sid)
        {
            for (var i = 0; i < list.Count; i++)
                if (list[i].Sid == sid)
                {
                    list.RemoveAt(i);
                    return;
                }
        }

        /// <summary>The list whose first request went out first (detail requests, replies, feed subscriptions).</summary>
        List<Outstanding> OldestOutstanding()
        {
            List<Outstanding>? best = null;
            foreach (var list in new[] { _detailsOut, _repliesOut, _feedsOut })
                if (list.Count > 0 && (best == null || list[0].Seq < best[0].Seq)) best = list;
            return best!;
        }

        /// <summary>The bridge's errors that answer a detail request, a reply or a feed subscription (never a decision).</summary>
        static bool AnswersARequest(string error) => error == OldBridgeProbeAnswer || error == "unknown-agent" || error == "rate-limited";

        /// <summary>This connection's bridge answered a detail request with bad-message: not sent again until the next connection.</summary>
        bool _detailUnsupported;

        /// <param name="openSocket">Creates an unconnected socket for a ws:// URL (one per attempt).</param>
        public LiveSource(HostBook hosts, Func<string, ILinkSocket> openSocket)
        {
            _hosts = hosts;
            _openSocket = openSocket;
        }

        public LinkState State { get; private set; } = LinkState.Idle;
        /// <summary>The bridge address being used, or null.</summary>
        public string? Host { get; private set; }
        /// <summary>One line of plain German for the pairing panel or the banner.</summary>
        public string Status { get; private set; } = "";
        public event Action? StateChanged;

        public bool Finished => false;

        public static string UrlFor(string host) => "ws://" + host + ":" + Port;

        public static bool IsPairingCode(string? code)
        {
            if (code == null || code.Length != KeypadEntry.CodeLength) return false;
            foreach (var c in code)
                if (c < '0' || c > '9') return false;
            return true;
        }

        /// <summary>A typed code is tried this often at an address that does not answer, then given up.</summary>
        public const int CodeAttempts = 3;

        /// <summary>
        /// Pairs with a fresh 6-digit code from `lotse start`. False (and nothing happens) for a bad host or
        /// code, or while an attempt is already connecting.
        /// A code is never kept for an address that does not answer (U11 fix round 1: otherwise the headset would pair,
        /// without another word from the user, whenever something starts listening there): a typed code gets
        /// CodeAttempts connection attempts, then NeedsCode. `single` (a scanned code): exactly one attempt, and when it
        /// does not pair, Host is again what it was before.
        /// </summary>
        public bool PairWithCode(string host, string code, bool single = false)
        {
            if (State == LinkState.Connecting || !HostBook.IsValidHost(host) || !IsPairingCode(code)) return false;
            // set before the first attempt: a socket may close from inside Connect
            Begin(host, code, single, Host);
            return true;
        }

        bool _single;
        string? _hostBefore;

        /// <summary>A single attempt that did not pair leaves no trace: the address is what it was before.</summary>
        void EndSingle()
        {
            if (!_single) return;
            _single = false;
            Host = _hostBefore;
        }

        /// <summary>
        /// Resumes with the session stored for `host`. False when there is none, when the bridge has already
        /// refused it (only a new code helps then), or while connecting.
        /// </summary>
        /// <param name="once">
        /// U12a, for the resume right after "Connect to my PC": exactly one attempt with ResumeOnceMs to pair. When it
        /// does not, it is dropped (NeedsCode, nothing to say, no retry: the stored address may be an old one and the
        /// user cannot see it). Once it has paired, a later drop is reconnected as always, without limit.
        /// </param>
        public bool Resume(string host, bool once = false)
        {
            if (State == LinkState.Connecting || !HostBook.IsValidHost(host) || _hosts.SessionFor(host) == null ||
                _hosts.SessionRefused(host)) return false;
            Begin(host, null, resumeOnce: once);
            return true;
        }

        bool _resumeOnce;

        /// <summary>Why the last attempt ended without pairing.</summary>
        public LinkFailure LastFailure { get; private set; }

        /// <summary>Closes the connection on purpose; no retry, no ConnectionLostMessage.</summary>
        public void Disconnect()
        {
            DropSocket();
            EndSingle();
            _inbox.Clear(); // what the old connection still had in flight never reaches the next session
            _code = null;
            _failures = 0;
            Set(LinkState.Idle, "");
        }

        public IReadOnlyList<ServerMessage> Poll(double dtMs)
        {
            _now += dtMs;
            Expire(_detailsOut);
            Expire(_repliesOut);
            Expire(_feedsOut);
            _socket?.Pump();
            if (State == LinkState.Connecting && _now - _attemptStart >= (_resumeOnce ? ResumeOnceMs : HandshakeTimeoutMs))
            {
                LastFailure = LinkFailure.Timeout;
                NoSnapshot();
            }
            else if (State == LinkState.Retrying && _now >= _retryAt) Attempt();
            else if (State == LinkState.Live) CheckLiveness();
            if (_inbox.Count == 0) return Array.Empty<ServerMessage>();
            var due = _inbox.ToArray();
            _inbox.Clear();
            return due;
        }

        /// <summary>
        /// Sends the decision while paired; null otherwise (also when the socket refuses it). The bridge's
        /// perm_closed comes later through Poll, and has to come within AnswerTimeoutMs.
        /// </summary>
        public IReadOnlyList<ServerMessage>? Send(ClientDecision decision)
        {
            if (State != LinkState.Live || _socket == null) return null;
            if (!_socket.Send(Protocol.Serialize(new PermDecisionMessage { Decision = decision }))) return null;
            _answerId = decision.Id;
            _answerDue = _now + AnswerTimeoutMs;
            return Array.Empty<ServerMessage>();
        }

        /// <summary>
        /// Sends agent_detail_request while paired; false otherwise, while a decision waits for its answer, or once the
        /// bridge has shown it does not know the request (an older bridge's `error bad-message`, which is swallowed).
        /// </summary>
        public bool RequestDetail(string sid, out AgentDetailMessage? answer)
        {
            answer = null;
            if (State != LinkState.Live || _socket == null || _detailUnsupported || _answerId != null) return false;
            if (!_socket.Send(Protocol.Serialize(new AgentDetailRequestMessage { Sid = sid }))) return false;
            _detailsOut.Add(new Outstanding(_now, ++_sentSeq, sid));
            return true;
        }

        /// <summary>
        /// Sends agent_reply while paired; null otherwise. The bridge's agent_reply_ack comes through Poll (an older
        /// bridge's `error bad-message` to it is swallowed; the panel's own 5 s timeout then says so).
        /// </summary>
        public IReadOnlyList<ServerMessage>? SendReply(string sid, string text)
        {
            if (State != LinkState.Live || _socket == null) return null;
            if (!_socket.Send(Protocol.Serialize(new AgentReplyMessage { Sid = sid, Text = text }))) return null;
            _repliesOut.Add(new Outstanding(_now, ++_sentSeq, sid));
            return Array.Empty<ServerMessage>();
        }

        /// <summary>
        /// Sends feed_subscribe while paired; false otherwise, while a decision waits for its answer, or once this
        /// connection's bridge has shown it does not know it. The snapshot and the appends come through Poll.
        /// </summary>
        public bool SubscribeFeed(string sid, out FeedSnapshotMessage? answer)
        {
            answer = null;
            if (State != LinkState.Live || _socket == null || _feedUnsupported || _answerId != null) return false;
            if (!_socket.Send(Protocol.Serialize(new FeedSubscribeMessage { Sid = sid }))) return false;
            RemoveSid(_feedsOut, sid);
            _feedsOut.Add(new Outstanding(_now, ++_sentSeq, sid));
            return true;
        }

        /// <summary>Sends `halt` while paired; null otherwise. The bridge's halt_state comes through Poll.</summary>
        public IReadOnlyList<ServerMessage>? SendHalt(bool on)
        {
            if (State != LinkState.Live || _socket == null) return null;
            if (!_socket.Send(Protocol.Serialize(new HaltMessage { On = on }))) return null;
            return Array.Empty<ServerMessage>();
        }

        /// <summary>Sends feed_unsubscribe while paired (the bridge ends every feed on disconnect anyway).</summary>
        public void UnsubscribeFeed(string sid)
        {
            RemoveSid(_feedsOut, sid);
            if (State != LinkState.Live || _socket == null || _feedUnsupported) return;
            _socket.Send(Protocol.Serialize(new FeedUnsubscribeMessage { Sid = sid }));
        }

        void Begin(string host, string? code, bool single = false, string? hostBefore = null, bool resumeOnce = false)
        {
            DropSocket();
            _resumeOnce = resumeOnce;
            LastFailure = LinkFailure.None;
            _single = single;
            _hostBefore = hostBefore;
            Host = host;
            _code = code;
            _failures = 0;
            Attempt();
        }

        void Attempt()
        {
            // a typed code pairs; without one the stored session resumes
            _viaSession = _code == null;
            if (_viaSession && _hosts.SessionFor(Host!) == null)
            {
                Set(LinkState.NeedsCode, Texts.NeedCode);
                return;
            }
            _helloSent = false;
            _codeSent = false;
            _probeOut = false;
            _detailUnsupported = false; // a new connection may reach an updated bridge
            _feedUnsupported = false;
            _answerId = null;
            _attemptStart = _now;
            var s = _openSocket(UrlFor(Host!));
            _socket = s;
            // a socket we gave up on may still report: only the current one counts
            s.Opened += () => { if (s == _socket) OnOpened(); };
            s.Received += text => { if (s == _socket) OnReceived(text); };
            s.Closed += code => { if (s == _socket) OnClosed(code); };
            Set(LinkState.Connecting, Texts.Connecting);
            s.Connect();
        }

        void OnOpened()
        {
            var hello = _viaSession
                ? new HelloMessage { Session = _hosts.SessionFor(Host!) }
                : new HelloMessage { Code = _code };
            _helloSent = _socket!.Send(Protocol.Serialize(hello));
            if (_helloSent && !_viaSession)
            {
                _codeSent = true;
                _code = null; // single-use: it has gone out, it is never sent again
            }
        }

        void OnReceived(string text)
        {
            _lastFrameAt = _now;
            var firstAfterProbe = _probeOut;
            _probeOut = false; // any frame proves the link is alive
            var msg = Protocol.Parse(text);
            if (msg == null) return;
            if (State != LinkState.Live)
            {
                if (msg is SnapshotMessage)
                {
                    // accepted: a code pairing made a new session, the token follows in `paired`
                    if (!_viaSession) _hosts.ForgetSession(Host!);
                    _single = false; // paired: the address stays
                    _resumeOnce = false; // paired: a later drop is reconnected without limit
                    _code = null;
                    _failures = 0;
                    _hosts.Remember(Host!);
                    _inbox.Add(msg);
                    Set(LinkState.Live, Texts.Paired);
                }
                else if (msg is ErrorMessage)
                {
                    Rejected();
                }
                return; // the bridge sends nothing else before the snapshot
            }
            switch (msg)
            {
                case PairedMessage paired:
                    if (paired.Session.Length > 0) _hosts.SaveSession(Host!, paired.Session);
                    return;
                case PongMessage _:
                    return; // the ping's answer
                case ErrorMessage err when firstAfterProbe && err.Error == OldBridgeProbeAnswer:
                    return; // an older bridge's answer to the ping
                case ErrorMessage err when (_detailsOut.Count > 0 || _repliesOut.Count > 0 || _feedsOut.Count > 0) && AnswersARequest(err.Error):
                    // The bridge's error answer to an outstanding agent_detail_request or agent_reply: `unknown-agent`,
                    // `rate-limited`, or an older bridge's `bad-message`. A decision never gets one of these (the
                    // broker answers decisions with unknown / needs-two-hand / needs-pinch-hold), so it is never shown
                    // as a refusal and never touches the decision on its way. The bridge sends such an error at once,
                    // while it handles the frame, and handles the frames one by one in the order they came: it belongs
                    // to the oldest request still waiting (its answers, unlike the AI's later detail, are never late).
                    // (a feed_subscribe too: the bridge answers an unknown sid with `unknown-agent`)
                    var oldest = OldestOutstanding();
                    if (oldest == _feedsOut)
                    {
                        var feed = _feedsOut[0];
                        _feedsOut.RemoveAt(0);
                        if (err.Error == OldBridgeProbeAnswer) _feedUnsupported = true;
                        _inbox.Add(new FeedFailedMessage { Sid = feed.Sid });
                    }
                    else if (oldest == _detailsOut)
                    {
                        var detail = _detailsOut[0];
                        _detailsOut.RemoveAt(0);
                        if (err.Error == OldBridgeProbeAnswer) _detailUnsupported = true;
                        _inbox.Add(new AgentDetailFailedMessage { Sid = detail.Sid });
                    }
                    else
                    {
                        var reply = _repliesOut[0];
                        _repliesOut.RemoveAt(0);
                        _inbox.Add(new AgentReplyFailedMessage { Sid = reply.Sid, Error = err.Error });
                    }
                    return;
                case ErrorMessage err when _now < _expiredUntil && AnswersARequest(err.Error):
                    return; // the late answer to a request given up a moment ago: dropped, never a refusal
                case AgentReplyAckMessage ack:
                    RemoveSid(_repliesOut, ack.Sid);
                    break;
                case FeedSnapshotMessage snapMsg:
                    RemoveSid(_feedsOut, snapMsg.Sid); // its subscription's answer
                    break;
                case AgentDetailMessage detailMsg:
                    // its own request's answer (the AI's later follow-up finds nothing left to clear)
                    RemoveSid(_detailsOut, detailMsg.Sid);
                    break;
                case ErrorMessage _:
                    _answerId = null; // the bridge answered the decision (with a refusal)
                    break;
                case PermClosedMessage closed when closed.Id == _answerId:
                    _answerId = null;
                    break;
            }
            _inbox.Add(msg);
        }

        void OnClosed(int code)
        {
            _socket = null;
            if (State == LinkState.Live)
            {
                Lost();
                return;
            }
            // only a close after our hello is the bridge's verdict on it
            if (_helloSent && (code == UndefinedClose || code >= 4000)) Rejected();
            else
            {
                LastFailure = LinkFailure.Closed;
                NoSnapshot();
            }
        }

        /// <summary>
        /// No snapshot (timeout or close): a sent code is used up; an unsent code is tried CodeAttempts times (a scanned
        /// one once) and then dropped; only a stored session is tried again without limit.
        /// </summary>
        void NoSnapshot()
        {
            if (_resumeOnce)
            {
                // the one resume attempt after "Connect to my PC": dropped, silently
                _resumeOnce = false;
                DropSocket();
                Set(LinkState.NeedsCode, "");
                return;
            }
            if (_codeSent)
            {
                DropSocket();
                EndSingle();
                Set(LinkState.NeedsCode, Texts.CodeUnconfirmed);
                return;
            }
            if (!_viaSession && (_single || _failures + 1 >= CodeAttempts))
            {
                DropSocket();
                _code = null;
                EndSingle();
                Set(LinkState.NeedsCode, Texts.UnreachableGaveUp);
                return;
            }
            ScheduleRetry(null);
        }

        void CheckLiveness()
        {
            if (_answerId != null && _now >= _answerDue)
            {
                Lost();
                return;
            }
            var silent = _now - _lastFrameAt;
            if (silent >= IdleTimeoutMs)
            {
                Lost();
                return;
            }
            if (silent >= IdleProbeMs && !_probeOut && _socket != null)
                _probeOut = _socket.Send(Probe);
        }

        void Lost()
        {
            _inbox.Add(new ConnectionLostMessage());
            ScheduleRetry(Texts.ConnectionLost);
        }

        /// <summary>The bridge said no to the code or the session: ask for a new code. The token stays until a new pairing replaces it.</summary>
        void Rejected()
        {
            DropSocket();
            _code = null;
            _resumeOnce = false;
            LastFailure = LinkFailure.Refused;
            if (_viaSession) _hosts.MarkSessionRefused(Host!);
            var viaSession = _viaSession;
            EndSingle();
            Set(LinkState.NeedsCode, viaSession ? Texts.SessionExpired : Texts.CodeRejected);
        }

        void ScheduleRetry(string? why)
        {
            DropSocket();
            var delay = Math.Min(FirstBackoffMs * Math.Pow(2, Math.Min(_failures, 10)), MaxBackoffMs);
            _failures++;
            _retryAt = _now + delay;
            Set(LinkState.Retrying, why ?? Texts.Unreachable(delay));
        }

        void DropSocket()
        {
            var s = _socket;
            _socket = null;
            _answerId = null;
            _probeOut = false;
            _detailsOut.Clear();
            _repliesOut.Clear();
            _feedsOut.Clear();
            s?.Close();
        }

        void Set(LinkState state, string status)
        {
            var changed = state != State || status != Status;
            State = state;
            Status = status;
            if (changed) StateChanged?.Invoke();
        }
    }
}
