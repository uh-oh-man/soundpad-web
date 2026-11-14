import { describe, it, expect } from 'vitest';
import { arrangeSounds, boardTitle, type BoardSelection } from '../../web/src/arrange';
import { companionAtLeast, missingFeatures, supports, updatedSince } from '../../web/src/version';
import { wheelGoesToPage, WHEEL_ZOOM_DELAY_MS } from '../../web/src/timeline';
import { blankWorkspace, type Workspace } from '../../web/src/workspace';
import { EMPTY, type Snapshot, type Sound } from '../../web/src/protocol';
const host = 'a'.repeat(64);
const id = (c: string) => c.repeat(64);
const sound = (c: string, name: string, extra: Partial<Sound> = {}) =>
  ({ id: id(c), name, icon: null, ...extra }) as Sound;
const sounds = [
  sound('1', 'Airhorn', { durationMs: 3000, nativePlays: 4 }),
  sound('2', 'bonk', { durationMs: 1000, nativePlays: 9 }),
  sound('3', 'Crickets', { durationMs: 2000, nativePlays: 1 }),
  sound('4', 'Airhorn reversed', { internalVariant: true }),
];
const state: Snapshot = {
  ...EMPTY,
  library: {
    version: 'test',
    sounds,
    categories: [
      {
        id: 'memes',
        name: 'Memes',
        icon: null,
        soundIds: [id('3'), id('1')],
        children: [],
      } as unknown as Snapshot['library']['categories'][number],
    ],
  },
};
const all: BoardSelection = { category: 'all', myCategory: 'all', search: '' };
const names = (list: Sound[]) => list.map((s) => s.name);
const mine = (patch: Partial<Workspace> = {}): Workspace => ({
  ...blankWorkspace(host),
  layout: 'mine',
  ...patch,
});
describe('board arrangement', () => {
  it('shows the host library without internal variants', () => {
    expect(names(arrangeSounds(state, null, all, {}))).toEqual(['Airhorn', 'bonk', 'Crickets']);
    expect(names(arrangeSounds(state, null, { ...all, category: 'memes' }, {}))).toEqual([
      'Crickets',
      'Airhorn',
    ]);
  });
  it('sorts by each key in both directions', () => {
    const w = (sort: Workspace['sort'], descending = false) => ({
      ...blankWorkspace(host),
      sort,
      descending,
    });
    expect(names(arrangeSounds(state, w('name'), all, {}))).toEqual([
      'Airhorn',
      'bonk',
      'Crickets',
    ]);
    expect(names(arrangeSounds(state, w('duration', true), all, {}))).toEqual([
      'Airhorn',
      'Crickets',
      'bonk',
    ]);
    expect(names(arrangeSounds(state, w('native'), all, {}))[0]).toBe('Crickets');
    const totals = { [id('3')]: { total: 7, lastPlayedAt: 5 } };
    expect(names(arrangeSounds(state, w('web', true), all, totals))[0]).toBe('Crickets');
  });
  it('applies personal folders, favorites, hidden sounds and custom order', () => {
    const folder = crypto.randomUUID();
    const w = mine({
      folders: [{ id: folder, name: 'Loud', parent: null, soundIds: [id('2'), id('1')] }],
      favorites: [id('3')],
      hidden: [id('2')],
      order: [id('3'), id('1')],
    });
    expect(names(arrangeSounds(state, w, all, {}))).toEqual(['Crickets', 'Airhorn']);
    expect(names(arrangeSounds(state, w, { ...all, myCategory: 'favorites' }, {}))).toEqual([
      'Crickets',
    ]);
    expect(names(arrangeSounds(state, w, { ...all, myCategory: folder }, {}))).toEqual(['Airhorn']);
    expect(boardTitle(state, w, { ...all, myCategory: folder })).toBe('Loud');
  });
  it('searches every personal sound case-insensitively, ignoring surrounding spaces', () => {
    const w = mine({ favorites: [id('3')] });
    const selection = { ...all, myCategory: 'favorites', search: '  AIR ' };
    expect(names(arrangeSounds(state, w, selection, {}))).toEqual(['Airhorn']);
    expect(boardTitle(state, w, selection)).toBe('Search results');
  });
  it('titles host categories and falls back for unknown ones', () => {
    expect(boardTitle(state, null, { ...all, category: 'memes' })).toBe('Memes');
    expect(boardTitle(state, null, { ...all, category: 'missing' })).toBe('All sounds');
  });
});
describe('companion version gate', () => {
  it('compares major and minor numerically', () => {
    expect(companionAtLeast('2.3.0', 2, 3)).toBe(true);
    expect(companionAtLeast('2.10', 2, 3)).toBe(true);
    expect(companionAtLeast('3.0', 2, 3)).toBe(true);
    expect(companionAtLeast('2.2.9', 2, 3)).toBe(false);
    expect(companionAtLeast(null, 2, 3)).toBe(false);
    expect(companionAtLeast('dev', 2, 3)).toBe(false);
  });
});

describe('feature gates for older companions', () => {
  it('hides each feature until the companion that added it', () => {
    expect(supports('2.3.1', 'mimic')).toBe(false);
    expect(supports('2.4.0', 'mimic')).toBe(true);
    expect(supports('2.4.0', 'transcripts')).toBe(false);
    expect(supports('2.6.0', 'hotkey')).toBe(false);
    expect(supports('2.8.1', 'trim')).toBe(true);
    expect(supports('2.8.1', 'effects')).toBe(false);
    expect(supports('2.10.1', 'effects')).toBe(true);
    expect(supports('2.10.4', 'hotkeyLength')).toBe(false);
    expect(supports('2.11.0', 'hotkeyLength')).toBe(true);
    expect(supports('2.13.5', 'hotkeyKey')).toBe(false);
    expect(supports('2.14.0', 'hotkeyKey')).toBe(true);
    expect(supports('2.14.5', 'voiceReplayLibrary')).toBe(false);
    expect(supports('2.15.0', 'voiceReplayLibrary')).toBe(true);
    expect(missingFeatures('2.13.0')).toEqual([
      'the hotkey key choice (F6–F10)',
      'importing from Voice Replay Studio',
      'any speed for edited copies (0.05× to 20×)',
      'Keep pitch when changing speed',
      'clearing all temporary audio during a session',
      'previewing an edited copy before saving it',
      'naming trimmed copies after their words',
      'Undo that stops only its own clip',
      'Undo for guests on their own clips and copies',
      'playback that follows the newest click, and Panic',
      'the companion window that keeps your session running when the browser tab closes',
      'guest admission, places and permissions that survive a reload of the host page',
      'a rolling recording history of up to an hour, with clickable word-by-word subtitles',
      'drafts of Mimic clips you can edit before keeping, Latest phrase, private preview for guests, guest Keep with storage budgets, and Temp that stays until you quit',
    ]);
    expect(missingFeatures('2.15.9')).toEqual([
      'any speed for edited copies (0.05× to 20×)',
      'Keep pitch when changing speed',
      'clearing all temporary audio during a session',
      'previewing an edited copy before saving it',
      'naming trimmed copies after their words',
      'Undo that stops only its own clip',
      'Undo for guests on their own clips and copies',
      'playback that follows the newest click, and Panic',
      'the companion window that keeps your session running when the browser tab closes',
      'guest admission, places and permissions that survive a reload of the host page',
      'a rolling recording history of up to an hour, with clickable word-by-word subtitles',
      'drafts of Mimic clips you can edit before keeping, Latest phrase, private preview for guests, guest Keep with storage budgets, and Temp that stays until you quit',
    ]);
    expect(missingFeatures('2.16.2')).toEqual([
      'Keep pitch when changing speed',
      'clearing all temporary audio during a session',
      'previewing an edited copy before saving it',
      'naming trimmed copies after their words',
      'Undo that stops only its own clip',
      'Undo for guests on their own clips and copies',
      'playback that follows the newest click, and Panic',
      'the companion window that keeps your session running when the browser tab closes',
      'guest admission, places and permissions that survive a reload of the host page',
      'a rolling recording history of up to an hour, with clickable word-by-word subtitles',
      'drafts of Mimic clips you can edit before keeping, Latest phrase, private preview for guests, guest Keep with storage budgets, and Temp that stays until you quit',
    ]);
    expect(missingFeatures('2.17.0')).toEqual([
      'clearing all temporary audio during a session',
      'previewing an edited copy before saving it',
      'naming trimmed copies after their words',
      'Undo that stops only its own clip',
      'Undo for guests on their own clips and copies',
      'playback that follows the newest click, and Panic',
      'the companion window that keeps your session running when the browser tab closes',
      'guest admission, places and permissions that survive a reload of the host page',
      'a rolling recording history of up to an hour, with clickable word-by-word subtitles',
      'drafts of Mimic clips you can edit before keeping, Latest phrase, private preview for guests, guest Keep with storage budgets, and Temp that stays until you quit',
    ]);
    expect(missingFeatures('2.19.0')).toEqual([
      'previewing an edited copy before saving it',
      'naming trimmed copies after their words',
      'Undo that stops only its own clip',
      'Undo for guests on their own clips and copies',
      'playback that follows the newest click, and Panic',
      'the companion window that keeps your session running when the browser tab closes',
      'guest admission, places and permissions that survive a reload of the host page',
      'a rolling recording history of up to an hour, with clickable word-by-word subtitles',
      'drafts of Mimic clips you can edit before keeping, Latest phrase, private preview for guests, guest Keep with storage budgets, and Temp that stays until you quit',
    ]);
    expect(supports('2.19.0', 'trimPreview')).toBe(false);
    expect(supports('2.20.0', 'trimPreview')).toBe(true);
    expect(missingFeatures('2.18.0')).toEqual([
      'previewing an edited copy before saving it',
      'naming trimmed copies after their words',
      'Undo that stops only its own clip',
      'Undo for guests on their own clips and copies',
      'playback that follows the newest click, and Panic',
      'the companion window that keeps your session running when the browser tab closes',
      'guest admission, places and permissions that survive a reload of the host page',
      'a rolling recording history of up to an hour, with clickable word-by-word subtitles',
      'drafts of Mimic clips you can edit before keeping, Latest phrase, private preview for guests, guest Keep with storage budgets, and Temp that stays until you quit',
    ]);
    expect(missingFeatures('2.20.0')).toEqual([
      'naming trimmed copies after their words',
      'Undo that stops only its own clip',
      'Undo for guests on their own clips and copies',
      'playback that follows the newest click, and Panic',
      'the companion window that keeps your session running when the browser tab closes',
      'guest admission, places and permissions that survive a reload of the host page',
      'a rolling recording history of up to an hour, with clickable word-by-word subtitles',
      'drafts of Mimic clips you can edit before keeping, Latest phrase, private preview for guests, guest Keep with storage budgets, and Temp that stays until you quit',
    ]);
    expect(missingFeatures('2.29.3')).toEqual([
      'Undo that stops only its own clip',
      'Undo for guests on their own clips and copies',
      'playback that follows the newest click, and Panic',
      'the companion window that keeps your session running when the browser tab closes',
      'guest admission, places and permissions that survive a reload of the host page',
      'a rolling recording history of up to an hour, with clickable word-by-word subtitles',
      'drafts of Mimic clips you can edit before keeping, Latest phrase, private preview for guests, guest Keep with storage budgets, and Temp that stays until you quit',
    ]);
    expect(missingFeatures('2.24.0')).toEqual([
      'Undo that stops only its own clip',
      'Undo for guests on their own clips and copies',
      'playback that follows the newest click, and Panic',
      'the companion window that keeps your session running when the browser tab closes',
      'guest admission, places and permissions that survive a reload of the host page',
      'a rolling recording history of up to an hour, with clickable word-by-word subtitles',
      'drafts of Mimic clips you can edit before keeping, Latest phrase, private preview for guests, guest Keep with storage budgets, and Temp that stays until you quit',
    ]);
    expect(missingFeatures('2.30.0')).toEqual([
      'Undo for guests on their own clips and copies',
      'playback that follows the newest click, and Panic',
      'the companion window that keeps your session running when the browser tab closes',
      'guest admission, places and permissions that survive a reload of the host page',
      'a rolling recording history of up to an hour, with clickable word-by-word subtitles',
      'drafts of Mimic clips you can edit before keeping, Latest phrase, private preview for guests, guest Keep with storage budgets, and Temp that stays until you quit',
    ]);
    expect(missingFeatures('2.32.0')).toEqual([
      'playback that follows the newest click, and Panic',
      'the companion window that keeps your session running when the browser tab closes',
      'guest admission, places and permissions that survive a reload of the host page',
      'a rolling recording history of up to an hour, with clickable word-by-word subtitles',
      'drafts of Mimic clips you can edit before keeping, Latest phrase, private preview for guests, guest Keep with storage budgets, and Temp that stays until you quit',
    ]);
    expect(missingFeatures('2.34.2')).toEqual([
      'playback that follows the newest click, and Panic',
      'the companion window that keeps your session running when the browser tab closes',
      'guest admission, places and permissions that survive a reload of the host page',
      'a rolling recording history of up to an hour, with clickable word-by-word subtitles',
      'drafts of Mimic clips you can edit before keeping, Latest phrase, private preview for guests, guest Keep with storage budgets, and Temp that stays until you quit',
    ]);
    expect(missingFeatures('2.35.0')).toEqual([
      'the companion window that keeps your session running when the browser tab closes',
      'guest admission, places and permissions that survive a reload of the host page',
      'a rolling recording history of up to an hour, with clickable word-by-word subtitles',
      'drafts of Mimic clips you can edit before keeping, Latest phrase, private preview for guests, guest Keep with storage budgets, and Temp that stays until you quit',
    ]);
    expect(missingFeatures('2.36.0')).toEqual([
      'guest admission, places and permissions that survive a reload of the host page',
      'a rolling recording history of up to an hour, with clickable word-by-word subtitles',
      'drafts of Mimic clips you can edit before keeping, Latest phrase, private preview for guests, guest Keep with storage budgets, and Temp that stays until you quit',
    ]);
    expect(missingFeatures('2.37.0')).toEqual([
      'a rolling recording history of up to an hour, with clickable word-by-word subtitles',
      'drafts of Mimic clips you can edit before keeping, Latest phrase, private preview for guests, guest Keep with storage budgets, and Temp that stays until you quit',
    ]);
    // 2.38.0 (Try the example) changed the website only, so its companion lacks the history.
    expect(missingFeatures('2.38.0')).toEqual([
      'a rolling recording history of up to an hour, with clickable word-by-word subtitles',
      'drafts of Mimic clips you can edit before keeping, Latest phrase, private preview for guests, guest Keep with storage budgets, and Temp that stays until you quit',
    ]);
    expect(missingFeatures('2.39.0')).toEqual([
      'drafts of Mimic clips you can edit before keeping, Latest phrase, private preview for guests, guest Keep with storage budgets, and Temp that stays until you quit',
    ]);
    expect(missingFeatures('2.40.0')).toEqual([]);
    expect(supports('2.39.0', 'drafts')).toBe(false);
    expect(supports('2.40.0', 'drafts')).toBe(true);
    expect(supports('2.38.0', 'history')).toBe(false);
    expect(supports('2.39.0', 'history')).toBe(true);
    expect(supports('2.36.0', 'sessionControl')).toBe(false);
    expect(supports('2.37.0', 'sessionControl')).toBe(true);
    expect(supports('2.35.0', 'desktopSession')).toBe(false);
    expect(supports('2.36.0', 'desktopSession')).toBe(true);
    expect(supports('2.34.2', 'intents')).toBe(false);
    expect(supports('2.35.0', 'intents')).toBe(true);
    expect(supports('2.15.9', 'speed')).toBe(false);
    expect(supports('2.16.0', 'speed')).toBe(true);
    expect(missingFeatures(null)).toEqual([]);
  });
});

describe('waveform wheel delay', () => {
  it('lets the page scroll until the pointer rested a second with no page scrolling', () => {
    expect(WHEEL_ZOOM_DELAY_MS).toBe(1000);
    expect(wheelGoesToPage(5000, 4500, 0)).toBe(true); // arrived half a second ago
    expect(wheelGoesToPage(5000, 1000, 4800)).toBe(true); // the page scrolled just now
    expect(wheelGoesToPage(5000, 1000, 3900)).toBe(false); // rested over a second: zoom
  });
});

describe('what is new after an update', () => {
  it('shows only when this browser used an older version before', () => {
    expect(updatedSince(null, '2.18.2')).toBe(false); // first visit
    expect(updatedSince('2.18.2', '2.18.2')).toBe(false);
    expect(updatedSince('2.18.1', '2.18.2')).toBe(true);
  });
});
