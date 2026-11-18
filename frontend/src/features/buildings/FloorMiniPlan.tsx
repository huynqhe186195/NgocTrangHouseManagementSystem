import { useMemo } from 'react';
import type { FloorLayoutItemDto, RoomDto } from '../../lib/api/types';
import { floorLayoutKindMeta, isFurniture } from '../../lib/api/types';
import { buildPlanGeometry, parsePoints, unplacedRooms } from '../../lib/floorGeometry';
import {
  LayoutObjectArt,
  WALL_COLOR,
  innerBox,
  statusStyles,
} from './planDrawing';

interface FloorMiniPlanProps {
  rooms: RoomDto[];
  items?: FloorLayoutItemDto[];
  /** Height in CSS pixels; the width follows the plan's own aspect ratio. */
  height?: number;
  /** Longest side in CSS pixels, so a deep plan cannot tower over its row. */
  maxWidth?: number;
}
/**
 * The row thumbnail on the building screen. It is drawn from exactly the
 * geometry the floor plan screen uses — the same `buildPlanGeometry`, the same
 * status palette, the same wall ring — so what the table shows is the floor's
 * real layout at small size, not an unrelated diagram of coloured squares.
 *
 * A floor whose rooms have no coordinates yet says so instead of drawing an
 * invented plan.
 */
export function FloorMiniPlan({ rooms, items = [], height = 55, maxWidth = 168 }: FloorMiniPlanProps) {
  const plan = useMemo(() => buildPlanGeometry(rooms, items), [rooms, items]);
  const unplaced = useMemo(() => unplacedRooms(rooms), [rooms]);

  if (rooms.length === 0) {
    return <span className="text-xs text-outline">Chưa có phòng</span>;
  }

  if (!plan) {
    return (
      <span
        className="inline-flex items-center gap-1 text-[11px] text-on-surface-variant"
        title="Các phòng của tầng chưa được đặt toạ độ trên sơ đồ."
      >
        <span className="material-symbols-outlined text-sm text-outline">grid_off</span>
        Chưa có sơ đồ
      </span>
    );
  }

  const { bounds } = plan;
  // Drawn as a share of the whole floor rather than of one room: at 60 px the
  // true 0.27 m band would be under a pixel and the plan would read as coloured
  // tiles again, which is exactly what this thumbnail used to look like.
  const detail = Math.max(bounds.width, bounds.height);
  const wall = detail * 0.012;
  const hairline = detail * 0.003;

  return (
    <figure className="m-0 inline-flex flex-col items-start gap-1">
      <svg
        viewBox={`${bounds.minX} ${bounds.minY} ${bounds.width} ${bounds.height}`}
        height={height}
        style={{ maxWidth }}
        preserveAspectRatio="xMidYMid meet"
        role="img"
        aria-label={
          unplaced.length > 0
            ? `Sơ đồ mặt bằng thu nhỏ: ${plan.rooms.length} trên ${rooms.length} phòng có toạ độ`
            : `Sơ đồ mặt bằng thu nhỏ: ${plan.rooms.length} phòng`
        }
        className="rounded border border-outline-variant bg-white"
      >
        {/* Floor slab behind everything, so the corridor reads as a light
            surface rather than as white space between two rows of rooms. */}
        <rect
          x={bounds.minX}
          y={bounds.minY}
          width={bounds.width}
          height={bounds.height}
          fill="#FBFDFF"
        />

        {/* Walls first, rooms painted over them: shared partitions then read as
            one band and the storey outline is whatever wall the owner placed. */}
        {plan.rooms.map((room) => (
          <rect
            key={`wall-${room.id}`}
            x={room.x}
            y={room.y}
            width={room.width}
            height={room.height}
            fill={WALL_COLOR}
          />
        ))}

        {plan.items
          .filter((item) => !isFurniture(item.kind) && item.kind !== 12)
          .map((item) => {
            const meta = floorLayoutKindMeta[item.kind];
            return (
              <g key={item.id}>
                <LayoutObjectArt
                  box={item}
                  kind={item.kind}
                  wall={Math.max(wall * 0.6, hairline)}
                />
                {meta && <title>{item.label ?? meta.label}</title>}
              </g>
            );
          })}

        {plan.rooms.map((room) => {
          const style = statusStyles[room.status] ?? statusStyles[5]!;
          const floor = innerBox(room, wall);
          return (
            <g key={room.id}>
              <rect
                x={floor.x}
                y={floor.y}
                width={floor.width}
                height={floor.height}
                fill={style.fill}
              />
              <title>{`${room.roomNumber} — ${style.label}`}</title>
            </g>
          );
        })}

        {/* Furniture and walls, painted over the rooms for the same reason the
            full plan paints them last. */}
        {plan.items
          .filter((item) => isFurniture(item.kind) || item.kind === 12)
          .map((item) => (
            <LayoutObjectArt
              key={item.id}
              box={item}
              kind={item.kind}
              wall={Math.max(wall * 0.6, hairline)}
              points={parsePoints(item.points)}
              thickness={
                item.kind === 12
                  ? (item.thickness ?? Math.max(wall * 0.6, hairline))
                  : item.thickness
              }
              cornerRadius={item.cornerRadius}
            />
          ))}
      </svg>

      {/* A storey can be half-designed: some rooms have coordinates and some do
          not. Drawing only the placed ones silently understates the floor, so
          the count is stated instead — at 162 px there is no room for a note
          inside the frame. */}
      {unplaced.length > 0 && (
        <figcaption
          className="text-[10px] leading-tight text-on-surface-variant"
          title={`${unplaced.length} phòng chưa được đặt toạ độ trên sơ đồ: ${unplaced
            .map((room) => room.roomNumber)
            .join(', ')}`}
        >
          {plan.rooms.length}/{rooms.length} phòng có toạ độ
        </figcaption>
      )}
    </figure>
  );
}
