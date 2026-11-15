import { useEffect, useState } from 'react';

export interface GalleryPhoto {
  id: string;
  /** A resolved src: a data URL for uploaded bytes, or a placeholder. */
  src: string;
  alt: string;
}

interface PhotoGalleryProps {
  photos: GalleryPhoto[];
  /** Shown inside the frame when there are no photos. */
  emptyLabel?: string;
  onAdd?: () => void;
  onDelete?: (photoId: string) => void;
  /** Extra label under the delete button, e.g. "Đang xoá…". */
  deletingId?: string | null;
  loading?: boolean;
}

const MAX_THUMBS = 4;

/**
 * One large frame plus a thumbnail strip. The strip only ever renders the
 * first few photos; the "+N" tile opens nothing yet because the detail screen
 * has no lightbox, so it states the remaining count instead of pretending to
 * be a button.
 */
export function PhotoGallery({
  photos,
  emptyLabel = 'Chưa có ảnh',
  onAdd,
  onDelete,
  deletingId = null,
  loading = false,
}: PhotoGalleryProps) {
  const [activeIndex, setActiveIndex] = useState(0);

  // A delete can leave the selection past the end of the list.
  useEffect(() => {
    if (activeIndex > photos.length - 1) {
      setActiveIndex(Math.max(photos.length - 1, 0));
    }
  }, [photos.length, activeIndex]);

  const active = photos[activeIndex] ?? null;
  const visible = photos.slice(0, MAX_THUMBS);
  const hidden = photos.length - visible.length;

  return (
    <div className="flex flex-col gap-3">
      <div className="relative aspect-[4/3] w-full overflow-hidden rounded-[var(--radius-panel)] border border-outline-variant bg-surface-low">
        {loading ? (
          <div className="flex h-full items-center justify-center text-sm text-on-surface-variant">
            Đang tải ảnh…
          </div>
        ) : active ? (
          <img src={active.src} alt={active.alt} className="h-full w-full object-cover" />
        ) : (
          <div className="flex h-full flex-col items-center justify-center gap-2 text-on-surface-variant">
            <span className="material-symbols-outlined text-3xl" aria-hidden>
              image
            </span>
            <span className="text-sm">{emptyLabel}</span>
            {onAdd && (
              <button
                type="button"
                onClick={onAdd}
                className="mt-1 rounded-[0.5rem] border border-outline-variant px-3 py-1.5 text-xs font-semibold text-on-surface hover:bg-surface-container focus:outline-none focus-visible:ring-3 focus-visible:ring-primary/25"
              >
                Tải ảnh lên
              </button>
            )}
          </div>
        )}

        {active && onDelete && (
          <button
            type="button"
            onClick={() => onDelete(active.id)}
            disabled={deletingId === active.id}
            className="absolute top-2 right-2 inline-flex h-8 w-8 items-center justify-center rounded-full bg-black/55 text-white transition hover:bg-black/70 focus:outline-none focus-visible:ring-3 focus-visible:ring-white/60 disabled:cursor-not-allowed disabled:opacity-60"
            aria-label={`Xoá ${active.alt}`}
            title="Xoá ảnh"
          >
            <span className="material-symbols-outlined text-[18px]" aria-hidden>
              {deletingId === active.id ? 'hourglass' : 'delete'}
            </span>
          </button>
        )}
      </div>

      {(photos.length > 0 || onAdd) && (
        <div className="flex flex-wrap items-center gap-2">
          {visible.map((photo, index) => (
            <button
              key={photo.id}
              type="button"
              onClick={() => setActiveIndex(index)}
              aria-label={`Xem ${photo.alt}`}
              aria-current={index === activeIndex ? 'true' : undefined}
              className={
                'h-14 w-16 overflow-hidden rounded-[0.5rem] border-2 transition focus:outline-none focus-visible:ring-3 focus-visible:ring-primary/25 ' +
                (index === activeIndex
                  ? 'border-primary'
                  : 'border-transparent hover:border-outline')
              }
            >
              <img src={photo.src} alt="" className="h-full w-full object-cover" />
            </button>
          ))}

          {hidden > 0 && (
            <span
              className="inline-flex h-14 w-16 items-center justify-center rounded-[0.5rem] border border-outline-variant bg-surface-container text-xs font-semibold text-on-surface-variant tnum"
              title={`Còn ${hidden} ảnh nữa`}
            >
              +{hidden}
            </span>
          )}

          {onAdd && photos.length > 0 && (
            <button
              type="button"
              onClick={onAdd}
              aria-label="Thêm ảnh"
              className="inline-flex h-14 w-16 items-center justify-center rounded-[0.5rem] border border-dashed border-outline text-outline transition hover:border-primary hover:text-primary focus:outline-none focus-visible:ring-3 focus-visible:ring-primary/25"
            >
              <span className="material-symbols-outlined text-[20px]" aria-hidden>
                add_photo_alternate
              </span>
            </button>
          )}
        </div>
      )}
    </div>
  );
}
