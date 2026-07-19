using System.Text.Json;
using Serilog;

namespace SipBot;

/// <summary>
/// Dispatches Grok Realtime function calls onto <see cref="HomeLineToolFunctions"/> (Relay + hangup/transfer).
/// </summary>
public static class HomeLineGrokToolDispatch
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public static async Task<string> DispatchAsync(
        HomeLineToolFunctions tools,
        string name,
        Dictionary<string, JsonElement> args)
    {
        string Arg(string key)
        {
            if (!args.TryGetValue(key, out var el)) return "";
            return el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "") : el.ToString();
        }

        bool Flag(string key) =>
            args.TryGetValue(key, out var el) && (
                el.ValueKind == JsonValueKind.True
                || (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var b) && b));

        switch (name)
        {
            case "authenticate_pin":
                return await tools.AuthenticatePinAsync(Arg("pin"), Arg("spoken_name"), Arg("ani"))
                    .ConfigureAwait(false);

            case "get_dtmf_digits":
                return await tools.GetDtmfDigitsAsync().ConfigureAwait(false);

            case "clear_dtmf_digits":
                return await tools.ClearDtmfDigitsAsync().ConfigureAwait(false);

            case "list_session_context":
                return await tools.ListSessionContextAsync().ConfigureAwait(false);

            case "create_message":
            case "create_relay_message":
                return await tools.CreateMessageAsync(
                    Arg("body_text"),
                    Arg("contact_id"),
                    string.IsNullOrEmpty(Arg("channel")) ? "auto" : Arg("channel"),
                    Flag("is_setup_message")).ConfigureAwait(false);

            case "create_email_message":
                return await tools.CreateEmailMessageAsync(
                    Arg("body_text"),
                    Arg("dest_email"),
                    Arg("contact_id")).ConfigureAwait(false);

            case "create_sms_message":
                return await tools.CreateSmsMessageAsync(
                    Arg("body_text"),
                    Arg("contact_id"),
                    Arg("dest_e164"),
                    Flag("is_setup_message")).ConfigureAwait(false);

            case "validate_email_address":
                return await tools.ValidateEmailAddressAsync(Arg("candidate")).ConfigureAwait(false);

            case "get_inbox":
                return await tools.GetInboxAsync(Flag("mark_read")).ConfigureAwait(false);

            case "connect_contact":
                return await tools.ConnectContactAsync(Arg("contact_id")).ConfigureAwait(false);

            case "send_checkin":
                return await tools.SendCheckinAsync().ConfigureAwait(false);

            case "transfer_conversation":
            {
                var target = Arg("dial_target");
                if (string.IsNullOrEmpty(target)) target = Arg("extension");
                return await tools.TransferConversationAsync(target, Arg("reason")).ConfigureAwait(false);
            }

            case "end_conversation":
                await tools.EndConversationAsync(Arg("reason")).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { ok = true, end = true }, JsonOpts);

            default:
                Log.Warning("Unknown Grok tool: {Name}", name);
                return JsonSerializer.Serialize(new { ok = false, error = $"unknown tool {name}" }, JsonOpts);
        }
    }
}

/// <summary>Tool definitions for session.update (OpenAI/Grok Realtime shape).</summary>
public static class HomeLineGrokToolSchema
{
    public static object[] ToolsJsonElements() =>
    [
        Tool("authenticate_pin",
            "Verify 4-digit keypad PIN + spoken name. Leave pin empty to use last 4 DTMF digits.",
            Props(
                ("pin", "string", "4-digit PIN or empty for DTMF buffer"),
                ("spoken_name", "string", "Name they spoke"),
                ("ani", "string", "Caller ID if known")),
            required: ["spoken_name"]),

        Tool("get_dtmf_digits", "Return keypad digits collected this call for the PIN.", Props()),
        Tool("clear_dtmf_digits", "Clear keypad digit buffer.", Props()),

        Tool("create_message", "Send message to phone-book contact; auto prefers email.",
            Props(
                ("body_text", "string", "Message body"),
                ("contact_id", "string", "Contact id"),
                ("channel", "string", "auto|email|sms")),
            required: ["body_text"]),

        Tool("create_email_message", "Send email to dest_email or contact.",
            Props(
                ("body_text", "string", "Body"),
                ("dest_email", "string", "Email"),
                ("contact_id", "string", "Contact id")),
            required: ["body_text"]),

        Tool("validate_email_address", "Validate a spoken email before sending.",
            Props(("candidate", "string", "Email candidate")),
            required: ["candidate"]),

        Tool("get_inbox", "List recent messages / replies.",
            Props(("mark_read", "boolean", "Mark as read"))),

        Tool("connect_contact", "Authorize live connect; returns dial_target for transfer.",
            Props(("contact_id", "string", "Contact id")),
            required: ["contact_id"]),

        Tool("send_checkin", "Send I'm-OK check-in to allowed contacts.", Props()),

        Tool("transfer_conversation", "Blind-transfer the SIP call to dial_target from connect_contact.",
            Props(("dial_target", "string", "Number or SIP URI")),
            required: ["dial_target"]),

        Tool("end_conversation", "Caller is done; hang up.", Props()),
    ];

    private static object Tool(string name, string description, Dictionary<string, object> properties, string[]? required = null) =>
        new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required ?? Array.Empty<string>(),
            },
        };

    private static Dictionary<string, object> Props(params (string name, string type, string desc)[] fields)
    {
        var d = new Dictionary<string, object>();
        foreach (var (name, type, desc) in fields)
        {
            d[name] = new Dictionary<string, object>
            {
                ["type"] = type,
                ["description"] = desc,
            };
        }
        return d;
    }
}
