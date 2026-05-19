using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace GamePeek
{
    // ── Incoming message POCOs (deserialized from phone JSON) ─────────────────

    /// <summary>Discriminator wrapper — only the <c>type</c> field is read first.</summary>
    [Serializable]
    internal class BaseMessage { public string type; }

    /// <summary>Configuration update sent by the phone app.</summary>
    [Serializable]
    public class ConfigMessage
    {
        public string type;
        public string resolution;  // e.g. "520x1131" — always in portrait order (short × long)
        public int    quality;     // 0–100
        public int    fps;         // frames per second cap
        public bool   landscape;   // true when the device is in landscape orientation
    }

    /// <summary>Touch event from the phone.</summary>
    [Serializable]
    public class TouchMessage
    {
        public string type;
        public string phase;      // began | moved | ended | canceled
        public float  x;          // normalised [0, 1]
        public float  y;          // normalised [0, 1]
        public int    fingerId;
    }

    /// <summary>Gyroscope data from the phone (rad/s).</summary>
    [Serializable]
    public class GyroMessage
    {
        public string type;
        public float x, y, z;
    }

    /// <summary>Accelerometer data from the phone (g-force).</summary>
    [Serializable]
    public class AccelMessage
    {
        public string type;
        public float x, y, z;
    }

    /// <summary>Hello/handshake message sent by the phone on connect.</summary>
    [Serializable]
    public class HelloMessage
    {
        public string type;
        public string client;       // "flutter" | "flutter_webrtc"
        public string tier;         // "free" | "pro"
        public string deviceName;   // human-readable device name, e.g. "John's iPhone 15 Pro"
        public int    width;        // native screen width in pixels (0 if not provided)
        public int    height;       // native screen height in pixels (0 if not provided)
        public string orientation;  // "portrait" | "landscape" (empty if not provided)
    }

    /// <summary>WebRTC SDP answer from the phone (signaling).</summary>
    [Serializable]
    internal class AnswerMessage
    {
        public string type;
        public string sdp;
    }

    /// <summary>ICE candidate from the phone (signaling).</summary>
    [Serializable]
    internal class CandidateMessage
    {
        public string type;
        public string candidate;
        public string sdpMid;
        public int    sdpMLineIndex;
    }

    /// <summary>Ping/pong timestamp message.</summary>
    [Serializable]
    internal class PingPongMessage
    {
        public string type;
        public long   ts;   // Unix epoch milliseconds
    }

    /// <summary>Sent to clients when the Unity editor enters or exits Play Mode.</summary>
    [Serializable]
    internal class PlayModeMessage
    {
        public string type;    // always "playmode"
        public bool   playing; // true = in Play Mode, false = Edit Mode
    }

    // ── WebSocket behaviour (one instance per connected client) ───────────────

    /// <summary>
    /// Per-connection WebSocket behaviour class consumed by websocket-sharp.
    /// Routes incoming text messages to the static event handlers on
    /// <see cref="GamePeekWebSocketServer"/> via thread-safe static events.
    /// </summary>
    internal class GamePeekBehavior : WebSocketBehavior
    {
        // Static events so ConnectionManager can subscribe without holding a
        // reference to individual behaviour instances.
        internal static event Action<string, string> OnClientConnected;     // (sessionId, deviceName)
        internal static event Action<string>          OnClientDisconnected;  // (sessionId)
        internal static event Action<string, string>  OnTextMessage;         // (sessionId, json)

        protected override void OnOpen()
        {
            string deviceName = Context.Headers["X-Device-Name"] ?? "Unknown";
            OnClientConnected?.Invoke(ID, deviceName);
        }

        protected override void OnClose(CloseEventArgs e)
        {
            OnClientDisconnected?.Invoke(ID);
        }

        protected override void OnMessage(MessageEventArgs e)
        {
            if (e.IsText)
                OnTextMessage?.Invoke(ID, e.Data);
            // Binary frames from phone are not expected; ignore silently.
        }

        protected override void OnError(WebSocketSharp.ErrorEventArgs e)
        {
            GamePeekConstants.LogWarning($"[WS] Client {ID} error: {e.Message}");
        }

        /// <summary>Sends a text message to THIS specific client session.</summary>
        internal void SendText(string json) => Send(json);
    }

    // ── Server wrapper ────────────────────────────────────────────────────────

    /// <summary>
    /// Wraps a <c>websocket-sharp</c> <see cref="WebSocketSharp.Server.WebSocketServer"/>
    /// and exposes a clean API for the rest of GamePeek.
    /// <para>
    /// <b>Thread safety:</b> <see cref="BroadcastFrame"/> and
    /// <see cref="SendToSession"/> may be called from any thread.
    /// All events are raised on the websocket-sharp internal thread pool
    /// and must be marshalled to the Unity main thread by the subscriber
    /// (see <see cref="ConnectionManager"/>).
    /// </para>
    /// </summary>
    public sealed class GamePeekWebSocketServer : IDisposable
    {
        // ── Events (raised on websocket-sharp background threads) ─────────────

        /// <summary>Raised when a new client connects. Args: (sessionId, deviceName).</summary>
        public event Action<string, string> ClientConnected;

        /// <summary>Raised when a client disconnects. Args: (sessionId).</summary>
        public event Action<string>         ClientDisconnected;

        /// <summary>Raised when a configuration update is received. Args: (sessionId, msg).</summary>
        public event Action<string, ConfigMessage>  ConfigReceived;

        /// <summary>Raised when a touch event is received. Args: (sessionId, msg).</summary>
        public event Action<string, TouchMessage>   TouchReceived;

        /// <summary>Raised when gyroscope data is received. Args: (sessionId, msg).</summary>
        public event Action<string, GyroMessage>    GyroReceived;

        /// <summary>Raised when accelerometer data is received. Args: (sessionId, msg).</summary>
        public event Action<string, AccelMessage>   AccelReceived;

        // ── WebRTC signaling events ───────────────────────────────────────────

        /// <summary>Raised when a hello handshake is received. Args: (sessionId, hello).</summary>
        public event Action<string, HelloMessage> HelloReceived;

        /// <summary>Raised when an SDP answer arrives from the phone. Args: (sessionId, sdp).</summary>
        public event Action<string, string>  AnswerReceived;

        /// <summary>
        /// Raised when an ICE candidate arrives from the phone.
        /// Args: (sessionId, candidate, sdpMid, sdpMLineIndex).
        /// </summary>
        public event Action<string, string, string, int> CandidateReceived;

        // ── RTT events ────────────────────────────────────────────────────────

        /// <summary>Raised when a pong reply arrives. Args: timestamp that was echoed back.</summary>
        public event Action<long> PongReceived;

        // ── Internal state ────────────────────────────────────────────────────
        private WebSocketSharp.Server.WebSocketServer _server;
        private readonly int  _port;
        private volatile bool _running;
        private bool _eventsHooked;

        // ── Constructor ───────────────────────────────────────────────────────
        /// <summary>Creates the server wrapper. Call <see cref="Start"/> to bind the socket.</summary>
        /// <param name="port">TCP port to listen on (default 7777).</param>
        public GamePeekWebSocketServer(int port = GamePeekConstants.DefaultPort) => _port = port;

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>Binds the socket and starts accepting connections.</summary>
        public void Start()
        {
            if (_running) return;

            try
            {
                _server = new WebSocketSharp.Server.WebSocketServer(_port);
                _server.AddWebSocketService<GamePeekBehavior>("/");
                HookBehaviorEvents();
                _server.Start();
                _running = true;
            }
            catch
            {
                Stop();
                throw;
            }

            GamePeekConstants.Log($"[WS] Server listening on port {_port}.");
        }

        /// <summary>Broadcasts a raw JPEG frame to all connected clients as a binary message.</summary>
        /// <param name="jpegBytes">Complete JPEG byte array representing one frame.</param>
        public void BroadcastFrame(byte[] jpegBytes)
        {
            if (!_running || jpegBytes == null || jpegBytes.Length == 0) return;
            _server?.WebSocketServices["/"]?.Sessions?.Broadcast(jpegBytes);
        }

        /// <summary>Broadcasts a UTF-8 text message to all connected clients.</summary>
        public void BroadcastText(string json)
        {
            if (!_running || string.IsNullOrEmpty(json)) return;
            _server?.WebSocketServices["/"]?.Sessions?.Broadcast(json);
        }

        /// <summary>
        /// Sends a UTF-8 text message to a single specific session.
        /// Safe to call from any thread.
        /// </summary>
        /// <param name="sessionId">websocket-sharp session ID.</param>
        /// <param name="json">JSON payload to send.</param>
        public void SendToSession(string sessionId, string json)
        {
            if (!_running || string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(json)) return;
            try
            {
                _server?.WebSocketServices["/"]?.Sessions?.SendTo(json, sessionId);
            }
            catch (Exception ex)
            {
                GamePeekConstants.LogWarning($"[WS] SendToSession failed: {ex.Message}");
            }
        }

        /// <summary>Forcibly closes a specific client session.</summary>
        public void CloseSession(string sessionId)
        {
            if (!_running || string.IsNullOrEmpty(sessionId)) return;
            try
            {
                _server?.WebSocketServices["/"]?.Sessions
                    ?.CloseSession(sessionId, WebSocketSharp.CloseStatusCode.Normal, "use_jpeg");
            }
            catch { }
        }

        /// <summary>Returns the number of currently connected clients.</summary>
        public int ConnectedCount =>
            _server?.WebSocketServices["/"]?.Sessions?.Count ?? 0;

        /// <summary>Stops the server and releases resources.</summary>
        public void Stop()
        {
            if (!_running && !_eventsHooked && _server == null) return;
            _running = false;

            UnhookBehaviorEvents();

            try
            {
                _server?.Stop();
            }
            catch (Exception ex)
            {
                GamePeekConstants.LogWarning($"[WS] Server stop failed: {ex.Message}");
            }

            _server = null;
            GamePeekConstants.Log("[WS] Server stopped.");
        }

        private void HookBehaviorEvents()
        {
            if (_eventsHooked) return;

            GamePeekBehavior.OnClientConnected    -= HandleConnect;
            GamePeekBehavior.OnClientDisconnected -= HandleDisconnect;
            GamePeekBehavior.OnTextMessage        -= HandleTextMessage;

            GamePeekBehavior.OnClientConnected    += HandleConnect;
            GamePeekBehavior.OnClientDisconnected += HandleDisconnect;
            GamePeekBehavior.OnTextMessage        += HandleTextMessage;

            _eventsHooked = true;
        }

        private void UnhookBehaviorEvents()
        {
            if (!_eventsHooked) return;

            GamePeekBehavior.OnClientConnected    -= HandleConnect;
            GamePeekBehavior.OnClientDisconnected -= HandleDisconnect;
            GamePeekBehavior.OnTextMessage        -= HandleTextMessage;

            _eventsHooked = false;
        }

        /// <inheritdoc/>
        public void Dispose() => Stop();

        // ── Private handlers ──────────────────────────────────────────────────

        private void HandleConnect(string sessionId, string deviceName)
            => ClientConnected?.Invoke(sessionId, deviceName);

        private void HandleDisconnect(string sessionId)
            => ClientDisconnected?.Invoke(sessionId);

        private void HandleTextMessage(string sessionId, string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;

            GamePeekConstants.Log($"[WS] Received: {json}");

            try
            {
                var baseMsg = UnityEngine.JsonUtility.FromJson<BaseMessage>(json);
                if (baseMsg == null) return;

                switch (baseMsg.type)
                {
                    case "hello":
                        var hello = UnityEngine.JsonUtility.FromJson<HelloMessage>(json);
                        HelloReceived?.Invoke(sessionId, hello);
                        break;

                    case "config":
                        ConfigReceived?.Invoke(sessionId,
                            UnityEngine.JsonUtility.FromJson<ConfigMessage>(json));
                        break;

                    case "touch":
                        TouchReceived?.Invoke(sessionId,
                            UnityEngine.JsonUtility.FromJson<TouchMessage>(json));
                        break;

                    case "gyro":
                        GyroReceived?.Invoke(sessionId,
                            UnityEngine.JsonUtility.FromJson<GyroMessage>(json));
                        break;

                    case "accel":
                        AccelReceived?.Invoke(sessionId,
                            UnityEngine.JsonUtility.FromJson<AccelMessage>(json));
                        break;

                    // ── WebRTC signaling ──────────────────────────────────────
                    case "answer":
                        var ans = UnityEngine.JsonUtility.FromJson<AnswerMessage>(json);
                        AnswerReceived?.Invoke(sessionId, ans.sdp);
                        break;

                    case "candidate":
                        var cand = UnityEngine.JsonUtility.FromJson<CandidateMessage>(json);
                        CandidateReceived?.Invoke(sessionId, cand.candidate, cand.sdpMid, cand.sdpMLineIndex);
                        break;

                    // ── RTT ping/pong ─────────────────────────────────────────
                    case "ping":
                        // Echo back as pong with the same timestamp.
                        var pingMsg = UnityEngine.JsonUtility.FromJson<PingPongMessage>(json);
                        SendToSession(sessionId,
                            UnityEngine.JsonUtility.ToJson(
                                new PingPongMessage { type = "pong", ts = pingMsg.ts }));
                        break;

                    case "pong":
                        var pongMsg = UnityEngine.JsonUtility.FromJson<PingPongMessage>(json);
                        PongReceived?.Invoke(pongMsg.ts);
                        break;

                    default:
                        GamePeekConstants.LogWarning($"[WS] Unknown message type: {baseMsg.type}");
                        break;
                }
            }
            catch (Exception ex)
            {
                GamePeekConstants.LogWarning($"[WS] Failed to parse message: {ex.Message}\n{json}");
            }
        }
    }
}
