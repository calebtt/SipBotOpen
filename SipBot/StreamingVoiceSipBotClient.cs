using Serilog;
using SIPSorcery.SIP;
using System.Net;

namespace SipBot;

public static partial class Algos
{
    /// <param name="port">
    /// Local UDP bind port. Use 0 for ephemeral. Do NOT pass the PBX server port (5060) when
    /// co-located with Asterisk — that port is already taken.
    /// </param>
    public static SIPTransport CreateSipTransport(int port = 0)
    {
        StunHelper.SetupStun();
        var sipTransport = new SIPTransport();
        // Prefer env SIP_LOCAL_PORT; default ephemeral so we can run beside Asterisk on the same host.
        if (int.TryParse(Environment.GetEnvironmentVariable("SIP_LOCAL_PORT"), out var envPort) && envPort >= 0)
            port = envPort;
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
    /// <summary>
    /// HomeLine default: Grok Voice Realtime (cloud speech). Local Whisper/Kokoro only for personal profile
    /// or when HOMELINE_SPEECH=local.
    /// </summary>
    public static bool UseGrokVoice
    {
        get
        {
            var mode = (Environment.GetEnvironmentVariable("HOMELINE_SPEECH")
                ?? Environment.GetEnvironmentVariable("SIPBOT_SPEECH")
                ?? "").Trim().ToLowerInvariant();
            if (mode is "local" or "whisper" or "kokoro")
                return false;
            if (mode is "grok" or "realtime" or "voice")
                return true;
            // Default: Grok when HomeLine relay is configured
            return !string.IsNullOrWhiteSpace(BotSettings.Settings.ProfileExtension.HomelineBaseUrl);
        }
    }

    private readonly SttProviderStreaming? _streamingSttClient;
    private readonly TtsStreamer? _ttsProvider;
    private readonly SimpleSemanticToolFunctions _toolFunctions;
    private readonly HomeLineToolFunctions? _homeLineTools;
    private readonly LlmChat? _llmClient;
    private readonly bool _useGrokVoice;

    private string _welcomeMessageText = String.Empty;
    private byte[]? _welcomeMessagePcmu;

    private BaseAudioEndPoint? _audioEndPoint = null;
    private GrokVoiceAudioEndPoint? _grokEndPoint = null;

    public string WelcomeMessagePath { get; } = BotSettings.Settings.LanguageModel.WelcomeFilePath;  // Immutable

    public SipClient Sip { get; private set; } = null!;  // Non-nullable post-init

    public StreamingVoiceSipBotClient(SipConfig sipConfig)
    {
        ArgumentNullException.ThrowIfNull(sipConfig);

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

        _useGrokVoice = UseGrokVoice;
        if (_useGrokVoice)
        {
            // Cloud speech path — no Whisper, no Kokoro, no local LLM chat loop
            _streamingSttClient = null;
            _ttsProvider = null;
            _llmClient = null;
            Log.Information("Speech path: Grok Voice Realtime (cloud). Local STT/TTS disabled.");
        }
        else
        {
            _ttsProvider = new TtsStreamer();
            _llmClient = new LlmChat(BotSettings.Settings.LanguageModel, _toolFunctions, Algos.BuildKernel(BotSettings.Settings.LanguageModel));

            string whisperModelUrl = BotSettings.Settings.SpeechToText.SttModelUrl;
            if (string.IsNullOrEmpty(whisperModelUrl))
            {
                Log.Error("No Whisper model URL configured.");
                throw new InvalidOperationException("No Whisper model URL configured in settings.");
            }

            _streamingSttClient = new SttProviderStreaming(whisperModelUrl);
            Log.Information("Speech path: local Whisper STT + Kokoro TTS + LLM tools.");
        }

        (Sip, _) = BuildSipClient(sipConfig);  // Use selected config

        // Prefer SipClient events over blocking .Wait() on handlers (deadlock risk).
        Sip.CallEnded += OnCallEnded;
        Sip.CallAnswer += OnCallAnswered;
        Sip.DtmfDigitReceived += OnDtmfDigit;
        // Fire-and-forget Task — avoids async void event handlers (VSTHRD100).
        Sip.IncomingCall += (client, invite) =>
        {
            _ = HandleIncomingCallAsync(client, invite);
        };

        if (!_useGrokVoice)
            _ = InitializeWelcomeMessageAsync();
    }

    private void OnDtmfDigit(SipClient _, char digit)
    {
        if (_grokEndPoint != null)
            _grokEndPoint.OnDtmfDigit(digit);
        else
            _homeLineTools?.AppendDtmfDigit(digit);
    }

    /// <summary>
    /// Plays the pre-loaded welcome message audio via the endpoint (async; non-blocking).
    /// Guards against nulls/unready session; adds brief delay for stability.
    /// </summary>
    public async Task PlayWelcomeMessage()
    {
        if (_audioEndPoint is not StreamingVoiceAudioEndPoint streaming)
        {
            // Grok path speaks welcome via Realtime API
            return;
        }

        if (_welcomeMessagePcmu == null || _welcomeMessagePcmu.Length == 0)
        {
            Log.Warning("Cannot play welcome: Message audio not loaded.");
            return;
        }

        try
        {
            await Task.Delay(200);
            streaming.SendAudio(_welcomeMessagePcmu);
            Log.Information("Welcome message queued for playback ({Bytes} bytes).", _welcomeMessagePcmu.Length);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to queue welcome message audio");
        }
    }

    private async Task InitializeWelcomeMessageAsync()
    {
        if (_streamingSttClient is null || _llmClient is null)
            return;
        try
        {
            var (pcmuMessageBytes, msgTxt) = await GetWelcomeMessageAsync(_streamingSttClient);
            _welcomeMessageText = msgTxt;
            _welcomeMessagePcmu = AudioAlgos.AppendBuffer(AudioAlgos.GeneratePcmuSilence(2, 8000), pcmuMessageBytes);
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

        // Local bind: ephemeral (0). sipSettings.Port is the *server* port for REGISTER, not local bind.
        var transport = Algos.CreateSipTransport(0);
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

            _homeLineTools?.ResetSession();
            _homeLineTools?.SetCallerAni(ExtractAni(sipRequest));
            _llmClient?.ClearHistory();
            if (_llmClient != null && !string.IsNullOrWhiteSpace(_welcomeMessageText))
                _llmClient.AddAssistantMessage(_welcomeMessageText);

            // Tear down previous call endpoint if still around (back-to-back calls)
            await DisposeAudioEndpointAsync().ConfigureAwait(false);

            BaseAudioEndPoint eps;
            if (_useGrokVoice)
            {
                if (_homeLineTools is null)
                    throw new InvalidOperationException("Grok Voice path requires HomeLine tools (HomelineBaseUrl).");

                var apiKey = BotSettings.Settings.LanguageModel.ApiKey;
                if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "YOUR_API_KEY_HERE")
                    throw new InvalidOperationException("Set XAI_API_KEY / LanguageModel.ApiKey for Grok Voice.");

                var grok = new GrokVoiceAudioEndPoint(_homeLineTools, apiKey);
                // Connect Grok WS before answer so welcome can start as soon as media is up
                await grok.InitializeAsync().ConfigureAwait(false);
                _grokEndPoint = grok;
                eps = grok;
            }
            else
            {
                var local = new StreamingVoiceAudioEndPoint(
                    _streamingSttClient!,
                    _llmClient!,
                    _ttsProvider!,
                    onUserEngaged: _toolFunctions.CancelPendingHangup);
                await local.InitializeAsync().ConfigureAwait(false);
                eps = local;
            }

            _audioEndPoint = eps;

            // Local path: wait for pre-rendered welcome WAV
            if (!_useGrokVoice && _welcomeMessagePcmu == null)
            {
                Log.Information("Waiting for welcome message...");
                const int maxWait = 50;  // 5s
                for (int waitCount = 0; waitCount < maxWait && _welcomeMessagePcmu == null; waitCount++)
                    await Task.Delay(100).ConfigureAwait(false);
                Log.Information(_welcomeMessagePcmu != null ? "Welcome ready" : "Welcome not ready; proceeding without");
            }

            var answered = await sipClient.Answer(eps, eps).ConfigureAwait(false);
            Log.Information(answered ? "Call answered successfully." : "Failed to answer call.");

            if (!_useGrokVoice)
                _ = PlayWelcomeMessage();
            // Grok path: SpeakWelcomeAsync already fired inside GrokVoiceAudioEndPoint.InitializeAsync
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to handle incoming call");
        }
    }

    private async Task DisposeAudioEndpointAsync()
    {
        var ep = _audioEndPoint;
        _audioEndPoint = null;
        _grokEndPoint = null;
        if (ep == null) return;
        try
        {
            await ep.ShutdownAsync().ConfigureAwait(false);
            if (ep is IDisposable d)
                d.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error disposing previous audio endpoint");
        }
    }

    private void OnCallEnded(SipClient client)
    {
        Log.Information("Call ended.");
        _homeLineTools?.ResetSession();
        _ = DisposeAudioEndpointAsync();
    }

    private void OnCallAnswered(SipClient client) => Log.Information("Call answered!");

    /// <summary>Best-effort caller ID from SIP From user part.</summary>
    private static string? ExtractAni(SIPRequest req)
    {
        try
        {
            var user = req.Header.From?.FromURI?.User;
            if (string.IsNullOrWhiteSpace(user))
                return null;
            // Prefer E.164-ish digit strings
            var digits = new string(user.Where(c => char.IsDigit(c) || c == '+').ToArray());
            return digits.Length >= 3 ? digits : user.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Stops client
    /// </summary>
    public async Task StopAsync()
    {
        Log.Information("Stopping streaming voice SIP bot client...");
        await DisposeAudioEndpointAsync().ConfigureAwait(false);
        _ttsProvider?.Dispose();
        _streamingSttClient?.Dispose();
        Log.Information("Streaming voice SIP bot client stopped successfully.");
    }
}