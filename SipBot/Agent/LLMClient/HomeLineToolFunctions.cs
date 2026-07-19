using System.ComponentModel;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Serilog;

namespace SipBot;

/// <summary>
/// Conversational tools for the HomeLine jail hub profile.
/// Side effects go through HomeLine Relay API; transfer numbers only from connect_contact.
/// </summary>
public class HomeLineToolFunctions : SimpleSemanticToolFunctions
{
    private readonly HomeLineRelayClient _relay;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = false };
    private string? _sessionToken;
    private string? _agentHints;

    public HomeLineToolFunctions(
        HomeLineRelayClient relay,
        Func<Task>? hangupAction = null,
        Func<string, Task<bool>>? transferCallAction = null)
        : base(hangupAction, transferCallAction, extensions: null)
    {
        _relay = relay ?? throw new ArgumentNullException(nameof(relay));
    }

    /// <summary>Clear per-call session when the SIP call ends.</summary>
    public void ResetSession()
    {
        _sessionToken = null;
        _agentHints = null;
    }

    [KernelFunction("authenticate_pin")]
    [Description(
        "Authenticate after the caller enters their 4-digit DTMF PIN and says their name. " +
        "Call this first before other HomeLine tools. Pass pin from keypad and spoken_name from speech. " +
        "Never ask them to speak the PIN aloud. Returns session context: contacts, credits, invite code, unread.")]
    public async Task<string> AuthenticatePinAsync(
        [Description("Exactly 4-digit PIN from the keypad (DTMF)")] string pin,
        [Description("Name they spoke after the PIN (first or full name on file)")] string spoken_name,
        [Description("Caller ANI if known, else empty")] string? ani = "")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(spoken_name))
                return JsonSerializer.Serialize(new
                {
                    error = "name_required",
                    detail = "Ask them to say their name after the PIN, then call again with spoken_name.",
                }, _json);

            var result = await _relay.AuthAsync(
                pin.Trim(),
                string.IsNullOrWhiteSpace(ani) ? null : ani,
                spoken_name.Trim()).ConfigureAwait(false);
            if (result is null)
                return JsonSerializer.Serialize(new { error = "Authentication failed", detail = "Relay unreachable or invalid response" }, _json);

            if (result.Value.TryGetProperty("detail", out _) && !result.Value.TryGetProperty("session_token", out _))
                return result.Value.GetRawText();

            if (!result.Value.TryGetProperty("session_token", out var tok))
                return JsonSerializer.Serialize(new
                {
                    error = "Could not verify PIN and name",
                    detail = "Ask them to re-enter the 4-digit PIN on the keypad and say their name again.",
                }, _json);

            _sessionToken = tok.GetString();
            _agentHints = result.Value.TryGetProperty("agent_greeting_hints", out var h) ? h.GetString() : null;
            Log.Information("HomeLine session authenticated (PIN + name).");
            return result.Value.GetRawText();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "authenticate_pin failed");
            return JsonSerializer.Serialize(new { error = "auth_failed", details = ex.Message }, _json);
        }
    }

    [KernelFunction("list_session_context")]
    [Description("Return cached greeting hints after authenticate_pin (contacts, credits, invite code).")]
    public Task<string> ListSessionContextAsync()
    {
        if (string.IsNullOrEmpty(_sessionToken))
            return Task.FromResult(JsonSerializer.Serialize(new { error = "Not authenticated. Call authenticate_pin first." }, _json));
        return Task.FromResult(JsonSerializer.Serialize(new
        {
            status = "ok",
            session_active = true,
            hints = _agentHints ?? "",
        }, _json));
    }

    [KernelFunction("create_message")]
    [Description("Send a message to a phone-book contact. Prefers email automatically (channel=auto). Use for 'tell Maria…' / 'message Dad'. Requires authenticate_pin first.")]
    public async Task<string> CreateMessageAsync(
        [Description("Message body to deliver")] string body_text,
        [Description("Contact id from auth response")] string? contact_id = "",
        [Description("auto (prefer email), email, or sms")] string channel = "auto",
        [Description("True if setup message that includes join link")] bool is_setup_message = false)
    {
        if (string.IsNullOrEmpty(_sessionToken))
            return JsonSerializer.Serialize(new { error = "Not authenticated" }, _json);
        try
        {
            var result = await _relay.CreateMessageAsync(
                _sessionToken,
                string.IsNullOrWhiteSpace(channel) ? "auto" : channel,
                body_text,
                string.IsNullOrWhiteSpace(contact_id) ? null : contact_id,
                destE164: null,
                destEmail: null,
                isSetupMessage: is_setup_message).ConfigureAwait(false);
            return result?.GetRawText() ?? JsonSerializer.Serialize(new { error = "No response" }, _json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "create_message failed");
            return JsonSerializer.Serialize(new { error = ex.Message }, _json);
        }
    }

    [KernelFunction("create_relay_message")]
    [Description("Legacy alias for create_message with channel=auto (email preferred). Prefer create_message.")]
    public Task<string> CreateRelayMessageAsync(
        [Description("Message body to deliver")] string body_text,
        [Description("Contact id from auth response")] string? contact_id = "",
        [Description("E.164 only if forcing SMS setup without contact")] string? dest_e164 = "",
        [Description("True if setup message with join link")] bool is_setup_message = false)
    {
        if (!string.IsNullOrWhiteSpace(dest_e164))
            return CreateSmsMessageAsync(body_text, contact_id, dest_e164, is_setup_message);
        return CreateMessageAsync(body_text, contact_id, "auto", is_setup_message);
    }

    [KernelFunction("create_sms_message")]
    [Description("Force SMS path (usually log-only until 10DLC). Prefer create_message / email.")]
    public async Task<string> CreateSmsMessageAsync(
        [Description("Message body")] string body_text,
        [Description("Contact id optional")] string? contact_id = "",
        [Description("Phone if no contact")] string? dest_e164 = "",
        [Description("Setup message flag")] bool is_setup_message = false)
    {
        if (string.IsNullOrEmpty(_sessionToken))
            return JsonSerializer.Serialize(new { error = "Not authenticated" }, _json);
        try
        {
            var result = await _relay.CreateMessageAsync(
                _sessionToken,
                "sms",
                body_text,
                string.IsNullOrWhiteSpace(contact_id) ? null : contact_id,
                string.IsNullOrWhiteSpace(dest_e164) ? null : dest_e164,
                destEmail: null,
                isSetupMessage: is_setup_message).ConfigureAwait(false);
            return result?.GetRawText() ?? JsonSerializer.Serialize(new { error = "No response" }, _json);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, _json);
        }
    }

    [KernelFunction("validate_email_address")]
    [Description("Normalize and validate a spoken email candidate before create_email_message.")]
    public async Task<string> ValidateEmailAddressAsync(
        [Description("Raw spoken or typed email candidate, e.g. maria dot lopez at gmail.com")] string candidate)
    {
        try
        {
            var result = await _relay.ValidateEmailAsync(candidate).ConfigureAwait(false);
            return result?.GetRawText() ?? JsonSerializer.Serialize(new { ok = false, reason = "No response" }, _json);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, reason = ex.Message }, _json);
        }
    }

    [KernelFunction("create_email_message")]
    [Description("PRIMARY delivery: send email after validate_email_address (or to contact email on file). Requires authenticate_pin.")]
    public async Task<string> CreateEmailMessageAsync(
        [Description("Message body")] string body_text,
        [Description("Validated email, or empty if using contact_id with email on file")] string? dest_email = "",
        [Description("Contact id if emailing someone in the phone book")] string? contact_id = "")
    {
        if (string.IsNullOrEmpty(_sessionToken))
            return JsonSerializer.Serialize(new { error = "Not authenticated" }, _json);
        try
        {
            var result = await _relay.CreateMessageAsync(
                _sessionToken,
                "email",
                body_text,
                string.IsNullOrWhiteSpace(contact_id) ? null : contact_id,
                destE164: null,
                destEmail: string.IsNullOrWhiteSpace(dest_email) ? null : dest_email,
                isSetupMessage: false).ConfigureAwait(false);
            return result?.GetRawText() ?? JsonSerializer.Serialize(new { error = "No response" }, _json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "create_email_message failed");
            return JsonSerializer.Serialize(new { error = ex.Message }, _json);
        }
    }

    [KernelFunction("get_inbox")]
    [Description("List recent messages and replies for the authenticated inmate.")]
    public async Task<string> GetInboxAsync(
        [Description("If true, mark inbound messages as read")] bool mark_read = true)
    {
        if (string.IsNullOrEmpty(_sessionToken))
            return JsonSerializer.Serialize(new { error = "Not authenticated" }, _json);
        try
        {
            var result = await _relay.GetInboxAsync(_sessionToken, mark_read).ConfigureAwait(false);
            return result?.GetRawText() ?? JsonSerializer.Serialize(new { items = Array.Empty<object>() }, _json);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, _json);
        }
    }

    [KernelFunction("connect_contact")]
    [Description("Authorize a live call to a phone-book contact. On success, call transfer_conversation with the dial_target returned.")]
    public async Task<string> ConnectContactAsync(
        [Description("Contact id from authenticate_pin contacts list")] string contact_id)
    {
        if (string.IsNullOrEmpty(_sessionToken))
            return JsonSerializer.Serialize(new { error = "Not authenticated" }, _json);
        try
        {
            var result = await _relay.ConnectAsync(_sessionToken, contact_id).ConfigureAwait(false);
            return result?.GetRawText() ?? JsonSerializer.Serialize(new { ok = false, reason = "No response" }, _json);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, reason = ex.Message }, _json);
        }
    }

    [KernelFunction("send_checkin")]
    [Description("Send an I'm OK check-in SMS to all opted-in contacts.")]
    public async Task<string> SendCheckinAsync()
    {
        if (string.IsNullOrEmpty(_sessionToken))
            return JsonSerializer.Serialize(new { error = "Not authenticated" }, _json);
        try
        {
            var result = await _relay.CheckinAsync(_sessionToken).ConfigureAwait(false);
            return result?.GetRawText() ?? JsonSerializer.Serialize(new { ok = false }, _json);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, detail = ex.Message }, _json);
        }
    }

    // Hide personal-assistant notification path for this profile by no-oping base-style usage:
    // agents should use create_relay_message instead.
    [KernelFunction("send_notification")]
    [Description("Deprecated on HomeLine — use create_relay_message or create_email_message.")]
    public override Task<string> SendNotificationAsync(
        string issue,
        string? location = "",
        string urgency = "medium",
        string? caller_name = "")
    {
        return Task.FromResult(JsonSerializer.Serialize(new
        {
            error = "Use create_relay_message or create_email_message on HomeLine.",
        }, _json));
    }
}
