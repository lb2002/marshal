#nullable enable
using System;
using System.Collections.Generic;
using Lotse.App;

namespace Lotse.App.Tests
{
    /// <summary>A socket the test drives by hand: it records what the client sends and raises what the bridge would.</summary>
    sealed class FakeSocket : ILinkSocket
    {
        public readonly string Url;
        public readonly List<string> Sent = new List<string>();
        public bool ConnectCalled;
        public bool ClosedByClient;
        /// <summary>False: the socket is not open for sending (an app stall, a half-open link); Send refuses.</summary>
        public bool Accepts = true;

        public FakeSocket(string url)
        {
            Url = url;
        }

        public event Action? Opened;
        public event Action<string>? Received;
        public event Action<int>? Closed;

        /// <summary>The close code a socket raises from inside Connect (a connection refused at once), or null.</summary>
        public int? ClosesInConnect;

        public void Connect()
        {
            ConnectCalled = true;
            if (ClosesInConnect.HasValue) Closed?.Invoke(ClosesInConnect.Value);
        }
        public bool Send(string text)
        {
            if (!Accepts) return false;
            Sent.Add(text);
            return true;
        }
        public void Close() => ClosedByClient = true;
        public void Pump() { }

        // the bridge's side
        public void Open() => Opened?.Invoke();
        public void Receive(string json) => Received?.Invoke(json);
        public void Drop(int code) => Closed?.Invoke(code);
    }

    /// <summary>Hands out a new FakeSocket per connection attempt.</summary>
    sealed class FakeNet
    {
        public readonly List<FakeSocket> Sockets = new List<FakeSocket>();
        public FakeSocket Last => Sockets[Sockets.Count - 1];

        /// <summary>Every new socket closes with this code from inside Connect.</summary>
        public int? ClosesInConnect;

        public ILinkSocket Open(string url)
        {
            var s = new FakeSocket(url) { ClosesInConnect = ClosesInConnect };
            Sockets.Add(s);
            return s;
        }
    }

    sealed class MemoryStore : IKeyValueStore
    {
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>();

        public string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => Values[key] = value;
        public void Delete(string key) => Values.Remove(key);
    }

    /// <summary>Frames the bridge sends (shapes as in bridge/src/protocol.ts).</summary>
    static class Bridge
    {
        public const string Host = "192.168.1.20";
        public const string EmptySnapshot = "{\"type\":\"snapshot\",\"agents\":[],\"pending\":[],\"explains\":{}}";

        public static string Paired(string session) => "{\"type\":\"paired\",\"session\":\"" + session + "\"}";

        public const string Pong = "{\"type\":\"pong\"}";

        public static string Error(string error) => "{\"type\":\"error\",\"error\":\"" + error + "\"}";

        public static string Request(string id, string risk = "safe", bool truncated = false, string command = "ls -la", string sid = "s1") =>
            "{\"id\":\"" + id + "\",\"sid\":\"" + sid + "\",\"tool\":\"Bash\",\"command\":\"" + command + "\",\"truncated\":" +
            (truncated ? "true" : "false") + ",\"paths\":[],\"risk\":{\"risk\":\"" + risk +
            "\",\"reason\":\"test\"},\"createdAt\":1000,\"expiresAt\":91000}";

        public static string Explain(string text, string reply) =>
            "{\"text\":\"" + text + "\",\"replies\":[\"" + reply + "\"],\"source\":\"rules\"}";

        public static string PermOpen(string id, string risk = "safe", string reply = "Nein, lass das.", string sid = "s1") =>
            "{\"type\":\"perm_open\",\"request\":" + Request(id, risk, sid: sid) + ",\"explain\":" + Explain("Listet Dateien.", reply) + "}";

        public static string PermExplain(string id, string text, string aiText) =>
            "{\"type\":\"perm_explain\",\"id\":\"" + id + "\",\"explain\":{\"text\":\"" + text + "\",\"aiText\":\"" + aiText +
            "\",\"replies\":[\"Bitte nicht.\"],\"source\":\"ai\"}}";

        public static string PermClosed(string id, string reason) =>
            "{\"type\":\"perm_closed\",\"id\":\"" + id + "\",\"reason\":\"" + reason + "\"}";

        public static string Agent(string sid, string status) =>
            "{\"type\":\"agent\",\"agent\":{\"sid\":\"" + sid + "\",\"name\":\"Alpha\",\"status\":\"" + status +
            "\",\"lastAction\":\"\",\"since\":0,\"files\":[],\"waitingMsTotal\":0}}";
    }
}
