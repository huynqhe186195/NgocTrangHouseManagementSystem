import { describe, expect, it } from 'vitest';
import type { BuildingDto, FloorDto, RoomDto } from './types';

/**
 * Mirrors the aggregation inside useBuildingSnapshot: the server returns
 * three flat lists and Room links to Floor (not Building), so the counts
 * are computed client-side.
 */
function aggregate(buildingId: string, floors: FloorDto[], rooms: RoomDto[]) {
  const buildingFloors = floors
    .filter((floor) => floor.buildingId === buildingId)
    .sort((a, b) => a.floorNumber - b.floorNumber);
  const floorIds = new Set(buildingFloors.map((floor) => floor.id));
  const buildingRooms = rooms.filter((room) => floorIds.has(room.floorId));

  const roomsByFloor = new Map<string, RoomDto[]>();
  for (const room of buildingRooms) {
    const bucket = roomsByFloor.get(room.floorId) ?? [];
    bucket.push(room);
    roomsByFloor.set(room.floorId, bucket);
  }

  const occupiedCount = buildingRooms.filter((room) => room.status === 2).length;
  return {
    floors: buildingFloors,
    roomCount: buildingRooms.length,
    occupiedCount,
    occupancyRate:
      buildingRooms.length === 0 ? 0 : Math.round((occupiedCount / buildingRooms.length) * 100),
    roomsByFloor,
  };
}

const T0 = '2026-11-13T00:00:00Z';

function building(id: string, name: string): BuildingDto {
  return { id, name, address: 'x', description: null, creationTime: T0, creatorId: null, lastModificationTime: null, lastModifierId: null };
}

function floor(id: string, buildingId: string, floorNumber: number): FloorDto {
  return { id, buildingId, floorNumber, name: `Tầng ${floorNumber}`, description: null, creationTime: T0, creatorId: null, lastModificationTime: null, lastModifierId: null };
}

function room(id: string, floorId: string, roomNumber: string, status: 1 | 2 | 3 | 4 | 5): RoomDto {
  return {
    id,
    floorId,
    roomNumber,
    name: `Phòng ${roomNumber}`,
    area: 20,
    capacity: 2,
    status,
    description: null,
    creationTime: T0,
    creatorId: null,
    lastModificationTime: null,
    lastModifierId: null,
  };
}

describe('building snapshot aggregation', () => {
  const buildings = [building('b1', 'A'), building('b2', 'B')];
  const floors = [floor('f1', 'b1', 1), floor('f2', 'b1', 2), floor('f3', 'b2', 1)];
  const rooms = [
    room('r1', 'f1', '101', 1),
    room('r2', 'f1', '102', 2),
    room('r3', 'f2', '201', 2),
    room('r4', 'f2', '202', 4),
    room('r5', 'f3', '101', 1),
  ];

  it('counts only rooms whose floor belongs to the requested building', () => {
    const result = aggregate('b1', floors, rooms);
    expect(result.roomCount).toBe(4);
    expect(result.occupiedCount).toBe(2);
    expect(result.occupancyRate).toBe(50);
  });

  it('excludes another building that reuses the same room number', () => {
    const b1 = aggregate('b1', floors, rooms);
    const b2 = aggregate('b2', floors, rooms);
    expect(b1.roomCount).toBe(4);
    expect(b2.roomCount).toBe(1);
    expect(b2.roomsByFloor.get('f3')?.map((r) => r.id)).toEqual(['r5']);
  });

  it('returns floors sorted by floor number', () => {
    expect(aggregate('b1', floors, rooms).floors.map((f) => f.floorNumber)).toEqual([1, 2]);
  });

  it('reports 0% occupancy instead of NaN when a building has no rooms', () => {
    const empty = aggregate('b1', [floor('f9', 'b1', 1)], []);
    expect(empty.roomCount).toBe(0);
    expect(empty.occupancyRate).toBe(0);
  });

  it('ignores rooms belonging to a floor of the other building', () => {
    const result = aggregate('b2', floors, rooms);
    expect(result.roomsByFloor.get('f1')).toBeUndefined();
  });

  it('keeps the building list independent of the aggregation scope', () => {
    expect(buildings).toHaveLength(2);
  });
});
