using Serilog;

namespace SipBot;

/// <summary>
/// Simplified TTS sender: Generates full audio response from streaming provider and sends to pacer for pacing/filtering.
/// Backward-compatible API: Retained StartStreamingAsync, Stop, events, and IsPlaying for seamless integration.
/// No internal queue or pacing—defers to RTP pacer for chunking, timing, and effects (e.g., volume).
/// Modern: Collects streaming output into full buffer for simplicity; linked CTS for cancellation; minimal state.
/// </summary>
public class TtsStreamer
{
    private CancellationTokenSource? _cancellationSource;
    private bool _isPlaying;
    private readonly object _lock = new();

    /// <summary>
    /// Event raised when full PCMU audio needs echo cancellation registration.
    /// Subscriber (e.g., endpoint) should handle registration for the complete response.
    /// </summary>
    public event EventHandler<byte[]>? OnEchoRegistrationRequired;

    /// <summary>
    /// Event raised when full PCMU audio is ready for sending (e.g., to RTP pacer).
    /// Subscriber should forward to pacer, which handles splitting/pacing/filtering.
    /// </summary>
    public event EventHandler<byte[]>? OnAudioChunkReady;

    /// <summary>
    /// Indicates if TTS is currently generating/sending.
    /// </summary>
    public bool IsPlaying
    {
        get
        {
            lock (_lock)
            {
                return _isPlaying;
            }
        }
    }

    public TtsStreamer()
    {
    }

    /// <summary>
    /// Sends full TTS audio from text to pacer, with interruption support (backward-compatible).
    /// Collects streaming PCMU chunks into complete buffer; raises events for registration/sending.
    /// Simpler: No internal pacing—let pacer chunk and pace.
    /// </summary>
    /// <param name="text">LLM response text to synthesize/stream.</param>
    /// <param name="voiceKey">Optional voice key.</param>
    /// <param name="ct">CancellationToken for interruption.</param>
    public async Task StartStreamingAsync(string text, string? voiceKey = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_isPlaying)
            {
                Log.Warning("TTS already generating, stopping previous via cancellation.");
                Stop();
            }

            _isPlaying = true;
            _cancellationSource?.Cancel();
            _cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        try
        {
            // Collect all streaming chunks into full buffer (efficient: List<byte[]> then concat)
            var chunks = new List<byte[]>();
            await foreach (var pcmuChunk in TtsProviderStreaming.TextToSpeechStreamAsync(text, voiceKey, _cancellationSource.Token).ConfigureAwait(false))
            {
                if (pcmuChunk.Length > 0 && !_cancellationSource.Token.IsCancellationRequested)
                {
                    chunks.Add(pcmuChunk);
                }
            }

            if (_cancellationSource.Token.IsCancellationRequested)
            {
                Log.Debug("TTS generation cancelled during streaming collection.");
                return;
            }

            if (chunks.Count == 0)
            {
                Log.Warning("No TTS audio chunks generated; skipping send.");
                return;
            }

            // Concatenate into full PCMU buffer (modern: Use Buffer.BlockCopy for zero-alloc where possible, but List for simplicity)
            int totalLength = chunks.Sum(c => c.Length);
            byte[] fullPcmu = new byte[totalLength];
            int offset = 0;
            foreach (var chunk in chunks)
            {
                Buffer.BlockCopy(chunk, 0, fullPcmu, offset, chunk.Length);
                offset += chunk.Length;
            }

            // Register full audio for echo cancellation (pacer/endpoint can split if needed)
            OnEchoRegistrationRequired?.Invoke(this, fullPcmu);

            // Send full to pacer (it splits into 20ms frames, paces, and applies filters like volume)
            OnAudioChunkReady?.Invoke(this, fullPcmu);

            Log.Information($"Collected and sent full TTS audio ({fullPcmu.Length} bytes) for '{text.Substring(0, Math.Min(50, text.Length))}...'");
        }
        catch (OperationCanceledException)
        {
            Log.Debug("TTS generation cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during TTS generation and send.");
        }
        finally
        {
            lock (_lock)
            {
                _isPlaying = false;
            }
        }
    }

    /// <summary>
    /// Stops (cancels) in-progress TTS generation.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (!_isPlaying)
                return;

            _cancellationSource?.Cancel();
            _isPlaying = false;
            Log.Information("TTS generation cancelled.");
        }
    }

    public void Dispose()
    {
        Stop();
        _cancellationSource?.Dispose();
        _cancellationSource = null;
        OnEchoRegistrationRequired = null;
        OnAudioChunkReady = null;
    }
}