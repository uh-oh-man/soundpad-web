import { useEffect, useState } from 'react';
import { controller } from '../controller';
import { statisticsSchema, type Sound } from '../protocol';
export function Statistics({ sound, version }: { sound: Sound; version: number }) {
  const [data, setData] = useState<ReturnType<typeof statisticsSchema.parse> | null>(null),
    [error, setError] = useState('');
  useEffect(() => {
    let live = true;
    void controller
      .run('statistics', { soundId: sound.id })
      .then((raw) => {
        const result = statisticsSchema.parse(raw);
        if (live) setData(result);
      })
      .catch((e) => {
        if (live) setError(e.message);
      });
    return () => {
      live = false;
    };
  }, [sound.id, version]);
  return (
    <div className="statistics-panel">
      <h3>{sound.name}</h3>
      <div className="statistics-totals">
        <div>
          <strong>{data?.statistics.total ?? '—'}</strong>
          <span>Web plays</span>
        </div>
        <div>
          <strong>{sound.nativePlays ?? '—'}</strong>
          <span>Soundpad microphone plays</span>
        </div>
      </div>
      <p className="muted">
        Native last played: {sound.nativeLastPlayed ?? 'Unavailable'}. Web plays count accepted
        starts, including cues and regions; seeks, repeats and duplicate requests do not increment
        them.
      </p>
      {data && (
        <>
          <h3>Played by</h3>
          {Object.entries(data.statistics.participants).map(([id, p]) => (
            <div className="guest-row" key={id}>
              <span>{p.name}</span>
              <strong>{p.count}</strong>
            </div>
          ))}
          <h3>Recent activity</h3>
          {data.recent
            .slice()
            .reverse()
            .map((a) => (
              <p key={a.commandId}>
                {a.name} · {new Date(a.at).toLocaleString()}
              </p>
            ))}
          {!data.statistics.total && <p className="muted">No web plays recorded yet.</p>}
        </>
      )}
      {error && (
        <p className="warning" role="alert">
          {error}
        </p>
      )}
    </div>
  );
}
