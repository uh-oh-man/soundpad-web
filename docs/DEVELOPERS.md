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

This connects to real Soundpad if running. Routine browser tests instead launch a completely separate mock pipe via `companion/harness`; fixtures never ship in the EXE. Optional real Nostr/WebRTC browser integration tests: `$env:LIVE_RELAY='1'; npm run test:e2e -- --grep 'live Nostr'`. They still use a mock Soundpad, so no real audio plays. CI's default tests do not depend on public relay availability. The separate **Live relay guest tests** workflow runs them weekly (Mondays), on demand and when its file changes.

Mimic checks (Windows):

```powershell
dotnet run --project mimic/tests/VoiceReplay.Tests -c Release   # Voice Replay Studio's own tests
.\artifacts\release\SoundpadWeb.exe --transcribe-file sample.wav  # local Whisper check (add --download the first time)
companion\harness\bin\Release\net10.0-windows10.0.19041.0\win-x64\SoundpadWeb.Harness.exe --mimic-probe   # real capture levels, nothing saved
```

Advanced companion options: `--site <HTTPS URL>`, `--no-browser`, `--data-dir <local directory>`, `--pipe <test pipe name>`. `--dev` permits HTTP only for a loopback website. Close the companion with Ctrl+C. No LAN listener, firewall changes or system service is installed.

You can also press Enter in the companion window for a clean shutdown. Only one companion may use a given data directory at a time. `--licenses` prints embedded notices without starting a server. Native runtime dependencies are pinned to .NET / ASP.NET Core 10.0.13; update that patch and the bundled notices together when maintaining a fork. NuGet and npm lockfiles are checked in.

## Publish your fork with GitHub Pages

1. Put this source in your GitHub repository and enable Actions.
2. In **Settings → Pages**, choose **GitHub Actions** as the build source.
3. Push to `main` or `master`, or run **GitHub Pages** manually. Its final deployment step reports your actual URL.
4. Download the Windows workflow artifact and launch the EXE. The workflow embeds the default project Pages URL. For a custom domain or user/organization Pages repository, set the repository variable `SOUNDPAD_WEB_URL` to the actual website URL before building, or override it with the companion's `--site` option.

The deployment workflow derives the correct repository base path from Pages configuration, including project pages and custom domains. Local builds use relative asset paths by default. Override `VITE_BASE_PATH` only when needed. Push a `v*` Git tag to build, test and publish an EXE release with a SHA-256 hash. Source builds may embed a default website using `dotnet publish ... -p:WebsiteUrl=https://your-site.example/`; the runtime `--site` override always remains available.

## Network notes

Public [Nostr relays via Trystero](https://trystero.dev/) establish the connection; encrypted WebRTC carries commands/state. No application signaling server is deployed. Public relays are best effort, and restrictive NAT/firewalls may prevent direct WebRTC. No free universal TURN service is assumed. Advanced maintainers may configure their own TURN server at build time using `VITE_TURN_CONFIG` (JSON array of RTCIceServer entries). Anything embedded in a static bundle is public: never put confidential or long-lived third-party credentials there. Use credentials intended for public clients, or your own short-lived credential provisioning outside this project's normal setup.

## Where data is kept

Counter, permissions, statistics, icon references and an opaque-identity key live in `%LOCALAPPDATA%\SoundpadWeb\state.json`. Icon images live beside it in `icons/`; waveform caches live in `waveforms/`. Managed uploads/reversed WAVs and the schema-3 recovery journal live in `managed-audio/`; permanent copies default to `library/` beside it. Legacy `session-audio/` records are backed up before migration. Previously accepted legacy files are preserved until the host confirms durable promotion; do not manually delete them. Small settings are saved atomically; unreadable data stops startup instead of silently resetting your counter. Browser refresh retains the session within the same host tab. Restarting the EXE or ending the host tab starts a new invitation session. Other people's hosts have separate state and secrets.

## Technical notes and limitations

See [vendor API research](API-RESEARCH.md), [protocol/security design](../shared/protocol/README.md), and [test evidence](VERIFICATION.md).

Icons follow an HMAC of the vendor file URL and duplicate occurrence. Unique paths survive reordering; moving/renaming a file changes identity, and exact duplicate paths cannot be perfectly distinguished. Soundpad does not offer an atomic persistent-ID play command, leaving a narrow last-query/play reorder race. Now-playing titles are deliberately not guessed from the last browser click: manual Soundpad playback remains usable.

Browser background throttling or a suspended mobile host tab can interrupt hosting. The EXE does not independently maintain WebRTC when the host tab is closed. HTTPS-to-loopback policies vary by browser; passing local HTTP tests is not proof of GitHub Pages compatibility. The EXE is unsigned; this project does not install a certificate or modify your system trust settings.
