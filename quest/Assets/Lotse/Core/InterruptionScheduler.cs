#nullable enable
using System.Collections.Generic;

namespace Lotse.Core
{
    /// <summary>
    /// Decides which pending request's card is shown. Pure, time injected. A request's waiting time
    /// counts from the first Pick that saw it (the bridge's createdAt is on another clock), so Pick must
    /// be called every frame (also while a card is open) or the waiting time starts late. A caller that
    /// knows better (a reconnect) can give the first-seen time per new id.
    /// </summary>
    public sealed class InterruptionScheduler
    {
        public const double QuietMs = 2000;
        public const double EscalateMs = 45000;

        readonly Dictionary<string, double> _firstSeen = new Dictionary<string, double>();
        readonly List<string> _gone = new List<string>();

        /// <param name="pending">Open order, oldest first.</param>
        /// <param name="lastGestureEventMs">Time of the last gesture event, or negative infinity for none.</param>
        /// <param name="firstSeenMs">Optional, asked only for ids not seen before; a null result means "now".</param>
        /// <returns>The request whose card to show now, or null.</returns>
        /// <remarks>Runs every frame: no allocation (index loops over the interface, a reused scratch list).</remarks>
        public PermRequest? Pick(double nowMs, bool cardOpen, double lastGestureEventMs, IReadOnlyList<PermRequest> pending,
            System.Func<string, double?>? firstSeenMs = null)
        {
            Forget(pending);
            for (var i = 0; i < pending.Count; i++)
            {
                var p = pending[i];
                if (!_firstSeen.ContainsKey(p.Id)) _firstSeen[p.Id] = firstSeenMs?.Invoke(p.Id) ?? nowMs;
            }

            if (cardOpen || pending.Count == 0) return null;

            for (var i = 0; i < pending.Count; i++)
                if (nowMs - _firstSeen[pending[i].Id] >= EscalateMs) return pending[i];

            if (nowMs - lastGestureEventMs < QuietMs) return null;
            return pending[0];
        }

        void Forget(IReadOnlyList<PermRequest> pending)
        {
            if (_firstSeen.Count == 0) return;
            _gone.Clear();
            foreach (var id in _firstSeen.Keys)
                if (!Contains(pending, id)) _gone.Add(id);
            for (var i = 0; i < _gone.Count; i++) _firstSeen.Remove(_gone[i]);
        }

        static bool Contains(IReadOnlyList<PermRequest> pending, string id)
        {
            for (var i = 0; i < pending.Count; i++)
                if (pending[i].Id == id) return true;
            return false;
        }
    }
}
