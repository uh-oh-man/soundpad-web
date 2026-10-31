# Voice Replay Studio V1.1 — verification report

Verified on 2026-10-19: Windows 11 Pro build 26200, Ryzen 7 5800X, RTX 4070 Ti, approximately 128 GB RAM.

## Playback root cause and repair

The saved monitor setting was empty. The old preview filter also rejected all device names containing "Wave Link" or "virtual", including valid monitor outputs.

A real output test then reproduced the engine fault: **"Source array type cannot be assigned to destination array type."** NAudio's SampleToWaveProvider passes a WaveBuffer float view backed by a byte array. Array.Copy in the application's sample provider checked the runtime array types and threw during audio rendering. The waveform/file decoder could still work, which is why clips looked valid but preview produced no sound.

Both sample providers now write samples through the float view directly. A regression test uses the actual NAudio byte-provider adapter, not an ordinary float-array substitute. Runtime playback errors now stop the playing indication, remain visible, and are logged.

The existing reference WAV was decoded as 176,000 non-silent mono samples at 16 kHz, exactly 11 seconds, peak 0.7827. This establishes that the inspected file itself was not empty/silent.

The main window now has an independent monitor selector, a low-level Test Playback tone, and endpoint/application-session mute and volume diagnostics. No audio settings are changed. Physical headphones and actual Elgato Monitor outputs are accepted; known outgoing voice-chat/mixer buses are rejected.

## Automated regression/integration suite: 55 passed, 0 failed

Command:

```powershell
.tools\dotnet\dotnet.exe run --project tests/VoiceReplay.Tests -c Release -- --live --preview-live --speech .tools/jfk.wav
```

Coverage includes all previous ring, WAV, filename, persistence, transcription failure, Soundpad simulator/transport, capture burst, and import-association regressions, plus:

- Exact PCM decoding and NAudio WaveBuffer alias compatibility for both source providers.
- Output selection policy and selected endpoint forwarding.
- Empty, reversed, out-of-range and silent regions rejected with actionable errors.
- Playback initialization failure, asynchronous device failure, session mute, position, selection loop boundaries and seeking within the original region.
- Historical buffer selections retain their original absolute sample indices as newer audio arrives. Expired regions and restarted buffer identities are detected.
- Warmup silence appears on the left; newest data stays at the right edge.
- Cached min/max summaries match full-PCM extrema exactly after non-aligned ring wraparound.
- Buffer writes continue while clip files and live views are read.
- Selected historical audio saves to an exact 500 ms WAV and restores from persistent metadata.
- 30-second / 1,200-bin waveform aggregation benchmark: approximately **0.50 ms per frame** in the recorded run. The full-quality ring remains separate from the display summaries.

Four capture/transcription live checks additionally verify device/process initialization, stop/pause behavior including cancellation during activation, unchanged default device IDs, and actual offline Whisper recognition. These are included in the 55 count.

The physical playback check is also included: a 400 ms, 660 Hz tone at peak 0.035 was rendered through **Headphones (2- Elgato Wave:3)**. The playback cursor advanced, and simultaneous process loopback captured the test signal at peak 0.0350. Monitoring stayed active during playback. Endpoint mute/volume and default render/communications microphone IDs remained unchanged. The app's observed playback session was unmuted at 100%; endpoint volume was 87%. This is automated evidence that the renderer produced the expected data on the chosen endpoint, not a human listening confirmation.

## Actual WPF desktop checks

The main window, settings, and inline clip editor were rendered and visually inspected. The separate popup editor has been removed.

- Actual inline trim to 500 ms, undo to 1 second, saved WAV re-read, original revert, and Live Buffer/Clip Editor mode switches passed.
- A real Windows process-loopback client remained running while the WPF editor was used. The same ring instance continued receiving samples through mode switches.
- Desktop testing exposed an STA/MTA COM client error that console-only tests had missed. Activation, initialization, reads and release now stay on the background MTA; the GUI lifecycle check passes.
- Live display uses one background 25 FPS producer and one latest prepared frame. It cannot build an unbounded render queue. Cached ruler drawings and batched waveform geometry reduced desktop redraw work; the inspected smoke run measured **25.5 displayed frames/second**.
- A 1000×850 layout check retains the waveform, library and clip actions.
- The published self-contained WPF executable also passed actual Test Playback through the explicitly selected Wave:3 output: cursor progression and the tone in simultaneous own-process loopback were verified. This exercises the UI/STA playback path as well as the console/MTA renderer.
- Native hotkey registration, own-window message dispatch and conflicting-registration rejection passed. No global key press was synthesized.
- Offscreen smoke metadata is isolated and cleaned up by the application. The older running V1 recorder was not stopped or replaced.

Published-package smoke results, screenshots, native playback evidence and runtime paths are included under Smoke/ and Verification/. The self-contained runtime and native Whisper DLLs must remain beside the EXE.

## Soundpad scope

The Soundpad implementation was preserved. Existing simulator tests still cover rejection handling, duplicate prevention, delayed verification, reconnect/retry, and retained backing files. The real named pipe was queried read-only: Soundpad 4.0.35, RC 1.1.2, IsTrial=0, reachable; 50 sounds and four categories were observed. This session did not add/remove/replace live Soundpad entries.

The V1 report's DoAddSound R-204 rejection is not declared fixed by this playback/UI task. Live import mutations were not repeated while the old recorder was active. The application continues to show actual per-clip import state rather than inferring success from connection status.

## Remaining listening/hardware acceptance

- The user must confirm the tone/clip is heard at the intended physical headphones and that their existing mixer does not feed that output into outgoing chat. No microphone playback API is used and no routing is changed by this app.
- Fullscreen-game hotkeys and physical unplug/replug behavior were not forced. No games or services were launched/restarted.
- The 25.5 FPS measurement is from a real offscreen WPF window; visible on-screen cadence and subjective smoothness should be confirmed on the user's display.
- No live Soundpad import success is claimed in this session.

The Release folder is versioned V1.1 because the V1 executable was already running. Exit the old version through its tray menu before launching V1.1; settings and clip metadata are shared and compatible.
