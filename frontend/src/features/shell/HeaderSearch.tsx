import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useBuildings, useFloors, useRooms } from '../../lib/api/buildingQueries';
import { searchIndex, type SearchResult } from './searchIndex';

const KIND_LABELS: Record<SearchResult['kind'], string> = {
  building: 'Tòa nhà',
  floor: 'Tầng',
  room: 'Phòng',
};

const KIND_ICONS: Record<SearchResult['kind'], string> = {
  building: 'apartment',
  floor: 'layers',
  room: 'meeting_room',
};

/**
 * The mockup's header search box. It reads the three flat lists the screens
 * already load rather than calling a search endpoint that does not exist, so
 * every row it offers is a building, floor or room the server really returned
 * for this account. When the server refuses those lists (no building grant)
 * the box says so instead of looking like it searched and found nothing.
 */
export function HeaderSearch() {
  const navigate = useNavigate();
  const buildings = useBuildings();
  const floors = useFloors();
  const rooms = useRooms();

  const [query, setQuery] = useState('');
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const containerRef = useRef<HTMLDivElement>(null);
  const listId = useId();

  const loading = buildings.isLoading || floors.isLoading || rooms.isLoading;
  const failed = Boolean(buildings.error || floors.error || rooms.error);

  const results = useMemo(() => {
    if (loading || failed) return [];
    return searchIndex(
      query,
      buildings.data ?? [],
      floors.data ?? [],
      rooms.data ?? [],
    );
  }, [query, loading, failed, buildings.data, floors.data, rooms.data]);

  useEffect(() => {
    setActive(0);
  }, [query]);

  // A click anywhere else closes the panel, the way a dropdown normally does.
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

  function choose(result: SearchResult) {
    setOpen(false);
    setQuery('');
    navigate(result.to);
  }

  function onKeyDown(event: React.KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'Escape') {
      setOpen(false);
      return;
    }
    if (!open || results.length === 0) return;
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      setActive((index) => (index + 1) % results.length);
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      setActive((index) => (index - 1 + results.length) % results.length);
    } else if (event.key === 'Enter') {
      const result = results[active];
      if (result) {
        event.preventDefault();
        choose(result);
      }
    }
  }

  const showPanel = open && query.trim().length > 0;

  return (
    <div ref={containerRef} className="relative hidden max-w-xl flex-1 md:block">
      <span className="material-symbols-outlined pointer-events-none absolute top-1/2 left-3.5 -translate-y-1/2 text-xl text-outline">
        search
      </span>
      <input
        type="search"
        role="combobox"
        aria-expanded={showPanel}
        aria-controls={listId}
        aria-autocomplete="list"
        aria-activedescendant={
          showPanel && results[active] ? `${listId}-${results[active].key}` : undefined
        }
        value={query}
        onChange={(event) => {
          setQuery(event.target.value);
          setOpen(true);
        }}
        onFocus={() => setOpen(true)}
        onKeyDown={onKeyDown}
        placeholder="Tìm kiếm tòa nhà, tầng, phòng…"
        aria-label="Tìm kiếm tòa nhà, tầng, phòng"
        className="h-10 w-full rounded-lg border border-outline-variant bg-surface-low pr-4 pl-11 text-sm text-on-surface placeholder:text-outline focus:border-primary focus:outline-none"
      />

      {showPanel && (
        <div
          id={listId}
          role="listbox"
          aria-label="Kết quả tìm kiếm"
          className="absolute top-full z-50 mt-2 max-h-[26rem] w-full overflow-y-auto rounded-lg border border-outline-variant bg-surface-lowest py-1 shadow-lg"
        >
          {loading && (
            <p className="px-4 py-3 text-sm text-on-surface-variant">Đang tải dữ liệu…</p>
          )}

          {!loading && failed && (
            <p className="px-4 py-3 text-sm text-on-surface-variant">
              Máy chủ chưa cho tải danh sách tòa nhà, nên chưa tìm kiếm được. Cần quyền đọc tòa nhà.
            </p>
          )}

          {!loading && !failed && results.length === 0 && (
            <p className="px-4 py-3 text-sm text-on-surface-variant">
              Không có tòa nhà, tầng hay phòng nào khớp “{query.trim()}”.
            </p>
          )}

          {results.map((result, index) => (
            <button
              key={result.key}
              id={`${listId}-${result.key}`}
              type="button"
              role="option"
              aria-selected={index === active}
              onMouseEnter={() => setActive(index)}
              onClick={() => choose(result)}
              className={
                'flex w-full items-center gap-3 px-4 py-2 text-left transition ' +
                (index === active ? 'bg-surface-low' : 'hover:bg-surface-low')
              }
            >
              <span className="material-symbols-outlined text-[20px] text-on-surface-variant">
                {KIND_ICONS[result.kind]}
              </span>
              <span className="min-w-0 flex-1">
                <span className="block truncate text-sm font-medium text-on-surface">
                  {result.title}
                </span>
                <span className="block truncate text-xs text-on-surface-variant">
                  {result.subtitle}
                </span>
              </span>
              <span className="shrink-0 text-[11px] text-on-surface-variant">
                {KIND_LABELS[result.kind]}
              </span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
