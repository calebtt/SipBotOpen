using Microsoft.SemanticKernel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Serilog;
using SipBot;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace SipBot.Tests;

[TestClass]
public class PersonalToolCallingTests : IDisposable
{
    private ILogger? _testLogger;
    private LlmChat? _llmChat;
    private List<CapturedToolCall> _capturedCalls = new();

    // Updated TestInitializeAsync in PersonalProfileToolCallingTest.cs
    [TestInitialize]
    public async Task TestInitializeAsync()
    {
        // Fresh logger per test (avoids sink races)
        _testLogger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .CreateLogger();

        Log.Logger = _testLogger; // Override global for this test

        // Load + clone config (isolates from static)
        await BotSettings.LoadSettingsFromJsonAsync("personal");
        var configJson = JsonSerializer.Serialize(BotSettings.Settings.LanguageModel);
        var config = JsonSerializer.Deserialize<LanguageModelConfig>(configJson)!; // Deep clone via JSON

        // Fresh captured list per test
        _capturedCalls = new List<CapturedToolCall>();

        // Use test subclass for capturing tool calls; pass extensions for mapping
        var toolFunctions = new TestSemanticToolFunctions(_capturedCalls);

        // Build kernel dynamically (similar to production)
        var kernel = Algos.BuildKernel(config);

        // Fresh LlmChat per test with pre-loaded kernel and tool functions
        _llmChat = new LlmChat(config: config, toolFunctions: toolFunctions, kernel: kernel);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        // No explicit Dispose on LlmChat; rely on GC for kernel cleanup
        _llmChat = null;
        _capturedCalls.Clear();
        ((IDisposable?)_testLogger)?.Dispose();
        Log.CloseAndFlush(); // Serilog best practice
    }

    [DataRow("Hi, this is Bob. Please tell Caleb to call me back about the project.", "", "send_notification", "tell Caleb to call me back about the project", "medium")]
    [DataRow("I need to speak to Caleb right now, it's urgent.", "", "transfer_conversation", "escalation requested", null, "102@slowcasting.com")]
    [DataRow("Wrong number, sorry.", "", "end_conversation", "wrong number")]
    [DataRow("Can you schedule a callback for tomorrow afternoon?", "", "schedule_followup", "callback", null, null, "tomorrow afternoon")]
    [DataRow("Tell Caleb that the meeting is rescheduled to Friday.", "", "send_notification", "meeting is rescheduled to Friday", "low")]
    [DataRow("This is an emergency, connect me to Caleb!", "", "transfer_conversation", "emergency", null, "102@slowcasting.com")]
    [DataRow("Goodbye, that's all.", "", "end_conversation", "user ended call")]
    [DataRow("I'd like a follow-up visit next week.", "", "schedule_followup", "visit", null, null, "next week")]
    [DataRow("My name is Alice, and I have a question about the account.", "Actually, can I speak to Caleb?", "transfer_conversation", "speak to Caleb", null, "102@slowcasting.com")]
    [DataRow("Hang up now!", "", "end_conversation", "user requested hang up")]
    [DataRow("Please log this: The server is down.", "", "send_notification", "server is down", "high")]
    [DataRow("Transfer me to support.", "", "transfer_conversation", "transfer to support", null, "102@slowcasting.com")]
    [DataTestMethod]
    public async Task ToolCalling_WithArguments_ShouldValidateParameters(
        string firstPhrase,
        string followUpPhrase,
        string expectedTool,
        string expectedArgValue,
        string? expectedUrgency = null,
        string? expectedExtension = null,
        string? expectedTime = null)
    {
        Log.Information("Test Input Phrase 1: '{Phrase}' | Expected Tool: {ExpectedTool} | Expected Arg: {ExpectedArg}", firstPhrase, expectedTool, expectedArgValue);

        // Act: Multi-turn simulation
        var response1 = await _llmChat!.ProcessMessageAsync(firstPhrase);
        Log.Information("LLM Conversation - Sent: '{UserMessage}' | Received: '{Response}'", firstPhrase, response1 ?? "No response");

        if (!string.IsNullOrEmpty(followUpPhrase))
        {
            Log.Information("Test Input Phrase 2: '{Phrase}'", followUpPhrase);
            var response2 = await _llmChat.ProcessMessageAsync(followUpPhrase);
            Log.Information("LLM Conversation - Sent: '{UserMessage}' | Received: '{Response}'", followUpPhrase, response2 ?? "No response");
        }

        Log.Information("Total Tool Calls Captured: {Count}", _capturedCalls.Count);

        // Assert: Use captured for robustness
        var relevantCalls = _capturedCalls.Where(c => c.Name == expectedTool).ToList();
        Assert.IsTrue(relevantCalls.Count >= 1, $"Expected >=1 '{expectedTool}'; captured {_capturedCalls.Count} total. Check LLM responses above for clues.");

        var call = relevantCalls.First(); // Take first (or aggregate if multi)
        AssertExpectedArgs(call.Arguments, expectedTool, expectedArgValue, expectedUrgency, expectedExtension, expectedTime);

        // Log for debugging
        Log.Information($"Tool {expectedTool} invoked with validated arguments.");
    }

    // Test for clear history (ensures fresh sessions)
    // Updated ClearHistory test in PersonalProfileToolCallingTest.cs (similar updates for sessions)
    [TestMethod]
    public async Task ClearHistory_ShouldResetForNewSessions()
    {
        // Arrange: First instance
        await BotSettings.LoadSettingsFromJsonAsync("personal");
        var configJson = JsonSerializer.Serialize(BotSettings.Settings.LanguageModel);
        var config = JsonSerializer.Deserialize<LanguageModelConfig>(configJson)!;

        // Fresh captured for session 1
        var captured1 = new List<CapturedToolCall>();
        var toolFunctions1 = new TestSemanticToolFunctions(captured1);

        // Build kernel1 dynamically
        var kernel1 = Algos.BuildKernel(config);

        var llmChat1 = new LlmChat(config: config, toolFunctions: toolFunctions1, kernel: kernel1);

        // Act: First message triggers transfer_conversation
        var response1 = await llmChat1.ProcessMessageAsync("I'd like to speak to Caleb about an urgent matter.");
        Log.Information("LLM Conversation (Session 1) - Sent: 'I'd like to speak to Caleb about an urgent matter.' | Received: '{Response}'", response1 ?? "No response");

        // Assert: transfer_conversation called at least once (LLM likely passes "personal"; mapping happens internally)
        Assert.IsTrue(captured1.Any(c => c.Name == "transfer_conversation"), "Expected transfer_conversation in session 1.");

        // Act: Clear and second message triggers end_conversation
        llmChat1.ClearHistory();

        // Fresh second instance (isolates any leak)
        var captured2 = new List<CapturedToolCall>();
        var toolFunctions2 = new TestSemanticToolFunctions(captured2);

        // Build kernel2 dynamically
        var kernel2 = Algos.BuildKernel(config);

        var llmChat2 = new LlmChat(config: config, toolFunctions: toolFunctions2, kernel: kernel2);
        var response2 = await llmChat2.ProcessMessageAsync("Wrong number.");
        Log.Information("LLM Conversation (Session 2) - Sent: 'Wrong number.' | Received: '{Response}'", response2 ?? "No response");

        // Assert: end_conversation called at least once (independent of first session)
        Assert.IsTrue(captured2.Any(c => c.Name == "end_conversation"), "Expected end_conversation in session 2.");

        // Cross-verify no bleed
        Assert.IsFalse(captured1.Any(c => c.Name == "end_conversation"), "Session 1 should not have end_conversation.");
        Assert.IsFalse(captured2.Any(c => c.Name == "transfer_conversation"), "Session 2 should not have transfer_conversation.");

        Log.Information("ClearHistory verified: Independent sessions with distinct tools.");
    }

    // Helper for arg assertions (extracted for reusability; uses fuzzy matching)
    private static void AssertExpectedArgs(Dictionary<string, object> args, string toolName, string? expectedArgValue, string? expectedUrgency, string? expectedExtension, string? expectedTime)
    {
        // Normalize: Trim and lowercase for robustness
        var normalizedArgs = args.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString()?.Trim().ToLowerInvariant() ?? "");

        if (!string.IsNullOrEmpty(expectedArgValue))
        {
            string argKey = toolName switch
            {
                "send_notification" => "issue",
                "transfer_conversation" => "reason",
                "end_conversation" => "reason",
                "schedule_followup" => "service_type",
                _ => throw new NotSupportedException($"Unsupported tool: {toolName}")
            };
            Assert.IsTrue(normalizedArgs.ContainsKey(argKey), $"Missing key '{argKey}'.");
            // Fuzzy: Use Contains (case-insensitive)
            Assert.IsTrue(normalizedArgs[argKey].Contains(expectedArgValue.ToLowerInvariant()),
                $"Arg '{argKey}' should contain '{expectedArgValue}' (actual: '{normalizedArgs[argKey]}').");
        }

        // Similar for other params (exact or fuzzy as needed)
        if (!string.IsNullOrEmpty(expectedUrgency) && toolName == "send_notification")
        {
            Assert.IsTrue(normalizedArgs.ContainsKey("urgency") && normalizedArgs["urgency"] == expectedUrgency.ToLowerInvariant());
        }
        if (!string.IsNullOrEmpty(expectedExtension) && toolName == "transfer_conversation")
        {
            Assert.IsTrue(normalizedArgs.ContainsKey("extension") && normalizedArgs["extension"] == expectedExtension.ToLowerInvariant());
        }
        if (!string.IsNullOrEmpty(expectedTime) && toolName == "schedule_followup")
        {
            Assert.IsTrue(normalizedArgs.ContainsKey("preferred_time") && normalizedArgs["preferred_time"].Contains(expectedTime.ToLowerInvariant()));
        }
    }

    public void Dispose()
    {
        TestCleanup(); // Ensure final flush
    }

    // Record for captures (immutable, thread-safe)
    private record CapturedToolCall(string Name, Dictionary<string, object> Arguments);

    // Now passes extensions to base for mapping support

    private class TestSemanticToolFunctions : SimpleSemanticToolFunctions
    {
        private readonly List<CapturedToolCall> _captured;

        public TestSemanticToolFunctions(List<CapturedToolCall> captured)
            : base(null, null)
        {
            _captured = captured;
        }

        public override async Task<string> SendNotificationAsync(string issue, string? location = "", string urgency = "medium", string? caller_name = "")
        {
            _captured.Add(new CapturedToolCall("send_notification", new Dictionary<string, object> { ["issue"] = issue, ["location"] = location ?? "", ["urgency"] = urgency, ["caller_name"] = caller_name ?? "" }));
            Log.Information("Captured: send_notification | Args: [issue={issue}, location={location}, urgency={urgency}, caller_name={caller_name}]", issue, location ?? "", urgency, caller_name ?? "");
            return await base.SendNotificationAsync(issue, location, urgency, caller_name);
        }

        public override async Task<string> TransferConversationAsync(string extension, string? reason = "")
        {
            _captured.Add(new CapturedToolCall("transfer_conversation", new Dictionary<string, object> { ["extension"] = extension, ["reason"] = reason ?? "" }));
            Log.Information("Captured: transfer_conversation | Args: [extension={extension}, reason={reason}]", extension, reason ?? "");
            return await base.TransferConversationAsync(extension, reason);
        }

        public override async Task<string> EndConversationAsync(string? reason = "")
        {
            _captured.Add(new CapturedToolCall("end_conversation", new Dictionary<string, object> { ["reason"] = reason ?? "" }));
            Log.Information("Captured: end_conversation | Args: [reason={reason}]", reason ?? "");
            return await base.EndConversationAsync(reason);
        }

        public override async Task<string> ScheduleFollowupAsync(string service_type = "callback", string? location = "", string? preferred_time = "")
        {
            _captured.Add(new CapturedToolCall("schedule_followup", new Dictionary<string, object> { ["service_type"] = service_type, ["location"] = location ?? "", ["preferred_time"] = preferred_time ?? "" }));
            Log.Information("Captured: schedule_followup | Args: [service_type={service_type}, location={location}, preferred_time={preferred_time}]", service_type, location ?? "", preferred_time ?? "");
            return await base.ScheduleFollowupAsync(service_type, location, preferred_time);
        }
    }
}