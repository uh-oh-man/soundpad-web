# 2.3 development verification — 2026-10-30

The completed pass uses isolated temporary data and named-pipe mocks. No real Soundpad library, audible playback, microphone/device settings, running companion, games or Steam were changed.

| Check | Result |
| --- | --- |
| TypeScript, ESLint, production static build | Passed |
| Frontend unit tests | 43 passed |
| Native tests | 64 passed |
| Chromium browser suite with LIVE_RELAY=1 | 25 passed; 1 deployed-site scenario intentionally skipped; final native-original Temp control regression also passed |
| Windows WebKit targeted regression | 9 passed |
| Portable win-x64 self-contained publish and isolated smoke | Passed |
| npm production dependency audit | Zero reported vulnerabilities |
| npm full audit at high severity | Passed threshold; 11 moderate advisories in the development lint dependency tree |
| NuGet dependency audit | No vulnerable packages reported |

The three live Nostr/WebRTC scenarios use actual public relay pairing and isolated native fixtures. They cover guest play/counter synchronization, invitation revocation, independent hosts, compact invites, guest upload acknowledgment, shared layouts and guest Spotify panel visibility. Local browser tests cover Space typing/dialog exclusions, artwork/Play-strip geometry, rapid seek waveform retention, full-tile precision, five views, cue/region metadata, name prompting, crop/persistence, all viewport sizes, separate panel scrolling, local theme persistence, notification expiry/deduplication and persistent inline upload errors. The 10,000-sound test also passed.

Native tests cover exact stereo frame reversal, normal/reversed reuse, confirmed durable promotion, response-loss/import interruption recovery, partial copy resumption, conflict preservation, offline import/reconnect, user `temp` conflicts, legacy reverse chains and approvals, malformed/traversal journals, playing-file and lock protection, atomic ownership race rejection, production orphan fallback, pending-play cancellation by Stop, guest authority/path privacy, and metadata/icon/statistics identity through promotion. Production native deletion remains deliberately deferred because Soundpad does not expose the stronger atomic capability supplied only by the test adapter.

Actual screenshots were inspected at desktop, narrow desktop, tablet and phone viewport sizes, in default and Purple themes, with normal/large artwork and waveform tiles, active playback, Temp and permanent confirmation. Screenshots show generated fixtures. WebKit on Windows and browser viewport emulation are not real Safari or physical-device acceptance.

The release includes source, docs and screenshot evidence. CI uploads the Windows EXE, static build and browser artifacts. The owner approved normal publication after the development review; Pages and the tagged Windows release use the normal workflows. Actual Soundpad imports/audible mic playback, live Spotify transport and physical mobile devices remain unverified acceptance checks.

See [2.3 lifecycle and recovery notes](V2.3.md). Earlier verification records below are historical.

---

# Verification — 2026-10-29

Built and tested locally on Windows 11 Pro x64 (10.0.26200), Node.js 24.16.0 and .NET SDK 10.0.402. The companion is a real self-contained executable. **This is not a claim of public deployment or real Soundpad audio acceptance.**

| Check | Result | Evidence / boundary |
| --- | --- | --- |
| Reproducible npm installation | Passed | `npm ci`, checked-in package-lock.json |
| TypeScript | Passed | `npm run build` includes `tsc --noEmit` |
| ESLint | Passed | `npm run lint`, zero warnings |
| Browser protocol/library tests | **19 passed** | Deterministic Vitest transport: two guests, independent hosts, signatures, guest refresh, isolation, permissions, malformed messages, floods, counter synchronization, duplicate IDs, search/order/duplicates and host-unavailable state |
| Native companion tests | **19 passed** | xUnit: isolated real Windows message-mode pipes, large UTF-8 responses, reconnect, Soundpad restart, XML validation/nesting/indexes, stale-play rejection, durable counters/icons, duplicate receipts, wrong Origin/token and loopback-only binding |
| Chromium browser tests | **8 passed** with live relays enabled, plus **1 deployed-Pages acceptance test passed** | Actual website + native companion bridge + mock Soundpad pipe; see below |
| Static frontend build | Passed | `dist/`, approximately 130 KiB gzipped JavaScript + 4 KiB CSS, plus separate notices; no runtime server required |
| Windows publish | Passed | `artifacts/release/SoundpadWeb.exe`, single EXE with included .NET / ASP.NET Core runtime |
| Published EXE smoke | Passed | `scripts/smoke-exe.ps1`: start real EXE without browser, numeric loopback-only listener owned by that process, Enter shutdown, embedded notices |
| npm audit | Passed | Zero reported vulnerabilities at verification time |
| NuGet audit | Passed | No vulnerable native packages reported by current nuget.org advisories |
| Git diff / source review | Passed | No whitespace errors, missing implementations, real credentials, or personal machine paths in distributed source |

## Browser coverage

The six deterministic Chromium E2E cases exercise the landing state; real loopback authentication; nested category order; duplicate titles; empty categories; cross-category search; correct `DoPlaySound(1)` received by the mock pipe; Stop; counter and icon persistence through browser refresh; permission controls; reset confirmation; and layouts at 1280 × 720, 820 × 1180 and 390 × 844 without horizontal overflow.

The static production bundle was first tested using a synthetic HTTPS origin. After publishing, an additional acceptance test loaded the **actual GitHub Pages site** at https://uh-oh-man.github.io/soundpad-web/ with Chromium's local-network permission granted normally. Its real authenticated `ws://127.0.0.1` connection passed. A separate guest context opened the generated Pages invitation, completed live Nostr/WebRTC pairing, delivered the correct playback command to the isolated mock pipe, and received synchronized counter state. This closes the deployed HTTPS-to-loopback verification gap for this Chromium/Windows configuration.

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
- **Browser policies:** actual Pages-to-loopback and guest pairing passed in automated Chromium on this Windows PC. Other browser versions and managed browser policies still require acceptance; do not disable browser security protections.
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

## Public deployment follow-up

- Public source: https://github.com/uh-oh-man/soundpad-web
- Live website: https://uh-oh-man.github.io/soundpad-web/
- Initial Pages deployment passed: https://github.com/uh-oh-man/soundpad-web/actions/runs/40976631693
- GitHub's Windows/native/browser workflow runs independently; published run results are available in the repository's Actions tab.
- Release builds embed the configured Pages URL, so the normal release EXE opens the website without asking for a URL. `--site` and saved website overrides remain supported.
- The extra deployed acceptance test is opt-in: set `DEPLOYED_URL` to the actual HTTPS site and run `npm run test:e2e -- --grep 'deployed Pages'`. It uses live public relays and an isolated mock Soundpad. Default CI skips it as well as the two live-relay cases.
