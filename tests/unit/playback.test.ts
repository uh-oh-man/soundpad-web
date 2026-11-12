import { describe, expect, it, vi } from 'vitest';
// The app's own instance talks to the controller; these tests build theirs on a stand-in page.
vi.mock('../../web/src/controller', () => ({ controller: {} }));
import { guestHeld, intentKey, PlaybackIntents, type PlaybackHost } from '../../web/src/playback';
import { Disconnected, EMPTY, type Command, type Snapshot } from '../../web/src/protocol';

const epoch = '11111111-1111-4111-8111-111111111111';
const asset = '22222222-2222-4222-8222-222222222222';
type Sent = {
  op: Command['op'];
  fields: Partial<Command>;
  /** Sent the quiet way (a refusal shows nothing by itself) or the usual way. */
  quiet: boolean;
  resolve: (data?: unknown) => void;
  reject: (e: Error) => void;
};
/** A page with a companion or host behind it that answers only when the test says so. */
function page({
  mode = 'host',
  companion = '2.35.0',
  hostSite = '',
  status,
}: { mode?: 'host' | 'guest'; companion?: string; hostSite?: string; status?: string } = {}) {
  const view = {
    mode,
    status: status ?? (mode === 'host' ? 'Host ready' : 'Connected'),
    state: {
      ...EMPTY,
      available: true,
      companionVersion: companion,
      library: { ...EMPTY.library, version: 'library-1' },
    } as Snapshot,
  };
  const sent: Sent[] = [];
  const notices: string[] = [];
  const dismissed: string[] = [];
  let clock = 0;
  const call =
    (quiet: boolean) =>
    (op: Command['op'], fields: Partial<Command> = {}) =>
      new Promise<unknown>((resolve, reject) => sent.push({ op, fields, quiet, resolve, reject }));
  const host: PlaybackHost = {
    view: () => view,
    hostSite: () => hostSite,
    send: call(true),
    run: call(false),
    notify: (message) => {
      notices.push(message);
      return `notice-${notices.length}`;
    },
    dismiss: (id) => dismissed.push(id),
    now: () => clock,
  };
  return {
    intents: new PlaybackIntents(host),
    view,
    sent,
    notices,
    dismissed,
    ops: () => sent.map((s) => s.op),
    wait: (ms: number) => (clock += ms),
    /** What the next report from the companion says is playing. */
    report: (playback: Partial<Snapshot['playback']>, more: Partial<Snapshot> = {}) => {
      view.state = { ...view.state, ...more, playback: { ...view.state.playback, ...playback } };
    },
  };
}
const played = { played: true, epoch };
const superseded = { superseded: true, reason: 'newer' };

describe('playback intents (2.35)', () => {
  it('sends every click at once, with the library version of that moment', async () => {
    const p = page();
    const first = p.intents.play('a');
    p.view.state = { ...p.view.state, library: { ...p.view.state.library, version: 'library-2' } };
    const second = p.intents.startAt('b', 250);
    const third = p.intents.region('c', 100, 900, true);
    const fourth = p.intents.reverse('d');
    const fifth = p.intents.audioPlay(asset, 'reversed');
    const sixth = p.intents.stop();
    // Nothing waited for an earlier answer.
    expect(p.sent.map((s) => [s.op, s.fields])).toEqual([
      ['play', { soundId: 'a', libraryVersion: 'library-1' }],
      ['startAt', { soundId: 'b', libraryVersion: 'library-2', positionMs: 250 }],
      [
        'region',
        { soundId: 'c', libraryVersion: 'library-2', positionMs: 100, endMs: 900, repeat: true },
      ],
      ['reverse', { soundId: 'd', libraryVersion: 'library-2' }],
      ['audioPlay', { assetId: asset, orientation: 'reversed' }],
      ['stop', {}],
    ]);
    expect(p.sent.every((s) => s.quiet)).toBe(true);
    for (const s of p.sent) s.resolve(s.op === 'stop' ? undefined : played);
    expect((await first).result).toBe('played');
    expect((await sixth).result).toBe('done');
    await Promise.all([second, third, fourth, fifth]);
    expect(p.notices).toEqual([]);
  });

  it('stays quiet about a click the companion says was superseded', async () => {
    const p = page();
    const click = p.intents.play('a');
    p.sent[0].resolve(superseded);
    expect(await click).toEqual({ result: 'superseded', data: superseded });
    for (const reason of ['expired', 'suspended']) {
      const again = p.intents.play('a');
      p.sent.at(-1)!.resolve({ superseded: true, reason });
      expect((await again).result).toBe('superseded');
    }
    expect(p.notices).toEqual([]);
    expect(p.ops()).toEqual(['play', 'play', 'play']); // never retried
  });

  it('shows nothing for an older click that fails after a newer one was made', async () => {
    const p = page();
    const older = p.intents.play('a');
    const newer = p.intents.play('b');
    p.sent[0].reject(Error('Soundpad is not responding.'));
    expect((await older).result).toBe('failed');
    expect(p.notices).toEqual([]);
    p.sent[1].resolve(played);
    expect((await newer).result).toBe('played');
    expect(p.notices).toEqual([]);
  });

  it('shows one notice for a repeated failure and replaces it when the cause changes', async () => {
    const p = page();
    async function fails(message: string) {
      const click = p.intents.play('a');
      p.sent.at(-1)!.reject(Error(message));
      expect((await click).result).toBe('failed');
    }
    await fails('Soundpad is unavailable.');
    await fails('Soundpad is unavailable.');
    await fails('Soundpad is unavailable.');
    expect(p.notices).toEqual(['Soundpad is unavailable.']);
    // A different cause takes the place of the one on screen instead of stacking under it.
    await fails('The host has disabled this action.');
    expect(p.notices).toEqual(['Soundpad is unavailable.', 'The host has disabled this action.']);
    expect(p.dismissed).toEqual(['notice-1']);
    // Once the notice has had its time on screen, the same failure may be said again.
    p.wait(8001);
    await fails('The host has disabled this action.');
    expect(p.notices).toHaveLength(3);
    expect(p.ops()).toHaveLength(5); // five clicks, five commands: none was sent twice
  });

  it('drops clicks made while disconnected instead of sending them later', async () => {
    for (const mode of ['host', 'guest'] as const) {
      const p = page({ mode, status: 'Reconnecting' });
      expect(await p.intents.play('a')).toEqual({ result: 'dropped' });
      expect((await p.intents.stop()).result).toBe('dropped');
      expect((await p.intents.audioPlay(asset)).result).toBe('dropped');
      await p.intents.position('a', 100);
      p.view.status = mode === 'host' ? 'Host ready' : 'Connected';
      expect(p.sent).toEqual([]);
      expect(p.notices).toEqual([]);
      expect(p.intents.getPending()).toBe('');
    }
  });

  it('adds no notice when the connection is lost under a click', async () => {
    const p = page();
    const click = p.intents.play('a');
    p.sent[0].reject(new Disconnected('Companion disconnected.'));
    expect((await click).result).toBe('failed');
    expect(p.notices).toEqual([]);
  });

  it('marks a click as pending until it is answered, and moves the mark to a newer click', async () => {
    const p = page();
    const changes: string[] = [];
    const unsubscribe = p.intents.subscribe(() => changes.push(p.intents.getPending()));
    const a = p.intents.play('a');
    expect(p.intents.getPending()).toBe(intentKey.play('a'));
    const b = p.intents.reverse('b');
    expect(p.intents.getPending()).toBe(intentKey.reverse('b'));
    // The older click's answer must not clear the newer click's mark.
    p.sent[0].resolve(superseded);
    await a;
    expect(p.intents.getPending()).toBe(intentKey.reverse('b'));
    p.sent[1].resolve(played);
    await b;
    expect(p.intents.getPending()).toBe('');
    expect(changes).toEqual([intentKey.play('a'), intentKey.reverse('b'), '']);
    unsubscribe();
    const c = p.intents.audioPlay(asset);
    expect(p.intents.getPending()).toBe(intentKey.asset(asset, 'normal'));
    p.sent[2].reject(Error('Audio preparation failed.'));
    await c;
    expect(p.intents.getPending()).toBe(''); // a refusal ends the wait too
    expect(changes).toHaveLength(3);
  });

  it('decides between seek and start from the latest report, not from an earlier one', async () => {
    const p = page();
    // Nothing playing: a waveform click starts the sound there.
    const start = p.intents.position('a', 300);
    expect(p.sent[0]).toMatchObject({ op: 'startAt', fields: { soundId: 'a', positionMs: 300 } });
    p.sent[0].resolve(played);
    await start;
    // The report now says it plays: the next click on it is a seek with that playback's id.
    p.report({ status: 'PLAYING', soundId: 'a', epoch });
    const seek = p.intents.position('a', 700);
    expect(p.sent[1]).toMatchObject({
      op: 'seek',
      fields: { soundId: 'a', epoch, positionMs: 700 },
    });
    p.sent[1].resolve();
    await seek;
    // Someone else started another sound meanwhile: no seek with the old id, a fresh start instead.
    p.report({ status: 'PLAYING', soundId: 'b', epoch: '33333333-3333-4333-8333-333333333333' });
    const restart = p.intents.position('a', 900);
    expect(p.sent[2]).toMatchObject({ op: 'startAt', fields: { soundId: 'a', positionMs: 900 } });
    p.sent[2].resolve(played);
    await restart;
    expect(p.notices).toEqual([]);
  });

  it('starts instead of seeking while a start of its own is still unanswered', async () => {
    const p = page();
    p.report({ status: 'PLAYING', soundId: 'a', epoch });
    const other = p.intents.play('b');
    // The report still names "a", but "b" is about to replace it: a seek would aim at a dead playback.
    const click = p.intents.position('a', 400);
    expect(p.ops()).toEqual(['play', 'startAt']);
    p.sent[0].resolve(superseded);
    p.sent[1].resolve(played);
    await Promise.all([other, click]);
  });

  it('resumes a paused sound once after seeking it', async () => {
    const p = page();
    p.report({ status: 'PAUSED', soundId: 'a', epoch, positionRevision: 4 });
    const click = p.intents.position('a', 500);
    expect(p.ops()).toEqual(['seek']);
    p.sent[0].resolve();
    await vi.waitFor(() => expect(p.ops()).toEqual(['seek', 'pause']));
    expect(p.sent[1].fields).toEqual({ soundId: 'a', epoch });
    p.sent[1].resolve();
    await click;
  });

  it('treats a seek or pause of a playback that is gone as quiet, old companions included', async () => {
    const p = page({ companion: '2.34.2' });
    p.report({ status: 'PLAYING', soundId: 'a', epoch });
    const pause = p.intents.pause();
    expect(p.sent[0]).toMatchObject({ op: 'pause', fields: { soundId: 'a', epoch } });
    // Before 2.35 the companion refused these with an error.
    p.sent[0].reject(Error('Playback changed. This control belongs to an older sound.'));
    expect((await pause).result).toBe('superseded');
    const seek = p.intents.seek(250);
    p.sent[1].resolve({ superseded: true, reason: 'stale' });
    await seek;
    expect(p.notices).toEqual([]);
    // With nothing playing there is nothing to pause or seek, so nothing is sent.
    p.report({ status: 'STOPPED', soundId: null, epoch: null });
    expect((await p.intents.pause()).result).toBe('dropped');
    await p.intents.seek(10);
    expect(p.ops()).toEqual(['pause', 'seek']);
  });

  it('shows a real seek failure once', async () => {
    const p = page();
    p.report({ status: 'PLAYING', soundId: 'a', epoch });
    const pause = p.intents.pause();
    p.sent[0].reject(Error('The host has disabled this action.'));
    expect((await pause).result).toBe('failed');
    expect(p.notices).toEqual(['The host has disabled this action.']);
  });
});

describe('Panic (2.35)', () => {
  it('sends enable as the newest intent and disable as a plain command', async () => {
    const p = page();
    const start = p.intents.play('a');
    const panic = p.intents.panic();
    expect(p.sent[1]).toMatchObject({ op: 'panic', fields: { action: 'enable' } });
    expect(p.intents.getPending()).toBe(intentKey.panic);
    // The start it cut off was refused in the old way by some path: still nothing to show.
    p.sent[0].reject(Error('Busy. Try again.'));
    p.sent[1].resolve();
    expect((await start).result).toBe('failed');
    expect((await panic).result).toBe('done');
    expect(p.notices).toEqual([]);
    const resume = p.intents.resume();
    expect(p.sent[2]).toMatchObject({ op: 'panic', fields: { action: 'disable' } });
    expect(p.intents.getPending()).toBe(''); // it starts nothing
    p.sent[2].resolve();
    expect((await resume).result).toBe('done');
  });

  it('holds a guest’s clicks while the host has paused guest playback, and never the host’s', async () => {
    const guest = page({ mode: 'guest', hostSite: '2.35.0' });
    guest.report({ status: 'PLAYING', soundId: 'a', epoch }, { playbackSuspended: true });
    expect(guestHeld(guest.view.state, false)).toBe(true);
    expect((await guest.intents.play('a')).result).toBe('dropped');
    expect((await guest.intents.stop()).result).toBe('dropped');
    expect((await guest.intents.pause()).result).toBe('dropped');
    await guest.intents.position('a', 100);
    expect(guest.sent).toEqual([]);
    expect(guest.notices).toEqual([]);
    // Resumed: the next click goes out.
    guest.report({}, { playbackSuspended: false });
    void guest.intents.play('a');
    expect(guest.ops()).toEqual(['play']);

    const host = page();
    host.report({}, { playbackSuspended: true });
    expect(guestHeld(host.view.state, true)).toBe(false);
    void host.intents.play('a');
    expect(host.ops()).toEqual(['play']);
  });
});

describe('make and play (2.35)', () => {
  const trim = { assetId: asset, positionMs: 0, endMs: 500, action: 'none' } as const;
  const copy = '44444444-4444-4444-8444-444444444444';

  it('is one command with autoplay on a 2.35 companion', async () => {
    const p = page();
    expect(p.intents.autoplays()).toBe(true);
    const made = p.intents.make('trimAudio', trim, 'make:x');
    expect(p.sent).toHaveLength(1);
    expect(p.sent[0]).toMatchObject({ op: 'trimAudio', fields: { ...trim, autoplay: true } });
    expect(p.sent[0].quiet).toBe(false); // failing to make the copy is still shown
    expect(p.intents.getPending()).toBe('make:x');
    p.sent[0].resolve({ assetId: copy, played: true, epoch });
    expect(await made).toEqual({ data: { assetId: copy, played: true, epoch }, played: true });
    expect(p.ops()).toEqual(['trimAudio']); // no follow-up audioPlay
    expect(p.intents.getPending()).toBe('');
  });

  it('reports a copy that was made but lost to a newer click as not played', async () => {
    const p = page();
    const made = p.intents.make('mimicClip', { mimicEnd: 1, positionMs: 0, endMs: 900 });
    const newer = p.intents.play('a');
    p.sent[0].resolve({ assetId: copy, played: false });
    p.sent[1].resolve(played);
    expect((await made).played).toBe(false);
    await newer;
    expect(p.ops()).toEqual(['mimicClip', 'play']);
    expect(p.notices).toEqual([]);
  });

  it('keeps two commands for a companion before 2.35', async () => {
    const p = page({ companion: '2.34.2' });
    expect(p.intents.autoplays()).toBe(false);
    const made = p.intents.make('mimicLibraryImport', { sourceId: copy });
    expect(p.sent[0].fields).toEqual({ sourceId: copy }); // no field the companion does not know
    p.sent[0].resolve({ assetId: copy });
    await vi.waitFor(() => expect(p.ops()).toEqual(['mimicLibraryImport', 'audioPlay']));
    expect(p.sent[1].fields).toEqual({ assetId: copy, orientation: 'normal' });
    p.sent[1].resolve({ assetId: copy, state: 'ready' });
    expect((await made).played).toBe(true);
  });

  it('skips the follow-up play when this person clicked something else meanwhile', async () => {
    const p = page({ companion: '2.34.2' });
    const made = p.intents.make('trimAudio', trim);
    const stop = p.intents.stop();
    p.sent[1].resolve();
    await stop;
    p.sent[0].resolve({ assetId: copy });
    expect(await made).toEqual({ data: { assetId: copy }, played: false });
    expect(p.ops()).toEqual(['trimAudio', 'stop']);
  });

  it('lets a guest use autoplay only through a host tab that knows the field', async () => {
    // A 2.35 companion behind a host tab that loaded an older website: the tab's strict command
    // check would disconnect the guest, so the guest keeps to two commands.
    for (const hostSite of ['', '2.34.2']) {
      const p = page({ mode: 'guest', hostSite });
      expect(p.intents.autoplays()).toBe(false);
      void p.intents.make('trimAudio', trim).catch(() => {});
      expect(p.sent[0].fields).toEqual(trim);
    }
    const current = page({ mode: 'guest', hostSite: '2.35.0' });
    expect(current.intents.autoplays()).toBe(true);
    void current.intents.make('trimAudio', trim).catch(() => {});
    expect(current.sent[0].fields).toEqual({ ...trim, autoplay: true });
    // A current tab in front of an older companion: the companion would not know the field.
    const older = page({ mode: 'guest', hostSite: '2.35.0', companion: '2.34.2' });
    expect(older.intents.autoplays()).toBe(false);
    // The host's own page needs no relay.
    expect(page({ mode: 'host', hostSite: '' }).intents.autoplays()).toBe(true);
  });

  it('passes on a refusal to make the copy and clears its mark', async () => {
    const p = page();
    const made = p.intents.make('trimAudio', trim, 'make:x');
    p.sent[0].reject(Error('Another copy is being made.'));
    await expect(made).rejects.toThrow('Another copy is being made.');
    expect(p.intents.getPending()).toBe('');
    // The usual command path showed it; the playback path adds no second notice.
    expect(p.notices).toEqual([]);
  });

  it('does not let a slow copy hold up a play button', async () => {
    const p = page();
    void p.intents.make('trimAudio', trim, 'make:x').catch(() => {});
    const click = p.intents.audioPlay(asset);
    expect(p.ops()).toEqual(['trimAudio', 'audioPlay']);
    expect(p.intents.getPending()).toBe(intentKey.asset(asset, 'normal'));
    p.sent[1].resolve(played);
    expect((await click).result).toBe('played');
  });
});
