# Verification — 2026-10-29

Built and tested locally on Windows 11 Pro x64 (10.0.26200), Node.js 24.16.0 and .NET SDK 10.0.402. The companion is a real self-contained executable. **This is not a claim of public deployment or real Soundpad audio acceptance.**

| Check | Result | Evidence / boundary |
| --- | --- | --- |
| Reproducible npm installation | Passed | `npm ci`, checked-in package-lock.json |
| TypeScript | Passed | `npm run build` includes `tsc --noEmit` |
| ESLint | Passed | `npm run lint`, zero warnings |
| Browser protocol/library tests | **19 passed** | Deterministic Vitest transport: two guests, independent hosts, signatures, guest refresh, isolation, permissions, malformed messages, floods, counter synchronization, duplicate IDs, search/order/duplicates and host-unavailable state |
| Native companion tests | **19 passed** | xUnit: isolated real Windows message-mode pipes, large UTF-8 responses, reconnect, Soundpad restart, XML validation/nesting/indexes, stale-play rejection, durable counters/icons, duplicate receipts, wrong Origin/token and loopback-only binding |
| Chromium browser tests | **8 passed** with live relays enabled | Actual website + native companion bridge + mock Soundpad pipe; see below |
| Static frontend build | Passed | `dist/`, approximately 130 KiB gzipped JavaScript + 4 KiB CSS, plus separate notices; no runtime server required |
| Windows publish | Passed | `artifacts/release/SoundpadWeb.exe`, single EXE with included .NET / ASP.NET Core runtime |
| Published EXE smoke | Passed | `scripts/smoke-exe.ps1`: start real EXE without browser, numeric loopback-only listener owned by that process, Enter shutdown, embedded notices |
| npm audit | Passed | Zero reported vulnerabilities at verification time |
| NuGet audit | Passed | No vulnerable native packages reported by current nuget.org advisories |
| Git diff / source review | Passed | No whitespace errors, missing implementations, real credentials, or personal machine paths in distributed source |

## Browser coverage

The six deterministic Chromium E2E cases exercise the landing state; real loopback authentication; nested category order; duplicate titles; empty categories; cross-category search; correct `DoPlaySound(1)` received by the mock pipe; Stop; counter and icon persistence through browser refresh; permission controls; reset confirmation; and layouts at 1280 × 720, 820 × 1180 and 390 × 844 without horizontal overflow.

The static production bundle was also loaded using a **synthetic HTTPS origin** intercepted by Playwright, with Chromium's local-network permission granted normally. Its real authenticated `ws://127.0.0.1` connection and counter command passed. This tests the secure-origin browser path, but is not an actual deployed GitHub Pages test.

Two optional live Nostr/WebRTC tests passed. Separate browser contexts used the real Trystero Nostr strategy/public relays, actual WebRTC transport, host signatures and application acknowledgments:

1. One host and two guests: join, command delivery to the mock pipe, guest counter permissions, counter synchronization, guest refresh, host refresh with both guests reconnecting, custom-icon synchronization to both guests, invitation replacement and explicit access revocation.
2. Two independent native companions/host sessions with one guest each: unrelated invitations, counter isolation and playback delivered only to the intended host's mock pipe.

All contexts ran on the same Windows PC. These tests establish live public signaling and browser-to-browser transport on this machine, not arbitrary internet/NAT reliability. Fixtures are schema-derived and never appear in normal production operation. No actual audio played.

Firefox 157 / Playwright Firefox 1555 was installed and attempted. All eight cases failed **before page execution** because Windows returned `browserType.launch: spawn UNKNOWN`. No Firefox functionality is verified. The failure was not bypassed by weakening Windows/browser security. Reproduce on another machine with `npx playwright install firefox`, then `$env:TEST_BROWSER='firefox'; npm run test:e2e`. Real Safari, Edge as a separately launched product, physical phones/tablets, and restrictive network pairs were not tested.

## Defects found and repaired during verification

- Native validation exceptions escaped instead of returning a useful command rejection.
- WebSocket close frames did not complete the closing handshake.
- Controlled permission toggles reverted visually before the native acknowledgment.
- A first rejected command could reload an unsaved identity key, breaking icon associations; the identity seed is now persisted on first load.
- Relayed native commands now use a fixed guest actor rather than treating a peer-provided ID as an authorization role.
- Extreme command timestamps are rejected without signed-integer overflow.
- Initial tooling failures (dependency resolution, browser installation and an invalid PNG test fixture) were repaired. No failing application test was disabled.

## Remaining acceptance work

- **Real Soundpad:** none was running. Steam/standalone actual library captures and audible playback through existing speaker/microphone routing remain unverified. Tests used a private mock pipe. No games, Steam, Soundpad, audio devices, drivers, firewall rules or registry settings were changed.
- **Deployment:** the public repository [uh-oh-man/soundpad-web](https://github.com/uh-oh-man/soundpad-web) has now been created and Pages configured through the user's browser-authorized GitHub CLI connection. Workflow/deployment results are recorded after verification; initial local-only results above do not themselves prove deployment.
- **Real HTTPS hosting:** test the companion from the eventual GitHub Pages/custom-domain URL in the intended host browser. Loopback permission policies vary.
- **Devices/networks:** physical mobile browsers, multi-PC WAN conditions, TURN configuration and browser background/sleep recovery need acceptance on the intended devices/networks. Public relays have no availability guarantee.
- **Vendor limitation:** Soundpad has no atomic play-by-persistent-ID transaction. The bridge revalidates immediately before playback but cannot eliminate a reorder between the last query and command. Moving files or identical duplicate URLs can change icon identity associations; see API research.

## Artifacts and reproduction

- `artifacts/release/SoundpadWeb.exe` — portable Windows x64 companion.
- `artifacts/release/SHA256.txt` — SHA-256 for the built EXE.
- `dist/` and `artifacts/SoundpadWeb-static.zip` — static website.
- `artifacts/SoundpadWeb-source.zip` — distributable source, tests, workflows, licenses and documentation; excludes dependency/build folders, local state and credentials.
- `docs/screenshots/` — screenshots of the actual application using clearly identified fixture data.
- `.github/workflows/deploy.yml` and `.github/workflows/build-windows.yml` — Pages deployment, native/browser CI and tagged EXE releases.

Default CI uses deterministic tests and skips the two live-relay tests. To include them locally: `$env:LIVE_RELAY='1'; npm run test:e2e`. See the README for full build commands.
