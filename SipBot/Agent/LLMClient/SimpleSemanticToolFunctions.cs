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
    //private readonly BulkVsSmsService? _smsService;

    /// <summary>
    /// Primary constructor: Inject actions for SIP and optional SMS service.
    /// </summary>
    public SimpleSemanticToolFunctions(
        Func<Task>? hangupAction = null,
        Func<string, Task<bool>>? transferCallAction = null)
        //BulkVsSmsService? smsService = null)
    {
        _hangupAction = hangupAction ?? (() => Task.CompletedTask);
        _transferCallAction = transferCallAction ?? ((string extension) => Task.FromResult(false));
        //_smsService = smsService;
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

            // Fire-and-forget transfer (non-awaited per original design); action now takes full extension
            _ = _transferCallAction!(actualExtension);

            return JsonSerializer.Serialize(new { status = "success", message = $"Transferring to extension {actualExtension}." }, _jsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Tool execution failed for transfer_conversation: {Message}", ex.Message);
            return JsonSerializer.Serialize(new { error = "Execution failed", details = ex.Message }, _jsonOptions);
        }
    }

    [KernelFunction("end_conversation")]
    [Description("Gracefully end the call after message or resolution.")]
    public virtual async Task<string> EndConversationAsync(
        [Description("Reason (e.g., 'message taken', 'resolved')")] string? reason = "")
    {
        try
        {
            Log.Warning("EndConversation: Reason={Reason}", reason);

            // Immediate TTS response
            var ttsMessage = $"Call ended ({reason}). Goodbye!";
            var toolResult = JsonSerializer.Serialize(new { status = "success", message = ttsMessage }, _jsonOptions);

            // Delayed hangup (fire-and-forget, background task)
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(HangupDelaySeconds));
                    await _hangupAction!();
                    Log.Information("Delayed hangup executed: {Reason}", reason);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Delayed hangup failed: {Reason}", reason);
                }
            });

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