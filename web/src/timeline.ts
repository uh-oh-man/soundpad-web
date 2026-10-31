import { useCallback, useEffect, useRef, useState, type HTMLAttributes } from 'react';
export const MAX_ZOOM = 128;
export type Viewport = { zoom: number; offset: number };
export function boundedView(total: number, zoom: number, offset: number): Viewport {
  const z = Math.max(1, Math.min(MAX_ZOOM, Number.isFinite(zoom) ? zoom : 1));
  return {
    zoom: z,
    offset: Math.max(
      0,
      Math.min(Math.max(0, total - total / z), Number.isFinite(offset) ? offset : 0),
    ),
  };
}
export function zoomAt(total: number, view: Viewport, zoom: number, fraction: number) {
  const ratio = Math.max(0, Math.min(1, fraction));
  const anchor = view.offset + (total / view.zoom) * ratio;
  const next = boundedView(total, zoom, 0);
  return boundedView(total, next.zoom, anchor - (total / next.zoom) * ratio);
}
type Gesture = {
  kind: 'seek' | 'pan' | 'pinch' | 'range';
  startX: number;
  startAt: number;
  view: Viewport;
  distance: number;
  anchor: number;
  moved: boolean;
};
export function useTimeline({
  identity,
  duration,
  initialZoom = 1,
  disabled,
  commit,
  range,
  persistZoom,
}: {
  identity: string;
  duration: number;
  initialZoom?: number;
  disabled: boolean;
  commit: (at: number) => void;
  range?: (start: number, end: number) => void;
  persistZoom?: (zoom: number) => void;
}) {
  // A callback ref: the surface can mount after this hook first runs (the waveform appears once its data
  // arrives), and the wheel listener below must follow whichever element is actually there.
  const surfaceNode = useRef<HTMLDivElement | null>(null);
  const [node, setNode] = useState<HTMLDivElement | null>(null);
  const surface = useCallback((element: HTMLDivElement | null) => {
    surfaceNode.current = element;
    setNode(element);
  }, []);
  const [view, setView] = useState<Viewport>(() => boundedView(duration, initialZoom, 0));
  const [selected, setSelected] = useState(0),
    [preview, setPreview] = useState<number | null>(null);
  const points = useRef(new Map<number, { x: number; y: number }>()),
    gesture = useRef<Gesture | null>(null);
  const options = useRef({ commit, range, persistZoom });
  useEffect(() => {
    options.current = { commit, range, persistZoom };
  }, [commit, range, persistZoom]);
  useEffect(() => {
    setView(boundedView(duration, initialZoom, 0));
    setPreview(null);
    setSelected(0);
    points.current.clear();
    gesture.current = null;
    // A changed sound aborts the old gesture; live duration/zoom persistence does not reset a scrub.
  }, [identity]);
  useEffect(() => {
    const timer = setTimeout(() => options.current.persistZoom?.(view.zoom), 200);
    return () => clearTimeout(timer);
  }, [view.zoom, identity]);
  const effective = boundedView(duration, view.zoom, view.offset),
    span = duration / effective.zoom;
  function fraction(x: number) {
    const rect = surfaceNode.current?.getBoundingClientRect();
    return rect?.width ? Math.max(0, Math.min(1, (x - rect.left) / rect.width)) : 0;
  }
  function timeAt(x: number) {
    return Math.round(
      Math.max(0, Math.min(Math.max(0, duration - 1), effective.offset + fraction(x) * span)),
    );
  }
  function fit() {
    setView({ zoom: 1, offset: 0 });
    setPreview(null);
  }
  useEffect(() => {
    if (!node || !duration) return;
    const wheel = (event: WheelEvent) => {
      event.preventDefault();
      const rect = node.getBoundingClientRect(),
        scale = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? rect.width : 1;
      if (event.shiftKey || Math.abs(event.deltaX) > Math.abs(event.deltaY)) {
        const delta = (event.shiftKey ? event.deltaY || event.deltaX : event.deltaX) * scale;
        setView((previous) =>
          boundedView(
            duration,
            previous.zoom,
            previous.offset + ((delta / Math.max(1, rect.width)) * duration) / previous.zoom,
          ),
        );
      } else {
        const ratio = (event.clientX - rect.left) / Math.max(1, rect.width),
          factor = Math.exp(Math.max(-0.7, Math.min(0.7, -event.deltaY * scale * 0.003)));
        setView((previous) => zoomAt(duration, previous, previous.zoom * factor, ratio));
      }
    };
    node.addEventListener('wheel', wheel, { passive: false });
    return () => node.removeEventListener('wheel', wheel);
  }, [node, duration, identity]);
  const interactions: HTMLAttributes<HTMLDivElement> = {
    onPointerDown(event) {
      if (event.button !== 0 && event.button !== 1) return;
      event.preventDefault();
      event.currentTarget.focus({ preventScroll: true });
      event.currentTarget.setPointerCapture(event.pointerId);
      points.current.set(event.pointerId, { x: event.clientX, y: event.clientY });
      if (points.current.size === 2) {
        const [a, b] = [...points.current.values()],
          center = (a.x + b.x) / 2;
        gesture.current = {
          kind: 'pinch',
          startX: center,
          startAt: 0,
          view: effective,
          distance: Math.max(1, Math.hypot(a.x - b.x, a.y - b.y)),
          anchor: effective.offset + fraction(center) * span,
          moved: true,
        };
        setPreview(null);
        return;
      }
      if (points.current.size > 2) return;
      const navigating = event.shiftKey || event.button === 1;
      gesture.current = {
        kind: navigating || disabled ? 'pan' : options.current.range ? 'range' : 'seek',
        startX: event.clientX,
        startAt: timeAt(event.clientX),
        view: effective,
        distance: 0,
        anchor: 0,
        moved: false,
      };
      if (!navigating && !disabled) setPreview(timeAt(event.clientX));
    },
    onPointerMove(event) {
      if (!points.current.has(event.pointerId)) return;
      points.current.set(event.pointerId, { x: event.clientX, y: event.clientY });
      const current = gesture.current;
      if (!current) return;
      const width = Math.max(1, surfaceNode.current!.getBoundingClientRect().width);
      if (current.kind === 'pinch' && points.current.size >= 2) {
        const [a, b] = [...points.current.values()],
          center = (a.x + b.x) / 2;
        const zoom = boundedView(
          duration,
          (current.view.zoom * Math.hypot(a.x - b.x, a.y - b.y)) / current.distance,
          0,
        ).zoom;
        setView(boundedView(duration, zoom, current.anchor - (fraction(center) * duration) / zoom));
      } else if (current.kind === 'pan' || current.kind === 'pinch') {
        setView(
          boundedView(
            duration,
            current.view.zoom,
            current.view.offset -
              (((event.clientX - current.startX) / width) * duration) / current.view.zoom,
          ),
        );
      } else {
        current.moved ||= Math.abs(event.clientX - current.startX) > 3;
        setPreview(timeAt(event.clientX));
      }
    },
    onPointerUp(event) {
      const current = gesture.current;
      if (!points.current.has(event.pointerId)) return;
      points.current.delete(event.pointerId);
      if (event.currentTarget.hasPointerCapture(event.pointerId))
        event.currentTarget.releasePointerCapture(event.pointerId);
      if (points.current.size) {
        if (current) {
          const remaining = [...points.current.values()][0];
          current.kind = 'pan';
          current.startX = remaining.x;
          current.view = effective;
        }
        return;
      }
      gesture.current = null;
      setPreview(null);
      if (!current || disabled || current.kind === 'pan' || current.kind === 'pinch') return;
      const at = timeAt(event.clientX);
      setSelected(at);
      if (current.kind === 'range')
        options.current.range?.(Math.min(at, current.startAt), Math.max(at, current.startAt));
      else options.current.commit(at);
    },
    onPointerCancel() {
      points.current.clear();
      gesture.current = null;
      setPreview(null);
    },
    onKeyDown(event) {
      if (event.defaultPrevented) return;
      if (event.key === '0' || event.key === 'Escape') {
        event.preventDefault();
        fit();
        return;
      }
      if (event.key === '+' || event.key === '=' || event.key === '-') {
        event.preventDefault();
        setView((previous) =>
          zoomAt(duration, previous, previous.zoom * (event.key === '-' ? 1 / 1.5 : 1.5), 0.5),
        );
        return;
      }
      if (event.altKey && ['ArrowLeft', 'ArrowRight'].includes(event.key)) {
        event.preventDefault();
        setView((previous) =>
          boundedView(
            duration,
            previous.zoom,
            previous.offset + (event.key === 'ArrowRight' ? 1 : -1) * span * 0.1,
          ),
        );
        return;
      }
      if (disabled) return;
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        options.current.commit(selected);
        return;
      }
      const delta = event.shiftKey ? 1000 : 10;
      const at =
        event.key === 'Home'
          ? 0
          : event.key === 'End'
            ? duration - 1
            : event.key === 'ArrowRight'
              ? selected + delta
              : event.key === 'ArrowLeft'
                ? selected - delta
                : null;
      if (at !== null) {
        event.preventDefault();
        const value = Math.round(Math.max(0, Math.min(duration - 1, at)));
        setSelected(value);
        if (value < effective.offset || value > effective.offset + span)
          setView(boundedView(duration, effective.zoom, value - span / 2));
      }
    },
  };
  return {
    surface,
    interactions,
    zoom: effective.zoom,
    offset: effective.offset,
    span,
    selected,
    setSelected,
    preview,
    setPreview,
    timeAt,
    fit,
  };
}
