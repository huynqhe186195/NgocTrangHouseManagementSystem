import { useMemo } from 'react';
import type { FloorLayoutItemDto, RoomDto } from '../../lib/api/types';
import { floorLayoutKindMeta } from '../../lib/api/types';
import { buildPlanGeometry } from '../../lib/floorGeometry';
import { statusStyles } from './FloorPlan';

interface FloorMiniPlanProps {
  rooms: RoomDto[];
  items?: FloorLayoutItemDto[];
  /** Height in CSS pixels; the width follows the plan's own aspect ratio. */
  height?: number;
}

/**
 * The row thumbnail on the building screen. It is drawn from exactly the
 * geometry the floor plan screen uses — the same `buildPlanGeometry`, the same
 * status palette — so what the table shows is the floor's real layout and not
 * an unrelated diagram of coloured squares.
 *
 * A floor whose rooms have no coordinates yet says so instead of drawing an
 * invented plan.
 */
export function FloorMiniPlan({ rooms, items = [], height = 54 }: FloorMiniPlanProps) {
  const plan = useMemo(() => buildPlanGeometry(rooms, items), [rooms, items]);

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

  return (
    <svg
      viewBox={`${bounds.minX} ${bounds.minY} ${bounds.width} ${bounds.height}`}
      height={height}
      preserveAspectRatio="xMidYMid meet"
      role="img"
      aria-label={`Sơ đồ mặt bằng thu nhỏ: ${plan.rooms.length} phòng`}
      className="rounded border border-outline-variant bg-white"
    >
      {plan.items.map((item) => {
        const meta = floorLayoutKindMeta[item.kind];
        return (
          <g key={item.id}>
            <rect
              x={item.x}
              y={item.y}
              width={item.width}
              height={item.height}
              fill={item.kind === 1 ? '#F1F5F9' : '#EEF2F7'}
              stroke="#cbd5e1"
              strokeWidth={Math.max(bounds.width, bounds.height) * 0.004}
            />
            {meta && (
              <title>{item.label ?? meta.label}</title>
            )}
          </g>
        );
      })}

      {plan.rooms.map((room) => {
        const style = statusStyles[room.status] ?? statusStyles[5]!;
        const strokeWidth = Math.max(bounds.width, bounds.height) * 0.005;
        return (
          <rect
            key={room.id}
            x={room.x}
            y={room.y}
            width={room.width}
            height={room.height}
            rx={0.06}
            fill={style.fill}
            stroke={style.stroke}
            strokeWidth={strokeWidth}
          >
            <title>{`${room.roomNumber} — ${style.label}`}</title>
          </rect>
        );
      })}
    </svg>
  );
}
