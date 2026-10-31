# Third-party components

- NAudio 2.2.1 — MIT — https://github.com/naudio/NAudio/blob/master/license.txt
- Whisper.net and Whisper.net.Runtime 1.9.1 — MIT — https://github.com/sandrohanea/whisper.net
- whisper.cpp / GGML native libraries shipped through Whisper.net.Runtime — MIT — https://github.com/ggml-org/whisper.cpp
- Microsoft .NET 10 / WPF self-contained runtime — MIT and associated third-party notices — https://github.com/dotnet/runtime and https://github.com/dotnet/wpf
- Whisper base.en model downloaded separately from the Whisper.net configured GGML distribution; OpenAI Whisper model/license: https://github.com/openai/whisper/blob/main/LICENSE

NuGet component license texts and runtime third-party notices are included in the release `Licenses` directory. The JFK speech WAV used for developer verification came from https://github.com/ggml-org/whisper.cpp/blob/master/samples/jfk.wav and is not distributed as application content.

Soundpad communication is implemented against Leppsoft's documented Remote Control Interface 1.1.2. No Soundpad executable or library is distributed. Its existing libraries remain managed by Soundpad.
