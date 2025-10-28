using Serilog;

namespace SipBot;

public class RtpAudioPacer
{
    private const int PCMU_FRAME_SIZE = 160;
    private RtpPacedSender? _rtpSender;
    private bool _isAttached;

    public event Action? SendingComplete
    {
        add
        {
            if (_rtpSender != null)
            {
                _rtpSender.SendingComplete += value;
            }
        }
        remove
        {
            if (_rtpSender != null)
            {
                _rtpSender.SendingComplete -= value;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether audio is currently playing (queue has frames).
    /// </summary>
    public bool IsAudioPlaying => _rtpSender != null && _rtpSender.IsPlaying;

    public void Attach(BaseAudioEndPoint audioEndPoint)
    {
        if (_isAttached)
            return;

        if (audioEndPoint is not StreamingVoiceAudioEndPoint streamingEndPoint)
        {
            throw new ArgumentException($"Unsupported audio endpoint type: {audioEndPoint.GetType().Name}", nameof(audioEndPoint));
        }

        streamingEndPoint.OnAudioReplyReady += OnAudioReplyReady;

        _rtpSender = new RtpPacedSender();
        _rtpSender.SendAction += audioEndPoint.ExternalAudioSourceEncodedSample!;
        _rtpSender.Start();

        _isAttached = true;

        Log.Information($"[{nameof(RtpAudioPacer)}] SIP audio pacer attached to {audioEndPoint.GetType().Name}.");
    }

    public async Task Detach(BaseAudioEndPoint audioEndPoint)
    {
        if (!_isAttached)
            return;

        if (audioEndPoint is StreamingVoiceAudioEndPoint streamingEndPoint)
        {
            streamingEndPoint.OnAudioReplyReady -= OnAudioReplyReady;
        }

        if (_rtpSender != null)
        {
            _rtpSender.SendAction -= audioEndPoint.ExternalAudioSourceEncodedSample;
            await _rtpSender.Stop();
            _rtpSender = null;
        }

        _isAttached = false;

        Log.Information($"[{nameof(RtpAudioPacer)}] SIP audio pacer detached from {audioEndPoint.GetType().Name}.");
    }

    /// <summary>
    /// Applies a filter to outgoing audio frames until ClearFilter() is called.
    /// Synchronous; replaces any existing filter.
    /// </summary>
    /// <param name="filter">The filter function to apply (input: 160-byte PCMU frame; output: transformed frame).</param>
    public void ApplyFilter(Func<byte[], byte[]> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (!_isAttached || _rtpSender == null)
        {
            Log.Warning($"[{nameof(RtpAudioPacer)}] Cannot apply filter: pacer not attached.");
            return;
        }

        _rtpSender.ApplyFilter(filter);
    }

    /// <summary>
    /// Immediately clears any active filter.
    /// </summary>
    public void ClearFilter()
    {
        _rtpSender?.ClearFilter();
    }

    /// <summary>
    /// Manually enqueues audio data to be sent with RTP pacing.
    /// </summary>
    public void EnqueueBufferForSendManual(byte[] pcmuBytes)
    {
        ProcessAndSend(pcmuBytes);
    }

    /// <summary>
    /// Splits the input bytes into PCMU frames and enqueues them directly.
    /// Discards any remaining bytes that don't form a complete frame.
    /// </summary>
    private void ProcessAndSend(byte[] pcmuBytes)
    {
        if (!_isAttached || pcmuBytes == null || pcmuBytes.Length == 0 || _rtpSender == null)
            return;

        var offset = 0;
        while (offset + PCMU_FRAME_SIZE <= pcmuBytes.Length)
        {
            var frame = new byte[PCMU_FRAME_SIZE];
            Array.Copy(pcmuBytes, offset, frame, 0, PCMU_FRAME_SIZE);
            _rtpSender.Enqueue(frame);
            offset += PCMU_FRAME_SIZE;
        }

        if (offset < pcmuBytes.Length)
        {
            var remaining = pcmuBytes.Length - offset;
            Log.Debug($"[{nameof(RtpAudioPacer)}] Discarded {remaining} PCMU bytes (incomplete frame).");
        }
    }

    /// <summary>
    /// Receives audio FROM TTS. NOTE: The entire audio response is expected, not streaming!
    /// </summary>
    public void OnAudioReplyReady(byte[] pcmuBytes)
    {
        if (pcmuBytes == null || pcmuBytes.Length == 0)
        {
            Log.Warning($"[{nameof(RtpAudioPacer)}] Received empty or null PCMU audio from TTS.");
            return;
        }

        if (!_isAttached)
            return;

        ProcessAndSend(pcmuBytes);
    }

    /// <summary>
    /// Allows manual buffer reset (should be called at the start of a new TTS response)
    /// </summary>
    public void ResetBuffer()
    {
        _rtpSender?.ResetBuffer();
    }
}