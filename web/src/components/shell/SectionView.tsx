import type { ReactNode } from 'react';
import { SectionHeader } from './SectionHeader';
import type { HeaderRibbonProps } from './HeaderRibbon';

const copy = {
  voices: { eyebrow: 'VOICE LIBRARY', title: 'Voices' },
  sentences: { eyebrow: 'SENTENCE BUILDER', title: 'Sentences' },
} as const;

/**
 * Voices or Sentences as a main view (2.43; dialogs before): the section's header with its ribbon, then
 * the section itself at the main column's full width. Leaving is the nav's Soundboard; Escape does
 * nothing at view level.
 */
export function SectionView({
  view,
  ribbon,
  children,
}: {
  view: 'voices' | 'sentences';
  ribbon?: HeaderRibbonProps;
  children: ReactNode;
}) {
  return (
    <>
      <SectionHeader
        className="section-heading"
        eyebrow={copy[view].eyebrow}
        title={copy[view].title}
        ribbon={ribbon}
      />
      <div className={`section-body section-${view}`}>{children}</div>
    </>
  );
}
