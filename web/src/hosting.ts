import { supports } from './version';

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
