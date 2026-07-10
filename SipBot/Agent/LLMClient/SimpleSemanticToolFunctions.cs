using Microsoft.SemanticKernel;
using Serilog;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Transactions;

namespace SipBot;

public interface ISemanticToolFunctions
{
    Task<string> SendNotificationAsync(string issue, string? location = "", string urgency = "medium", string? caller_name = "");
    Task<string> TransferConversationAsync(string extension, string? reason = "");
    Task<string> EndConversationAsync(string? reason = "");
    Task<string> ScheduleFollowupAsync(string service_type = "callback", string? location = "", string? preferred_time = "");
}

/// <summary>
/// Simplified, modern implementation of ISemanticToolFunctions for Semantic Kernel integration.
/// Uses public methods with [KernelFunction] and [Description] attributes for direct import as plugins.
/// Implements logic inline; extensible via dependency injection for real services.
/// Best practices: Primary constructor, required members, immutable where possible, async throughout.
/// </summary>
public class SimpleSemanticToolFunctions
{
    private const int HangupDelaySeconds = 3;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly Func<Task>? _hangupAction;
    private readonly Func<string, Task<bool>>? _transferCallAction;
    private readonly Dictionary<string, string> _extensionMap = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _pendingHangupCts;
    //private readonly BulkVsSmsService? _smsService;

    /// <summary>
    /// Primary constructor: Inject actions for SIP and optional SMS service.
    /// </summary>
    public SimpleSemanticToolFunctions(
        Func<Task>? hangupAction = null,
        Func<string, Task<bool>>? transferCallAction = null,
        IEnumerable<ExtensionSchema>? extensions = null)
        //BulkVsSmsService? smsService = null)
    {
        _hangupAction = hangupAction ?? (() => Task.CompletedTask);
        _transferCallAction = transferCallAction ?? ((string extension) => Task.FromResult(false));
        //_smsService = smsService;

        if (extensions != null)
        {
            foreach (var ext in extensions)
            {
                if (string.IsNullOrWhiteSpace(ext.Name) || string.IsNullOrWhiteSpace(ext.Number))
                    continue;
                _extensionMap[ext.Name] = ext.Number;
            }
        }
    }

    [KernelFunction("send_notification")]
    [Description("Log a message or vending issue for follow-up (treat 'message' as issue for general calls).")]
    public virtual async Task<string> SendNotificationAsync(
            [Description("Message content or issue description")] string issue,
            [Description("Location or context (optional for messages)")] string? location = "",
            [Description("Urgency level (default 'medium')")] string urgency = "medium",
            [Description("Caller's name")] string? caller_name = "")
    {
        try
        {
            Log.Information("SendNotification: Location={Location}, Issue={Issue}, Urgency={Urgency}, Caller={CallerName}",
                location, issue, urgency, caller_name);

            // TODO see note on BulkVsSmsService usage
            // Delegate to SMS if available; fallback to log-only

            //return _smsService != null
            //    ? await _smsService.SendMessageAsync(issue, location, urgency, caller_name)
            //    : JsonSerializer.Serialize(new { status = "success", message = "Notification logged (SMS pending)." }, _jsonOptions);

            return JsonSerializer.Serialize(new { status = "success", message = "Notification logged (SMS service not configured)." }, _jsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Tool execution failed for send_notification: {Message}", ex.Message);
            return JsonSerializer.Serialize(new { error = "Execution failed", details = ex.Message }, _jsonOptions);
        }
    }

    [KernelFunction("transfer_conversation")]
    [Description("Transfer the conversation to an extension for escalation.")]
    public virtual async Task<string> TransferConversationAsync(
        [Description("Extension number or name (e.g., '102@slowcasting.com' or 'personal')")] string extension,
        [Description("Brief reason")] string? reason = "")
    {
        try
        {
            Log.Information("TransferConversation: Extension={Extension}, Reason={Reason}", extension, reason);

            if (string.IsNullOrWhiteSpace(extension))
            {
                Log.Error("TransferConversation: Invalid extension '{Extension}'", extension);
                return JsonSerializer.Serialize(new { error = "Transfer failed: Invalid extension." }, _jsonOptions);
            }

            // Resolve extension name to full number if mapped (e.g., "personal" -> "102@slowcasting.com")
            string actualExtension = extension;
            if (_extensionMap.TryGetValue(extension, out string? mappedNumber))
            {
                actualExtension = mappedNumber;
                Log.Information("Resolved extension name '{Original}' to full number '{Resolved}'", extension, actualExtension);
            }

            // Await so the LLM/tool result reflects real success/failure (was fire-and-forget).
            bool ok = await _transferCallAction!(actualExtension).ConfigureAwait(false);
            if (!ok)
            {
                return JsonSerializer.Serialize(new
                {
                    error = "Transfer failed",
                    details = $"Could not transfer to {actualExtension}."
                }, _jsonOptions);
            }

            return JsonSerializer.Serialize(new { status = "success", message = $"Transferring to extension {actualExtension}." }, _jsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Tool execution failed for transfer_conversation: {Message}", ex.Message);
            return JsonSerializer.Serialize(new { error = "Execution failed", details = ex.Message }, _jsonOptions);
        }
    }

    /// <summary>
    /// Cancels a delayed hangup from end_conversation (e.g. caller keeps talking).
    /// </summary>
    public void CancelPendingHangup()
    {
        var cts = Interlocked.Exchange(ref _pendingHangupCts, null);
        if (cts == null)
            return;
        try
        {
            cts.Cancel();
            Log.Information("Cancelled pending delayed hangup — caller still engaged.");
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cts.Dispose();
        }
    }

    [KernelFunction("end_conversation")]
    [Description("Hang up ONLY when the caller explicitly ends the call (goodbye, hang up, that's all) or is abusive. Do NOT use for declining a message, saying no, or changing topic — stay on the line and listen.")]
    public virtual async Task<string> EndConversationAsync(
        [Description("Reason (e.g., 'user said goodbye', 'user requested hang up')")] string? reason = "")
    {
        try
        {
            Log.Warning("EndConversation: Reason={Reason}", reason);

            // Let the model/TTS say a natural goodbye; do not force "Call ended (...)" into the pipeline.
            var toolResult = JsonSerializer.Serialize(new
            {
                status = "success",
                message = "Call will end shortly. Say a brief goodbye if you have not already."
            }, _jsonOptions);

            // Replace any prior delayed hangup; cancelable if the caller keeps speaking.
            CancelPendingHangup();
            var hangupCts = new CancellationTokenSource();
            _pendingHangupCts = hangupCts;
            var token = hangupCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(HangupDelaySeconds), token).ConfigureAwait(false);
                    await _hangupAction!().ConfigureAwait(false);
                    Log.Information("Delayed hangup executed: {Reason}", reason);
                }
                catch (OperationCanceledException)
                {
                    Log.Information("Delayed hangup cancelled before execute: {Reason}", reason);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Delayed hangup failed: {Reason}", reason);
                }
                finally
                {
                    Interlocked.CompareExchange(ref _pendingHangupCts, null, hangupCts);
                    hangupCts.Dispose();
                }
            }, CancellationToken.None);

            await Task.CompletedTask;
            return toolResult;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Tool execution failed for end_conversation: {Message}", ex.Message);
            return JsonSerializer.Serialize(new { error = "Execution failed", details = ex.Message }, _jsonOptions);
        }
    }

    [KernelFunction("schedule_followup")]
    [Description("Schedule a follow-up if mentioned in message (e.g., callback).")]
    public virtual async Task<string> ScheduleFollowupAsync(
        [Description("Type (default 'callback')")] string service_type = "callback",
        [Description("Context or location (optional)")] string? location = "",
        [Description("Preferred time (e.g., 'tomorrow')")] string? preferred_time = "")
    {
        try
        {
            Log.Information("ScheduleFollowup: Type={Type}, Location={Location}, Time={PreferredTime}", service_type, location, preferred_time);

            await Task.Delay(150); // Simulate async scheduling

            // TODO: Integrate with real scheduling system

            return JsonSerializer.Serialize(new { status = "success", message = $"Follow-up '{service_type}' scheduled for {location} at {preferred_time}." }, _jsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Tool execution failed for schedule_followup: {Message}", ex.Message);
            return JsonSerializer.Serialize(new { error = "Execution failed", details = ex.Message }, _jsonOptions);
        }
    }
}