using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Serilog;

namespace SipBot;

/// <summary>
/// xAI Grok Voice Agent (Realtime WebSocket) session for HomeLine phone audio.
/// Speech understanding + reply audio run in the cloud — no local STT/TTS.
/// Protocol mirrors OpenAI Realtime-style events used by <c>homeline.voice.grok_session</c>.
/// </summary>
public sealed class GrokRealtimeSession : IAsyncDisposable
{
    public const int GrokSampleRateHz = 24_000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _voice;
    private readonly string _instructions;
    private readonly HomeLineToolFunctions _tools;
    private readonly Func<byte[], int, Task> _playPcm; // pcm16 LE mono + sampleRate
    private readonly Channel<string> _outbound = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _recvLoop;
    private Task? _sendLoop;
    private bool _endRequested;

    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public bool EndRequested => _endRequested;

    public GrokRealtimeSession(
        string apiKey,
        HomeLineToolFunctions tools,
        Func<byte[], int, Task> playPcm,
        string? instructions = null,
        string? model = null,
        string? voice = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _playPcm = playPcm ?? throw new ArgumentNullException(nameof(playPcm));
        _model = model
            ?? Environment.GetEnvironmentVariable("GROK_VOICE_MODEL")
            ?? "grok-voice-think-fast-1.0";
        _voice = voice
            ?? Environment.GetEnvironmentVariable("GROK_VOICE_NAME")
            ?? "ara";
        _instructions = instructions
            ?? BuildDefaultInstructions();
    }

    public static string BuildDefaultInstructions()
    {
        // Prefer profile text when available
        try
        {
            var lm = BotSettings.Settings.LanguageModel;
            var parts = new[]
            {
                lm.InstructionsText,
                lm.InstructionsAddendum,
                lm.ToolGuidance,
            }.Where(s => !string.IsNullOrWhiteSpace(s));
            var joined = string.Join("\n", parts);
            if (!string.IsNullOrWhiteSpace(joined))
                return joined;
        }
        catch
        {
            /* settings not loaded */
        }

        return """
            You are HomeLine, a call services AI phone assistant.
            Calls may be logged or recorded for any purpose. Not a confidential attorney line.
            Speak plainly; 1–2 short sentences per turn. No press-1 menus. No legal advice.
            Never ask for money or gift cards. Prefer email for messages.
            AUTH first: 4-digit keypad PIN (DTMF — never ask them to speak the PIN), then spoken name.
            Call authenticate_pin (leave pin empty to use collected DTMF digits) with spoken_name.
            Then greet using tool context. end_conversation only on explicit goodbye.
            """;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_ws != null)
            throw new InvalidOperationException("Session already connected");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        // Some gateways also accept OpenAI-style header
        try { _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1"); } catch { /* optional */ }

        var url = $"wss://api.x.ai/v1/realtime?model={Uri.EscapeDataString(_model)}";
        Log.Information("Connecting Grok Realtime → {Url}", url);
        await _ws.ConnectAsync(new Uri(url), _cts.Token).ConfigureAwait(false);

        _recvLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _sendLoop = Task.Run(() => SendLoopAsync(_cts.Token));

        // Wait for session.created (first message) — receive loop handles events
        await Task.Delay(50, _cts.Token).ConfigureAwait(false);
        await ConfigureSessionAsync().ConfigureAwait(false);
        Log.Information("Grok Realtime session configured (voice={Voice}, model={Model})", _voice, _model);
    }

    private async Task ConfigureSessionAsync()
    {
        var tools = HomeLineGrokToolSchema.ToolsJsonElements();
        var session = new Dictionary<string, object?>
        {
            ["instructions"] = _instructions,
            ["modalities"] = new[] { "audio", "text" },
            ["voice"] = _voice,
            ["input_audio_format"] = "pcm16",
            ["output_audio_format"] = "pcm16",
            ["turn_detection"] = new Dictionary<string, object?> { ["type"] = "server_vad" },
            ["input_audio_transcription"] = new Dictionary<string, object?> { ["model"] = "whisper-1" },
            ["tools"] = tools,
            ["tool_choice"] = "auto",
        };

        await EnqueueAsync(new Dictionary<string, object?>
        {
            ["type"] = "session.update",
            ["session"] = session,
        }).ConfigureAwait(false);
    }

    /// <summary>Ask the model to speak the call welcome (disclaimer + PIN prompt).</summary>
    public Task SpeakWelcomeAsync(string? welcomeText = null)
    {
        var text = welcomeText
            ?? BotSettings.Settings.LanguageModel.WelcomeMessage
            ?? "You've reached HomeLine. This call may be logged or recorded for any purpose and is not a private attorney line. Enter your four-digit PIN on the keypad, then say your name.";

        return EnqueueAsync(new Dictionary<string, object?>
        {
            ["type"] = "response.create",
            ["response"] = new Dictionary<string, object?>
            {
                ["modalities"] = new[] { "audio", "text" },
                ["instructions"] = $"Speak this greeting now, then wait for the caller: {text}",
            },
        });
    }

    /// <summary>Append telephony PCM (any rate) after resampling to 24 kHz mono pcm16.</summary>
    public async Task AppendInputPcmAsync(byte[] pcm, int sampleRateHz, CancellationToken ct = default)
    {
        if (!IsConnected || pcm.Length == 0)
            return;

        byte[] pcm24 = sampleRateHz == GrokSampleRateHz
            ? pcm
            : AudioAlgos.ResamplePcmWithNAudio(pcm, sampleRateHz, GrokSampleRateHz);

        var b64 = Convert.ToBase64String(pcm24);
        await EnqueueAsync(new Dictionary<string, object?>
        {
            ["type"] = "input_audio_buffer.append",
            ["audio"] = b64,
        }).ConfigureAwait(false);
    }

    /// <summary>Notify the model that keypad digits were collected (for PIN).</summary>
    public Task NotifyDtmfAsync(string digits)
    {
        return EnqueueAsync(new Dictionary<string, object?>
        {
            ["type"] = "conversation.item.create",
            ["item"] = new Dictionary<string, object?>
            {
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "input_text",
                        ["text"] = $"[keypad] Caller entered DTMF digits (use for PIN; do not read back): length={digits.Length}, last4={(digits.Length >= 4 ? digits[^4..] : digits)}",
                    },
                },
            },
        });
    }

    private async Task EnqueueAsync(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        await _outbound.Writer.WriteAsync(json).ConfigureAwait(false);
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var json in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_ws?.State != WebSocketState.Open)
                    break;
                var bytes = Encoding.UTF8.GetBytes(json);
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "Grok Realtime send loop failed");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[256 * 1024];
        var message = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Log.Information("Grok WebSocket closed by server: {Status}", result.CloseStatus);
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                await HandleEventAsync(json).ConfigureAwait(false);
                if (_endRequested)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "Grok Realtime receive loop failed");
        }
    }

    private async Task HandleEventAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeEl))
            return;
        var type = typeEl.GetString() ?? "";

        switch (type)
        {
            case "session.created":
            case "session.updated":
                Log.Debug("Grok event {Type}", type);
                break;

            case "error":
                Log.Warning("Grok error event: {Json}", json.Length > 500 ? json[..500] : json);
                break;

            case "response.output_audio.delta":
            case "response.audio.delta":
                if (root.TryGetProperty("delta", out var delta) && delta.GetString() is { Length: > 0 } b64)
                {
                    var pcm = Convert.FromBase64String(b64);
                    await _playPcm(pcm, GrokSampleRateHz).ConfigureAwait(false);
                }
                break;

            case "response.function_call_arguments.done":
                await HandleFunctionCallAsync(root).ConfigureAwait(false);
                break;

            case "input_audio_buffer.speech_started":
                Log.Debug("Grok VAD: speech started");
                break;

            case "input_audio_buffer.speech_stopped":
                Log.Debug("Grok VAD: speech stopped");
                break;

            default:
                // Keep noise down — many event types are informational
                if (type.Contains("error", StringComparison.OrdinalIgnoreCase))
                    Log.Warning("Grok event {Type}: {Snippet}", type, json.Length > 300 ? json[..300] : json);
                break;
        }
    }

    private async Task HandleFunctionCallAsync(JsonElement root)
    {
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var callId = root.TryGetProperty("call_id", out var c) ? c.GetString() ?? "" : "";
        var argsJson = root.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}";

        Log.Information("Grok tool call: {Name}", name);
        Dictionary<string, JsonElement>? args = null;
        try
        {
            args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson, JsonOpts);
        }
        catch
        {
            args = new Dictionary<string, JsonElement>();
        }

        string resultJson;
        try
        {
            resultJson = await HomeLineGrokToolDispatch.DispatchAsync(_tools, name, args ?? new())
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Tool {Name} failed", name);
            resultJson = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }

        if (resultJson.Contains("\"end\":true", StringComparison.OrdinalIgnoreCase)
            || resultJson.Contains("\"end\": true", StringComparison.OrdinalIgnoreCase))
        {
            _endRequested = true;
        }

        await EnqueueAsync(new Dictionary<string, object?>
        {
            ["type"] = "conversation.item.create",
            ["item"] = new Dictionary<string, object?>
            {
                ["type"] = "function_call_output",
                ["call_id"] = callId,
                ["output"] = resultJson,
            },
        }).ConfigureAwait(false);

        await EnqueueAsync(new Dictionary<string, object?>
        {
            ["type"] = "response.create",
        }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _outbound.Writer.TryComplete();
            _cts?.Cancel();
            if (_ws?.State == WebSocketState.Open)
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Grok session dispose");
        }
        finally
        {
            _ws?.Dispose();
            _cts?.Dispose();
        }

        if (_recvLoop != null) try { await _recvLoop.ConfigureAwait(false); } catch { /* ignore */ }
        if (_sendLoop != null) try { await _sendLoop.ConfigureAwait(false); } catch { /* ignore */ }
    }
}
