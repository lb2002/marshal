#nullable enable
using System.Collections.Generic;
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>What the detail panel can show for an agent right now.</summary>
    public enum DetailState
    {
        /// <summary>Nothing came (or can come): "Keine Details".</summary>
        None,
        /// <summary>Asked, the answer is still on its way.</summary>
        Loading,
        /// <summary>A detail is there (the latest one).</summary>
        Ready,
    }

    /// <summary>
    /// The agent details of one session, by sid. Asks at most once per sid every 3.5 s (a little over the bridge's own
    /// 3 s limit, so the bridge never drops a request of ours), keeps the latest answer (a rules detail is replaced by
    /// the AI's follow-up, also while the panel is closed), and gives up on an answer after 5 s ("Keine Details"),
    /// after which the agent may be asked again: a request never locks up. Every detail is capped and
    /// its invisible characters shown on the way in (Untrusted.Detail). Pure, time injected.
    /// </summary>
    public sealed class AgentDetails
    {
        /// <summary>At most one request per sid this often (the bridge drops any within 3 s).</summary>
        public const double RequestEveryMs = 3500;
        /// <summary>An answer that has not come after this counts as none.</summary>
        public const double AnswerTimeoutMs = 5000;
        /// <summary>At most this many agents' details are kept (the oldest go first).</summary>
        public const int MaxAgents = 32;

        readonly Dictionary<string, AgentDetailMessage> _details = new Dictionary<string, AgentDetailMessage>();
        readonly Dictionary<string, double> _askedAt = new Dictionary<string, double>();
        readonly HashSet<string> _noAnswer = new HashSet<string>();
        /// <summary>The sids with a kept detail, oldest first (for the bound).</summary>
        readonly List<string> _order = new List<string>();

        /// <summary>True when a request for `sid` may go out now (and records it); false within 3.5 s of the last.</summary>
        public bool TryRequest(string sid, double nowMs)
        {
            if (_askedAt.Count > 4 * MaxAgents) _askedAt.Clear(); // a bound: only the rate limit is forgotten
            if (_askedAt.TryGetValue(sid, out var at) && nowMs - at < RequestEveryMs) return false;
            _askedAt[sid] = nowMs;
            _noAnswer.Remove(sid);
            return true;
        }

        /// <summary>The source cannot ask for this agent (no pack entry, no live link): nothing will come.</summary>
        public void NoAnswer(string sid) => _noAnswer.Add(sid);

        /// <summary>Stores an arrived detail, capped (it replaces an older one for the same sid).</summary>
        public void Put(AgentDetailMessage detail)
        {
            Untrusted.Detail(detail); // idempotent: a detail the session capped already stays as it is
            if (!_details.ContainsKey(detail.Sid))
            {
                _order.Add(detail.Sid);
                while (_order.Count > MaxAgents)
                {
                    var oldest = _order[0];
                    _order.RemoveAt(0);
                    _details.Remove(oldest);
                    _askedAt.Remove(oldest);
                    _noAnswer.Remove(oldest);
                }
            }
            _details[detail.Sid] = detail;
        }

        public int Count => _details.Count;

        public AgentDetailMessage? Get(string sid) => _details.TryGetValue(sid, out var d) ? d : null;

        public DetailState StateOf(string sid, double nowMs)
        {
            if (_details.ContainsKey(sid)) return DetailState.Ready;
            if (_noAnswer.Contains(sid) || !_askedAt.TryGetValue(sid, out var at)) return DetailState.None;
            return nowMs - at < AnswerTimeoutMs ? DetailState.Loading : DetailState.None;
        }

        public void Clear()
        {
            _details.Clear();
            _askedAt.Clear();
            _noAnswer.Clear();
            _order.Clear();
        }
    }
}
