using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Utilities;
using Microsoft.ML.OnnxRuntime;
using NAudio.Codecs;
using NAudio.Wave;
using Serilog;
using System.Buffers; // For ArrayPool (zero-alloc)
using System.Collections.Concurrent; // For ConcurrentQueue
using System.Diagnostics; // For RTF metrics
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace SipBot;

/// <summary>
/// Public static partial class for audio processing algorithms using NAudio where possible.
/// Handles WAV creation via NAudio.
/// </summary>
public static partial class Algos
{
    private static readonly Regex SentenceRegex = new(@"(?<!\w\.\w.)(?<![A-Z][a-z]\.)(?<=\.|\?|\!)\s", RegexOptions.Compiled | RegexOptions.Multiline); // Efficient sentence splitter

    /// <summary>
    /// Splits text into sentences for streaming synthesis (modern: compiled regex, zero-alloc where possible).
    /// </summary>
    public static IEnumerable<string> SplitIntoSentences(string text) => SentenceRegex.Split(text).Where(s => !string.IsNullOrWhiteSpace(s));

    /// <summary>
    /// Creates a WAV file from raw 16-bit mono PCM bytes using NAudio's WaveFileWriter.
    /// Ensures valid RIFF header, fmt chunk, and data alignment for NAudio compatibility.
    /// </summary>
    public static byte[] CreateWavFromPcm(byte[] rawPcmBytes, int sampleRate)
    {
        if (rawPcmBytes.Length % 2 != 0)
        {
            throw new ArgumentException("Raw PCM must be even length for 16-bit samples.", nameof(rawPcmBytes));
        }

        var format = new WaveFormat(sampleRate, 16, 1); // 22kHz mono 16-bit PCM
        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(ms, format))
        {
            writer.Write(rawPcmBytes, 0, rawPcmBytes.Length); // Explicit byte[] overload for compatibility
        }
        var wavBytes = ms.ToArray();
        Log.Debug("Generated WAV header: {Length} bytes (fmt chunk present)", wavBytes.Length);
        return wavBytes;
    }

    /// <summary>
    /// Convert a WAV file (as byte array) to raw PCM (16-bit, mono) at a specified target sample rate.
    /// Note that it reads the WAV file header for information on the source audio's sample rate.
    /// </summary>
    /// <param name="wavAudio">WAV file data</param>
    /// <param name="targetSampleRateHz">Desired output sample rate (e.g. 8000, 16000)</param>
    /// <returns>Raw PCM byte array (16-bit mono)</returns>
    public static byte[] ConvertWavToPcm(byte[] wavAudio, int targetSampleRateHz)
    {
        try
        {
            using var wavStream = new MemoryStream(wavAudio);
            using var reader = new WaveFileReader(wavStream);

            var inputFormat = reader.WaveFormat;
            var targetFormat = new WaveFormat(targetSampleRateHz, 16, 1);

            using var resampler = new MediaFoundationResampler(reader, targetFormat)
            {
                ResamplerQuality = 60
            };

            using var outStream = new MemoryStream();
            byte[] buffer = new byte[4096];
            int bytesRead;
            while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
            {
                outStream.Write(buffer, 0, bytesRead);
            }

            byte[] rawPcm = outStream.ToArray();
            Log.Debug($"Converted WAV to PCM: {rawPcm.Length} bytes, {targetSampleRateHz} Hz, 16-bit mono");
            return rawPcm;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to convert WAV to raw PCM.");
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Encodes 16 bit mono PCM data, irrespective of sample rate (this encoding algo doesn't depend on it.)
    /// </summary>
    public static byte[] EncodePcmToPcmuWithNAudio(byte[] pcmSamples)
    {
        if (pcmSamples.Length % 2 != 0)
        {
            Log.Warning($"[EncodePcmToPcmuWithNAudio] PCM samples length {pcmSamples.Length} is not even, trimming last byte.");
            Array.Resize(ref pcmSamples, pcmSamples.Length - 1);
        }

        int sampleCount = pcmSamples.Length / 2;
        byte[] pcmu = new byte[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            // Combine two bytes into one short (little-endian)
            short sample = (short)(pcmSamples[i * 2] | (pcmSamples[i * 2 + 1] << 8));
            pcmu[i] = MuLawEncoder.LinearToMuLawSample(sample);
        }

        return pcmu;
    }

}

/// <summary>
/// A static service class for offline, high-performance text-to-speech synthesis using KokoroSharp.
/// Outputs 8kHz mono PCMU-encoded audio bytes by default (G.711 μ-law, no header).
/// Alternatively, can output WAV (22kHz mono PCM) via parameter.
/// Models and voices (~320MB total) download on first use. Thread-safe with lazy init.
/// Delegates processing to <see cref="Algos"/> for SRP compliance.
/// GPU acceleration auto-enabled with KokoroSharp.GPU.Windows NuGet + CUDA (2-5x speedup).
/// </summary>
public static class TtsProviderStreaming
{
    private static KokoroWavSynthesizer? _synthesizer;
    private static readonly SemaphoreSlim _initLock = new(1, 1);
    private static readonly object _voiceLock = new();
    private static readonly ConcurrentDictionary<string, KokoroVoice> _voiceCache = new();
    private static readonly string _modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "kokoro.onnx");
    private static readonly Stopwatch _perfTimer = new(); // For RTF logging
    private const int MaxConcurrency = 3; // Limit concurrent segments for GPU/memory (configurable)

    public const string DefaultVoiceKey = "af_heart"; // Natural American English

    /// <summary>
    /// Synthesizes text to audio asynchronously, returning a seekable MemoryStream (non-streaming, full utterance).
    /// </summary>
    public static async Task<MemoryStream> TextToSpeechAsync(
        string text,
        string? voiceKey = null,
        bool outputAsWav = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text cannot be null or empty.", nameof(text));
        }

        voiceKey ??= DefaultVoiceKey;

        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        voiceKey = voiceKey ?? DefaultVoiceKey;
        var voice = GetOrCacheVoice(voiceKey);

        // Synthesize to raw PCM bytes (async via Task.Run for sync method; DIP: Delegates to Algos for processing)
        _perfTimer.Restart();
        byte[] rawPcmBytes = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return _synthesizer!.Synthesize(text, voice); // Sync method returns byte[] raw PCM (22kHz 16-bit mono; GPU-accelerated if enabled)
        }, ct).ConfigureAwait(false);
        _perfTimer.Stop();
        var rtf = (double)text.Length / _perfTimer.Elapsed.TotalSeconds; // Rough RTF estimate (chars/sec; >100 on GPU)
        Log.Information("TTS synthesis RTF: {Rtf:F2} chars/sec ({Elapsed}s for {Length} chars)", rtf, _perfTimer.Elapsed.TotalSeconds, text.Length);

        if (rawPcmBytes.Length == 0)
        {
            throw new InvalidOperationException("Synthesis returned empty PCM data.");
        }

        byte[] outputBytes;
        if (outputAsWav)
        {
            outputBytes = Algos.CreateWavFromPcm(rawPcmBytes, 22050); // Full WAV with valid fmt chunk
            Log.Debug($"Synthesized to WAV: {outputBytes.Length} bytes");
        }
        else
        {
            // Create temp WAV for AudioAlgos pipeline (ensures fmt chunk for NAudio)
            var tempWavBytes = Algos.CreateWavFromPcm(rawPcmBytes, 22050);
            byte[] pcm8kHz = Algos.ConvertWavToPcm(tempWavBytes, 8000);
            outputBytes = Algos.EncodePcmToPcmuWithNAudio(pcm8kHz);
            Log.Debug($"Synthesized and converted to 8kHz PCMU: {outputBytes.Length} bytes");
        }

        var stream = new MemoryStream(outputBytes);
        stream.Position = 0; // Reset for reading
        return stream;
    }

    /// <summary>
    /// Synthesizes text to streaming PCMU chunks asynchronously (IAsyncEnumerable for zero-copy yield).
    /// Prioritizes first sentence: Synthesizes sync/immediately for <500ms first chunk; parallels the rest.
    /// Outputs 8kHz mono PCMU (no header). Uses helpers for modularity.
    /// </summary>
    public static async IAsyncEnumerable<byte[]> TextToSpeechStreamAsync(
        string text,
        string? voiceKey = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        voiceKey ??= DefaultVoiceKey;
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var voice = GetOrCacheVoice(voiceKey);

        var sentences = Algos.SplitIntoSentences(text).ToArray();
        if (sentences.Length == 0)
            yield break;

        Log.Information("Streaming TTS: {SentenceCount} sentences, prioritizing first for low latency", sentences.Length);

        var channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });

        // PRIORITY: Yield first sentence immediately (sync via helper for fast start)
        var firstChunk = await SynthesizeSentenceToPcmuAsync(_synthesizer!, voice, sentences[0], ct).ConfigureAwait(false);
        if (firstChunk.Length > 0)
        {
            await channel.Writer.WriteAsync(firstChunk, ct).ConfigureAwait(false);
            Log.Debug("Streaming TTS: First chunk yielded ({Bytes} bytes)", firstChunk.Length);
        }

        // Parallelize + yield remaining sentences (fire-and-forget write task)
        _ = WriteRemainingSentencesAsync(sentences, 1, voice, channel.Writer, ct);

        // Yield all chunks (first already written)
        await foreach (var chunk in YieldChunksFromChannel(channel.Reader, ct))
        {
            yield return chunk;
        }

        Log.Information("Streaming TTS complete ({Sentences} segments)", sentences.Length);
    }

    /// <summary>
    /// Helper: Synthesizes a single sentence to PCMU bytes (sync inference wrapped in ValueTask for perf).
    /// Logs RTF for first sentence only (via optional param).
    /// </summary>
    private static async ValueTask<byte[]> SynthesizeSentenceToPcmuAsync(
        KokoroWavSynthesizer synthesizer,
        KokoroVoice voice,
        string sentence,
        CancellationToken ct,
        bool logRtf = false)
    {
        var perfTimer = logRtf ? Stopwatch.StartNew() : null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var rawPcm = synthesizer.Synthesize(sentence, voice);
            if (rawPcm.Length == 0)
                return Array.Empty<byte>();

            var tempWav = Algos.CreateWavFromPcm(rawPcm, 22050);
            var pcm8k = Algos.ConvertWavToPcm(tempWav, 8000);
            var chunk = Algos.EncodePcmToPcmuWithNAudio(pcm8k);

            if (logRtf)
            {
                perfTimer!.Stop();
                var rtf = (double)sentence.Length / perfTimer.Elapsed.TotalSeconds;
                Log.Information("TTS first-sentence RTF: {Rtf:F2} chars/sec ({Elapsed}s for {Length} chars)", rtf, perfTimer.Elapsed.TotalSeconds, sentence.Length);
                Log.Debug("Streaming TTS: First sentence chunk ready ({Bytes} bytes)", chunk.Length);
            }

            return chunk;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in sentence TTS: '{Sentence}'", sentence);
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Helper: Launches parallel tasks for remaining sentences and writes chunks sequentially to channel.
    /// Uses semaphore for concurrency limit; fire-and-forget for non-blocking.
    /// </summary>
    private static async Task WriteRemainingSentencesAsync(
        string[] sentences,
        int startIndex,
        KokoroVoice voice,
        ChannelWriter<byte[]> writer,
        CancellationToken ct)
    {
        using var semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);

        var synthesisTasks = new List<Task<byte[]>>();
        for (int i = startIndex; i < sentences.Length; i++)
        {
            var sentence = sentences[i];
            synthesisTasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    return await SynthesizeSentenceToPcmuAsync(_synthesizer!, voice, sentence, ct).ConfigureAwait(false);
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct));
        }

        try
        {
            foreach (var task in synthesisTasks)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = await task.ConfigureAwait(false);
                if (chunk.Length > 0)
                {
                    await writer.WriteAsync(chunk, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            writer.TryComplete();
        }
    }

    /// <summary>
    /// Helper: Yields chunks from channel reader with explicit cancellation check (thin wrapper for clarity).
    /// </summary>
    private static async IAsyncEnumerable<byte[]> YieldChunksFromChannel(
        ChannelReader<byte[]> reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var chunk in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            yield return chunk;
        }
    }

    /// <summary>
    /// Pre-initializes the service for better first-call performance (SRP: Init only).
    /// </summary>
    public static async Task InitializeAsync(string? voiceKey = null, CancellationToken ct = default)
    {
        voiceKey ??= DefaultVoiceKey;
        await Task.Run(() =>
        {
            _ = KokoroTTS.LoadModel();
        }, ct).ConfigureAwait(false);

        // Cache default voice (OCP: Lazy per-call, but init for perf)
        if (!_voiceCache.ContainsKey(voiceKey))
        {
            var voice = KokoroVoiceManager.GetVoice(voiceKey) ?? throw new ArgumentException("Default voice not found.", nameof(voiceKey));
            _voiceCache[voiceKey] = voice;
        }
    }

    /// <summary>
    /// Lists available voice keys synchronously for American English (SRP: Query only).
    /// </summary>
    public static IReadOnlyList<string> ListVoices()
    {
        return KokoroVoiceManager.GetVoices(KokoroLanguage.AmericanEnglish).Select(v => v.Name).ToList();
    }

    /// <summary>
    /// Ensures initialized (internal async).
    /// </summary>
    private static async Task EnsureInitializedAsync(CancellationToken ct)
    {
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_synthesizer is null)
            {
                ct.ThrowIfCancellationRequested();
                _ = KokoroTTS.LoadModel(); // Existing

                if (!File.Exists(_modelPath))
                    throw new InvalidOperationException($"Kokoro model not found at {_modelPath}");

                // NEW: Create GPU-enabled SessionOptions (robust: explicit provider, error handling)
                var sessionOptions = SessionOptions.MakeSessionOptionWithCudaProvider(0); // deviceId=0 for primary GPU
                //var sessionOptions = SessionOptions.MakeSessionOptionWithTensorrtProvider(0); // deviceId=0 for primary GPU
                sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL; // Modern: Full opts for perf
                sessionOptions.EnableMemoryPattern = true; // Reduce VRAM allocs
                sessionOptions.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL; // Stable for TTS
                sessionOptions.IntraOpNumThreads = Environment.ProcessorCount; // Max CPU threads for non-GPU ops
                sessionOptions.InterOpNumThreads = 1; // Single thread for inter-op to reduce context switching

                //sessionOptions.AppendExecutionProvider_CUDA(); // Ensure CUDA provider appended
                Log.Information("Attempting to initialize Kokoro with GPU acceleration...");
                try
                {
                    // Assuming KokoroWavSynthesizer accepts SessionOptions (check/fork source if not)
                    _synthesizer = new KokoroWavSynthesizer(_modelPath, sessionOptions);
                }
                catch (OnnxRuntimeException ex) when (ex.Message.Contains("CUDA") || ex.Message.Contains("cuDNN"))
                {
                    Log.Error(ex, "GPU init failed - falling back to CPU. Check CUDA/cuDNN versions and PATH.");
                    // Fallback: Create CPU session
                    var cpuOptions = new SessionOptions();
                    _synthesizer = new KokoroWavSynthesizer(_modelPath, cpuOptions);
                }
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Gets or caches voice (thread-safe).
    /// </summary>
    private static KokoroVoice GetOrCacheVoice(string voiceKey)
    {
        if (!_voiceCache.TryGetValue(voiceKey, out var voice))
        {
            lock (_voiceLock)
            {
                if (!_voiceCache.TryGetValue(voiceKey, out voice))
                {
                    voice = KokoroVoiceManager.GetVoice(voiceKey);
                    if (voice is null)
                    {
                        var available = string.Join(", ", KokoroVoiceManager.GetVoices(KokoroLanguage.AmericanEnglish).Select(v => v.Name));
                        throw new ArgumentException($"Voice key '{voiceKey}' not found. Available: {available}", nameof(voiceKey));
                    }
                    _voiceCache[voiceKey] = voice;
                }
            }
        }
        return voice;
    }
}