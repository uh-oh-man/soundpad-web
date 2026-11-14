import type { Snapshot } from '../../protocol';

/**
 * What the header ribbon is given (Stage G spec 6.3). Frozen in Phase 0: SectionHeader (WP-A) renders
 * the ribbon with these props, and WP-B implements it.
 */
export type HeaderRibbonProps = {
  /** The shared playback state; the ribbon follows only a web play it can identify (spec 6.3). */
  playback: Snapshot['playback'];
  /** Whose library the playing sound belongs to (the waveform cache's key). */
  hostId: string | null | undefined;
  /** Guest playback held by the host's Panic, or playback suspended: the ribbon settles. */
  held: boolean;
  /** False while this section is not the current view: nothing is drawn. */
  active: boolean;
};

/**
 * The audio-reactive header ribbon (Stage G spec 6). Phase 0 stub: draws nothing yet. WP-B owns it.
 */
export function HeaderRibbon(props: HeaderRibbonProps) {
  void props;
  return null;
}
