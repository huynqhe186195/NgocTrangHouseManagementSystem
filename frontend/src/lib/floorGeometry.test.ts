import { describe, expect, it } from 'vitest';
import {
  MAX_ZOOM,
  MIN_ZOOM,
  buildPlanGeometry,
  clampZoom,
  formatZoom,
  hasGeometry,
  unplacedRooms,
  viewBoxFor,
  zoomIn,
  zoomOut,
} from './floorGeometry';
import type { FloorLayoutItemDto, RoomDto } from './api/types';

function room(overrides: Partial<RoomDto> = {}): RoomDto {
  return {
    id: 'r1',
    creationTime: '2026-01-01T00:00:00Z',
    creatorId: null,
    lastModificationTime: null,
    lastModifierId: null,
    floorId: 'f1',
    roomNumber: 'A101',
    name: 'Phòng A101',
    area: 22,
    capacity: 2,
    status: 1,
    description: null,
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

function item(overrides: Partial<FloorLayoutItemDto> = {}): FloorLayoutItemDto {
  return {
    id: 'i1',
    creationTime: '2026-01-01T00:00:00Z',
    creatorId: null,
    lastModificationTime: null,
    lastModifierId: null,
    floorId: 'f1',
    kind: 1,
    label: 'Hành lang',
    x: 0,
    y: 4,
    width: 12,
    height: 2,
    ...overrides,
  };
}

describe('hasGeometry', () => {
  it('accepts a fully placed room', () => {
    expect(hasGeometry(room({ layoutX: 1, layoutY: 2, layoutWidth: 3, layoutHeight: 4 }))).toBe(true);
  });

  it('rejects a room with any coordinate missing', () => {
    expect(hasGeometry(room({ layoutX: 1, layoutY: 2, layoutWidth: 3 }))).toBe(false);
  });

  it('rejects zero or negative extents', () => {
    expect(hasGeometry(room({ layoutX: 0, layoutY: 0, layoutWidth: 0, layoutHeight: 4 }))).toBe(false);
    expect(hasGeometry(room({ layoutX: 0, layoutY: 0, layoutWidth: 3, layoutHeight: -1 }))).toBe(false);
  });
});

describe('buildPlanGeometry', () => {
  it('returns null when no room has geometry, so the caller can fall back', () => {
    expect(buildPlanGeometry([room(), room({ id: 'r2' })], [])).toBeNull();
  });

  it('bounds the placed rooms and ignores unplaced ones', () => {
    const plan = buildPlanGeometry(
      [
        room({ id: 'a', layoutX: 0, layoutY: 0, layoutWidth: 4, layoutHeight: 3 }),
        room({ id: 'b', layoutX: 4, layoutY: 0, layoutWidth: 6, layoutHeight: 3 }),
        room({ id: 'c' }),
      ],
      [],
    );

    expect(plan).not.toBeNull();
    expect(plan!.rooms.map((r) => r.id)).toEqual(['a', 'b']);
    expect(plan!.bounds.width).toBeGreaterThan(10);
    expect(plan!.bounds.minX).toBeLessThan(0);
  });

  it('includes floor objects in the bounds', () => {
    const plan = buildPlanGeometry(
      [room({ layoutX: 0, layoutY: 0, layoutWidth: 4, layoutHeight: 3 })],
      [item({ x: 0, y: 3, width: 4, height: 2 })],
    );

    expect(plan!.bounds.height).toBeGreaterThan(5);
    expect(plan!.items).toHaveLength(1);
  });

  it('drops a floor object with a nonsensical extent', () => {
    const plan = buildPlanGeometry(
      [room({ layoutX: 0, layoutY: 0, layoutWidth: 4, layoutHeight: 3 })],
      [item({ width: 0 }), item({ id: 'i2' })],
    );

    expect(plan!.items.map((i) => i.id)).toEqual(['i2']);
  });
});

describe('zoom', () => {
  it('clamps to the supported range', () => {
    expect(clampZoom(0.1)).toBe(MIN_ZOOM);
    expect(clampZoom(9)).toBe(MAX_ZOOM);
    expect(clampZoom(1.25)).toBe(1.25);
  });

  it('treats a non-finite zoom as neutral', () => {
    expect(clampZoom(Number.NaN)).toBe(1);
  });

  it('steps up and down without leaving the range', () => {
    expect(zoomIn(1)).toBe(1.25);
    expect(zoomOut(1)).toBe(0.75);
    expect(zoomIn(MAX_ZOOM)).toBe(MAX_ZOOM);
    expect(zoomOut(MIN_ZOOM)).toBe(MIN_ZOOM);
  });

  it('formats the control label', () => {
    expect(formatZoom(1)).toBe('100%');
    expect(formatZoom(0.75)).toBe('75%');
  });
});

describe('viewBoxFor', () => {
  const bounds = { minX: 0, minY: 0, maxX: 10, maxY: 8, width: 10, height: 8 };

  it('shows the whole plan at 100%', () => {
    expect(viewBoxFor(bounds, 1)).toBe('0 0 10 8');
  });

  it('shrinks the window as zoom grows, staying centred', () => {
    expect(viewBoxFor(bounds, 2)).toBe('2.5 2 5 4');
  });

  it('widens the window when zoomed out', () => {
    expect(viewBoxFor(bounds, 0.5)).toBe('-5 -4 20 16');
  });
});

describe('unplacedRooms', () => {
  it('reports the rooms the plan cannot draw', () => {
    const rooms = [
      room({ id: 'a', layoutX: 0, layoutY: 0, layoutWidth: 3, layoutHeight: 3 }),
      room({ id: 'b' }),
    ];
    expect(unplacedRooms(rooms).map((r) => r.id)).toEqual(['b']);
  });
});
