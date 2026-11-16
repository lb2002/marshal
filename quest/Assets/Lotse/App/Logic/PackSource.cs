#nullable enable
using System.Collections.Generic;
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>The offline demo pack as a message source: PackPlayer on its own clock, decisions answered at once.</summary>
    public sealed class PackSource : IMessageSource
    {
        readonly PackPlayer _player;
        readonly DemoPack _pack;
        readonly DemoFeed _feed = new DemoFeed();
        readonly HashSet<string> _feedSids = new HashSet<string>();
        readonly Dictionary<string, AgentState> _agents = new Dictionary<string, AgentState>();
        readonly List<ServerMessage> _out = new List<ServerMessage>();

        public PackSource(DemoPack pack)
        {
            _pack = pack;
            _player = new PackPlayer(pack);
        }

        public bool Finished => _player.Finished;

        /// <summary>The demo's sources, all of which "stop before their next step" while halted.</summary>
        public static readonly string[] DemoSources = { "claude", "codex", "muse", "gemini" };

        /// <summary>Halted (demo): playback stands still until released.</summary>
        public bool Halted { get; private set; }

        /// <summary>The player's messages, each agent update of a subscribed agent followed by its feed entry (U4b). Nothing while halted.</summary>
        public IReadOnlyList<ServerMessage> Poll(double dtMs) => Halted ? System.Array.Empty<ServerMessage>() : WithFeed(_player.Tick(dtMs));

        /// <summary>
        /// The demo handles halt itself: on, playback pauses and the open request (if any) is denied, its perm_closed
        /// carrying "halt"; off, playback resumes. Answered at once with the halt_state the bridge would send. Halting
        /// again while halted changes nothing. Never an allow.
        /// </summary>
        public IReadOnlyList<ServerMessage>? SendHalt(bool on)
        {
            var msgs = new List<ServerMessage>();
            var state = new HaltStateMessage { On = on, Since = on ? _player.NowMs : (double?)null };
            foreach (var s in DemoSources) state.Enforcement[s] = "every-step";
            msgs.Add(state);
            if (on && !Halted && _player.PendingId != null)
                foreach (var m in _player.Decide(_player.PendingId, false))
                    msgs.Add(m is PermClosedMessage c ? new PermClosedMessage { Id = c.Id, Reason = "halt" } : m);
            Halted = on;
            return WithFeed(msgs);
        }

        IReadOnlyList<ServerMessage> WithFeed(IReadOnlyList<ServerMessage> msgs)
        {
            var any = false;
            for (var i = 0; i < msgs.Count; i++) // no enumerator: a frame without a feed allocates nothing
                if (msgs[i] is AgentMessage am)
                {
                    _agents[am.Agent.Sid] = am.Agent;
                    any |= _feedSids.Contains(am.Agent.Sid);
                }
            if (!any) return msgs;
            _out.Clear();
            foreach (var m in msgs)
            {
                _out.Add(m);
                if (m is AgentMessage a && _feedSids.Contains(a.Agent.Sid))
                {
                    var e = _feed.Update(a.Agent);
                    if (e != null) _out.Add(new FeedAppendMessage { Sid = a.Agent.Sid, Entries = new List<FeedEntry> { e } });
                }
            }
            return _out.ToArray();
        }

        /// <summary>Offline: the feed derived from the pack's agent detail (DemoFeed), at once.</summary>
        public bool SubscribeFeed(string sid, out FeedSnapshotMessage? answer)
        {
            _pack.Agents.TryGetValue(sid, out var detail);
            _agents.TryGetValue(sid, out var agent);
            answer = null;
            if (detail == null && agent == null) return false;
            _feedSids.Add(sid);
            answer = new FeedSnapshotMessage { Sid = sid, Entries = _feed.Snapshot(sid, detail, agent) };
            return true;
        }

        public void UnsubscribeFeed(string sid) => _feedSids.Remove(sid);

        /// <summary>Answers from the pack's static `agents` (a copy, so the pack itself is never changed); false without an entry.</summary>
        public bool RequestDetail(string sid, out AgentDetailMessage? answer)
        {
            answer = null;
            if (!_pack.Agents.TryGetValue(sid, out var d)) return false;
            answer = new AgentDetailMessage
            {
                Sid = d.Sid,
                Title = d.Title,
                Summary = d.Summary,
                Recent = new List<string>(d.Recent),
                WaitingFor = d.WaitingFor,
                Source = d.Source,
            };
            return true;
        }

        /// <summary>Offline: acknowledged as delivered, and the agent reacts (the pack's canned reaction, else a fallback).</summary>
        public IReadOnlyList<ServerMessage>? SendReply(string sid, string text)
        {
            var msgs = new List<ServerMessage> { new AgentReplyAckMessage { Sid = sid, Delivered = true } };
            if (_feedSids.Contains(sid))
                msgs.Add(new FeedAppendMessage { Sid = sid, Entries = new List<FeedEntry> { _feed.Reply(sid, text) } });
            var reaction = _pack.Replies.TryGetValue(sid, out var r) ? r : Texts.ReplyFallbackReaction;
            var agent = _player.Reply(sid, reaction);
            if (agent != null) msgs.Add(agent);
            return WithFeed(msgs);
        }

        /// <summary>Decides at the player's own clock; null when `decision.Id` is not the pending request.</summary>
        public IReadOnlyList<ServerMessage>? Send(ClientDecision decision)
        {
            var msgs = _player.Decide(decision.Id, decision.Behavior == "allow");
            return msgs.Count == 0 ? null : msgs;
        }
    }
}
