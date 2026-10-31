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
} as const satisfies Record<string, readonly [number, number]>;

export const supports = (version: string | null | undefined, feature: keyof typeof FEATURES) =>
  companionAtLeast(version, FEATURES[feature][0], FEATURES[feature][1]);
