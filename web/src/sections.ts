/**
 * What each main section was showing (2.40): its open item, draft and view position. Closing a window
 * (Mimic is a dialog) or playing a Soundboard tile must not lose an edit in progress, and a page reload
 * comes back to it. Kept in memory and in this tab's sessionStorage, never shared or sent anywhere.
 */
export type SectionName = 'mimic' | 'temp';
export type SectionMemory = {
  mimic: {
    /** The draft shown (one at a time), if any. */
    draftId?: string | null;
    /** The recording history session the draft and view belong to. */
    sessionId?: string | null;
    /** False when the view was moved off live, to `viewEnd` (a history frame). */
    follow?: boolean;
    viewEnd?: number;
  };
  temp: {
    open?: boolean;
    find?: string;
  };
};

const KEY = 'soundpad-web:sections';
let memory: Partial<SectionMemory> | null = null;

function load(): Partial<SectionMemory> {
  if (memory) return memory;
  try {
    const raw = JSON.parse(sessionStorage.getItem(KEY) ?? '{}') as unknown;
    memory = raw && typeof raw === 'object' ? (raw as Partial<SectionMemory>) : {};
  } catch {
    memory = {};
  }
  return memory;
}

/** What `section` remembered, or an empty object. */
export function recall<S extends SectionName>(section: S): SectionMemory[S] {
  const value = load()[section];
  return value && typeof value === 'object' ? { ...value } : ({} as SectionMemory[S]);
}

/** Merges `value` into what `section` remembers. */
export function remember<S extends SectionName>(section: S, value: Partial<SectionMemory[S]>) {
  const all = load();
  all[section] = { ...(all[section] ?? {}), ...value } as SectionMemory[S];
  try {
    sessionStorage.setItem(KEY, JSON.stringify(all));
  } catch {
    // Storage can be unavailable; the memory still holds for this page.
  }
}

/** Forgets everything (tests). */
export function forgetSections() {
  memory = {};
  try {
    sessionStorage.removeItem(KEY);
  } catch {
    // nothing stored
  }
}

/** Test seam: drops what is held in memory, as a page reload would (sessionStorage stays). */
export function reloadSections() {
  memory = null;
}
