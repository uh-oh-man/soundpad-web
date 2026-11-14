import { useEffect, useId, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Gauge } from 'lucide-react';
import { z } from 'zod';
import type { Snapshot, Sound } from '../protocol';
import { controller } from '../controller';
import { HELD_BY_HOST, playback, usePendingIntent } from '../playback';
import { supports } from '../version';
import { peekWaveform } from '../waveform-cache';
import { removeNewTempAudio } from '../remove-temp';
import { requestEditor } from '../shell';

/** The quick speeds (Stage G spec 5.3); "More in editor…" opens the sound's editor for the rest. */
export const QUICK_SPEEDS = [0.5, 0.75, 1.25, 1.5, 2] as const;
/** The longest source an edited copy may name (the command schema's limit); the companion clamps it. */
const LONGEST_MS = 1800000;
const pendingKey = (soundId: string) => `speed:${soundId}`;
const label = (speed: number) => `${speed}×`;

/**
 * Where a quick Speed copy comes from: the tile's Temp asset, or (companion 2.43, gate soundEdit) the
 * library sound itself. Null when this tile has no source the companion can copy.
 */
export function speedSource(sound: Sound, state: Snapshot) {
  if (sound.assetId) return { assetId: sound.assetId };
  if (supports(state.companionVersion, 'soundEdit')) return { soundId: sound.id };
  return null;
}

/**
 * Whether the Speed button shows on a tile: the companion makes speed copies (Edit copy and its free
 * speed), this person may make copies (host, or a guest the host lets upload), and the tile has a
 * source the companion can copy.
 */
export function canQuickSpeed(sound: Sound, state: Snapshot, host: boolean) {
  return (
    supports(state.companionVersion, 'trim') &&
    supports(state.companionVersion, 'speed') &&
    (host || state.permissions.guestUploads !== false) &&
    !!speedSource(sound, state)
  );
}

/**
 * Makes a Temp copy of `sound` at `speed` and plays it when it is ready (the same "make and play" as
 * Edit copy's Save & play): one command on a 2.35 companion, which plays it only if nothing newer was
 * asked for meanwhile. Pitch is kept where the companion can.
 */
export async function makeSpeedCopy(sound: Sound, state: Snapshot, host: boolean, speed: number) {
  const source = speedSource(sound, state);
  if (!source) return;
  const known = sound.durationMs ?? peekWaveform(state.hostId, sound.id)?.durationMs ?? LONGEST_MS;
  const keepPitch = supports(state.companionVersion, 'keepPitch');
  try {
    const { data, played } = await playback.make(
      'trimAudio',
      {
        ...source,
        positionMs: 0,
        endMs: Math.min(LONGEST_MS, Math.max(100, Math.round(known))),
        action: 'speed',
        speed,
        ...(keepPitch && { keepPitch: true }),
      },
      pendingKey(sound.id),
    );
    const copy = z.object({ assetId: z.string().max(64) }).safeParse(data);
    // Undo, as after Edit copy (2.28; guests since 2.32): takes the new copy straight back out.
    const undo =
      (host || supports(state.companionVersion, 'guestUndo')) && copy.success
        ? {
            label: 'Undo',
            run: () =>
              void removeNewTempAudio(copy.data.assetId).then(
                () => controller.notify('Copy removed from Temp.', 'success'),
                () => {},
              ),
          }
        : undefined;
    controller.notify(
      played
        ? `${label(speed)} copy added to Temp and playing. The original is unchanged.`
        : `${label(speed)} copy added to Temp. The original is unchanged.`,
      'success',
      undo,
    );
  } catch {
    // The controller already showed the refusal.
  }
}

type Place = { left: number; top: number; up: boolean };

/**
 * A tile's quick Speed button and its menu (Stage G spec 5.3). The menu is a `role="menu"` popover,
 * never a dialog. It opens with focus on the first speed; arrows, Home and End move; Escape and Tab
 * close it and focus returns to the button. It is portalled to the page so the tile's clipping never
 * cuts it off, and it opens upward when it would cross the player. While the copy is being made the
 * button is aria-disabled (it keeps focus) and presses are ignored.
 */
export function SpeedMenu({
  sound,
  state,
  host,
  disabled,
  held,
}: {
  sound: Sound;
  state: Snapshot;
  host: boolean;
  /** Not allowed to play now (disconnected, no Soundpad, no remote playback). */
  disabled: boolean;
  /** The host's Panic holds guest playback. */
  held: boolean;
}) {
  const [open, setOpen] = useState(false);
  const [place, setPlace] = useState<Place | null>(null);
  const button = useRef<HTMLButtonElement>(null);
  const menu = useRef<HTMLDivElement>(null);
  const id = 'speed-menu-' + useId().replace(/:/g, '');
  const busy = usePendingIntent() === pendingKey(sound.id);
  const name = `Speed for ${sound.name}`;

  function close(refocus = true) {
    setOpen(false);
    setPlace(null);
    if (refocus) button.current?.focus();
  }
  function items() {
    return [...(menu.current?.querySelectorAll<HTMLElement>('[role="menuitem"]') ?? [])];
  }

  // Placed before it paints: below the button, or above it when it would reach the player.
  useLayoutEffect(() => {
    if (!open || !button.current || !menu.current) return;
    const anchor = button.current.getBoundingClientRect();
    const { width, height } = menu.current.getBoundingClientRect();
    const player =
      parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--player-height')) ||
      0;
    const floor = innerHeight - player - 8;
    const up = anchor.bottom + 4 + height > floor && anchor.top - 4 - height >= 8;
    setPlace({
      left: Math.max(8, Math.min(anchor.right - width, innerWidth - width - 8)),
      top: up ? anchor.top - 4 - height : anchor.bottom + 4,
      up,
    });
  }, [open]);
  useEffect(() => {
    if (open && place) items()[0]?.focus();
  }, [open, place]);
  // An outside press, a scroll of the page behind it or a resize closes it (without taking focus).
  useEffect(() => {
    if (!open) return;
    const outside = (e: Event) => {
      const target = e.target as Node | null;
      if (target && (menu.current?.contains(target) || button.current?.contains(target))) return;
      close(false);
    };
    const away = () => close(false);
    document.addEventListener('pointerdown', outside, true);
    document.addEventListener('scroll', outside, true);
    window.addEventListener('resize', away);
    return () => {
      document.removeEventListener('pointerdown', outside, true);
      document.removeEventListener('scroll', outside, true);
      window.removeEventListener('resize', away);
    };
  }, [open]);

  function keys(e: React.KeyboardEvent) {
    const list = items(),
      at = list.indexOf(document.activeElement as HTMLElement);
    const move = (index: number) => list[(index + list.length) % list.length]?.focus();
    if (e.key === 'ArrowDown') move(at + 1);
    else if (e.key === 'ArrowUp') move(at - 1);
    else if (e.key === 'Home') move(0);
    else if (e.key === 'End') move(list.length - 1);
    else if (e.key === 'Escape' || e.key === 'Tab') close();
    else return;
    e.preventDefault();
    e.stopPropagation();
  }
  function choose(speed: number) {
    close();
    if (busy || held || disabled) return;
    void makeSpeedCopy(sound, state, host, speed);
  }

  return (
    <>
      <button
        ref={button}
        className="icon-button tile-speed"
        aria-label={name}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? id : undefined}
        title={held ? HELD_BY_HOST : busy ? 'Making the copy…' : 'Play a copy at another speed'}
        disabled={disabled}
        // Busy or held: keeps focus, and the press is ignored.
        aria-disabled={busy || held || undefined}
        onClick={() => {
          if (busy || held) return;
          if (open) close();
          else setOpen(true);
        }}
        onKeyDown={(e) => {
          if ((e.key === 'ArrowDown' || e.key === 'ArrowUp') && !open && !busy && !held) {
            e.preventDefault();
            setOpen(true);
          }
        }}
      >
        <Gauge size={14} aria-hidden="true" />
      </button>
      {open &&
        createPortal(
          <div
            ref={menu}
            id={id}
            className={`popover speed-menu ${place?.up ? 'opens-up' : ''}`}
            role="menu"
            aria-label={name}
            style={
              place
                ? { left: place.left, top: place.top }
                : { left: -9999, top: 0, visibility: 'hidden' }
            }
            onKeyDown={keys}
          >
            {QUICK_SPEEDS.map((speed) => (
              <button
                key={speed}
                role="menuitem"
                tabIndex={-1}
                className="tabular"
                onClick={() => choose(speed)}
              >
                {label(speed)}
              </button>
            ))}
            <button
              role="menuitem"
              tabIndex={-1}
              className="speed-more"
              onClick={() => {
                close();
                requestEditor({ kind: 'sound-edit', soundId: sound.id, focus: 'effects' });
              }}
            >
              More in editor…
            </button>
          </div>,
          document.body,
        )}
    </>
  );
}
