using Serilog;

namespace SipBot;

/// <summary>
/// Enhanced audio endpoint with streaming STT for real-time interruption detection.
/// Composes VoiceAgentCore for decoupled AI/voice logic.
/// </summary>
public class StreamingVoiceAudioEndPoint : BaseAudioEndPoint, IDisposable
{
    private readonly VoiceAgentCore _voiceAgentCore;
    private readonly RtpAudioPacer _audioPacer = new();

    private bool _isInitialized = false;
    private bool disposedValue;

    /// <summary>
    /// Buffer attaches to this, used for audio FROM TTS.
    /// </summary>
    public event Action<byte[]>? OnAudioReplyReady;

    public StreamingVoiceAudioEndPoint(
        SttProviderStreaming streamingSttClient,
        LlmChat llmClient,
        TtsStreamer ttsStreamer)
    {
        // Compose core with pacer
        _voiceAgentCore = new VoiceAgentCore(streamingSttClient, llmClient, ttsStreamer, _audioPacer);

        // Forward events from core
        _voiceAgentCore.OnAudioReplyReady += chunk => OnAudioReplyReady?.Invoke(chunk);

        Log.Information("StreamingVoiceAudioEndPoint created with VoiceAgentCore2.");
    }

    public override async Task InitializeAsync()
    {
        if (_isInitialized)
            return;

        // Delegate to core
        await _voiceAgentCore.InitializeAsync();

        // Attach RTP audio pacer for sending TTS audio to SIP call
        _audioPacer.Attach(this);

        Log.Information("Streaming voice endpoint initialized with decoupled core logic.");

        _isInitialized = true;
    }

    public override async Task ShutdownAsync()
    {
        try
        {
            // Delegate to core first
            await _voiceAgentCore.ShutdownAsync();

            // Detach RTP audio pacer
            _audioPacer.Detach(this).Wait();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during streaming voice endpoint shutdown");
        }
    }

    /// <summary>
    /// Sends the audio response from TTS to the cellphone user.
    /// </summary>
    /// <param name="audio">8khz 16bit mono PCMU</param>
    public void SendAudio(byte[] audio)
    {
        if (!_isMediaSessionReady)
        {
            Log.Warning("Media session not ready, cannot send audio.");
            return;
        }
        OnAudioReplyReady?.Invoke(audio); // Forwards from core
    }

    /// <summary>
    /// Audio received for internal processing (from cellphone).
    /// Audio is 8khz 16bit mono PCM (decoded from SIP).
    /// Enhanced with echo cancellation; delegates VAD/STT to core.
    /// </summary>
    protected override async Task ProcessAudioAsync(byte[] pcm8Khz)
    {
        try
        {
            // Resample to 16kHz for processing
            byte[] pcm16Khz = AudioAlgos.ResamplePcmWithNAudio(pcm8Khz, AudioConstants.SAMPLE_RATE_8KHZ, AudioConstants.SAMPLE_RATE_16KHZ);

            // Delegate to core for VAD (before WebRTC for faster onset)
            _voiceAgentCore.ProcessIncomingAudioChunk(pcm16Khz ?? Array.Empty<byte>());

            // TODO: Apply WebRTC filter if needed (e.g., for echo cancellation on audio)
            // _webRtcFilter.ProcessFrameRecorded(pcm16Khz);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in audio processing pipeline");
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                ShutdownAsync().GetAwaiter().GetResult(); // Sync in Dispose (safe)
                _voiceAgentCore.Dispose();
            }

            disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}