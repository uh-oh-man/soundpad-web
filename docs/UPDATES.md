# Updates and session continuity (2.44)

Since 2.44 the Windows companion can update itself from this project's GitHub releases and carry on the session it
was running. This page describes what it checks, what it keeps and what it does when something goes wrong. The user
guide part is in README.md; the restore points are in ROLLBACK.md.

## Where updates come from

- Release builds name the repository with the `UpdateRepository` assembly metadata. Only CI sets it
  (`-p:UpdateRepository=${{ github.repository }}` in `build-windows.yml`). Builds without it (local, dev, forks built
  by hand, the test harness) are `unsupported`: Settings says "This build doesn't update itself" and links the
  releases page.
- `--update-source <url>` replaces GitHub completely, for tests. It is accepted only for a loopback base,
  `http(s)://127.0.0.1:<port>/` or `http(s)://[::1]:<port>/`; anything else is refused with a console line. The
  test server answers `api/releases/latest`, `releases/latest` and `releases/download/vX.Y.Z/<asset>`.
- Rebuilt old releases (`rebuild-old-release.yml`) are created with `--latest=false`, so they never become the
  latest release. The companion also never installs a version that is not newer than itself.

## The check

- At launch (once, a few seconds after the first poll, unless the host turned "Check for updates when the companion
  starts" off) and when the host presses Check again.
- `GET https://api.github.com/repos/<repo>/releases/latest` with `User-Agent: SoundpadWeb-companion`,
  `Accept: application/vnd.github+json` and `X-GitHub-Api-Version: 2022-11-28`; nothing about the person is sent.
  10 s timeout, on a background task.
- A 304, or a 403 or 429 with `x-ratelimit-remaining: 0` or `retry-after`, falls back to
  `HEAD https://github.com/<repo>/releases/latest` and reads its `/releases/tag/vX.Y.Z` redirect (no API size or
  digest then; the size comes from Content-Length). Without a usable redirect: failed, reason `rateLimited`.
- Only a non-draft, non-prerelease tag `vX.Y.Z` strictly newer than the running version counts, with assets named
  exactly `SoundpadWeb.exe` and `SHA256.txt` on GitHub's hosts.
- The EXE's folder must be on a local NTFS drive and writable (a probe file is written and deleted). Otherwise the
  state is `unsupported` with the new version and a "Download it yourself" link.

## Download and checks

HTTPS only, redirects followed by hand and only to `github.com`, `objects.githubusercontent.com` and
`release-assets.githubusercontent.com` (never to http), at most 256 MiB, 30 s without data ends it. The file is
`<exe folder>\.SoundpadWeb.<version>.download`. Then, in this order:

1. Its size equals the asset size (or Content-Length).
2. Its SHA-256 equals `SHA256.txt` (64 hex characters, any case, whitespace trimmed).
3. It equals the API's `sha256:` digest when the API gave one (no digest is "unknown", not a failure).
4. It is a PE file whose ProductVersion is the release's version (`2.44.1` or `2.44.1+<commit>`).
5. A test run of `<download> --licenses` (no window, output read by the companion, 20 s) exits 0 and prints the
   Soundpad notice. Windows or an antivirus that would block the new version blocks this run, while the old version
   is still in place.

The test run happens only after the hash matched. The EXE is not code-signed: these checks prove the file is the
one published with the release, not who built it. Say "checked against the hash published with the release", never
"signed" or "verified publisher".

## Installing

Only on the host's Update now (op `updateInstall {version, confirm: true}`); never by itself. Refused while another
Stage H step registered with `Bridge.BlockUpdatesWhile` is busy (a whole-library restore, an Organize apply).

1. A `pre-update` backup checkpoint (`CheckpointH`). Without a backup folder it is skipped and the dialog's status
   says so; it never blocks the update.
2. `Bridge.EnterUpdateExit`: capture stops with an `update` gap, the recording history is flushed and closed without
   being deleted, the F6 hotkey is released, deferred state is written. Temp, the journal's open session, drafts
   and every guest's join time stay. Nothing of the ordinary quit runs (no ForgetDrafts, CloseHistory,
   CleanupSession or RetireSession).
3. Pages whose hello said `updates: true` get `{type: "updating", version}`; the companion window closes through
   `CloseForExit` (no quit question); the server, the Soundpad pipe and `companion.lock` are released.
4. The hand-over is written (below), `SoundpadWeb.exe` is renamed to `SoundpadWeb.previous.exe` (replacing an older
   one), and the download takes its name. Windows allows renaming a running program on NTFS.
5. The new EXE starts with the original safe flags (`--data-dir`, `--pipe`, `--site`, `--dev`, `--hidden-window`,
   `--no-window`, `--no-browser`, `--update-source`) plus `--resume-update <id> --wait-pid <old pid>`, through
   CreateProcess: no console window, standard output and errors in `<data>\update-child.log`, standard input from
   NUL, never activated. A companion that had its own console (no WebView2, or `--no-window` started by hand) gets
   a new console that starts minimized and is not activated.
6. Watchdog: the old process stays alive, holding nothing, and waits up to 90 s for `update-ready.<id>` with the new
   version. On a non-zero exit, a timeout, a ready file of another version, or a failed move or start, the new
   process is stopped, the failed EXE becomes `SoundpadWeb.failed-<version>.exe`, the previous EXE gets its name
   back, the hand-over is written again from memory, and the old version starts again with `--resume-update`. If
   that fails too, the hand-over is deleted and the next start takes the crash path (Recover or Discard for Temp).

## The hand-over

`<data>\update-handoff.v1.bin`: UTF-8 JSON, at most 1 MiB, protected with DPAPI for the current Windows user (fixed
entropy). It is valid for 5 minutes, for the same data folder, and only for the process the watchdog started (the
writer must have ended or be the `--wait-pid` process). The new process renames it to `.consumed` when it reads it
and deletes that once it is running; a normal start deletes every `update-handoff.v1.bin*`, `update-ready.*`,
`update-child.log`, unfinished `.SoundpadWeb.*.download` and `SoundpadWeb.failed-*.exe`. Keys and tokens are never
logged.

It carries:

- the loopback server: token and port (the old port is tried first, then any), the owner's invitation identity
  (with its private key), session settings and session summary, whether hosting had been ended, and the page that
  ran the session (its place is held for 20 s while pages reconnect; not after End hosting);
- the window: hidden or minimized;
- the Bridge's in-memory session: the recording history's session id, whether capture was running and on which
  source, transcripts on or off, the F6 hotkey's key, length and state, drafts, guests' join times and the guests
  the host disconnected, shared ranges, who is present or away, the companion's own replays, guest budgets and
  grants, voice-library provenance, recent Mimic clips, Kick's session count, Panic, whether the session accepts
  guests and its audience, and revoked peers.

Not carried, rebuilt instead: speaker groups, and the per-launch ids that guests see for words and phrases (a guest
page re-reads the transcript), and the colours of other people's draft bands.

## The new process

With `--resume-update`: it waits up to 30 s for `companion.lock`, reads the hand-over (an unusable one is deleted;
while the watchdog still waits the process exits with code 3 so the previous version comes back, otherwise it starts
as after an unexpected exit), and calls `Bridge.ResumeFromHandoff` instead of RecoverHistory and StartupCleanup:

- `HistoryStore.Open(id, "update")`: the same history with an `update` gap (a gap that was already open, because
  capture had been stopped, keeps its own reason);
- drafts with their pins (a pin that did not survive is made again, or the draft goes);
- joins, forgotten guests and shares exactly as they were, with the old launch's start, so nothing is clamped;
- Temp and the journal's open session: imports are checked again (`Recover` without retiring the session) and there
  is no Recover or Discard question;
- capture starts again only when it was running, on the same source id, and only if the source is still listed
  (outputs are render endpoints; `RequireOutput` still applies). A source that is gone leaves capture off with the
  usual "choose another output". This is the one exception to "capture never starts without a press", see MIMIC.md.
- the F6 hotkey is registered again through its normal path (a key another app took meanwhile is reported).

It writes `update-ready.<id>` after the server started and the first poll ran, then deletes the consumed hand-over.
The window comes back hidden if it was, otherwise with `SW_SHOWNOACTIVATE` (or `SW_SHOWMINNOACTIVE` when it was
minimized), never Show or Activate; WebView2 creation is retried for up to 10 s while the old window's profile is let
go. Without a window, a browser tab opens only when the old port could not be used again.

Pages: a host page reconnects by itself (same port and token; it retries every second while "updating" and counts
revisions afresh on each connection). The companion window's new page gets the identity and settings back, so
guests rejoin with the same invitation and are not asked to join again. An owner page told `ended` (hosting had been
ended before the update) shows Resume hosting instead of starting a new invitation.

## Files

| File | Where | Lifetime |
|---|---|---|
| `update-settings.v1.json` | data folder | `{format: 1, checkAtLaunch}`, at most 4 KiB, strict; unknown fields kept; a damaged file means "check at launch" and is left alone |
| `update-handoff.v1.bin` (`.consumed`) | data folder | seconds; deleted by the new process and by every normal start |
| `update-ready.<id>` | data folder | seconds; deleted by the watchdog and by every normal start |
| `update-child.log` | data folder | the relaunched process's output; deleted by the next normal start |
| `SoundpadWeb.previous.exe` | beside the EXE | until the next update; rename it to `SoundpadWeb.exe` to go back by hand |
| `.SoundpadWeb.<ver>.download`, `SoundpadWeb.failed-<ver>.exe` | beside the EXE | until the next normal start |

## Tests

- xUnit: `UpdateReleaseTests` (check, fallback, redirect allow-list, SHA256.txt, size/hash/digest/version checks,
  test-run refusal, settings file), `UpdateHandoffTests` (DPAPI round trip, every refusal deletes it, leftovers),
  `UpdateSwapTests` (every failing step rolls back and restarts the old version), `UpdateContinuityTests` (one Bridge
  hands its session to the next: same history and gap, drafts, Temp, joins, capture, F7, Panic, Kick; the crash path
  without a hand-over; host-only, strict ops).
- Harness: `--handoff-out` (verb `update-exit`, or Update now in the page, ends the harness through the update path
  and writes the hand-over), `--resume-update` (continues it), verb `update-state <state> [version] [reason]` (shows a
  state without any network).
- `tests/e2e/updates.spec.ts`: an update with a guest connected, an ordinary restart, the UI, the gate, and an update
  while hosting was ended.
- `scripts/update-swap-test.ps1` (Windows PowerShell 5.1; CI runs it after the smoke test): real EXEs, the mock
  Soundpad and a TcpListener fake release server. A good update, a wrong hash, a truncated download, a wrong
  version, a failing test run, and a new version that never becomes ready (rolled back after 90 s); `-Hidden` adds a
  `--hidden-window` run. It reads the token from `--launch-file` (accepted only with `--dev`).

## Not proven

Real GitHub downloads and rate limits; SmartScreen or Smart App Control on a freshly downloaded unsigned EXE; who
built the EXE (no Authenticode, no attestation); Soundpad playback, WASAPI capture or guests' WAN connections
continuing across the swap on real hardware, and the length of the gap there; port rebinding on every machine (a
fallback exists); renaming a running EXE on anything but local NTFS. There is no live binary replacement and no
uninterrupted audio or capture: the companion restarts.
