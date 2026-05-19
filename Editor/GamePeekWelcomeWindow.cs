using UnityEditor;
using UnityEngine;

namespace GamePeek
{
    /// <summary>
    /// Welcome window that opens automatically the first time GamePeek is imported
    /// into a project (or when a new version is detected).
    /// Access manually via  Window → GamePeek Welcome.
    /// </summary>
    internal sealed class GamePeekWelcomeWindow : EditorWindow
    {
        // ── Paths ─────────────────────────────────────────────────────────────
        private const string QrTexturePath   = "Assets/Plugins/GamePeek/Textures/qr-code.png";
        private const string LogoTexturePath = "Assets/Plugins/GamePeek/Textures/gamepeek-logo.png";

        // ── EditorPrefs key ───────────────────────────────────────────────────
        // Stores the last version for which the welcome window was shown.
        // Set to the literal string "never" by "Don't show again".
        private const string PrefShownVersion = "GamePeek_WelcomeShownVersion";

        // ── State ─────────────────────────────────────────────────────────────
        private Texture2D _qrTexture;
        private Texture2D _logoTexture;
        private Vector2   _scrollPos;

        // Styles are lazy-initialized inside OnGUI after the skin is ready.
        private GUIStyle _headerStyle;
        private GUIStyle _subheaderStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _stepStyle;
        private bool     _stylesInitialized;

        // ─────────────────────────────────────────────────────────────────────
        // Auto-show on import
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Nested postprocessor detects when the GamePeek package files land in
        /// the project and schedules the welcome window for the next editor frame.
        /// </summary>
        private sealed class ImportDetector : AssetPostprocessor
        {
#pragma warning disable IDE0060 // unused parameters required by Unity callback signature
            private static void OnPostprocessAllAssets(
                string[] importedAssets,
                string[] deletedAssets,
                string[] movedAssets,
                string[] movedFromAssetPaths)
#pragma warning restore IDE0060
            {
                foreach (var path in importedAssets)
                {
                    // Trigger on the constants file — it is always present and
                    // uniquely identifies a GamePeek import.
                    if (path.StartsWith("Assets/Plugins/GamePeek/", System.StringComparison.OrdinalIgnoreCase)
                        && path.EndsWith("GamePeek.cs", System.StringComparison.OrdinalIgnoreCase))
                    {
                        ShowIfNew();
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Opens the welcome window if it has not been shown for the current version.
        /// Safe to call at any time; deferred via <see cref="EditorApplication.delayCall"/>
        /// so it always runs after the import pipeline finishes.
        /// </summary>
        internal static void ShowIfNew()
        {
            var shownVersion = EditorPrefs.GetString(PrefShownVersion, string.Empty);
            if (shownVersion == GamePeekConstants.Version || shownVersion == "never") return;

            EditorApplication.delayCall += () =>
            {
                EditorPrefs.SetString(PrefShownVersion, GamePeekConstants.Version);
                Open();
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // Menu item
        // ─────────────────────────────────────────────────────────────────────

        // [MenuItem("Window/GamePeek Welcome")]
        public static void Open()
        {
            var window = GetWindow<GamePeekWelcomeWindow>(
                utility: true,
                title: "Welcome to GamePeek",
                focus: true);
            window.minSize = new Vector2(400f, 520f);
            window.maxSize = new Vector2(500f, 720f);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Lifecycle
        // ─────────────────────────────────────────────────────────────────────

        private void OnEnable()
        {
            _qrTexture   = AssetDatabase.LoadAssetAtPath<Texture2D>(QrTexturePath);
            _logoTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(LogoTexturePath);
        }

        // ─────────────────────────────────────────────────────────────────────
        // GUI
        // ─────────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            InitStyles();

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            // Header ──────────────────────────────────────────────────────────
            GUILayout.Space(12f);

            if (_logoTexture != null)
            {
                var logoRect = GUILayoutUtility.GetRect(
                    GUIContent.none, GUIStyle.none,
                    GUILayout.Height(48f), GUILayout.ExpandWidth(true));
                GUI.DrawTexture(logoRect, _logoTexture, ScaleMode.ScaleToFit);
            }

            GUILayout.Space(6f);
            GUILayout.Label("Welcome to GamePeek", _headerStyle);
            GUILayout.Label($"v{GamePeekConstants.Version}", EditorStyles.centeredGreyMiniLabel);
            GUILayout.Space(6f);
            EditorGUILayout.LabelField(
                "Stream the Unity Game View to the GamePeek app on your iOS or Android device " +
                "in real-time over Wi-Fi.",
                _bodyStyle);

            GUILayout.Space(10f);
            DrawSeparator();
            GUILayout.Space(8f);

            // QR code ─────────────────────────────────────────────────────────
            GUILayout.Label("Download the App", _subheaderStyle);
            GUILayout.Space(4f);
            EditorGUILayout.LabelField(
                "Scan the QR code to download the GamePeek companion app.",
                _bodyStyle);
            GUILayout.Space(6f);

            if (_qrTexture != null)
            {
                var qrRect = GUILayoutUtility.GetRect(
                    GUIContent.none, GUIStyle.none,
                    GUILayout.Height(170f), GUILayout.ExpandWidth(true));
                GUI.DrawTexture(qrRect, _qrTexture, ScaleMode.ScaleToFit);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    $"QR texture not found.\nExpected path: {QrTexturePath}",
                    MessageType.Warning);
            }

            GUILayout.Space(8f);
            DrawSeparator();
            GUILayout.Space(8f);

            // Quick start ─────────────────────────────────────────────────────
            GUILayout.Label("Quick Start", _subheaderStyle);
            GUILayout.Space(4f);
            DrawStep("1", "Open  Window \u2192 GamePeek.");
            DrawStep("2", "Click  \u25b6 Start Streaming  — a QR code appears.");
            DrawStep("3", "Open the GamePeek app on your phone and tap  Scan QR.");
            DrawStep("4", "Point the camera at the QR code in the Editor.");
            DrawStep("5", "The live Game View streams to your device.");

            GUILayout.Space(8f);
            DrawSeparator();
            GUILayout.Space(8f);

            // Highlights ──────────────────────────────────────────────────────
            GUILayout.Label("Highlights", _subheaderStyle);
            GUILayout.Space(4f);
            EditorGUILayout.LabelField("\u2022  JPEG streaming over WebSocket, WebRTC low-latency mode", _bodyStyle);
            EditorGUILayout.LabelField("\u2022  mDNS auto-discovery — no manual IP required", _bodyStyle);
            EditorGUILayout.LabelField("\u2022  Touch, gyroscope & accelerometer injection", _bodyStyle);
            EditorGUILayout.LabelField("\u2022  Works in both Edit Mode and Play Mode", _bodyStyle);
            EditorGUILayout.LabelField("\u2022  Windows \u2022 macOS \u2022 Linux", _bodyStyle);

            GUILayout.Space(12f);

            // Action buttons ──────────────────────────────────────────────────
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Open GamePeek", GUILayout.Height(32f)))
                {
                    GamePeekWindow.ShowWindow();
                    Close();
                }

                if (GUILayout.Button("Close", GUILayout.Height(32f), GUILayout.Width(80f)))
                {
                    Close();
                }
            }

            GUILayout.Space(6f);

            // "Don't show again" ──────────────────────────────────────────────
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Don't show on future imports", EditorStyles.miniButton))
                {
                    EditorPrefs.SetString(PrefShownVersion, "never");
                    Close();
                }
                GUILayout.FlexibleSpace();
            }

            GUILayout.Space(10f);
            EditorGUILayout.EndScrollView();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        private void InitStyles()
        {
            if (_stylesInitialized) return;
            _stylesInitialized = true;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize  = 18,
                alignment = TextAnchor.MiddleCenter,
            };

            _subheaderStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
            };

            _bodyStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                fontSize = 11,
            };

            _stepStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                fontSize = 11,
                padding  = new RectOffset(4, 0, 1, 1),
            };
        }

        private static void DrawSeparator()
        {
            var rect = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, new Color(0.35f, 0.35f, 0.35f, 0.6f));
        }

        private void DrawStep(string number, string text)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label($"{number}.", EditorStyles.boldLabel, GUILayout.Width(20f));
                GUILayout.Label(text, _stepStyle);
            }
        }
    }
}
