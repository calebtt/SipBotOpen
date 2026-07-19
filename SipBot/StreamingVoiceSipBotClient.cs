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
    private readonly HomeLineToolFunctions? _homeLineTools;
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

        string sipServer = sipConfig.Server;
        // Hang up the active call only — never StopAsync() here. StopAsync disposes STT/TTS
        // and tears down registration, so the first end_conversation would kill the whole bot.
        Func<Task> hangup = () =>
        {
            Log.Information("Hangup action: ending active call only (bot keeps listening).");
            Sip.Hangup();
            return Task.CompletedTask;
        };
        Func<string, Task<bool>> transfer = async (target) =>
        {
            string uri = NormalizeTransferTarget(target, sipServer);
            Log.Information("Blind transfer requested: raw='{Raw}' uri='{Uri}'", target, uri);
            return await Sip.BlindTransferAsync(uri, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        };

        var ext = BotSettings.Settings.ProfileExtension;
        if (!string.IsNullOrWhiteSpace(ext.HomelineBaseUrl))
        {
            var token = string.IsNullOrWhiteSpace(ext.HomelineServiceToken)
                ? Environment.GetEnvironmentVariable("HOMELINE_SERVICE_TOKEN") ?? ""
                : ext.HomelineServiceToken;
            var relay = new HomeLineRelayClient(ext.HomelineBaseUrl, token);
            var homeLineTools = new HomeLineToolFunctions(relay, hangup, transfer);
            _toolFunctions = homeLineTools;
            _homeLineTools = homeLineTools;
            Log.Information("HomeLine relay tools enabled → {BaseUrl}", ext.HomelineBaseUrl);
        }
        else
        {
            _homeLineTools = null;
            _toolFunctions = new SimpleSemanticToolFunctions(
                hangup,
                transfer,
                BotSettings.Settings.LanguageModel.Extensions);
        }

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

        // Prefer SipClient events over blocking .Wait() on handlers (deadlock risk).
        Sip.CallEnded += OnCallEnded;
        Sip.CallAnswer += OnCallAnswered;
        // Fire-and-forget Task — avoids async void event handlers (VSTHRD100).
        Sip.IncomingCall += (client, invite) =>
        {
            _ = HandleIncomingCallAsync(client, invite);
        };

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

    /// <summary>
    /// Accepts "personal" map results like "102@host", bare "102", or full "sip:102@host".
    /// BlindTransferAsync requires a parseable SIP URI.
    /// </summary>
    public static string NormalizeTransferTarget(string target, string defaultServer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        string t = target.Trim();
        if (t.StartsWith("sip:", StringComparison.OrdinalIgnoreCase))
            return t;
        if (t.Contains('@', StringComparison.Ordinal))
            return "sip:" + t;
        string host = string.IsNullOrWhiteSpace(defaultServer) ? "localhost" : defaultServer.Trim();
        // Strip accidental sip: on server if present
        if (host.StartsWith("sip:", StringComparison.OrdinalIgnoreCase))
            host = host[4..];
        return $"sip:{t}@{host}";
    }

    private (SipClient, SIPTransport) BuildSipClient(SipConfig sipSettings)
    {
        ArgumentNullException.ThrowIfNull(sipSettings);

        var transport = Algos.CreateSipTransport(sipSettings.Port);
        var sipClient = new SipClient(transport, sipSettings);

        // Event handlers (answer/end/incoming are wired on Sip above)
        sipClient.StatusMessage += (client, message) =>
        {
            // SipClient logs registration success at Debug; surface it here for ops visibility.
            if (message.Contains("Registration successful", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Registration failed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Registration temporary", StringComparison.OrdinalIgnoreCase))
                Log.Information("SIP: {Message}", message);
            else
                Log.Debug("SIP status: {Message}", message);
        };
        sipClient.RegistrationStatusChanged += client =>
            Log.Information("SIP registration state: registered={Registered} lastOk={LastOk:u}",
                client.IsRegistered, client.LastSuccessfulRegistration);
        sipClient.RemotePutOnHold += (client) => Log.Information("Remote party put us on hold.");
        sipClient.RemoteTookOffHold += (client) => Log.Information("Remote party took us off hold.");
        sipClient.ErrorOccurred += (client, ex) => Log.Error(ex, "SIP client error");

        // OPTIONS qualify is answered inside SipClient (SipBotLib) — do not double-reply here.
        sipClient.StartRegistration();

        return (sipClient, transport);
    }

    /// <summary>
    /// Handles INVITE via SipClient.IncomingCall (same path as LiveCallTest).
    /// </summary>
    private async Task HandleIncomingCallAsync(SipClient sipClient, SIPRequest sipRequest)
    {
        try
        {
            Log.Information($"Incoming call from {sipRequest.Header.From.FriendlyDescription()}");
            sipClient.Accept(sipRequest);

            // Reset LLM state for new call
            _llmClient.ClearHistory();
            if (!string.IsNullOrWhiteSpace(_welcomeMessageText))
                _llmClient.AddAssistantMessage(_welcomeMessageText);

            // Tear down previous call endpoint if still around (back-to-back calls)
            if (_audioEndPoint != null)
            {
                try
                {
                    await _audioEndPoint.ShutdownAsync().ConfigureAwait(false);
                    _audioEndPoint.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Error disposing previous audio endpoint before new call");
                }
                _audioEndPoint = null;
            }

            var eps = new StreamingVoiceAudioEndPoint(
                _streamingSttClient,
                _llmClient,
                _ttsProvider,
                onUserEngaged: _toolFunctions.CancelPendingHangup);
            await eps.InitializeAsync().ConfigureAwait(false);
            _audioEndPoint = eps;

            // Wait for welcome, bounded retry
            if (_welcomeMessagePcmu == null)
            {
                Log.Information("Waiting for welcome message...");
                const int maxWait = 50;  // 5s
                for (int waitCount = 0; waitCount < maxWait && _welcomeMessagePcmu == null; waitCount++)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }
                Log.Information(_welcomeMessagePcmu != null ? "Welcome ready" : "Welcome not ready; proceeding without");
            }

            var answered = await sipClient.Answer(eps, eps).ConfigureAwait(false);
            Log.Information(answered ? "Call answered successfully." : "Failed to answer call.");

            _ = PlayWelcomeMessage();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to handle incoming call");
        }
    }

    private void OnCallEnded(SipClient _)
    {
        Log.Information("Call ended.");
        _homeLineTools?.ResetSession();
        try
        {
            _audioEndPoint?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error disposing audio endpoint on call end");
        }
        finally
        {
            _audioEndPoint = null;
        }
    }

    private void OnCallAnswered(SipClient _) => Log.Information("Call answered!");

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
            _audioEndPoint = null;
        }
        _ttsProvider.Dispose();
        _streamingSttClient?.Dispose();
        Log.Information("Streaming voice SIP bot client stopped successfully.");
    }
}