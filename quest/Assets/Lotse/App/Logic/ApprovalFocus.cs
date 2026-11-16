#nullable enable
using System.Collections.Generic;
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>
    /// Which request the workspace's approval block shows (U5a), and so the only request a gesture can approve: the
    /// oldest open request of the selected agent, and only while the conversation is on screen and "Später" has not
    /// hidden it. Nothing else is ever approvable: no selected agent, another agent's request, a hidden block or a
    /// window that is not shown all mean no block. "Später" lasts until the user selects an agent again (the same one
    /// too). Pure.
    /// </summary>
    public sealed class ApprovalFocus
    {
        /// <summary>The agent whose conversation is shown, or null.</summary>
        public string? Selected { get; private set; }

        /// <summary>"Später" hid the block until the next selection.</summary>
        public bool PutOff { get; private set; }

        /// <summary>Selecting (again) shows the block again.</summary>
        public void Select(string? sid)
        {
            Selected = sid;
            PutOff = false;
        }

        /// <summary>"Später": the block goes until the user selects an agent again. Sends nothing.</summary>
        public void Later() => PutOff = true;

        /// <summary>The request the block shows, or null; `more` counts the selected agent's other open requests.</summary>
        /// <param name="pending">Open requests, oldest first.</param>
        public PermRequest? Pick(IReadOnlyList<PermRequest> pending, bool viewShown, out int more)
        {
            more = 0;
            if (Selected == null) return null;
            PermRequest? first = null;
            for (var i = 0; i < pending.Count; i++)
            {
                if (pending[i].Sid != Selected) continue;
                if (first == null) first = pending[i];
                else more++;
            }
            if (!viewShown || PutOff) return null;
            return first;
        }
    }

    /// <summary>
    /// The block's countdown until the bridge gives up on a request and the terminal asks by itself (`expiresAt`). The
    /// bridge's clock is not ours, so the time left is its own span (expiresAt − createdAt) minus the time since we first
    /// saw the request. Pure.
    /// </summary>
    public static class Countdown
    {
        /// <summary>The time left in ms (0 when up); null when the request carries no usable span.</summary>
        public static double? LeftMs(PermRequest req, double firstSeenMs, double nowMs)
        {
            var span = req.ExpiresAt - req.CreatedAt;
            if (!(span > 0) || double.IsInfinity(span)) return null;
            var left = span - (nowMs - firstSeenMs);
            return left < 0 ? 0 : left > span ? span : left;
        }

        /// <summary>The share of the time left, 1 when the request opened, 0 when it ran out; null without a span.</summary>
        public static double? Fraction(PermRequest req, double firstSeenMs, double nowMs)
        {
            var left = LeftMs(req, firstSeenMs, nowMs);
            if (left == null) return null;
            return left.Value / (req.ExpiresAt - req.CreatedAt);
        }
    }
}
