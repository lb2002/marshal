using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using NativeWebSocket;
using UnityEngine;

namespace Lotse.App
{
    /// <summary>
    /// ILinkSocket over NativeWebSocket (System.Net.WebSockets on Android). Its open, error and close
    /// callbacks may come from async continuations, so they only record; Pump raises everything on the
    /// main thread once per frame: the open and the frames (DispatchMessageQueue first), then the close, so
    /// frames that arrived before the close always come before it.
    /// </summary>
    public sealed class NativeLinkSocket : ILinkSocket
    {
        readonly WebSocket _ws;
        readonly object _lock = new object();
        readonly Queue<Action> _events = new Queue<Action>();
        readonly List<Action> _scratch = new List<Action>();
        bool _closedRaised;
        int? _closeCode;

        public NativeLinkSocket(string url)
        {
            _ws = new WebSocket(url);
            _ws.OnOpen += () => Enqueue(() => Opened?.Invoke());
            _ws.OnMessage += bytes =>
            {
                var text = Encoding.UTF8.GetString(bytes);
                Enqueue(() => Received?.Invoke(text));
            };
            // the library's error text may carry the address: only that there was one
            _ws.OnError += _ => Debug.LogWarning("[Lotse] socket error");
            _ws.OnClose += code =>
            {
                lock (_lock)
                    if (_closeCode == null) _closeCode = (int)code; // NativeWebSocket may report a failed connect twice
            };
        }

        public event Action Opened;
        public event Action<string> Received;
        public event Action<int> Closed;

        public static ILinkSocket Open(string url) => new NativeLinkSocket(url);

        public void Connect()
        {
            // Connect() runs until the socket closes: start it, never await it here
            Observe(_ws.Connect(), "connect");
        }

        public bool Send(string text)
        {
            if (_ws.State != WebSocketState.Open)
            {
                Debug.LogWarning("[Lotse] socket not open; frame dropped");
                return false;
            }
            Observe(_ws.SendText(text), "send");
            return true;
        }

        public void Close()
        {
            try
            {
                if (_ws.State == WebSocketState.Open) Observe(_ws.Close(), "close");
                else _ws.CancelConnection();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Lotse] socket close: " + Kind(e));
            }
        }

        public void Pump()
        {
            int? close;
            lock (_lock) close = _closeCode; // read first: every frame received before the close is queued by now
            _ws.DispatchMessageQueue(); // OnMessage -> queue
            lock (_lock)
            {
                _scratch.Clear();
                _scratch.AddRange(_events);
                _events.Clear();
            }
            foreach (var e in _scratch) e();
            if (close != null && !_closedRaised)
            {
                _closedRaised = true;
                Closed?.Invoke(close.Value);
            }
        }

        void Enqueue(Action a)
        {
            lock (_lock) _events.Enqueue(a);
        }

        /// <summary>
        /// What kind of failure, for the log: the exception's type name and, for a socket error anywhere in its chain, the
        /// SocketErrorCode's name (ConnectionRefused, TimedOut, HostUnreachable…). Names only: a message may carry the address.
        /// </summary>
        public static string Kind(Exception e)
        {
            if (e == null) return "unknown";
            var innermost = e;
            for (var x = e; x != null; x = x is AggregateException a && a.InnerExceptions.Count > 0 ? a.InnerExceptions[0] : x.InnerException)
            {
                innermost = x;
                if (x is System.Net.Sockets.SocketException s) return "SocketException " + s.SocketErrorCode;
            }
            return innermost.GetType().Name;
        }

        static void Observe(Task task, string what)
        {
            task?.ContinueWith(t => Debug.LogWarning($"[Lotse] socket {what} failed: {Kind(t.Exception)}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
