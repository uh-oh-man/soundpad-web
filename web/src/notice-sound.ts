import { useEffect } from 'react';
import { onNoticePushed, type NoticeKind } from './notifications';
import { usePref } from './prefs';

/**
 * Notice sounds (2.43, Settings → Appearance → Notice sounds: Subtle): a 60 ms sine blip in this
 * browser when a notice appears, 880 Hz for information and success, 440 Hz for errors, very quiet.
 * It never goes through Soundpad, is skipped while the tab is hidden, and plays at most once per
 * 250 ms. The audio context is made on the first press or key after the setting is on, as browsers
 * only allow sound after a gesture.
 */
let context: AudioContext | null = null;
let last = 0;

function blip(kind: NoticeKind) {
  if (!context || document.hidden) return;
  const now = performance.now();
  if (now - last < 250) return;
  last = now;
  try {
    const t = context.currentTime;
    const osc = context.createOscillator();
    const gain = context.createGain();
    osc.type = 'sine';
    osc.frequency.value = kind === 'error' ? 440 : 880;
    gain.gain.setValueAtTime(0.05, t);
    gain.gain.exponentialRampToValueAtTime(0.0001, t + 0.06);
    osc.connect(gain).connect(context.destination);
    osc.start(t);
    osc.stop(t + 0.06);
  } catch {
    // A suspended or closed context: no sound, nothing else changes.
  }
}

/** Mounted once by the app: plays the blip for each new notice while the setting is Subtle. */
export function useNoticeSounds() {
  const [setting] = usePref('noticeSounds');
  useEffect(() => {
    if (setting !== 'subtle') return;
    const gesture = () => {
      if (context) return;
      try {
        const Ctor =
          window.AudioContext ??
          (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
        if (Ctor) context = new Ctor();
      } catch {
        context = null;
      }
    };
    document.addEventListener('pointerdown', gesture, true);
    document.addEventListener('keydown', gesture, true);
    const stop = onNoticePushed((notice) => blip(notice.kind));
    return () => {
      document.removeEventListener('pointerdown', gesture, true);
      document.removeEventListener('keydown', gesture, true);
      stop();
    };
  }, [setting]);
}
