import { LogOut, Sparkles } from 'lucide-react';
import { controller } from '../../controller';

/**
 * Stays at the top of the board for the whole example, with the way back to the start (Stage G P0.6:
 * moved out of main.tsx; WP-C owns it).
 */
export function DemoBanner({ inert }: { inert?: boolean }) {
  return (
    // Inert while the phone's editor sheet covers it, like the board: Tab must not reach a hidden button.
    <aside className="demo-banner" aria-label="Example session" inert={inert}>
      <Sparkles size={18} aria-hidden="true" />
      <span>
        <strong>Solo example.</strong> Sounds play only in this browser when you choose to play
        them. Leaving clears the example’s recordings, drafts and clips.
      </span>
      <button className="btn" onClick={() => void controller.leave()}>
        <LogOut size={15} aria-hidden="true" /> Leave example
      </button>
    </aside>
  );
}
