import { useRef } from 'react';
import type { ReactNode } from 'react';

export interface TabItem {
  id: string;
  label: string;
  /** Optional count rendered next to the label, e.g. "Thiết bị 4". */
  badge?: ReactNode;
  disabled?: boolean;
}

interface TabsProps {
  items: TabItem[];
  activeId: string;
  onChange: (id: string) => void;
  /** Rendered right of the tab strip, e.g. an "Thêm thiết bị" button. */
  action?: ReactNode;
  label: string;
}

/**
 * WAI-ARIA tabs: arrow keys move focus, Home/End jump to the ends. The panel
 * itself is rendered by the caller so each tab can decide whether it needs a
 * query or a gate notice.
 */
export function Tabs({ items, activeId, onChange, action, label }: TabsProps) {
  const listRef = useRef<HTMLDivElement>(null);

  function focusTab(index: number) {
    const nodes = listRef.current?.querySelectorAll<HTMLButtonElement>('[role="tab"]');
    nodes?.[index]?.focus();
  }

  function handleKeyDown(event: React.KeyboardEvent<HTMLButtonElement>, index: number) {
    const enabled = items.map((item, i) => (item.disabled ? -1 : i)).filter((i) => i >= 0);
    if (enabled.length === 0) {
      return;
    }
    const position = enabled.indexOf(index);
    let next: number | undefined;

    if (event.key === 'ArrowRight') {
      next = enabled[(position + 1) % enabled.length];
    } else if (event.key === 'ArrowLeft') {
      next = enabled[(position - 1 + enabled.length) % enabled.length];
    } else if (event.key === 'Home') {
      next = enabled[0];
    } else if (event.key === 'End') {
      next = enabled[enabled.length - 1];
    }

    if (next === undefined) {
      return;
    }
    const target = items[next];
    if (!target) {
      return;
    }
    event.preventDefault();
    focusTab(next);
    onChange(target.id);
  }

  return (
    <div className="flex flex-wrap items-end justify-between gap-3 border-b border-outline-variant">
      <div ref={listRef} role="tablist" aria-label={label} className="flex flex-wrap gap-1">
        {items.map((item, index) => {
          const selected = item.id === activeId;
          return (
            <button
              key={item.id}
              type="button"
              role="tab"
              id={`tab-${item.id}`}
              aria-selected={selected}
              aria-controls={`tabpanel-${item.id}`}
              tabIndex={selected ? 0 : -1}
              disabled={item.disabled}
              onClick={() => onChange(item.id)}
              onKeyDown={(event) => handleKeyDown(event, index)}
              className={
                '-mb-px inline-flex items-center gap-2 border-b-2 px-3 py-2.5 text-sm font-semibold transition ' +
                'focus:outline-none focus-visible:ring-3 focus-visible:ring-primary/25 disabled:cursor-not-allowed disabled:opacity-50 ' +
                (selected
                  ? 'border-primary text-primary'
                  : 'border-transparent text-on-surface-variant hover:text-on-surface')
              }
            >
              {item.label}
              {item.badge !== undefined && item.badge !== null && (
                <span
                  className={
                    'rounded-full px-2 py-0.5 text-[11px] font-semibold tnum ' +
                    (selected
                      ? 'bg-primary/10 text-primary'
                      : 'bg-surface-container text-on-surface-variant')
                  }
                >
                  {item.badge}
                </span>
              )}
            </button>
          );
        })}
      </div>
      {action}
    </div>
  );
}

interface TabPanelProps {
  id: string;
  activeId: string;
  children: ReactNode;
}

export function TabPanel({ id, activeId, children }: TabPanelProps) {
  if (id !== activeId) {
    return null;
  }
  return (
    <div role="tabpanel" id={`tabpanel-${id}`} aria-labelledby={`tab-${id}`} className="pt-4">
      {children}
    </div>
  );
}
