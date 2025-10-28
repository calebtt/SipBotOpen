# SipBotOpen

## Overview

SipBotOpen is an open-source telephone voice assistant designed to handle incoming calls intelligently. It acts as a full AI-powered voice agent pipeline, leveraging large language models (LLMs) for natural conversation handling and tool calling capabilities. Built with Semantic Kernel for AI orchestration, this project provides a robust foundation for creating automated voice responses over SIP (Session Initiation Protocol) telephony.

Key highlights:
- **Answer-Only Mode**: Focuses exclusively on responding to incoming calls without initiating outbound communications.
- **AI-Driven Interactions**: Uses LLMs to process voice inputs, generate responses, and invoke tools dynamically.
- **Extensible Pipeline**: Modular design allows easy integration of custom tools, APIs, or additional AI features.

This project is ideal for developers building automated customer service bots, personal assistants, or interactive voice response (IVR) systems.

## Features

- **Voice-to-Text and Text-to-Voice Conversion**: Seamless integration for converting speech to text (STT) and text to speech (TTS).
- **Tool Calling**: Semantic Kernel enables the agent to call external tools or APIs based on user queries (e.g., fetching weather, scheduling events).
- **SIP Integration**: Handles telephony via SIP protocols for reliable call management.
- **LLM Agnostic**: Compatible with various LLMs (e.g., OpenAI, Hugging Face models) through Semantic Kernel.
- **Customizable Prompts**: Easily tweak system prompts for tailored assistant behavior.
- **Logging and Monitoring**: Built-in support for call logging and error handling.
- **Security-Focused**: Designed with best practices for handling sensitive call data (e.g., no storage of personal info by default).

## Prerequisites

- .NET SDK (version 8.0 or higher recommended)
- Access to an LLM provider (e.g., xAI API key)
- SIP server or provider (e.g., Twilio, Asterisk) for testing telephony features
- ONNX Runtime (for model inference)
- cuDNN (for GPU acceleration with CUDA-enabled setups)
- Optional: STT/TTS services (e.g., Google Cloud Speech-to-Text, Amazon Polly); for local STT, ensure Whisper.cpp dependencies are met, including CUDA toolkit if using GPU.

## Installation

1. Clone the repository:

    git clone https://github.com/calebtt/SipBotOpen.git
    cd SipBotOpen

2. Restore NuGet packages:

    dotnet restore

3. Configure settings:
   - Edit `sipsettings.json` to add your SIP info, such as server, port, username, password, and BulkVS details if applicable (note: storing keys in JSON is not the most secure option, but it's how the project is currently set up; consider using a proper secrets manager in production).
   - Edit `sttsettings.json` to set the STT model URL if needed.
   - Create or edit a profile JSON file in the `profiles/` directory (e.g., `personal.json`) and add your LLM API key, model, endpoint, and other settings.
   - Optionally, set the `BOT_PROFILE` environment variable to your profile name (without .json extension) to load it automatically.

4. Build the project:

    dotnet build

## Usage

1. Run the application:

    dotnet run

2. The bot acts as a full SIP client and will register with the SIP server using the credentials from `sipsettings.json`. Ensure your SIP server or trunk is configured to route incoming calls to the registered usernames/extensions (e.g., "101", "102").

3. Test a call:
   - Dial the configured SIP number associated with one of the bot's registered accounts.
   - Speak a query (e.g., "What's the weather today?").
   - The bot will process the input via LLM, call tools if needed, and respond verbally.

For advanced customization:
- Edit the profile JSON (e.g., `personal.json`) to modify system prompts like InstructionsText, InstructionsAddendum, ToolGuidance, etc., and to define tools schemas under the "tools" array.
- Add new tools by implementing Semantic Kernel skills in code, following the examples provided in SimpleSemanticToolFunctions.cs (code-based implementations, no plugin DLLs required).

Example code snippet for adding a custom tool (using modern C# best practices):

    using Microsoft.SemanticKernel;
    using Microsoft.SemanticKernel.Skills.Core;

    // Define a custom tool
    [KernelFunction("GetWeather")]
    [Description("Fetches current weather for a city.")]
    public async Task<string> GetWeatherAsync(string city)
    {
        // Use HttpClient for API calls (inject via DI for robustness)
        using var httpClient = new HttpClient();
        var response = await httpClient.GetStringAsync($"https://api.weatherapi.com/v1/current.json?key=your_key&q={city}");
        // Parse and return relevant data (error handling omitted for brevity)
        return $"Weather in {city}: Sunny, 75°F"; // Placeholder
    }

## Contributing

Contributions are welcome! Please follow these steps:
1. Fork the repository.
2. Create a feature branch (`git checkout -b feature/YourFeature`).
3. Commit your changes (`git commit -m 'Add YourFeature'`).
4. Push to the branch (`git push origin feature/YourFeature`).
5. Open a Pull Request.

Adhere to modern best practices: Use meaningful commit messages, include unit tests, and follow C# coding standards (e.g., async/await for I/O operations).

## License

No license has been set for this project yet. Please contact the repository owner for usage permissions.

## Acknowledgments

- Built with [Microsoft Semantic Kernel](https://github.com/microsoft/semantic-kernel).
- Inspired by open-source voice AI projects.

For questions or issues, open a GitHub issue or reach out via discussions.
