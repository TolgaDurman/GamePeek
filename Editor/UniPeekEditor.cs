using UnityEditor;
using UnityEngine;

namespace UniPeek
{
    /// <summary>
    /// Main UniPeek Editor window (<c>Window ▶ UniPeek</c>).
    /// </summary>
    public sealed class UniPeekWindow : EditorWindow
    {
        // ── Persistent settings ───────────────────────────────────────────────
        private bool _requirePlayMode;
        private bool _autoStartOnPlayMode;
        private SocketMode _socketMode;
        private LogLevel   _logLevel;

        // Set when streaming was triggered automatically by entering Play Mode,
        // so we know to auto-stop it on exit.
        private bool _autoStartedByPlayMode;

        // Survives domain reloads; claimed with DeleteKey to prevent double-start.
        private const string PrefPendingStart = "UniPeek_PendingStart";

        // ── Runtime state ─────────────────────────────────────────────────────
        private bool  _streaming;
        private float _captureFps;
        private float _encodeMs;
        private float _rttMs;
        private bool  _webRtcActive;

        // ── QR code ───────────────────────────────────────────────────────────
        private Texture2D _qrTexture;
        private Texture2D _downloadQrTexture;

        // ── Styles (initialized lazily) ───────────────────────────────────────
        private GUIStyle _titleStyle;
        private GUIStyle _versionStyle;
        private GUIStyle _statusTextStyle;
        private GUIStyle _sectionLabelStyle;
        private GUIStyle _infoIconStyle;
        private GUIStyle _tooltipBoxStyle;
        private bool     _stylesInitialized;

        // ── Assets ────────────────────────────────────────────────────────────
        private Texture2D _logoTexture;
        private Texture2D _proIcon;

        // ── Editor name ───────────────────────────────────────────────────────
        private string _editorName = string.Empty;

        // ── Port ──────────────────────────────────────────────────────────────
        private int _port = UniPeekConstants.DefaultPort;

        // ── Package install ───────────────────────────────────────────────────
        private bool _webRtcInstallRequested;

        // ── Network interface selection ───────────────────────────────────────
        private int      _nicIndex;            // 0 = Auto, 1..n = specific interface
        private string[] _nicLabels = System.Array.Empty<string>();
        private string[] _nicIPs    = System.Array.Empty<string>();

        // ── Scroll ────────────────────────────────────────────────────────────
        private Vector2 _scrollPos;

        // ── Tooltip ───────────────────────────────────────────────────────────
        private string _hoveredTooltip;

        // ── Colors ────────────────────────────────────────────────────────────
        private static readonly Color ColGreen = new(0.18f, 0.80f, 0.32f);
        private static readonly Color ColAmber = new(1.00f, 0.72f, 0.00f);
        private static readonly Color ColGrey  = new(0.45f, 0.45f, 0.45f);

        // ─────────────────────────────────────────────────────────────────────
        // Menu item
        // ─────────────────────────────────────────────────────────────────────

        [MenuItem("Window/UniPeek")]
        public static void ShowWindow()
        {
            var logoTex = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/Plugins/UniPeek/Textures/unipeek-logo.png");

            var window = GetWindow<UniPeekWindow>(utility: false, title: "UniPeek", focus: true);
            window.titleContent = new GUIContent("UniPeek", logoTex, "UniPeek — Game View streaming");
            window.minSize = new Vector2(280f, 440f);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Lifecycle
        // ─────────────────────────────────────────────────────────────────────

        private void OnEnable()
        {
            LoadPrefs();
            RefreshNetworkInterfaces();
            SubscribeToManager();

            _logoTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/Plugins/UniPeek/Textures/unipeek-logo.png");
            _proIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/Plugins/UniPeek/Textures/pro-user.png");
            _downloadQrTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/Plugins/UniPeek/Textures/qr-code.png");

            EditorApplication.playModeStateChanged += OnPlayModeChanged;

            // After a domain reload the EditorWindow is recreated while Unity is already
            // in play mode. EnteredPlayMode may have fired before OnEnable ran, so we
            // check the pending flag here as well — whichever runs first claims it.
            if (Application.isPlaying && EditorPrefs.GetBool(PrefPendingStart, false))
            {
                EditorPrefs.DeleteKey(PrefPendingStart);
                DoStartStreaming();
            }
            else if (!_requirePlayMode && EditorPrefs.GetBool(UniPeekConstants.PrefPersistStreaming, false))
            {
                DoStartStreaming();
            }
        }

        private void OnDisable()
        {
            UnsubscribeFromManager();
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        }

        private void OnFocus()
        {
            RefreshNetworkInterfaces();
            if (_streaming) RefreshQR();
        }

        private void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode && EditorPrefs.GetBool(PrefPendingStart, false))
            {
                EditorPrefs.DeleteKey(PrefPendingStart);
                DoStartStreaming();
                return;
            }

            if (state == PlayModeStateChange.EnteredPlayMode && _autoStartOnPlayMode && !_streaming)
            {
                DoStartStreaming();
                _autoStartedByPlayMode = true;
                return;
            }

            if (state == PlayModeStateChange.EnteredPlayMode
                && !_requirePlayMode && !_streaming
                && EditorPrefs.GetBool(UniPeekConstants.PrefPersistStreaming, false))
            {
                DoStartStreaming();
                return;
            }

            if (_streaming && state == PlayModeStateChange.ExitingPlayMode
                && (_requirePlayMode || _autoStartedByPlayMode))
            {
                _autoStartedByPlayMode = false;
                StopStreaming();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // GUI
        // ─────────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            InitStyles();
            _hoveredTooltip = null;

            DrawHeader();

            using var scroll = new EditorGUILayout.ScrollViewScope(_scrollPos);
            _scrollPos = scroll.scrollPosition;

            GUILayout.Space(10f);
            DrawStatusCard();
            GUILayout.Space(10f);
            DrawMainButton();
            GUILayout.Space(8f);

            DrawQRCode();

            if (_streaming)
            {
                DrawStatsBar();
                GUILayout.Space(8f);
            }

            DrawSectionLabel("Options");
            DrawSettings();
            GUILayout.Space(8f);

            DrawDeviceList();

            if (!_streaming)
            {
                GUILayout.Space(8f);
                DrawDownloadAppCard();
            }

            GUILayout.FlexibleSpace();
            DrawFooter();
            DrawTooltipOverlay();
        }

        // ── Header ────────────────────────────────────────────────────────────

        private void DrawHeader()
        {
            using var row = new EditorGUILayout.HorizontalScope(EditorStyles.toolbar);

            if (_logoTexture != null)
                GUILayout.Label(_logoTexture, GUILayout.Width(18f), GUILayout.Height(18f));

            GUILayout.Label("UniPeek", _titleStyle, GUILayout.ExpandWidth(true));
            GUILayout.Label($"v{UniPeekConstants.Version}", _versionStyle);
        }

        // ── Status card ───────────────────────────────────────────────────────

        private void DrawStatusCard()
        {
            var mgr   = ConnectionManager.Instance;
            var state = mgr.State;

            Color  dotColor;
            string primaryText;
            string secondaryText = string.Empty;

            switch (state)
            {
                case ConnectionState.Advertising:
                    dotColor      = ColAmber;
                    primaryText   = string.IsNullOrWhiteSpace(_editorName)
                        ? System.Environment.MachineName
                        : _editorName;
                    secondaryText = $"{QRCodeGenerator.GetLocalIPv4()}:{ConnectionManager.Instance.Port}";
                    break;
                case ConnectionState.Connected:
                    dotColor = ColGreen;
                    int n    = mgr.ConnectedDevices.Count;
                    primaryText   = n == 1 ? mgr.ConnectedDevices[0].DeviceName : $"{n} devices";
                    secondaryText = "Connected";
                    break;
                default:
                    dotColor    = ColGrey;
                    primaryText = "Not streaming";
                    break;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Space(6f);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(6f);
                DrawColorDot(dotColor);
                GUILayout.Space(4f);
                GUILayout.Label(primaryText, _statusTextStyle, GUILayout.ExpandWidth(true));
                GUILayout.Space(6f);
            }

            if (!string.IsNullOrEmpty(secondaryText))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(28f);
                    GUILayout.Label(secondaryText, EditorStyles.miniLabel);
                }
            }

            GUILayout.Space(6f);
            EditorGUILayout.EndVertical();
        }

        // ── Main button ───────────────────────────────────────────────────────

        private void DrawMainButton()
        {
            var prev = GUI.backgroundColor;
            GUI.backgroundColor = _streaming
                ? new Color(0.88f, 0.30f, 0.30f)
                : new Color(0.28f, 0.76f, 0.44f);

            if (GUILayout.Button(
                    _streaming ? "■   Stop Streaming" : "▶   Start Streaming",
                    GUILayout.Height(42f)))
            {
                if (_streaming) StopStreaming();
                else            StartStreaming();
            }

            GUI.backgroundColor = prev;
        }

        // ── QR code ───────────────────────────────────────────────────────────

        private void DrawQRCode()
        {
            if (!_streaming) return;
            if (ConnectionManager.Instance.State == ConnectionState.Connected) return;

            RefreshQR();

            if (_qrTexture == null)
            {
                EditorGUILayout.HelpBox(
                    "Could not generate QR code — check local network connection.",
                    MessageType.Warning);
                GUILayout.Space(6f);
                return;
            }

            float size = Mathf.Min(position.width - 48f, 220f);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label(_qrTexture, GUILayout.Width(size), GUILayout.Height(size));
                GUILayout.FlexibleSpace();
            }

            GUILayout.Label(
                "Scan with the UniPeek app to connect",
                EditorStyles.centeredGreyMiniLabel);
            GUILayout.Space(10f);
        }

        // ── Stats bar ─────────────────────────────────────────────────────────

        private void DrawDownloadAppCard()
        {
            DrawSectionLabel("UniPeek App");

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Space(8f);

            GUILayout.Label("Download the UniPeek app", _statusTextStyle);
            GUILayout.Label(
                "Scan this QR code before streaming to install the iOS or Android companion app.",
                EditorStyles.wordWrappedMiniLabel);
            GUILayout.Space(8f);

            if (_downloadQrTexture != null)
            {
                float size = Mathf.Min(position.width - 72f, 180f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(_downloadQrTexture, GUILayout.Width(size), GUILayout.Height(size));
                    GUILayout.FlexibleSpace();
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Download QR texture not found at Assets/Plugins/UniPeek/Textures/qr-code.png.",
                    MessageType.Warning);
            }

            GUILayout.Space(6f);
            if (GUILayout.Button("Open UniPeek App Page", GUILayout.Height(26f)))
                Application.OpenURL("https://unipeek.app");

            GUILayout.Space(6f);
            EditorGUILayout.EndVertical();
        }

        private void DrawStatsBar()
        {
            using var row = new EditorGUILayout.HorizontalScope(EditorStyles.helpBox);

            if (_webRtcActive)
            {
                DrawColorDot(RttColor(_rttMs), small: true);
                GUILayout.Space(2f);
                GUILayout.Label("WebRTC", EditorStyles.miniLabel, GUILayout.Width(46f));
                GUILayout.Label(
                    _rttMs > 0f ? $"RTT {_rttMs:F0} ms" : "RTT —",
                    EditorStyles.miniLabel, GUILayout.Width(64f));
            }
            else
            {
                GUILayout.Label($"FPS  {_captureFps:F1}", EditorStyles.miniLabel, GUILayout.Width(64f));
                GUILayout.Label($"Enc  {_encodeMs:F0} ms", EditorStyles.miniLabel, GUILayout.Width(70f));
            }

            GUILayout.FlexibleSpace();
            int c = ConnectionManager.Instance.ConnectedDevices.Count;
            GUILayout.Label(c == 1 ? "1 client" : $"{c} clients", EditorStyles.miniLabel);
        }

        // ── Settings ──────────────────────────────────────────────────────────

        private void DrawSettings()
        {
            // ── Editor Name ────────────────────────────────────────────────────
            using (new EditorGUILayout.HorizontalScope())
            {
                OptionLabel("Editor Name",
                    "Display name shown in the UniPeek app's device list.\nDefaults to your machine name.");
                _editorName = EditorGUILayout.TextField(_editorName, GUILayout.ExpandWidth(true));
                if (GUILayout.Button("Set", EditorStyles.miniButton, GUILayout.Width(32f)))
                {
                    SavePrefs();
                    QRCodeGenerator.Invalidate();
                }
            }

            // ── Run in Play Mode ───────────────────────────────────────────────
            int pmIndex;
            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                OptionLabel("Run in Play Mode",
                    "On: streaming only runs while the Editor is in Play Mode.\nOff: streams in Edit + Play Mode (briefly drops on script recompile).");
                pmIndex = EditorGUILayout.Popup(_requirePlayMode ? 0 : 1,
                    new[] { "True", "False" }, GUILayout.ExpandWidth(true));
            }
            if (EditorGUI.EndChangeCheck())
            {
                _requirePlayMode = pmIndex == 0;
                SavePrefs();
            }
            if (!_requirePlayMode)
            {
                GUILayout.Space(2f);
                EditorGUILayout.HelpBox(
                    "Recompiling will cut the connection. Pro users have automatic reconnect. " +
                    "Enabling 'Run in Play Mode' avoids interruptions.",
                    MessageType.Info);
            }

            // ── Auto Start on Play Mode ────────────────────────────────────────
            GUILayout.Space(4f);
            bool newAutoStart;
            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                OptionLabel("Auto Start on Play Mode",
                    "Automatically starts streaming when entering Play Mode and stops when exiting.");
                newAutoStart = EditorGUILayout.Toggle(_autoStartOnPlayMode, GUILayout.ExpandWidth(true));
            }
            if (EditorGUI.EndChangeCheck())
            {
                _autoStartOnPlayMode = newAutoStart;
                SavePrefs();
            }

            // ── Socket Mode ────────────────────────────────────────────────────
            GUILayout.Space(4f);
#if UNITY_WEBRTC
            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                OptionLabel("Socket Mode",
                    "WebSocket: JPEG frames over WebSocket — works on all setups.\nWebRTC: low-latency peer-to-peer video; requires com.unity.webrtc ≥ 3.0.0.");
                _socketMode = (SocketMode)EditorGUILayout.EnumPopup(_socketMode, GUILayout.ExpandWidth(true));
            }
            if (EditorGUI.EndChangeCheck())
                SavePrefs();

            // ── WebRTC Max Bitrate ─────────────────────────────────────────────
            GUILayout.Space(4f);
            EditorGUI.BeginChangeCheck();
            int currentMbps = ConnectionManager.Instance.Config.MaxBitrateKbps / 1000;
            int newMbps;
            using (new EditorGUILayout.HorizontalScope())
            {
                OptionLabel("Max Bitrate (Mbps)",
                    "WebRTC maximum video bitrate.\nHigher = better quality but more bandwidth.\nRecommended: 5–20 Mbps on local Wi-Fi.");
                newMbps = EditorGUILayout.IntSlider(currentMbps, 1, 50, GUILayout.ExpandWidth(true));
            }
            if (EditorGUI.EndChangeCheck())
            {
                ConnectionManager.Instance.SetWebRtcMaxBitrate(newMbps * 1000);
                EditorPrefs.SetInt(UniPeekConstants.PrefWebRtcMaxBitrateKbps, newMbps * 1000);
            }

            GUILayout.Space(4f);
            EditorGUI.BeginChangeCheck();
            string stunUrl;
            using (new EditorGUILayout.HorizontalScope())
            {
                OptionLabel("STUN URL",
                    "Optional WebRTC STUN server for VPNs, hotspots, or unusual subnet setups.\nLeave empty for local-only LAN behavior.");
                stunUrl = EditorGUILayout.TextField(ConnectionManager.Instance.Config.WebRtcStunUrl, GUILayout.ExpandWidth(true));
            }
            if (EditorGUI.EndChangeCheck())
            {
                ConnectionManager.Instance.Config.WebRtcStunUrl = stunUrl?.Trim() ?? string.Empty;
                EditorPrefs.SetString(UniPeekConstants.PrefWebRtcStunUrl, ConnectionManager.Instance.Config.WebRtcStunUrl);
            }
#else
            using (new EditorGUILayout.HorizontalScope())
            {
                OptionLabel("Socket Mode",
                    "WebRTC mode requires the com.unity.webrtc package (≥ 3.0.0).\nClick 'Add' to install it via the Package Manager.");
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.EnumPopup(SocketMode.WebSocket, GUILayout.ExpandWidth(true));
                EditorGUI.EndDisabledGroup();
                EditorGUI.BeginDisabledGroup(_webRtcInstallRequested);
                if (GUILayout.Button(_webRtcInstallRequested ? "Adding…" : "Add",
                        EditorStyles.miniButton, GUILayout.Width(54f)))
                {
                    _webRtcInstallRequested = true;
                    UnityEditor.PackageManager.Client.Add("https://github.com/TolgaDurman/com.unity.webrtc.git");
                }
                EditorGUI.EndDisabledGroup();
            }
#endif

            // ── Log Level ──────────────────────────────────────────────────────
            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                OptionLabel("Log Level",
                    "Controls how much UniPeek writes to the Unity Console.\nNone: silent  |  Error: errors only  |  Warning: errors + warnings  |  All: full diagnostics.");
                _logLevel = (LogLevel)EditorGUILayout.EnumPopup(_logLevel, GUILayout.ExpandWidth(true));
            }
            if (EditorGUI.EndChangeCheck())
            {
                UniPeekConstants.CurrentLogLevel = _logLevel;
                SavePrefs();
            }

            // ── Network Interface ──────────────────────────────────────────────
            GUILayout.Space(4f);
            DrawNetworkInterfaceDropdown();

            // ── Capture Method ─────────────────────────────────────────────────
            GUILayout.Space(4f);
            var mgr = ConnectionManager.Instance;
            CaptureMethod newMethod;
            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                OptionLabel("Capture Method",
                    "Camera Render: synchronous, works in Edit + Play Mode.\nAsync GPU Readback: non-blocking, ~1 frame of extra latency.");
                newMethod = (CaptureMethod)EditorGUILayout.EnumPopup(mgr.ActiveCaptureMethod, GUILayout.ExpandWidth(true));
            }
            if (EditorGUI.EndChangeCheck())
                mgr.SetCaptureMethod(newMethod);

            // ── Touch Gizmos ───────────────────────────────────────────────────
            GUILayout.Space(4f);
            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                OptionLabel("Show Touch Gizmos",
                    "Draws touch-position circles on the Game View when the phone sends touch events.");
                EditorGUILayout.Toggle(TouchGizmoOverlay.ShowGizmos, GUILayout.ExpandWidth(true));
            }
            if (EditorGUI.EndChangeCheck())
                TouchGizmoOverlay.ShowGizmos = !TouchGizmoOverlay.ShowGizmos;
        }

        // ── Network interface dropdown ────────────────────────────────────────

        private void RefreshNetworkInterfaces()
        {
            var candidates = NetworkInterfaceSelector.GetCandidates();

            _nicLabels    = new string[candidates.Count + 1];
            _nicIPs       = new string[candidates.Count];
            _nicLabels[0] = "Auto (best match)";

            for (int i = 0; i < candidates.Count; i++)
            {
                _nicLabels[i + 1] = candidates[i].Label;
                _nicIPs[i]        = candidates[i].IP;
            }

            // Restore saved selection (reset to Auto if the saved IP is no longer present).
            string savedIP = NetworkInterfaceSelector.GetSavedIP();
            _nicIndex = 0;
            if (!string.IsNullOrEmpty(savedIP))
            {
                for (int i = 0; i < _nicIPs.Length; i++)
                {
                    if (_nicIPs[i] == savedIP)
                    {
                        _nicIndex = i + 1;
                        break;
                    }
                }
            }
        }

        private void DrawNetworkInterfaceDropdown()
        {
            int newIndex;
            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                OptionLabel("Network Interface",
                    "Which network adapter UniPeek binds to.\nUse Auto for most setups. Change this if UniPeek advertises a virtual adapter (e.g. VPN or Hyper-V).");
                newIndex = EditorGUILayout.Popup(_nicIndex, _nicLabels, GUILayout.ExpandWidth(true));
            }
            if (EditorGUI.EndChangeCheck())
            {
                _nicIndex = newIndex;
                string selectedIP = _nicIndex == 0 ? string.Empty : _nicIPs[_nicIndex - 1];
                NetworkInterfaceSelector.SaveIP(selectedIP);
                QRCodeGenerator.Invalidate();

                if (_streaming)
                {
                    StopStreaming();
                    DoStartStreaming();
                }
            }

            if (_nicIndex == 0)
            {
                string bestIp = NetworkInterfaceSelector.GetBestIP();
                EditorGUILayout.HelpBox(
                    $"Auto-selected: {bestIp}  —  Change this if UniPeek advertises a virtual adapter address.",
                    MessageType.None);
            }
        }

        // ── Device list ───────────────────────────────────────────────────────

        private void DrawDeviceList()
        {
            var devices = ConnectionManager.Instance.ConnectedDevices;
            if (devices.Count == 0) return;

            DrawSectionLabel("Connected Devices");

            string toDisconnect = null;
            foreach (var d in devices)
            {
                using var card = new EditorGUILayout.HorizontalScope(EditorStyles.helpBox);
                DrawColorDot(ColGreen);
                GUILayout.Space(2f);
                if (d.IsPro && _proIcon != null)
                    GUILayout.Label(_proIcon, GUILayout.Width(16f), GUILayout.Height(16f));
                GUILayout.Label(d.DeviceName, GUILayout.ExpandWidth(true));
                GUILayout.Label(
                    d.ConnectedAt.ToLocalTime().ToString("HH:mm:ss"),
                    EditorStyles.miniLabel, GUILayout.Width(52f));
                if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(20f)))
                    toDisconnect = d.SessionId;
            }

            if (toDisconnect != null)
                ConnectionManager.Instance.DisconnectDevice(toDisconnect);

            GUILayout.Space(8f);
        }


        // ── Footer ────────────────────────────────────────────────────────────

        private void DrawFooter()
        {
            GUILayout.Label(
                "Pro features (multi-device, 1080p, 60 fps) unlocked via the UniPeek app.",
                EditorStyles.centeredGreyMiniLabel);
            GUILayout.Space(2f);

            using var row = new EditorGUILayout.HorizontalScope(EditorStyles.toolbar);
            GUILayout.Label("Port", EditorStyles.miniLabel, GUILayout.Width(28f));
            EditorGUI.BeginDisabledGroup(_streaming);
            var newPort = EditorGUILayout.IntField(_port, EditorStyles.toolbarTextField, GUILayout.Width(50f));
            if (newPort != _port && newPort > 1024 && newPort <= 65535)
            {
                _port = newPort;
                SavePrefs();
                QRCodeGenerator.Invalidate();
            }
            EditorGUI.EndDisabledGroup();
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Welcome", EditorStyles.toolbarButton, GUILayout.Width(62f)))
                UniPeekWelcomeWindow.Open();

            if (GUILayout.Button("Guide", EditorStyles.toolbarButton, GUILayout.Width(44f)))
                UniPeekGuideWindow.Open();

            if (GUILayout.Button("Docs", EditorStyles.toolbarButton, GUILayout.Width(38f)))
                Application.OpenURL("https://unipeek.app");

            if (GUILayout.Button("Reset FW", EditorStyles.toolbarButton, GUILayout.Width(58f)))
                FirewallHelper.ResetAndReConfigure();
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        private void DrawSectionLabel(string title)
        {
            GUILayout.Space(2f);
            using var row = new EditorGUILayout.HorizontalScope();
            GUILayout.Label(title.ToUpper(), _sectionLabelStyle);
            GUILayout.Space(4f);
        }

        /// <summary>
        /// Draws a label column with a trailing (i) icon. Hover is detected manually
        /// so Unity's native tooltip system is never triggered (avoids double tooltip).
        /// </summary>
        private void OptionLabel(string label, string tooltip)
        {
            GUILayout.Label(label, EditorStyles.label,
                GUILayout.Width(EditorGUIUtility.labelWidth - 18f));
            // No tooltip in GUIContent — we track hover ourselves to avoid Unity's native tooltip.
            GUILayout.Label(new GUIContent("ⓘ"), _infoIconStyle,
                GUILayout.Width(16f), GUILayout.Height(EditorGUIUtility.singleLineHeight));
            if (Event.current.type == EventType.Repaint &&
                GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
            {
                _hoveredTooltip = tooltip;
            }
            GUILayout.Space(2f);
        }

        /// <summary>
        /// Draws a floating tooltip box near the mouse when hovering over an (i) icon.
        /// Must be called at the very end of OnGUI.
        /// </summary>
        private void DrawTooltipOverlay()
        {
            if (string.IsNullOrEmpty(_hoveredTooltip)) return;

            var   content = new GUIContent(_hoveredTooltip);
            float maxW    = Mathf.Min(position.width - 24f, 260f);
            float textH   = _tooltipBoxStyle.CalcHeight(content, maxW - 16f);
            float height  = textH + 14f;
            var   mp      = Event.current.mousePosition;

            var rect = new Rect(mp.x + 16f, mp.y - height - 6f, maxW, height);
            rect.x = Mathf.Clamp(rect.x, 4f, position.width  - maxW   - 4f);
            rect.y = Mathf.Clamp(rect.y, 4f, position.height - height - 4f);

            // Solid background — no transparency
            var bgColor  = EditorGUIUtility.isProSkin
                ? new Color(0.15f, 0.15f, 0.15f, 1f)
                : new Color(0.90f, 0.90f, 0.90f, 1f);
            var rimColor = EditorGUIUtility.isProSkin
                ? new Color(0.40f, 0.40f, 0.40f, 1f)
                : new Color(0.60f, 0.60f, 0.60f, 1f);

            EditorGUI.DrawRect(rect, rimColor);
            EditorGUI.DrawRect(new Rect(rect.x + 1, rect.y + 1, rect.width - 2, rect.height - 2), bgColor);

            var textRect = new Rect(rect.x + 8f, rect.y + 7f, rect.width - 16f, textH);
            GUI.Label(textRect, _hoveredTooltip, _tooltipBoxStyle);

            Repaint();
        }

        private static void DrawColorDot(Color color, bool small = false)
        {
            float w = small ? 14f : 16f;
            var prev = GUI.color;
            GUI.color = color;
            GUILayout.Label("●", GUILayout.Width(w), GUILayout.Height(w));
            GUI.color = prev;
        }

        private static Color RttColor(float rttMs)
        {
            if (rttMs <= 0f)                          return ColGrey;
            if (rttMs < UniPeekConstants.RttGreenMs)  return ColGreen;
            if (rttMs < UniPeekConstants.RttYellowMs) return new Color(1f, 0.9f, 0f);
            if (rttMs < UniPeekConstants.RttOrangeMs) return new Color(1f, 0.5f, 0f);
            return new Color(0.9f, 0.2f, 0.2f);
        }

        private void InitStyles()
        {
            if (_stylesInitialized) return;
            _stylesInitialized = true;

            _titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize  = 13,
                alignment = TextAnchor.MiddleLeft,
            };

            _versionStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal    = { textColor = new Color(0.55f, 0.55f, 0.55f) },
            };

            _statusTextStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
            };

            _sectionLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontStyle = FontStyle.Bold,
                normal    = { textColor = new Color(0.50f, 0.50f, 0.50f) },
            };

            _infoIconStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontSize  = 10,
                alignment = TextAnchor.MiddleCenter,
                padding   = new RectOffset(1, 1, 0, 1),
                normal    = { textColor = new Color(0.40f, 0.75f, 1.00f) },
            };

            _tooltipBoxStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 12,
                wordWrap = true,
                normal   = { textColor = EditorGUIUtility.isProSkin ? new Color(0.85f, 0.85f, 0.85f) : new Color(0.10f, 0.10f, 0.10f) },
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // Streaming control
        // ─────────────────────────────────────────────────────────────────────

        private void StartStreaming()
        {
            if (_streaming) return;

            SavePrefs();
            Application.runInBackground = true;

            if (_requirePlayMode && !EditorApplication.isPlaying)
            {
                // Setting isPlaying triggers a domain reload — plant a flag so streaming
                // resumes once the domain is back up (EnteredPlayMode / OnEnable).
                EditorPrefs.SetBool(PrefPendingStart, true);
                EditorApplication.isPlaying = true;
                return;
            }

            DoStartStreaming();
        }

        private void DoStartStreaming()
        {
            ConnectionManager.Instance.StartStreaming(_port);
            _streaming = true;

            if (!_requirePlayMode)
                EditorPrefs.SetBool(UniPeekConstants.PrefPersistStreaming, true);

            Application.runInBackground = true;
            RefreshQR();
            Repaint();
        }

        private void StopStreaming()
        {
            if (!_streaming) return;
            ConnectionManager.Instance.StopStreaming();
            _streaming             = false;
            _autoStartedByPlayMode = false;
            _captureFps            = 0f;
            _encodeMs              = 0f;
            EditorPrefs.DeleteKey(UniPeekConstants.PrefPersistStreaming);
            DestroyQR();
            Repaint();
        }

        // ─────────────────────────────────────────────────────────────────────
        // ConnectionManager subscriptions
        // ─────────────────────────────────────────────────────────────────────

        private void SubscribeToManager()
        {
            var mgr = ConnectionManager.Instance;
            mgr.StateChanged       += OnStateChanged;
            mgr.DeviceConnected    += OnDeviceConnected;
            mgr.DeviceDisconnected += OnDeviceDisconnected;
            mgr.StatsUpdated       += OnStatsUpdated;
            mgr.RttUpdated         += OnRttUpdated;
        }

        private void UnsubscribeFromManager()
        {
            if (ConnectionManager.Instance == null) return;
            var mgr = ConnectionManager.Instance;
            mgr.StateChanged       -= OnStateChanged;
            mgr.DeviceConnected    -= OnDeviceConnected;
            mgr.DeviceDisconnected -= OnDeviceDisconnected;
            mgr.StatsUpdated       -= OnStatsUpdated;
            mgr.RttUpdated         -= OnRttUpdated;
        }

        private void OnStateChanged(ConnectionState _)  => Repaint();
        private void OnDeviceConnected(DeviceInfo _)    => Repaint();
        private void OnDeviceDisconnected(DeviceInfo _) => Repaint();

        private void OnStatsUpdated(float fps, float encodeMs)
        {
            _captureFps   = fps;
            _encodeMs     = encodeMs;
            _webRtcActive = ConnectionManager.Instance.WebRtcActive;
            Repaint();
        }

        private void OnRttUpdated(float rttMs)
        {
            _rttMs        = rttMs;
            _webRtcActive = ConnectionManager.Instance.WebRtcActive;
            Repaint();
        }

        // ─────────────────────────────────────────────────────────────────────
        // QR helpers
        // ─────────────────────────────────────────────────────────────────────

        private void RefreshQR()
            => _qrTexture = QRCodeGenerator.GetConnectionQR(
                _port, pixelsPerModule: 8);

        private void DestroyQR() => _qrTexture = null;

        // ─────────────────────────────────────────────────────────────────────
        // EditorPrefs + crash-safe file storage
        // ─────────────────────────────────────────────────────────────────────

        // EditorPrefs (NSUserDefaults on macOS) has a delayed disk flush — a hard
        // crash can lose the last write. The editor name is also written to a file
        // so it survives crashes (file I/O is synchronous / immediately flushed).
        private static readonly string EditorNameFilePath =
            System.IO.Path.Combine("UserSettings", "UniPeekEditorName.txt");

        private void LoadPrefs()
        {
            _requirePlayMode     = EditorPrefs.GetBool(UniPeekConstants.PrefAutoStopPlay, true);
            _autoStartOnPlayMode = EditorPrefs.GetBool(UniPeekConstants.PrefAutoStartOnPlay, false);
            _socketMode          = (SocketMode)EditorPrefs.GetInt(UniPeekConstants.PrefSocketMode, (int)SocketMode.WebRTC);
            _logLevel            = (LogLevel)EditorPrefs.GetInt(UniPeekConstants.PrefLogLevel, (int)LogLevel.All);
            _port                = EditorPrefs.GetInt(UniPeekConstants.PrefPort, UniPeekConstants.DefaultPort);
            UniPeekConstants.CurrentLogLevel = _logLevel;
            ConnectionManager.Instance.Config.MaxBitrateKbps = EditorPrefs.GetInt(
                UniPeekConstants.PrefWebRtcMaxBitrateKbps, UniPeekConstants.DefaultWebRtcMaxBitrateKbps);
            ConnectionManager.Instance.Config.WebRtcStunUrl = EditorPrefs.GetString(
                UniPeekConstants.PrefWebRtcStunUrl, string.Empty);

            // File takes priority — it's written synchronously so it's crash-safe.
            if (System.IO.File.Exists(EditorNameFilePath))
                _editorName = System.IO.File.ReadAllText(EditorNameFilePath);
            else
                _editorName = EditorPrefs.GetString(UniPeekConstants.PrefEditorName, string.Empty);
        }

        private void SavePrefs()
        {
            EditorPrefs.SetBool(UniPeekConstants.PrefAutoStopPlay, _requirePlayMode);
            EditorPrefs.SetBool(UniPeekConstants.PrefAutoStartOnPlay, _autoStartOnPlayMode);
            EditorPrefs.SetInt(UniPeekConstants.PrefSocketMode, (int)_socketMode);
            EditorPrefs.SetInt(UniPeekConstants.PrefLogLevel, (int)_logLevel);
            EditorPrefs.SetString(UniPeekConstants.PrefEditorName, _editorName);
            EditorPrefs.SetInt(UniPeekConstants.PrefPort, _port);

            // Also write to file for crash resilience.
            try
            {
                System.IO.Directory.CreateDirectory("UserSettings");
                System.IO.File.WriteAllText(EditorNameFilePath, _editorName);
            }
            catch { /* non-critical */ }
        }
    }
}
