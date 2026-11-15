import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react';
import { ChevronDown } from 'lucide-react';
import { nameProblem } from '../../organize';

export type MenuItem = { id: string; label: string; depth?: number };

/**
 * "Add selected to…" (2.44): the keyboard way to do what a drop does. A menu button: arrow keys move
 * between the targets, Enter picks, Escape closes it (and only it). The last item makes a new category
 * or folder from a typed name.
 */
export function AddToMenu({
  label,
  items,
  onPick,
  onNew,
  newLabel,
  disabled,
}: {
  label: string;
  items: MenuItem[];
  onPick: (id: string) => void;
  onNew?: (name: string) => void;
  newLabel?: string;
  disabled?: boolean;
}) {
  const [open, setOpen] = useState(false);
  const [naming, setNaming] = useState(false);
  const [name, setName] = useState('');
  const trigger = useRef<HTMLButtonElement>(null);
  const menu = useRef<HTMLDivElement>(null);
  const menuId = useId();
  const problem = naming ? nameProblem(name) : null;
  useEffect(() => {
    if (!open) return;
    menu.current?.querySelector<HTMLElement>('[role="menuitem"], input')?.focus();
    const away = (e: PointerEvent) => {
      if (!menu.current?.contains(e.target as Node) && e.target !== trigger.current) close(false);
    };
    document.addEventListener('pointerdown', away);
    return () => document.removeEventListener('pointerdown', away);
  }, [open, naming]);
  function close(focus = true) {
    setOpen(false);
    setNaming(false);
    setName('');
    if (focus) trigger.current?.focus();
  }
  function keys(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      close();
      return;
    }
    if (
      naming ||
      (e.key !== 'ArrowDown' && e.key !== 'ArrowUp' && e.key !== 'Home' && e.key !== 'End')
    )
      return;
    e.preventDefault();
    const entries = [...(menu.current?.querySelectorAll<HTMLElement>('[role="menuitem"]') ?? [])];
    const at = entries.indexOf(document.activeElement as HTMLElement);
    const next =
      e.key === 'Home'
        ? 0
        : e.key === 'End'
          ? entries.length - 1
          : (at + (e.key === 'ArrowDown' ? 1 : -1) + entries.length) % entries.length;
    entries[next]?.focus();
  }
  return (
    <div className="organize-menu">
      <button
        ref={trigger}
        type="button"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? menuId : undefined}
        disabled={disabled}
        onClick={() => (open ? close() : setOpen(true))}
      >
        {label} <ChevronDown size={14} aria-hidden="true" />
      </button>
      {open && (
        <div
          id={menuId}
          ref={menu}
          role="menu"
          aria-label={label}
          className="organize-menu-list"
          onKeyDown={keys}
        >
          {items.map((item) => (
            <button
              key={item.id}
              type="button"
              role="menuitem"
              tabIndex={-1}
              style={item.depth ? { paddingInlineStart: `${12 + item.depth * 14}px` } : undefined}
              onClick={() => {
                onPick(item.id);
                close();
              }}
            >
              {item.label}
            </button>
          ))}
          {onNew &&
            (naming ? (
              <form
                className="organize-menu-new"
                onSubmit={(e) => {
                  e.preventDefault();
                  if (problem) return;
                  onNew(name);
                  close();
                }}
              >
                <input
                  aria-label={newLabel ?? 'New name'}
                  value={name}
                  maxLength={80}
                  onChange={(e) => setName(e.target.value)}
                  aria-invalid={!!problem && name.length > 0}
                />
                <button type="submit" aria-disabled={!!problem || undefined}>
                  Add
                </button>
                {name.length > 0 && problem && <small role="status">{problem}</small>}
              </form>
            ) : (
              <button type="button" role="menuitem" tabIndex={-1} onClick={() => setNaming(true)}>
                {newLabel ?? 'New…'}
              </button>
            ))}
        </div>
      )}
    </div>
  );
}
