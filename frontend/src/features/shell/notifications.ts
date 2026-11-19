import type { BuildingDto, FloorDto, RoomDto } from '../../lib/api/types';

/**
 * The server has no notification table and no notification app service, so a
 * bell fed by an invented feed would be exactly what master prompt §3.3
 * forbids: a hardcoded count behind a decorative control.
 *
 * These rows are instead derived from state the server really returns for
 * this account — the same three scoped lists the screens already read. Each
 * row therefore describes something that is genuinely true right now, and the
 * badge counts only the rows that exist. There is no read/unread state,
 * because nothing on the server records one; the badge is a plain count of
 * open items, not an unread count.
 */

export type NotificationTone = 'warning' | 'error' | 'neutral';

export interface ShellNotification {
  key: string;
  /** Material Symbols name, matching the rest of the shell. */
  icon: string;
  title: string;
  detail: string;
  to: string;
  tone: NotificationTone;
}

/**
 * What the count means, in words. Stated in the UI so the badge is never read
 * as an unread counter it cannot be.
 */
export const NOTIFICATION_COUNT_HINT = 'Số mục cần chú ý, không phải số thông báo chưa đọc.';

export function deriveNotifications(
  buildings: BuildingDto[],
  floors: FloorDto[],
  rooms: RoomDto[],
): ShellNotification[] {
  const buildingById = new Map(buildings.map((building) => [building.id, building]));
  const floorById = new Map(floors.map((floor) => [floor.id, floor]));

  const rows: ShellNotification[] = [];

  for (const room of rooms) {
    if (room.status === 4) {
      const floor = floorById.get(room.floorId);
      const building = floor ? buildingById.get(floor.buildingId) : undefined;
      rows.push({
        key: `room-maintenance:${room.id}`,
        icon: 'build',
        title: `Phòng ${room.roomNumber} đang bảo trì`,
        detail: [building?.name, floor?.name].filter(Boolean).join(' · ') || room.name,
        to: `/rooms/${room.id}`,
        tone: 'warning',
      });
    } else if (room.status === 3) {
      const floor = floorById.get(room.floorId);
      const building = floor ? buildingById.get(floor.buildingId) : undefined;
      rows.push({
        key: `room-reserved:${room.id}`,
        icon: 'event_available',
        title: `Phòng ${room.roomNumber} đã được đặt`,
        detail: [building?.name, floor?.name].filter(Boolean).join(' · ') || room.name,
        to: `/rooms/${room.id}`,
        tone: 'neutral',
      });
    }
  }

  for (const floor of floors) {
    if (floor.status === 2) {
      const building = buildingById.get(floor.buildingId);
      rows.push({
        key: `floor-maintenance:${floor.id}`,
        icon: 'layers',
        title: `${floor.name} đang bảo trì`,
        detail: building?.name ?? '',
        to: `/buildings/${floor.buildingId}/floors/${floor.id}`,
        tone: 'error',
      });
    }
  }

  // Maintenance before reservations, and the more serious floor state first,
  // so the list opens on what needs attention rather than on noise.
  const order: Record<NotificationTone, number> = { error: 0, warning: 1, neutral: 2 };
  return rows.sort(
    (a, b) =>
      order[a.tone] - order[b.tone] ||
      a.title.localeCompare(b.title, 'vi', { numeric: true }),
  );
}
