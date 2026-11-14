/**
 * The example session's library: freely licensed sounds served from `demo/` next to the page (see
 * web/public/demo/CREDITS.md for each file's source and licence). Nothing here is fetched until the
 * visitor presses "Try the example".
 */

export type DemoWord = { w: string; startMs: number; endMs: number };
export type DemoSound = {
  /** Stable key, also the file's base name. */
  key: string;
  title: string;
  file: string;
  /** Artwork beside the sound file, a 128 x 128 PNG. */
  art?: string;
  category: string;
  /** Length measured from the file's MP3 frames; the decoded length replaces it once loaded. */
  durationMs: number;
  /** Speech clips: the published wording with approximate word times (about ±0.15 s). */
  words?: DemoWord[];
};

export const DEMO_SOUNDS: DemoSound[] = [
  {
    key: 'air-horn',
    title: 'Air Horn',
    file: 'air-horn.mp3',
    art: 'art/air-horn.png',
    category: 'Reactions',
    durationMs: 2377,
  },
  {
    key: 'applause',
    title: 'Applause',
    file: 'applause.mp3',
    category: 'Reactions',
    durationMs: 9404,
  },
  {
    key: 'audience-laugh',
    title: 'Audience Laugh',
    file: 'audience-laugh.mp3',
    category: 'Reactions',
    durationMs: 7632,
  },
  {
    key: 'crowd-cheer',
    title: 'Crowd Cheer',
    file: 'crowd-cheer.mp3',
    category: 'Reactions',
    durationMs: 6432,
  },
  {
    key: 'wrong-buzzer',
    title: 'Wrong Answer',
    file: 'wrong-buzzer.mp3',
    art: 'art/wrong-buzzer.png',
    category: 'Reactions',
    durationMs: 1149,
  },
  {
    key: 'rimshot',
    title: 'Rimshot',
    file: 'rimshot.mp3',
    category: 'Reactions',
    durationMs: 2011,
  },
  {
    key: 'cymbal-crash',
    title: 'Cymbal Crash',
    file: 'cymbal-crash.mp3',
    category: 'Impacts',
    durationMs: 5564,
  },
  {
    key: 'firecracker',
    title: 'Firecracker',
    file: 'firecracker.mp3',
    art: 'art/firecracker.png',
    category: 'Impacts',
    durationMs: 2352,
  },
  {
    key: 'door-knock',
    title: 'Door Knock',
    file: 'door-knock.mp3',
    category: 'Impacts',
    durationMs: 2011,
  },
  {
    key: 'service-bell',
    title: 'Service Bell',
    file: 'service-bell.mp3',
    art: 'art/service-bell.png',
    category: 'Impacts',
    durationMs: 3960,
  },
  {
    key: 'boing',
    title: 'Boing',
    file: 'boing.mp3',
    art: 'art/boing.png',
    category: 'Cartoon',
    durationMs: 2232,
  },
  {
    key: 'whoosh',
    title: 'Whoosh',
    file: 'whoosh.mp3',
    art: 'art/whoosh.png',
    category: 'Cartoon',
    durationMs: 2208,
  },
  {
    key: 'cat-meow',
    title: 'Cat Meow',
    file: 'cat-meow.mp3',
    art: 'art/cat-meow.png',
    category: 'Animals',
    durationMs: 1541,
  },
  {
    key: 'dog-bark',
    title: 'Dog Bark',
    file: 'dog-bark.mp3',
    category: 'Animals',
    durationMs: 4056,
  },
  {
    key: 'drum-roll',
    title: 'Drum Roll',
    file: 'drum-roll.mp3',
    art: 'art/drum-roll.png',
    category: 'Music',
    durationMs: 8304,
  },
  { key: 'fanfare', title: 'Fanfare', file: 'fanfare.mp3', category: 'Music', durationMs: 5808 },
  {
    key: 'sousa-columbias-pride',
    title: "Columbia's Pride (Sousa)",
    file: 'sousa-columbias-pride.mp3',
    art: 'art/sousa-columbias-pride.png',
    category: 'Music',
    durationMs: 35304,
  },
  {
    key: 'apollo11-small-step',
    title: 'One Small Step (Apollo 11)',
    file: 'apollo11-small-step.mp3',
    art: 'art/apollo11-small-step.png',
    category: 'Speech',
    durationMs: 6713,
    words: [
      { w: "That's", startMs: 100, endMs: 290 },
      { w: 'one', startMs: 320, endMs: 540 },
      { w: 'small', startMs: 860, endMs: 1170 },
      { w: 'step', startMs: 1180, endMs: 1510 },
      { w: 'for', startMs: 1700, endMs: 1810 },
      { w: 'man,', startMs: 1850, endMs: 2180 },
      { w: 'one', startMs: 3810, endMs: 4000 },
      { w: 'giant', startMs: 4870, endMs: 5120 },
      { w: 'leap', startMs: 5240, endMs: 5400 },
      { w: 'for', startMs: 5570, endMs: 5670 },
      { w: 'mankind.', startMs: 5750, endMs: 6550 },
    ],
  },
  {
    key: 'apollo11-eagle-landed',
    title: 'The Eagle Has Landed (Apollo 11)',
    file: 'apollo11-eagle-landed.mp3',
    art: 'art/apollo11-eagle-landed.png',
    category: 'Speech',
    durationMs: 3161,
    words: [
      { w: 'Houston,', startMs: 300, endMs: 680 },
      { w: 'Tranquility', startMs: 740, endMs: 1380 },
      { w: 'Base', startMs: 1440, endMs: 1620 },
      { w: 'here.', startMs: 1620, endMs: 1740 },
      { w: 'The', startMs: 2040, endMs: 2140 },
      { w: 'Eagle', startMs: 2140, endMs: 2440 },
      { w: 'has', startMs: 2440, endMs: 2640 },
      { w: 'landed.', startMs: 2700, endMs: 3000 },
    ],
  },
];

/** Category order in the sidebar, and the sound whose artwork each category shows. */
export const DEMO_CATEGORIES: { name: string; art: string }[] = [
  { name: 'Reactions', art: 'air-horn' },
  { name: 'Impacts', art: 'firecracker' },
  { name: 'Cartoon', art: 'boing' },
  { name: 'Animals', art: 'cat-meow' },
  { name: 'Music', art: 'drum-roll' },
  { name: 'Speech', art: 'apollo11-small-step' },
];

/**
 * What Mimic "hears" in the example: the two Apollo 11 clips placed on a 20-second loop, as if someone
 * kept saying them in a voice chat. The live buffer scrolls in real time over this loop.
 */
export const MIMIC_LOOP_MS = 20000;
export const MIMIC_PLACEMENTS: { key: string; atMs: number }[] = [
  { key: 'apollo11-small-step', atMs: 1000 },
  { key: 'apollo11-eagle-landed', atMs: 11000 },
];
export const MIMIC_SOURCE = {
  id: 'example:apollo-11',
  name: 'Example: Apollo 11 radio (repeats every 20 s)',
  kind: 'app',
};

export type DemoLine = { startMs: number; endMs: number; text: string };

/** Groups words into transcript lines: a new line after a full stop or a pause longer than 1.2 s. */
export function wordLines(words: DemoWord[], offsetMs = 0): DemoLine[] {
  const lines: DemoLine[] = [];
  let current: DemoWord[] = [];
  const flush = () => {
    if (!current.length) return;
    lines.push({
      startMs: current[0].startMs + offsetMs,
      endMs: current[current.length - 1].endMs + offsetMs,
      text: current.map((w) => w.w).join(' '),
    });
    current = [];
  };
  words.forEach((word, i) => {
    current.push(word);
    const next = words[i + 1];
    if (/[.!?]$/.test(word.w) || (next && next.startMs - word.endMs > 1200)) flush();
  });
  flush();
  return lines;
}

/** The loop's transcript lines, in loop time. */
export function mimicLoopLines(): DemoLine[] {
  return MIMIC_PLACEMENTS.flatMap(({ key, atMs }) =>
    wordLines(DEMO_SOUNDS.find((s) => s.key === key)?.words ?? [], atMs),
  );
}

/** Words that fall inside [fromMs, toMs) of a clip, moved so the clip starts at 0. */
export function wordsWithin(words: DemoWord[] | undefined, fromMs: number, toMs: number) {
  return (words ?? [])
    .filter((w) => w.endMs > fromMs && w.startMs < toMs)
    .map((w) => ({
      w: w.w,
      startMs: Math.max(0, w.startMs - fromMs),
      endMs: Math.min(toMs, w.endMs) - fromMs,
    }));
}
