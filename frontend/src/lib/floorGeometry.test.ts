import { describe, expect, it } from 'vitest';
import {
  GRID_STEP_METRES,
  MAX_ZOOM,
  MIN_SIZE_METRES,
  MIN_ZOOM,
  boxesEqual,
  buildPlanGeometry,
  clampBoxToBounds,
  clampZoom,
  defaultRoomBox,
  formatMetres,
  formatZoom,
  hasGeometry,
  insertPointOnPolyline,
  moveBox,
  movePolyline,
  movePolylinePoint,
  parsePoints,
  pointToMetres,
  polylineBounds,
  projectOnSegment,
  resizeBox,
  serializePoints,
  snapToGrid,
  unplacedRooms,
  viewBoxFor,
  viewBoxRectFor,
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
    points: null,
    thickness: null,
    cornerRadius: null,
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

  it('pans the window without changing its size', () => {
    const rest = viewBoxRectFor(bounds, 1);
    const panned = viewBoxRectFor(bounds, 1, { x: 2, y: -1 });
    expect(panned.width).toBe(rest.width);
    expect(panned.height).toBe(rest.height);
    expect(panned.x).toBe(rest.x + 2);
    expect(panned.y).toBe(rest.y - 1);
  });
});

describe('snapToGrid', () => {
  it('rounds to the nearest 10 cm', () => {
    expect(snapToGrid(2.34)).toBe(2.3);
    expect(snapToGrid(2.36)).toBe(2.4);
  });

  it('leaves a value already on the grid untouched', () => {
    expect(snapToGrid(2.3)).toBe(2.3);
    expect(snapToGrid(0)).toBe(0);
    expect(snapToGrid(-1.1)).toBe(-1.1);
  });

  it('never returns binary-float noise', () => {
    for (const raw of [0.1, 0.2, 0.3, 1.1, 7.7]) {
      expect(String(snapToGrid(raw)).length).toBeLessThanOrEqual(4);
    }
  });

  it('treats a non-finite value as the origin', () => {
    expect(snapToGrid(Number.NaN)).toBe(0);
    expect(snapToGrid(Number.POSITIVE_INFINITY)).toBe(0);
  });

  it('accepts a coarser step', () => {
    expect(snapToGrid(2.34, 0.5)).toBe(2.5);
    expect(GRID_STEP_METRES).toBe(0.1);
  });
});

describe('moveBox', () => {
  const box = { x: 1, y: 2, width: 4, height: 3 };

  it('moves by a snapped delta and keeps the size', () => {
    const moved = moveBox(box, 0.53, -0.47);
    expect(moved).toEqual({ x: 1.5, y: 1.5, width: 4, height: 3 });
  });

  it('does not change the box when the delta is under half a step', () => {
    expect(moveBox(box, 0.04, 0.04)).toEqual(box);
  });
});

describe('resizeBox', () => {
  const box = { x: 1, y: 1, width: 4, height: 3 };

  it('drags the south-east corner and keeps the north-west one', () => {
    expect(resizeBox(box, 'se', { x: 6.02, y: 5.04 })).toEqual({
      x: 1,
      y: 1,
      width: 5,
      height: 4,
    });
  });

  it('drags the north-west corner and keeps the south-east one', () => {
    expect(resizeBox(box, 'nw', { x: 0.5, y: 0.5 })).toEqual({
      x: 0.5,
      y: 0.5,
      width: 4.5,
      height: 3.5,
    });
  });

  it('drags the north-east corner: width grows, height moves up', () => {
    expect(resizeBox(box, 'ne', { x: 6, y: 0 })).toEqual({
      x: 1,
      y: 0,
      width: 5,
      height: 4,
    });
  });

  it('drags the south-west corner: width moves left, height grows', () => {
    expect(resizeBox(box, 'sw', { x: 0, y: 5 })).toEqual({
      x: 0,
      y: 1,
      width: 5,
      height: 4,
    });
  });

  it('refuses to shrink below the minimum size', () => {
    const tiny = resizeBox(box, 'se', { x: 0.2, y: 0.2 });
    expect(tiny.width).toBe(MIN_SIZE_METRES);
    expect(tiny.height).toBe(MIN_SIZE_METRES);
  });

  it('refuses to drag a corner past the opposite edge', () => {
    const flipped = resizeBox(box, 'nw', { x: 900, y: 900 });
    expect(flipped.width).toBe(MIN_SIZE_METRES);
    expect(flipped.height).toBe(MIN_SIZE_METRES);
    expect(flipped.x).toBeCloseTo(5 - MIN_SIZE_METRES, 6);
  });
});

describe('clampBoxToBounds', () => {
  const bounds = { minX: 0, minY: 0, maxX: 10, maxY: 8, width: 10, height: 8 };

  it('pulls an object back inside the drawing', () => {
    const clamped = clampBoxToBounds({ x: 20, y: -5, width: 2, height: 2 }, bounds);
    expect(clamped.x).toBe(8);
    expect(clamped.y).toBe(0);
  });

  it('never returns a box larger than the drawing', () => {
    const clamped = clampBoxToBounds({ x: 0, y: 0, width: 40, height: 40 }, bounds);
    expect(clamped.width).toBe(10);
    expect(clamped.height).toBe(8);
  });
});

describe('defaultRoomBox', () => {
  const bounds = { minX: 0, minY: 0, maxX: 10, maxY: 8, width: 10, height: 8 };

  it('drops a new room in the middle of the plan', () => {
    expect(defaultRoomBox(bounds, 4, 4)).toEqual({ x: 3, y: 2, width: 4, height: 4 });
  });

  it('shrinks the default size to fit a small drawing', () => {
    const small = { minX: 0, minY: 0, maxX: 2, maxY: 2, width: 2, height: 2 };
    const box = defaultRoomBox(small, 4, 4);
    expect(box.width).toBe(2);
    expect(box.height).toBe(2);
  });
});

describe('pointToMetres', () => {
  it('passes finite coordinates through unchanged', () => {
    expect(pointToMetres({ x: 3.25, y: -1.5 })).toEqual({ x: 3.25, y: -1.5 });
  });

  it('treats a non-finite coordinate as the origin', () => {
    expect(pointToMetres({ x: Number.NaN, y: undefined as unknown as number })).toEqual({
      x: 0,
      y: 0,
    });
  });
});

describe('boxesEqual / formatMetres', () => {
  const box = { x: 1, y: 1, width: 2, height: 2 };

  it('compares all four numbers', () => {
    expect(boxesEqual(box, { ...box })).toBe(true);
    expect(boxesEqual(box, { ...box, width: 2.1 })).toBe(false);
  });

  it('renders metres in Vietnamese grouping', () => {
    expect(formatMetres(2.3)).toBe('2,3 m');
    expect(formatMetres(12)).toBe('12 m');
    expect(formatMetres(Number.NaN)).toBe('—');
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

describe('parsePoints', () => {
  it('reads a stored centreline', () => {
    expect(parsePoints('[{"x":0,"y":1},{"x":4,"y":1}]')).toEqual([
      { x: 0, y: 1 },
      { x: 4, y: 1 },
    ]);
  });

  it('treats an absent column as no centreline', () => {
    expect(parsePoints(null)).toBeNull();
    expect(parsePoints('')).toBeNull();
  });

  it('rejects broken JSON and non-arrays', () => {
    expect(parsePoints('{oops')).toBeNull();
    expect(parsePoints('{"x":0,"y":0}')).toBeNull();
  });

  it('rejects a point missing a coordinate', () => {
    expect(parsePoints('[{"x":0},{"x":1,"y":2}]')).toBeNull();
    expect(parsePoints('[{"x":0,"y":"1"},{"x":1,"y":2}]')).toBeNull();
  });

  it('rejects a non-finite coordinate', () => {
    expect(parsePoints('[{"x":null,"y":0},{"x":1,"y":2}]')).toBeNull();
  });

  it('applies the caller minimum: a polygon needs three points', () => {
    expect(parsePoints('[{"x":0,"y":0},{"x":1,"y":0}]', 3)).toBeNull();
    expect(
      parsePoints('[{"x":0,"y":0},{"x":1,"y":0},{"x":1,"y":1}]', 3),
    ).toHaveLength(3);
  });
});

describe('serializePoints', () => {
  it('round-trips through parsePoints', () => {
    const points = [
      { x: 1.2, y: 3.4 },
      { x: 5.6, y: 3.4 },
      { x: 5.6, y: 7.8 },
    ];
    expect(parsePoints(serializePoints(points))).toEqual(points);
  });

  it('drops extra properties so a stale field cannot leak into storage', () => {
    const dirty = [{ x: 1, y: 2, selected: true } as unknown as { x: number; y: number }];
    expect(serializePoints(dirty)).toBe('[{"x":1,"y":2}]');
  });
});

describe('polylineBounds', () => {
  it('covers every vertex, not just the first and last', () => {
    const bounds = polylineBounds([
      { x: 0, y: 0 },
      { x: 2, y: 6 },
      { x: 8, y: 1 },
    ]);
    expect(bounds).toEqual({ minX: 0, minY: 0, maxX: 8, maxY: 6, width: 8, height: 6 });
  });

  it('has no bounds for an empty line', () => {
    expect(polylineBounds([])).toBeNull();
  });
});

describe('projectOnSegment', () => {
  it('finds the closest point on the interior of a segment', () => {
    const hit = projectOnSegment({ x: 0, y: 0 }, { x: 10, y: 0 }, { x: 4, y: 3 });
    expect(hit.t).toBeCloseTo(0.4, 6);
    expect(hit.point).toEqual({ x: 4, y: 0 });
    expect(hit.distance).toBeCloseTo(3, 6);
  });

  it('clamps past the end so a click beyond the wall still lands on it', () => {
    const hit = projectOnSegment({ x: 0, y: 0 }, { x: 10, y: 0 }, { x: 20, y: 0 });
    expect(hit.t).toBe(1);
    expect(hit.point).toEqual({ x: 10, y: 0 });
  });

  it('measures to the vertex when the segment has no length', () => {
    const hit = projectOnSegment({ x: 2, y: 2 }, { x: 2, y: 2 }, { x: 5, y: 6 });
    expect(hit.distance).toBeCloseTo(5, 6);
  });
});

describe('insertPointOnPolyline', () => {
  const straight = [
    { x: 0, y: 0 },
    { x: 10, y: 0 },
  ];

  it('splits the segment nearest the click', () => {
    const result = insertPointOnPolyline(straight, { x: 3.04, y: 0.6 });
    expect(result).not.toBeNull();
    expect(result!.index).toBe(1);
    expect(result!.points).toEqual([
      { x: 0, y: 0 },
      { x: 3, y: 0 },
      { x: 10, y: 0 },
    ]);
  });

  it('picks the nearer of two segments on a bent wall', () => {
    const bent = [
      { x: 0, y: 0 },
      { x: 5, y: 0 },
      { x: 5, y: 5 },
    ];
    const result = insertPointOnPolyline(bent, { x: 4.5, y: 3 });
    expect(result!.index).toBe(2);
    expect(result!.points).toHaveLength(4);
  });

  it('refuses a bend that would sit on top of an existing vertex', () => {
    expect(insertPointOnPolyline(straight, { x: 0.02, y: 0 })).toBeNull();
    expect(insertPointOnPolyline(straight, { x: 10, y: 0 })).toBeNull();
  });

  it('refuses a wall with fewer than two vertices', () => {
    expect(insertPointOnPolyline([{ x: 1, y: 1 }], { x: 2, y: 2 })).toBeNull();
    expect(insertPointOnPolyline([], { x: 2, y: 2 })).toBeNull();
  });
});

describe('movePolyline / movePolylinePoint', () => {
  const wall = [
    { x: 0, y: 0 },
    { x: 4, y: 0 },
    { x: 4, y: 2 },
  ];

  it('shifts every vertex by the same snapped delta', () => {
    expect(movePolyline(wall, 0.52, 1.03)).toEqual([
      { x: 0.5, y: 1 },
      { x: 4.5, y: 1 },
      { x: 4.5, y: 3 },
    ]);
  });

  it('moves one vertex and snaps it, leaving the rest alone', () => {
    expect(movePolylinePoint(wall, 1, { x: 6.07, y: -0.02 })).toEqual([
      { x: 0, y: 0 },
      { x: 6.1, y: 0 },
      { x: 4, y: 2 },
    ]);
  });

  it('ignores an index that does not exist', () => {
    expect(movePolylinePoint(wall, 9, { x: 1, y: 1 })).toEqual(wall);
  });
});

describe('buildPlanGeometry with a bent wall', () => {
  it('grows the frame to include a vertex outside the stored box', () => {
    const plan = buildPlanGeometry(
      [room({ layoutX: 0, layoutY: 0, layoutWidth: 4, layoutHeight: 3 })],
      [
        item({
          kind: 12,
          x: 0,
          y: 0,
          width: 1,
          height: 1,
          points: '[{"x":0,"y":0},{"x":20,"y":12}]',
        }),
      ],
    );

    expect(plan).not.toBeNull();
    expect(plan!.bounds.maxX).toBeGreaterThan(20);
    expect(plan!.bounds.maxY).toBeGreaterThan(12);
  });
});
