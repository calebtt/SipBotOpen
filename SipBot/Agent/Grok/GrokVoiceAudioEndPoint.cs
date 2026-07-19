using Serilog;

namespace SipBot;

/// <summary>
/// SIP audio endpoint that bridges RTP ↔ Grok Voice Realtime (no local STT/TTS).
/// Inbound: decoded PCM → resample 24 kHz → Grok input_audio_buffer.
/// Outbound: Grok pcm16 24 kHz → SendAudioFrame (BaseAudioEndPoint resamples/encodes PCMU).
/// </summary>
public sealed class GrokVoiceAudioEndPoint : BaseAudioEndPoint, IDisposable
{
    private readonly HomeLineToolFunctions _tools;
    private readonly string _apiKey;
    private GrokRealtimeSession? _session;
    private readonly object _bufLock = new();
    private readonly List<byte> _uploadBuf = new(48_000);
    private DateTime _lastFlushUtc = DateTime.UtcNow;
    private int _lastDtmfLenNotified;
    private bool _disposed;

    // Flush ~100ms of 24kHz mono pcm16 to Grok at a time (4800 samples ≈ 9600 bytes)
    private const int FlushBytes = 9600;
    private const int FlushMs = 100;

    public GrokVoiceAudioEndPoint(HomeLineToolFunctions tools, string apiKey)
        : base(enableContinuousKeepAlive: true, enableWidebandAudio: false)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
    }

    public override async Task InitializeAsync()
    {
        _session = new GrokRealtimeSession(
            apiKey: _apiKey,
            tools: _tools,
            playPcm: PlayGrokPcmAsync);

        await _session.ConnectAsync().ConfigureAwait(false);
        // Defer welcome until after SIP Answer so RTP is up when audio deltas arrive.
        Log.Information("GrokVoiceAudioEndPoint WS ready (welcome deferred until media up)");
    }

    /// <summary>Call after SIP Answer so the welcome is heard on a live media path.</summary>
    public Task SpeakWelcomeWhenReadyAsync()
    {
        if (_session == null)
            return Task.CompletedTask;
        return _session.SpeakWelcomeAsync();
    }

    public override async Task ShutdownAsync()
    {
        try
        {
            if (_session != null)
                await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Grok session shutdown");
        }

        try
        {
            _processAudioCancellationSource.Cancel();
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Forward RFC2833 digits into tool buffer + notify Grok when PIN length reached.</summary>
    public void OnDtmfDigit(char digit)
    {
        _tools.AppendDtmfDigit(digit);
        _ = NotifyDtmfIfReadyAsync();
    }

    private async Task NotifyDtmfIfReadyAsync()
    {
        try
        {
            var json = await _tools.GetDtmfDigitsAsync().ConfigureAwait(false);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var len = doc.RootElement.TryGetProperty("length", out var l) ? l.GetInt32() : 0;
            var digits = doc.RootElement.TryGetProperty("digits", out var d) ? d.GetString() ?? "" : "";
            if (len >= 4 && len != _lastDtmfLenNotified && _session != null)
            {
                _lastDtmfLenNotified = len;
                await _session.NotifyDtmfAsync(digits).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "DTMF notify skipped");
        }
    }

    protected override async Task ProcessAudioAsync(byte[] pcm, int sampleRateHz)
    {
        if (_session == null || pcm.Length == 0)
            return;

        try
        {
            // Resample to Grok 24 kHz, then batch ~100ms before WS append
            byte[] pcm24 = sampleRateHz == GrokRealtimeSession.GrokSampleRateHz
                ? pcm
                : AudioAlgos.ResamplePcmWithNAudio(pcm, sampleRateHz, GrokRealtimeSession.GrokSampleRateHz);

            bool shouldFlush = false;
            lock (_bufLock)
            {
                _uploadBuf.AddRange(pcm24);
                var elapsed = (DateTime.UtcNow - _lastFlushUtc).TotalMilliseconds;
                if (_uploadBuf.Count >= FlushBytes || elapsed >= FlushMs)
                    shouldFlush = true;
            }

            if (shouldFlush)
                await FlushUploadAsync().ConfigureAwait(false);

            if (_session.EndRequested)
            {
                Log.Information("Grok requested end_conversation — waiting for hangup tool delay");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Grok inbound audio path failed");
        }
    }

    private async Task FlushUploadAsync()
    {
        byte[] chunk;
        lock (_bufLock)
        {
            if (_uploadBuf.Count == 0)
                return;
            chunk = _uploadBuf.ToArray();
            _uploadBuf.Clear();
            _lastFlushUtc = DateTime.UtcNow;
        }

        if (_session != null)
            await _session.AppendInputPcmAsync(chunk, GrokRealtimeSession.GrokSampleRateHz).ConfigureAwait(false);
    }

    private Task PlayGrokPcmAsync(byte[] pcm24k, int sampleRateHz)
    {
        if (!_isMediaSessionReady || pcm24k.Length == 0)
            return Task.CompletedTask;

        try
        {
            // BaseAudioEndPoint encodes + paces when keep-alive mode is on
            SendAudioFrame(pcm24k, sampleRateHz);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to play Grok audio frame");
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            ShutdownAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "GrokVoiceAudioEndPoint dispose");
        }
    }
}
