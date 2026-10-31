# Mimic

Mimic turns something that was *just said* into a sound. The host's companion keeps the last 30 seconds of one app or output device in memory. Anyone allowed to use Mimic can open the **Mimic** window, see that audio as a live waveform, select a moment, and play it back through Soundpad.

Mimic came from a separate desktop project, **Voice Replay Studio** (a rolling "instant replay" recorder with a clip editor, Whisper transcription and Soundpad import). As of 2.4.0 both projects live in this repository:

| Part | Where | What it is |
| --- | --- | --- |
| Mimic in Soundpad Web | `companion/src/MimicCapture.cs`, `companion/src/BridgeMimic.cs`, `web/src/components/MimicPanel.tsx` | The browser feature: host chooses a source, guests clip from it. |
| Voice Replay Studio | `mimic/` | The original Windows desktop app (V1.1), unchanged, with its own README and tests. |

The capture engine in the companion is a port of Voice Replay Studio's `CaptureService`, rewritten for the companion's NAudio 3.3. NAudio 3.3 includes process-loopback activation, so the hand-written COM interop is no longer needed. The desktop app keeps its own copy on NAudio 2.2.1 and Whisper.net.

## Using it

**Host**

1. Click **Mimic** above the soundboard.
2. Under **Listen to**, choose an **Output** (everything that device plays) or an **App** (that program and its child processes, e.g. Discord).
3. Click **Start listening**. A pulsing **Mimic on** badge appears in everyone's header.
4. To let guests clip, turn on **Session & access → Guest Mimic**. It is off by default.
5. **Stop listening**, **End hosting**, or closing the companion all stop capture and drop the buffer.

**Anyone allowed**

1. Open **Mimic** (or click the **Mimic on** badge).
2. Drag across the waveform, or use **Last 3 s / 5 s / 10 s**. A selection freezes the view so the moment stays put.
3. Optionally name it, then **Mimic it** (adds it to Temp) or **Mimic & play** (adds it and plays it through Soundpad).

The clip becomes a normal Temp sound, so everything Temp already does applies: reverse, **Keep permanently** with host confirmation, and cleanup at End Hosting.

## Privacy and safety rules

- **Off until the host starts it.** Capture never starts on launch and is never restored after a restart.
- **No microphones.** Only output devices and app loopback are offered. Mimic cannot listen to the host's room.
- **Everyone can see it.** While capture runs, every participant's header shows **Mimic on** with the source name.
- **Audio stays on the host PC.** Guests receive only 300 peak levels for the waveform. A clip is cut on the companion and imported locally, like any Temp upload.
- **Memory only, bounded.** The buffer holds 45 seconds (the 30 s window plus 15 s of grace so a selection survives the round trip from a guest). Nothing is written to disk unless someone clips, and clips follow the existing Temp limits.
- **Separate permission.** Guest clipping needs **Guest Mimic**; playing the result also needs **Remote playback**. Only the host can choose or change the source.
- **Be fair to the people you record.** Mimic captures other people's voices. Tell your session it is on; the badge exists for that reason.

## Protocol

New command ops: `mimicSources`, `mimicStart`, `mimicStop` (host only), `mimicPeek` and `mimicClip` (host, or guests with `guestMimic`). New fields: `sourceId`, `mimicEnd`, `name`. New permission: `guestMimic` (default `false`). Snapshots gain an optional `mimic` object (`active`, `source`, `status`, `windowMs`).

`mimicPeek` returns `end`, an absolute frame counter for the right edge of the window. `mimicClip` sends that `end` back with `positionMs`/`endMs` offsets inside the window. The companion therefore cuts exactly the samples the person saw, even though capture kept running. If those samples have been overwritten, the clip is refused rather than shifted to newer audio, the same rule Voice Replay Studio used.

Requires companion 2.4. Older companions ignore the new permission, and the website hides Mimic for them.

## Testing

- `companion/tests/MimicTests.cs`: ring buffer addressing and expiry, permissions, exact clip length, silence and pre-start trimming.
- `tests/unit/mimic.test.ts`: selection math and protocol validation.
- `tests/e2e/app.spec.ts` ("Mimic: …"): the full host flow against `MimicFixture`, a test-only source that plays a tone every 2 seconds. An opt-in `LIVE_RELAY=1` test covers a real guest.
- `SoundpadWeb.Harness.exe --mimic-probe [source]` listens to a real device or app for 2 seconds and prints only frame counts and the peak level, to check capture on a given PC.

## Not yet included

Voice Replay Studio features that are not in the browser version yet: the Whisper transcript and caption search, the clip trim editor, and the F6 global hotkey. They remain available in the desktop app under `mimic/`.
