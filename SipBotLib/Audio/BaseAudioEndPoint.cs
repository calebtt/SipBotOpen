using NAudio.Codecs;
using Serilog;
using SIPSorceryMedia.Abstractions;
using System.Net;
namespace SipBot;

public abstract class BaseAudioEndPoint : IAudioSource, IAudioSink
{
    protected List<AudioFormat> _supportedFormats;
    protected bool _isStarted = false;
    protected bool _isMediaSessionReady = false;
    protected CancellationTokenSource _processAudioCancellationSource = new();

    public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
    public event RawAudioSampleDelegate? OnAudioSourceRawSample;
    public event SourceErrorDelegate? OnAudioSourceError;
    public event SourceErrorDelegate? OnAudioSinkError;
    public event EncodedSampleDelegate? OnAudioSinkSample;

    public BaseAudioEndPoint()
    {
        _supportedFormats = new List<AudioFormat>
        {
            new AudioFormat(0, "PCMU", 8000, 1)
        };
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
    public virtual void GotAudioRtp(IPEndPoint remoteEndPoint, uint syncSource, uint seqNum, uint timestamp, int payloadID, bool marker, byte[] payload)
    {
        if (!_isStarted || payloadID != 0)
            return;

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

        var task = ProcessAudioAsync(rawBytes);
        task.Wait(_processAudioCancellationSource.Token);
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
        Log.Information($"[{GetType().Name}] StartAudio called, _isStarted={_isStarted}, SupportedFormats={string.Join(", ", _supportedFormats.Select(f => f.FormatName))}");
        return Task.CompletedTask;
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
    public virtual Task CloseAudio()
    {
        _isStarted = false;
        _isMediaSessionReady = false;
        return Task.CompletedTask;
    }
    public AudioFormat GetAudioSourceFormat() => _supportedFormats[0];
    public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample != null;
}