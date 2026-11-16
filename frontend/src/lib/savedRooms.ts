/**
 * Rooms the visitor starred, kept in this browser only.
 *
 * There is no saved-room endpoint on the server yet, so this store is
 * deliberately named and labelled "trên trình duyệt này" everywhere it
 * surfaces: it must never read as an account-level favourite.
 */

const STORAGE_KEY = 'ngoctrang.savedRooms';

export const SAVED_ROOMS_EVENT = 'ngoctrang:saved-rooms-changed';

function read(): string[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) {
      return [];
    }
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) {
      return [];
    }
    return parsed.filter((id): id is string => typeof id === 'string');
  } catch {
    // Private windows and blocked storage both throw here; an empty list is
    // the honest answer, and the toggle still works for this render.
    return [];
  }
}

function write(ids: string[]): void {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(ids));
  } catch {
    return;
  }
  window.dispatchEvent(new Event(SAVED_ROOMS_EVENT));
}

export const savedRoomStore = {
  list(): string[] {
    return read();
  },

  has(roomId: string): boolean {
    return read().includes(roomId);
  },

  toggle(roomId: string): boolean {
    const current = read();
    const exists = current.includes(roomId);
    write(exists ? current.filter((id) => id !== roomId) : [...current, roomId]);
    return !exists;
  },
};
