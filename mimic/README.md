> **Part of Soundpad Web since 2.4.0.** This folder is the Voice Replay Studio V1.1 desktop app, imported unchanged apart from this note. Its rolling-capture idea now also runs inside the Soundpad Web companion as **Mimic**, a window in the website; see [docs/MIMIC.md](../docs/MIMIC.md). Build this app from this folder with `.\Build.ps1` (it looks for a local SDK in `.tools\dotnet`, otherwise uses `dotnet`).

# Voice Replay Studio V1.1

Native Windows 11 x64 rolling audio recorder with an integrated live waveform and clip editor.

Run `Release\VoiceReplayStudio-V1.1\Voice Replay Studio.exe`. Keep the entire folder together. The ZIP contains the same self-contained Windows application; Visual Studio and a separate .NET installation are not required. Native Whisper also uses the Microsoft VC++ x64 runtime, which is already installed on this machine.

The older V1 application was running during development and was left untouched. **Exit that version from its tray menu before opening V1.1.** The normal application enforces one instance to protect capture hotkeys and the shared library.

## First run

1. Select **Monitor out** below the waveform, independently of **Record from**. For this machine, **Headphones (2- Elgato Wave:3)** is an available physical output. An actual Wave Link **Monitor** output is supported if Windows exposes it. Application buses such as Elgato System/Game/Voice chat/Soundpad and known outgoing mixer endpoints are blocked for previews. No Windows or Wave Link routing, volume, mute, or default devices are changed.
2. Press **Test Playback**. It plays a short, low-level 400 ms tone through that output. If it is not heard, press **Output diagnostic** and check the reported endpoint and application-session mute/volume in Windows Volume Mixer. The app reports a muted/zero-volume session without changing it. Ensure your existing mixer does not include the chosen monitor output in outgoing chat.
3. Select the recording application or output device in **Record from**, then **Start monitoring**. Process capture includes that process tree. An output source captures all applications rendered to that endpoint. Microphone inputs are separately labeled and require explicit selection. No audio is continuously routed back to the microphone.
4. **Live Buffer** shows incoming PCM with the newest audio on the right. Choose a 5-, 10-, or 30-second view. The producer runs at 25 FPS; drawing is bounded to the newest prepared frame. Waveform work is suspended while the main window is hidden, while capture and the rolling clock continue.
5. Drag to select historical audio, or enter start/end times matching the ruler. In Live Buffer these are negative seconds relative to the displayed right edge. The display stays still while dragging so the selection has a stable reference; capture continues. Press **Save live selection** to save precisely that full-quality audio. Expired selections are disabled and rejected, never silently shifted to new audio.
6. **F6** saves the configured last 3/5/10/15/30 seconds and immediately opens the new clip in the inline editor. During buffer warmup only available audio is saved. Silence produces a visible error rather than an empty recording. If another clip has unsaved changes, its edits are preserved and the new capture is available in the library.

## One waveform workspace

Select a library item or press **Clip Editor**. There is no popup editor. Switch back to **Live Buffer** at any time; neither mode switch restarts recording. Unsaved working audio is preserved when switching between modes for the same clip.

- **Play All**, **Play Selection**, **Play From Cursor**, **Stop**, and **Loop selection** use the selected monitor output.
- Click/drag the timeline ruler or Ctrl+drag to seek. During playback, seeking preserves the active playback region; a point outside it is rejected. A seek is committed on mouse release to avoid repeatedly reopening the device while dragging.
- Zoom +/− and horizontal scrolling inspect saved clips. Gold handles adjust selection boundaries. Exact numerical points and selection duration are displayed.
- **Trim to selection** changes working audio in memory. **Undo trim** restores earlier audio (up to 24 states). **Revert original** loads the original recording. **Reload saved** discards unsaved work and reloads the current saved version.
- Edit the title and transcript, then **Save version**. The current working audio is saved; merely selecting a region does not trim until Trim is pressed. **Save + retranscribe** queues recognition after the edit.
- Original recordings and existing Soundpad backing files are retained. Edited files are new versions. Generated captions update when available without overwriting fields you have manually corrected.

The library searches titles, transcripts, and filenames. Clip actions below it retry speech/import, show the file in Explorer, and delete local entries after confirmation. Retained files that might back Soundpad entries are preserved. The app never removes Soundpad entries as part of local deletion.

## Transcription and Soundpad

Whisper base.en performs local English recognition with four CPU threads. The model is already installed for this Windows account. On a different account use **Settings → Download / repair model** (~148 MB). Download needs internet; recognition does not. No recording is uploaded, and no account/API key is needed.

Captures are saved before recognition or import. Full transcripts remain separate from sanitized, bounded, unique filenames. Transcription failures retain timestamp-named audio and can be retried.

Soundpad integration retains the existing named-pipe commands, dedicated category, verified-path associations, retry queue, duplicate checks, and preservation of imported backing files. The read-only Soundpad diagnostic reports versions, license status, reachability, and sound counts. Trial editions cannot add sounds.

**Verification boundary:** V1 testing observed `R-204: File does not exist` from live DoAddSound despite a locally readable WAV. This playback/UI update does not change that protocol implementation or claim to fix the old import rejection. V1.1 tests cover the integration with a simulator and read-only live queries; live library additions were not forced while the older recorder remained running. Check each clip's actual import status; connection alone never means an import succeeded.

## Background operation and storage

Close/minimize hides the app in the system tray. Capture remains active. Tray commands open, capture, pause, resume, and exit. Pause clears capture; Exit stops the application. Capture starts paused on a fresh launch. Windows startup remains opt-in.

Settings, JSON metadata, the model, and rotating diagnostic logs live in `%LOCALAPPDATA%\Voice Replay Studio`. Default WAV storage is its `Clips` subfolder. Changing storage affects new recordings only. Settings/library formats are compatible with V1. JSON/WAV writes are committed before import; corrupted JSON is reported and preserved.

Preview diagnostics include the selected endpoint, sample rate, region, endpoint/session mute and volume, and asynchronous playback failures. Missing/disconnected outputs and empty/silent regions produce actionable errors. No output volume setter is used.

## Build and diagnostics

Use the local .NET 10 SDK, or install .NET SDK 10 on another Windows development machine:

```powershell
.\Build.ps1
.tools\dotnet\dotnet.exe run --project tests/VoiceReplay.Tests -c Release
```

The build script publishes to `Release/VoiceReplayStudio-V1.1` by default. `-OutputDirectory` can select another destination when needed. The active V1 Release folder is intentionally preserved.

Optional development checks:

- `--live --preview-live --speech .tools/jfk.wav` after the test-project command runs actual loopback lifecycle checks, a brief low-level physical Wave:3 headphone render check, and offline speech recognition. `--preview-live` requires that named hardware and produces sound; the normal suite produces none.
- `--playback-diagnostic` on the test program reads monitor configuration, endpoints, and decoded WAV levels without playing audio.
- `--soundpad-diagnostic` on the test program reads the real pipe owner, versions, license and library counts.
- `Voice Replay Studio.exe --smoke-test` renders the main/settings/inline editor, tests trim/save/undo/revert, uses actual silent process capture to verify mode switches and live refresh, and checks compact layout and native hotkey registration. It uses isolated temporary metadata and exits without touching the real library or generating keyboard input.
- Adding `--test-audio "<explicit render endpoint ID>"` to smoke mode enables one low-level WPF playback/loopback check through that chosen output. It never selects a microphone or modifies routing.

See TEST-RESULTS.md and packaged Verification/Smoke files for evidence and precise hardware/listening limits.

## Dependencies

NAudio 2.2.1, Whisper.net/CPU runtime 1.9.1, .NET 10/WPF. Licenses are included. API references: [NAudio](https://github.com/naudio/NAudio), [Whisper.net](https://github.com/sandrohanea/whisper.net), [Microsoft process loopback](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/), [Soundpad official API](https://www.leppsoft.com/soundpad/en/rc/).
