/**
 * Who may be in a hosted session and what each guest may do (2.37): admission (at once, or ask the
 * host first), the number of guest places, extra places granted for this session, per-guest
 * permission overrides and the guests the host turned away. Everything is keyed by the guest's
 * participant id, not the transport's peer id, which changes on every reload. Since 2.41 that id is the
 * one the guest's browser key proves (network/guest-key.ts), not a profile id the page declares, so
 * nobody can take over another guest's admission, permissions or place by copying their profile. A
 * guest whose page proves no key (older than 2.41) is unverified and new on every page load. Someone who
 * clears their browser storage just becomes a new guest and, with "Ask me first", has to ask again.
 */
import { z } from 'zod';
import { participantSchema, type Participant, type Snapshot } from '../protocol';

/** Guest places a host can choose, at most. */
export const MAX_PLACES = 12;
/** Extra places a host can grant on request beyond the chosen number (a bound for benchmarked capacity). */
export const MAX_EXTRA = 4;
/** How long a request waits for the host before it lapses. */
export const REQUEST_MS = 2 * 60 * 1000;
/** How long a guest who left (a reload, a dropped connection) keeps their place. */
export const HOLD_MS = 60 * 1000;
/** Entries kept per list, so the settings stay far below the companion's 64 KiB limit. */
export const LIST = 64;

export type Permissions = Snapshot['permissions'];
export type PermissionKey = keyof Permissions;
export const PERMISSION_KEYS = [
  'remotePlayback',
  'guestSeeking',
  'guestSpotify',
  'guestUploads',
  'guestTransforms',
  'guestMimic',
  'guestKeep',
  'guestLibraryEdit',
  'guestCounter',
] as const satisfies readonly PermissionKey[];
export type Override = Partial<Record<PermissionKey, boolean>>;

export type SessionPolicy = {
  admission: 'open' | 'ask';
  capacity: number;
  /** Places added by "Let in" on an extra-place request; for this session only. */
  extra: number;
  /** Per-guest permissions; a key that is absent means "Same as everyone". Only "Off" changes anything. */
  overrides: Record<string, Override>;
  /** Guests the host turned away, with the name they used, so the host can let them in after all. */
  turnedAway: Participant[];
  /** Guests already admitted in this session, so a reload or a reconnect never has to ask again. */
  admitted: string[];
};
export const DEFAULT_POLICY: SessionPolicy = {
  admission: 'open',
  capacity: MAX_PLACES,
  extra: 0,
  overrides: {},
  turnedAway: [],
  admitted: [],
};

const overrideSchema = z
  .object(Object.fromEntries(PERMISSION_KEYS.map((key) => [key, z.boolean().optional()])))
  .strict() as z.ZodType<Override>;
/** The settings as the companion hands them back; it never reads them, so they are checked here. */
export const policySchema = z.object({
  admission: z.enum(['open', 'ask']),
  capacity: z.number().int().min(1).max(MAX_PLACES),
  extra: z.number().int().min(0).max(MAX_EXTRA),
  overrides: z
    .record(z.uuid(), overrideSchema)
    .refine((value) => Object.keys(value).length <= LIST),
  turnedAway: z.array(participantSchema).max(LIST),
  admitted: z.array(z.uuid()).max(LIST),
});

/** Reads kept settings; anything unreadable gives the defaults rather than a broken session. */
export function readPolicy(raw: unknown): SessionPolicy {
  const parsed = policySchema.safeParse(raw);
  return parsed.success ? (parsed.data as SessionPolicy) : { ...DEFAULT_POLICY };
}

/** Keeps the newest entries of a list within its bound. */
export const bounded = <T>(list: T[]) => list.slice(-LIST);

/** The same session with its one-session parts dropped: after Replace invite, every guest is new. */
export const forNewInvite = (policy: SessionPolicy): SessionPolicy => ({
  ...policy,
  extra: 0,
  turnedAway: [],
  admitted: [],
});

/** How many guests may be in at once. */
export const places = (policy: SessionPolicy) =>
  Math.min(MAX_PLACES + MAX_EXTRA, policy.capacity + policy.extra);

/**
 * A guest's permissions: the session-wide value, turned off where the host set an override "Off" for
 * them. An override can only take a permission away (2.37 review): the companion checks the
 * session-wide permissions and cannot tell guests apart, so a guest set to "On" while it is off for
 * everyone would see a control that every press of fails.
 */
export function effectivePermissions(base: Permissions, override?: Override): Permissions {
  if (!override) return base;
  const result = { ...base };
  // "On" keeps the session-wide value as it is (some are unset on older companions, which means allowed).
  for (const key of PERMISSION_KEYS) if (override[key] === false) result[key] = false;
  return result;
}
