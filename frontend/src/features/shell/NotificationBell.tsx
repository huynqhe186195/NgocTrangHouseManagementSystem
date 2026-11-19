import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { useBuildings, useFloors, useRooms } from '../../lib/api/buildingQueries';
import { deriveNotifications, NOTIFICATION_COUNT_HINT } from './notifications';

const TONE_CLASSES: Record<string, string> = {
  error: 'bg-error/10 text-error',
  warning: 'bg-amber-100 text-amber-700',
  neutral: 'bg-surface-container text-on-surface-variant',
};

/**
 * The bell lists what actually needs attention in the data this account can
 * read: rooms held for maintenance or already reserved, and floors under
 * maintenance. It shows a badge only when such a row exists — never a fixed
 * number, which §3.3 forbids — and says what the number counts.
 */
export function NotificationBell() {
  const buildings = useBuildings();
  const floors = useFloors();
  const rooms = useRooms();

  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);

  const loading = buildings.isLoading || floors.isLoading || rooms.isLoading;
  const failed = Boolean(buildings.error || floors.error || rooms.error);

  // A header bell must not crash the shell when one list is unavailable, so
  // an unreachable list contributes no rows rather than an error screen.
  const rows = failed || loading
    ? []
    : deriveNotifications(buildings.data ?? [], floors.data ?? [], rooms.data ?? []);

  useEffect(() => {
    if (!open) return;
    function onPointerDown(event: MouseEvent) {
      if (!containerRef.current?.contains(event.target as Node)) {
        setOpen(false);
      }
    }
    document.addEventListener('mousedown', onPointerDown);
    return () => document.removeEventListener('mousedown', onPointerDown);
  }, [open]);

  const label = rows.length > 0 ? `Thông báo (${rows.length})` : 'Thông báo';

  return (
    <div ref={containerRef} className="relative">
      <button
        type="button"
        aria-label={label}
        aria-expanded={open}
        aria-haspopup="dialog"
        onClick={() => setOpen((value) => !value)}
        className="relative rounded-lg p-2 text-on-surface-variant transition hover:bg-surface-low"
      >
        <span className="material-symbols-outlined text-xl">notifications</span>
        {rows.length > 0 && (
          <span
            aria-hidden
            className="absolute top-1 right-1 flex h-4 min-w-4 items-center justify-center rounded-full bg-error px-1 text-[10px] font-bold text-white"
          >
            {rows.length}
          </span>
        )}
      </button>

      {open && (
        <div
          role="dialog"
          aria-label="Thông báo"
          className="fixed inset-x-4 top-[62px] z-50 rounded-lg border border-outline-variant bg-surface-lowest shadow-lg md:absolute md:inset-x-auto md:top-full md:right-0 md:mt-2 md:w-[20rem]"
        >
          <div className="flex flex-col gap-0.5 border-b border-outline-variant px-4 py-3">
            <p className="text-sm font-semibold text-on-surface">Cần chú ý</p>
            <p className="text-[11px] leading-snug text-on-surface-variant">
              {NOTIFICATION_COUNT_HINT}
            </p>
          </div>

          {loading && (
            <p className="px-4 py-4 text-sm text-on-surface-variant">Đang tải dữ liệu…</p>
          )}

          {!loading && failed && (
            <p className="px-4 py-4 text-sm text-on-surface-variant">
              Chưa tải được danh sách tòa nhà, tầng, phòng nên chưa có gì để nhắc.
            </p>
          )}

          {!loading && !failed && rows.length === 0 && (
            <p className="px-4 py-4 text-sm text-on-surface-variant">
              Không có phòng bảo trì, phòng đã đặt hay tầng bảo trì nào.
            </p>
          )}

          {rows.length > 0 && (
            <ul className="max-h-[22rem] overflow-y-auto py-1">
              {rows.map((row) => (
                <li key={row.key}>
                  <Link
                    to={row.to}
                    onClick={() => setOpen(false)}
                    className="flex items-start gap-3 px-4 py-2 transition hover:bg-surface-low"
                  >
                    <span
                      className={
                        'material-symbols-outlined mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-full text-[18px] ' +
                        (TONE_CLASSES[row.tone] ?? TONE_CLASSES.neutral)
                      }
                    >
                      {row.icon}
                    </span>
                    <span className="min-w-0">
                      <span className="block text-sm text-on-surface">{row.title}</span>
                      {row.detail && (
                        <span className="block truncate text-xs text-on-surface-variant">
                          {row.detail}
                        </span>
                      )}
                    </span>
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </div>
  );
}
