import { describe, expect, it } from 'vitest';
import type { BuildingDto, FloorDto, RoomDto } from '../../lib/api/types';
import { deriveNotifications } from './notifications';

/**
 * The server has no notification table, so the bell reports state the lists
 * genuinely carry. These cases pin that only real rows produce a count and
 * that the ordering puts the worse state first.
 */

function building(overrides: Partial<BuildingDto> = {}): BuildingDto {
  return {
    id: 'b1',
    creationTime: '2026-01-01T00:00:00Z',
    creatorId: null,
    lastModificationTime: null,
    lastModifierId: null,
    name: 'Ngọc Trang Building',
    address: '123 Đường ABC',
    description: null,
    hasPhoto: false,
    ...overrides,
  };
}

function floor(overrides: Partial<FloorDto> = {}): FloorDto {
  return {
    id: 'f1',
    creationTime: '2026-01-01T00:00:00Z',
    creatorId: null,
    lastModificationTime: null,
    lastModifierId: null,
    buildingId: 'b1',
    floorNumber: 1,
    name: 'Tầng 1',
    description: null,
    status: 1,
    ...overrides,
  };
}

function room(overrides: Partial<RoomDto> = {}): RoomDto {
  return {
    id: 'r1',
    creationTime: '2026-01-01T00:00:00Z',
    creatorId: null,
    lastModificationTime: null,
    lastModifierId: null,
    floorId: 'f1',
    roomNumber: '101',
    name: 'Phòng 101',
    area: 24,
    capacity: 2,
    status: 1,
    description: null,
    roomType: null,
    locationNote: null,
    direction: null,
    monthlyRent: null,
    layoutX: null,
    layoutY: null,
    layoutWidth: null,
    layoutHeight: null,
    layoutShape: null,
    layoutRotationDegrees: null,
    layoutPoints: null,
    equipmentCount: 0,
    photoCount: 0,
    ...overrides,
  };
}

describe('deriveNotifications', () => {
  it('reports nothing when every room and floor is in service', () => {
    expect(deriveNotifications([building()], [floor()], [room()])).toEqual([]);
  });

  it('raises a row for a room held for maintenance and links to it', () => {
    const rows = deriveNotifications([building()], [floor()], [room({ status: 4 })]);

    expect(rows).toHaveLength(1);
    expect(rows[0]).toMatchObject({
      title: 'Phòng 101 đang bảo trì',
      detail: 'Ngọc Trang Building · Tầng 1',
      to: '/rooms/r1',
      tone: 'warning',
    });
  });

  it('reports a reserved room without calling it a problem', () => {
    const rows = deriveNotifications([building()], [floor()], [room({ status: 3 })]);

    expect(rows[0]).toMatchObject({ tone: 'neutral', to: '/rooms/r1' });
  });

  it('reports a floor under maintenance and puts it first', () => {
    const rows = deriveNotifications(
      [building()],
      [floor({ status: 2 })],
      [room({ status: 4 }), room({ id: 'r2', roomNumber: '102', status: 3 })],
    );

    expect(rows).toHaveLength(3);
    expect(rows[0]).toMatchObject({
      title: 'Tầng 1 đang bảo trì',
      tone: 'error',
      to: '/buildings/b1/floors/f1',
    });
    expect(rows[1]!.title).toBe('Phòng 101 đang bảo trì');
    expect(rows[2]!.tone).toBe('neutral');
  });

  it('ignores rooms whose floor the caller cannot see', () => {
    const rows = deriveNotifications([], [], [room({ status: 4 })]);

    // The row is still real — the room itself was returned — but with no
    // building context to name, so it falls back to the room's own name.
    expect(rows).toHaveLength(1);
    expect(rows[0]!.detail).toBe('Phòng 101');
  });
});
