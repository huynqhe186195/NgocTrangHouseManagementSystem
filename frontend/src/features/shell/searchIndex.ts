import type { BuildingDto, FloorDto, RoomDto } from '../../lib/api/types';

/**
 * The header search runs over the three flat lists the screens already load
 * (`/building`, `/floor`, `/room`), joined the same way `useBuildingSnapshot`
 * joins them. Nothing extra is fetched and nothing is invented: a row exists
 * only because the server returned the building, floor or room it names.
 *
 * The lists are building-scoped by the server, so this can only ever reveal
 * what the signed-in account is already allowed to read.
 */

export type SearchResultKind = 'building' | 'floor' | 'room';

export interface SearchResult {
  /** Unique per row: a room and a floor can share a number. */
  key: string;
  kind: SearchResultKind;
  title: string;
  /** Building · floor context, so two "101"s are never ambiguous. */
  subtitle: string;
  to: string;
}

/**
 * Vietnamese typed without the diacritics is the common case on a desktop
 * keyboard, so "toa nha" has to find "Tòa nhà". `đ` is not a combining mark
 * and survives NFD, so it is folded separately.
 */
export function foldForSearch(value: string): string {
  return value
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .replace(/đ/g, 'd')
    .replace(/Đ/g, 'D')
    .toLowerCase();
}

interface Indexed {
  result: SearchResult;
  /** Folded text every term has to appear in. */
  haystack: string;
}

function buildIndex(
  buildings: BuildingDto[],
  floors: FloorDto[],
  rooms: RoomDto[],
): Indexed[] {
  const buildingById = new Map(buildings.map((building) => [building.id, building]));
  const floorById = new Map(floors.map((floor) => [floor.id, floor]));

  const rows: Indexed[] = [];

  for (const building of buildings) {
    rows.push({
      result: {
        key: `building:${building.id}`,
        kind: 'building',
        title: building.name,
        subtitle: building.address,
        to: `/buildings/${building.id}`,
      },
      haystack: foldForSearch(`${building.name} ${building.address}`),
    });
  }

  for (const floor of floors) {
    // A floor's building may be outside the authorized set; the server would
    // not have returned the floor then, but the map lookup stays total.
    const building = buildingById.get(floor.buildingId);
    const buildingName = building?.name ?? '';
    rows.push({
      result: {
        key: `floor:${floor.id}`,
        kind: 'floor',
        title: floor.name,
        subtitle: buildingName,
        to: `/buildings/${floor.buildingId}/floors/${floor.id}`,
      },
      haystack: foldForSearch(
        `${floor.name} Tầng ${floor.floorNumber} ${buildingName}`,
      ),
    });
  }

  for (const room of rooms) {
    const floor = floorById.get(room.floorId);
    const building = floor ? buildingById.get(floor.buildingId) : undefined;
    const context = [building?.name, floor?.name].filter(Boolean).join(' · ');
    rows.push({
      result: {
        key: `room:${room.id}`,
        kind: 'room',
        title: room.name,
        subtitle: context ? `${context} · ${room.roomNumber}` : room.roomNumber,
        to: `/rooms/${room.id}`,
      },
      haystack: foldForSearch(
        [room.roomNumber, room.name, room.roomType ?? '', floor?.name ?? '', building?.name ?? '']
          .join(' '),
      ),
    });
  }

  return rows;
}

export const SEARCH_RESULT_LIMIT = 8;

/**
 * Every term has to match, in any field, so "nha 101" narrows rather than
 * widens. Rows are ordered buildings → floors → rooms because that is the
 * order the sidebar walks the hierarchy.
 */
export function searchIndex(
  query: string,
  buildings: BuildingDto[],
  floors: FloorDto[],
  rooms: RoomDto[],
  limit = SEARCH_RESULT_LIMIT,
): SearchResult[] {
  const terms = foldForSearch(query)
    .split(/\s+/)
    .filter(Boolean);
  if (terms.length === 0) {
    return [];
  }

  const order: Record<SearchResultKind, number> = { building: 0, floor: 1, room: 2 };
  return buildIndex(buildings, floors, rooms)
    .filter(({ haystack }) => terms.every((term) => haystack.includes(term)))
    .sort(
      (a, b) =>
        order[a.result.kind] - order[b.result.kind] ||
        a.result.title.localeCompare(b.result.title, 'vi', { numeric: true }),
    )
    .slice(0, limit)
    .map(({ result }) => result);
}
