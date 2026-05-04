using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEditor;
using UnityEngine;

using WebSocketSharp;

namespace UniPeek
{
    // ── State & data model ────────────────────────────────────────────────────

    /// <summary>Connection state of the UniPeek plugin.</summary>
    public enum ConnectionState
    {
        /// <summary>Server is stopped; no clients connected.</summary>
        Disconnected,
        /// <summary>Server is running and advertising via mDNS; waiting for a device.</summary>
        Advertising,
        /// <summary>At least one device is connected and streaming is active.</summary>
        Connected,
        /// <summary>Attempting an outbound (reverse) connection to the phone.</summary>
        ReverseConnecting,
    }

    /// <summary>Describes a connected device.</summary>
    public sealed class DeviceInfo
    {
        /// <summary>websocket-sharp session identifier.</summary>
        public string SessionId  { get; init; }
        /// <summary>Device name reported by the phone via the <c>X-Device-Name</c> header.</summary>
        public string DeviceName { get; init; }
        /// <summary>IP address of the remote device (empty string if unknown).</summary>
        public string IPAddress  { get; init; }
        /// <summary>UTC time when the device connected.</summary>
        public DateTime ConnectedAt { get; init; }
        /// <summary>Whether the device has a Pro tier subscription.</summary>
        public bool IsPro { get; init; }
    }

    // ── Streaming configuration ───────────────────────────────────────────────

    /// <summary>All runtime-adjustable streaming parameters.</summary>
    public sealed class StreamConfig
    {
        public int Width          { get; set; } = 1280;
        public int Height         { get; set; } = 720;
        public int Quality        { get; set; } = 75;
        public int FpsCap         { get; set; } = 30;
        public int MaxBitrateKbps { get; set; } = UniPeekConstants.DefaultWebRtcMaxBitrateKbps;
        public string WebRtcStunUrl { get; set; } = string.Empty;
    }

    // ── Manager ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Central orchestrator for UniPeek.  Manages the full lifecycle of the
    /// WebSocket server, mDNS advertiser, frame capture, frame encoder, and
    /// (when the <c>com.unity.webrtc</c> package is present) the WebRTC streamer.
    /// <para>
    /// All state-change events are guaranteed to fire on the Unity <em>main
    /// thread</em> via a <see cref="ConcurrentQueue{T}"/> drained on each
    /// <see cref="EditorApplication.update"/> tick.
    /// </para>
    /// </summary>
    public sealed class ConnectionManager : IDisposable
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        private static ConnectionManager _instance;
        /// <summary>Returns the single shared instance, creating it if necessary.</summary>
        public static ConnectionManager Instance
            => _instance ??= new ConnectionManager();

        // ── Events (main-thread) ──────────────────────────────────────────────
        /// <summary>Fired whenever the connection state changes.</summary>
        public event Action<ConnectionState>   StateChanged;
        /// <summary>Fired when a device successfully connects.</summary>
        public event Action<DeviceInfo>        DeviceConnected;
        /// <summary>Fired when a device disconnects.</summary>
        public event Action<DeviceInfo>        DeviceDisconnected;
        /// <summary>Fired on each FPS stats update (≈ once per second).</summary>
        public event Action<float, float>      StatsUpdated;  // (captureFps, encodeMs)
        /// <summary>Fired when the smoothed RTT changes. Arg is RTT in milliseconds.</summary>
        public event Action<float>             RttUpdated;

        // ── Observed state (main-thread readable) ─────────────────────────────
        /// <summary>Current connection state.</summary>
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

        /// <summary>The port the WebSocket server is (or will be) listening on.</summary>
        public int Port { get; private set; } = UniPeekConstants.DefaultPort;

        /// <summary>Read-only view of all currently connected devices.</summary>
        public IReadOnlyList<DeviceInfo> ConnectedDevices => _devices;

        /// <summary>Active streaming configuration.</summary>
        public StreamConfig Config { get; } = new StreamConfig();

        /// <summary>Smoothed round-trip time in milliseconds (0 until first measurement).</summary>
        public float SmoothedRtt { get; private set; }

        /// <summary>Whether a WebRTC connection is currently active.</summary>
        public bool WebRtcActive { get; private set; }

        /// <summary>Active frame capture strategy.</summary>
        public CaptureMethod ActiveCaptureMethod { get; private set; } = CaptureMethod.CameraRender;

        // ── Internal components ───────────────────────────────────────────────
        private UniPeekWebSocketServer _wsServer;
        private MdnsAdvertiser         _mdns;
        private FrameCapture           _capture;
        private FrameEncoder           _encoder;
        private WebSocket              _reverseClient;  // used in reverse-connection mode

        private readonly List<DeviceInfo>       _devices         = new();
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new();

        // First session to send a valid hello becomes host; only it can send config/input.
        private string _hostSessionId;

        // Suppress repeated "not in Play Mode" warnings for touch input.
        private bool _gameViewFocusWarningLogged;

        private bool  _editorHooked;
        private float _statsTimer;

        // ── RTT ───────────────────────────────────────────────────────────────
        private float _pingTimer;
        private readonly Queue<float> _rttSamples = new(5);

        // ── WebRTC (compiled only when package is installed) ──────────────────
#if UNITY_WEBRTC
        private WebRTCStreamer _webRtcStreamer;
        private string         _webRtcSessionId;

        // Outbound signaling POCOs — kept here to avoid dependency on WebRTCStreamer.cs
        [Serializable] private class WsOfferMsg    { public string type; public string sdp; }
        [Serializable] private class WsCandidateMsg
        {
            public string type;
            public string candidate;
            public string sdpMid;
            public int    sdpMLineIndex;
        }
#endif

        // ── Constructor / dispose ─────────────────────────────────────────────

        private ConnectionManager()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private void OnBeforeAssemblyReload()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            StopStreaming();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            StopStreaming();
            _instance = null;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Starts the WebSocket server and mDNS advertiser.
        /// Also auto-configures the Windows firewall on first run.
        /// </summary>
        public void StartStreaming(int port = UniPeekConstants.DefaultPort)
        {
            if (State != ConnectionState.Disconnected) return;

            Port = port;

            // One-time Windows firewall setup
            FirewallHelper.EnsureFirewallRule(port);

            // Boot WebSocket server
            _wsServer = new UniPeekWebSocketServer(port);
            _wsServer.ClientConnected    += OnClientConnected;
            _wsServer.ClientDisconnected += OnClientDisconnected;
            _wsServer.ConfigReceived     += OnConfigReceived;
            _wsServer.TouchReceived      += (sid, msg) => { if (sid == _hostSessionId) Enqueue(() => HandleTouch(msg)); };
            _wsServer.GyroReceived       += (sid, msg) =>
            {
                if (sid != _hostSessionId) return;
                Enqueue(() =>
                {
                    UniPeekConstants.Log($"[Input] Gyro  x={msg.x:F3} y={msg.y:F3} z={msg.z:F3}");
                    InputInjector.InjectGyro(msg.x, msg.y, msg.z);
                });
            };
            _wsServer.AccelReceived += (sid, msg) =>
            {
                if (sid != _hostSessionId) return;
                Enqueue(() =>
                {
                    UniPeekConstants.Log($"[Input] Accel x={msg.x:F3} y={msg.y:F3} z={msg.z:F3}");
                    InputInjector.InjectAccelerometer(msg.x, msg.y, msg.z);
                });
            };
            _wsServer.HelloReceived       += OnHelloReceived;
            _wsServer.PongReceived        += OnPongReceived;
#if UNITY_WEBRTC
            _wsServer.AnswerReceived    += OnAnswerReceived;
            _wsServer.CandidateReceived += OnCandidateReceived;
#endif
            _wsServer.Start();

            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            // Boot mDNS
            string localIp    = QRCodeGenerator.GetLocalIPv4();
            string editorName = EditorPrefs.GetString(UniPeekConstants.PrefEditorName, string.Empty);
            _mdns = new MdnsAdvertiser(port, localIp, editorName);
            _mdns.Start();

            // Boot encoder + capture
            _encoder = new FrameEncoder(_wsServer, Config.Quality);
            _capture = new FrameCapture(_encoder, Config.Width, Config.Height, Config.FpsCap);
            _capture.SetCaptureMethod(ActiveCaptureMethod);
            _capture.Start();

            HookEditorUpdate();
            SetState(ConnectionState.Advertising);
        }

        /// <summary>
        /// Sends a shutdown message to a single client and closes its connection.
        /// All other clients remain connected and streaming continues.
        /// </summary>
        public void DisconnectDevice(string sessionId)
        {
            if (_wsServer == null || string.IsNullOrEmpty(sessionId)) return;
            _wsServer.SendToSession(sessionId, "{\"type\":\"shutdown\"}");
            _wsServer.CloseSession(sessionId);
        }

        /// <summary>Stops all streaming, severs all connections, and releases resources.</summary>
        public void StopStreaming()
        {
            if (State == ConnectionState.Disconnected) return;

#if UNITY_WEBRTC
            // Send shutdown to the WebRTC client BEFORE closing the peer connection,
            // so Flutter transitions to ConnectionState.shutdown (clean exit, no reconnect
            // prompt) rather than ConnectionState.disconnected (reconnect countdown).
            if (_webRtcSessionId != null)
                _wsServer?.SendToSession(_webRtcSessionId, "{\"type\":\"shutdown\"}");
            TearDownWebRTC();
#endif

            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;

            _capture?.Stop();
            _capture = null;

            _encoder = null;

            // Tell all connected clients the stream is ending before closing the socket.
            _wsServer?.BroadcastText("{\"type\":\"shutdown\"}");
            _wsServer?.Stop();
            _wsServer = null;

            _mdns?.Stop();
            _mdns = null;

            _reverseClient?.Close();
            _reverseClient = null;

            _devices.Clear();
            _hostSessionId = null;
            SmoothedRtt  = 0f;
            WebRtcActive = false;
            _rttSamples.Clear();

            UnhookEditorUpdate();
            QRCodeGenerator.Invalidate();
            SetState(ConnectionState.Disconnected);

#if ENABLE_INPUT_SYSTEM
            InputInjector.RemoveVirtualDevices();
#endif
        }

        /// <summary>
        /// Initiates a reverse connection: Unity connects <em>outward</em> to the
        /// phone's WebSocket server on <paramref name="phoneIp"/>:<see cref="UniPeekConstants.ReversePort"/>.
        /// </summary>
        public void ConnectReverse(string phoneIp)
        {
            if (string.IsNullOrWhiteSpace(phoneIp)) return;

            SetState(ConnectionState.ReverseConnecting);
            string url = $"ws://{phoneIp}:{UniPeekConstants.ReversePort}/";

            _reverseClient = new WebSocket(url);
            _reverseClient.OnOpen  += (_, __) => Enqueue(() =>
            {
                UniPeekConstants.Log($"[Reverse] Connected to phone at {url}");
                SetState(ConnectionState.Connected);
            });
            _reverseClient.OnClose += (_, __) => Enqueue(() =>
            {
                _reverseClient = null;
                SetState(_devices.Count > 0 ? ConnectionState.Connected : ConnectionState.Advertising);
            });
            _reverseClient.OnError += (_, e) => Enqueue(() =>
            {
                UniPeekConstants.LogWarning($"[Reverse] Error: {e.Message}");
                SetState(ConnectionState.Advertising);
            });
            _reverseClient.ConnectAsync();
        }

        /// <summary>Updates the WebRTC maximum video bitrate at runtime.</summary>
        public void SetWebRtcMaxBitrate(int kbps)
        {
            Config.MaxBitrateKbps = kbps;
#if UNITY_WEBRTC
            _webRtcStreamer?.SetMaxBitrate(kbps);
#endif
        }

        /// <summary>Switches the frame capture strategy at runtime.</summary>
        public void SetCaptureMethod(CaptureMethod method)
        {
            ActiveCaptureMethod = method;
            _capture?.SetCaptureMethod(method);
        }

        /// <summary>
        /// Applies a <see cref="StreamConfig"/> received from the phone app and
        /// updates the capture + encoder components accordingly.
        /// </summary>
        public void ApplyConfig(int width, int height, int quality, int fpsCap)
        {
            bool resolutionChanged = width != Config.Width || height != Config.Height;

            Config.Width   = width;
            Config.Height  = height;
            Config.Quality = quality;
            Config.FpsCap  = fpsCap;

            _capture?.SetResolution(width, height);
            _capture?.SetFpsCap(fpsCap);
            _encoder?.SetQuality(quality);
#if UNITY_WEBRTC
            _webRtcStreamer?.SetFpsCap(fpsCap);
#endif

            // Resize the Game View so ScreenCapture captures at the phone's exact
            // resolution — avoids stretching when aspect ratios differ.
            TrySetGameViewResolution(width, height);

#if UNITY_WEBRTC
            // The WebRTC RenderTexture is fixed-size — restart the session with the
            // new dimensions so the video track matches the phone's resolution.
            if (resolutionChanged && _webRtcStreamer != null && _webRtcSessionId != null)
            {
                var sessionId = _webRtcSessionId;
                TearDownWebRTC();
                StartWebRTCNegotiation(sessionId);
            }
#endif
        }

        /// <summary>
        /// Sets the Unity Game View to a custom fixed resolution using
        /// <see cref="UnityEditor.TestTools.Graphics.GameViewSize"/>.
        /// </summary>
        private static void TrySetGameViewResolution(int width, int height)
        {
            try
            {
                var sizeObj = GameViewSize.SetCustomSize(width, height);
                GameViewSize.SelectSize(sizeObj);
            }
            catch (Exception ex)
            {
                UniPeekConstants.LogWarning($"[Config] Could not resize Game View: {ex.Message}");
            }
        }

        // ── Editor update hook ────────────────────────────────────────────────

        private void HookEditorUpdate()
        {
            if (_editorHooked) return;
            EditorApplication.update += OnEditorUpdate;
            _editorHooked = true;
        }

        private void UnhookEditorUpdate()
        {
            if (!_editorHooked) return;
            EditorApplication.update -= OnEditorUpdate;
            _editorHooked = false;
        }

        private void OnEditorUpdate()
        {
            // Drain cross-thread callbacks
            while (_mainThreadQueue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { UniPeekConstants.LogError(ex.ToString()); }
            }

#if UNITY_WEBRTC
            // Drive the WebRTC engine every editor tick
            _webRtcStreamer?.Tick();
#endif

            // Periodic stats update
            _statsTimer += Time.unscaledDeltaTime;
            if (_statsTimer >= 1f)
            {
                _statsTimer = 0f;
                if (_capture != null && _encoder != null)
                {
#if UNITY_WEBRTC
                    float captureFps = WebRtcActive && _webRtcStreamer != null
                        ? _webRtcStreamer.SmoothedCaptureFps
                        : _capture.SmoothedFps;
                    StatsUpdated?.Invoke(captureFps, _encoder.LastEncodeMs);
#else
                    StatsUpdated?.Invoke(_capture.SmoothedFps, _encoder.LastEncodeMs);
#endif
                }
            }

            // RTT ping every 30 s (only while connected)
            if (State == ConnectionState.Connected && _devices.Count > 0)
            {
                _pingTimer += Time.unscaledDeltaTime;
                if (_pingTimer >= UniPeekConstants.PingIntervalSeconds)
                {
                    _pingTimer = 0f;
                    SendPing();
                }
            }
        }

        // ── Ping / pong ───────────────────────────────────────────────────────

        private void SendPing()
        {
            long ts  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var  msg = new PingPongMessage { type = "ping", ts = ts };
            _wsServer?.BroadcastText(UnityEngine.JsonUtility.ToJson(msg));
        }

        private void OnPongReceived(long ts)
        {
            long  now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            float rtt = (float)(now - ts);

            Enqueue(() =>
            {
                if (_rttSamples.Count >= 5) _rttSamples.Dequeue();
                _rttSamples.Enqueue(rtt);

                float sum = 0f;
                foreach (float s in _rttSamples) sum += s;
                SmoothedRtt = sum / _rttSamples.Count;

                RttUpdated?.Invoke(SmoothedRtt);
            });
        }

        // ── Event handlers (may arrive on background thread) ─────────────────

        private void OnClientConnected(string sessionId, string deviceName)
            => Enqueue(() =>
            {
                var info = new DeviceInfo
                {
                    SessionId   = sessionId,
                    DeviceName  = deviceName,
                    IPAddress   = string.Empty,
                    ConnectedAt = DateTime.UtcNow,
                };
                _devices.Add(info);
                UniPeekConstants.Log($"[WS] Device connected: {deviceName} (session {sessionId})");

#if ENABLE_INPUT_SYSTEM
                InputInjector.EnsureVirtualDevices();
#endif
                SetState(ConnectionState.Connected);
                DeviceConnected?.Invoke(info);
            });

        private void OnClientDisconnected(string sessionId)
            => Enqueue(() =>
            {
                int idx = _devices.FindIndex(d => d.SessionId == sessionId);
                if (idx < 0) return;

                var info = _devices[idx];
                _devices.RemoveAt(idx);
                UniPeekConstants.Log($"[WS] Device disconnected: {info.DeviceName}");

                if (_hostSessionId == sessionId)
                {
                    _hostSessionId = _devices.Count > 0 ? _devices[0].SessionId : null;
                    if (_hostSessionId != null)
                        UniPeekConstants.Log($"[Auth] Host transferred to {_devices[0].DeviceName}");
                }

#if UNITY_WEBRTC
                if (_webRtcSessionId == sessionId)
                    TearDownWebRTC();
#endif

                SetState(_devices.Count > 0 ? ConnectionState.Connected : ConnectionState.Advertising);
                DeviceDisconnected?.Invoke(info);
            });

        private void OnConfigReceived(string sessionId, ConfigMessage msg)
        {
            if (msg == null) return;

            // Parse on the background thread (no Unity API access needed here).
            int width  = Config.Width;
            int height = Config.Height;
            if (!string.IsNullOrEmpty(msg.resolution))
            {
                var parts = msg.resolution.Split('x');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out int w) &&
                    int.TryParse(parts[1], out int h))
                {
                    width  = w;
                    height = h;
                }
            }

            // Resolution is always sent in portrait order (short × long).
            // Swap when the device is landscape so the capture RT matches the screen.
            if (msg.landscape && width < height) { int t = width; width = height; height = t; }
            else if (!msg.landscape && width > height) { int t = width; width = height; height = t; }

            int quality = msg.quality > 0 ? Mathf.Clamp(msg.quality, 1, 100) : Config.Quality;
            int fps     = msg.fps     > 0 ? Mathf.Clamp(msg.fps, 1, 120)     : Config.FpsCap;

            // Host check MUST run on the main thread after OnHelloReceived has been
            // processed — doing it here (BG thread) races against the hello Enqueue
            // and always sees _hostSessionId == null on the first config message.
            Enqueue(() =>
            {
                if (sessionId != _hostSessionId)
                {
                    UniPeekConstants.LogWarning($"[Auth] Config rejected from non-host session {sessionId}");
                    return;
                }
                ApplyConfig(width, height, quality, fps);
            });
        }

        private void OnHelloReceived(string sessionId, HelloMessage hello)
        {
            // First device to send hello becomes the host (controls config and input).
            Enqueue(() =>
            {
                if (_hostSessionId == null)
                {
                    _hostSessionId = sessionId;
                    UniPeekConstants.Log($"[Auth] Host session set: {hello?.deviceName ?? sessionId}");
                }

            });

            // Overwrite the device name stored at connect-time (which fell back to the
            // X-Device-Name header) with the richer name the app sends in the hello payload.
            if (!string.IsNullOrEmpty(hello?.deviceName))
            {
                Enqueue(() =>
                {
                    int idx = _devices.FindIndex(d => d.SessionId == sessionId);
                    if (idx >= 0)
                    {
                        var old = _devices[idx];
                        _devices[idx] = new DeviceInfo
                        {
                            SessionId   = old.SessionId,
                            DeviceName  = hello.deviceName,
                            IPAddress   = old.IPAddress,
                            ConnectedAt = old.ConnectedAt,
                            IsPro       = hello.tier == "pro",
                        };
                        DeviceConnected?.Invoke(_devices[idx]);
                    }
                });
            }

#if UNITY_WEBRTC
            if (hello?.client == "flutter_webrtc")
            {
                // EditorPrefs and StartWebRTCNegotiation both require the main thread.
                Enqueue(() =>
                {
                    var socketMode = (SocketMode)EditorPrefs.GetInt(UniPeekConstants.PrefSocketMode, (int)SocketMode.WebRTC);
                    if (socketMode == SocketMode.WebRTC)
                    {
                        if (Application.isPlaying)
                            StartWebRTCNegotiation(sessionId);
                        else
                            // WebRTC's sync-context callback loop only runs in play mode; starting
                            // negotiation in edit mode causes native callbacks to post to a null or
                            // idle context, crashing/freezing the editor.  Close the session so the
                            // app reconnects cleanly once play mode begins.
                            _wsServer?.CloseSession(sessionId);
                    }
                    else
                        _wsServer?.CloseSession(sessionId);
                });
                return;
            }
#endif
            UniPeekConstants.Log($"[WS] Hello from {hello?.client ?? "unknown"} ({hello?.deviceName ?? "?"}) session {sessionId}");

            // Tell the new client whether the editor is currently in Play Mode.
            // Application.isPlaying is not thread-safe — enqueue to main thread.
            Enqueue(() =>
            {
                var playModeJson = UnityEngine.JsonUtility.ToJson(
                    new PlayModeMessage { type = "playmode", playing = Application.isPlaying });
                _wsServer?.SendToSession(sessionId, playModeJson);
            });
        }

        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // Fire only after the transition is complete so Application.isPlaying is correct.
            if (change != PlayModeStateChange.EnteredPlayMode &&
                change != PlayModeStateChange.EnteredEditMode) return;

            BroadcastPlayMode();
        }

        private void BroadcastPlayMode()
        {
            var json = UnityEngine.JsonUtility.ToJson(
                new PlayModeMessage { type = "playmode", playing = Application.isPlaying });
            _wsServer?.BroadcastText(json);
        }

        private void HandleTouch(TouchMessage msg)
        {
            if (msg == null) return;

            // Keep the Game View focused so the Input System processes injected events.
            if (Application.isPlaying)
            {
                _gameViewFocusWarningLogged = false; // reset when we enter Play Mode
                GameViewSize.GetMainGameView()?.Focus();
            }
            else if (!_gameViewFocusWarningLogged)
            {
                _gameViewFocusWarningLogged = true;
                UniPeekConstants.LogWarning("[Input] Touch received but the Editor is not in Play Mode — Input System events will not be processed. Enter Play Mode or click the Game View to enable input.");
            }

            UniPeekConstants.Log($"[Input] Touch phase={msg.phase} x={msg.x:F3} y={msg.y:F3} finger={msg.fingerId}");
            InputInjector.InjectTouch(msg.phase, msg.x, msg.y, msg.fingerId);
            var pos = new Vector2(msg.x, msg.y);
            UniPeekInput.OnTouch?.Invoke(pos);
            UniPeekInput.OnTouchDetailed?.Invoke(msg.fingerId, msg.phase, pos);
        }

        // ── WebRTC orchestration ──────────────────────────────────────────────
#if UNITY_WEBRTC

        /// <summary>
        /// Routes JSON messages received via the WebRTC DataChannel to the
        /// appropriate input injector or responds to ping messages.
        /// Must be called on the Unity main thread (via Enqueue).
        /// </summary>
        private void HandleInputJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            try
            {
                var base_ = UnityEngine.JsonUtility.FromJson<BaseMessage>(json);
                switch (base_?.type)
                {
                    case "touch":
                        HandleTouch(UnityEngine.JsonUtility.FromJson<TouchMessage>(json));
                        break;
                    case "gyro":
                        var g = UnityEngine.JsonUtility.FromJson<GyroMessage>(json);
                        InputInjector.InjectGyro(g.x, g.y, g.z);
                        break;
                    case "accel":
                        var a = UnityEngine.JsonUtility.FromJson<AccelMessage>(json);
                        InputInjector.InjectAccelerometer(a.x, a.y, a.z);
                        break;
                    case "config":
                        var cfg = UnityEngine.JsonUtility.FromJson<ConfigMessage>(json);
                        UniPeekConstants.Log($"[WS] Received: {json}");
                        OnConfigReceived(_webRtcSessionId ?? string.Empty, cfg);
                        break;
                    case "ping":
                        // DataChannel ping — respond with pong so Flutter can measure RTT.
                        var p = UnityEngine.JsonUtility.FromJson<PingPongMessage>(json);
                        _wsServer?.SendToSession(_webRtcSessionId ?? string.Empty,
                            UnityEngine.JsonUtility.ToJson(new PingPongMessage { type = "pong", ts = p.ts }));
                        break;
                }
            }
            catch (Exception ex)
            {
                UniPeekConstants.LogWarning($"[DC] Failed to handle input: {ex.Message}");
            }
        }

        private void StartWebRTCNegotiation(string sessionId)
        {
            UniPeekConstants.Log($"[WebRTC] Starting negotiation for session {sessionId}");

            TearDownWebRTC();

            _webRtcSessionId = sessionId;

            // Stop JPEG pipeline immediately — WebRTC will carry video.
            if (_capture != null) _capture.UseWebRTC = true;

            _webRtcStreamer = new WebRTCStreamer(Config.Width, Config.Height, Config.FpsCap,
                Config.MaxBitrateKbps, Config.WebRtcStunUrl);
            var streamer = _webRtcStreamer;
            var activeSessionId = sessionId;

            _webRtcStreamer.OfferReady += sdp =>
            {
                if (_webRtcStreamer != streamer || _webRtcSessionId != activeSessionId) return;
                var payload = UnityEngine.JsonUtility.ToJson(new WsOfferMsg { type = "offer", sdp = sdp });
                _wsServer?.SendToSession(activeSessionId, payload);
                UniPeekConstants.Log("[WebRTC] Offer sent to Flutter.");
            };

            _webRtcStreamer.IceCandidateReady += (candidate, sdpMid, sdpMLineIndex) =>
            {
                if (_webRtcStreamer != streamer || _webRtcSessionId != activeSessionId) return;
                var payload = UnityEngine.JsonUtility.ToJson(new WsCandidateMsg
                {
                    type           = "candidate",
                    candidate      = candidate,
                    sdpMid         = sdpMid,
                    sdpMLineIndex  = sdpMLineIndex,
                });
                _wsServer?.SendToSession(activeSessionId, payload);
            };

            _webRtcStreamer.Connected += () => Enqueue(() =>
            {
                if (_webRtcStreamer != streamer || _webRtcSessionId != activeSessionId) return;
                WebRtcActive = true;
                UniPeekConstants.Log("[WebRTC] P2P connection established — video flowing.");
            });

            _webRtcStreamer.Disconnected += () => Enqueue(() =>
            {
                if (_webRtcStreamer != streamer || _webRtcSessionId != activeSessionId) return;
                UniPeekConstants.Log("[WebRTC] P2P connection lost, reverting to JPEG.");
                TearDownWebRTC();
                // Resume JPEG pipeline
                if (_capture != null) _capture.UseWebRTC = false;
            });

            _webRtcStreamer.DataChannelMessage += json => Enqueue(() =>
            {
                if (_webRtcStreamer != streamer || _webRtcSessionId != activeSessionId) return;
                HandleInputJson(json);
            });

            try
            {
                _webRtcStreamer.StartNegotiation();
            }
            catch (Exception ex)
            {
                UniPeekConstants.LogError($"[WebRTC] StartNegotiation failed: {ex.Message}");
                TearDownWebRTC();
            }
        }

        private void TearDownWebRTC()
        {
            if (_webRtcStreamer == null) return;
            _webRtcStreamer.Dispose();
            _webRtcStreamer   = null;
            _webRtcSessionId  = null;
            WebRtcActive      = false;
            if (_capture != null) _capture.UseWebRTC = false;
            SmoothedRtt = 0f;
            _rttSamples.Clear();
        }

        private void OnAnswerReceived(string sessionId, string sdp)
        {
            if (sessionId != _webRtcSessionId) return;
            UniPeekConstants.Log("[WebRTC] SDP answer received from Flutter.");
            _webRtcStreamer?.SetRemoteAnswer(sdp);
        }

        private void OnCandidateReceived(string sessionId, string candidate, string sdpMid, int sdpMLineIndex)
        {
            if (sessionId != _webRtcSessionId) return;
            _webRtcStreamer?.AddIceCandidate(candidate, sdpMid, sdpMLineIndex);
        }

#endif // UNITY_WEBRTC

        // ── Helpers ───────────────────────────────────────────────────────────

        private void SetState(ConnectionState newState)
        {
            if (State == newState) return;
            State = newState;
            StateChanged?.Invoke(newState);
        }

        /// <summary>Enqueues an action to be executed on the next main-thread update.</summary>
        private void Enqueue(Action action) => _mainThreadQueue.Enqueue(action);
    }
}
