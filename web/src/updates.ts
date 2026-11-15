import { useSyncExternalStore } from 'react';
import { z } from 'zod';
import { command, type Command, type UpdateSnapshot } from './protocol';

/**
 * The companion's self-update (2.44, gate updater): what each state shows, the bodies of the update
 * ops, and the launch offer's once-per-launch rule. Pure, so tests/unit/updates.test.ts covers it.
 * The companion decides everything; this only words it. See docs/UPDATES.md.
 */

/** The host page's own status line while the companion restarts for an update (local.ts). */
export const UPDATING_STATUS = 'Soundpad Web is updating; reconnecting…';
/** What guests see meanwhile (the host page passes it on as the companion's error). */
export const GUEST_UPDATING = 'The host is updating Soundpad Web; reconnecting…';

const version = z.string().regex(/^\d{1,5}\.\d{1,5}\.\d{1,6}$/);
/** Each op's body, checked here before it is sent (the companion checks again, strictly). */
export const updateBodies = {
  updateStatus: z.object({}).strict(),
  updateCheck: z.object({}).strict(),
  updateDownload: z.object({ version }).strict(),
  updateInstall: z.object({ version, confirm: z.literal(true) }).strict(),
  updateLater: z.object({}).strict(),
  updateSettings: z.object({ checkAtLaunch: z.boolean() }).strict(),
} as const;
export type UpdateOp = keyof typeof updateBodies;

/** A Stage H update command with its body, refused here if the body is wrong. */
export function updateCommand<Op extends UpdateOp>(
  op: Op,
  body: z.input<(typeof updateBodies)[Op]>,
): Command {
  return command(op, { body: updateBodies[op].parse(body) });
}

export const statusReplySchema = z.object({
  update: z.object({ state: z.string() }).passthrough(),
  checkAtLaunch: z.boolean(),
});

/** Plain words for a reason the companion gives (UpdateReasons in the companion). */
export function reasonText(reason?: string, error?: string): string {
  switch (reason) {
    case 'rateLimited':
      return "GitHub's limit for update checks was reached. Try again in an hour, or download it yourself.";
    case 'hashMismatch':
    case 'digestMismatch':
      return 'The download does not match the hash published with the release, so it was not installed.';
    case 'sizeMismatch':
      return 'The download was incomplete, so it was not installed. Try again.';
    case 'versionMismatch':
      return 'The download is not the version it should be, so it was not installed.';
    case 'blocked':
      return 'The new version would not start on this PC (Windows or an antivirus blocked it). The current version stays.';
    case 'notWritable':
      return 'Soundpad Web cannot write to its own folder, so it cannot update itself there.';
    case 'notNtfs':
      return 'Soundpad Web can update itself only from a folder on a local NTFS drive.';
    case 'tooLarge':
      return 'The new version is larger than the updater accepts.';
    case 'noAssets':
      return 'That release has no Soundpad Web EXE to install.';
    case 'noRepository':
    case 'noExe':
      return "This build doesn't update itself.";
    case 'busy':
      return error || 'Another task is running. Try again when it is done.';
    case 'rolledBack':
      return "The new version didn't start, so the previous one came back.";
    case 'network':
      return error || 'GitHub could not be reached. Check the internet connection and try again.';
    default:
      return error || 'The update did not work.';
  }
}

export type UpdateCopy = {
  /** One line for Settings and the dialog. */
  line: string;
  /** A newer version that can be installed now (Update now / Update to vX). */
  installable: boolean;
  /** Something is running: buttons are aria-disabled, never disabled. */
  busy: boolean;
  /** "Download it yourself" is offered (failures, and builds or folders that cannot update). */
  downloadYourself: boolean;
  /** Bytes so far and in all, while downloading or verifying. */
  progress?: { bytes: number; total?: number };
};

const mb = (bytes: number) => `${(bytes / 1048576).toFixed(bytes < 10485760 ? 1 : 0)} MB`;

/** What a state looks like to the host. Promises no duration. */
export function updateCopy(u: UpdateSnapshot): UpdateCopy {
  const v = u.latest ? `v${u.latest}` : 'the new version';
  const base = { installable: false, busy: false, downloadYourself: false };
  switch (u.state) {
    case 'idle':
      return { ...base, line: `Soundpad Web ${u.current} is the newest version.` };
    case 'checking':
      return { ...base, busy: true, line: 'Checking for updates…' };
    case 'available':
      return { ...base, installable: true, line: `Soundpad Web ${u.latest} is available.` };
    case 'downloading':
      return {
        ...base,
        busy: true,
        line:
          u.total && u.bytes !== undefined
            ? `Downloading ${v}: ${mb(u.bytes)} of ${mb(u.total)}`
            : `Downloading ${v}…`,
        progress: { bytes: u.bytes ?? 0, total: u.total },
      };
    case 'verifying':
      return {
        ...base,
        busy: true,
        line: `Checking ${v} against the hash published with the release…`,
        progress: { bytes: u.bytes ?? 0, total: u.total },
      };
    case 'ready':
      return {
        ...base,
        installable: true,
        line: `${v} is downloaded and checked, ready to install.`,
      };
    case 'installing':
      return { ...base, busy: true, line: `Installing ${v}: the companion restarts by itself.` };
    case 'failed':
      return {
        ...base,
        installable: !!u.latest && u.reason !== 'blocked',
        downloadYourself: true,
        line: reasonText(u.reason, u.error),
      };
    case 'unsupported':
      return {
        ...base,
        downloadYourself: true,
        line: u.latest
          ? `Soundpad Web ${u.latest} is available. ${reasonText(u.reason, u.error)}`
          : reasonText(u.reason ?? 'noRepository', u.error),
      };
  }
}

/** The release page to download from by hand: the companion's, else the project's. */
export function downloadUrl(u: UpdateSnapshot | undefined, projectUrl: string) {
  return u?.notesUrl || `${projectUrl}/releases/latest`;
}

/**
 * The launch offer (once per launch): for a host page on a companion with the updater, while a newer
 * version is offered and the host has not said Later in this launch (the companion's `offered`) or in
 * this tab for this version (`dismissed`).
 */
export function shouldOffer(
  u: UpdateSnapshot | undefined,
  host: boolean,
  demo: boolean,
  dismissed: string | null,
): boolean {
  if (!host || demo || !u || u.offered) return false;
  if (u.state !== 'available' && u.state !== 'ready') return false;
  return dismissed !== u.latest;
}

/** The header chip "Update": after Later, while the update is still there to take. */
export const chipShown = (u: UpdateSnapshot | undefined, host: boolean, demo: boolean) =>
  host &&
  !demo &&
  !!u &&
  !!u.latest &&
  (u.state === 'available' || u.state === 'ready') &&
  u.offered === true;

const DISMISSED = 'soundpad-web:update-dismissed';
/** This tab's Later for one version (the companion's `offered` covers the launch). */
export function dismissedVersion(): string | null {
  try {
    return sessionStorage.getItem(DISMISSED);
  } catch {
    return null;
  }
}
export function rememberDismissed(version: string | undefined) {
  if (!version) return;
  try {
    sessionStorage.setItem(DISMISSED, version);
  } catch {
    /* the companion's offered flag still applies */
  }
}

// The Update now dialog is opened from the offer, the header chip and Settings; one at a time.
let dialogOpen = false;
const listeners = new Set<() => void>();
export function openUpdateDialog(open = true) {
  dialogOpen = open;
  for (const listener of listeners) listener();
}
export function useUpdateDialog() {
  return useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
    () => dialogOpen,
    () => false,
  );
}
