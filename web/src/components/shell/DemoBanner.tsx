import { LogOut, Sparkles } from 'lucide-react';
import { controller } from '../../controller';

/**
 * Stays at the top of the board for the whole example, with the way back to the start (Stage G P0.6:
 * moved out of main.tsx unchanged; WP-C owns it).
 */
export function DemoBanner() {
  return (
    <aside className="demo-banner" aria-label="Example session">
      <Sparkles size={18} aria-hidden="true" />
      <span>
        <strong>Example session: nothing here reaches Soundpad.</strong> Sounds play in this
        browser, the guests are simulated, and leaving clears the example’s recordings, drafts and
        clips. Favourites and view settings stay on this browser.
      </span>
      <button className="btn" onClick={() => void controller.leave()}>
        <LogOut size={15} aria-hidden="true" /> Leave example
      </button>
    </aside>
  );
}
