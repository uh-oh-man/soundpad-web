import { useEffect, useState } from 'react';
import { onPulse } from './pulse';

/**
 * Faint sound-wave rings behind the start page's soundboard (homepage). They sit still; each pad press
 * sends one wave out through them (home.css animates it only when motion is welcome). Decorative.
 */
export function HomeRings() {
  const [wave, setWave] = useState(0);
  useEffect(() => onPulse(() => setWave((n) => n + 1)), []);
  return (
    <span className="home-rings" aria-hidden="true">
      <i />
      <i />
      <i />
      {wave > 0 && <b key={wave} className="home-rings-wave" />}
    </span>
  );
}
