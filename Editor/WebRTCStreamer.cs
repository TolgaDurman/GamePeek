// Entire file is compiled only when com.unity.webrtc is present in the project.
#if UNITY_WEBRTC

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Unity.EditorCoroutines.Editor;
using Unity.WebRTC;
using UnityEditor;
using UnityEngine;

namespace UniPeek
{
    /// <summary>
    /// Manages a single WebRTC peer connection that streams the Unity Game View
    /// to the Flutter companion app.
    /// <para>
    /// <b>Video:</b> captures the composited Game View at end-of-frame into a
    /// <see cref="RenderTexture"/> consumed by <see cref="VideoStreamTrack"/>.
    /// </para>
    /// <para>
    /// <b>Input:</b> received via an <see cref="RTCDataChannel"/> named "input"
    /// and forwarded to <see cref="InputInjector"/> through <see cref="DataChannelMessage"/>.
    /// </para>
    /// <para>
    /// <b>Signaling:</b> caller is responsible for routing offer/answer/ICE over
    /// the existing WebSocket by subscribing to <see cref="OfferReady"/> and
    /// <see cref="IceCandidateReady"/>, then calling <see cref="SetRemoteAnswer"/>
    /// and <see cref="AddIceCandidate"/> when the remote peer's messages arrive.
    /// </para>
    /// </summary>
    internal sealed class WebRTCStreamer : IDisposable
    {
        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>Fired on the WebRTC thread when an SDP offer is ready for the remote peer.</summary>
        public event Action<string> OfferReady;

        /// <summary>Fired on the WebRTC thread when a local ICE candidate is gathered.</summary>
        public event Action<string, string, int> IceCandidateReady; // (candidate, sdpMid, sdpMLineIndex)

        /// <summary>Fired (main-thread safe via Enqueue) when the P2P connection is established.</summary>
        public event Action Connected;

        /// <summary>Fired (main-thread safe via Enqueue) when the P2P connection is lost or fails.</summary>
        public event Action Disconnected;

        /// <summary>Fired when a UTF-8 JSON message arrives on the DataChannel.</summary>
        public event Action<string> DataChannelMessage;

        // ── Configuration ─────────────────────────────────────────────────────
        private readonly int _width;
        private readonly int _height;
        private const double NegotiationTimeoutSeconds = 12.0;
        private const double IceDisconnectedGraceSeconds = 3.0;
        private const int MinFpsCap = 1;
        private const int MaxFpsCap = 120;
        private const int MinBitrateKbps = 100;
        private const int MaxBitrateKbps = 50_000;
        private double _captureInterval; // seconds between frames = 1 / fpsCap
        private double _lastCaptureTime;
        private int _maxBitrateKbps;
        private readonly string _stunUrl;

        // ── WebRTC objects ────────────────────────────────────────────────────
        private RTCPeerConnection _pc;
        private VideoStreamTrack  _videoTrack;
        private AudioStreamTrack  _audioTrack;
        private MediaStream       _mediaStream;
        private RTCDataChannel    _dataChannel;
        private readonly List<RTCDataChannel> _remoteDataChannels = new();

        private bool _disposed;
        private bool _mainThreadQueueHooked;
        private int _iceDisconnectGeneration;
        private readonly ConcurrentQueue<Action> _mainThreadActions = new();

        private EditorCoroutine _offerCoroutine;
        private EditorCoroutine _negotiationTimeoutCoroutine;
        private EditorCoroutine _webRtcUpdateCoroutine;
        private EditorCoroutine _cameraLoopCoroutine;
        private EditorCoroutine _iceDisconnectedGraceCoroutine;

        // ICE candidate buffer — candidates may arrive before the SDP answer is
        // applied (SetRemoteDescription yields 1+ editor ticks).  Mirror what the
        // Flutter side does: queue them and drain once the remote description is set.
        private bool _remoteDescriptionSet;
        private readonly List<RTCIceCandidate> _pendingCandidates = new();

        // Render texture fed directly to the VideoStreamTrack
        private RenderTexture _renderTexture;

        // Play Mode overlay capture (same CaptureHelper as JPEG pipeline)
        private CaptureHelper _captureHelper;
        private double _fpsWindowStart;
        private int _fpsWindowCount;
        private float _smoothedCaptureFps;

        // ── Constructor ───────────────────────────────────────────────────────

        /// <param name="width">Video width (pixels). Defaults to 1280.</param>
        /// <param name="height">Video height (pixels). Defaults to 720.</param>
        /// <param name="fpsCap">Maximum capture rate (frames/second). Defaults to 30.</param>
        /// <param name="maxBitrateKbps">Maximum video bitrate in kbps. Defaults to 10 000 (10 Mbps).</param>
        public WebRTCStreamer(int width = 1280, int height = 720, int fpsCap = 30,
                              int maxBitrateKbps = UniPeekConstants.DefaultWebRtcMaxBitrateKbps,
                              string stunUrl = "")
        {
            _width           = width;
            _height          = height;
            _captureInterval = FpsCapToInterval(fpsCap);
            _maxBitrateKbps  = ClampBitrate(maxBitrateKbps);
            _stunUrl         = string.IsNullOrWhiteSpace(stunUrl) ? string.Empty : stunUrl.Trim();
        }

        /// <summary>Smoothed WebRTC capture rate displayed in the Editor window.</summary>
        public float SmoothedCaptureFps => _smoothedCaptureFps;

        /// <summary>Updates the FPS cap. Takes effect on the next capture.</summary>
        public void SetFpsCap(int fpsCap)
            => _captureInterval = FpsCapToInterval(fpsCap);

        /// <summary>Updates the maximum video bitrate and re-applies it to any active senders.</summary>
        public void SetMaxBitrate(int kbps)
        {
            _maxBitrateKbps = ClampBitrate(kbps);
            ApplyBitrateSettings();
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Initialises the WebRTC engine, creates the peer connection, adds the
        /// camera video track and data channel, then starts creating an SDP offer.
        /// <b>Must be called from the Unity main thread.</b>
        /// </summary>
        public void StartNegotiation()
        {
            if (_pc != null || _disposed) return;
            HookMainThreadQueue();

            var config = new RTCConfiguration
            {
                iceServers = string.IsNullOrEmpty(_stunUrl)
                    ? Array.Empty<RTCIceServer>()
                    : new[] { new RTCIceServer { urls = new[] { _stunUrl } } },
            };
            if (!string.IsNullOrEmpty(_stunUrl))
                UniPeekConstants.Log($"[WebRTC] Using STUN server: {_stunUrl}");

            _pc = new RTCPeerConnection(ref config);
            _pc.OnIceCandidate          = OnIceCandidate;
            _pc.OnIceConnectionChange   = OnIceConnectionChange;
            _pc.OnConnectionStateChange = OnConnectionStateChange;
            _pc.OnDataChannel           = OnRemoteDataChannel;

            // ── Video track ────────────────────────────────────────────────────
            // sRGB read-write ensures the camera's linear output is gamma-corrected
            // when written to the RT, matching the WebSocket/JPEG capture path.
            // Clone-camera approaches (CopyFrom) do not copy UniversalAdditionalCameraData
            // so URP skips its final sRGB blit — hence we drive Camera.main directly.
            _renderTexture = new RenderTexture(_width, _height, 24, RenderTextureFormat.BGRA32, RenderTextureReadWrite.sRGB);
            _renderTexture.Create();
            // In a linear project VideoStreamTrack's internal blit (VerticalFlipCopy) runs
            // outside a camera context where GL.sRGBWrite defaults to false.  On D3D11
            // Unity then uses a UNORM (non-sRGB) RTV, so the linear→sRGB conversion is
            // skipped and the encoder receives linearised bytes → washed-out colours.
            // Force GL.sRGBWrite=true around the blit so the sRGB RTV is used and the
            // correct gamma-corrected bytes reach the encoder.
            _videoTrack = new VideoStreamTrack(_renderTexture, LinearSafeFlipCopy);
            _mediaStream = new MediaStream();
            _mediaStream.AddTrack(_videoTrack);
            _pc.AddTrack(_videoTrack, _mediaStream);

            // ── Audio track ────────────────────────────────────────────────────
            try
            {
                var listener = UnityEngine.Object.FindObjectOfType<AudioListener>();
                if (listener != null)
                {
                    _audioTrack = new AudioStreamTrack(listener);
                    _mediaStream.AddTrack(_audioTrack);
                    _pc.AddTrack(_audioTrack, _mediaStream);
                    UniPeekConstants.Log("[WebRTC] Audio track added.");
                }
                else
                    UniPeekConstants.LogWarning("[WebRTC] No AudioListener found — audio not streamed.");
            }
            catch (Exception ex)
            {
                UniPeekConstants.LogWarning($"[WebRTC] Audio track setup failed, continuing without audio: {ex.Message}");
            }

            // ── Data channel (input messages from Flutter) ────────────────────
            ApplyBitrateSettings();

            var dcInit = new RTCDataChannelInit { ordered = true };
            _dataChannel          = _pc.CreateDataChannel("input", dcInit);
            _dataChannel.OnMessage = bytes =>
                DataChannelMessage?.Invoke(Encoding.UTF8.GetString(bytes));

            // ── Start offer creation and drive WebRTC update loop ─────────────
            _fpsWindowStart = EditorApplication.timeSinceStartup;
            _fpsWindowCount = 0;
            _smoothedCaptureFps = 0f;

            _offerCoroutine = EditorCoroutineUtility.StartCoroutineOwnerless(CreateOfferCoroutine());
            _negotiationTimeoutCoroutine = EditorCoroutineUtility.StartCoroutineOwnerless(NegotiationTimeoutCoroutine());
            _webRtcUpdateCoroutine = EditorCoroutineUtility.StartCoroutineOwnerless(WebRtcUpdateWrapper());
            _cameraLoopCoroutine = EditorCoroutineUtility.StartCoroutineOwnerless(CameraLoop());
        }

        /// <summary>
        /// Applies the SDP answer received from the Flutter app.
        /// May be called from any thread.
        /// </summary>
        public void SetRemoteAnswer(string sdp)
        {
            if (string.IsNullOrWhiteSpace(sdp)) return;
            EnqueueMainThread(() =>
            {
                if (_pc == null || _disposed) return;
                EditorCoroutineUtility.StartCoroutineOwnerless(SetRemoteDescriptionCoroutine(sdp));
            });
        }

        /// <summary>
        /// Adds a remote ICE candidate received from the Flutter app via the WebSocket.
        /// May be called from any thread.
        /// </summary>
        public void AddIceCandidate(string candidate, string sdpMid, int sdpMLineIndex)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            EnqueueMainThread(() =>
            {
                if (_pc == null || _disposed) return;
                var c = new RTCIceCandidate(new RTCIceCandidateInit
                {
                    candidate     = candidate,
                    sdpMid        = sdpMid,
                    sdpMLineIndex = sdpMLineIndex,
                });
                // Flutter may send candidates before SetRemoteDescriptionCoroutine finishes
                // (it yields at least one editor tick). Buffer them and drain after the
            // answer is applied — same pattern as Flutter's _pendingCandidates list.
                if (!_remoteDescriptionSet)
                    _pendingCandidates.Add(c);
                else
                    _pc.AddIceCandidate(c);
            });
        }

        /// <summary>
        /// No-op in WebRTC 3.x — the engine is driven by the internal UpdateLoop coroutine.
        /// Kept for API compatibility with ConnectionManager.
        /// </summary>
        public void Tick() { }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            UnhookMainThreadQueue();

            _dataChannel?.Dispose();
            _dataChannel = null;

            foreach (var channel in _remoteDataChannels)
                channel?.Dispose();
            _remoteDataChannels.Clear();

            _audioTrack?.Dispose();
            _audioTrack = null;

            _videoTrack?.Dispose();
            _videoTrack = null;

            _mediaStream?.Dispose();
            _mediaStream = null;

            _pc?.Dispose();
            _pc = null;

            // Both coroutines (WebRtcUpdateWrapper, CameraLoop) check _disposed
            // (set to true above) and exit naturally on the next tick.
            // Do NOT use StopCoroutine — it sets m_Routine=null which causes a
            // NullReferenceException if MoveNext is still in the current frame's
            // EditorApplication.update snapshot.

            _pendingCandidates.Clear();
            while (_mainThreadActions.TryDequeue(out _)) { }
            _remoteDescriptionSet = false;
            _offerCoroutine = null;
            _negotiationTimeoutCoroutine = null;
            _webRtcUpdateCoroutine = null;
            _cameraLoopCoroutine = null;
            _iceDisconnectedGraceCoroutine = null;

            if (_captureHelper != null)
            {
                UnityEngine.Object.DestroyImmediate(_captureHelper.gameObject);
                _captureHelper = null;
            }

            if (_renderTexture != null)
            {
                _renderTexture.Release();
                UnityEngine.Object.DestroyImmediate(_renderTexture);
                _renderTexture = null;
            }

            UniPeekConstants.Log("[WebRTC] Streamer disposed.");
        }

        private void HookMainThreadQueue()
        {
            if (_mainThreadQueueHooked) return;
            EditorApplication.update += DrainMainThreadQueue;
            _mainThreadQueueHooked = true;
        }

        private void UnhookMainThreadQueue()
        {
            if (!_mainThreadQueueHooked) return;
            EditorApplication.update -= DrainMainThreadQueue;
            _mainThreadQueueHooked = false;
        }

        private void EnqueueMainThread(Action action)
        {
            if (_disposed) return;
            _mainThreadActions.Enqueue(action);
        }

        private void DrainMainThreadQueue()
        {
            while (!_disposed && _mainThreadActions.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { UniPeekConstants.LogError($"[WebRTC] Main-thread action failed: {ex}"); }
            }
        }

        private static double FpsCapToInterval(int fpsCap)
        {
            int clamped = Mathf.Clamp(fpsCap, MinFpsCap, MaxFpsCap);
            return 1.0 / clamped;
        }

        private static int ClampBitrate(int kbps)
            => Mathf.Clamp(kbps, MinBitrateKbps, MaxBitrateKbps);

        // ── Coroutines ────────────────────────────────────────────────────────

        // Drives WebRTC.Update() one step per editor tick. Using a wrapper instead
        // of running WebRTC.Update() directly as an EditorCoroutine lets us exit via
        // the _disposed flag without calling StopCoroutine (which causes a
        // NullReferenceException in EditorCoroutines when stopped mid-frame).
        private IEnumerator WebRtcUpdateWrapper()
        {
            var updater = WebRTC.Update();
            while (!_disposed)
            {
                updater.MoveNext();
                yield return null;
            }
        }

        // In Play Mode: schedules a full-screen capture (including Overlay canvases)
        // via CaptureHelper, then blits the result into _renderTexture each frame.
        // In Edit Mode: renders Camera.main directly into _renderTexture (same as the
        // WebSocket JPEG path) so the full URP pipeline — including the final sRGB
        // output blit — runs correctly. Clone-camera approaches skip that final pass
        // because CopyFrom does not copy UniversalAdditionalCameraData.
        private IEnumerator CameraLoop()
        {
            _lastCaptureTime = EditorApplication.timeSinceStartup - _captureInterval;

            while (!_disposed)
            {
                if (!Application.isPlaying)
                {
                    if (_captureHelper != null)
                    {
                        UnityEngine.Object.DestroyImmediate(_captureHelper.gameObject);
                        _captureHelper = null;
                    }
                    yield return null;
                    continue;
                }

                // Respect the FPS cap — skip this tick if the interval hasn't elapsed.
                double now = EditorApplication.timeSinceStartup;
                if (now - _lastCaptureTime < _captureInterval)
                {
                    yield return null;
                    continue;
                }
                _lastCaptureTime = now;

                if (_captureHelper == null)
                {
                    var go = new GameObject("[UniPeek] WebRTCCapture")
                        { hideFlags = HideFlags.HideAndDontSave };
                    _captureHelper         = go.AddComponent<CaptureHelper>();
                    _captureHelper.OnFrame = OnCaptureFrame;
                }
                _captureHelper.RequestCapture();
                yield return null;
            }
        }

        private void OnCaptureFrame(Texture2D tex)
        {
            if (_renderTexture != null)
            {
                if (QualitySettings.activeColorSpace == ColorSpace.Linear)
                {
                    // The capture texture is correctly flagged as sRGB (linear=false),
                    // so the GPU reads sRGB→linear on input.  Force GL.sRGBWrite=true so
                    // the write side also applies linear→sRGB, giving a correct
                    // sRGB→linear→sRGB round-trip into the render texture.
                    // (In editor coroutine context GL.sRGBWrite defaults to false, so we
                    // must set it explicitly.)
                    bool prev = GL.sRGBWrite;
                    GL.sRGBWrite = true;
                    Graphics.Blit(tex, _renderTexture);
                    GL.sRGBWrite = prev;
                }
                else
                {
                    Graphics.Blit(tex, _renderTexture);
                }
            }
            UpdateFpsStats();
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private void UpdateFpsStats()
        {
            _fpsWindowCount++;
            double elapsed = EditorApplication.timeSinceStartup - _fpsWindowStart;
            if (elapsed < 1.0) return;

            _smoothedCaptureFps = (float)(_fpsWindowCount / elapsed);
            _fpsWindowCount = 0;
            _fpsWindowStart = EditorApplication.timeSinceStartup;
        }

        // VideoStreamTrack's default VerticalFlipCopy runs outside a camera context
        // where GL.sRGBWrite is false.  Without it the UNORM RTV is used instead of
        // UNORM_SRGB, so the linear→sRGB conversion is skipped and the encoder gets
        // raw linear bytes.  Explicitly enable sRGB write for the duration of the blit.
        private static readonly Vector2 s_flipScale  = new Vector2(1f, -1f);
        private static readonly Vector2 s_flipOffset = new Vector2(0f,  1f);
        private static void LinearSafeFlipCopy(Texture source, RenderTexture dest)
        {
            if (QualitySettings.activeColorSpace == ColorSpace.Linear)
            {
                bool prev = GL.sRGBWrite;
                GL.sRGBWrite = true;
                Graphics.Blit(source, dest, s_flipScale, s_flipOffset);
                GL.sRGBWrite = prev;
            }
            else
            {
                Graphics.Blit(source, dest, s_flipScale, s_flipOffset);
            }
        }

        private IEnumerator NegotiationTimeoutCoroutine()
        {
            double startTime = EditorApplication.timeSinceStartup;
            while (!_disposed && !_remoteDescriptionSet &&
                   EditorApplication.timeSinceStartup - startTime < NegotiationTimeoutSeconds)
            {
                yield return null;
            }

            if (_disposed || _remoteDescriptionSet) yield break;

            NotifyDisconnected($"Negotiation timed out after {NegotiationTimeoutSeconds:0.#} seconds waiting for remote answer.");
        }

        private IEnumerator IceDisconnectedGraceCoroutine(int generation)
        {
            double startTime = EditorApplication.timeSinceStartup;
            while (!_disposed && generation == _iceDisconnectGeneration &&
                   EditorApplication.timeSinceStartup - startTime < IceDisconnectedGraceSeconds)
            {
                yield return null;
            }

            if (_disposed || generation != _iceDisconnectGeneration) yield break;

            NotifyDisconnected($"ICE remained disconnected for {IceDisconnectedGraceSeconds:0.#} seconds.");
        }

        private IEnumerator CreateOfferCoroutine()
        {
            var offerOp = _pc.CreateOffer();
            yield return offerOp;

            if (offerOp.IsError)
            {
                UniPeekConstants.LogError($"[WebRTC] CreateOffer error: {offerOp.Error.message}");
                yield break;
            }
            if (_disposed || _pc == null) yield break;

            var desc       = offerOp.Desc;
            var setLocalOp = _pc.SetLocalDescription(ref desc);
            yield return setLocalOp;

            if (setLocalOp.IsError)
            {
                UniPeekConstants.LogError($"[WebRTC] SetLocalDescription error: {setLocalOp.Error.message}");
                yield break;
            }
            if (_disposed || _pc == null) yield break;

            UniPeekConstants.Log("[WebRTC] Offer created, sending to Flutter.");
            OfferReady?.Invoke(offerOp.Desc.sdp);
        }

        private IEnumerator SetRemoteDescriptionCoroutine(string sdp)
        {
            var desc = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = sdp };
            var op   = _pc.SetRemoteDescription(ref desc);
            yield return op;
            if (_disposed || _pc == null) yield break;

            if (op.IsError)
            {
                UniPeekConstants.LogError($"[WebRTC] SetRemoteDescription error: {op.Error.message}");
            }
            else
            {
                UniPeekConstants.Log("[WebRTC] Remote answer accepted.");
                _remoteDescriptionSet = true;
                ApplyBitrateSettings();
                // Drain ICE candidates that arrived before the answer was processed.
                foreach (var c in _pendingCandidates)
                    _pc?.AddIceCandidate(c);
                _pendingCandidates.Clear();
            }
        }

        // ── Peer-connection callbacks ─────────────────────────────────────────

        private void OnIceCandidate(RTCIceCandidate candidate)
        {
            if (_disposed) return;
            if (candidate?.Candidate == null) return;
            IceCandidateReady?.Invoke(
                candidate.Candidate,
                candidate.SdpMid  ?? string.Empty,
                candidate.SdpMLineIndex ?? 0);
        }

        private void OnIceConnectionChange(RTCIceConnectionState state)
        {
            if (_disposed) return;
            UniPeekConstants.Log($"[WebRTC] ICE state → {state}");
            switch (state)
            {
                case RTCIceConnectionState.Connected:
                case RTCIceConnectionState.Completed:
                    _iceDisconnectGeneration++;
                    ApplyBitrateSettings();
                    Connected?.Invoke();
                    break;

                case RTCIceConnectionState.Disconnected:
                    _iceDisconnectedGraceCoroutine = EditorCoroutineUtility.StartCoroutineOwnerless(
                            IceDisconnectedGraceCoroutine(++_iceDisconnectGeneration));
                    break;

                case RTCIceConnectionState.Failed:
                case RTCIceConnectionState.Closed:
                    _iceDisconnectGeneration++;
                    NotifyDisconnected($"ICE state became {state}.");
                    break;
            }
        }

        private void NotifyDisconnected(string reason)
        {
            if (_disposed) return;
            UniPeekConstants.LogWarning($"[WebRTC] {reason} Falling back to JPEG.");
            Disconnected?.Invoke();
        }

        // Raise the video sender's bitrate cap so the stream quality is not
        // limited by WebRTC's conservative default (~600 kbps).
        // 10 Mbps max allows HD streaming over a local Wi-Fi link.
        private void ApplyBitrateSettings()
        {
            if (_pc == null) return;
            int updatedSenders = 0;
            int failedSenders = 0;
            foreach (var sender in _pc.GetSenders())
            {
                if (sender.Track?.Kind != TrackKind.Video) continue;

                var parameters = sender.GetParameters();
                if (parameters.encodings == null) continue;
                foreach (var enc in parameters.encodings)
                    enc.maxBitrate = (ulong)(_maxBitrateKbps * 1000);

                var error = sender.SetParameters(parameters);
                if (error.errorType == RTCErrorType.None)
                    updatedSenders++;
                else
                {
                    failedSenders++;
                    UniPeekConstants.LogWarning($"[WebRTC] Failed to set video bitrate: {error.message}");
                }
            }

            if (updatedSenders > 0)
                UniPeekConstants.Log($"[WebRTC] Bitrate cap set to {_maxBitrateKbps} kbps on {updatedSenders} sender(s).");
            else if (failedSenders == 0)
                UniPeekConstants.LogWarning("[WebRTC] Bitrate cap was not applied because no RTP sender encodings were available.");
        }

        private void OnConnectionStateChange(RTCPeerConnectionState state)
        {
            if (_disposed) return;
            UniPeekConstants.Log($"[WebRTC] PC state → {state}");
        }

        private void OnRemoteDataChannel(RTCDataChannel channel)
        {
            if (_disposed) return;
            // Flutter may open a data channel — accept and mirror messages.
            _remoteDataChannels.Add(channel);
            channel.OnMessage = bytes =>
            {
                if (_disposed) return;
                DataChannelMessage?.Invoke(Encoding.UTF8.GetString(bytes));
            };
        }
    }
}

#endif // UNITY_WEBRTC
