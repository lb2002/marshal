using System;
using Lotse.Core;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// Replying to an agent, wired to the live view's input bar (U4b). The draft can be written whenever the view is
    /// open: with the Quest system keyboard ("Edit", a tap on the text field), or with the desk
    /// keyboard ("Tastatur"). The two keyboards are never out at the same time. Only "Senden" sends (the input bar's or
    /// the desk keyboard's, which is the same explicit Senden), and only a non-blank draft, only while the system
    /// keyboard is closed (it holds the input focus) and only to an agent that waits ("Agent arbeitet gerade, Nachricht
    /// geht, sobald er wartet"). The ack, the bridge's refusal or no answer shows in the status line; a delivered reply
    /// clears the draft. ReplyLogic decides; this class moves text between the keyboards, the logic, the session and the
    /// view. A card that opens, placing, or leaving the playing phase closes the keyboards and keeps the draft; the desk
    /// keyboard is never out while a card is. The text is never logged.
    /// </summary>
    public sealed class ReplyController
    {
        readonly ReplyLogic _logic = new ReplyLogic();
        readonly IReplyKeyboard _keyboard;
        readonly DeskKeyboard _desk;
        readonly DeskKeys _keys = new DeskKeys();
        DemoSession _session;
        InputBar _view;
        string _sid;
        bool _keyboardUp;
        bool _noKeyboard;
        bool _blocked;
        string _note;
        Color _noteColor;
        double _nowMs;

        public ReplyController(IReplyKeyboard keyboard, DeskKeyboard desk)
        {
            _keyboard = keyboard;
            _desk = desk;
            _desk.KeyPressed += OnKey;
        }

        /// <summary>The agent's waitingFor from its detail (AppRoot wires it to the detail cache), or null.</summary>
        public Func<string, string> WaitingFor { get; set; }
        /// <summary>Where the desk keyboard lies (AppRoot: on the desk, or below the live view without a desk).</summary>
        public Func<Pose> KeyboardPose { get; set; }

        public ReplyLogic Logic => _logic;
        public DeskKeyboard Desk => _desk;
        public DeskKeys Keys => _keys;
        /// <summary>A reply is open (the live view's input bar).</summary>
        public bool IsOpen => _logic.OpenSid != null;
        public bool SystemKeyboardUp => _keyboardUp;
        /// <summary>The user is typing (a keyboard is out): the live view does not time out meanwhile.</summary>
        public bool Typing => _keyboardUp || _desk.Shown;

        /// <summary>The desk keyboard's keys are not a click elsewhere for the live view.</summary>
        public bool OwnsButton(PokeButton b) => _desk.Owns(b);

        /// <summary>A new session (or none): the reply and the keyboards close.</summary>
        public void Reset(DemoSession session)
        {
            Detach();
            if (_session != null)
            {
                _session.ReplyAcked -= OnAck;
                _session.ReplyFailed -= OnFailed;
            }
            _session = session;
            if (session == null) return;
            session.ReplyAcked += OnAck;
            session.ReplyFailed += OnFailed;
        }

        /// <summary>
        /// A view opened for `agent` (the live view now, U5's window later): `buildBar` makes its InputBar with our
        /// actions (Send, Edit, Keyboard); the bar shows the agent's kept draft (or nothing).
        /// </summary>
        public void Attach(AgentState agent, Func<Action, Action, Action, InputBar> buildBar, double nowMs)
        {
            _nowMs = nowMs;
            CloseKeyboard();
            HideDesk();
            _sid = agent.Sid;
            _note = null;
            _logic.Open(agent.Sid, false, Availability(), editAnyway: true);
            _view = buildBar(Send, Edit, ToggleDesk);
            Refresh();
        }

        /// <summary>The input bar the reply is attached to, or null.</summary>
        public InputBar Bar => _view;

        /// <summary>The live view closed: the keyboards go away, the draft stays.</summary>
        public void Detach()
        {
            CloseKeyboard();
            HideDesk();
            if (_logic.OpenSid != null) _logic.Close();
            _view = null;
            _sid = null;
        }

        ReplyAvailability Availability()
        {
            var agent = AgentOf(_sid);
            return agent == null ? ReplyAvailability.NotSupported : ReplyLogic.Can(agent, WaitingFor?.Invoke(_sid));
        }

        AgentState AgentOf(string sid)
        {
            if (sid == null || _session == null) return null;
            foreach (var a in _session.Store.Agents)
                if (a.Sid == sid) return a;
            return null;
        }

        /// <summary>Once per frame: the keyboard's text, the ack timeout, the card priority, a delivered reply.</summary>
        public void Step(double nowMs, bool blocked)
        {
            _nowMs = nowMs;
            _blocked = blocked;
            if (blocked) HideDesk(); // never out while a card is, nor while placing
            if (_logic.OpenSid != null && _keyboardUp)
            {
                var status = _keyboard.Status;
                // the text of the last Visible frame (or of Done) is the draft; a cancelled or lost keyboard may hand
                // back something else, which is not taken. The keyboard's Done never sends.
                if (status == KeyboardStatus.Visible || status == KeyboardStatus.Done) _logic.SetText(_keyboard.Text);
                if (status != KeyboardStatus.Visible) _keyboardUp = false;
            }
            switch (_logic.Step(nowMs, blocked))
            {
                case ReplyStep.ClosedForCard:
                    CloseKeyboard();
                    HideDesk();
                    Debug.Log("[Lotse] reply closed: a card, placing or another phase comes first (draft kept)");
                    return;
                case ReplyStep.ClosedDelivered:
                    // the view stays open: the input bar starts over, empty
                    if (_view != null && _sid != null)
                    {
                        _logic.Open(_sid, false, Availability(), editAnyway: true);
                        _note = null;
                    }
                    break;
            }
            if (_logic.OpenSid != null && _view != null) Refresh();
        }

        void OnFailed(string sid, string error)
        {
            Debug.Log($"[Lotse] reply refused {Texts.LogSid(sid)}: {Untrusted.Clamp(error, 24)}");
            _logic.OnFailed(sid, error);
            if (_view != null) Refresh();
        }

        void OnAck(string sid, bool delivered)
        {
            Debug.Log($"[Lotse] reply ack {Texts.LogSid(sid)} delivered={delivered}");
            _logic.OnAck(sid, delivered, _nowMs);
            if (_view != null) Refresh();
        }

        /// <summary>"Bearbeiten", or a tap on the text: the system keyboard opens with the draft (the desk keyboard goes).</summary>
        void Edit()
        {
            if (!CanEdit) return;
            HideDesk();
            OpenKeyboard();
            _note = null;
            Refresh();
        }

        /// <summary>"Tastatur": the desk keyboard comes out on the desk (the system keyboard closes), or goes away.</summary>
        void ToggleDesk()
        {
            if (_desk.Shown)
            {
                HideDesk();
                Refresh();
                return;
            }
            if (!CanEdit || _blocked || KeyboardPose == null) return;
            CloseKeyboard();
            _keys.Begin(_logic.Draft);
            _desk.Show(KeyboardPose(), _keys);
            _note = null;
            Debug.Log("[Lotse] desk keyboard out");
            Refresh();
        }

        void HideDesk()
        {
            if (!_desk.Shown) return;
            _desk.Hide();
            Debug.Log("[Lotse] desk keyboard away");
        }

        /// <summary>A key of the desk keyboard: it edits the draft; its "Senden" is the explicit Senden. It never sends by itself.</summary>
        void OnKey(DeskKey key)
        {
            if (!_desk.Shown || _logic.OpenSid == null) return;
            switch (_keys.Press(key, _logic.Draft, out var draft))
            {
                case DeskKeyResult.Typed:
                case DeskKeyResult.Deleted:
                    _logic.SetText(draft);
                    _note = null;
                    _desk.Relabel(_keys);
                    break;
                case DeskKeyResult.Toggled:
                    _desk.Relabel(_keys);
                    break;
                case DeskKeyResult.Send:
                    Send();
                    return;
            }
            Refresh();
        }

        bool CanEdit => _logic.OpenSid != null && !_keyboardUp && _logic.Phase != ReplyPhase.Sending;

        void OpenKeyboard()
        {
            _keyboardUp = _keyboard.Open(_logic.Draft);
            _noKeyboard = !_keyboardUp;
        }

        /// <summary>"Senden": the only way a reply goes out, and only once the system keyboard is closed.</summary>
        void Send()
        {
            var sid = _logic.OpenSid;
            if (sid == null || _keyboardUp) return; // the system keyboard holds the input focus; the text may still change
            switch (_logic.Send(_nowMs, out var text, Availability()))
            {
                case SendResult.Sent:
                    _note = null;
                    var sent = _session != null && _session.SendReply(sid, text);
                    if (!sent) _logic.SendFailed();
                    Debug.Log($"[Lotse] reply sent {Texts.LogSid(sid)} ({text.Length} characters)" + (sent ? "" : ": could not go out"));
                    break;
                case SendResult.TooSoon:
                    _note = Texts.ReplyTooSoon;
                    _noteColor = Theme.WarnText;
                    break;
                case SendResult.NotWaiting:
                    _note = Texts.ReplyAgentBusy;
                    _noteColor = Theme.Muted;
                    break;
            }
            Refresh();
        }

        void CloseKeyboard()
        {
            if (!_keyboardUp && _keyboard.Status == KeyboardStatus.Closed) return;
            _keyboard.Close();
            _keyboardUp = false;
        }

        void Refresh()
        {
            if (_view == null) return;
            var can = Availability();
            _view.SetDraft(_logic.Draft);
            _view.SetKeyboardOn(_desk.Shown);
            var busy = _logic.Phase == ReplyPhase.Sending || _logic.Phase == ReplyPhase.Delivered;
            var canSend = !_keyboardUp && !ReplyLogic.IsBlank(_logic.Draft) && can == ReplyAvailability.Available && !busy;
            var canEdit = !_keyboardUp && !busy;
            _desk.SetSendEnabled(canSend);
            string line;
            Color color;
            switch (_logic.Phase)
            {
                case ReplyPhase.Sending:
                    line = Texts.ReplySending;
                    color = Theme.Muted;
                    break;
                case ReplyPhase.Delivered:
                    line = Texts.ReplyDelivered;
                    color = Theme.GoodText;
                    break;
                case ReplyPhase.NotDelivered:
                    line = _note ?? Texts.ReplyNotDelivered;
                    color = Theme.WarnText;
                    break;
                case ReplyPhase.NoAnswer:
                    line = _note ?? Texts.ReplyNoAnswer;
                    color = Theme.WarnText;
                    break;
                case ReplyPhase.Refused:
                    line = _note ?? Texts.ReplyRefused(_logic.RefusedError ?? "");
                    color = Theme.WarnText;
                    break;
                default:
                    if (_note != null)
                    {
                        line = _note;
                        color = _noteColor;
                    }
                    else if (_noKeyboard && !_desk.Shown)
                    {
                        line = Texts.ReplyNoKeyboard;
                        color = Theme.WarnText;
                    }
                    else if (!string.IsNullOrEmpty(WaitingFor?.Invoke(_sid)))
                    {
                        // what it waits for, in its own words (capped and made visible); then why Senden waits, if it does
                        line = Texts.WaitingForLine(WaitingFor(_sid)) +
                               (can == ReplyAvailability.NotSupported ? ". " + Texts.ReplyNotSupported : "");
                        color = Theme.WarnText;
                    }
                    else if (can == ReplyAvailability.NotWaiting)
                    {
                        line = Texts.ReplyAgentBusy;
                        color = Theme.Muted;
                    }
                    else if (can == ReplyAvailability.NotSupported)
                    {
                        line = Texts.ReplyNotSupported;
                        color = Theme.Muted;
                    }
                    else
                    {
                        line = "";
                        color = Theme.Muted;
                    }
                    break;
            }
            _view.SetStatus(line, color, canSend, canEdit);
        }
    }
}
