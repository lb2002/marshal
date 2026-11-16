#nullable enable
using System.Collections.Generic;
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>
    /// Where a session's ServerMessages come from: the offline demo pack (PackSource) or the bridge on the
    /// user's PC (LiveSource). The session feeds both into the same store and card pipeline.
    /// </summary>
    public interface IMessageSource
    {
        /// <summary>Advances by `dtMs` and returns what arrived since the last call, in order.</summary>
        IReadOnlyList<ServerMessage> Poll(double dtMs);

        /// <summary>
        /// Sends a decision. Null when it could not be sent (not the pending request, not connected).
        /// Otherwise the messages that answer it at once: the pack's agent update and perm_closed; empty for
        /// the bridge, whose perm_closed arrives later through Poll.
        /// </summary>
        IReadOnlyList<ServerMessage>? Send(ClientDecision decision);

        /// <summary>Nothing more will come and nothing is pending (the pack is played out; never for live).</summary>
        bool Finished { get; }

        /// <summary>
        /// Asks for one agent's detail. False when it cannot be asked (no live link, an older bridge, no pack entry).
        /// `answer` is the detail when it is known at once (the pack's static `agents`); live, it arrives later
        /// through Poll as an AgentDetailMessage.
        /// </summary>
        bool RequestDetail(string sid, out AgentDetailMessage? answer);

        /// <summary>
        /// Sends the user's reply to an agent. Null when it could not be sent (no live link). Otherwise what answers it
        /// at once: the pack's ack and the agent's canned reaction; empty for the bridge, whose ack comes through Poll.
        /// </summary>
        IReadOnlyList<ServerMessage>? SendReply(string sid, string text);

        /// <summary>
        /// Starts one agent's live feed (U4b). False when it cannot (no live link, an older bridge). The snapshot and the
        /// appends come through Poll (the pack's snapshot at once: see `answer`).
        /// </summary>
        bool SubscribeFeed(string sid, out FeedSnapshotMessage? answer);

        /// <summary>Ends one agent's live feed (nothing answers it).</summary>
        void UnsubscribeFeed(string sid);

        /// <summary>
        /// Halts every agent (on) or releases them (off). Null when it could not be sent (no live link). Otherwise what
        /// answers it at once: the pack handles halt itself (its halt_state, and its open request closed with "halt");
        /// empty for the bridge, whose halt_state comes through Poll. Never an allow.
        /// </summary>
        IReadOnlyList<ServerMessage>? SendHalt(bool on);
    }

    /// <summary>
    /// Not a wire message: LiveSource puts it into the stream when the bridge answered a feed_subscribe with an error
    /// (`unknown-agent`, or an older bridge's `bad-message`). The live view says "Kein Verlauf verfügbar"; the error
    /// never reaches the decision path.
    /// </summary>
    public sealed class FeedFailedMessage : ServerMessage
    {
        public string Sid { get; set; } = "";
    }

    /// <summary>
    /// Not a wire message: LiveSource puts it into the stream, in order, when the connection to the bridge
    /// is gone. The bridge answers every pending request with "no decision" then, so the terminal asks
    /// again by itself; the session drops its cards and sends nothing.
    /// </summary>
    public sealed class ConnectionLostMessage : ServerMessage
    {
    }

    /// <summary>
    /// Not a wire message: LiveSource puts it into the stream when the bridge answered an agent_detail_request with an
    /// error (`unknown-agent`, `rate-limited`, or an older bridge's `bad-message`). The panel then says "Keine Details";
    /// the error never reaches the decision path.
    /// </summary>
    public sealed class AgentDetailFailedMessage : ServerMessage
    {
        public string Sid { get; set; } = "";
    }

    /// <summary>
    /// Not a wire message: LiveSource puts it into the stream when the bridge answered an agent_reply with an error
    /// (`rate-limited`, `bad-message`, `unknown-agent`). The reply panel says why; it never reaches the decision path.
    /// </summary>
    public sealed class AgentReplyFailedMessage : ServerMessage
    {
        public string Sid { get; set; } = "";
        public string Error { get; set; } = "";
    }
}
