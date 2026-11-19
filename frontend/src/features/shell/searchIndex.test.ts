import { describe, expect, it } from 'vitest';
import type { BuildingDto, FloorDto, RoomDto } from '../../lib/api/types';
import { foldForSearch, searchIndex } from './searchIndex';

/**
 * The header search has no endpoint of its own: it filters the three lists the
 * screens already hold. These cases pin what a row is allowed to say and what
 * has to match.
 */

function building(overrides: Partial<BuildingDto> = {}): BuildingDto {
  return {
    id: 'b1',
    creationTime: '2026-01-01T00:00:00Z',
    creatorId: null,
    lastModificationTime: null,
    lastModifierId: null,
    name: 'Ngọc Trang Building',
    address: '123 Đường ABC, Quận 1',
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
    roomType: 'Phòng tiêu chuẩn',
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

describe('foldForSearch', () => {
  it('folds Vietnamese diacritics and đ so an unaccented keyboard still matches', () => {
    expect(foldForSearch('Tòa nhà Ngọc Trang')).toBe('toa nha ngoc trang');
    expect(foldForSearch('ĐƯỜNG ABC')).toBe('duong abc');
  });
});

describe('searchIndex', () => {
  it('returns nothing for a blank query rather than the whole estate', () => {
    expect(searchIndex('   ', [building()], [floor()], [room()])).toEqual([]);
  });

  it('finds a room by number and gives it its building and floor as context', () => {
    const results = searchIndex('101', [building()], [floor()], [room()]);

    expect(results).toHaveLength(1);
    expect(results[0]).toMatchObject({
      kind: 'room',
      title: 'Phòng 101',
      subtitle: 'Ngọc Trang Building · Tầng 1 · 101',
      to: '/rooms/r1',
    });
  });

  it('finds every kind by a diacritic-free query and orders buildings first', () => {
    const results = searchIndex('ngoc trang', [building()], [floor()], [room()]);

    expect(results[0]!.kind).toBe('building');
    expect(results.map((result) => result.kind)).toContain('room');
  });

  it('requires every term to match, so a second word narrows the list', () => {
    const second = room({ id: 'r2', roomNumber: '202', name: 'Phòng 202' });
    const floors = [floor(), floor({ id: 'f2', floorNumber: 2, name: 'Tầng 2' })];
    const withSecond = second;

    const broad = searchIndex('phong', [building()], floors, [room(), withSecond]);
    const narrow = searchIndex('phong 101', [building()], floors, [room(), withSecond]);

    expect(broad).toHaveLength(2);
    expect(narrow).toHaveLength(1);
    expect(narrow[0]!.to).toBe('/rooms/r1');
  });

  it('matches a room type, which is how the owner thinks of a room', () => {
    const results = searchIndex('tieu chuan', [building()], [floor()], [room()]);

    expect(results.map((result) => result.to)).toEqual(['/rooms/r1']);
  });

  it('links a floor to its plan and a building to its own page', () => {
    expect(searchIndex('tang 1', [], [floor()], [])[0]!.to).toBe('/buildings/b1/floors/f1');
    expect(searchIndex('duong abc', [building()], [], [])[0]!.to).toBe('/buildings/b1');
  });

  it('never offers a room whose building the server did not return', () => {
    // An empty building list is what a caller with no grant actually receives.
    const results = searchIndex('101', [], [], []);

    expect(results).toEqual([]);
  });

  it('caps the list so the panel cannot grow past the viewport', () => {
    const many = Array.from({ length: 20 }, (_, index) =>
      room({ id: `r${index}`, roomNumber: `10${index}`, name: `Phòng 10${index}` }),
    );

    expect(searchIndex('phong', [], [floor()], many)).toHaveLength(8);
  });
});
