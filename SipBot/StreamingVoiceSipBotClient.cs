using Serilog;
using SIPSorcery.SIP;
using System.Net;

namespace SipBot;

public static partial class Algos
{
    public static SIPTransport CreateSipTransport(int port)
    {
        StunHelper.SetupStun();
        var sipTransport = new SIPTransport();
        sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, port)));

        foreach (var channel in sipTransport.GetSIPChannels())
        {
            Log.Information($"SIP Transport Listening on SIP Channel: {channel.ID} - {channel.Port} - {channel.ListeningIPAddress}");
        }

        return sipTransport;
    }
}

/// <summary>
/// SIP Bot client with streaming STT for real-time interruption detection.
/// Can stop TTS playback when user starts speaking, improving responsiveness.
/// Typically a volume lowering filter is applied when VAD senses speech starting,
/// interruption occurs on full STT transcription if necessary.
/// Note: I don't know why, but the Grok phone app doesn't do that and it's super annoying.
/// </summary>
public class StreamingVoiceSipBotClient
{
    private readonly SttProviderStreaming _streamingSttClient;
    private readonly TtsStreamer _ttsProvider = new();
    private readonly SimpleSemanticToolFunctions _toolFunctions;
    private readonly LlmChat _llmClient;

    private string _welcomeMessageText = String.Empty;
    private byte[]? _welcomeMessagePcmu;

    private StreamingVoiceAudioEndPoint? _audioEndPoint = null;

    public string WelcomeMessagePath { get; } = BotSettings.Settings.LanguageModel.WelcomeFilePath;  // Immutable

    public SipClient Sip { get; private set; } = null!;  // Non-nullable post-init

    public StreamingVoiceSipBotClient(SipConfig sipConfig)
    {
        ArgumentNullException.ThrowIfNull(sipConfig);

        // TODO see SMS notes
        // Per-extension SMS (create if BulkVs present; no env override)
        //BulkVsSmsService? smsService = null;
        //if (selectedConfig.BulkVs != null)
        //{
        //    smsService = new BulkVsSmsService(selectedConfig.BulkVs);
        //    Log.Information("BulkVS SMS enabled for extension {Username}", selectedConfig.Username);
        //}

        _toolFunctions = new(
            async () => { Sip.Hangup(); await StopAsync(); },
            async (x) => { return await Sip.BlindTransferAsync(x, TimeSpan.FromSeconds(5)); }
        );
        _llmClient = new LlmChat(BotSettings.Settings.LanguageModel, _toolFunctions, Algos.BuildKernel(BotSettings.Settings.LanguageModel));

        // Construct pipeline
        string whisperModelUrl = BotSettings.Settings.SpeechToText.SttModelUrl;
        if (string.IsNullOrEmpty(whisperModelUrl))
        {
            Log.Error("No Whisper model URL configured.");
            throw new InvalidOperationException("No Whisper model URL configured in settings.");
        }

        _streamingSttClient = new SttProviderStreaming(whisperModelUrl);

        (Sip, _) = BuildSipClient(sipConfig);  // Use selected config

        Sip!.CallEnded += (obj) => { Sip_CallEnded(obj).Wait(); };
        Sip!.CallAnswer += (obj) => { Sip_CallAnswer(obj).Wait(); };

        _ = InitializeWelcomeMessageAsync();
    }

    /// <summary>
    /// Plays the pre-loaded welcome message audio via the endpoint (async; non-blocking).
    /// Guards against nulls/unready session; adds brief delay for stability.
    /// </summary>
    public async Task PlayWelcomeMessage()
    {
        if (_audioEndPoint == null)
        {
            Log.Warning("Cannot play welcome: Audio endpoint not initialized.");
            return;
        }

        if (_welcomeMessagePcmu == null || _welcomeMessagePcmu.Length == 0)
        {
            Log.Warning("Cannot play welcome: Message audio not loaded.");
            return;
        }

        try
        {
            // Brief delay for session readiness
            await Task.Delay(200);

            // Queue audio (PCMU; interruptible by VAD)
            _audioEndPoint.SendAudio(_welcomeMessagePcmu);
            Log.Information("Welcome message queued for playback ({Bytes} bytes).", _welcomeMessagePcmu.Length);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to queue welcome message audio");
        }
    }

    private async Task InitializeWelcomeMessageAsync()
    {
        try
        {
            var (pcmuMessageBytes, msgTxt) = await GetWelcomeMessageAsync(_streamingSttClient);
            _welcomeMessageText = msgTxt;
            _welcomeMessagePcmu = AudioAlgos.AppendBuffer(AudioAlgos.GeneratePcmuSilence(2, 8000), pcmuMessageBytes);

            // Clear history for fresh session (immutable state reset)
            _llmClient.ClearHistory();

            Log.Information("Welcome message initialized successfully");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize welcome message");
        }
    }

    /// <summary>
    /// Reads WAV, converts to PCMU, transcribes text (async; validated).
    /// </summary>
    private async Task<Tuple<byte[], string>> GetWelcomeMessageAsync(SttProviderStreaming streamingSttClient)
    {
        const string defaultWelcomeText = "Hello, thank you for calling. How can I assist you today?";

        // Load PCMU (early exit if missing)
        var pcmuBytes = AudioAlgos.ReadWelcomeWavBytesAsPcmu(WelcomeMessagePath);
        if (pcmuBytes.Length == 0)
        {
            Log.Warning("No welcome message found, using default string");
            return new Tuple<byte[], string>(Array.Empty<byte>(), defaultWelcomeText);
        }

        var welcomeInPcm16 = AudioAlgos.ReadWelcomeWavBytesAsPcm16kHz(WelcomeMessagePath);

        // TCS for event-driven transcription, timeout + unsubscribe
        var tcs = new TaskCompletionSource<string>();
        string? welcomeMessageText = null;

        void OnTranscriptionComplete(object? sender, string transcription)
        {
            welcomeMessageText = transcription;
            tcs.TrySetResult(transcription);
            streamingSttClient.TranscriptionComplete -= OnTranscriptionComplete;  // Unsubscribe to avoid leaks
        }

        streamingSttClient.TranscriptionComplete += OnTranscriptionComplete;

        try
        {
            // Process chunk
            await streamingSttClient.ProcessAudioChunkAsync(new MemoryStream(welcomeInPcm16));

            // Wait with timeout, prevents hang
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await tcs.Task.WaitAsync(cts.Token);
            return new Tuple<byte[], string>(pcmuBytes, welcomeMessageText ?? "");
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Welcome transcription timed out");
            return new Tuple<byte[], string>(pcmuBytes, defaultWelcomeText);
        }
        finally
        {
            streamingSttClient.TranscriptionComplete -= OnTranscriptionComplete;  // Ensure cleanup
        }
    }

    private (SipClient, SIPTransport) BuildSipClient(SipConfig sipSettings)
    {
        ArgumentNullException.ThrowIfNull(sipSettings);

        var transport = Algos.CreateSipTransport(sipSettings.Port);
        var sipClient = new SipClient(transport, sipSettings);

        // Event handlers, log only
        sipClient.StatusMessage += (client, message) => Log.Debug($"Status: {message}");
        sipClient.CallAnswer += (client) => Log.Information("Call answered!");
        sipClient.CallEnded += (client) => Log.Information("Call ended.");
        sipClient.RemotePutOnHold += (client) => Log.Information("Remote party put us on hold.");
        sipClient.RemoteTookOffHold += (client) => Log.Information("Remote party took us off hold.");

        transport = GetSipTransportWithHandlers(transport, sipClient);
        sipClient.StartRegistration();

        return (sipClient, transport);
    }

    private SIPTransport GetSipTransportWithHandlers(SIPTransport sipTransport, SipClient sipClient)
    {
        ArgumentNullException.ThrowIfNull(sipTransport);
        ArgumentNullException.ThrowIfNull(sipClient);

        sipTransport.SIPTransportRequestReceived += async (localSIPEndPoint, remoteEndPoint, sipRequest) =>
        {
            if (sipRequest.Method == SIPMethodsEnum.OPTIONS)
            {
                var optionsResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
                await sipTransport.SendResponseAsync(optionsResponse);
                Log.Debug("Responded to SIP OPTIONS ping.");
            }
            else if (sipRequest.Method == SIPMethodsEnum.INVITE)
            {
                Log.Information($"Incoming call from {sipRequest.Header.From.FriendlyDescription()}");
                sipClient.Accept(sipRequest);

                // Reset LLM state for new call
                _llmClient.ClearHistory();
                _llmClient.AddAssistantMessage(_welcomeMessageText);  // Add welcome as assistant message

                var eps = new StreamingVoiceAudioEndPoint(_streamingSttClient, _llmClient, _ttsProvider);
                await eps.InitializeAsync();
                _audioEndPoint = eps;

                // Wait for welcome, bounded retry
                if (_welcomeMessagePcmu == null)
                {
                    Log.Information("Waiting for welcome message...");
                    const int maxWait = 50;  // 5s
                    for (int waitCount = 0; waitCount < maxWait && _welcomeMessagePcmu == null; waitCount++)
                    {
                        await Task.Delay(100);
                    }
                    Log.Information(_welcomeMessagePcmu != null ? "Welcome ready" : "Welcome not ready; proceeding without");
                }

                var answered = await sipClient.Answer(eps, eps);
                Log.Information(answered ? "Call answered successfully." : "Failed to answer call.");

                _ = PlayWelcomeMessage();
            }
            else if (sipRequest.Method == SIPMethodsEnum.BYE)
            {
                _audioEndPoint?.Dispose();
                Log.Information("Audio endpoint disposed.");
            }
        };

        return sipTransport;
    }

    /// <summary>
    /// Stops client
    /// </summary>
    public async Task StopAsync()
    {
        Log.Information("Stopping streaming voice SIP bot client...");
        if (_audioEndPoint != null)
        {
            await _audioEndPoint.ShutdownAsync();
            _audioEndPoint.Dispose();
        }
        _streamingSttClient?.Dispose();
        Log.Information("Streaming voice SIP bot client stopped successfully.");
    }

    // Event handlers
    private async Task Sip_CallEnded(SipClient obj) => Log.Information("Call ended.");
    private async Task Sip_CallAnswer(SipClient obj) => Log.Information("Call answered!");
}