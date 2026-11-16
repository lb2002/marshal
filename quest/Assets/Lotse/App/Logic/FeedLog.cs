#nullable enable
using System.Collections.Generic;
using Lotse.Core;

namespace Lotse.App
{
    /// <summary>Where an agent's live feed stands.</summary>
    public enum FeedState { None, Loading, Ready, Unavailable }

    /// <summary>
    /// The live feeds the headset has seen (contract 2026-11-12-contract-live-feed.md), per agent, in arrival order
    /// (the bridge sends them oldest first). Entries are de-duplicated by id, so a snapshot after a re-subscribe that
    /// repeats what is known adds only what is new. At most MaxEntries per agent (the oldest go first) and MaxAgents
    /// agents (the least recently changed go first). Display text only, already capped by Untrusted. Pure.
    /// </summary>
    public sealed class FeedLog
    {
        public const int MaxEntries = 200;
        public const int MaxAgents = 8;

        sealed class Feed
        {
            public readonly List<FeedEntry> Entries = new List<FeedEntry>();
            public readonly HashSet<string> Ids = new HashSet<string>();
            public FeedState State = FeedState.None;
            public int Version;
            public long Touched;
        }

        static readonly IReadOnlyList<FeedEntry> NoEntries = new List<FeedEntry>();
        readonly Dictionary<string, Feed> _feeds = new Dictionary<string, Feed>();
        long _clock;

        public IReadOnlyList<FeedEntry> Entries(string sid) => _feeds.TryGetValue(sid, out var f) ? f.Entries : NoEntries;
        public FeedState StateOf(string sid) => _feeds.TryGetValue(sid, out var f) ? f.State : FeedState.None;
        /// <summary>Changes whenever the agent's entries or state change (the view redraws on a new version only).</summary>
        public int VersionOf(string sid) => _feeds.TryGetValue(sid, out var f) ? f.Version : 0;

        /// <summary>Subscribed: Loading until the snapshot comes (what is known stays shown).</summary>
        public void Subscribed(string sid)
        {
            var f = Get(sid);
            if (f.State != FeedState.Ready) Set(f, FeedState.Loading);
        }

        /// <summary>The bridge cannot give this feed (unknown agent, an older bridge): "Kein Verlauf verfügbar" unless entries are known.</summary>
        public void Failed(string sid)
        {
            var f = Get(sid);
            Set(f, f.Entries.Count > 0 ? FeedState.Ready : FeedState.Unavailable);
        }

        /// <summary>A snapshot or an append: the entries not seen yet are added in order; returns how many.</summary>
        public int Merge(string sid, IReadOnlyList<FeedEntry> entries)
        {
            var f = Get(sid);
            var added = 0;
            foreach (var e in entries)
            {
                if (e == null || string.IsNullOrEmpty(e.Id) || !f.Ids.Add(e.Id)) continue;
                f.Entries.Add(e);
                added++;
            }
            while (f.Entries.Count > MaxEntries)
            {
                f.Ids.Remove(f.Entries[0].Id);
                f.Entries.RemoveAt(0);
            }
            if (added > 0 || f.State != FeedState.Ready)
            {
                f.State = FeedState.Ready;
                f.Version++;
            }
            return added;
        }

        public void Clear() => _feeds.Clear();

        Feed Get(string sid)
        {
            if (!_feeds.TryGetValue(sid, out var f))
            {
                if (_feeds.Count >= MaxAgents)
                {
                    string? oldest = null;
                    var at = long.MaxValue;
                    foreach (var kv in _feeds)
                        if (kv.Value.Touched < at)
                        {
                            at = kv.Value.Touched;
                            oldest = kv.Key;
                        }
                    if (oldest != null) _feeds.Remove(oldest);
                }
                f = new Feed();
                _feeds[sid] = f;
            }
            f.Touched = ++_clock;
            return f;
        }

        static void Set(Feed f, FeedState state)
        {
            if (f.State == state) return;
            f.State = state;
            f.Version++;
        }
    }

    /// <summary>
    /// The feed list's scroll position, newest at the bottom. `Bottom` is one past the last entry shown; the view lays
    /// the entries out upward from there. While it follows the end, new entries scroll in by themselves; once the user
    /// scrolls up it stays put and counts what came below (NewBelow) until the user scrolls back down. Drags move the
    /// content with the hand: a hand moving down brings older entries in. Pure.
    /// </summary>
    public sealed class FeedScroll
    {
        int _count;
        int _seen;
        float _drag;

        public int Bottom { get; private set; }
        public bool FollowsEnd { get; private set; } = true;
        /// <summary>Entries that arrived while the user was scrolled up (not the ones scrolled past).</summary>
        public int NewBelow => FollowsEnd ? 0 : System.Math.Max(0, _count - System.Math.Max(_seen, Bottom));

        /// <summary>The list now has `count` entries.</summary>
        public void Update(int count)
        {
            _count = count;
            if (FollowsEnd || Bottom > count) Bottom = count;
            if (Bottom == count) FollowsEnd = true;
        }

        /// <summary>Moves by whole entries: positive shows newer ones (down the list), negative older ones.</summary>
        public void Scroll(int entries)
        {
            if (entries == 0) return;
            if (FollowsEnd) _seen = _count; // what had come when the user left the end
            Bottom = System.Math.Max(System.Math.Min(_count, 1), System.Math.Min(_count, Bottom + entries));
            FollowsEnd = Bottom >= _count;
        }

        /// <summary>A drag of `dyMeters` (up positive) with entries about `stepMeters` tall: the content follows the hand.</summary>
        public void Drag(float dyMeters, float stepMeters)
        {
            if (stepMeters <= 0f) return;
            _drag += dyMeters / stepMeters;
            var whole = (int)_drag; // toward zero
            if (whole == 0) return;
            _drag -= whole;
            Scroll(whole);
        }

        /// <summary>A drag ended: what was left of a step is dropped.</summary>
        public void EndDrag() => _drag = 0f;

        public void ScrollToEnd()
        {
            Bottom = _count;
            FollowsEnd = true;
        }

        /// <summary>Another agent: back to following the end.</summary>
        public void Reset()
        {
            _count = 0;
            _seen = 0;
            Bottom = 0;
            _drag = 0f;
            FollowsEnd = true;
        }
    }
}

namespace Lotse.App
{
    /// <summary>What a feed comes from (DemoSession: the bridge live, the pack offline).</summary>
    public interface IFeedSource
    {
        /// <summary>Starts one agent's feed; false when it cannot (no live link, an older bridge, no pack entry).</summary>
        bool SubscribeFeed(string sid);
        void UnsubscribeFeed(string sid);
    }

    /// <summary>
    /// The one feed a view follows (U4b; reusable for U5): opening an agent subscribes to its feed and unsubscribes
    /// the one before, closing unsubscribes, a new connection subscribes again (the bridge ends every feed with the old
    /// one). What arrives is merged into the FeedLog (de-duplicated by id). Pure: the source and the log are injected.
    /// </summary>
    public sealed class FeedSubscription
    {
        readonly FeedLog _log;
        IFeedSource? _source;

        public FeedSubscription(FeedLog log)
        {
            _log = log;
        }

        /// <summary>The agent whose feed is followed now, or null.</summary>
        public string? Sid { get; private set; }

        /// <summary>A new session (or none): nothing is followed; what the old one sent is forgotten.</summary>
        public void Use(IFeedSource? source)
        {
            Sid = null;
            _source = source;
            _log.Clear();
        }

        /// <summary>Follows `sid` (the one before is unsubscribed); false when its feed cannot be had (then it says so).</summary>
        public bool Open(string sid)
        {
            if (Sid != null && Sid != sid) Close();
            Sid = sid;
            _log.Subscribed(sid);
            if (_source != null && _source.SubscribeFeed(sid)) return true;
            _log.Failed(sid);
            return false;
        }

        public void Close()
        {
            if (Sid == null) return;
            _source?.UnsubscribeFeed(Sid);
            Sid = null;
        }

        /// <summary>A new connection: the followed feed is subscribed again (what is known stays, the repeat is de-duplicated).</summary>
        public bool Reconnected() => Sid != null && Open(Sid);

        /// <summary>Entries arrived (any agent's); returns how many were new.</summary>
        public int Arrived(string sid, System.Collections.Generic.IReadOnlyList<Lotse.Core.FeedEntry> entries) => _log.Merge(sid, entries);

        /// <summary>The source cannot give `sid`'s feed.</summary>
        public void Failed(string sid) => _log.Failed(sid);
    }
}
