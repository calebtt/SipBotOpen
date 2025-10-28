using Serilog;

namespace SipBot;

public static class AudioConstants
{
    public const int FRAME_SIZE_MS = 20; // 20ms frames
    public const int SAMPLE_RATE_8KHZ = 8000;
    public const int SAMPLE_RATE_16KHZ = 16000;
    public const int FRAME_SIZE_16KHZ = SAMPLE_RATE_16KHZ * FRAME_SIZE_MS / 1000; // 320 samples
}

/// <summary>
/// Configuration for audio processing components including echo cancellation and noise suppression.
/// </summary>
public class AudioProcessingConfig
{
    // Echo Cancellation Settings
    public int SystemLatencyMs { get; set; } = 160; // Estimated system latency in milliseconds
    public int FilterLengthMs { get; set; } = 100; // Echo cancellation filter length in milliseconds
    public bool EnableEchoCancellation { get; set; } = true;

    // Noise Suppression Settings
    public bool EnableNoiseSuppression { get; set; } = true;
    public NoiseSuppressionLevel NoiseSuppressionLevel { get; set; } = NoiseSuppressionLevel.Moderate;

    // Automatic Gain Control Settings
    public bool EnableAutomaticGainControl { get; set; } = true;
    public int TargetLevelDbfs { get; set; } = -18; // Target level in dBFS
    public int CompressionGainDb { get; set; } = 9; // Compression gain in dB

    // Audio Format Settings
    public int ProcessingSampleRate { get; set; } = 16000; // Sample rate for processing
    public int FrameSizeMs { get; set; } = 20; // Frame size in milliseconds
    public int Channels { get; set; } = 1; // Mono audio
    public int BitsPerSample { get; set; } = 16; // 16-bit audio

    // Performance Settings
    public bool EnableMetrics { get; set; } = false; // Enable processing metrics
    public bool EnableDebugLogging { get; set; } = false; // Enable detailed debug logging

    /// <summary>
    /// Creates a default configuration optimized for voice communication.
    /// </summary>
    public static AudioProcessingConfig CreateDefault()
    {
        return new AudioProcessingConfig
        {
            SystemLatencyMs = 160,
            FilterLengthMs = 100,
            EnableEchoCancellation = true,
            EnableNoiseSuppression = true,
            NoiseSuppressionLevel = NoiseSuppressionLevel.Moderate,
            EnableAutomaticGainControl = true,
            TargetLevelDbfs = -18,
            CompressionGainDb = 9,
            ProcessingSampleRate = 16000,
            FrameSizeMs = 20,
            Channels = 1,
            BitsPerSample = 16,
            EnableMetrics = false,
            EnableDebugLogging = false
        };
    }

    /// <summary>
    /// Creates a configuration optimized for noisy environments.
    /// </summary>
    public static AudioProcessingConfig CreateNoiseOptimized()
    {
        return new AudioProcessingConfig
        {
            SystemLatencyMs = 160,
            FilterLengthMs = 100,
            EnableEchoCancellation = true,
            EnableNoiseSuppression = true,
            NoiseSuppressionLevel = NoiseSuppressionLevel.Aggressive,
            EnableAutomaticGainControl = true,
            TargetLevelDbfs = -15,
            CompressionGainDb = 12,
            ProcessingSampleRate = 16000,
            FrameSizeMs = 20,
            Channels = 1,
            BitsPerSample = 16,
            EnableMetrics = false,
            EnableDebugLogging = false
        };
    }

    /// <summary>
    /// Creates a configuration optimized for low-latency applications.
    /// </summary>
    public static AudioProcessingConfig CreateLowLatency()
    {
        return new AudioProcessingConfig
        {
            SystemLatencyMs = 80,
            FilterLengthMs = 50,
            EnableEchoCancellation = true,
            EnableNoiseSuppression = true,
            NoiseSuppressionLevel = NoiseSuppressionLevel.Conservative,
            EnableAutomaticGainControl = true,
            TargetLevelDbfs = -20,
            CompressionGainDb = 6,
            ProcessingSampleRate = 16000,
            FrameSizeMs = 10,
            Channels = 1,
            BitsPerSample = 16,
            EnableMetrics = false,
            EnableDebugLogging = false
        };
    }

    /// <summary>
    /// Logs the current configuration for debugging purposes.
    /// </summary>
    public void LogConfiguration()
    {
        Log.Information("Audio Processing Configuration:");
        Log.Information($"  Echo Cancellation: {EnableEchoCancellation} (Latency: {SystemLatencyMs}ms, Filter: {FilterLengthMs}ms)");
        Log.Information($"  Noise Suppression: {EnableNoiseSuppression} (Level: {NoiseSuppressionLevel})");
        Log.Information($"  Automatic Gain Control: {EnableAutomaticGainControl} (Target: {TargetLevelDbfs}dBFS, Gain: {CompressionGainDb}dB)");
        Log.Information($"  Audio Format: {ProcessingSampleRate}Hz, {FrameSizeMs}ms frames, {Channels} channel(s), {BitsPerSample}-bit");
        Log.Information($"  Performance: Metrics={EnableMetrics}, Debug={EnableDebugLogging}");
    }
}

/// <summary>
/// Noise suppression levels available in WebRTC.
/// </summary>
public enum NoiseSuppressionLevel
{
    /// <summary>
    /// Conservative noise suppression - minimal processing.
    /// </summary>
    Conservative = 0,

    /// <summary>
    /// Moderate noise suppression - balanced processing.
    /// </summary>
    Moderate = 1,

    /// <summary>
    /// Aggressive noise suppression - maximum processing.
    /// </summary>
    Aggressive = 2
}