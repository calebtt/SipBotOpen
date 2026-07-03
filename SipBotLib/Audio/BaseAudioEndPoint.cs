using NAudio.Codecs;
using Serilog;
using SIPSorceryMedia.Abstractions;
using System.Net;
using System.Threading.Channels;
namespace SipBot;

public abstract class BaseAudioEndPoint : IAudioSource, IAudioSink
{
    protected List<AudioFormat> _supportedFormats;
    protected bool _isStarted = false;
    protected bool _isMediaSessionReady = false;
    protected CancellationTokenSource _processAudioCancellationSource = new();

    // GotAudioRtp is invoked synchronously by the SIP/RTP receive path. Frames are handed off
    // to this channel and drained by a single background worker, so ProcessAudioAsync() (which
    // may do real work like STT/echo-cancellation) never blocks the RTP thread. Bounded +
    // DropOldest keeps memory bounded and favors real-time freshness over completeness if the
    // consumer falls behind, rather than growing an unbounded backlog.
    private Channel<byte[]>? _rtpAudioChannel;
    private Task? _rtpProcessingTask;

    // Opt-in continuous outbound RTP: many real PBX/trunk providers sit behind their own NAT and
    // won't relay any RTP back to us until they've seen a packet from us on that same flow (plus
    // some NATs/firewalls on our own side won't admit a brand-new inbound UDP flow at all without
    // one first going out). A purely reactive audio source that only sends once it has something
    // to say can deadlock: nothing goes out -> nothing ever gets let back in -> nothing ever
    // arrives to react to. Confirmed live: without this, calls answer fine, zero RTP is ever
    // received, and SIPSorcery's own 30s RTP-timeout silently hangs up the call. Enabling this
    // starts a background sender the moment the call answers, streaming silence (or real audio,
    // via SendAudioFrame) every 20ms so the pinhole opens/stays open immediately regardless of
    // whether the subclass has anything to say yet.
    private readonly bool _enableContinuousKeepAlive;
    private readonly RtpPacedSender _keepAlivePacedSender = new();
    private volatile bool _keepAliveSenderStarted;

    public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
    public event Action<EncodedAudioFrame>? OnAudioSourceEncodedFrameReady;
    public event RawAudioSampleDelegate? OnAudioSourceRawSample;
    public event SourceErrorDelegate? OnAudioSourceError;
    public event SourceErrorDelegate? OnAudioSinkError;
    public event EncodedSampleDelegate? OnAudioSinkSample;

    /// <param name="enableContinuousKeepAlive">
    /// Start a continuous 20ms-paced outbound RTP stream (silence by default) the moment a call
    /// answers, to keep a NAT/firewall pinhole open for inbound media. Defaults to false to avoid
    /// changing behavior for existing subclasses; recommended true for anything calling out to a
    /// real PBX/trunk from behind NAT. When enabled, use SendAudioFrame() instead of
    /// ExternalAudioSourceEncodedSample() so outbound audio is paced onto the same cadence instead
    /// of bursting.
    /// </param>
    public BaseAudioEndPoint(bool enableContinuousKeepAlive = false)
    {
        _supportedFormats = new List<AudioFormat>
        {
            new AudioFormat(0, "PCMU", 8000, 1)
        };
        _enableContinuousKeepAlive = enableContinuousKeepAlive;
    }

    // Forwards (invokes) the data on OnAudioSourceEncodedSample, which actually tells the SIP lib to send data.
    public void ExternalAudioSourceEncodedSample(uint durationRtpUnits, byte[] sample)
    {
        // TODO needs another indicator here that the RTP session is not connected.
        bool isCanceled = _processAudioCancellationSource.IsCancellationRequested;
        bool isReady = _isMediaSessionReady && _isStarted;
        bool hasEventHandler = OnAudioSourceEncodedSample is not null;
        if (!isCanceled && isReady && hasEventHandler)
        {
            OnAudioSourceEncodedSample?.Invoke(durationRtpUnits, sample);
        }
        else
        {
            Log.Warning($"[{GetType().Name}] Cannot forward encoded sample: media session not ready.");
        }
    }

    /// <summary>
    /// Decodes the 8khz 16bit mono PCMU (mu-law), then forwards the 8khz 16bit mono PCM audio RTP frames
    /// to your implementation of ProcessAudioAsync().
    /// Waits for ProcessAudioAsync() to return.
    /// </summary>
    [Obsolete("VoIPMediaSession (SIPSorcery 10.x) delivers incoming audio via GotEncodedMediaFrame, not this RTP-level callback. Kept only for IAudioSink completeness / other media session implementations that may still use it.")]
    public virtual void GotAudioRtp(IPEndPoint remoteEndPoint, uint syncSource, uint seqNum, uint timestamp, int payloadID, bool marker, byte[] payload)
    {
        if (!_isStarted || payloadID != 0)
            return;

        ProcessIncomingPcmuPayload(payload);
    }

    /// <summary>
    /// This is the path VoIPMediaSession actually calls for incoming audio as of SIPSorcery 10.x
    /// (wired via RTPSession.OnAudioFrameReceived += AudioSink.GotEncodedMediaFrame). Supersedes
    /// the RTP-level GotAudioRtp callback above.
    /// </summary>
    public virtual void GotEncodedMediaFrame(EncodedAudioFrame encodedMediaFrame)
    {
        if (!_isStarted)
            return;

        ProcessIncomingPcmuPayload(encodedMediaFrame.EncodedAudio);
    }

    private void ProcessIncomingPcmuPayload(byte[] payload)
    {
        if (payload.Length != 160)
        {
            Log.Warning($"[{GetType().Name}] Unexpected PCMU payload length: {payload.Length} bytes, expected 160 bytes.");
            return;
        }
        // Decode PCMU 8khz 16bit mono to PCM
        short[] decodedSamples = new short[payload.Length];
        for (int i = 0; i < payload.Length; i++)
            decodedSamples[i] = MuLawDecoder.MuLawToLinearSample(payload[i]);
        byte[] rawBytes = new byte[decodedSamples.Length * 2];
        Buffer.BlockCopy(decodedSamples, 0, rawBytes, 0, rawBytes.Length);

        // Hand off for background processing instead of blocking the RTP receive thread.
        // TryWrite never blocks on a bounded DropOldest channel.
        _rtpAudioChannel?.Writer.TryWrite(rawBytes);
    }

    private async Task ProcessAudioChannelAsync(ChannelReader<byte[]> reader, CancellationToken token)
    {
        try
        {
            await foreach (var pcm in reader.ReadAllAsync(token))
            {
                try
                {
                    await ProcessAudioAsync(pcm);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"[{GetType().Name}] Exception while processing incoming audio frame.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public virtual void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample) { }

    public abstract Task InitializeAsync();
    public abstract Task ShutdownAsync();

    /// <summary>
    /// Audio received for internal processing (from cellphone, GotAudioRtp forwards audio to this.)
    /// Audio is 8khz 16bit mono PCM.
    /// </summary>
    protected abstract Task ProcessAudioAsync(byte[] pcm8Khz);

    // IAudioSource and IAudioSink interface implementations
    public void SetAudioSinkFormat(AudioFormat audioFormat) { }
    public void SetAudioSourceFormat(AudioFormat audioFormat) { }
    public bool IsAudioSourcePaused() => !_isMediaSessionReady;
    public Task PauseAudio() => Task.CompletedTask;
    public Task ResumeAudio() => Task.CompletedTask;
    public virtual Task StartAudio()
    {
        _isStarted = true;
        _isMediaSessionReady = true;
        _rtpAudioChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(50)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _rtpProcessingTask = Task.Run(() => ProcessAudioChannelAsync(_rtpAudioChannel.Reader, _processAudioCancellationSource.Token));

        // VoIPMediaSession invokes StartAudio() via BOTH StartAudioSink() and StartAudioSource()
        // (this object implements both interfaces), so guard against starting the pacer twice --
        // RtpPacedSender.Start() throws if already running.
        if (_enableContinuousKeepAlive && !_keepAliveSenderStarted)
        {
            _keepAliveSenderStarted = true;
            _keepAlivePacedSender.SendAction = ExternalAudioSourceEncodedSample;
            _keepAlivePacedSender.Start();
            Log.Information($"[{GetType().Name}] Continuous RTP keep-alive started.");
        }

        Log.Information($"[{GetType().Name}] StartAudio called, _isStarted={_isStarted}, SupportedFormats={string.Join(", ", _supportedFormats.Select(f => f.FormatName))}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Send a 160-byte PCMU frame outbound. When continuous keep-alive is enabled, this queues
    /// onto the paced sender's existing 20ms cadence; otherwise it sends immediately.
    /// </summary>
    protected void SendAudioFrame(byte[] pcmuFrame)
    {
        if (_enableContinuousKeepAlive)
        {
            _keepAlivePacedSender.Enqueue(pcmuFrame);
        }
        else
        {
            ExternalAudioSourceEncodedSample(160, pcmuFrame);
        }
    }
    public Task StartAudioSink() => StartAudio();
    public Task StartAudioSource() => StartAudio();
    public Task PauseAudioSink() => PauseAudio();
    public Task PauseAudioSource() => PauseAudio();
    public Task ResumeAudioSink() => ResumeAudio();
    public Task ResumeAudioSource() => ResumeAudio();
    public Task CloseAudioSink() => CloseAudio();
    public Task CloseAudioSource() => CloseAudio();
    public List<AudioFormat> GetAudioSinkFormats() => _supportedFormats;
    public List<AudioFormat> GetAudioSourceFormats() => _supportedFormats;
    public void RestrictFormats(Func<AudioFormat, bool> filter)
    {
        _supportedFormats.RemoveAll(f => !filter(f));
    }
    public virtual async Task CloseAudio()
    {
        _isStarted = false;
        _isMediaSessionReady = false;
        _rtpAudioChannel?.Writer.TryComplete();
        if (_keepAliveSenderStarted)
        {
            _keepAliveSenderStarted = false;
            await _keepAlivePacedSender.Stop();
        }
    }
    public AudioFormat GetAudioSourceFormat() => _supportedFormats[0];
    public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample != null;
}