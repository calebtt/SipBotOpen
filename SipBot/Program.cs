using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using SipBot;

public class Program
{
    private static async Task RunStreamingVoiceTest()
    {
        List<SipConfig> sipConfigs = BotSettings.Settings.SipSettings.Configs;
        string grokApiKey = BotSettings.Settings.LanguageModel.ApiKey;

        // Configure audio processing for excellent echo cancellation and noise suppression
        var audioConfig = AudioProcessingConfig.CreateDefault();
        audioConfig.LogConfiguration();

        // Initialize TTS service for streaming client
        await TtsProviderStreaming.InitializeAsync();
        var voices = TtsProviderStreaming.ListVoices();
        Log.Information("Available TTS voices:");
        voices.ToList().ForEach(voice => Log.Information($" - {voice}"));

        // generate welcome message
        await EnsureWelcomeMessageExistsAsync();

        Log.Information($" -> (Currently running only 1) -> Constructing {sipConfigs.Count} streaming SIP clients to listen for calls.");
        List<StreamingVoiceSipBotClient> clients = new();

        // Load sip account index
        int sipIndex = BotSettings.Settings.LanguageModel.ListenSipAccountIndex;
        if (sipIndex < 0 || sipIndex >= sipConfigs.Count)
            throw new InvalidOperationException($"Invalid ListenSipAccountIndex {sipIndex}; must be 0-{sipConfigs.Count - 1}.");

        var selectedConfig = sipConfigs[sipIndex];
        ArgumentNullException.ThrowIfNull(selectedConfig);

        // Construct with selected SIP config.
        var sipBotClient = new StreamingVoiceSipBotClient(selectedConfig);

        Log.Information("Listening for incoming calls with streaming STT, streaming TTS, and real-time interruption detection...");
        Log.Information("Press any key to exit...");

        Console.ReadKey();

        await sipBotClient.StopAsync();
        sipBotClient.Sip.Shutdown();
    }

    private static async Task EnsureWelcomeMessageExistsAsync()
    {
        // Assuming BotSettings.Settings.LanguageModel has been extended with WelcomeText and WelcomeFilePath properties
        // e.g., in the JSON: "WelcomeText": "Hello, you've reached Vend Partners...", "WelcomeFilePath": "welcome.wav"
        string welcomeText = BotSettings.Settings.LanguageModel.WelcomeMessage ?? "Hello, you've reached our AI assistant. How can I help you today?";
        string welcomeFilePath = BotSettings.Settings.LanguageModel.WelcomeFilePath;
        Log.Information("Generating welcome message...");
        await GenerateWelcomeMessageAsync(welcomeText, welcomeFilePath);
    }

    private static async Task GenerateWelcomeMessageAsync(string text, string filePath)
    {
        try
        {
            // Synthesize text to WAV stream
            using var wavStream = await TtsProviderStreaming.TextToSpeechAsync(text, outputAsWav: true);
            if (wavStream == null || wavStream.Length == 0)
            {
                throw new InvalidOperationException("TTS synthesis failed: No audio generated.");
            }

            // Ensure output directory exists
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write WAV directly to file (no encoding needed)
            await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            wavStream.Position = 0; // Reset stream position for reading
            await wavStream.CopyToAsync(fileStream).ConfigureAwait(false);

            Log.Information($"Welcome message generated successfully: {filePath}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to generate welcome message");
            throw; // Re-throw to halt startup if critical
        }
    }

    public static async Task Main(string[] args)
    {
        AddConsoleLogger();

        // Parse profile from args/env (modern: Use args parser or IOptions)
        string? profile = args.FirstOrDefault(a => a.StartsWith("--profile="))?.Split('=')[1]
            ?? Environment.GetEnvironmentVariable("BOT_PROFILE")
            ?? "vendpartners"; // Sensible default

        // Load once, async (no legacy string param)
        await BotSettings.LoadSettingsFromJsonAsync(profileName: profile, profilesDirectory: "profiles");

        // Use streaming voice client for real-time interruption detection
        await RunStreamingVoiceTest();
    }

    private static void AddConsoleLogger()
    {
        var serilogLogger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .MinimumLevel.Verbose() // Keep global Verbose for ONNX
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .MinimumLevel.Override("ONNXRuntime", LogEventLevel.Debug) // ONNX GPU details to Debug (less noise)
            .WriteTo.Console(
                restrictedToMinimumLevel: LogEventLevel.Information, // Console skips Verbose/Debug
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: "log.txt",
                restrictedToMinimumLevel: LogEventLevel.Debug, // File gets Debug for ONNX
                rollingInterval: RollingInterval.Infinite,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 100 * 1024 * 1024, // 100 MB
                retainedFileCountLimit: 5)
            .CreateLogger();

        var factory = new SerilogLoggerFactory(serilogLogger);
        SIPSorcery.LogFactory.Set(factory);
        Log.Logger = serilogLogger;

        Log.Information("Serilog configured—ONNX GPU logs in file (Debug level); console Info+.");
    }
}