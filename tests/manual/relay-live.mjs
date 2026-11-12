// Opt-in live check of the relay fallback transport (web/src/network/relay) against real public
// Nostr relays. It is NOT part of `npm test`, because it uses the network.
//
// What it does: starts two relay links in this process (a "host" that listens and a "guest" that
// announces itself) under a random throwaway room and secret, lets them find each other through the
// relays, exchanges 1 KB and 40 KB messages both ways for about 20 seconds, sends one 256 KB
// snapshot, and prints how long each relay took to deliver. It publishes a few dozen encrypted
// ephemeral events (relays do not store those), like the app's LIVE_RELAY end-to-end tests.
//
// Usage (Node 24 or newer, from the repository root):
//   node tests/manual/relay-live.mjs                    the relays the app would use
//   node tests/manual/relay-live.mjs --relays=trystero  every relay in Trystero's built-in list
//   node tests/manual/relay-live.mjs --relays=wss://a.example,wss://b.example
//   node tests/manual/relay-live.mjs --seconds=20 --no-bulk
//   node tests/manual/relay-live.mjs --dry-run          in-memory relays, no network at all

// The project's lint settings only list globals for TypeScript files; this is the one Node script
// under tests/.
/* global process, Buffer, console, crypto, setTimeout, clearTimeout */
import { registerHooks } from 'node:module';

// The transport is TypeScript with extension-less imports, as the Vite build expects. Node removes
// the types by itself; this hook only supplies the missing ".ts".
registerHooks({
  resolve(specifier, context, nextResolve) {
    try {
      return nextResolve(specifier, context);
    } catch (error) {
      if (!/^\.{1,2}\//.test(specifier) || /\.[cm]?[jt]s$/.test(specifier)) throw error;
      return nextResolve(specifier + '.ts', context);
    }
  },
});
const { createRelayLink, DEFAULT_RELAYS } = await import('../../web/src/network/relay/index.ts');

const args = Object.fromEntries(
  process.argv.slice(2).map((a) => {
    const [name, value = 'true'] = a.replace(/^--/, '').split('=');
    return [name, value];
  }),
);
const seconds = Number(args.seconds ?? 20);
const dryRun = 'dry-run' in args;
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const hex = (n) => Buffer.from(crypto.getRandomValues(new Uint8Array(n))).toString('hex');

let relays = DEFAULT_RELAYS;
let webSocketFactory;
if (dryRun) {
  const { FakeHub } = await import('../unit/relay-hub.ts');
  const hub = new FakeHub();
  hub.add('wss://fast.test', { delayMs: () => 20 + Math.random() * 20 });
  hub.add('wss://slow.test', { delayMs: () => 150 + Math.random() * 200 });
  hub.add('wss://strict.test', { delayMs: 40, rateLimit: 3 });
  hub.add('wss://small.test', { delayMs: 40, maxEventBytes: 20_000 });
  hub.add('wss://dead.test', { dead: true });
  hub.add('wss://hole.test', { drop: true });
  relays = hub.urls;
  webSocketFactory = hub.factory;
} else if (args.relays === 'trystero')
  relays = (await import('@trystero-p2p/nostr')).defaultRelayUrls;
else if (args.relays && args.relays !== 'default') relays = args.relays.split(',');

// A fresh random room and secret every run: nothing here is tied to a real invitation.
const room = hex(16);
const secret = hex(24);
let phase = 'connect';
const started = Date.now();
const log = (...text) => console.log(`[${((Date.now() - started) / 1000).toFixed(1)}s]`, ...text);

/** One end of the test, with taps that record when each event went to and came from each relay. */
function end(selfId, role) {
  const sent = new Map(); // event id -> { phase, bytes, relays: Map(relay -> time written) }
  const answered = new Map(); // event id -> Map(relay -> time the relay's OK arrived)
  const received = new Map(); // event id -> Map(relay -> time received)
  const link = createRelayLink({
    room,
    secret,
    selfId,
    role,
    relays,
    webSocketFactory,
    debug: {
      sent: ({ id, relay, bytes, at }) => {
        if (!sent.has(id)) sent.set(id, { phase, bytes, relays: new Map() });
        sent.get(id).relays.set(relay, at);
      },
      received: ({ id, relay, at }) => {
        if (!received.has(id)) received.set(id, new Map());
        if (!received.get(id).has(relay)) received.get(id).set(relay, at);
      },
      answered: ({ id, relay, ok, at }) => {
        if (!ok) return;
        if (!answered.has(id)) answered.set(id, new Map());
        answered.get(id).set(relay, at);
      },
    },
  });
  const seen = [];
  const messages = [];
  link.onPeer((id, event) => seen.push(`${event} ${id}`));
  link.onMessage((from, value) => messages.push({ at: Date.now(), value }));
  return { link, sent, answered, received, seen, messages, selfId };
}

const host = end('live-host', 'host');
const guest = end('live-guest', 'guest');
const endToEnd = { '1 KB': [], '40 KB': [], bulk: [] };
const pending = new Map(); // message number -> { kind, at }
let counter = 0;

/** Sends one numbered test message and remembers when, so its arrival can be timed. */
function send(from, to, kind) {
  const n = ++counter;
  let value;
  if (kind === '1 KB') value = { n, pad: 'x'.repeat(1000) };
  else {
    value = crypto.getRandomValues(new Uint8Array(kind === '40 KB' ? 40_000 : 65_536));
    if (kind === 'bulk') value = Buffer.concat(Array(4).fill(value));
    new DataView(value.buffer, value.byteOffset).setUint32(0, n);
  }
  pending.set(n, { kind, at: Date.now() });
  // 40 KB goes on the control lane so that every relay carries it and can be timed.
  const options = kind === 'bulk' ? {} : { priority: 'control' };
  return from.link.send(to.selfId, value, options).then((result) => {
    if (result !== 'sent') log(`${kind} message ${n} from ${from.selfId}: ${result}`);
    return result;
  });
}
for (const side of [host, guest])
  side.link.onMessage((_, value) => {
    const n =
      value instanceof Uint8Array
        ? new DataView(value.buffer, value.byteOffset).getUint32(0)
        : value.n;
    const entry = pending.get(n);
    if (!entry) return;
    pending.delete(n);
    endToEnd[entry.kind].push(Date.now() - entry.at);
  });

const stats = (list) => {
  if (!list.length) return '-';
  const sorted = [...list].sort((a, b) => a - b);
  const median = sorted[Math.floor(sorted.length / 2)];
  return `${Math.round(median)} ms median, ${Math.round(sorted.at(-1))} ms max`;
};

/**
 * Per relay: of the events written to it in a size class, how many the other end got from it and
 * how fast, and how long the relay took to answer OK (which shows whether a slow relay is slow to
 * take events in or slow to hand them on).
 */
function perRelay(kind) {
  const rows = new Map(relays.map((url) => [url, { written: 0, times: [], acks: [] }]));
  for (const [from, to] of [
    [host, guest],
    [guest, host],
  ])
    for (const [id, event] of from.sent) {
      const big = event.bytes > 40_000;
      const match =
        kind === '1 KB'
          ? event.phase === 'exchange' && event.bytes > 1500 && event.bytes < 6000
          : kind === '40 KB'
            ? event.phase === 'exchange' && big
            : event.phase === 'bulk' && big;
      if (!match) continue;
      for (const [relay, at] of event.relays) {
        const row = rows.get(relay);
        row.written++;
        const arrived = to.received.get(id)?.get(relay);
        if (arrived !== undefined) row.times.push(arrived - at);
        const ok = from.answered.get(id)?.get(relay);
        if (ok !== undefined) row.acks.push(ok - at);
      }
    }
  return rows;
}

function report() {
  const status = { host: host.link.status(), guest: guest.link.status() };
  const classes = ['1 KB', '40 KB', 'bulk'].map((kind) => [kind, perRelay(kind)]);
  console.log('\nRelays (state as the host / as the guest, self-check round trip):');
  for (const url of relays) {
    const h = status.host.relays.find((r) => r.url === url);
    const g = status.guest.relays.find((r) => r.url === url);
    const check = [h, g].map((r) =>
      r?.latencyMs === undefined ? '-' : Math.round(r.latencyMs) + ' ms',
    );
    console.log(
      `  ${url.padEnd(38)} ${`${h?.state}/${g?.state}`.padEnd(22)} check ${check.join(' / ')}`,
    );
    for (const [kind, rows] of classes) {
      const row = rows.get(url);
      if (row.written)
        console.log(
          `      ${kind.padEnd(6)} delivered ${row.times.length}/${row.written}   ${stats(row.times)}   (relay's OK: ${stats(row.acks)})`,
        );
    }
    if (h?.controlOnly || g?.controlOnly)
      console.log(
        `      kept free of bulk by: ${[h?.controlOnly && 'host', g?.controlOnly && 'guest'].filter(Boolean).join(', ')}`,
      );
    const error = h?.error ?? g?.error;
    if (error) console.log(`      note: ${error}`);
  }
  console.log('\nMessages, from send() to onMessage() on the other end (fastest relay wins):');
  for (const kind of ['1 KB', '40 KB', 'bulk'])
    console.log(
      `  ${kind.padEnd(6)} ${endToEnd[kind].length} delivered   ${stats(endToEnd[kind])}`,
    );
  if (pending.size)
    console.log(`  not delivered: ${[...pending.values()].map((p) => p.kind).join(', ')}`);
  console.log(`\nLink state: host ${status.host.state}, guest ${status.guest.state}.`);
  console.log(`Peers seen: host [${host.seen.join(', ')}], guest [${guest.seen.join(', ')}].`);
  console.log(`Round trip through the relays: ${status.guest.rttMs ?? '-'} ms.`);
  console.log(`Ignored packets: host ${JSON.stringify(status.host.dropped)}`);
  console.log(`                 guest ${JSON.stringify(status.guest.dropped)}`);
  const events = host.sent.size + guest.sent.size;
  console.log(`Distinct events published: ${events}.`);
}

// Whatever happens, print what was measured and leave.
const giveUp = setTimeout(
  () => {
    log('Taking too long; stopping.');
    report();
    process.exit(1);
  },
  (seconds + 60) * 1000,
);

try {
  log(
    `${dryRun ? 'DRY RUN, in-memory relays. ' : ''}Opening ${relays.length} relays twice (host and guest)…`,
  );
  await Promise.all([host.link.start(), guest.link.start()]);
  log(`Relays checked: host ${host.link.status().state}, guest ${guest.link.status().state}.`);
  guest.link.broadcastHello();
  const pairing = Date.now();
  while (!(host.seen.includes('seen live-guest') && guest.seen.includes('seen live-host'))) {
    if (Date.now() - pairing > 20_000)
      throw Error('The two links did not find each other in 20 s.');
    await sleep(50);
  }
  log(`Host and guest found each other after ${Date.now() - pairing} ms.`);
  // Give slower relays a moment to finish their self-check, so they are measured too.
  await sleep(3000);

  phase = 'exchange';
  const rounds = Math.max(1, Math.round(seconds / 2));
  for (let round = 0; round < rounds; round++) {
    void send(guest, host, '1 KB');
    if (round % 3 === 1) void send(guest, host, '40 KB');
    await sleep(1000);
    void send(host, guest, '1 KB');
    if (round % 3 === 2) void send(host, guest, '40 KB');
    await sleep(1000);
  }
  await sleep(2000);
  if (!('no-bulk' in args)) {
    phase = 'bulk';
    const before = Date.now();
    const result = await send(host, guest, 'bulk');
    log(`256 KB snapshot: ${result} to the relays after ${Date.now() - before} ms.`);
    for (let waited = 0; waited < 10_000 && pending.size; waited += 100) await sleep(100);
  }
  phase = 'done';
  // Copies still on their way through slower relays count too.
  await sleep(3000);
  report();
  await Promise.all([host.link.stop(), guest.link.stop()]);
  clearTimeout(giveUp);
  process.exit(0);
} catch (error) {
  log('Failed:', error.message);
  report();
  process.exit(1);
}
