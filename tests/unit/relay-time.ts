// Simulated time for the relay transport tests.
import { vi } from 'vitest';

// WebCrypto finishes on a real thread, which fake timers do not wait for. If simulated time simply
// ran on, a busy computer would see simulated timeouts fire before real encryption had finished,
// and the tests would depend on machine speed. So every WebCrypto call is counted, and simulated
// time only moves while none is in flight. (Captured before any test installs fake timers.)
const realImmediate = setImmediate;
const turn = () => new Promise<void>((resolve) => realImmediate(resolve));
let inFlight = 0;
let counting = false;
function countCrypto() {
  if (counting) return;
  counting = true;
  const subtle = crypto.subtle as unknown as Record<
    string,
    (...args: unknown[]) => Promise<unknown>
  >;
  const names = ['digest', 'encrypt', 'decrypt', 'sign', 'deriveBits', 'deriveKey'];
  for (const name of [...names, 'importKey', 'exportKey', 'generateKey']) {
    const original = subtle[name].bind(subtle);
    subtle[name] = (...args) => {
      inFlight++;
      return original(...args).finally(() => inFlight--);
    };
  }
}

/** Fake timers that leave `setImmediate` real, as `advance` and `until` need. */
export function useFakeClock() {
  countCrypto();
  vi.useFakeTimers({
    toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'],
  });
}

/** Lets every started piece of real work (and what it starts in turn) finish. */
async function idle() {
  for (let quiet = 0; quiet < 2;) {
    await turn();
    quiet = inFlight ? 0 : quiet + 1;
  }
}

/** Moves simulated time forward in small steps, letting real crypto work finish between them. */
export async function advance(ms: number) {
  await idle();
  for (let t = 0; t < ms; t += 10) {
    await vi.advanceTimersByTimeAsync(Math.min(10, ms - t));
    await idle();
  }
}

/** Advances simulated time until `check` holds; fails after `limitMs` of simulated time. */
export async function until(check: () => boolean, limitMs = 30_000) {
  for (let t = 0; t < limitMs && !check(); t += 10) await advance(10);
  if (!check()) throw Error(`Still false after ${limitMs} ms of simulated time: ${check}`);
}
