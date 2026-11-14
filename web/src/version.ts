declare const __APP_VERSION__: string;

/** Website version, injected from package.json at build time. */
export const APP_VERSION: string = typeof __APP_VERSION__ === 'string' ? __APP_VERSION__ : '0.0.0';

/** Short label such as "2.3" for display in the header and footer. */
export const APP_VERSION_LABEL = APP_VERSION.split('.').slice(0, 2).join('.');

/** True when the companion reports at least `major.minor`; unknown versions are treated as old. */
export function companionAtLeast(version: string | null | undefined, major: number, minor: number) {
  const [actualMajor = 0, actualMinor = 0] = (version ?? '0.0').split('.').map(Number);
  if (!Number.isFinite(actualMajor) || !Number.isFinite(actualMinor)) return false;
  return actualMajor > major || (actualMajor === major && actualMinor >= minor);
}

/**
 * True when a host's open tab runs a website new enough to relay the 2.35 command fields ("autoplay").
 * An older tab's strict command check would disconnect the guest who sent one, so guests ask first: the
 * host tab reports its version in its welcome. Unknown (an older tab reports nothing) counts as old.
 */
export const siteRelaysIntents = (site: string | null | undefined) => companionAtLeast(site, 2, 35);
/**
 * True when the host's page relays the 2.40 commands (drafts, guest preview, guest Keep, budgets). An
 * older host page refuses commands it does not know, so guests check its welcome first.
 */
export const siteRelaysDrafts = (site: string | null | undefined) => companionAtLeast(site, 2, 40);
/** True when the host's page relays the 2.41 voice-library commands (its welcome also says so). */
export const siteRelaysLibrary = (site: string | null | undefined) => companionAtLeast(site, 2, 41);

/** Oldest companion that supports every feature of this website. */
export const REQUIRED_COMPANION = { major: 2, minor: 3, label: '2.3' } as const;

/**
 * The companion version each newer feature needs. The website always serves the newest version, so a host
 * who rolled back only the Windows companion must not see controls their companion would reject.
 */
export const FEATURES = {
  mimic: [2, 4],
  transcripts: [2, 5],
  hotkey: [2, 7],
  trim: [2, 8],
  effects: [2, 9],
  hotkeyLength: [2, 11],
  preview: [2, 12],
  hotkeyKey: [2, 14],
  voiceReplayLibrary: [2, 15],
  speed: [2, 16],
  keepPitch: [2, 17],
  clearTemp: [2, 18],
  trimPreview: [2, 20],
  trimWords: [2, 24],
  undoStop: [2, 30],
  guestUndo: [2, 32],
  intents: [2, 35],
  desktopSession: [2, 36],
  sessionControl: [2, 37],
  history: [2, 39],
  drafts: [2, 40],
  voiceLibrary: [2, 41],
} as const satisfies Record<string, readonly [number, number]>;

/** True when the website changed version since this browser last used it (and it had used it before). */
export const updatedSince = (last: string | null | undefined, current: string) =>
  !!last && last !== current;

export const supports = (version: string | null | undefined, feature: keyof typeof FEATURES) =>
  companionAtLeast(version, FEATURES[feature][0], FEATURES[feature][1]);

/** What each companion-dependent feature is called, for telling a host what an update would add. */
const FEATURE_NAMES: Record<keyof typeof FEATURES, string> = {
  mimic: 'the Mimic window',
  transcripts: 'Mimic transcripts',
  hotkey: 'the Mimic hotkey',
  trim: 'Edit copy',
  effects: 'voice effects',
  hotkeyLength: 'the hotkey length choice',
  preview: 'private preview',
  hotkeyKey: 'the hotkey key choice (F6–F10)',
  voiceReplayLibrary: 'importing from Voice Replay Studio',
  speed: 'any speed for edited copies (0.05× to 20×)',
  keepPitch: 'Keep pitch when changing speed',
  clearTemp: 'clearing all temporary audio during a session',
  trimPreview: 'previewing an edited copy before saving it',
  trimWords: 'naming trimmed copies after their words',
  undoStop: 'Undo that stops only its own clip',
  guestUndo: 'Undo for guests on their own clips and copies',
  intents: 'playback that follows the newest click, and Panic',
  desktopSession:
    'the companion window that keeps your session running when the browser tab closes',
  sessionControl: 'guest admission, places and permissions that survive a reload of the host page',
  history: 'a rolling recording history of up to an hour, with clickable word-by-word subtitles',
  drafts:
    'drafts of Mimic clips you can edit before keeping, Latest phrase, private preview for guests, guest Keep with storage budgets, and Temp that stays until you quit',
  voiceLibrary:
    'the voice library: kept clips with voices, tags and their words, word search that plays a single word, wanted words, a review queue, and word search in Soundpad categories you choose',
};

/**
 * Names of the features this companion version lacks, oldest first. Empty for an unknown version (the
 * companion has not reported one yet) so nothing is claimed before it connects.
 */
export function missingFeatures(version: string | null | undefined) {
  if (!version) return [];
  return (Object.keys(FEATURES) as (keyof typeof FEATURES)[])
    .filter((feature) => !supports(version, feature))
    .map((feature) => FEATURE_NAMES[feature]);
}
