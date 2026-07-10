using MinimalSileroVAD.Core;
using Serilog;

namespace SipBot;

public class VoiceAgentCore
{
    private const float VolumeLoweringFactor = 0.35f;

    private readonly SttProviderStreaming _streamingSttClient;
    private readonly LlmChat _llmChat;
    private readonly TtsStreamer _ttsStreamer;
    private readonly RtpAudioPacer _audioPacer;
    private readonly Action? _onUserEngaged;

    private CancellationTokenSource? _cancellationTokenSource = null;
    private IVadSpeechSegmenter? _vad = null;

    // Streaming STT and interruption handling
    private readonly object _processingLock = new();
    private bool _isProcessingTranscription = false;
    /// <summary>Latest user utterance while LLM/TTS is busy — keeps the newest, drops older.</summary>
    private string? _pendingTranscription;
    private bool _volumeFilterActive;

    private bool disposedValue;

    public event Action<byte[]>? OnAudioReplyReady;

    public VoiceAgentCore(
        SttProviderStreaming streamingSttClient,
        LlmChat llmChat,
        TtsStreamer ttsStreamer,
        RtpAudioPacer audioPacer,
        Action? onUserEngaged = null)
    {
        _streamingSttClient = streamingSttClient ?? throw new ArgumentNullException(nameof(streamingSttClient));
        _llmChat = llmChat ?? throw new ArgumentNullException(nameof(llmChat));
        _ttsStreamer = ttsStreamer ?? throw new ArgumentNullException(nameof(ttsStreamer));
        _audioPacer = audioPacer ?? throw new ArgumentNullException(nameof(audioPacer));
        _onUserEngaged = onUserEngaged;

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

            // Initialize VAD for speech segmentation. MsPerFrame MUST match what
            // ProcessIncomingAudioChunk actually pushes per call (20ms, matching RTP) -- it's not
            // just documentation, VadSpeechSegmenter computes its begin/end-of-utterance frame-
            // count thresholds from it once at construction (e.g. BeginOfUtteranceMs / MsPerFrame),
            // so a mismatch against the real per-call duration silently skews utterance timing.
            // The library's own default (32ms, the Silero model's native inference window) is
            // unrelated to and independent of this -- PushFrame's internal windowing handles that
            // regardless of the caller's chunk size.
            //
            // Telephony defaults: library BeginOfUtteranceMs=500 requires ~half a second of
            // *consecutive* speech frames before an utterance even starts. Single-word answers
            // ("yes"/"no"/"ok"/"what") are often 150-350ms of energy and never fire SpeechStarted.
            // Lower begin threshold; keep pre-speech padding so the word onset is still in the
            // segment; slightly snappier end silence for turn-taking.
            _vad = new VadSpeechSegmenter(new VadOptions
            {
                MsPerFrame = 20,
                BeginOfUtteranceMs = 160, // ~8 × 20ms frames
                EndOfUtteranceMs = 400,
                PreSpeechMs = 800,
                Threshold = 0.25f,
            });

            // VAD speech started: lower TTS volume during user speech
            _vad.SpeechStarted += (sender, e) =>
            {
                Log.Information("VAD: Speech segment started.");
                // Caller still talking — do not complete a delayed hangup from end_conversation
                _onUserEngaged?.Invoke();

                if (_audioPacer.IsAudioPlaying && !_volumeFilterActive)
                {
                    Log.Information("VAD: Applying volume filter during potential interrupt activation.");

                    _audioPacer.ApplyFilter(chunk => AudioAlgos.AdjustPcmuVolume(chunk, VolumeLoweringFactor));
                    _volumeFilterActive = true;
                }
            };

            _vad.SpeechCompleted += (sender, segment) =>
            {
                if (_cancellationTokenSource?.IsCancellationRequested == true)
                    return;

                Log.Information("VAD: Speech segment completed, {Bytes} bytes, {Duration:F2}s, peak probability {Probability:F2}",
                    segment.Pcm.Length, segment.Duration.TotalSeconds, segment.Probability);

                if (_volumeFilterActive)
                {
                    _audioPacer.ClearFilter();
                    _volumeFilterActive = false;
                    Log.Information("VAD: Volume filter cleared after speech segment.");
                }

                // Process through streaming STT without blocking the VAD callback thread
                _ = ProcessSegmentSttAsync(segment);
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

            // Stop generation only — TtsStreamer is owned by StreamingVoiceSipBotClient
            // and shared across calls. Disposing it here broke every call after the first.
            _ttsStreamer.Stop();
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

        _vad.PushFrame(pcm16Khz, frameLengthMs: 20);
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

    private async Task ProcessSegmentSttAsync(SpeechSegment segment)
    {
        try
        {
            await _streamingSttClient.ProcessAudioChunkAsync(segment.AsStream()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "VoiceAgentCore: STT processing failed for speech segment");
        }
    }

    /// <summary>
    /// Called when complete transcription is ready.
    /// If LLM/TTS is already busy, keep the *latest* utterance (barge-in / "I said no") instead of dropping.
    /// </summary>
    private void OnTranscriptionComplete(object? sender, string transcription)
    {
        if (string.IsNullOrWhiteSpace(transcription))
            return;

        Log.Information("VoiceAgentCore: STT Complete transcription: '{Transcription}'", transcription);
        _onUserEngaged?.Invoke();

        lock (_processingLock)
        {
            if (_isProcessingTranscription)
            {
                _pendingTranscription = transcription.Trim();
                Log.Information("VoiceAgentCore: Queued latest user utterance while previous turn is in flight.");
                return;
            }
            _isProcessingTranscription = true;
        }

        _ = ProcessTranscriptionGuardedAsync(transcription.Trim());
    }

    private async Task ProcessTranscriptionGuardedAsync(string transcription)
    {
        string? current = transcription;
        try
        {
            while (current != null)
            {
                await ProcessCompleteTranscriptionAsync(current).ConfigureAwait(false);

                lock (_processingLock)
                {
                    current = _pendingTranscription;
                    _pendingTranscription = null;
                    if (current == null)
                        _isProcessingTranscription = false;
                }

                if (current != null)
                    Log.Information("VoiceAgentCore: Processing queued utterance: '{Transcription}'", current);
            }
        }
        catch
        {
            lock (_processingLock)
            {
                _isProcessingTranscription = false;
                _pendingTranscription = null;
            }
            throw;
        }
    }

    /// <summary>
    /// Requests LLM chat completion and starts TTS streaming for the given transcription.
    /// </summary>
    private async Task ProcessCompleteTranscriptionAsync(string transcription)
    {
        try
        {
            Log.Information("VoiceAgentCore: Processing complete transcription: {Transcription}", transcription);

            string response = await _llmChat.ProcessMessageAsync(transcription, _cancellationTokenSource?.Token ?? CancellationToken.None);
            Log.Information("VoiceAgentCore: LLM Response: {Response}", response);

            if (_cancellationTokenSource?.IsCancellationRequested == true)
                return;

            if (string.IsNullOrWhiteSpace(response))
            {
                Log.Warning("VoiceAgentCore: Empty LLM response; skipping TTS.");
                return;
            }

            // Interrupt any prior playback before speaking the new reply
            InterruptPlayback();
            _ttsStreamer.Stop();

            await _ttsStreamer.StartStreamingAsync(response, ct: _cancellationTokenSource?.Token ?? CancellationToken.None);
        }
        catch (TaskCanceledException tcex)
        {
            Log.Debug("VoiceAgentCore: Task canceled, info: {Message}", tcex.Message);
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