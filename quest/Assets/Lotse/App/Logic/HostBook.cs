#nullable enable
using System;
using System.Collections.Generic;

namespace Lotse.App
{
    /// <summary>
    /// The bridge addresses the user paired with (the last three, newest first), the session token per
    /// address, and whether the bridge has refused that token (then it is not tried again: every refused
    /// hello counts towards the bridge's lock-out of the address). Persisted in an IKeyValueStore
    /// (PlayerPrefs on the headset).
    /// </summary>
    public sealed class HostBook
    {
        public const int MaxRecent = 3;
        public const string HostsKey = "lotse.hosts";
        public const string SessionPrefix = "lotse.session.";
        public const string RefusedPrefix = "lotse.refused.";

        readonly IKeyValueStore _store;

        public HostBook(IKeyValueStore store)
        {
            _store = store;
        }

        /// <summary>Newest first, at most three, only valid addresses.</summary>
        public IReadOnlyList<string> Recent
        {
            get
            {
                var list = new List<string>();
                var raw = _store.Get(HostsKey);
                if (string.IsNullOrEmpty(raw)) return list;
                foreach (var h in raw!.Split('\n'))
                    if (IsValidHost(h) && !list.Contains(h) && list.Count < MaxRecent) list.Add(h);
                return list;
            }
        }

        /// <summary>Moves `host` to the front of the recent list.</summary>
        public void Remember(string host)
        {
            if (!IsValidHost(host)) return;
            var list = new List<string> { host };
            foreach (var h in Recent)
                if (h != host && list.Count < MaxRecent) list.Add(h);
            _store.Set(HostsKey, string.Join("\n", list));
        }

        public string? SessionFor(string host)
        {
            var s = _store.Get(SessionPrefix + host);
            return string.IsNullOrEmpty(s) ? null : s;
        }

        /// <summary>A new token from `paired`; it clears an earlier refusal.</summary>
        public void SaveSession(string host, string token)
        {
            _store.Set(SessionPrefix + host, token);
            _store.Delete(RefusedPrefix + host);
        }

        public void ForgetSession(string host)
        {
            _store.Delete(SessionPrefix + host);
            _store.Delete(RefusedPrefix + host);
        }

        /// <summary>The bridge refused the stored token for `host` (NeedsCode): it is not resumed again.</summary>
        public void MarkSessionRefused(string host) => _store.Set(RefusedPrefix + host, "1");

        public bool SessionRefused(string host) => _store.Get(RefusedPrefix + host) != null;

        /// <summary>A dotted IPv4 address as typed on the keypad: four parts 0-255, no leading zeros.</summary>
        public static bool IsValidHost(string? host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            var parts = host!.Split('.');
            if (parts.Length != 4) return false;
            foreach (var p in parts)
            {
                if (p.Length == 0 || p.Length > 3) return false;
                foreach (var c in p)
                    if (c < '0' || c > '9') return false;
                if (p.Length > 1 && p[0] == '0') return false;
                if (int.Parse(p) > 255) return false;
            }
            return true;
        }
    }
}
