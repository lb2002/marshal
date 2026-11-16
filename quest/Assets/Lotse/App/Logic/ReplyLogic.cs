#nullable enable
using System.Collections.Generic;
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>Whether an agent can be answered from the headset now.</summary>
    public enum ReplyAvailability
    {
        Available,
        /// <summary>It is busy: "Antworten geht, sobald der Agent auf dich wartet".</summary>
        NotWaiting,
        /// <summary>Not a Claude Code session: "Antworten geht bei diesem Agenten noch nicht".</summary>
        NotSupported,
    }

    public enum ReplyPhase { Editing, Sending, Delivered, NotDelivered, NoAnswer, Refused }

    public enum SendResult
    {
        Sent,
        /// <summary>Nothing (or only blanks) typed: nothing sent.</summary>
        Empty,
        /// <summary>Within 5 s of the last reply to this agent: nothing sent.</summary>
        TooSoon,
        /// <summary>A reply is on its way already.</summary>
        Busy,
        /// <summary>No reply panel is open.</summary>
        NotOpen,
        /// <summary>The agent does not wait for an answer now (or cannot be answered): nothing sent.</summary>
        NotWaiting,
    }

    public enum ReplyStep { None, ClosedForCard, ClosedDelivered }

    /// <summary>
    /// Replying to an agent (shared contract 2026-11-12-contract-agent-reply.md), as a state machine. A reply panel
    /// opens for one agent only when it can be answered and no card is open; a card that opens closes it and keeps
    /// the draft (the card comes first). The draft is the user's own text, capped at 1000 characters; nothing is
    /// prefilled. Only an explicit Send sends it, at most once per agent every 5.5 s; the bridge's ack says whether a
    /// waiting agent got it; without an ack in 5 s the bridge did not answer. A delivered reply clears the draft and
    /// the panel closes a moment later. A reply never approves or touches a card. Pure, time injected.
    /// </summary>
    public sealed class ReplyLogic
    {
        public const int MaxChars = 1000;
        public const double AckTimeoutMs = 5000;
        /// <summary>At most one reply per agent this often: a little over the bridge's 5 s limit, so it never refuses ours.</summary>
        public const double SendEveryMs = 5500;
        /// <summary>"Gesendet" stays this long, then the panel closes.</summary>
        public const double DeliveredShowMs = 2500;

        readonly Dictionary<string, string> _drafts = new Dictionary<string, string>();
        readonly Dictionary<string, double> _sentAt = new Dictionary<string, double>();
        string? _sendingSid;
        bool _hadSentAt;
        double _previousSentAt;
        double _sendingAt;
        double _deliveredAt;

        /// <summary>
        /// Only Claude Code sessions can be answered for now; one that is done, waits, failed, or whose detail says it
        /// waits for something.
        /// </summary>
        public static ReplyAvailability Can(AgentState agent, string? waitingFor)
        {
            if (agent.Source != "claude") return ReplyAvailability.NotSupported;
            var waits = agent.Status == "done" || agent.Status == "waiting" || agent.Status == "failed" || !string.IsNullOrEmpty(waitingFor);
            return waits ? ReplyAvailability.Available : ReplyAvailability.NotWaiting;
        }

        /// <summary>The agent whose reply panel is open, or null.</summary>
        public string? OpenSid { get; private set; }
        public ReplyPhase Phase { get; private set; }
        /// <summary>The open agent's draft ("" when none).</summary>
        public string Draft => OpenSid != null && _drafts.TryGetValue(OpenSid, out var d) ? d : "";

        /// <summary>The kept draft for `sid`, open or not ("" for none): a draft keeps the window from folding (U6).</summary>
        public string DraftOf(string sid) => sid != null && _drafts.TryGetValue(sid, out var d) ? d : "";

        /// <summary>
        /// Opens the reply for `sid` (its own draft, if one was kept); false while blocked or when it cannot be answered.
        /// `editAnyway` (the live view's input bar, U4b): a draft may be written while the agent works; Send still
        /// refuses until it waits.
        /// </summary>
        public bool Open(string sid, bool blocked, ReplyAvailability can, bool editAnyway = false)
        {
            if (blocked || (can != ReplyAvailability.Available && !editAnyway)) return false;
            if (sid != OpenSid || Phase != ReplyPhase.Sending)
            {
                if (_sendingSid != sid) _sendingSid = null; // an ack for another agent no longer matters
                Phase = _sendingSid == sid ? ReplyPhase.Sending : ReplyPhase.Editing;
            }
            OpenSid = sid;
            RefusedError = null;
            return true;
        }

        /// <summary>What the keyboard holds now (ignored while a reply is on its way).</summary>
        public void SetText(string text)
        {
            if (OpenSid == null || Phase == ReplyPhase.Sending) return;
            var capped = Cap(text ?? "");
            if (capped == Draft && Phase == ReplyPhase.Editing) return;
            if (capped != Draft) Phase = ReplyPhase.Editing; // typing after a result starts over
            _drafts[OpenSid] = capped;
        }

        static string Cap(string text)
        {
            if (text.Length <= MaxChars) return text;
            var end = MaxChars;
            if (char.IsHighSurrogate(text[end - 1])) end--; // never half a surrogate pair
            return text.Substring(0, end);
        }

        /// <summary>The explicit Senden: only an open, non-blank draft to an agent that waits, at most once per agent every 5.5 s.</summary>
        public SendResult Send(double nowMs, out string text, ReplyAvailability can = ReplyAvailability.Available)
        {
            text = "";
            if (OpenSid == null) return SendResult.NotOpen;
            if (Phase == ReplyPhase.Sending) return SendResult.Busy;
            if (IsBlank(Draft)) return SendResult.Empty;
            if (can != ReplyAvailability.Available) return SendResult.NotWaiting;
            if (_sentAt.TryGetValue(OpenSid, out var at) && nowMs - at < SendEveryMs) return SendResult.TooSoon;
            text = Draft;
            _hadSentAt = _sentAt.TryGetValue(OpenSid, out _previousSentAt);
            _sentAt[OpenSid] = nowMs;
            _sendingSid = OpenSid;
            _sendingAt = nowMs;
            Phase = ReplyPhase.Sending;
            return SendResult.Sent;
        }

        /// <summary>The bridge's ack; true when it was for the reply on its way.</summary>
        public bool OnAck(string sid, bool delivered, double nowMs)
        {
            if (_sendingSid != sid || Phase != ReplyPhase.Sending) return false; // also while the panel is closed
            _sendingSid = null;
            if (delivered)
            {
                _drafts.Remove(sid);
                _deliveredAt = nowMs;
                Phase = ReplyPhase.Delivered;
            }
            else
            {
                Phase = ReplyPhase.NotDelivered;
            }
            return true;
        }

        /// <summary>The bridge's error for the reply on its way (RefusedError says which); true when it was for it.</summary>
        public bool OnFailed(string sid, string error)
        {
            if (_sendingSid != sid || Phase != ReplyPhase.Sending) return false;
            _sendingSid = null;
            RefusedError = error;
            Phase = ReplyPhase.Refused;
            return true;
        }

        /// <summary>The bridge's error code while Refused, else null.</summary>
        public string? RefusedError { get; private set; }

        /// <summary>Blank by the bridge's own rule (bridge/src/replies.ts cleanReply): it would refuse it as empty.</summary>
        public static bool IsBlank(string text)
        {
            // cleanReply: CR to LF, every other control (U+0000–0009, 000B–001F, 007F–009F) removed, then JS trim
            foreach (var c in text)
            {
                if (c == '\r' || c == '\n') continue;
                if (c <= '\u001F' || (c >= '\u007F' && c <= '\u009F')) continue;
                if (char.IsWhiteSpace(c) || c == '\uFEFF') continue;
                return false;
            }
            return true;
        }

        /// <summary>The sent text could not go out at all (no link): treated like no answer.</summary>
        public void SendFailed()
        {
            if (Phase != ReplyPhase.Sending) return;
            // nothing went out: the rate limit is as it was
            if (_sendingSid != null)
            {
                if (_hadSentAt) _sentAt[_sendingSid] = _previousSentAt;
                else _sentAt.Remove(_sendingSid);
            }
            _sendingSid = null;
            Phase = ReplyPhase.NoAnswer;
        }

        /// <summary>Once per frame: the ack timeout, the card priority and the close after delivery.</summary>
        public ReplyStep Step(double nowMs, bool blocked)
        {
            if (Phase == ReplyPhase.Sending && _sendingSid != null && nowMs - _sendingAt >= AckTimeoutMs)
            {
                _sendingSid = null;
                Phase = ReplyPhase.NoAnswer;
            }
            if (OpenSid == null) return ReplyStep.None;
            if (blocked)
            {
                OpenSid = null; // the card comes first; the draft stays
                return ReplyStep.ClosedForCard;
            }
            if (Phase == ReplyPhase.Delivered && nowMs - _deliveredAt >= DeliveredShowMs)
            {
                OpenSid = null;
                return ReplyStep.ClosedDelivered;
            }
            return ReplyStep.None;
        }

        /// <summary>The live view closes: the reply closes, its draft stays for next time (a reply on its way still gets its ack).</summary>
        public void Close() => OpenSid = null;

        /// <summary>"Abbrechen": the panel closes and its draft is dropped.</summary>
        public void Cancel()
        {
            if (OpenSid == null) return;
            _drafts.Remove(OpenSid);
            if (_sendingSid == OpenSid) _sendingSid = null;
            OpenSid = null;
        }
    }
}
