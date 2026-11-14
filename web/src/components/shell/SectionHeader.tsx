import type { ReactNode } from 'react';
import { HeaderRibbon, type HeaderRibbonProps } from './HeaderRibbon';

export type SectionHeaderProps = {
  /** The header's class: the Soundboard keeps "board-heading" (e2e and CSS hooks). */
  className: string;
  /** Small caps line above the title ("YOUR SOUNDBOARD"). */
  eyebrow?: ReactNode;
  title: ReactNode;
  /** Everything under the title in the title block: count line, guests, alerts. */
  children?: ReactNode;
  /** The header's buttons (rendered in `.board-actions`). */
  actions?: ReactNode;
  /** The ribbon behind the header; omitted for no ribbon. */
  ribbon?: HeaderRibbonProps;
};

/**
 * A section's header (Stage G spec 4.7): title block, actions and the ribbon slot. Phase 0 lifted the
 * Soundboard's `.board-heading` markup into it unchanged; WP-A owns the layout from here, and WP-B the
 * ribbon (the stub renders nothing, so the markup is exactly what it was).
 */
export function SectionHeader({
  className,
  eyebrow,
  title,
  children,
  actions,
  ribbon,
}: SectionHeaderProps) {
  return (
    <div className={className}>
      {ribbon && <HeaderRibbon {...ribbon} />}
      <div>
        {eyebrow !== undefined && <div className="eyebrow">{eyebrow}</div>}
        <h1>{title}</h1>
        {children}
      </div>
      <div className="board-actions">{actions}</div>
    </div>
  );
}
