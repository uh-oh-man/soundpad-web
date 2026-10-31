import { useEffect, useRef, useState } from 'react';
import { ImagePlus, RotateCcw, Trash2, Save } from 'lucide-react';
import { controller } from '../controller';
import type { Sound } from '../protocol';
export function cropGeometry(width: number, height: number, zoom: number, x: number, y: number) {
  const scale = Math.max(256 / width, 256 / height) * zoom;
  return {
    width: width * scale,
    height: height * scale,
    x: (256 - width * scale) / 2 + x,
    y: (256 - height * scale) / 2 + y,
  };
}
export function IconEditor({ sound, close }: { sound: Sound; close: () => void }) {
  const [bitmap, setBitmap] = useState<ImageBitmap | null>(null),
    [zoom, setZoom] = useState(1),
    [offset, setOffset] = useState({ x: 0, y: 0 }),
    [error, setError] = useState(''),
    [busy, setBusy] = useState(false);
  const canvas = useRef<HTMLCanvasElement>(null),
    drag = useRef<{ x: number; y: number; offset: { x: number; y: number } } | null>(null),
    pointers = useRef(new Map<number, { x: number; y: number }>()),
    pinch = useRef<{ distance: number; zoom: number } | null>(null);
  useEffect(() => () => bitmap?.close(), [bitmap]);
  useEffect(() => {
    if (!bitmap) return;
    const g = cropGeometry(bitmap.width, bitmap.height, zoom, 0, 0);
    setOffset((p) => ({
      x: Math.max(-(g.width - 256) / 2, Math.min((g.width - 256) / 2, p.x)),
      y: Math.max(-(g.height - 256) / 2, Math.min((g.height - 256) / 2, p.y)),
    }));
  }, [bitmap, zoom]);
  useEffect(() => {
    const c = canvas.current;
    if (!c) return;
    const ctx = c.getContext('2d')!;
    ctx.clearRect(0, 0, 256, 256);
    if (bitmap) {
      const g = cropGeometry(bitmap.width, bitmap.height, zoom, offset.x, offset.y);
      ctx.drawImage(bitmap, g.x, g.y, g.width, g.height);
    }
  }, [bitmap, zoom, offset]);
  async function load(file: File) {
    setError('');
    try {
      if (!['image/png', 'image/jpeg'].includes(file.type) || file.size > 12 * 1024 * 1024)
        throw Error('Choose a PNG or JPEG up to 12 MiB.');
      const image = await createImageBitmap(file);
      if (image.width > 12000 || image.height > 12000 || image.width * image.height > 40000000) {
        image.close();
        throw Error('The image exceeds 40 megapixels or 12,000 pixels per side.');
      }
      setBitmap(image);
      setZoom(1);
      setOffset({ x: 0, y: 0 });
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Image could not be decoded.');
    }
  }
  function move(x: number, y: number) {
    if (!bitmap) return;
    const g = cropGeometry(bitmap.width, bitmap.height, zoom, 0, 0);
    setOffset({
      x: Math.max(-(g.width - 256) / 2, Math.min((g.width - 256) / 2, x)),
      y: Math.max(-(g.height - 256) / 2, Math.min((g.height - 256) / 2, y)),
    });
  }
  async function save(remove = false) {
    if (busy) return; // buttons stay focusable while saving (aria-disabled), so ignore repeats
    setBusy(true);
    setError('');
    try {
      let image = '';
      if (!remove) {
        const result = document.createElement('canvas');
        result.width = result.height = 96;
        result.getContext('2d')!.drawImage(canvas.current!, 0, 0, 96, 96);
        image = result.toDataURL('image/png');
      }
      await controller.run('icon', { soundId: sound.id, image });
      close();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'The companion rejected the icon.');
    } finally {
      setBusy(false);
    }
  }
  return (
    <div
      className="icon-editor"
      onDragOver={(e) => e.preventDefault()}
      onDrop={(e) => {
        e.preventDefault();
        const file = e.dataTransfer.files[0];
        if (file) void load(file);
      }}
    >
      <p className="muted">{sound.name}</p>
      <label className="upload primary">
        <ImagePlus size={17} /> Choose PNG or JPEG
        <input
          type="file"
          aria-label="Choose sound icon"
          accept="image/png,image/jpeg"
          onChange={(e) => {
            const file = e.target.files?.[0];
            if (file) void load(file);
          }}
        />
      </label>
      {bitmap ? (
        <>
          <canvas
            ref={canvas}
            className="crop-canvas"
            width={256}
            height={256}
            aria-label="Square icon crop; drag to reposition"
            role="img"
            onWheel={(e) => {
              setZoom((z) => Math.max(1, Math.min(6, z - e.deltaY / 600)));
            }}
            onPointerDown={(e) => {
              e.currentTarget.setPointerCapture(e.pointerId);
              pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });
              drag.current = { x: e.clientX, y: e.clientY, offset };
              if (pointers.current.size === 2) {
                const [a, b] = [...pointers.current.values()];
                pinch.current = { distance: Math.hypot(a.x - b.x, a.y - b.y), zoom };
              }
            }}
            onPointerMove={(e) => {
              if (!pointers.current.has(e.pointerId)) return;
              pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });
              if (pointers.current.size === 2 && pinch.current) {
                const [a, b] = [...pointers.current.values()];
                setZoom(
                  Math.max(
                    1,
                    Math.min(
                      6,
                      (pinch.current.zoom * Math.hypot(a.x - b.x, a.y - b.y)) /
                        Math.max(1, pinch.current.distance),
                    ),
                  ),
                );
              } else if (drag.current)
                move(
                  drag.current.offset.x +
                    ((e.clientX - drag.current.x) * 256) / e.currentTarget.clientWidth,
                  drag.current.offset.y +
                    ((e.clientY - drag.current.y) * 256) / e.currentTarget.clientHeight,
                );
            }}
            onPointerUp={(e) => {
              pointers.current.delete(e.pointerId);
              drag.current = null;
              pinch.current = null;
              e.currentTarget.releasePointerCapture(e.pointerId);
            }}
            onPointerCancel={() => {
              pointers.current.clear();
              drag.current = null;
              pinch.current = null;
            }}
          />
          <label>
            Zoom
            <input
              type="range"
              aria-label="Crop zoom"
              min={1}
              max={6}
              step="0.01"
              value={zoom}
              onChange={(e) => {
                setZoom(+e.target.value);
                setOffset({ x: 0, y: 0 });
              }}
            />
          </label>
          <div className="crop-position">
            <label>
              Horizontal
              <input
                type="range"
                aria-label="Crop horizontal position"
                min={-300}
                max={300}
                value={offset.x}
                onChange={(e) => move(+e.target.value, offset.y)}
              />
            </label>
            <label>
              Vertical
              <input
                type="range"
                aria-label="Crop vertical position"
                min={-300}
                max={300}
                value={offset.y}
                onChange={(e) => move(offset.x, +e.target.value)}
              />
            </label>
          </div>
          <button
            onClick={() => {
              setZoom(1);
              setOffset({ x: 0, y: 0 });
            }}
          >
            <RotateCcw size={15} /> Reset crop
          </button>
        </>
      ) : (
        <div className="icon-preview">
          {sound.icon ? <img src={sound.icon} alt="Current sound icon" /> : <ImagePlus size={36} />}
        </div>
      )}
      <p className="muted">
        Drop a photo, drag to reposition, or use the keyboard-accessible sliders. Saved as a
        96-pixel PNG on the host.
      </p>
      {error && (
        <p role="alert" className="warning">
          {error}
        </p>
      )}
      <div className="editor-actions">
        <button
          disabled={!sound.icon}
          aria-disabled={busy || undefined}
          onClick={() => void save(true)}
        >
          <Trash2 size={15} /> Remove custom icon
        </button>
        <button onClick={() => !busy && close()} aria-disabled={busy || undefined}>
          Cancel
        </button>
        <button
          className="primary"
          disabled={!bitmap}
          aria-disabled={busy || undefined}
          onClick={() => void save()}
        >
          <Save size={15} />
          {busy ? 'Saving…' : 'Save icon'}
        </button>
      </div>
    </div>
  );
}
