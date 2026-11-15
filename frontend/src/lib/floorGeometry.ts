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
 * and only the visible window changes.
 */
export function viewBoxFor(bounds: PlanBounds, zoom: number): string {
  const width = bounds.width / zoom;
  const height = bounds.height / zoom;
  const x = bounds.minX + (bounds.width - width) / 2;
  const y = bounds.minY + (bounds.height - height) / 2;
  return `${x} ${y} ${width} ${height}`;
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
