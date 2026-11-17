import { useEffect, useState } from 'react';
import { SAVED_ROOMS_EVENT, savedRoomStore } from '../../lib/savedRooms';

/**
 * Save + share for the room the visitor is looking at.
 *
 * Save writes to this browser only (no saved-room endpoint exists), and the
 * label says so. Share opens the platform sheet when the browser has one and
 * otherwise copies the room's own link, reporting which one happened.
 */
export function RoomSaveShare({
  roomId,
  roomNumber,
  href,
}: {
  roomId: string;
  roomNumber: string;
  /** The screen the visitor is on; defaults to the admin room page. */
  href?: string;
}) {
  const [saved, setSaved] = useState(() => savedRoomStore.has(roomId));
  const [message, setMessage] = useState<string | null>(null);

  // The panel follows the selected room, so re-read when the id changes.
  useEffect(() => {
    setSaved(savedRoomStore.has(roomId));
  }, [roomId]);

  // Another tab or another save button may have changed the list.
  useEffect(() => {
    function sync() {
      setSaved(savedRoomStore.has(roomId));
    }
    window.addEventListener(SAVED_ROOMS_EVENT, sync);
    window.addEventListener('storage', sync);
    return () => {
      window.removeEventListener(SAVED_ROOMS_EVENT, sync);
      window.removeEventListener('storage', sync);
    };
  }, [roomId]);

  useEffect(() => {
    if (!message) {
      return;
    }
    const timer = window.setTimeout(() => setMessage(null), 4000);
    return () => window.clearTimeout(timer);
  }, [message]);

  function toggleSave() {
    const nowSaved = savedRoomStore.toggle(roomId);
    setSaved(nowSaved);
    setMessage(
      nowSaved
        ? `Đã lưu phòng ${roomNumber} trên trình duyệt này.`
        : `Đã bỏ lưu phòng ${roomNumber} trên trình duyệt này.`,
    );
  }

  async function share() {
    const url = new URL(href ?? `/rooms/${roomId}`, window.location.origin).toString();
    const title = `Phòng ${roomNumber}`;

    const nav = navigator as Navigator & {
      share?: (data: { title?: string; text?: string; url?: string }) => Promise<void>;
      canShare?: (data: { url?: string }) => boolean;
    };

    if (typeof nav.share === 'function' && (nav.canShare?.({ url }) ?? true)) {
      try {
        await nav.share({ title, url });
        setMessage('Đã mở hộp thoại chia sẻ.');
        return;
      } catch (shareError) {
        // AbortError means the visitor closed the sheet; anything else falls
        // through to copying so the action still does something.
        if (shareError instanceof DOMException && shareError.name === 'AbortError') {
          return;
        }
      }
    }

    try {
      await navigator.clipboard.writeText(url);
      setMessage('Đã sao chép đường dẫn phòng vào bộ nhớ tạm.');
    } catch {
      setMessage(`Không sao chép được tự động. Đường dẫn phòng: ${url}`);
    }
  }

  return (
    <div className="flex flex-col items-end gap-1">
      <div className="flex items-center gap-2">
        <button
          type="button"
          onClick={toggleSave}
          aria-pressed={saved}
          title={
            saved
              ? 'Bỏ lưu phòng này trên trình duyệt hiện tại'
              : 'Lưu phòng này trên trình duyệt hiện tại (không đồng bộ vào tài khoản)'
          }
          className={
            'flex h-10 items-center gap-2 rounded-lg border px-4 text-sm font-semibold transition ' +
            (saved
              ? 'border-primary bg-primary/8 text-primary'
              : 'border-outline-variant text-on-surface-variant hover:bg-surface-low')
          }
        >
          <span
            className="material-symbols-outlined text-lg"
            style={saved ? { fontVariationSettings: "'FILL' 1" } : undefined}
            aria-hidden
          >
            favorite
          </span>
          {saved ? 'Đã lưu' : 'Lưu'}
        </button>

        <button
          type="button"
          onClick={() => void share()}
          className="flex h-10 items-center gap-2 rounded-lg border border-outline-variant px-4 text-sm font-semibold text-on-surface-variant transition hover:bg-surface-low"
        >
          <span className="material-symbols-outlined text-lg" aria-hidden>
            share
          </span>
          Chia sẻ
        </button>
      </div>

      <p className="text-[11px] leading-tight text-on-surface-variant">
        Lưu chỉ trên trình duyệt này, không đồng bộ vào tài khoản.
      </p>

      {message && (
        <p role="status" className="max-w-[320px] text-right text-[11px] leading-snug text-primary">
          {message}
        </p>
      )}
    </div>
  );
}
