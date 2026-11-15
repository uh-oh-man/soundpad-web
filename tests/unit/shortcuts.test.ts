/**
 * Browser shortcuts (2.44, Settings → Keyboard): normalising and matching key presses, Space by code,
 * defaults that equal the 2.43 keys, refusals, the host-wide clash warning, and storage that throws,
 * holds damaged JSON or changes in another tab. Runs in node: without a stub there is no storage at all.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

type Mod = typeof import('../../web/src/shortcuts');
const press = (
  key: string,
  mods: Partial<{ ctrl: boolean; alt: boolean; shift: boolean; meta: boolean }> = {},
  code = key.length === 1 ? `Key${key.toUpperCase()}` : key,
) => ({
  key,
  code,
  ctrlKey: !!mods.ctrl,
  altKey: !!mods.alt,
  shiftKey: !!mods.shift,
  metaKey: !!mods.meta,
});
const SPACE = (mods = {}) => press(' ', mods, 'Space');
const SLASH = (mods = {}) => press('/', mods, 'Slash');
const SHIFT_ESCAPE = press('Escape', { shift: true }, 'Escape');

function memoryStorage(initial: Record<string, string> = {}) {
  const data = new Map(Object.entries(initial));
  return {
    data,
    getItem: (k: string) => data.get(k) ?? null,
    setItem: (k: string, v: string) => void data.set(k, v),
    removeItem: (k: string) => void data.delete(k),
  };
}

let s: Mod;
beforeEach(async () => {
  vi.resetModules();
  s = await import('../../web/src/shortcuts');
});
afterEach(() => {
  vi.unstubAllGlobals();
});

describe('matching', () => {
  it('has the 2.43 keys as defaults: / for Search, Space for Play/Pause, Shift+Escape for Panic, Stop off', () => {
    expect(s.DEFAULT_SHORTCUTS.stop).toBeNull();
    expect(s.matchesShortcut('search', SLASH())).toBe(true);
    // As in 2.43, Shift held still searches (layouts that need Shift for "/") and still toggles.
    expect(s.matchesShortcut('search', SLASH({ shift: true }))).toBe(true);
    expect(s.matchesShortcut('search', SLASH({ ctrl: true }))).toBe(false);
    expect(s.matchesShortcut('search', SLASH({ alt: true }))).toBe(false);
    expect(s.matchesShortcut('search', SLASH({ meta: true }))).toBe(false);
    expect(s.matchesShortcut('playPause', SPACE())).toBe(true);
    expect(s.matchesShortcut('playPause', SPACE({ shift: true }))).toBe(true);
    expect(s.matchesShortcut('playPause', SPACE({ ctrl: true }))).toBe(false);
    // Space by code: a layout or IME that reports another key for the space bar still matches.
    expect(s.matchesShortcut('playPause', press('Spacebar', {}, 'Space'))).toBe(true);
    expect(s.matchesShortcut('playPause', press(' ', {}, 'Numpad0'))).toBe(false);
    expect(s.matchesShortcut('panic', SHIFT_ESCAPE)).toBe(true);
    expect(s.matchesShortcut('panic', press('Escape'))).toBe(false);
    expect(s.matchesShortcut('panic', press('Escape', { shift: true, ctrl: true }))).toBe(false);
    expect(s.matchesShortcut('stop', press('x'))).toBe(false);
  });

  it('normalises keys and formats chords for people and for aria-keyshortcuts', () => {
    expect(s.chordOf(press('K', { ctrl: true, shift: true }))).toEqual({
      key: 'k',
      ctrl: true,
      alt: false,
      shift: true,
      meta: false,
    });
    expect(s.chordOf(press('Shift', { shift: true }))).toBeNull();
    expect(s.chordOf(press('Control', { ctrl: true }))).toBeNull();
    expect(s.chordOf(SPACE())?.key).toBe('Space');
    expect(s.foldKey('esc')).toBe('Escape');
    expect(s.foldKey('f9')).toBe('F9');
    expect(s.formatChord(s.chordOf(press('k', { ctrl: true, alt: true }))!)).toBe('Ctrl+Alt+K');
    expect(s.formatChord(s.DEFAULT_SHORTCUTS.panic!)).toBe('Shift+Escape');
    expect(s.formatChord(s.DEFAULT_SHORTCUTS.search!)).toBe('/');
    expect(s.ariaChord(s.chordOf(press('k', { ctrl: true }))!)).toBe('Control+K');
    expect(s.ariaChord(s.DEFAULT_SHORTCUTS.playPause)).toBe('Space');
    expect(s.ariaChord(null)).toBeUndefined();
    // Case-folded: Ctrl+K matches whether the browser reports k or K.
    s.writeShortcut('stop', s.chordOf(press('K', { ctrl: true }))!);
    expect(s.matchesShortcut('stop', press('k', { ctrl: true }))).toBe(true);
  });

  it('names the host-wide hotkey a key press makes, from the physical key', () => {
    expect(s.hostChordOf(press('F9', { ctrl: true, alt: true }, 'F9'))).toBe('Ctrl+Alt+F9');
    expect(s.hostChordOf(press('k', { meta: true, shift: true }, 'KeyK'))).toBe('Shift+Win+K');
    expect(s.hostChordOf(press('5', { alt: true }, 'Numpad5'))).toBe('Alt+Num5');
    expect(s.hostChordOf(press('!', { ctrl: true, shift: true }, 'Digit1'))).toBe('Ctrl+Shift+1');
    expect(s.hostChordOf(press('Pause', {}, 'Pause'))).toBe('Pause');
    expect(s.hostChordOf(press('Escape', {}, 'Escape'))).toBeNull();
    expect(s.hostChordOf(press('/', { ctrl: true }, 'Slash'))).toBeNull();
  });

  it('never acts while a recorder listens', () => {
    s.setRecording(true);
    expect(s.matchesShortcut('panic', SHIFT_ESCAPE)).toBe(false);
    expect(s.isRecording()).toBe(true);
    s.setRecording(false);
    expect(s.matchesShortcut('panic', SHIFT_ESCAPE)).toBe(true);
  });
});

describe('choosing a key', () => {
  it('refuses duplicates, the browser’s keys and single keys the page uses, naming why', () => {
    const c = (key: string, mods = {}) => s.chordOf(press(key, mods))!;
    expect(s.refusal('stop', s.DEFAULT_SHORTCUTS.panic!)).toBe('Shift+Escape is already Panic.');
    expect(s.refusal('stop', c('Tab'))).toMatch(/Tab moves/);
    expect(s.refusal('stop', c('Tab', { shift: true }))).toMatch(/Tab moves/);
    expect(s.refusal('stop', c('Enter'))).toMatch(/closes dialogs/);
    expect(s.refusal('stop', c('Escape'))).toMatch(/closes dialogs/);
    for (const k of ['c', 'v', 'x', 'a', 'z', 'y', 'f', 'l', 'r', 't', 'n', 'w']) {
      expect(s.refusal('stop', c(k, { ctrl: true }))).toMatch(/browser's or this page's own key/);
      expect(s.refusal('stop', c(k, { meta: true }))).toMatch(/own key/);
    }
    for (const k of ['F5', 'F11', 'F12']) expect(s.refusal('stop', c(k))).toMatch(/browser's own/);
    expect(s.refusal('stop', c('F4', { alt: true }))).toMatch(/Alt\+F4 closes/);
    expect(s.refusal('stop', c('3'))).toMatch(/Mimic uses 3, 5 and 0/);
    expect(s.refusal('stop', c('+'))).toMatch(/Kick counter/);
    expect(s.refusal('stop', c('-'))).toMatch(/Kick counter/);
    expect(s.refusal('stop', c('q'))).toMatch(/letters and digits/);
    expect(s.refusal('stop', c('Q', { shift: true }))).toMatch(/letters and digits/);
    // Fine: a modified letter, an F-key, a named key, and a key's own current binding.
    expect(s.refusal('stop', c('k', { ctrl: true, alt: true }))).toBeNull();
    expect(s.refusal('stop', c('F8'))).toBeNull();
    expect(s.refusal('stop', c('End'))).toBeNull();
    expect(s.refusal('panic', s.DEFAULT_SHORTCUTS.panic!)).toBeNull();
    expect(s.refusal('search', s.DEFAULT_SHORTCUTS.playPause!)).toBe(
      'Space is already Play/Pause.',
    );
  });

  it('warns when the companion takes the key on this PC', () => {
    const chord = s.chordOf(press('F9', { ctrl: true, alt: true }, 'F9'))!;
    expect(s.hostClash(chord, [null, 'Ctrl+Alt+F9'])).toBe(
      'Ctrl+Alt+F9: the companion takes this key on this PC; this page never receives it.',
    );
    expect(s.hostClash(chord, ['Ctrl+F9', null])).toBeNull();
    expect(s.hostClash(s.chordOf(press('k', { meta: true }))!, ['Win+K'])).not.toBeNull();
  });

  it('a rebound Panic: the old key no longer panics, the new one does; Shift on another binding is exact', () => {
    const next = s.chordOf(press('F8', { ctrl: true }, 'F8'))!;
    s.writeShortcut('panic', next);
    expect(s.matchesShortcut('panic', SHIFT_ESCAPE)).toBe(false);
    expect(s.matchesShortcut('panic', press('F8', { ctrl: true }, 'F8'))).toBe(true);
    s.resetShortcut('panic');
    expect(s.matchesShortcut('panic', SHIFT_ESCAPE)).toBe(true);
    // Stop on Shift+Space: Shift+Space is Stop now, plain Space still Play/Pause.
    s.writeShortcut('stop', s.chordOf(SPACE({ shift: true }))!);
    expect(s.matchesShortcut('stop', SPACE({ shift: true }))).toBe(true);
    expect(s.matchesShortcut('playPause', SPACE({ shift: true }))).toBe(false);
    expect(s.matchesShortcut('playPause', SPACE())).toBe(true);
    s.writeShortcut('search', null);
    expect(s.matchesShortcut('search', SLASH())).toBe(false);
  });
});

describe('storage', () => {
  it('without storage the defaults apply and a change holds in this tab', () => {
    // node has no localStorage: every access throws, as in a private window with blocked site data.
    expect(s.currentShortcuts()).toEqual(s.DEFAULT_SHORTCUTS);
    const stored = s.writeShortcut('stop', s.chordOf(press('F8', {}, 'F8'))!);
    expect(stored).toBe(false);
    expect(s.readShortcut('stop')?.key).toBe('F8');
  });

  it('storing writes version 1, keeps ids it does not know, and damaged entries fall back', () => {
    const storage = memoryStorage({
      [s.SHORTCUTS_KEY]: JSON.stringify({
        version: 1,
        bindings: {
          panic: { key: 'F8', ctrl: true, alt: false, shift: false, meta: false },
          search: { key: 'x' }, // damaged: default
          stop: null,
          fromANewerSite: { key: 'q', ctrl: true },
        },
      }),
    });
    vi.stubGlobal('localStorage', storage);
    expect(s.readShortcut('panic')).toEqual({
      key: 'F8',
      ctrl: true,
      alt: false,
      shift: false,
      meta: false,
    });
    expect(s.readShortcut('search')).toEqual(s.DEFAULT_SHORTCUTS.search);
    expect(s.readShortcut('stop')).toBeNull();
    expect(s.writeShortcut('stop', s.chordOf(press('End', {}, 'End'))!)).toBe(true);
    const saved = JSON.parse(storage.data.get(s.SHORTCUTS_KEY)!);
    expect(saved.version).toBe(1);
    expect(saved.bindings.fromANewerSite).toEqual({ key: 'q', ctrl: true });
    expect(saved.bindings.stop.key).toBe('End');
    expect(saved.bindings.panic.key).toBe('F8');
  });

  it('damaged JSON or another version gives the defaults', () => {
    for (const raw of ['{ nope', '[]', JSON.stringify({ version: 2, bindings: { stop: null } })]) {
      vi.stubGlobal('localStorage', memoryStorage({ [s.SHORTCUTS_KEY]: raw }));
      expect(s.currentShortcuts()).toEqual(s.DEFAULT_SHORTCUTS);
    }
  });

  it('follows a change made in another tab (storage event)', () => {
    const storage = memoryStorage();
    vi.stubGlobal('localStorage', storage);
    const handlers: ((e: { key: string | null }) => void)[] = [];
    vi.stubGlobal('window', {
      addEventListener: (_: string, h: (e: { key: string | null }) => void) => handlers.push(h),
    });
    let calls = 0;
    const off = s.subscribeShortcuts(() => calls++);
    expect(s.readShortcut('stop')).toBeNull();
    storage.data.set(
      s.SHORTCUTS_KEY,
      JSON.stringify({
        version: 1,
        bindings: { stop: { key: 'End', ctrl: false, alt: false, shift: false, meta: false } },
      }),
    );
    handlers.forEach((h) => h({ key: s.SHORTCUTS_KEY }));
    expect(calls).toBe(1);
    expect(s.readShortcut('stop')?.key).toBe('End');
    handlers.forEach((h) => h({ key: 'something-else' }));
    expect(calls).toBe(1);
    off();
  });
});
