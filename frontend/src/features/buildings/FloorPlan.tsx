import { useMemo, useState } from 'react';
import type { FloorLayoutItemDto, RoomDto } from '../../lib/api/types';
import { floorLayoutKindMeta } from '../../lib/api/types';
import {
  MAX_ZOOM,
  MIN_ZOOM,
  buildPlanGeometry,
  formatZoom,
  hasGeometry,
  unplacedRooms,
  viewBoxFor,
  zoomIn,
  zoomOut,
  type PlanRoom,
} from '../../lib/floorGeometry';

interface FloorPlanProps {
  rooms: RoomDto[];
  items?: FloorLayoutItemDto[];
  selectedId: string | null;
  onSelect: (roomId: string) => void;
  /** Hides the zoom controls and legend, for the small locator plan. */
  compact?: boolean;
}

const statusStyles: Record<number, { fill: string; stroke: string; label: string }> = {
  1: { fill: '#E9F7E7', stroke: '#9BD69B', label: 'Trống' },
  2: { fill: '#FDE2E1', stroke: '#F2A9A6', label: 'Đang thuê' },
  3: { fill: '#FDF0CC', stroke: '#EBCB7A', label: 'Đã đặt' },
  4: { fill: '#E8EEF5', stroke: '#B3C4D8', label: 'Bảo trì' },
  5: { fill: '#F1F5F9', stroke: '#CBD5E1', label: 'Ngừng dùng' },
};

const SELECTED_STROKE = '#1d4ed8';

/**
 * Draws the floor plan from the geometry the designer saved. When no room on
 * the floor carries coordinates yet, it falls back to the area-proportional
 * schematic the screen shipped with, and says which one is on screen.
 */
export function FloorPlan({ rooms, items = [], selectedId, onSelect, compact = false }: FloorPlanProps) {
  const [zoom, setZoom] = useState(1);

  function resetZoom() {
    setZoom(1);
  }

  const plan = useMemo(() => buildPlanGeometry(rooms, items), [rooms, items]);
  const missing = useMemo(() => unplacedRooms(rooms), [rooms]);

  if (rooms.length === 0) {
    return (
      <div className="flex h-64 items-center justify-center rounded-lg border border-dashed border-outline-variant bg-surface-lowest text-sm text-on-surface-variant">
        Tầng này chưa có phòng nào để hiển thị.
      </div>
    );
  }

  if (!plan) {
    return <SchematicPlan rooms={rooms} selectedId={selectedId} onSelect={onSelect} />;
  }

  const { bounds } = plan;
  const unit = Math.min(bounds.width, bounds.height);
  const titleSize = Math.max(0.28, unit * 0.028);
  const metaSize = Math.max(0.22, unit * 0.021);

  return (
    <div>
      {!compact && (
        <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
          <p className="text-xs text-on-surface-variant">
            Sơ đồ vẽ theo toạ độ thật (mét) · {plan.rooms.length} phòng đã đặt vị trí
            {missing.length > 0 && ` · ${missing.length} phòng chưa có toạ độ`}
          </p>
          <ZoomControls zoom={zoom} onChange={setZoom} onReset={resetZoom} />
        </div>
      )}

      <div className="overflow-hidden rounded-lg border border-outline-variant bg-white">
        <svg
          viewBox={viewBoxFor(bounds, zoom)}
          className="h-auto w-full"
          style={{ aspectRatio: `${bounds.width} / ${bounds.height}` }}
          role="group"
          aria-label="Sơ đồ mặt bằng tầng"
        >
          <defs>
            <pattern id="plan-grid" width="1" height="1" patternUnits="userSpaceOnUse">
              <path d="M 1 0 L 0 0 0 1" fill="none" stroke="#eef2f7" strokeWidth="0.02" />
            </pattern>
          </defs>

          <rect
            x={bounds.minX}
            y={bounds.minY}
            width={bounds.width}
            height={bounds.height}
            fill="url(#plan-grid)"
          />

          {plan.items.map((floorItem) => (
            <LayoutItemShape
              key={floorItem.id}
              item={floorItem}
              labelSize={metaSize * 0.9}
            />
          ))}

          {plan.rooms.map((room) => (
            <RoomShape
              key={room.id}
              room={room}
              selected={room.id === selectedId}
              onSelect={onSelect}
              titleSize={titleSize}
              metaSize={metaSize}
            />
          ))}
        </svg>
      </div>

      {!compact && (
        <div className="mt-3 flex flex-wrap gap-3 border-t border-outline-variant pt-3 text-[11px] text-on-surface-variant">
          {[1, 2, 3, 4, 5].map((status) => (
            <span key={status} className="flex items-center gap-1.5">
              <span
                className="h-3 w-3 rounded-[2px] border"
                style={{
                  backgroundColor: statusStyles[status]!.fill,
                  borderColor: statusStyles[status]!.stroke,
                }}
              />
              {statusStyles[status]!.label}
            </span>
          ))}
        </div>
      )}
    </div>
  );
}

function ZoomControls({
  zoom,
  onChange,
  onReset,
}: {
  zoom: number;
  onChange: (zoom: number) => void;
  onReset: () => void;
}) {
  return (
    <div className="flex items-center gap-1 rounded-lg border border-outline-variant bg-surface-lowest p-0.5">
      <button
        type="button"
        onClick={() => onChange(zoomOut(zoom))}
        aria-label="Thu nhỏ sơ đồ"
        className="flex h-7 w-7 items-center justify-center rounded-md text-on-surface-variant transition hover:bg-surface-low disabled:opacity-40"
        disabled={zoom <= MIN_ZOOM}
      >
        <span className="material-symbols-outlined text-base">remove</span>
      </button>
      <span className="tnum min-w-[46px] text-center text-xs font-semibold text-on-surface">
        {formatZoom(zoom)}
      </span>
      <button
        type="button"
        onClick={() => onChange(zoomIn(zoom))}
        aria-label="Phóng to sơ đồ"
        className="flex h-7 w-7 items-center justify-center rounded-md text-on-surface-variant transition hover:bg-surface-low disabled:opacity-40"
        disabled={zoom >= MAX_ZOOM}
      >
        <span className="material-symbols-outlined text-base">add</span>
      </button>
      <button
        type="button"
        onClick={onReset}
        aria-label="Đặt lại tỷ lệ"
        title="Đặt lại tỷ lệ"
        className="ml-0.5 flex h-7 w-7 items-center justify-center rounded-md text-on-surface-variant transition hover:bg-surface-low"
      >
        <span className="material-symbols-outlined text-base">fit_screen</span>
      </button>
    </div>
  );
}

function RoomShape({
  room,
  selected,
  onSelect,
  titleSize,
  metaSize,
}: {
  room: PlanRoom;
  selected: boolean;
  onSelect: (roomId: string) => void;
  titleSize: number;
  metaSize: number;
}) {
  const style = statusStyles[room.status] ?? statusStyles[5]!;
  const rotation = room.layoutRotationDegrees ?? 0;
  const centreX = room.x + room.width / 2;
  const centreY = room.y + room.height / 2;
  const polygon = parsePolygon(room.layoutPoints);

  const label = room.roomNumber;
  const sub = room.area ? `${room.area} m²` : null;

  return (
    <g
      onClick={() => onSelect(room.id)}
      role="button"
      tabIndex={0}
      aria-label={`Phòng ${room.roomNumber} — ${style.label}`}
      onKeyDown={(event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault();
          onSelect(room.id);
        }
      }}
      style={{ cursor: 'pointer' }}
      transform={rotation ? `rotate(${rotation} ${centreX} ${centreY})` : undefined}
    >
      {polygon ? (
        <polygon
          points={polygon
            .map((point) => `${room.x + point.x},${room.y + point.y}`)
            .join(' ')}
          fill={style.fill}
          stroke={selected ? SELECTED_STROKE : style.stroke}
          strokeWidth={selected ? 0.1 : 0.05}
        />
      ) : (
        <rect
          x={room.x}
          y={room.y}
          width={room.width}
          height={room.height}
          rx={0.08}
          fill={style.fill}
          stroke={selected ? SELECTED_STROKE : style.stroke}
          strokeWidth={selected ? 0.1 : 0.05}
        />
      )}

      <text
        x={centreX}
        y={sub ? centreY - metaSize * 0.1 : centreY + titleSize * 0.35}
        textAnchor="middle"
        fontSize={titleSize}
        fontWeight={700}
        fill="#0b1c30"
        fontFamily="Plus Jakarta Sans, sans-serif"
        pointerEvents="none"
      >
        {label}
      </text>
      {sub && (
        <text
          x={centreX}
          y={centreY + titleSize * 0.9}
          textAnchor="middle"
          fontSize={metaSize}
          fill="#475569"
          fontFamily="Inter, sans-serif"
          pointerEvents="none"
        >
          {sub}
        </text>
      )}
    </g>
  );
}

function LayoutItemShape({
  item,
  labelSize,
}: {
  item: FloorLayoutItemDto;
  labelSize: number;
}) {
  const meta = floorLayoutKindMeta[item.kind];
  const isCorridor = item.kind === 1;
  const fill = isCorridor ? '#F1F5F9' : '#EEF2F7';
  const centreX = item.x + item.width / 2;
  const centreY = item.y + item.height / 2;

  return (
    <g>
      <rect
        x={item.x}
        y={item.y}
        width={item.width}
        height={item.height}
        rx={0.06}
        fill={fill}
        stroke="#cbd5e1"
        strokeWidth={0.04}
      />
      {(item.label || meta) && (
        <text
          x={centreX}
          y={centreY + labelSize * 0.35}
          textAnchor="middle"
          fontSize={labelSize}
          fill="#64748b"
          fontFamily="Inter, sans-serif"
          pointerEvents="none"
        >
          {item.label ?? meta?.label ?? ''}
        </text>
      )}
    </g>
  );
}

function parsePolygon(raw: string | null): { x: number; y: number }[] | null {
  if (!raw) {
    return null;
  }
  try {
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed) || parsed.length < 3) {
      return null;
    }
    const points: { x: number; y: number }[] = [];
    for (const candidate of parsed) {
      if (
        typeof candidate === 'object' &&
        candidate !== null &&
        typeof (candidate as { x?: unknown }).x === 'number' &&
        typeof (candidate as { y?: unknown }).y === 'number'
      ) {
        points.push({
          x: (candidate as { x: number }).x,
          y: (candidate as { y: number }).y,
        });
      } else {
        return null;
      }
    }
    return points;
  } catch {
    return null;
  }
}

/**
 * The pre-designer drawing: each room becomes a block sized by its area,
 * laid out on two sides of a central corridor. Honest about being a
 * schematic, since no coordinates exist to draw from.
 */
function SchematicPlan({
  rooms,
  selectedId,
  onSelect,
}: {
  rooms: RoomDto[];
  selectedId: string | null;
  onSelect: (roomId: string) => void;
}) {
  const half = Math.ceil(rooms.length / 2);
  const top = rooms.slice(0, half);
  const bottom = rooms.slice(half);
  const totalArea = rooms.reduce((sum, room) => sum + (room.area || 1), 0);
  const placed = rooms.filter(hasGeometry).length;

  return (
    <div className="rounded-lg border border-outline-variant bg-surface-lowest p-4">
      <div className="mb-2 text-xs text-on-surface-variant">
        {placed > 0
          ? `Sơ đồ minh họa theo diện tích (chỉ ${placed}/${rooms.length} phòng có toạ độ) — mở màn thiết kế để vẽ sơ đồ thật.`
          : `Sơ đồ minh họa theo diện tích thật từng phòng (tổng ${Math.round(totalArea)} m²). Chưa phòng nào có toạ độ trên sơ đồ.`}
      </div>

      <div className="overflow-x-auto">
        <div className="flex min-w-[560px] flex-col gap-0">
          <div className="flex gap-1">
            {top.map((room) => (
              <RoomBlock
                key={room.id}
                room={room}
                totalArea={totalArea}
                selected={room.id === selectedId}
                onSelect={onSelect}
              />
            ))}
          </div>

          <div className="my-1 flex h-8 items-center justify-center rounded bg-surface-low text-[11px] font-medium tracking-wide text-on-surface-variant">
            Hành lang chung
          </div>

          <div className="flex gap-1">
            {bottom.map((room) => (
              <RoomBlock
                key={room.id}
                room={room}
                totalArea={totalArea}
                selected={room.id === selectedId}
                onSelect={onSelect}
              />
            ))}
          </div>
        </div>
      </div>

      <div className="mt-3 flex flex-wrap gap-3 border-t border-outline-variant pt-3 text-[11px] text-on-surface-variant">
        {[1, 2, 3, 4, 5].map((status) => (
          <span key={status} className="flex items-center gap-1.5">
            <span
              className="h-3 w-3 rounded-[2px] border"
              style={{
                backgroundColor: statusStyles[status]!.fill,
                borderColor: statusStyles[status]!.stroke,
              }}
            />
            {statusStyles[status]!.label}
          </span>
        ))}
      </div>
    </div>
  );
}

function RoomBlock({
  room,
  totalArea,
  selected,
  onSelect,
}: {
  room: RoomDto;
  totalArea: number;
  selected: boolean;
  onSelect: (roomId: string) => void;
}) {
  const style = statusStyles[room.status] ?? statusStyles[5]!;
  const flexGrow = Math.max(1, room.area || 1);
  const share = Math.round(((room.area || 1) / totalArea) * 100);

  return (
    <button
      type="button"
      onClick={() => onSelect(room.id)}
      title={`${room.roomNumber} — ${room.area ?? '?'} m² — ${style.label}`}
      style={{
        flexGrow,
        flexBasis: 0,
        backgroundColor: style.fill,
        borderColor: selected ? SELECTED_STROKE : style.stroke,
        boxShadow: selected ? '0 0 0 2px rgba(29,78,216,0.35)' : undefined,
      }}
      className="flex min-w-[76px] flex-col items-start justify-center rounded-md border-2 px-2 py-3 text-left transition hover:brightness-[0.98]"
    >
      <span className="font-display text-sm font-bold text-on-surface">{room.roomNumber}</span>
      <span className="text-[11px] leading-tight text-on-surface-variant">
        {room.area ? `${room.area} m²` : '—'}
      </span>
      <span className="mt-0.5 text-[10px] leading-tight text-on-surface-variant/80">
        {share}% tầng
      </span>
    </button>
  );
}
