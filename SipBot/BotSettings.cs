using Serilog;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using static SipBot.ToolSchema;

namespace SipBot;

public static class BotSettings
{
    private static BotSettingsConfig? _settings;

    public static BotSettingsConfig Settings
    {
        get => _settings ?? throw new InvalidOperationException("Settings not loaded. Call LoadSettingsFromJson first.");
        private set => _settings = value;
    }

    public static async Task LoadSettingsFromJsonAsync(string? profileName = null, string? profilesDirectory = "profiles")
    {
        if (_settings != null)
        {
            Log.Warning("Settings already loaded; reloading...");
            _settings = null;
        }

        // Load SIP settings first (global, from SipBotSettings in SipBotLib)
        SipBotSettings.LoadSettingsFromJson();  // Full qualification to avoid using
        Log.Information("SIP settings loaded via SipBotLib.SipBotSettings.");

        // Resolve profile
        profileName ??= Environment.GetEnvironmentVariable("BOT_PROFILE") ?? "vendpartners";
        var cleanProfileName = Path.GetFileNameWithoutExtension(profileName);
        var effectiveProfilesDir = profilesDirectory ?? Directory.GetCurrentDirectory();
        var fullProfilePath = Path.Combine(effectiveProfilesDir, $"{cleanProfileName}.json");

        // Ensure dir exists
        if (!Directory.Exists(effectiveProfilesDir))
        {
            Directory.CreateDirectory(effectiveProfilesDir);
            Log.Warning("Created missing profiles directory: {Dir}", effectiveProfilesDir);
        }

        if (!File.Exists(fullProfilePath))
        {
            var available = GetAvailableProfiles(effectiveProfilesDir);
            throw new FileNotFoundException($"Profile file not found: {fullProfilePath}. Available profiles: {available}. Create '{cleanProfileName}.json' in '{effectiveProfilesDir}' or specify a valid name.");
        }

        // Load STT and profile
        var sharedOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var sttJson = await File.ReadAllTextAsync("sttsettings.json");
        var profileJson = await File.ReadAllTextAsync(fullProfilePath);

        var sttTemp = JsonSerializer.Deserialize<SttTemp>(sttJson, sharedOptions)
            ?? throw new InvalidOperationException("Failed to deserialize sttsettings.json.");
        var profileTemp = JsonSerializer.Deserialize<ProfileTemp>(profileJson, sharedOptions)
            ?? throw new InvalidOperationException("Failed to deserialize profile JSON.");

        // Compose full settings (SIP from SipBotSettings)
        _settings = new BotSettingsConfig
        {
            SpeechToText = sttTemp.SpeechToText,
            SipSettings = SipBotSettings.Settings,
            LanguageModel = profileTemp.LanguageModel,
            ProfileExtension = profileTemp.ProfileExtension
        };

        // Post-load validation
        ValidateSettings();

        Log.Information("Settings loaded successfully for profile: {ProfileName} from {ProfileFile}", cleanProfileName, fullProfilePath);
    }

    public static void LoadSettingsFromJson(string? profileName = null, string? profilesDirectory = null) =>
        LoadSettingsFromJsonAsync(profileName, profilesDirectory).GetAwaiter().GetResult();

    private static string GetAvailableProfiles(string directory)
    {
        var sharedFiles = new[] { "sttsettings.json", "sipsettings.json" };
        var profileFiles = Directory.GetFiles(directory, "*.json")
            .Where(f => !sharedFiles.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(name => name)
            .ToArray();

        return profileFiles.Length > 0 ? string.Join(", ", profileFiles) : "None (add .json files to the directory)";
    }

    private static void ValidateSettings()
    {
        if (string.IsNullOrEmpty(Settings.SpeechToText.SttModelUrl))
            throw new InvalidOperationException("SpeechToText.SttModelUrl is required.");

        if (!Settings.SipSettings.Configs.Any())
            throw new InvalidOperationException("At least one SIP config is required.");

        if (string.IsNullOrEmpty(Settings.LanguageModel.ApiKey))
            throw new InvalidOperationException("LanguageModel.ApiKey is required.");

        var sipIndex = Settings.ProfileExtension.SipAccountIndex;
        if (sipIndex < 0 || sipIndex >= Settings.SipSettings.Configs.Count)
            throw new InvalidOperationException($"ProfileExtension.SipAccountIndex ({sipIndex}) is out of bounds for {Settings.SipSettings.Configs.Count} SIP configs.");

        // Additional validation for BulkVs (if any config has it)
        foreach (var config in Settings.SipSettings.Configs)
        {
            if (config.BulkVs != null && (string.IsNullOrEmpty(config.BulkVs.Username) || string.IsNullOrEmpty(config.BulkVs.Password)))
                throw new InvalidOperationException($"Incomplete BulkVS config for SIP username '{config.Username}': Username and Password required for Basic Auth.");
        }
    }

    public static async Task ReloadAsync(string? profileName = null, string? profilesDirectory = null) =>
        await LoadSettingsFromJsonAsync(profileName, profilesDirectory);
}

// Temp POCOs for targeted deserialization (STT and profile only; SIP via SipBotLib)
[JsonSerializable(typeof(SttTemp))]
[JsonSerializable(typeof(ProfileTemp))]
[JsonSerializable(typeof(ProfileExtension))]
[JsonSerializable(typeof(LanguageModelConfig))]
[JsonSerializable(typeof(ToolSchema))]
[JsonSerializable(typeof(ExtensionSchema))]
internal partial class ConfigJsonContext : JsonSerializerContext { }

public class SttTemp
{
    [JsonPropertyName("SpeechToText")]
    public SpeechToTextConfig SpeechToText { get; set; } = new();
}

public class ProfileTemp
{
    [JsonPropertyName("ProfileExtensionKey")]
    public ProfileExtension ProfileExtension { get; set; } = new();

    [JsonPropertyName("LanguageModel")]
    public LanguageModelConfig LanguageModel { get; set; } = new();
}

public class ProfileExtension
{
    [JsonPropertyName("SipAccountIndex")]
    public int SipAccountIndex { get; set; } = 0;
}

public class BotSettingsConfig
{
    public SpeechToTextConfig SpeechToText { get; set; } = new();
    public SipSettingsConfig SipSettings { get; set; } = new();
    public LanguageModelConfig LanguageModel { get; set; } = new();
    public ProfileExtension ProfileExtension { get; set; } = new();
}

public class SpeechToTextConfig
{
    [JsonPropertyName("SttModelUrl")]
    public string SttModelUrl { get; set; } = string.Empty;
}

public class LanguageModelConfig
{
    [JsonPropertyName("ListenSipAccountIndex")] public int ListenSipAccountIndex { get; set; } = 0;
    [JsonPropertyName("ApiKey")] public string ApiKey { get; set; } = string.Empty;
    [JsonPropertyName("Model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("EndPoint")] public string EndPoint { get; set; } = string.Empty;
    [JsonPropertyName("MaxTokens")] public int MaxTokens { get; set; }
    [JsonPropertyName("WelcomeMessage")] public string WelcomeMessage { get; set; } = string.Empty;
    [JsonPropertyName("WelcomeFilePath")] public string WelcomeFilePath { get; set; } = "recordings/welcome_message.wav";
    [JsonPropertyName("InstructionsText")] public string InstructionsText { get; set; } = string.Empty;
    [JsonPropertyName("InstructionsAddendum")] public string InstructionsAddendum { get; set; } = string.Empty;
    [JsonPropertyName("ToolGuidance")] public string ToolGuidance { get; set; } = string.Empty;
    [JsonPropertyName("Temperature")] public float? Temperature { get; set; } = 0.7f;
    [JsonPropertyName("Tools")] public List<ToolSchema> Tools { get; set; } = new();
    [JsonPropertyName("Extensions")] public List<ExtensionSchema> Extensions { get; set; } = new();
}

/// <summary>
/// Records for JSON tool schema (OpenAPI-style for SK compatibility). Immutable by design.
/// </summary>
public record ToolSchema(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")] ParametersSchema Parameters)
{
    public record ParametersSchema(
        [property: JsonPropertyName("type")] string Type, // e.g., "object"
        [property: JsonPropertyName("properties")] IReadOnlyDictionary<string, ParamSchema> Properties,
        [property: JsonPropertyName("required")] string[]? Required = null);

    public record ParamSchema(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("default")] object? Default = null,
        [property: JsonPropertyName("enum")] string[]? Enum = null);
}

public class ExtensionSchema
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("number")] public string Number { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
}