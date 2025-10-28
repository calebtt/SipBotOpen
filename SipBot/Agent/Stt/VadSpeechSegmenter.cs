using Serilog;
using WebRtcVadSharp;

namespace SipBot;

public class VadStartFramesBuffer
{
    private readonly int _maxFrames;
    private readonly List<byte[]> _frames = new();

    public VadStartFramesBuffer(int frameCount)
    {
        _maxFrames = frameCount;
    }

    public void AddFrame(ReadOnlySpan<byte> frame)
    {
        if (_frames.Count > _maxFrames)
        {
            _frames.RemoveAt(0);
        }
        _frames.Add(frame.ToArray());
    }

    public List<byte[]> GetFrames()
    {
        return _frames;
    }
}

public class VadFrameCounter
{
    private readonly int _framesUntilTrigger;
    private int _frameCount;

    public VadFrameCounter(int framesUntilStart)
    {
        _framesUntilTrigger = framesUntilStart;
    }

    public void CountTriggerFrame()
    {
        _frameCount++;
    }

    public void CountNonTriggerFrame()
    {
        _frameCount = 0;
    }

    public bool ShouldActivate()
    {
        return _frameCount >= _framesUntilTrigger;
    }
}

public sealed class VadSpeechSegmenter : IDisposable
{
    private readonly WebRtcVad _vad;
    private readonly int _msPerFrame;
    private readonly VadFrameCounter _vadStartFrameCounter;
    private readonly VadFrameCounter _vadEndFrameCounter;
    private readonly VadStartFramesBuffer _vadStartFramesBuffer;
    private readonly MemoryStream _buf = new();
    private bool _isUtteranceInProgress = false;
    private bool _justStartedUtterance = false;
    private bool _isDisposed;

    public event EventHandler? SentenceBegin;
    public event EventHandler<MemoryStream>? SentenceCompleted;

    public bool IsSentenceInProgress => _isUtteranceInProgress;

    public VadSpeechSegmenter(int endOfUtteranceMs = 550, int beginOfUtteranceMs = 500, int preSpeechMs = 1200, int msPerFrame = 20)
    {
        _vad = new WebRtcVad
        {
            OperatingMode = OperatingMode.Aggressive
        };
        _msPerFrame = msPerFrame;
        var speechFramesToStart = Math.Max(1, (int)Math.Ceiling((double)beginOfUtteranceMs / _msPerFrame));
        int preSpeechFrames = (int)Math.Ceiling((double)preSpeechMs / _msPerFrame);
        var speechFramesToEnd = Math.Max(1, (int)Math.Ceiling((double)endOfUtteranceMs / _msPerFrame));

        _vadStartFrameCounter = new(speechFramesToStart);
        _vadEndFrameCounter = new(speechFramesToEnd);
        _vadStartFramesBuffer = new(preSpeechFrames);
    }

    /// <summary>
    /// Expects mono PCM
    /// </summary>
    /// <param name="monoPcm">mono PCM</param>
    public void PushFrame(byte[] monoPcm, SampleRate sampleRate, FrameLength frameLength)
    {
        bool speech = _vad.HasSpeech(monoPcm, sampleRate, frameLength);

        // Always add the frame to the rolling buffer
        _vadStartFramesBuffer.AddFrame(monoPcm);

        if (speech)
        {
            _vadStartFrameCounter.CountTriggerFrame();
            _vadEndFrameCounter.CountNonTriggerFrame();

            if (_vadStartFrameCounter.ShouldActivate() && !_isUtteranceInProgress)
            {
                _isUtteranceInProgress = true;
                _justStartedUtterance = true;
                // Copy rolling buffer to main buffer
                foreach (var frame in _vadStartFramesBuffer.GetFrames())
                {
                    _buf.Write(frame);
                }
                if (SentenceBegin != null)
                {
                    Task.Run(() => SentenceBegin.Invoke(this, EventArgs.Empty));
                }
            }
            if (_isUtteranceInProgress && !_justStartedUtterance)
            {
                _buf.Write(monoPcm);
            }
            else if (_justStartedUtterance)
            {
                _justStartedUtterance = false;
            }
        }
        else
        {
            _vadStartFrameCounter.CountNonTriggerFrame();
            _vadEndFrameCounter.CountTriggerFrame();

            if (_isUtteranceInProgress)
            {
                _buf.Write(monoPcm); // Include silence in the utterance

                if (_vadEndFrameCounter.ShouldActivate())
                {
                    _isUtteranceInProgress = false;
                    var memStream = new MemoryStream(_buf.ToArray());
                    if (SentenceCompleted != null)
                    {
                        Task.Run(() => SentenceCompleted?.Invoke(this, memStream));
                    }
                    _buf.SetLength(0); // Clear buffer for next utterance
                }
            }
        }
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _vad.Dispose();
            _isDisposed = true;
            Log.Information("VadSpeechSegmenter disposed.");
        }
    }
}