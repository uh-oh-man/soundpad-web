/**
 * Host style sync (2.43, gate uiSync) against a stand-in companion: ui.json in memory, with replies that can be
 * held back to make choices while a request is on its way. Runs without React (the hook's parts) and without
 * storage (prefs then live in this tab only, as in a private window), except where a test gives it storage.
 */
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { UiPrefs } from '../../web/src/protocol';

const companion = vi.hoisted(() => {
  const defaults = {
    version: 1 as const,
    navStyle: 'tabs' as const,
    theme: 'red-blue' as const,
    motion: 'system' as const,
    contrast: 'standard' as const,
    headerAnimation: 'on' as const,
    activeDraft: null,
  };
  const state = {
    file: null as Record<string, unknown> | null,
    sets: [] as Record<string, unknown>[],
    defaults,
  };
  const reply = () => ({
    prefs: { ...defaults, ...(state.file ?? {}) },
    saved: state.file !== null,
  });
  const run = vi.fn(async (op: string, fields: { uiPrefs?: Record<string, unknown> }) => {
    if (op === 'uiPrefsSet') {
      state.sets.push(fields.uiPrefs ?? {});
      state.file = { ...(state.file ?? {}), ...fields.uiPrefs };
    }
    const answer = reply();
    return answer;
  });
  return { state, run };
});
vi.mock('../../web/src/controller', () => ({ controller: { run: companion.run } }));

const { hostStyleSyncForTests: sync, resetHostStyleSync } = await import('../../web/src/ui-sync');
const { readPref, writePref, resetPrefsForTests, subscribePrefs } =
  await import('../../web/src/prefs');
const { applyTheme, currentTheme, subscribeTheme } = await import('../../web/src/appearance');

let stops: (() => void)[] = [];
function start() {
  sync.enable(true);
  stops = [subscribePrefs(sync.styleChanged), subscribeTheme(sync.styleChanged)];
}
const flush = () => new Promise((r) => setTimeout(r, 0));

beforeEach(() => {
  companion.state.file = null;
  companion.state.sets = [];
  companion.run.mockClear();
  resetHostStyleSync();
  resetPrefsForTests();
  writePref('nav', 'tabs');
  writePref('motion', 'system');
  writePref('contrast', 'standard');
  writePref('headerAnimation', 'on');
  applyTheme('red-blue');
});
afterEach(() => {
  for (const stop of stops) stop();
  stops = [];
  vi.unstubAllGlobals();
});

it('a choice made while the first page seeds ui.json is sent afterwards, and not reverted', async () => {
  start();
  let releaseSeed!: () => void;
  companion.run.mockImplementationOnce(async () => ({
    prefs: { ...companion.state.defaults },
    saved: false,
  }));
  companion.run.mockImplementationOnce(async (_op, fields) => {
    companion.state.sets.push(fields.uiPrefs ?? {});
    companion.state.file = { ...fields.uiPrefs };
    const answer = { prefs: { ...companion.state.defaults, ...companion.state.file }, saved: true };
    await new Promise<void>((r) => (releaseSeed = r));
    return answer;
  });
  const pulling = sync.pull();
  await vi.waitFor(() => expect(companion.state.sets).toHaveLength(1));
  // Purple while the seed (Blue studio) is on its way.
  applyTheme('purple');
  releaseSeed();
  await pulling;
  await flush();
  expect(currentTheme()).toBe('purple');
  expect(companion.state.file?.theme).toBe('purple');
});

it('a choice made while a change is being sent after the read wins, and reaches ui.json', async () => {
  companion.state.file = { navStyle: 'sidebar' };
  start();
  // This page changed motion while the get was on its way; the send of that change is held.
  let releaseSet!: () => void;
  companion.run.mockImplementationOnce(async () => {
    const answer = { prefs: { ...companion.state.defaults, ...companion.state.file }, saved: true };
    writePref('motion', 'reduce');
    return answer;
  });
  companion.run.mockImplementationOnce(async (_op, fields) => {
    companion.state.sets.push(fields.uiPrefs ?? {});
    companion.state.file = { ...companion.state.file, ...fields.uiPrefs };
    const answer = { prefs: { ...companion.state.defaults, ...companion.state.file }, saved: true };
    await new Promise<void>((r) => (releaseSet = r));
    return answer;
  });
  const pulling = sync.pull();
  await vi.waitFor(() => expect(companion.state.sets).toEqual([{ motion: 'reduce' }]));
  // Ocean while that send is on its way: before the fix it was never sent, and the next pass put the old
  // theme back.
  applyTheme('ocean');
  releaseSet();
  await pulling;
  await flush();
  expect(currentTheme()).toBe('ocean');
  expect(readPref('motion')).toBe('reduce');
  expect(readPref('nav')).toBe('sidebar');
  expect(companion.state.file).toMatchObject({
    navStyle: 'sidebar',
    motion: 'reduce',
    theme: 'ocean',
  });
});

it('a browser that syncs for the first time keeps its own earlier choices where ui.json has the default', async () => {
  // The companion window connected first and seeded ui.json with its defaults; this browser chose Ocean
  // and the sidebar back in 2.42 (top tabs became the default in 2.47), when the choice was kept per browser.
  const storage = new Map<string, string>();
  vi.stubGlobal('localStorage', {
    getItem: (k: string) => storage.get(k) ?? null,
    setItem: (k: string, v: string) => void storage.set(k, v),
    removeItem: (k: string) => void storage.delete(k),
  });
  resetPrefsForTests();
  applyTheme('ocean');
  writePref('nav', 'sidebar');
  companion.state.file = { ...companion.state.defaults, motion: 'reduce' };
  start();
  await sync.pull();
  await flush();
  expect(currentTheme()).toBe('ocean');
  expect(readPref('nav')).toBe('sidebar');
  // A non-default shared choice still applies here.
  expect(readPref('motion')).toBe('reduce');
  expect(companion.state.file).toMatchObject({
    theme: 'ocean',
    navStyle: 'sidebar',
    motion: 'reduce',
  });
  // Later, ui.json's value wins again: the owner went back to Blue studio on the other page.
  companion.state.file = { ...companion.state.file, theme: 'red-blue' };
  await sync.pull();
  expect(currentTheme()).toBe('red-blue');
});

it('a page that has synced before takes the shared style as it is', async () => {
  const storage = new Map<string, string>([['soundpad-web:ui-synced', '1']]);
  vi.stubGlobal('localStorage', {
    getItem: (k: string) => storage.get(k) ?? null,
    setItem: (k: string, v: string) => void storage.set(k, v),
    removeItem: (k: string) => void storage.delete(k),
  });
  resetPrefsForTests();
  applyTheme('ocean');
  companion.state.file = { ...companion.state.defaults } satisfies Partial<UiPrefs>;
  start();
  await sync.pull();
  expect(currentTheme()).toBe('red-blue');
  expect(companion.state.sets).toEqual([]);
});
