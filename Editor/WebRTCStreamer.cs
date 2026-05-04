// Entire file is compiled only when com.unity.webrtc is present in the project.
#if UNITY_WEBRTC

using System;
using System.Collections;
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
    /// <b>Video:</b> uses <c>Camera.CaptureStreamTrack</c> — requires play mode.
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
        private double _captureInterval; // seconds between frames = 1 / fpsCap
        private double _lastCaptureTime;
        private int _maxBitrateKbps;

        // ── WebRTC objects ────────────────────────────────────────────────────
        private RTCPeerConnection _pc;
        private VideoStreamTrack  _videoTrack;
        private AudioStreamTrack  _audioTrack;
        private MediaStream       _mediaStream;
        private RTCDataChannel    _dataChannel;

        private bool _disposed;

        // ICE candidate buffer — candidates may arrive before the SDP answer is
        // applied (SetRemoteDescription yields 1+ editor ticks).  Mirror what the
        // Flutter side does: queue them and drain once the remote description is set.
        private bool _remoteDescriptionSet;
        private readonly List<RTCIceCandidate> _pendingCandidates = new();

        // Render texture fed directly to the VideoStreamTrack
        private RenderTexture _renderTexture;

        // Play Mode overlay capture (same CaptureHelper as JPEG pipeline)
        private CaptureHelper _captureHelper;

        // ── Constructor ───────────────────────────────────────────────────────

        /// <param name="width">Video width (pixels). Defaults to 1280.</param>
        /// <param name="height">Video height (pixels). Defaults to 720.</param>
        /// <param name="fpsCap">Maximum capture rate (frames/second). Defaults to 30.</param>
        /// <param name="maxBitrateKbps">Maximum video bitrate in kbps. Defaults to 10 000 (10 Mbps).</param>
        public WebRTCStreamer(int width = 1280, int height = 720, int fpsCap = 30,
                              int maxBitrateKbps = UniPeekConstants.DefaultWebRtcMaxBitrateKbps)
        {
            _width           = width;
            _height          = height;
            _captureInterval = fpsCap > 0 ? 1.0 / fpsCap : 1.0 / 30.0;
            _maxBitrateKbps  = maxBitrateKbps;
        }

        /// <summary>Updates the FPS cap. Takes effect on the next capture.</summary>
        public void SetFpsCap(int fpsCap)
            => _captureInterval = fpsCap > 0 ? 1.0 / fpsCap : 1.0 / 30.0;

        /// <summary>Updates the maximum video bitrate and re-applies it to any active senders.</summary>
        public void SetMaxBitrate(int kbps)
        {
            _maxBitrateKbps = kbps;
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
            var config = new RTCConfiguration
            {
                iceServers = Array.Empty<RTCIceServer>(),
            };

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
            var dcInit = new RTCDataChannelInit { ordered = true };
            _dataChannel          = _pc.CreateDataChannel("input", dcInit);
            _dataChannel.OnMessage = bytes =>
                DataChannelMessage?.Invoke(Encoding.UTF8.GetString(bytes));

            // ── Start offer creation and drive WebRTC update loop ─────────────
            EditorCoroutineUtility.StartCoroutineOwnerless(CreateOfferCoroutine());
            EditorCoroutineUtility.StartCoroutineOwnerless(WebRtcUpdateWrapper());
            EditorCoroutineUtility.StartCoroutineOwnerless(CameraLoop());
        }

        /// <summary>
        /// Applies the SDP answer received from the Flutter app.
        /// May be called from any thread.
        /// </summary>
        public void SetRemoteAnswer(string sdp)
        {
            if (_pc == null || _disposed) return;
            EditorCoroutineUtility.StartCoroutineOwnerless(SetRemoteDescriptionCoroutine(sdp));
        }

        /// <summary>
        /// Adds a remote ICE candidate received from the Flutter app via the WebSocket.
        /// May be called from any thread.
        /// </summary>
        public void AddIceCandidate(string candidate, string sdpMid, int sdpMLineIndex)
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

            _dataChannel?.Dispose();
            _dataChannel = null;

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
            _remoteDescriptionSet = false;

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
            UnityEngine.Object.DestroyImmediate(tex);
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

        private IEnumerator CreateOfferCoroutine()
        {
            var offerOp = _pc.CreateOffer();
            yield return offerOp;

            if (offerOp.IsError)
            {
                UniPeekConstants.LogError($"[WebRTC] CreateOffer error: {offerOp.Error.message}");
                yield break;
            }

            var desc       = offerOp.Desc;
            var setLocalOp = _pc.SetLocalDescription(ref desc);
            yield return setLocalOp;

            if (setLocalOp.IsError)
            {
                UniPeekConstants.LogError($"[WebRTC] SetLocalDescription error: {setLocalOp.Error.message}");
                yield break;
            }

            UniPeekConstants.Log("[WebRTC] Offer created, sending to Flutter.");
            OfferReady?.Invoke(offerOp.Desc.sdp);
        }

        private IEnumerator SetRemoteDescriptionCoroutine(string sdp)
        {
            var desc = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = sdp };
            var op   = _pc.SetRemoteDescription(ref desc);
            yield return op;

            if (op.IsError)
            {
                UniPeekConstants.LogError($"[WebRTC] SetRemoteDescription error: {op.Error.message}");
            }
            else
            {
                UniPeekConstants.Log("[WebRTC] Remote answer accepted.");
                _remoteDescriptionSet = true;
                // Drain ICE candidates that arrived before the answer was processed.
                foreach (var c in _pendingCandidates)
                    _pc?.AddIceCandidate(c);
                _pendingCandidates.Clear();
            }
        }

        // ── Peer-connection callbacks ─────────────────────────────────────────

        private void OnIceCandidate(RTCIceCandidate candidate)
        {
            if (candidate?.Candidate == null) return;
            IceCandidateReady?.Invoke(
                candidate.Candidate,
                candidate.SdpMid  ?? string.Empty,
                candidate.SdpMLineIndex ?? 0);
        }

        private void OnIceConnectionChange(RTCIceConnectionState state)
        {
            UniPeekConstants.Log($"[WebRTC] ICE state → {state}");
            switch (state)
            {
                case RTCIceConnectionState.Connected:
                case RTCIceConnectionState.Completed:
                    ApplyBitrateSettings();
                    Connected?.Invoke();
                    break;

                case RTCIceConnectionState.Disconnected:
                case RTCIceConnectionState.Failed:
                case RTCIceConnectionState.Closed:
                    Disconnected?.Invoke();
                    break;
            }
        }

        // Raise the video sender's bitrate cap so the stream quality is not
        // limited by WebRTC's conservative default (~600 kbps).
        // 10 Mbps max allows HD streaming over a local Wi-Fi link.
        private void ApplyBitrateSettings()
        {
            if (_pc == null) return;
            foreach (var sender in _pc.GetSenders())
            {
                var parameters = sender.GetParameters();
                if (parameters.encodings == null) continue;
                foreach (var enc in parameters.encodings)
                    enc.maxBitrate = (ulong)(_maxBitrateKbps * 1000);
                sender.SetParameters(parameters);
            }
            UniPeekConstants.Log($"[WebRTC] Bitrate cap set to {_maxBitrateKbps} kbps.");
        }

        private void OnConnectionStateChange(RTCPeerConnectionState state)
            => UniPeekConstants.Log($"[WebRTC] PC state → {state}");

        private void OnRemoteDataChannel(RTCDataChannel channel)
        {
            // Flutter may open a data channel — accept and mirror messages.
            channel.OnMessage = bytes =>
                DataChannelMessage?.Invoke(Encoding.UTF8.GetString(bytes));
        }
    }
}

#endif // UNITY_WEBRTC
