using Polly;
using Polly.Extensions.Http;
using Polly.Retry;
using Serilog;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SipBot;

public static partial class Algos
{
    public static string TruncateMessage(string message, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (maxLength <= 0) throw new ArgumentOutOfRangeException(nameof(maxLength));
        if (message.Length <= maxLength) return message;
        return message[..maxLength] + "...";
    }

    public static string NormalizeE164(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) throw new ArgumentException("Invalid phone number.", nameof(phone));
        var normalized = phone.Trim().Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "");
        return normalized.StartsWith("+") ? normalized : $"+1{normalized.TrimStart('1')}";
    }

    // BulkVS-specific normalizer—E.164 internally, plain 11-digit for API (no '+') (schema examples confirm no '+')
    public static string BulkVsNormalize(string phone)
    {
        var e164 = NormalizeE164(phone);
        if (!e164.StartsWith("+1") || e164.Length != 12)  // US/Canada only; +1 + 10 digits
            throw new ArgumentException("Invalid US/Canada phone number for BulkVS.", nameof(phone));
        return e164[1..];  // Strip '+'
    }
}

/// <summary>
/// TODO: automated SMS requires campaign setup in BulkVS portal for regulatory compliance.
/// For most applications, an SMS provider or email-to-SMS gateway may be more appropriate.
/// </summary>
public class BulkVsSmsService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly BulkVsConfig _config;
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;
    private readonly int _requestTimeoutSeconds;

    private const string MessageSendEndpoint = "messageSend";

    public BulkVsSmsService(BulkVsConfig config, ILogger logger, HttpClient? httpClient = null, int? requestTimeoutSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(config.Username) || string.IsNullOrWhiteSpace(config.Password))
            throw new ArgumentException("Username and Password required for Basic Auth.", nameof(config));
        if (string.IsNullOrWhiteSpace(config.FromNumber))
            throw new ArgumentException("FromNumber required (sender in E.164).", nameof(config));

        _config = config;
        _logger = logger;
        _requestTimeoutSeconds = requestTimeoutSeconds ?? 30;
        var baseUrl = config.ApiBaseUrl ?? "https://portal.bulkvs.com/api/v1.0/";
        _httpClient = httpClient ?? new HttpClient { BaseAddress = new Uri(baseUrl) };

        // Basic Auth (email as username, secret as password)
        var authBytes = Encoding.UTF8.GetBytes($"{config.Username}:{config.Password}");
        var base64Auth = Convert.ToBase64String(authBytes);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", base64Auth);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Polly retry policy (unchanged)
        _retryPolicy = HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(r => r.StatusCode == HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(
                retryCount: config.MaxRetries ?? 3,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                onRetry: (outcome, timespan, retryCount, ctx) =>
                {
                    var toNumber = ctx?.GetValueOrDefault("ToNumber")?.ToString() ?? "unknown";
                    var retryAfter = outcome.Result?.Headers.RetryAfter?.Delta?.TotalSeconds ?? timespan.TotalSeconds;
                    _logger.Warning("Retry {RetryCount}/{MaxRetries} after {DelayMs}ms (Retry-After: {RetryAfter}s): {StatusCode} for {ToNumber}",
                        retryCount, config.MaxRetries, timespan.TotalMilliseconds, retryAfter, outcome.Result?.StatusCode, toNumber);
                });
    }

    /// <summary>
    /// Sends an SMS or MMS message.
    /// </summary>
    public async Task<string> SendMessageAsync(string toNumber, string message, List<string>? mediaUrls = null, string urgency = "medium", string? callerName = null, string? location = null)
    {
        ArgumentNullException.ThrowIfNull(toNumber);
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrWhiteSpace(toNumber)) throw new ArgumentException("ToNumber required.", nameof(toNumber));

        var normalizedTo = Algos.NormalizeE164(toNumber);  // Keep E.164 for logs/validation
        var context = new Context { ["ToNumber"] = normalizedTo };

        try
        {
            var fullMessage = urgency.ToLowerInvariant() switch
            {
                "high" => $"URGENT from {callerName ?? "Caller"}: {message}{(location is { } l ? $" at {l}" : "")}",
                "medium" => $"Message from {callerName ?? "Caller"}: {message}{(location is { } l ? $" at {l}" : "")}",
                _ => $"Note from {callerName ?? "Caller"}: {message}{(location is { } l ? $" at {l}" : "")}"
            };
            fullMessage = Algos.TruncateMessage(fullMessage, 160);

            // Strip '+' via BulkVsNormalize for payload (schema examples: no '+')
            var bulkVsFrom = Algos.BulkVsNormalize(_config.FromNumber);
            var bulkVsTo = Algos.BulkVsNormalize(toNumber);

            var request = new SendRequest(
                From: bulkVsFrom,
                To: new List<string> { bulkVsTo },
                Message: fullMessage,
                MediaURLs: mediaUrls ?? new List<string>()  // Empty for SMS; non-empty for MMS (omits if empty via policy)
            );

            var jsonPayload = JsonSerializer.Serialize(request, _jsonOptions);
            using var jsonContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_requestTimeoutSeconds));

            _logger.Information("Sending JSON payload to {Endpoint}: {JsonPayload}", MessageSendEndpoint, jsonPayload);

            var response = await _retryPolicy.ExecuteAsync(ctx => _httpClient.PostAsync(MessageSendEndpoint, jsonContent, cts.Token), context);
            var responseBody = await response.Content.ReadAsStringAsync();

            _logger.Information("Received JSON response from {Endpoint} (Status: {StatusCode}): {ResponseBody}", MessageSendEndpoint, response.StatusCode, responseBody);

            if (!response.IsSuccessStatusCode)
            {
                string errorDetails = responseBody;
                try
                {
                    var errorObj = JsonSerializer.Deserialize<JsonElement>(responseBody, _jsonOptions);
                    if (errorObj.TryGetProperty("Code", out var code) && errorObj.TryGetProperty("Description", out var desc))
                    {
                        errorDetails = $"Code {code.GetString()} ({desc.GetString()})";
                    }
                }
                catch (JsonException)
                {
                    // Fallback to raw body
                }

                _logger.Warning("API error {StatusCode} for {ToNumber}: {ErrorDetails}", response.StatusCode, normalizedTo, errorDetails);
                return JsonSerializer.Serialize(new { error = "API request failed", details = $"{response.StatusCode}: {errorDetails}" }, _jsonOptions);
            }

            // Success: Deserialize and validate Results
            ApiResponse? apiResponse;
            try
            {
                apiResponse = JsonSerializer.Deserialize<ApiResponse>(responseBody, _jsonOptions);
            }
            catch (JsonException jex)
            {
                _logger.Warning(jex, "Non-JSON success response: {StatusCode} - {ResponseBody}", response.StatusCode, responseBody);
                return JsonSerializer.Serialize(new { error = "Invalid API response", details = $"{response.StatusCode}: {responseBody}" }, _jsonOptions);
            }

            // Check for success (RefId present + Results Status == "SUCCESS")
            var isSuccess = apiResponse?.RefId is { } && apiResponse.Results?.FirstOrDefault(r => r.To == bulkVsTo)?.Status?.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase) == true;
            if (isSuccess)
            {
                return HandleSendSuccess(apiResponse, normalizedTo);
            }

            // Partial/error in Results
            var details = string.Join(", ", apiResponse?.Results?.Select(r => $"{r.To}: {r.Status}") ?? new[] { "Unknown error" });
            _logger.Error("Send partial/error {StatusCode} for {ToNumber}: {Details}", response.StatusCode, normalizedTo, details);
            return JsonSerializer.Serialize(new { error = "Send failed", details }, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Send error for message to {ToNumber}", normalizedTo);
            return JsonSerializer.Serialize(new { error = "Delivery failed", details = ex.Message }, _jsonOptions);
        }
    }

    private string HandleSendSuccess(ApiResponse response, string toNumber)
    {
        _logger.Information("Send success: RefId={RefId}, MessageType={MessageType}, To={ToNumber}",
            response.RefId, response.MessageType, toNumber);
        return JsonSerializer.Serialize(new { status = "success", message = $"Message queued (RefId: {response.RefId})" }, _jsonOptions);
    }

    public void Dispose() => _httpClient?.Dispose();

    public record SendRequest(
        [property: JsonPropertyName("From")] string From,
        [property: JsonPropertyName("To")] List<string> To,
        [property: JsonPropertyName("Message")] string Message,
        [property: JsonPropertyName("MediaURLs")] List<string>? MediaURLs = null);

    private record ApiResponse(
        [property: JsonPropertyName("RefId")] string? RefId,
        [property: JsonPropertyName("From")] string? From,
        [property: JsonPropertyName("MessageType")] string? MessageType,
        [property: JsonPropertyName("Results")] IReadOnlyList<Result>? Results = null);

    private record Result(
        [property: JsonPropertyName("To")] string? To,
        [property: JsonPropertyName("Status")] string? Status);

    public record Webhook(
        [property: JsonPropertyName("Webhook")] string WebhookName,
        [property: JsonPropertyName("Description")] string? Description,
        [property: JsonPropertyName("Url")] string Url,
        [property: JsonPropertyName("Dlr")] bool Dlr,
        [property: JsonPropertyName("Method")] string Method,
        [property: JsonPropertyName("LastModification")] string? LastModification);
}