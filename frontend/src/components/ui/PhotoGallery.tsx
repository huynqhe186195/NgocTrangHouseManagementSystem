import { useCallback, useEffect, useRef, useState } from 'react';

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

/**
 * One large frame, arrows, a photo counter and a thumbnail strip. The frame
 * opens a full-screen view on click — the same images, larger — so the gallery
 * never implies a photo it cannot show.
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
  const [lightboxOpen, setLightboxOpen] = useState(false);

  // A delete can leave the selection past the end of the list.
  useEffect(() => {
    if (activeIndex > photos.length - 1) {
      setActiveIndex(Math.max(photos.length - 1, 0));
    }
  }, [photos.length, activeIndex]);

  // Closing the lightbox when the last photo is deleted avoids a blank overlay.
  useEffect(() => {
    if (photos.length === 0) {
      setLightboxOpen(false);
    }
  }, [photos.length]);

  const go = useCallback(
    (delta: number) => {
      setActiveIndex((index) => {
        if (photos.length === 0) {
          return 0;
        }
        return (index + delta + photos.length) % photos.length;
      });
    },
    [photos.length],
  );

  useGalleryKeyboard(lightboxOpen, photos.length, go, () => setLightboxOpen(false));

  const active = photos[activeIndex] ?? null;
  const multiple = photos.length > 1;

  return (
    <div className="flex flex-col gap-3">
      <div className="relative aspect-[4/3] w-full overflow-hidden rounded-[var(--radius-panel)] border border-outline-variant bg-surface-low">
        {loading ? (
          <div className="flex h-full items-center justify-center text-sm text-on-surface-variant">
            Đang tải ảnh…
          </div>
        ) : active ? (
          <button
            type="button"
            onClick={() => setLightboxOpen(true)}
            aria-label={`Mở ảnh lớn: ${active.alt}`}
            className="group block h-full w-full cursor-zoom-in focus:outline-none focus-visible:ring-3 focus-visible:ring-inset focus-visible:ring-primary/40"
          >
            <img src={active.src} alt={active.alt} className="h-full w-full object-cover" />
            <span className="pointer-events-none absolute right-2 bottom-2 rounded-full bg-black/55 px-2 py-0.5 text-[11px] font-semibold text-white opacity-0 transition group-hover:opacity-100">
              Xem ảnh lớn
            </span>
          </button>
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

        {multiple && active && (
          <>
            <GalleryArrow direction="prev" onClick={() => go(-1)} />
            <GalleryArrow direction="next" onClick={() => go(1)} />
          </>
        )}

        {active && (
          <span className="absolute bottom-2 left-2 rounded-full bg-black/55 px-2 py-0.5 text-[11px] font-semibold text-white tnum">
            {activeIndex + 1} / {photos.length}
          </span>
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
          {photos.map((photo, index) => (
            <button
              key={photo.id}
              type="button"
              onClick={() => setActiveIndex(index)}
              aria-label={`Xem ${photo.alt}`}
              aria-current={index === activeIndex ? 'true' : undefined}
              className={
                'h-14 w-16 shrink-0 overflow-hidden rounded-[0.5rem] border-2 transition focus:outline-none focus-visible:ring-3 focus-visible:ring-primary/25 ' +
                (index === activeIndex
                  ? 'border-primary'
                  : 'border-transparent hover:border-outline')
              }
            >
              <img src={photo.src} alt="" className="h-full w-full object-cover" />
            </button>
          ))}

          {onAdd && photos.length > 0 && (
            <button
              type="button"
              onClick={onAdd}
              aria-label="Thêm ảnh"
              className="inline-flex h-14 w-16 shrink-0 items-center justify-center rounded-[0.5rem] border border-dashed border-outline text-outline transition hover:border-primary hover:text-primary focus:outline-none focus-visible:ring-3 focus-visible:ring-primary/25"
            >
              <span className="material-symbols-outlined text-[20px]" aria-hidden>
                add_photo_alternate
              </span>
            </button>
          )}
        </div>
      )}

      {lightboxOpen && active && (
        <Lightbox
          photo={active}
          index={activeIndex}
          total={photos.length}
          onClose={() => setLightboxOpen(false)}
          onPrev={multiple ? () => go(-1) : undefined}
          onNext={multiple ? () => go(1) : undefined}
        />
      )}
    </div>
  );
}

/** Left/right arrow and Escape, only while the full-screen view is open. */
function useGalleryKeyboard(
  open: boolean,
  photoCount: number,
  go: (delta: number) => void,
  close: () => void,
) {
  useEffect(() => {
    if (!open) {
      return;
    }
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') {
        close();
        return;
      }
      if (photoCount < 2) {
        return;
      }
      if (event.key === 'ArrowLeft') {
        event.preventDefault();
        go(-1);
      } else if (event.key === 'ArrowRight') {
        event.preventDefault();
        go(1);
      }
    }
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [open, photoCount, go, close]);
}

function GalleryArrow({
  direction,
  onClick,
}: {
  direction: 'prev' | 'next';
  onClick: () => void;
}) {
  const isPrev = direction === 'prev';
  return (
    <button
      type="button"
      onClick={onClick}
      aria-label={isPrev ? 'Ảnh trước' : 'Ảnh tiếp theo'}
      title={isPrev ? 'Ảnh trước' : 'Ảnh tiếp theo'}
      className={`absolute top-1/2 flex h-9 w-9 -translate-y-1/2 items-center justify-center rounded-full bg-black/45 text-white transition hover:bg-black/65 focus:outline-none focus-visible:ring-3 focus-visible:ring-white/60 ${
        isPrev ? 'left-2' : 'right-2'
      }`}
    >
      <span className="material-symbols-outlined text-[20px]" aria-hidden>
        {isPrev ? 'chevron_left' : 'chevron_right'}
      </span>
    </button>
  );
}

/**
 * The full-photo view. Rendered in place rather than through a portal so it
 * inherits the page's tokens; it still covers the viewport and takes focus.
 */
function Lightbox({
  photo,
  index,
  total,
  onClose,
  onPrev,
  onNext,
}: {
  photo: GalleryPhoto;
  index: number;
  total: number;
  onClose: () => void;
  onPrev?: () => void;
  onNext?: () => void;
}) {
  const closeRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    closeRef.current?.focus();
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      document.body.style.overflow = previousOverflow;
    };
  }, []);

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-label={`Ảnh lớn ${index + 1} trên ${total}`}
      className="fixed inset-0 z-[70] flex flex-col bg-black/92"
      onClick={onClose}
    >
      <div className="flex items-center justify-end gap-2 p-3">
        <span className="mr-auto rounded-full bg-white/12 px-2.5 py-1 text-xs font-semibold text-white tnum">
          {index + 1} / {total}
        </span>
        <button
          ref={closeRef}
          type="button"
          onClick={onClose}
          aria-label="Đóng ảnh lớn"
          className="flex h-9 w-9 items-center justify-center rounded-full bg-white/12 text-white transition hover:bg-white/25 focus:outline-none focus-visible:ring-3 focus-visible:ring-white/60"
        >
          <span className="material-symbols-outlined text-[20px]" aria-hidden>
            close
          </span>
        </button>
      </div>

      <div
        className="relative flex min-h-0 flex-1 items-center justify-center px-4 pb-4"
        onClick={(event) => event.stopPropagation()}
      >
        {onPrev && (
          <button
            type="button"
            onClick={onPrev}
            aria-label="Ảnh trước"
            className="absolute left-3 flex h-11 w-11 items-center justify-center rounded-full bg-white/12 text-white transition hover:bg-white/25 focus:outline-none focus-visible:ring-3 focus-visible:ring-white/60"
          >
            <span className="material-symbols-outlined text-[24px]" aria-hidden>
              chevron_left
            </span>
          </button>
        )}
        <img
          src={photo.src}
          alt={photo.alt}
          className="max-h-full max-w-full rounded-lg object-contain"
        />
        {onNext && (
          <button
            type="button"
            onClick={onNext}
            aria-label="Ảnh tiếp theo"
            className="absolute right-3 flex h-11 w-11 items-center justify-center rounded-full bg-white/12 text-white transition hover:bg-white/25 focus:outline-none focus-visible:ring-3 focus-visible:ring-white/60"
          >
            <span className="material-symbols-outlined text-[24px]" aria-hidden>
              chevron_right
            </span>
          </button>
        )}
      </div>

      <p className="truncate px-4 pb-4 text-center text-xs text-white/70">{photo.alt}</p>
    </div>
  );
}
