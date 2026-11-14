import { supports } from './version';

/**
 * Why the example cannot do something that needs the host's PC (Stage G): the example's model refuses
 * with these, and the page shows the same words next to the control (HostOnlyTag) without loading the
 * example's code.
 */
export const HOST_ONLY = {
  keep: 'Not available in the example: Keep permanently writes a file and a Soundpad entry on the host’s PC.',
  spotify:
    'Not available in the example: Spotify controls work with the Spotify app on the host’s PC.',
  hotkey:
    'Not available in the example: the hotkey works only on the host’s PC, where it listens for a key in every app. A web page cannot do that.',
  library:
    'Not available in the example: Voice Replay Studio’s library is read from the host’s PC.',
} as const;

/** Where a host page's guest room runs (2.36); see the controller's `hosting`. */
export type Hosting = 'tab' | 'window' | 'view';

/** Shown in a view of the companion window's session (2.36). */
export const VIEW_NOTE =
  'Hosting runs in the Soundpad Web window. This tab is a view; closing it does not affect guests.';
/** Shown in a view when another browser tab hosts (a companion started with --no-window). */
export const TAB_VIEW_NOTE =
  'Hosting runs in another Soundpad Web tab. This tab is a view; closing it does not affect guests.';

/**
 * What a host page says about keeping the session running (2.36). Before the companion window, the
 * host tab ran the guest room, so closing it ended guest control. A page that is the window's, or a
 * view of a session another page runs, says so instead. Older companions always get the tab copy.
 */
export function hostCopy(
  hosting: Hosting,
  companionVersion: string | null | undefined,
  ownerWindow = true,
) {
  const place: Hosting =
    hosting !== 'tab' && supports(companionVersion, 'desktopSession') ? hosting : 'tab';
  if (place === 'window')
    return {
      place,
      sidebar: 'This window keeps your session running.',
      status: 'The companion controls Soundpad. This window connects your guests.',
      foot: 'This window keeps the session running. Closing it quits the companion and ends guest control.',
    };
  if (place === 'view')
    return {
      place,
      note: ownerWindow ? VIEW_NOTE : TAB_VIEW_NOTE,
      sidebar: ownerWindow
        ? 'The Soundpad Web window keeps your session running.'
        : 'Another Soundpad Web tab keeps your session running.',
      status: 'The companion controls Soundpad. This tab is a view of your session.',
      foot: ownerWindow
        ? 'The Soundpad Web window keeps the session running.'
        : 'Another Soundpad Web tab keeps the session running.',
    };
  return {
    place,
    sidebar: 'Keep this tab open to host.',
    status: 'The companion controls Soundpad. This tab connects your guests.',
    foot: 'Keep the host tab and companion open. Closing either ends guest control.',
  };
}

/** What the example session says instead: it runs in this tab and reaches no Soundpad. */
export const DEMO_COPY = {
  place: 'tab' as Hosting,
  note: undefined as string | undefined,
  sidebar: 'Example session: it runs in this tab and nothing reaches Soundpad.',
  status: 'An example running in this browser. No companion or Soundpad is involved.',
  foot: 'Leaving the example clears its recordings, drafts, clips and copies. Favourites and view settings stay on this browser.',
};
