#nullable enable
using System;

namespace Lotse.App
{
    /// <summary>
    /// One WebSocket connection attempt, as LiveSource needs it. The Unity side wraps NativeWebSocket;
    /// tests use a fake. Events are raised on the caller's thread, from Pump (or from Connect/Close).
    /// </summary>
    public interface ILinkSocket
    {
        event Action? Opened;
        /// <summary>One text frame.</summary>
        event Action<string>? Received;
        /// <summary>The connection is gone. 1006 when it never opened or broke without a close frame.</summary>
        event Action<int>? Closed;

        void Connect();
        /// <summary>False when the socket cannot send now (not open); nothing went out then.</summary>
        bool Send(string text);
        void Close();
        /// <summary>Raises what arrived since the last call. Called once per frame.</summary>
        void Pump();
    }

    /// <summary>Small persistent key/value strings (PlayerPrefs on the headset, a dictionary in tests).</summary>
    public interface IKeyValueStore
    {
        string? Get(string key);
        void Set(string key, string value);
        void Delete(string key);
    }
}
