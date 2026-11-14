# Developers and maintainers

Moved out of the README in 2.12.3, unchanged apart from links. See also [CLAUDE.md](../CLAUDE.md) (maintainer notes), [Mimic](MIMIC.md) and [rolling back](ROLLBACK.md).

## Build from source

Developer requirements: Node.js 24, .NET 10 SDK, Windows for native tests/publishing. End users need neither Node nor the .NET SDK.

```powershell
npm ci
npm run lint
npm test
npm run build
dotnet test companion/tests/SoundpadWeb.Tests.csproj -c Release
dotnet build companion/harness/SoundpadWeb.Harness.csproj -c Release
npx playwright install chromium
npm run test:e2e
# Safari's engine, as CI also runs it:
npx playwright install webkit
$env:TEST_BROWSER='webkit'; npx playwright test --output test-results/webkit
dotnet publish companion/SoundpadWeb.Companion.csproj -c Release -r win-x64 --self-contained true -o artifacts/release
```

`dist/` is the fully static website. `artifacts/release/SoundpadWeb.exe` is the standalone executable. The normal Windows workflow builds both and uploads them. Linux can build/test the frontend and publish Windows code with suitable targeting, but native pipe/image tests require Windows.

For development, run `npm run dev`, then:

```powershell
.\artifacts\release\SoundpadWeb.exe --dev --site http://127.0.0.1:5173/
```

This connects to real Soundpad if running. Routine browser tests instead launch a completely separate mock pipe via `companion/harness`; fixtures never ship in the EXE. Optional real Nostr/WebRTC browser integration tests: `$env:LIVE_RELAY='1'; npm run test:e2e -- --grep 'live Nostr'`. They still use a mock Soundpad, so no real audio plays. CI's default tests do not depend on public relay availability, and since 2.41.0 they do not connect to the relays at all: a fixture refuses every `wss://` connection unless `LIVE_RELAY` or `DEPLOYED_URL` is set (in Playwright's Windows WebKit, a host page's relay connections held up its companion socket for seconds). The separate **Live relay guest tests** workflow runs them weekly (Mondays), on demand and when its file changes.

Responsiveness under load (2.41): `SoundpadWeb.Harness.exe --playback-bench --slow-work [--transcribe-burn-ms N]` runs the 2.35 playback bench against the mock with keep-pitch copies, previews, Mimic recording with simulated recognition load and Panic presses, and reports how long the receive loop was held and how fast Stop and Panic were answered.

Sentences (2.42): matching is `companion/src/SentenceComposer.cs` (a seeded dynamic program over phrases of up to 8 words), rendering `SentenceRender.cs` (only library recordings, pauses and bleeps; nothing else can make sound), storage and validation `SentenceModel.cs`, and the ops `BridgeSentences.cs`; the website side is `web/src/library/sentences.ts` (pure rules), `sentence-store.ts` and `components/SentencesPanel.tsx`. Every sentence and palette op leaves the receive loop (`Bridge.Offloaded`) after its intent is reserved, and every render, encode, preview and kept-audio read runs on the AudioWorker (`Heavy`), with game renders one at a time and cancelled by a newer intent. Test seams: `Bridge.SentenceRenderHold`, `SentenceStore.SaveLimit`, `UseSentenceWriteLimit`. User guide: [SENTENCES.md](SENTENCES.md); protocol: [shared/protocol/README.md](../shared/protocol/README.md#sentences-and-the-speaking-palette-242).

The studio layout (2.43): `web/src/components/shell/` holds the app shell (`AppShell`, `TopBar`, `SectionNav`, `SectionHeader`, `SectionView`, `EditorPanel` with `Splitter`, `SoundEditor`, `ActivityView`, `HeaderRibbon`, `Landing`, `DemoBanner`); `shell.ts` carries editor and view requests as window events (`requestEditor`, `showView`), `sections.ts` remembers each section's place, and `prefs.ts` / `motion.ts` keep the appearance choices. The header ribbon is `web/src/ribbon/` (`envelope.ts`, `machine.ts`, `draw.ts`; pure, unit tested). Host style sync is `web/src/ui-sync.ts` with the companion's `UiPrefs.cs` (`<data>\ui.json`, version 1, at most 4 KiB, strictly validated input, unknown keys kept, written through a temporary file) and `BridgeUi.cs` (`uiPrefsGet` / `uiPrefsSet`, host only, raising the snapshot's `uiRevision`). The Kick session count is `counterSession` in the snapshot, kept in memory. Edited and speed copies take a `TrimSource` (`{ assetId }` for Temp audio or `{ soundId }` for any Soundpad sound, `BridgeTrim.cs`). The example (`web/src/demo/`) reports companion `DEMO_COMPANION` and answers every command the website sends up to that version; `tests/unit/demo-coverage.test.ts` checks it, so raise `DEMO_COMPANION` only together with the example's support for the new commands. `tests/unit/demo-engine.ts` drives the example's model in unit tests. Shared e2e steps are in `tests/e2e/helpers/` (`harness.ts`: the fixture, which refuses public relays and adds every page's notices to a failure; `shell.ts`; `playback.ts`). `tests/shots/` holds manual screenshot and ribbon performance probes (`playwright.shots.config.ts`); they assert nothing and are not part of CI.

Stylesheets load in this order from `main.tsx`: `tokens.css`, the DM Sans font (`@fontsource-variable/dm-sans`, self-hosted), `studio.css` (the older rules, consolidated), `controls.css` (shared controls and focus), `mimic.css`, `voices.css`, `sentences.css`, `shell.css`, `playback.css`, `surfaces.css`. Each later file overrides earlier ones at equal specificity; the comment at the top of each says which components it owns. Keyboard focus is kept clear of the notices, the player and the phone sheet's bar by scroll-padding that counts `--player-height` and `--notice-height` (published by `Player.tsx` and `Notifications.tsx`), and clear of the phone's sticky header and a dialog's sticky title row by scroll-padding-top that counts `--topbar-bottom` and `--dialog-title-height` (published by `TopBar.tsx` and `Modal.tsx`), with `web/src/focus-clear.ts` as the safety net for what scroll-padding cannot reach.

Mimic checks (Windows):

```powershell
dotnet run --project mimic/tests/VoiceReplay.Tests -c Release   # Voice Replay Studio's own tests
.\artifacts\release\SoundpadWeb.exe --transcribe-file sample.wav  # local Whisper check (add --download the first time)
companion\harness\bin\Release\net10.0-windows10.0.19041.0\win-x64\SoundpadWeb.Harness.exe --mimic-probe   # real capture levels, nothing saved
```

Advanced companion options: `--site <HTTPS URL>`, `--no-browser`, `--no-window` (2.35 behaviour: a console and the default browser, whose tab hosts), `--hidden-window` (the window and its WebView2 run but are never shown; for automated checks on a PC someone is using), `--data-dir <local directory>`, `--pipe <test pipe name>`. `--dev` permits HTTP only for a loopback website. No LAN listener, firewall changes or system service is installed.

Since 2.36 the EXE is a desktop app (WinExe): closing its window quits it with the usual cleanup. Started from a terminal it writes there, and Ctrl+C stops it; Enter stops it only when standard input is a pipe (scripts, `scripts/smoke-exe.ps1`) or the console it opens itself in `--no-window` mode. Output is also written to `<data>companion.log` (rotated at 1 MiB). The window remembers its place in `<data>window.json`, and WebView2 keeps its profile in `<data>webview2`. The window page is the session owner: it gets a per-launch owner key in its link (never saved) and the companion keeps the host identity in memory, so a reload keeps the invitation. Other host pages are views. The harness accepts `--owner-key <64 hex>` to stand in for the window in e2e tests. Only one companion may use a given data directory at a time. `--licenses` prints embedded notices without starting a server. Native runtime dependencies are pinned to .NET / ASP.NET Core 10.0.13; update that patch and the bundled notices together when maintaining a fork. NuGet and npm lockfiles are checked in.

## Publish your fork with GitHub Pages

1. Put this source in your GitHub repository and enable Actions.
2. In **Settings → Pages**, choose **GitHub Actions** as the build source.
3. Push to `main` or `master`, or run **GitHub Pages** manually. Its final deployment step reports your actual URL.
4. Download the Windows workflow artifact and launch the EXE. The workflow embeds the default project Pages URL. For a custom domain or user/organization Pages repository, set the repository variable `SOUNDPAD_WEB_URL` to the actual website URL before building, or override it with the companion's `--site` option.

The deployment workflow derives the correct repository base path from Pages configuration, including project pages and custom domains. Local builds use relative asset paths by default. Override `VITE_BASE_PATH` only when needed. Push a `v*` Git tag to build, test and publish an EXE release with a SHA-256 hash. Source builds may embed a default website using `dotnet publish ... -p:WebsiteUrl=https://your-site.example/`; the runtime `--site` override always remains available.

## Network notes

Public [Nostr relays via Trystero](https://trystero.dev/) establish the connection; encrypted WebRTC carries commands/state. No application signaling server is deployed. Public relays are best effort, and restrictive NAT/firewalls may prevent direct WebRTC. No free universal TURN service is assumed. Advanced maintainers may configure their own TURN server at build time using `VITE_TURN_CONFIG` (JSON array of RTCIceServer entries). Anything embedded in a static bundle is public: never put confidential or long-lived third-party credentials there. Use credentials intended for public clients, or your own short-lived credential provisioning outside this project's normal setup.

## Where data is kept

Counter, permissions, statistics, icon references and an opaque-identity key live in `%LOCALAPPDATA%\SoundpadWeb\state.json`. Icon images live beside it in `icons/`; waveform caches live in `waveforms/`. Managed uploads/reversed WAVs and the schema-3 recovery journal live in `managed-audio/`; permanent copies default to `library/` beside it. Since 2.41 `library/` also holds the voice library: `voice-library.v1.json` (the previous version is kept as `voice-library.v1.bak.json` on each save; a save that would exceed 64 MiB is refused), `voice-words.v1.jsonl` (recording words), `voice-audit.v1.jsonl` (who changed what; rotated to `voice-audit.v1.old.jsonl` past 32 MiB) and `sound-words.v1.jsonl` (the category word index, keyed by path, size and modification time). They are separate from `state.json` and the journal, whose formats did not change. Since 2.43 `ui.json` beside `state.json` holds the host's shared style and open Mimic draft (see above); a missing, damaged or oversized file reads as the defaults and is left on disk until the next change replaces it. Since 2.42 `library/` also holds sentences: `sentences.v1.json` (sentences with their versions, named palette pieces and performance sets per person; strictly validated on load and before every save, written through `.tmp` and `File.Replace` keeping `sentences.v1.bak.json`, a save over 64 MiB refused, a damaged main file falling back to the backup and both damaged blocking all writes) and `sentences/<id>/v<N>-<hash8>.wav` (each kept version's audio, written once, its SHA-256 recorded in the version). `state.json` and the journal are again unchanged. Guest browsers keep their identity key in IndexedDB (`soundpad-web-keys`, store `keys`, record `guest-v1`; the private key is non-extractable). Legacy `session-audio/` records are backed up before migration. Previously accepted legacy files are preserved until the host confirms durable promotion; do not manually delete them. Small settings are saved atomically; unreadable data stops startup instead of silently resetting your counter. Browser refresh retains the session within the same host tab. Restarting the EXE or ending the host tab starts a new invitation session. Other people's hosts have separate state and secrets.

## Technical notes and limitations

See [vendor API research](API-RESEARCH.md), [protocol/security design](../shared/protocol/README.md), and [test evidence](VERIFICATION.md).

Icons follow an HMAC of the vendor file URL and duplicate occurrence. Unique paths survive reordering; moving/renaming a file changes identity, and exact duplicate paths cannot be perfectly distinguished. Soundpad does not offer an atomic persistent-ID play command, leaving a narrow last-query/play reorder race. Now-playing titles are deliberately not guessed from the last browser click: manual Soundpad playback remains usable.

With `--no-window` (or without the WebView2 Runtime), browser background throttling or a suspended host tab can interrupt hosting, and the EXE does not independently maintain WebRTC when the host tab is closed. HTTPS-to-loopback policies vary by browser; passing local HTTP tests is not proof of GitHub Pages compatibility. The EXE is unsigned; this project does not install a certificate or modify your system trust settings.
