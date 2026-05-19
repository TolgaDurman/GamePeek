using System;
using System.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace GamePeek
{
    /// <summary>Strategy used to capture each frame.</summary>
    public enum CaptureMethod
    {
        /// <summary>
        /// <c>Camera.Render()</c> to a <see cref="RenderTexture"/> then <c>ReadPixels</c>.
        /// Works in both Play and Edit Mode. No Game View dependency.
        /// </summary>
        CameraRender,

        /// <summary>
        /// <c>Camera.Render()</c> to a <see cref="RenderTexture"/> then
        /// <c>AsyncGPUReadback.Request()</c>. Non-blocking — no CPU stall — at the cost of
        /// approximately one frame of additional latency. No Game View dependency.
        /// </summary>
        AsyncGPUReadback,
    }

    /// <summary>
    /// Captures the composited Game View on the Unity main thread and forwards
    /// each frame to a <see cref="FrameEncoder"/> for off-thread JPEG encoding.
    /// <para>
    /// <b>Capture strategy:</b>
    /// <list type="bullet">
    ///   <item>In <b>Play mode</b>: a hidden <see cref="CaptureHelper"/> MonoBehaviour
    ///         runs a <c>WaitForEndOfFrame</c> coroutine then calls
    ///         <c>ScreenCapture.CaptureScreenshotAsTexture()</c>, capturing the full
    ///         Game View including UI, post-processing, and overlays.</item>
    ///   <item>In <b>Edit mode</b>: renders the main camera directly to a
    ///         <see cref="RenderTexture"/> (Screen-space UI overlays are not captured).</item>
    ///   <item>Frames are scaled to the target resolution via a GPU blit if the
    ///         captured size differs from <see cref="SetResolution"/>.</item>
    ///   <item>If the encoder is busy the frame is dropped immediately to avoid
    ///         main-thread stalls.</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class FrameCapture
    {
        // ── Configuration ─────────────────────────────────────────────────────
        private int           _targetWidth;
        private int           _targetHeight;
        private float         _interval;    // seconds between captures = 1 / fpsCap
        private CaptureMethod _method = CaptureMethod.CameraRender;

        // ── State ─────────────────────────────────────────────────────────────
        private bool   _active;
        private double _lastCaptureTime;
        private bool   _hooked;
        private bool   _asyncRequestInFlight;

        // ── Dependencies ──────────────────────────────────────────────────────
        private readonly FrameEncoder _encoder;

        // ── Play-mode overlay capture ─────────────────────────────────────────
        private CaptureHelper _helper;

        // ── WebRTC bypass ─────────────────────────────────────────────────────
        private bool _useWebRTC;

        /// <summary>
        /// When <c>true</c>, the JPEG capture pipeline is suspended.
        /// Set this while WebRTC is active; the WebRTC video track handles
        /// capture independently via <c>Camera.CaptureStreamTrack</c>.
        /// </summary>
        public bool UseWebRTC
        {
            get => _useWebRTC;
            set => _useWebRTC = value;
        }

        // ── Stats ─────────────────────────────────────────────────────────────
        private double _fpsWindowStart;
        private int    _fpsWindowCount;
        private float  _smoothedFps;

        /// <summary>Smoothed capture rate displayed in the Editor window (frames/second).</summary>
        public float SmoothedFps => _smoothedFps;

        // ── Constructor ───────────────────────────────────────────────────────

        /// <summary>Creates the capture component.</summary>
        /// <param name="encoder">Encoder to pass captured frames to.</param>
        /// <param name="targetWidth">Streaming width in pixels.</param>
        /// <param name="targetHeight">Streaming height in pixels.</param>
        /// <param name="fpsCap">Maximum capture rate (frames per second).</param>
        public FrameCapture(FrameEncoder encoder, int targetWidth, int targetHeight, int fpsCap)
        {
            _encoder      = encoder;
            _targetWidth  = targetWidth;
            _targetHeight = targetHeight;
            _interval     = fpsCap > 0 ? 1f / fpsCap : 1f / 30f;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Activates frame capture. Hooks into <see cref="EditorApplication.update"/>.</summary>
        public void Start()
        {
            if (_active) return;
            _active         = true;
            _fpsWindowStart = EditorApplication.timeSinceStartup;
            _fpsWindowCount       = 0;
            _smoothedFps          = 0f;
            _lastCaptureTime      = EditorApplication.timeSinceStartup - _interval;
            _asyncRequestInFlight = false;

            if (!_hooked)
            {
                EditorApplication.update += OnEditorUpdate;
                _hooked = true;
            }
        }

        /// <summary>Deactivates frame capture. Unhooks from <see cref="EditorApplication.update"/>.</summary>
        public void Stop()
        {
            _active = false;
            if (_hooked)
            {
                EditorApplication.update -= OnEditorUpdate;
                _hooked = false;
            }
            DestroyHelper();
        }

        /// <summary>Updates the streaming resolution. Takes effect on the next capture.</summary>
        public void SetResolution(int width, int height)
        {
            _targetWidth  = width;
            _targetHeight = height;
        }

        /// <summary>Updates the FPS cap. Takes effect on the next capture.</summary>
        public void SetFpsCap(int fpsCap)
            => _interval = fpsCap > 0 ? 1f / fpsCap : 1f / 30f;

        /// <summary>Switches the capture strategy. Takes effect on the next capture.</summary>
        public void SetCaptureMethod(CaptureMethod method)
        {
            _method = method;
            if (method != CaptureMethod.AsyncGPUReadback)
                _asyncRequestInFlight = false;
        }

        // ── Editor update ─────────────────────────────────────────────────────

        private void OnEditorUpdate()
        {
            if (!_active) return;

            // When WebRTC is active it drives its own video track — skip JPEG.
            if (_useWebRTC) return;

            // Sync helper lifetime with Play mode.
            if (Application.isPlaying)
                EnsureHelper();
            else if (_helper != null)
                DestroyHelper();

            double now = EditorApplication.timeSinceStartup;
            if (now - _lastCaptureTime < _interval) return;

            _lastCaptureTime = now;

            if (Application.isPlaying)
            {
                // CaptureHelper uses ReadPixels from the framebuffer after
                // WaitForEndOfFrame — includes Screen Space Overlay canvases.
                if (_helper != null) _helper.RequestCapture();
            }
            else if (_method == CaptureMethod.AsyncGPUReadback)
            {
                CaptureFromCameraAsync();
            }
            else
            {
                CaptureFromCamera();
            }
        }

        // ── Play-mode helper management ───────────────────────────────────────

        private void EnsureHelper()
        {
            if (_helper != null) return;
            var go = new GameObject("[GamePeek] CaptureHelper") { hideFlags = HideFlags.HideAndDontSave };
            _helper          = go.AddComponent<CaptureHelper>();
            _helper.OnFrame  = OnHelperFrame;
        }

        private void DestroyHelper()
        {
            if (_helper == null) return;
            if (_helper.gameObject != null)
                UnityEngine.Object.DestroyImmediate(_helper.gameObject);
            _helper = null;
        }

        /// <summary>
        /// Callback from <see cref="CaptureHelper"/> — runs on the main thread immediately
        /// after <c>WaitForEndOfFrame</c> with the full composited screen texture.
        /// </summary>
        private void OnHelperFrame(Texture2D screenTex)
        {
            if (!_active || _encoder.IsEncoding)
            {
                UnityEngine.Object.DestroyImmediate(screenTex);
                return;
            }

            Texture2D toEncode  = null;
            Texture2D toDestroy = null; // screenTex reference kept for cleanup

            try
            {
                bool needsScale = screenTex.width != _targetWidth || screenTex.height != _targetHeight;

                if (needsScale)
                {
                    var rt = RenderTexture.GetTemporary(
                        _targetWidth, _targetHeight, 0,
                        RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    Graphics.Blit(screenTex, rt);

                    toDestroy = screenTex; // will be cleaned up in finally

                    var prevActive = RenderTexture.active;
                    RenderTexture.active = rt;
                    // The sRGB RT already holds gamma-corrected bytes; ReadPixels copies them
                    // as-is (no sRGB→linear conversion).  Mark the texture as non-linear so
                    // EncodeToJPG doesn't apply a second gamma pass and whiten the image.
                    toEncode = new Texture2D(_targetWidth, _targetHeight, TextureFormat.RGB24, false, false);
                    toEncode.ReadPixels(new Rect(0, 0, _targetWidth, _targetHeight), 0, 0);
                    toEncode.Apply();
                    RenderTexture.active = prevActive;
                    RenderTexture.ReleaseTemporary(rt);
                }
                else
                {
                    // Hand the texture directly to the encoder; no extra copy needed.
                    toEncode  = screenTex;
                    toDestroy = null;
                }

                if (_encoder.SubmitFrame(toEncode)) { toEncode = null; UpdateFpsStats(); }
            }
            catch (Exception ex)
            {
                GamePeekConstants.LogWarning($"[Capture] Screen capture processing failed: {ex.Message}");
            }
            finally
            {
                if (toDestroy != null) UnityEngine.Object.DestroyImmediate(toDestroy);
                if (toEncode  != null) UnityEngine.Object.DestroyImmediate(toEncode);
            }
        }

        // ── Camera render (Edit mode) ─────────────────────────────────────────

        private void CaptureFromCamera()
        {
            if (_encoder.IsEncoding) return;

            // Prefer Camera.main; fall back to any enabled camera
            var cam = Camera.main;
            if (cam == null)
            {
                var all = Camera.allCameras;
                cam = all.Length > 0 ? all[0] : null;
            }

            if (cam == null) return;

            RenderTexture rt        = null;
            RenderTexture prevActive = RenderTexture.active;
            var           prevTarget = cam.targetTexture;
            Texture2D     toEncode  = null;

            try
            {
                // sRGB read-write: prevents Graphics.Blit from linearising the
                // captured pixels in Linear-colour-space projects, which would
                // make colours appear washed-out on the phone.
                rt = RenderTexture.GetTemporary(
                    _targetWidth, _targetHeight, 24,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = prevTarget;

                RenderTexture.active = rt;
                // The sRGB RT already holds gamma-corrected bytes; ReadPixels copies them
                // as-is (no sRGB→linear conversion).  Mark the texture as non-linear so
                // EncodeToJPG doesn't apply a second gamma pass and whiten the image.
                toEncode = new Texture2D(_targetWidth, _targetHeight, TextureFormat.RGB24, false, false);
                toEncode.ReadPixels(new Rect(0, 0, _targetWidth, _targetHeight), 0, 0);
                toEncode.Apply();
                RenderTexture.active = prevActive;

                if (_encoder.SubmitFrame(toEncode)) { toEncode = null; UpdateFpsStats(); }
            }
            catch (Exception ex)
            {
                cam.targetTexture    = prevTarget;
                RenderTexture.active = prevActive;
                GamePeekConstants.LogWarning($"[Capture] Camera render failed: {ex.Message}");
            }
            finally
            {
                if (rt      != null) RenderTexture.ReleaseTemporary(rt);
                if (toEncode!= null) UnityEngine.Object.DestroyImmediate(toEncode);
            }
        }

        // ── Camera → AsyncGPUReadback (Edit mode, non-blocking) ───────────────

        private void CaptureFromCameraAsync()
        {
            if (_asyncRequestInFlight || _encoder.IsEncoding) return;

            var cam = Camera.main;
            if (cam == null)
            {
                var all = Camera.allCameras;
                cam = all.Length > 0 ? all[0] : null;
            }
            if (cam == null) return;

            RenderTexture rt        = null;
            var           prevTarget = cam.targetTexture;

            try
            {
                rt = RenderTexture.GetTemporary(
                    _targetWidth, _targetHeight, 24,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = prevTarget;
            }
            catch (Exception ex)
            {
                cam.targetTexture = prevTarget;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                GamePeekConstants.LogWarning($"[Capture] AsyncGPU camera render failed: {ex.Message}");
                return;
            }

            _asyncRequestInFlight = true;

            // Request non-blocking GPU→CPU readback. Callback fires on main thread.
            AsyncGPUReadback.Request(rt, 0, TextureFormat.RGB24, req =>
            {
                RenderTexture.ReleaseTemporary(rt);
                _asyncRequestInFlight = false;

                if (req.hasError) return;
                if (_encoder.IsEncoding) return;

                Texture2D tex = null;
                try
                {
                    // AsyncGPUReadback returns raw sRGB bytes from the sRGB RT (no color-space
                    // conversion).  Mark the texture as non-linear so EncodeToJPG doesn't apply
                    // a second gamma pass and whiten the image in linear color space projects.
                    tex = new Texture2D(_targetWidth, _targetHeight, TextureFormat.RGB24, false, false);
                    tex.LoadRawTextureData(req.GetData<byte>());
                    tex.Apply();

                    if (_encoder.SubmitFrame(tex)) { tex = null; UpdateFpsStats(); }
                }
                catch (Exception ex)
                {
                    GamePeekConstants.LogWarning($"[Capture] AsyncGPU readback processing failed: {ex.Message}");
                }
                finally
                {
                    if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                }
            });
        }

        // ── Stats ─────────────────────────────────────────────────────────────

        private void UpdateFpsStats()
        {
            _fpsWindowCount++;
            double elapsed = EditorApplication.timeSinceStartup - _fpsWindowStart;
            if (elapsed >= 1.0)
            {
                _smoothedFps    = (float)(_fpsWindowCount / elapsed);
                _fpsWindowCount = 0;
                _fpsWindowStart = EditorApplication.timeSinceStartup;
            }
        }
    }

    /// <summary>
    /// Hidden MonoBehaviour that drives Play-mode overlay capture via
    /// <c>WaitForEndOfFrame</c> + <c>ScreenCapture.CaptureScreenshotAsTexture()</c>.
    /// Created and destroyed by <see cref="FrameCapture"/> as needed.
    /// </summary>
    internal sealed class CaptureHelper : MonoBehaviour
    {
        /// <summary>
        /// Invoked on the main thread with the fully composited screen texture.
        /// The callee is responsible for destroying the texture.
        /// </summary>
        internal Action<Texture2D> OnFrame;

        private bool _pending;

        /// <summary>Schedules one capture at the end of the current frame.</summary>
        internal void RequestCapture()
        {
            if (_pending) return;
            _pending = true;
            StartCoroutine(DoCaptureEndOfFrame());
        }

        private IEnumerator DoCaptureEndOfFrame()
        {
            yield return new WaitForEndOfFrame();
            _pending = false;

            // ReadPixels from the screen framebuffer after all rendering is
            // complete (includes Screen Space Overlay canvases and all
            // post-processing).  The framebuffer stores the display-ready
            // sRGB output, so we mark the texture as non-linear to prevent
            // EncodeToJPG from applying a second gamma pass.  This also
            // avoids the extra processing ScreenCapture.CaptureScreenshotAsTexture
            // applies in Device Simulator mode which causes whitening in
            // linear colour-space projects.
            Texture2D tex = new Texture2D(Screen.width, Screen.height,
                TextureFormat.RGB24, false, false);
            tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
            tex.Apply();
            try
            {
                OnFrame?.Invoke(tex);
            }
            catch (Exception ex)
            {
                GamePeekConstants.LogWarning($"[Capture] CaptureHelper OnFrame callback threw: {ex.Message}");
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }
    }
}
