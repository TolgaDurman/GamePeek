using System;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;

namespace GamePeek
{
    /// <summary>
    /// Encodes raw captured frame data as JPEG on a background thread and
    /// delivers the result to the <see cref="GamePeekWebSocketServer"/>.
    /// <para>
    /// The encoder operates as a single-slot pipeline: a new encode is
    /// only started when the previous one has completed, implementing
    /// implicit back-pressure so the main thread is never stalled.
    /// </para>
    /// <para>
    /// <b>Threading model:</b>
    /// <list type="bullet">
    ///   <item><see cref="SubmitFrame"/> is called from the Unity <em>main</em> thread.</item>
    ///   <item>All JPEG encoding work runs on a <see cref="Task"/> (thread pool).</item>
    ///   <item>Broadcast is called from the thread-pool thread; websocket-sharp
    ///         handles its own thread safety.</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class FrameEncoder
    {
        // ── State ─────────────────────────────────────────────────────────────
        private volatile bool _encoding;
        private GamePeekWebSocketServer _server;
        private int   _quality = 75;

        // ── Stats ─────────────────────────────────────────────────────────────
        private volatile float _lastEncodeMs;

        /// <summary>Milliseconds taken by the most recent encode operation.</summary>
        public float LastEncodeMs => _lastEncodeMs;

        /// <summary>
        /// <c>true</c> while an encode is in progress.
        /// The capture loop uses this to skip frames during back-pressure.
        /// </summary>
        public bool IsEncoding => _encoding;

        // ── Constructor ───────────────────────────────────────────────────────

        /// <summary>
        /// Creates the encoder. <paramref name="server"/> may be null initially and
        /// set later via <see cref="SetServer"/>.
        /// </summary>
        /// <param name="server">WebSocket server to broadcast frames to.</param>
        /// <param name="quality">Initial JPEG quality [0, 100] (default 75).</param>
        public FrameEncoder(GamePeekWebSocketServer server, int quality = 75)
        {
            _server  = server;
            _quality = Mathf.Clamp(quality, 1, 100);
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Sets or replaces the WebSocket server used for broadcasting.</summary>
        public void SetServer(GamePeekWebSocketServer server) => _server = server;

        /// <summary>
        /// Updates the JPEG quality used for subsequent encodes.
        /// </summary>
        /// <param name="quality">Quality value [1, 100].</param>
        public void SetQuality(int quality) => _quality = Mathf.Clamp(quality, 1, 100);

        /// <summary>
        /// Resets statistics counters (call when streaming (re-)starts).
        /// </summary>
        public void ResetStats()
        {
            _lastEncodeMs = 0f;
        }

        /// <summary>
        /// Submits a captured <see cref="Texture2D"/> for background JPEG encoding
        /// and subsequent broadcast.
        /// <para>
        /// <b>Must be called from the Unity main thread.</b>
        /// The caller must <em>not</em> destroy <paramref name="texture"/> — the encoder
        /// takes ownership and schedules destruction on the main thread via
        /// <see cref="UnityEditor.EditorApplication.delayCall"/> after encoding.
        /// </para>
        /// </summary>
        /// <param name="texture">
        /// Texture to encode. Must have valid CPU-side pixel data
        /// (i.e. <c>Apply()</c> has already been called or the texture came from
        /// <c>ScreenCapture.CaptureScreenshotAsTexture</c> which does this automatically).
        /// </param>
        /// <returns>
        /// <c>true</c> if the frame was accepted for encoding;
        /// <c>false</c> if a previous encode is still in progress (frame is dropped
        /// and the caller should destroy <paramref name="texture"/> itself).
        /// </returns>
        public bool SubmitFrame(Texture2D texture)
        {
            if (_encoding) return false;
            if (_server == null || _server.ConnectedCount == 0)
            {
                UnityEngine.Object.DestroyImmediate(texture);
                return false;
            }

            _encoding = true;

            // EncodeToJPG must run on the main thread (Unity requirement).
            var sw = Stopwatch.StartNew();
            byte[] jpeg = null;
            try
            {
                jpeg = ImageConversion.EncodeToJPG(texture, _quality);
            }
            catch (Exception ex)
            {
                GamePeekConstants.LogWarning($"[Encoder] JPEG encode failed: {ex.Message}");
            }
            finally
            {
                sw.Stop();
                _lastEncodeMs = (float)sw.Elapsed.TotalMilliseconds;
                UnityEngine.Object.DestroyImmediate(texture);
                _encoding = false;
            }

            // Broadcast is network I/O — push to background to avoid blocking the main thread.
            if (jpeg != null && jpeg.Length > 0)
            {
                var jpegRef = jpeg;
                Task.Run(() =>
                {
                    try { _server?.BroadcastFrame(jpegRef); }
                    catch (Exception ex)
                    {
                        GamePeekConstants.LogWarning($"[Encoder] Broadcast failed: {ex.Message}");
                    }
                });
            }

            return true;
        }
    }
}
