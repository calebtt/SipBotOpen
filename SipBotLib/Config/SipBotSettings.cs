// Updated SipBotSettings.cs (in SipBotLib\Config)
// Changes: Added nested TrunkConfig and BulkVsConfig to SipConfig for per-extension support.
// Added optional ValidateSettings() method (call from BotSettings if needed).
// Maintains simple load; no breaking changes. Now fully supports BulkVS/Trunk deserialization.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace SipBot;

public static class SipBotSettings
{
    private static SipSettingsConfig? _settings;

    public static SipSettingsConfig Settings
    {
        get => _settings ?? throw new InvalidOperationException("SIP settings not loaded. Call LoadSettingsFromJson first.");
        private set => _settings = value;
    }

    public static void LoadSettingsFromJson(string filePath = "sipsettings.json")
    {
        string fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            string currentDir = Directory.GetCurrentDirectory();
            throw new FileNotFoundException($"File not found at: {filePath} - resolved to {fullPath}\nCurrent Working Dir: {currentDir}");
        }

        string jsonString = File.ReadAllText(fullPath);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var settings = JsonSerializer.Deserialize<SipSettingsWrapper>(jsonString, options);

        Settings = settings?.SipSettings ?? throw new InvalidOperationException("Failed to deserialize SIP settings from JSON.");

        // Optional post-load validation (uncomment/call as needed)
        // ValidateSettings();
    }

    /// <summary>
    /// Optional validation for nested Trunk/BulkVs configs.
    /// </summary>
    public static void ValidateSettings()
    {
        if (!Settings.Configs.Any())
            throw new InvalidOperationException("At least one SIP config is required.");

        // Validate BulkVs (if present)
        foreach (var config in Settings.Configs)
        {
            if (config.BulkVs != null && (string.IsNullOrEmpty(config.BulkVs.Username) || string.IsNullOrEmpty(config.BulkVs.Password)))
                throw new InvalidOperationException($"Incomplete BulkVS config for SIP username '{config.Username}': Username and Password required for Basic Auth.");
        }
    }
}

public class SipSettingsWrapper
{
    [JsonPropertyName("SipSettings")]
    public SipSettingsConfig SipSettings { get; set; } = new SipSettingsConfig();
}

public class SipSettingsConfig
{
    [JsonPropertyName("Configs")]
    public List<SipConfig> Configs { get; set; } = new List<SipConfig>();
}

// Per-extension SIP config (supports nested trunk and bulkVs)
public class SipConfig
{
    [JsonPropertyName("server")] public string Server { get; set; } = string.Empty;
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("password")] public string Password { get; set; } = string.Empty;
    [JsonPropertyName("fromname")] public string FromName { get; set; } = string.Empty;
    [JsonPropertyName("didMappings")]
    public Dictionary<string, string> DidMappings { get; set; } = new();  // Key: internal ext (e.g., "102"), Value: external DID (e.g., "+15551234567")

    [JsonPropertyName("trunk")]
    public TrunkConfig? Trunk { get; set; }

    [JsonPropertyName("bulkVs")]
    public BulkVsConfig? BulkVs { get; set; }
}

// SIP trunk details (nested in SipConfig)
public class TrunkConfig
{
    [JsonPropertyName("host")] public string Host { get; set; } = string.Empty;
    [JsonPropertyName("port")] public int Port { get; set; } = 5060;
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("password")] public string Password { get; set; } = string.Empty;
    [JsonPropertyName("realm")] public string Realm { get; set; } = string.Empty;
}

// BulkVS SMS config (nested in SipConfig; class style matching others)
public class BulkVsConfig
{
    [JsonPropertyName("apiBaseUrl")] public string ApiBaseUrl { get; set; } = "https://portal.bulkvs.com";
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;  // API username (e.g., email)
    [JsonPropertyName("password")] public string Password { get; set; } = string.Empty;  // API secret key
    [JsonPropertyName("fromNumber")] public string FromNumber { get; set; } = string.Empty;  // Sender DID (E.164)
    [JsonPropertyName("maxRetries")] public int? MaxRetries { get; set; } = 3;
}