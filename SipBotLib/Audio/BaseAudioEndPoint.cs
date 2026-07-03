using Serilog;
using SIPSorcery.Media;
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

    // GotAudioRtp/GotEncodedMediaFrame are invoked synchronously by the SIP/RTP receive path.
    // Frames are handed off to this channel (decoded PCM + the sample rate it decoded to) and
    // drained by a single background worker, so ProcessAudioAsync() (which may do real work like
    // STT/echo-cancellation) never blocks the RTP thread. Bounded + DropOldest keeps memory bounded
    // and favors real-time freshness over completeness if the consumer falls behind, rather than
    // growing an unbounded backlog.
    private Channel<(byte[] Pcm, int SampleRateHz)>? _rtpAudioChannel;
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
    private volatile bool _audioEncoderDisposed;

    // Wideband audio (G.722, 16kHz): off by default so existing subclasses keep their exact
    // current behavior (PCMU/8kHz only) unless they explicitly opt in. G.722 is a good first
    // wideband codec to add because it's supported out of the box by Asterisk-based PBX systems
    // (including VitalPBX) with no licensing or extra modules, and its 20ms RTP payload is 160
    // bytes -- same as PCMU's -- so RtpPacedSender's fixed frame size didn't need to change.
    //
    // NOTE: G.722 has a well-known RTP quirk -- the RTP/SDP clock rate is declared as 8000 for
    // historical compatibility, but the actual decoded PCM sample rate is 16000. Use
    // AudioFormat.ClockRate (not RtpClockRate) to get the real sample rate; this class does that
    // throughout.
    //
    // Enabling this changes ProcessAudioAsync's effective sample rate to whatever gets negotiated
    // (8000 or 16000) -- a subclass MUST use the sampleRateHz parameter rather than assuming 8kHz.
    // It also changes SendAudioFrame's contract to raw PCM in (previously pre-encoded PCMU bytes);
    // see that method's remarks.
    private readonly bool _enableWidebandAudio;
    private readonly AudioEncoder _audioEncoder;
    private readonly AudioFormat _pcmuFormat = new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU);
    private readonly AudioFormat _g722Format = new AudioFormat(SDPWellKnownMediaFormatsEnum.G722);
    private AudioFormat _negotiatedSendFormat;

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
    /// <param name="enableWidebandAudio">
    /// Advertise and support G.722 (16kHz) in addition to PCMU (8kHz). Defaults to false -- see
    /// class remarks above for what changes when this is enabled.
    /// </param>
    public BaseAudioEndPoint(bool enableContinuousKeepAlive = false, bool enableWidebandAudio = false)
    {
        _enableContinuousKeepAlive = enableContinuousKeepAlive;
        _enableWidebandAudio = enableWidebandAudio;

        _audioEncoder = enableWidebandAudio
            ? new AudioEncoder(_g722Format, _pcmuFormat) // G.722 listed first: preferred if the far end supports it
            : new AudioEncoder(_pcmuFormat);
        _supportedFormats = new List<AudioFormat>(_audioEncoder.SupportedFormats);
        _negotiatedSendFormat = _pcmuFormat; // safe default until SetAudioSourceFormat is called post-negotiation
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
    /// Decodes an RTP-level payload (PCMU or, if wideband is enabled, G.722), then forwards the
    /// decoded PCM to your implementation of ProcessAudioAsync().
    /// </summary>
    [Obsolete("VoIPMediaSession (SIPSorcery 10.x) delivers incoming audio via GotEncodedMediaFrame, not this RTP-level callback. Kept only for IAudioSink completeness / other media session implementations that may still use it.")]
    public virtual void GotAudioRtp(IPEndPoint remoteEndPoint, uint syncSource, uint seqNum, uint timestamp, int payloadID, bool marker, byte[] payload)
    {
        if (!_isStarted)
            return;

        if (payloadID == _pcmuFormat.FormatID)
        {
            ProcessIncomingEncodedPayload(payload, _pcmuFormat);
        }
        else if (_enableWidebandAudio && payloadID == _g722Format.FormatID)
        {
            ProcessIncomingEncodedPayload(payload, _g722Format);
        }
        else
        {
            Log.Warning($"[{GetType().Name}] GotAudioRtp: unrecognized/unsupported payload type {payloadID}, dropping.");
        }
    }

    /// <summary>
    /// This is the path VoIPMediaSession actually calls for incoming audio as of SIPSorcery 10.x
    /// (wired via RTPSession.OnAudioFrameReceived += AudioSink.GotEncodedMediaFrame). Supersedes
    /// the RTP-level GotAudioRtp callback above. The frame carries its own AudioFormat, so decoding
    /// is correct regardless of which codec was actually negotiated for this call.
    /// </summary>
    public virtual void GotEncodedMediaFrame(EncodedAudioFrame encodedMediaFrame)
    {
        if (!_isStarted)
            return;

        ProcessIncomingEncodedPayload(encodedMediaFrame.EncodedAudio, encodedMediaFrame.AudioFormat);
    }

    private void ProcessIncomingEncodedPayload(byte[] payload, AudioFormat format)
    {
        short[] decodedSamples;
        try
        {
            decodedSamples = _audioEncoder.DecodeAudio(payload, format);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[{GetType().Name}] Failed to decode {format.FormatName} payload ({payload.Length} bytes).");
            return;
        }

        byte[] rawBytes = new byte[decodedSamples.Length * 2];
        Buffer.BlockCopy(decodedSamples, 0, rawBytes, 0, rawBytes.Length);

        // Hand off for background processing instead of blocking the RTP receive thread.
        // TryWrite never blocks on a bounded DropOldest channel. format.ClockRate (not
        // RtpClockRate) is the real PCM sample rate -- see the G.722 note in the class remarks.
        _rtpAudioChannel?.Writer.TryWrite((rawBytes, format.ClockRate));
    }

    private async Task ProcessAudioChannelAsync(ChannelReader<(byte[] Pcm, int SampleRateHz)> reader, CancellationToken token)
    {
        try
        {
            await foreach (var (pcm, sampleRateHz) in reader.ReadAllAsync(token))
            {
                try
                {
                    await ProcessAudioAsync(pcm, sampleRateHz);
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
    /// Audio received for internal processing (from cellphone, GotAudioRtp/GotEncodedMediaFrame
    /// forwards audio to this). Audio is 16-bit mono PCM at sampleRateHz -- 8000 for PCMU, or 16000
    /// for G.722 when wideband audio is enabled. Do not assume 8kHz; use sampleRateHz.
    /// </summary>
    protected abstract Task ProcessAudioAsync(byte[] pcm, int sampleRateHz);

    // IAudioSource and IAudioSink interface implementations
    public void SetAudioSinkFormat(AudioFormat audioFormat) { }

    /// <summary>
    /// Called by VoIPMediaSession once SDP negotiation completes, telling us which format to use
    /// for OUTBOUND audio. (There's no equivalent call for the sink/inbound format -- per
    /// SIPSorcery's own VoIPMediaSession comments, that isn't knowable until the first RTP packet
    /// actually arrives, which is why GotEncodedMediaFrame reads the format off each frame instead.)
    /// </summary>
    public void SetAudioSourceFormat(AudioFormat audioFormat)
    {
        _negotiatedSendFormat = audioFormat;
        Log.Information($"[{GetType().Name}] Negotiated outbound audio format: {audioFormat.FormatName} @ {audioFormat.ClockRate}Hz (RTP clock rate {audioFormat.RtpClockRate}Hz).");
    }

    public bool IsAudioSourcePaused() => !_isMediaSessionReady;
    public Task PauseAudio() => Task.CompletedTask;
    public Task ResumeAudio() => Task.CompletedTask;
    public virtual Task StartAudio()
    {
        _isStarted = true;
        _isMediaSessionReady = true;
        _rtpAudioChannel = Channel.CreateBounded<(byte[] Pcm, int SampleRateHz)>(new BoundedChannelOptions(50)
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
    /// Send one 20ms frame of raw 16-bit mono PCM outbound. Resamples to the negotiated format's
    /// rate if pcmSampleRateHz doesn't already match it, then encodes with whichever codec was
    /// actually negotiated (PCMU or, if wideband audio is enabled and the far end agreed, G.722)
    /// before handing the encoded bytes to ExternalAudioSourceEncodedSample (paced via
    /// RtpPacedSender if continuous keep-alive is enabled, otherwise sent immediately).
    /// </summary>
    protected void SendAudioFrame(byte[] pcm, int pcmSampleRateHz)
    {
        short[] samples = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, samples, 0, samples.Length * 2);

        if (pcmSampleRateHz != _negotiatedSendFormat.ClockRate)
        {
            samples = PcmResampler.Resample(samples, pcmSampleRateHz, _negotiatedSendFormat.ClockRate);
        }

        byte[] encoded;
        try
        {
            encoded = _audioEncoder.EncodeAudio(samples, _negotiatedSendFormat);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[{GetType().Name}] Failed to encode outbound frame as {_negotiatedSendFormat.FormatName}.");
            return;
        }

        if (_enableContinuousKeepAlive)
        {
            _keepAlivePacedSender.Enqueue(encoded);
        }
        else
        {
            ExternalAudioSourceEncodedSample((uint)(pcmSampleRateHz / 50), encoded); // 50 frames/sec == 20ms/frame
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
        // CloseAudio(), like StartAudio(), can be invoked twice (via both CloseAudioSink() and
        // CloseAudioSource()) since this object implements both interfaces -- guard against
        // double-disposing the codec state.
        if (!_audioEncoderDisposed)
        {
            _audioEncoderDisposed = true;
            _audioEncoder.Dispose();
        }
    }
    public AudioFormat GetAudioSourceFormat() => _supportedFormats[0];
    public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample != null;
}
