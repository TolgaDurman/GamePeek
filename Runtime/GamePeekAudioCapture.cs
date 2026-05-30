using System;
using UnityEngine;

namespace GamePeek
{
    /// <summary>
    /// Runtime audio tap used by the editor WebRTC streamer.
    /// </summary>
    public sealed class GamePeekAudioCapture : MonoBehaviour
    {
        public event Action<float[], int, int> AudioRead;

        private int _sampleRate;

        private void OnEnable()
        {
            RefreshSampleRate(false);
            AudioSettings.OnAudioConfigurationChanged += RefreshSampleRate;
        }

        private void OnDisable()
        {
            AudioSettings.OnAudioConfigurationChanged -= RefreshSampleRate;
        }

        private void RefreshSampleRate(bool deviceWasChanged)
        {
            _sampleRate = AudioSettings.outputSampleRate;
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            AudioRead?.Invoke(data, channels, _sampleRate);
        }
    }
}
