# SipBotOpen

An AI voice agent that answers real phone calls over SIP. It registers as a PBX extension, picks up incoming calls, transcribes the caller in real time, runs the conversation through an LLM with tool-calling, and speaks the response back — all locally, with no cloud STT/TTS dependency required.

**Status:** active development. Not hardened for production use; see [Known limitations](#known-limitations) before deploying against real traffic.

## Contents

- [How it works](#how-it-works)
- [Project layout](#project-layout)
- [Prerequisites](#prerequisites)
- [Setup](#setup)
- [Running the bot](#running-the-bot)
- [Configuration reference](#configuration-reference)
- [Adding a tool](#adding-a-tool)
- [Known limitations](#known-limitations)
- [Contributing](#contributing)
- [License](#license)

## How it works

1. **SIP signaling and media** ([`SipBotLib`](SipBotLib)) — registers with a SIP server/PBX, answers incoming calls, and handles RTP audio. Built on [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery). Supports PCMU (narrowband, always on) and G.722 (wideband, opt-in) — VitalPBX and other Asterisk-based systems support G.722 with no extra licensing.
2. **Voice activity detection** ([`MinimalSileroVAD.Core`](MinimalSileroVAD.Core)) — segments caller audio into utterances using the [Silero VAD](https://github.com/snakers4/silero-vad) ONNX model, so the bot only transcribes actual speech.
3. **Speech-to-text** — local transcription via [Whisper.net](https://github.com/sandrohanea/whisper.net), optionally CUDA-accelerated.
4. **Conversation and tool-calling** — [Microsoft Semantic Kernel](https://github.com/microsoft/semantic-kernel) orchestrates the LLM call and any tool invocations (transferring the call, logging a message, scheduling a follow-up, ending the call). Talks to any OpenAI-compatible endpoint; the example profile targets Grok (xAI).
5. **Text-to-speech** — local synthesis via [KokoroSharp](https://github.com/Lyrcaxis/KokoroSharp), paced back out over RTP.

## Project layout

| Project | Purpose |
|---|---|
| `SipBot/` | The bot itself — entry point, call handling, STT/TTS/LLM pipeline, tool implementations. |
| `SipBotLib/` | Reusable SIP client library (registration, call answering, RTP audio, codec negotiation). |
| `MinimalSileroVAD.Core/` | Silero VAD wrapper used for speech segmentation. |
| `MinimalVadTest/` | Standalone console harness for exercising the VAD library against a WAV file. |
| `SipBotTestMs/` | MSTest unit tests. |

## Prerequisites

- .NET 8 SDK
- A SIP account or PBX extension to register against (this has been tested live against a VitalPBX-backed trunk)
- An API key for an OpenAI-compatible LLM endpoint (the example profile uses Grok/xAI)
- Local model files (not included in the repo — see below):
  - A Whisper GGML model for STT (e.g. `ggml-base.en-q5_1.bin`)
  - A Kokoro ONNX model for TTS (e.g. `kokoro.onnx`)
  - The Silero VAD ONNX model for speech segmentation (`silero_vad.onnx`)
- Optional: CUDA Toolkit + a supported NVIDIA GPU, if you want GPU-accelerated STT/TTS instead of CPU inference

## Setup

```bash
git clone https://github.com/calebtt/SipBotOpen.git
cd SipBotOpen
dotnet restore SipBot/SipBot.sln
```

**Model files.** Place the three model files above under `SipBot/models/`:

```
SipBot/models/
├── ggml-base.en-q5_1.bin
├── kokoro.onnx
└── silero_vad.onnx
```

**Configuration.** None of these are committed (they hold credentials) — create them alongside `SipBot.csproj`:

- `sipsettings.json` — one or more SIP account configs (server, port, username, password). See [`SipBotLib`'s settings model](SipBotLib/Config/SipBotSettings.cs) for the exact shape.
- `sttsettings.json` — points at your local model files:

  ```json
  {
    "SpeechToText": {
      "SttModelUrl": "models/ggml-base.en-q5_1.bin",
      "SileroVadModelPath": "models/silero_vad.onnx"
    }
  }
  ```

- `profiles/<name>.json` — one file per bot "personality": LLM settings, system prompt, and the tools it can call. See [`SipBot/profiles/personal.json`](SipBot/profiles/personal.json) for a full worked example.

Build once configured (from the repo root, builds all projects):

```bash
dotnet build SipBot/SipBot.sln
```

## Running the bot

Settings are loaded relative to the current directory, so run from inside `SipBot/`:

```bash
cd SipBot
dotnet run
```

By default it loads the `personal` profile; override with `--profile=<name>` or the `BOT_PROFILE` environment variable. On startup it registers with your SIP server using the account selected by `LanguageModel.ListenSipAccountIndex` in the active profile. Route incoming calls to that extension and call in to test.

## Configuration reference

| File | Contains |
|---|---|
| `sipsettings.json` | SIP account credentials (one or more accounts; the bot listens on the one selected by profile) |
| `sttsettings.json` | Paths to the local Whisper and Silero VAD models |
| `profiles/<name>.json` | LLM endpoint/key/model, system prompt (`InstructionsText`, `InstructionsAddendum`, `ToolGuidance`), welcome message, and the `tools`/`Extensions` the bot can use during a call |

Credentials in these files are plaintext JSON — fine for local development, but swap in a real secrets manager before running anything that matters.

## Adding a tool

Tools are defined two places: the JSON schema in your profile's `tools` array (so the LLM knows the tool exists and its parameters), and the implementation as a `[KernelFunction]` method. See [`SimpleSemanticToolFunctions.cs`](SipBot/Agent/LLMClient/SimpleSemanticToolFunctions.cs) for the existing tools (`send_notification`, `transfer_conversation`, `end_conversation`, `schedule_followup`) as a template:

```csharp
[KernelFunction("get_weather")]
[Description("Fetches current weather for a city.")]
public async Task<string> GetWeatherAsync(
    [Description("City name")] string city)
{
    using var httpClient = new HttpClient();
    var response = await httpClient.GetStringAsync($"https://api.weatherapi.com/v1/current.json?key=YOUR_KEY&q={city}");
    // parse and return the relevant fields
    return response;
}
```

Then add the matching entry to your profile's `tools` array so the model knows to call it.

## Known limitations

- **Wideband audio (G.722) doesn't reach the TTS output yet.** `SipBotLib` supports negotiating and decoding G.722, and inbound caller audio benefits from it automatically. Outbound TTS audio still goes out as PCMU regardless of what got negotiated, since that path doesn't route through the codec-aware pipeline. Enabling wideband on the bot's audio endpoint without also fixing this would risk sending malformed audio on any call where G.722 negotiates.
- **No AEC (acoustic echo cancellation).** There's a stub method suggesting one was planned; it's currently a no-op.
- No license is set yet — see [License](#license).

## Contributing

1. Fork the repository.
2. Create a feature branch (`git checkout -b feature/your-feature`).
3. Commit with a message that explains *why*, not just what.
4. Open a pull request describing what changed and how you verified it.

## License

No license has been set for this project yet. Contact the repository owner for usage permissions.
