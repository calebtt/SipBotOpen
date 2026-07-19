using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace SipBot;

/// <summary>
/// HTTP client for the HomeLine Relay API (PIN sessions, messages, connect auth).
/// </summary>
public sealed class HomeLineRelayClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger _log;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public HomeLineRelayClient(string baseUrl, string serviceToken, ILogger? log = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("HomeLine base URL required.", nameof(baseUrl));
        if (string.IsNullOrWhiteSpace(serviceToken))
            throw new ArgumentException("HomeLine service token required.", nameof(serviceToken));

        _log = log ?? Log.Logger;
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<JsonElement?> AuthAsync(
        string pin,
        string? ani,
        string? spokenName = null,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            pin,
            ani,
            spoken_name = spokenName,
        }, JsonOpts);
        return await PostJsonAsync("v1/session/auth", payload, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement?> CreateMessageAsync(
        string sessionToken,
        string channel,
        string bodyText,
        string? contactId = null,
        string? destE164 = null,
        string? destEmail = null,
        bool isSetupMessage = false,
        string? audioPath = null,
        CancellationToken ct = default)
    {
        // channel: auto (prefer email) | email | sms
        var payload = JsonSerializer.Serialize(new
        {
            session_token = sessionToken,
            channel = string.IsNullOrWhiteSpace(channel) ? "auto" : channel,
            body_text = bodyText,
            contact_id = contactId,
            dest_e164 = destE164,
            dest_email = destEmail,
            is_setup_message = isSetupMessage,
            audio_path = audioPath,
        }, JsonOpts);
        return await PostJsonAsync("v1/messages", payload, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement?> ValidateEmailAsync(string candidate, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { candidate }, JsonOpts);
        return await PostJsonAsync("v1/emails/validate", payload, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement?> GetInboxAsync(string sessionToken, bool markRead = false, CancellationToken ct = default)
    {
        var url = $"v1/inbox?session_token={Uri.EscapeDataString(sessionToken)}&mark_read={markRead.ToString().ToLowerInvariant()}";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _log.Warning("HomeLine inbox failed {Status}: {Body}", resp.StatusCode, body);
            return null;
        }
        return JsonSerializer.Deserialize<JsonElement>(body, JsonOpts);
    }

    public async Task<JsonElement?> ConnectAsync(string sessionToken, string contactId, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { session_token = sessionToken, contact_id = contactId }, JsonOpts);
        return await PostJsonAsync("v1/connect", payload, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement?> CheckinAsync(string sessionToken, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { session_token = sessionToken }, JsonOpts);
        return await PostJsonAsync("v1/checkin", payload, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement?> PostJsonAsync(string path, string json, CancellationToken ct)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(path, content, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _log.Warning("HomeLine POST {Path} failed {Status}: {Body}", path, resp.StatusCode, body);
            try
            {
                return JsonSerializer.Deserialize<JsonElement>(body, JsonOpts);
            }
            catch
            {
                return null;
            }
        }
        return JsonSerializer.Deserialize<JsonElement>(body, JsonOpts);
    }

    public void Dispose() => _http.Dispose();
}
