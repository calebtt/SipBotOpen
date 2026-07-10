# SipBotOpen

An AI voice agent that answers real phone calls over SIP. It registers as a PBX extension, picks up incoming calls, transcribes the caller in real time, runs the conversation through an LLM with tool-calling, and speaks the response back — STT/TTS run locally; the LLM is any OpenAI-compatible API (example profile uses Grok / xAI).

**Status:** active development. Live-tested against VitalPBX; still not a production phone system. See [Known limitations](#known-limitations).

**License:** [MIT](LICENSE).

## Contents

- [How it works](#how-it-works)
- [Project layout](#project-layout)
- [Prerequisites](#prerequisites)
- [Setup](#setup)
- [Running the bot](#running-the-bot)
- [Configuration reference](#configuration-reference)
- [Profiles](#profiles)
- [Adding a tool](#adding-a-tool)
- [Known limitations](#known-limitations)
- [Contributing](#contributing)
- [License](#license)

## How it works

1. **SIP signaling and media** ([`SipBotLib`](SipBotLib)) — registers with a SIP server/PBX, answers incoming calls, and handles RTP. Built on [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery). NAT-friendly registration (STUN `ContactHost`, Contact user part, OPTIONS qualify). PCMU always available; G.722 wideband is supported inbound when negotiated.
2. **Voice activity detection** ([`MinimalSileroVAD.Core`](MinimalSileroVad/MinimalSileroVAD.Core)) — segments caller audio with [Silero VAD](https://github.com/snakers4/silero-vad) (ONNX, model embedded in the library). Telephony-tuned timings catch short replies like “yes” / “no”.
3. **Speech-to-text** — local [Whisper.net](https://github.com/sandrohanea/whisper.net), optionally CUDA-accelerated. Short VAD clips are silence-padded before Whisper for reliability.
4. **Conversation and tools** — [Microsoft Semantic Kernel](https://github.com/microsoft/semantic-kernel) + OpenAI-compatible chat. Built-in tools: take a message, blind-transfer, end call (explicit goodbye only), schedule follow-up.
5. **Text-to-speech** — local [KokoroSharp](https://github.com/Lyrcaxis/KokoroSharp); 22.05 kHz PCM is resampled with a cross-platform WDL path to 8 kHz PCMU for the wire (works on Linux; no MediaFoundation).

## Project layout

| Project | Purpose |
|---|---|
| `SipBot/` | Bot entry point, call handling, STT/TTS/LLM pipeline, tools, example profile. |
| `SipBotLib/` | Git submodule — registration, answer, RTP pacing, codecs, STUN/NAT helpers. |
| `MinimalSileroVad/` | Git submodule — Silero VAD library. |
| `MinimalVadTest/` | Small console harness for the VAD library. |
| `SipBotTestMs/` | MSTest unit tests (e.g. transfer URI normalization). |

Clone with submodules:

```bash
git clone --recurse-submodules https://github.com/calebtt/SipBotOpen.git
cd SipBotOpen
# if you already cloned without submodules:
git submodule update --init --recursive
```

## Prerequisites

- .NET 8 SDK
- A SIP account / PBX extension (live-tested with VitalPBX + public DID)
- An API key for an OpenAI-compatible LLM (example: Grok / xAI)
- Local model files (not in the repo):
  - Whisper GGML for STT (e.g. `ggml-base.en-q5_1.bin`)
  - Kokoro ONNX for TTS (`kokoro.onnx`)
- Optional: CUDA + NVIDIA GPU for faster STT/TTS

Silero VAD weights ship embedded inside `MinimalSileroVAD.Core`; you do not need a separate `silero_vad.onnx` for the bot.

## Setup

```bash
git clone --recurse-submodules https://github.com/calebtt/SipBotOpen.git
cd SipBotOpen
dotnet restore SipBot/SipBot.sln
```

**Model files** (paths used at runtime when you run from `SipBot/`):

```
SipBot/
├── models/
│   └── ggml-base.en-q5_1.bin    # Whisper (relative path in sttsettings.json)
└── kokoro.onnx                  # Kokoro (loaded from app base directory)
```

**Configuration** (gitignored — never commit secrets):

| File | Role |
|---|---|
| `SipBot/sipsettings.json` | SIP accounts (`server`, `port`, `username`, `password`, `fromname`) |
| `SipBot/sttsettings.json` | Whisper model path |
| `SipBot/profiles/<name>.json` | LLM, prompts, tools, welcome text, listen SIP index |

Example `sttsettings.json`:

```json
{
  "SpeechToText": {
    "SttModelUrl": "models/ggml-base.en-q5_1.bin"
  }
}
```

Example `sipsettings.json` shape (see also `SipBotLib/Config/examplesipsettings.json`):

```json
{
  "SipSettings": {
    "configs": [
      {
        "server": "pbx.example.com",
        "port": 5060,
        "username": "101",
        "password": "secret",
        "fromname": "Bot"
      }
    ]
  }
}
```

Copy and edit the example profile:

```bash
cp SipBot/profiles/personal.json SipBot/profiles/local.json
# set LanguageModel.ApiKey (and model/endpoint if needed)
```

`profiles/local.json` is gitignored for local API keys.

Build:

```bash
dotnet build SipBot/SipBot.sln
```

## Running the bot

Settings paths are relative to the current directory — run from `SipBot/`:

```bash
cd SipBot
dotnet run -- --profile=personal
# or with a private key profile:
dotnet run -- --profile=local
```

- Default profile name: **`personal`** (override with `--profile=` or `BOT_PROFILE`).
- Listen extension: `LanguageModel.ListenSipAccountIndex` into `sipsettings.json` configs.
- Headless / agent runs: Ctrl+C / SIGTERM stops cleanly (no `Console.ReadKey` required).
- Behind NAT: STUN sets `ContactHost`; the stack answers OPTIONS for PBX qualify.

Route a DID or ring group to the registered extension and call in.

## Configuration reference

| File | Contains |
|---|---|
| `sipsettings.json` | One or more SIP configs; index selected by the profile |
| `sttsettings.json` | Local Whisper model path (`SpeechToText.SttModelUrl`) |
| `profiles/<name>.json` | `ApiKey`, `Model`, `EndPoint`, system prompts, welcome WAV text/path, `tools`, `Extensions`, `ListenSipAccountIndex` |

Credentials are plaintext JSON for local dev only.

## Profiles

| Profile | Intent |
|---|---|
| [`profiles/personal.json`](SipBot/profiles/personal.json) | Personal cellphone assistant: take messages, transfer to a mapped extension, hang up on explicit goodbye. |

Tools in the personal profile:

- `send_notification` — log a message (SMS hook optional / not configured by default)
- `transfer_conversation` — blind transfer (`102`, `102@host`, or `sip:…` all accepted)
- `end_conversation` — hang up only on explicit goodbye / hang-up (cancelable if the caller keeps talking)
- `schedule_followup` — stub scheduler reply

## Adding a tool

1. Implement a `[KernelFunction]` on a plugin type (see [`SimpleSemanticToolFunctions.cs`](SipBot/Agent/LLMClient/SimpleSemanticToolFunctions.cs)).
2. Add a matching entry to the profile `tools` array so the model knows the schema.

```csharp
[KernelFunction("get_weather")]
[Description("Fetches current weather for a city.")]
public async Task<string> GetWeatherAsync(
    [Description("City name")] string city)
{
    // ...
    return summary;
}
```

## Known limitations

- **Outbound TTS is PCMU-only.** Inbound G.722 can be negotiated/decoded; the TTS → RTP path still encodes μ-law at 8 kHz. Do not enable wideband on the bot endpoint until outbound is codec-aware.
- **No real AEC.** Echo-registration hooks exist; acoustic echo cancellation is not implemented.
- **Blind transfer depends on the PBX and the transfer target.** REFER success means the PBX accepted the transfer, not that extension 102 rang or answered. Test with a second registered softphone when possible.
- **LLM behavior is profile-driven.** Narrow “message / transfer” prompts will refuse open-ended questions (e.g. time of day) and may sound formulaic on short replies like “no.”
- **Not multi-tenant production hardened** (secrets in JSON, single listener, limited metrics/ops).

## Contributing

1. Fork the repository.
2. Create a feature branch (`git checkout -b feature/your-feature`).
3. Commit with a message that explains *why*, not just what.
4. Open a pull request describing what changed and how you verified it.

## License

This project is licensed under the [MIT License](LICENSE). Copyright (c) 2026 Caleb Taylor.
