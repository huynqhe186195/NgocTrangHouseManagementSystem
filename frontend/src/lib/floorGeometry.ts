import type { FloorLayoutItemDto, RoomDto } from './api/types';

/**
 * Floor-plan geometry.
 *
 * Rooms store `layoutX/Y/Width/Height` in metres, with the origin at the
 * floor's top-left. Floor objects share the same coordinate space. A room
 * with no geometry at all predates the designer, so the caller falls back
 * to the schematic drawing rather than inventing a position.
 */

/** Padding in metres around the drawn plan, so labels near the edge fit. */
const PLAN_PADDING_METRES = 0.6;

export interface PlanBounds {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
  width: number;
  height: number;
}

export interface PlanRoom extends RoomDto {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface PlanItem extends FloorLayoutItemDto {}

export interface PlanPoint {
  x: number;
  y: number;
}

/**
 * Reads a stored `points` column. `minPoints` separates a wall centreline (two
 * points is a straight run) from a room polygon (three, or it encloses no area).
 * Anything malformed returns null so the caller falls back to the box drawing
 * rather than painting half a shape.
 */
export function parsePoints(raw: string | null | undefined, minPoints = 2): PlanPoint[] | null {
  if (!raw) {
    return null;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }
  if (!Array.isArray(parsed) || parsed.length < minPoints) {
    return null;
  }
  const points: PlanPoint[] = [];
  for (const candidate of parsed) {
    if (
      typeof candidate !== 'object' ||
      candidate === null ||
      typeof (candidate as { x?: unknown }).x !== 'number' ||
      typeof (candidate as { y?: unknown }).y !== 'number' ||
      !Number.isFinite((candidate as PlanPoint).x) ||
      !Number.isFinite((candidate as PlanPoint).y)
    ) {
      return null;
    }
    points.push({ x: (candidate as PlanPoint).x, y: (candidate as PlanPoint).y });
  }
  return points;
}

export function serializePoints(points: PlanPoint[]): string {
  return JSON.stringify(points.map((p) => ({ x: p.x, y: p.y })));
}

/** Axis-aligned extent of a polyline, so a bent wall is not clipped by its box. */
export function polylineBounds(points: PlanPoint[]): PlanBounds | null {
  if (points.length === 0) {
    return null;
  }
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const point of points) {
    minX = Math.min(minX, point.x);
    minY = Math.min(minY, point.y);
    maxX = Math.max(maxX, point.x);
    maxY = Math.max(maxY, point.y);
  }
  return { minX, minY, maxX, maxY, width: maxX - minX, height: maxY - minY };
}

/**
 * Distance from `p` to the segment `a→b`, plus how far along the segment the
 * closest point sits (0 at a, 1 at b). Used to drop a new bend where the owner
 * double-clicked rather than at the end of the wall.
 */
export function projectOnSegment(
  a: PlanPoint,
  b: PlanPoint,
  p: PlanPoint,
): { distance: number; t: number; point: PlanPoint } {
  const dx = b.x - a.x;
  const dy = b.y - a.y;
  const lengthSquared = dx * dx + dy * dy;
  if (lengthSquared === 0) {
    return { distance: Math.hypot(p.x - a.x, p.y - a.y), t: 0, point: { ...a } };
  }
  const raw = ((p.x - a.x) * dx + (p.y - a.y) * dy) / lengthSquared;
  const t = Math.min(1, Math.max(0, raw));
  const point = { x: a.x + dx * t, y: a.y + dy * t };
  return { distance: Math.hypot(p.x - point.x, p.y - point.y), t, point };
}

/**
 * Inserts a bend into the segment nearest to `target`, snapped and clamped. The
 * index of the new point is the segment's end index, so the caller can select
 * and drag it straight away. Returns null when the wall is too short to hold
 * another vertex without collapsing.
 */
export function insertPointOnPolyline(
  points: PlanPoint[],
  target: PlanPoint,
): { points: PlanPoint[]; index: number } | null {
  if (points.length < 2) {
    return null;
  }
  let best = { index: 1, distance: Infinity, point: target };
  for (let i = 1; i < points.length; i += 1) {
    const a = points[i - 1];
    const b = points[i];
    if (!a || !b) {
      continue;
    }
    const hit = projectOnSegment(a, b, target);
    if (hit.distance < best.distance) {
      best = { index: i, distance: hit.distance, point: hit.point };
    }
  }
  const snapped = {
    x: clampToLimits(snapToGrid(best.point.x)),
    y: clampToLimits(snapToGrid(best.point.y)),
  };
  // A vertex sitting on top of its neighbour would give the wall a
  // zero-length segment, which renders as a spike.
  for (const existing of points) {
    if (Math.hypot(existing.x - snapped.x, existing.y - snapped.y) < GRID_STEP_METRES) {
      return null;
    }
  }
  const next = [...points.slice(0, best.index), snapped, ...points.slice(best.index)];
  return next.length > MAX_POLYLINE_POINTS ? null : { points: next, index: best.index };
}

export function movePolyline(points: PlanPoint[], deltaX: number, deltaY: number): PlanPoint[] {
  return points.map((point) => ({
    x: clampToLimits(snapToGrid(point.x + deltaX)),
    y: clampToLimits(snapToGrid(point.y + deltaY)),
  }));
}

export function movePolylinePoint(
  points: PlanPoint[],
  index: number,
  point: PlanPoint,
): PlanPoint[] {
  return points.map((existing, i) =>
    i === index
      ? { x: clampToLimits(snapToGrid(point.x)), y: clampToLimits(snapToGrid(point.y)) }
      : existing,
  );
}

export function maxPolylinePointsReached(points: PlanPoint[]): boolean {
  return points.length >= MAX_POLYLINE_POINTS;
}

/**
 * The box a bent wall is stored with. Storage still has X/Y/Width/Height for
 * every object, so a wall keeps a box that covers its whole run plus half the
 * band on each side — that is what the plan's framing and the list views read.
 */
export function boxOfPolyline(points: PlanPoint[], thickness: number): Box {
  const extent = polylineBounds(points);
  if (!extent) {
    return { x: 0, y: 0, width: MIN_SIZE_METRES, height: MIN_SIZE_METRES };
  }
  const pad = Math.max(0, thickness) / 2;
  const width = Math.max(MIN_SIZE_METRES, extent.width + pad * 2);
  const height = Math.max(MIN_SIZE_METRES, extent.height + pad * 2);
  return {
    x: clampToLimits(snapToGrid(extent.minX - pad)),
    y: clampToLimits(snapToGrid(extent.minY - pad)),
    width,
    height,
  };
}

export interface PlanGeometry {
  bounds: PlanBounds;
  rooms: PlanRoom[];
  items: PlanItem[];
}

/** A room counts as placed only when all four coordinates are present and sane. */
export function hasGeometry(room: RoomDto): boolean {
  return (
    isFiniteNumber(room.layoutX) &&
    isFiniteNumber(room.layoutY) &&
    isFiniteNumber(room.layoutWidth) &&
    isFiniteNumber(room.layoutHeight) &&
    room.layoutWidth > 0 &&
    room.layoutHeight > 0
  );
}

function isFiniteNumber(value: number | null | undefined): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

function isSaneItem(item: FloorLayoutItemDto): boolean {
  return (
    Number.isFinite(item.x) &&
    Number.isFinite(item.y) &&
    Number.isFinite(item.width) &&
    Number.isFinite(item.height) &&
    item.width > 0 &&
    item.height > 0
  );
}

/**
 * Returns null when not a single room has geometry: drawing an empty plan
 * would look like a floor with no rooms rather than a floor not yet designed.
 */
export function buildPlanGeometry(
  rooms: RoomDto[],
  items: FloorLayoutItemDto[],
): PlanGeometry | null {
  const placed: PlanRoom[] = rooms.filter(hasGeometry).map((room) => ({
    ...room,
    x: room.layoutX as number,
    y: room.layoutY as number,
    width: room.layoutWidth as number,
    height: room.layoutHeight as number,
  }));

  if (placed.length === 0) {
    return null;
  }

  const usableItems = items.filter(isSaneItem);

  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;

  for (const box of [...placed, ...usableItems]) {
    minX = Math.min(minX, box.x);
    minY = Math.min(minY, box.y);
    maxX = Math.max(maxX, box.x + box.width);
    maxY = Math.max(maxY, box.y + box.height);
  }

  // A bent wall reaches past the box it was stored with, so the frame has to
  // grow with its centreline or the bend is cut off at the edge of the canvas.
  for (const item of usableItems) {
    const extent = polylineBounds(parsePoints(item.points) ?? []);
    if (!extent) {
      continue;
    }
    minX = Math.min(minX, extent.minX);
    minY = Math.min(minY, extent.minY);
    maxX = Math.max(maxX, extent.maxX);
    maxY = Math.max(maxY, extent.maxY);
  }

  minX -= PLAN_PADDING_METRES;
  minY -= PLAN_PADDING_METRES;
  maxX += PLAN_PADDING_METRES;
  maxY += PLAN_PADDING_METRES;

  return {
    bounds: { minX, minY, maxX, maxY, width: maxX - minX, height: maxY - minY },
    rooms: placed,
    items: usableItems,
  };
}

export const MIN_ZOOM = 0.5;
export const MAX_ZOOM = 3;
export const ZOOM_STEP = 0.25;

/** Clamp keeps the control honest when a caller passes a computed value. */
export function clampZoom(zoom: number): number {
  if (!Number.isFinite(zoom)) {
    return 1;
  }
  return Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, Math.round(zoom * 100) / 100));
}

export function zoomIn(zoom: number): number {
  return clampZoom(zoom + ZOOM_STEP);
}

export function zoomOut(zoom: number): number {
  return clampZoom(zoom - ZOOM_STEP);
}

/**
 * The SVG viewBox shrinks as zoom grows, so drawing one unit stays one metre
 * and only the visible window changes. `offset` pans the window without
 * touching the zoom.
 */
export function viewBoxRectFor(
  bounds: PlanBounds,
  zoom: number,
  offset: { x: number; y: number } = { x: 0, y: 0 },
): ViewBoxRect {
  const width = bounds.width / zoom;
  const height = bounds.height / zoom;
  const x = bounds.minX + (bounds.width - width) / 2 + offset.x;
  const y = bounds.minY + (bounds.height - height) / 2 + offset.y;
  return { x, y, width, height };
}

/**
 * Grows the viewBox to the frame's aspect ratio while keeping the plan inside
 * it. Without this a wide floor is letterboxed into a tall canvas: the grid
 * and the drawing end up as a strip in the middle with bare space above and
 * below.
 */
export function fitViewBoxToAspect(
  box: ViewBoxRect,
  aspectRatio: number,
): ViewBoxRect {
  if (!Number.isFinite(aspectRatio) || aspectRatio <= 0) {
    return box;
  }
  const current = box.width / box.height;
  if (Math.abs(current - aspectRatio) < 0.001) {
    return box;
  }
  if (current > aspectRatio) {
    const height = box.width / aspectRatio;
    return {
      x: box.x,
      y: box.y - (height - box.height) / 2,
      width: box.width,
      height,
    };
  }
  const width = box.height * aspectRatio;
  return {
    x: box.x - (width - box.width) / 2,
    y: box.y,
    width,
    height: box.height,
  };
}

export function viewBoxFor(
  bounds: PlanBounds,
  zoom: number,
  offset?: { x: number; y: number },
): string {
  const box = viewBoxRectFor(bounds, zoom, offset);
  return `${box.x} ${box.y} ${box.width} ${box.height}`;
}

/** `1` -> `100%`, `0.75` -> `75%`. */
export function formatZoom(zoom: number): string {
  return `${Math.round(zoom * 100)}%`;
}

/**
 * Rooms on a floor that has geometry but which the owner never placed are
 * still selectable from the side list, so the screen can say how many are
 * missing instead of silently shortening the plan.
 */
export function unplacedRooms(rooms: RoomDto[]): RoomDto[] {
  return rooms.filter((room) => !hasGeometry(room));
}

/* ------------------------------------------------------------------------ *
 * Designer maths.
 *
 * The designer works in metres throughout, exactly like the stored columns,
 * so nothing has to be converted when a drag ends and the value is saved.
 * ------------------------------------------------------------------------ */

export interface ViewBoxRect {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** The designer snaps to 10 cm; the server stores whatever it is given. */
export const GRID_STEP_METRES = 0.1;

/** Mirrors the server's cap so the designer cannot build a wall it can't save. */
export const MAX_POLYLINE_POINTS = 64;

/** Smallest box a room or object may be shrunk to, in metres. */
export const MIN_SIZE_METRES = 0.5;

/** The server rejects coordinates outside ±10000 m; stay well inside. */
const MAX_COORDINATE_METRES = 10000;

export interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

export type ResizeHandle = 'nw' | 'ne' | 'se' | 'sw';

/**
 * Rounds to the nearest 10 cm. `toFixed` is applied after the rounding so a
 * value that is already on the grid (2.3) does not come back as
 * 2.3000000000000003 and fail an equality check.
 */
export function snapToGrid(value: number, step = GRID_STEP_METRES): number {
  if (!Number.isFinite(value)) {
    return 0;
  }
  return Number((Math.round(value / step) * step).toFixed(6));
}

export function clampToLimits(value: number): number {
  if (!Number.isFinite(value)) {
    return 0;
  }
  return Math.min(MAX_COORDINATE_METRES, Math.max(-MAX_COORDINATE_METRES, value));
}

/**
 * Turns a pointer position into metres. `svgPoint` should come from
 * `getScreenCTM().inverse()` applied to the client point, so it is already in
 * the drawing's own coordinate space — no zoom or pan maths is needed here,
 * and the result stays correct if the viewBox moves mid-drag.
 */
export function pointToMetres(svgPoint: { x: number; y: number }): { x: number; y: number } {
  return {
    x: Number.isFinite(svgPoint.x) ? svgPoint.x : 0,
    y: Number.isFinite(svgPoint.y) ? svgPoint.y : 0,
  };
}

/** Moves a box by a snapped delta, keeping its size and staying in range. */
export function moveBox(box: Box, deltaX: number, deltaY: number): Box {
  const snappedX = clampToLimits(snapToGrid(box.x + deltaX));
  const snappedY = clampToLimits(snapToGrid(box.y + deltaY));
  return { ...box, x: snappedX, y: snappedY };
}

/**
 * Resizes from one corner: the opposite corner stays put, so the box grows or
 * shrinks away from the handle the owner is dragging. Sizes never fall below
 * `MIN_SIZE_METRES`, which keeps a stray click from collapsing a room to a
 * sliver the server would still accept.
 */
export function resizeBox(box: Box, handle: ResizeHandle, point: { x: number; y: number }): Box {
  const right = box.x + box.width;
  const bottom = box.y + box.height;

  const px = clampToLimits(snapToGrid(point.x));
  const py = clampToLimits(snapToGrid(point.y));

  let x = box.x;
  let y = box.y;
  let width = box.width;
  let height = box.height;

  if (handle === 'nw' || handle === 'sw') {
    x = Math.min(px, right - MIN_SIZE_METRES);
    width = right - x;
  } else {
    width = Math.max(MIN_SIZE_METRES, px - box.x);
  }

  if (handle === 'nw' || handle === 'ne') {
    y = Math.min(py, bottom - MIN_SIZE_METRES);
    height = bottom - y;
  } else {
    height = Math.max(MIN_SIZE_METRES, py - box.y);
  }

  return { x, y, width, height };
}

/** Keeps a freshly dropped object inside the drawing instead of off-canvas. */
export function clampBoxToBounds(box: Box, bounds: PlanBounds): Box {
  const width = Math.max(MIN_SIZE_METRES, Math.min(box.width, bounds.width));
  const height = Math.max(MIN_SIZE_METRES, Math.min(box.height, bounds.height));
  return {
    width,
    height,
    x: clampToLimits(Math.min(Math.max(box.x, bounds.minX), bounds.maxX - width)),
    y: clampToLimits(Math.min(Math.max(box.y, bounds.minY), bounds.maxY - height)),
  };
}

/** A default box for a room that has never been placed, dropped in the middle. */
export function defaultRoomBox(bounds: PlanBounds, width = 4, height = 3.5): Box {
  return clampBoxToBounds(
    {
      width,
      height,
      x: bounds.minX + (bounds.width - width) / 2,
      y: bounds.minY + (bounds.height - height) / 2,
    },
    bounds,
  );
}

export function boxesEqual(a: Box, b: Box): boolean {
  return a.x === b.x && a.y === b.y && a.width === b.width && a.height === b.height;
}

/** `2.3` -> `2,3 m`, since the plan is read in Vietnamese. */
export function formatMetres(value: number): string {
  if (!Number.isFinite(value)) {
    return '—';
  }
  return `${value.toLocaleString('vi-VN', { maximumFractionDigits: 2 })} m`;
}
