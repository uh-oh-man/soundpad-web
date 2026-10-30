import { useEffect, useMemo, useRef, useState } from 'react';
import { Music2 } from 'lucide-react';
import { useWaveformData } from '../waveform-cache';
import { wavePath } from './Waveform';
export function WaveformPreview({ hostId, id }: { hostId: string; id: string }) {
  const root = useRef<HTMLSpanElement>(null);
  const [visible, setVisible] = useState(false);
  const { data } = useWaveformData(hostId, id, visible);
  useEffect(() => {
    if (!root.current || visible) return;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((entry) => entry.isIntersecting)) {
          setVisible(true);
          observer.disconnect();
        }
      },
      { rootMargin: '180px' },
    );
    observer.observe(root.current);
    return () => observer.disconnect();
  }, [visible]);
  const path = useMemo(() => (data ? wavePath(data, 220) : ''), [data]);
  return (
    <span className="mini-waveform" ref={root} aria-hidden="true">
      {path ? (
        <svg viewBox="0 0 220 64" preserveAspectRatio="none">
          <path d={path} />
        </svg>
      ) : (
        <Music2 size={22} />
      )}
    </span>
  );
}
