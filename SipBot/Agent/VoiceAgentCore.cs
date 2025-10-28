using Serilog;
using WebRtcVadSharp;

namespace SipBot;

public class VoiceAgentCore
{
    private const float VolumeLoweringFactor = 0.35f;

    private readonly SttProviderStreaming _streamingSttClient;
    private readonly LlmChat _llmChat;
    private readonly TtsStreamer _ttsStreamer;
    private readonly RtpAudioPacer _audioPacer;

    private CancellationTokenSource? _cancellationTokenSource = null;
    private VadSpeechSegmenter? _vad = null;

    // Streaming STT and interruption handling
    private readonly object _processingLock = new();
    private bool _isProcessingTranscription = false;

    private bool disposedValue;

    public event Action<byte[]>? OnAudioReplyReady;

    public VoiceAgentCore(
        SttProviderStreaming streamingSttClient,
        LlmChat llmChat,
        TtsStreamer ttsStreamer,
        RtpAudioPacer audioPacer)
    {
        _streamingSttClient = streamingSttClient ?? throw new ArgumentNullException(nameof(streamingSttClient));
        _llmChat = llmChat ?? throw new ArgumentNullException(nameof(llmChat));
        _ttsStreamer = ttsStreamer ?? throw new ArgumentNullException(nameof(ttsStreamer));
        _audioPacer = audioPacer ?? throw new ArgumentNullException(nameof(audioPacer));

        _ttsStreamer.OnEchoRegistrationRequired += RegisterTtsAudioForEchoCancellation;
        _ttsStreamer.OnAudioChunkReady += (sender, chunk) => OnAudioReplyReady?.Invoke(chunk!);

        // Subscribe to streaming STT events
        _streamingSttClient.TranscriptionComplete += OnTranscriptionComplete;

        Log.Information("VoiceAgentCore created.");
    }

    public bool IsProcessingTranscription => _isProcessingTranscription;

    public Task InitializeAsync()
    {
        try
        {
            _cancellationTokenSource = new CancellationTokenSource();

            // Initialize VAD for speech segmentation
            _vad = new();

            bool volumeFilterActive = false; // Local; use volatile if multi-threaded contention

            // VAD sentence begin: lower TTS volume during user speech
            _vad.SentenceBegin += (sender, e) =>
            {
                Log.Information("VAD: Speech segment started.");

                if (_audioPacer.IsAudioPlaying && !volumeFilterActive)
                {
                    Log.Information("VAD: Applying volume filter during potential interrupt activation.");

                    _audioPacer.ApplyFilter(chunk => AudioAlgos.AdjustPcmuVolume(chunk, VolumeLoweringFactor));
                    volumeFilterActive = true;
                }
            };

            _vad.SentenceCompleted += (sender, pcmStream) =>
            {
                if (_cancellationTokenSource?.IsCancellationRequested == true)
                    return;

                Log.Information($"VAD: Speech segment completed, {pcmStream.Length} bytes");

                if (volumeFilterActive)
                {
                    _audioPacer.ClearFilter();
                    volumeFilterActive = false;
                    Log.Information("VAD: Volume filter cleared after speech segment.");
                }

                // Process through streaming STT for real-time detection
                _streamingSttClient.ProcessAudioChunkAsync(pcmStream).Wait();
            };

            Log.Information("VoiceAgentCore initialized.");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize VoiceAgentCore");
            throw;
        }
    }

    public Task ShutdownAsync()
    {
        try
        {
            _cancellationTokenSource?.Cancel();

            _vad?.Dispose();
            _vad = null;

            _ttsStreamer.Stop();
            _ttsStreamer.Dispose();
            _audioPacer.ResetBuffer();
            Log.Information("VoiceAgentCore shutdown complete.");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during VoiceAgentCore shutdown");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Process incoming audio chunk (16kHz PCM) for VAD detection.
    /// Assumed to be RTP sized chunks of audio FROM CELLPHONE USER.
    /// Audio is pushed into VAD for segmentation.
    /// </summary>
    public void ProcessIncomingAudioChunk(byte[] pcm16Khz)
    {
        if (_vad == null || _cancellationTokenSource?.IsCancellationRequested == true)
            return;

        _vad.PushFrame(pcm16Khz, SampleRate.Is16kHz, FrameLength.Is20ms);
    }

    /// <summary>
    /// Interrupt current playback (e.g., from external trigger).
    /// </summary>
    public void InterruptPlayback()
    {
        if (_audioPacer.IsAudioPlaying)
        {
            Log.Information("VoiceAgentCore: Interrupting current TTS playback.");
            _audioPacer.ResetBuffer();
        }
    }

    /// <summary>
    /// Called when complete transcription is ready.
    /// </summary>
    private void OnTranscriptionComplete(object? sender, string transcription)
    {
        Log.Information($"VoiceAgentCore: STT Complete transcription: '{transcription}'");

        bool shouldProcess;
        lock (_processingLock)
        {
            if (_isProcessingTranscription)
            {
                return;
            }
            _isProcessingTranscription = true;
            shouldProcess = true;
        }

        if (!shouldProcess)
            return;

        try
        {
            ProcessCompleteTranscriptionAsync(transcription).Wait();
        }
        finally
        {
            lock (_processingLock)
            {
                _isProcessingTranscription = false;
            }
        }
    }

    /// <summary>
    /// Requests LLM chat completion and starts TTS streaming for the given transcription.
    /// </summary>
    private async Task ProcessCompleteTranscriptionAsync(string transcription)
    {
        try
        {
            Log.Information($"VoiceAgentCore: Processing complete transcription: {transcription}");

            // Updated: Use LlmChat.ProcessMessageAsync (no maxTokens; CT propagated)
            string response = await _llmChat.ProcessMessageAsync(transcription, _cancellationTokenSource?.Token ?? CancellationToken.None);
            Log.Information($"VoiceAgentCore: LLM Response: {response}");

            if (_cancellationTokenSource?.IsCancellationRequested == true)
                return;

            // Interrupt if playing
            InterruptPlayback();

            // Start streaming TTS (voiceKey null by default)
            await _ttsStreamer.StartStreamingAsync(response, ct: _cancellationTokenSource?.Token ?? CancellationToken.None);
        }
        catch (TaskCanceledException tcex)
        {
            Log.Debug($"VoiceAgentCore: Task canceled, info: {tcex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "VoiceAgentCore: Error processing complete transcription.");
        }
    }

    /// <summary>
    /// Registers TTS audio for echo cancellation processing (matches EventHandler<byte[]>).
    /// </summary>
    private void RegisterTtsAudioForEchoCancellation(object? sender, byte[] pcmuAudio)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(pcmuAudio); // Modern null safety

            // Convert PCMU to 16kHz PCM for echo cancellation
            byte[] pcm16Khz = AudioAlgos.ResamplePcmWithNAudio(
                AudioAlgos.ConvertPcmuToPcm16kHz(pcmuAudio),
                AudioConstants.SAMPLE_RATE_16KHZ,
                AudioConstants.SAMPLE_RATE_16KHZ
            );

            // In full system, split into frames and register with WebRTC filter
            // For core, we assume the endpoint handles registration post-event
            Log.Debug($"VoiceAgentCore: Echo registration prepared for {pcm16Khz.Length} bytes.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error preparing TTS audio for echo cancellation");
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                ShutdownAsync().GetAwaiter().GetResult(); // Sync in Dispose (safe)

                // Unsubscribe from events
                _streamingSttClient.TranscriptionComplete -= OnTranscriptionComplete;
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