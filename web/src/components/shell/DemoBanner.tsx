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
        <strong>Example session: nothing here reaches Soundpad.</strong> Sounds play in this browser
        and the guests are simulated. Leaving clears the example’s audio, copies and Mimic clips;
        your favourites and view settings stay.
      </span>
      <button onClick={() => void controller.leave()}>
        <LogOut size={15} aria-hidden="true" /> Leave example
      </button>
    </aside>
  );
}
