import { useMemo, useState } from 'react';
import type { FloorLayoutItemDto, RoomDto } from '../../lib/api/types';
import { floorLayoutKindMeta, isFurniture } from '../../lib/api/types';
import {
  MAX_ZOOM,
  MIN_ZOOM,
  buildPlanGeometry,
  formatZoom,
  hasGeometry,
  parsePoints,
  unplacedRooms,
  viewBoxRectFor,
  zoomIn,
  zoomOut,
  type PlanRoom,
} from '../../lib/floorGeometry';
import {
  LayoutObjectArt,
  LINE_COLOR,
  PlanCompass,
  PlanDimensions,
  PlanRulers,
  SELECTED_STROKE,
  WALL_COLOR,
  doorArcPath,
  doorWidth,
  objectLabel,
  shouldCaption,
  statusStyles,
  tickStep,
  wallThickness,
} from './planDrawing';

interface FloorPlanProps {
  rooms: RoomDto[];
  items?: FloorLayoutItemDto[];
  selectedId: string | null;
  onSelect: (roomId: string) => void;
  /** Hides the zoom controls and legend, for the small locator plan. */
  compact?: boolean;
  /** Stretches the drawing over the whole screen while in fullscreen. */
  fill?: boolean;
}

/** Shared with the building screen's miniature, so both read the same colours. */
export { statusStyles } from './planDrawing';

/**
 * Draws the floor plan from the geometry the designer saved. When no room on
 * the floor carries coordinates yet, it falls back to the area-proportional
 * schematic the screen shipped with, and says which one is on screen.
 */
export function FloorPlan({
  rooms,
  items = [],
  selectedId,
  onSelect,
  compact = false,
  fill = false,
}: FloorPlanProps) {
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
  // Partitions are what each room strokes for itself. The storey's outer wall
  // is a Wall object the owner places on the plan, so it is drawn with the
  // other objects rather than generated from the room boxes.
  const viewBox = viewBoxRectFor(bounds, zoom);
  // The rules and the compass are drawn outside the plan's own bounds, so the
  // view widens to make room for them.
  const margin = Math.max(1.1, unit * 0.09);
  const framed = {
    x: viewBox.x - margin,
    y: viewBox.y - margin,
    width: viewBox.width + margin * 2,
    height: viewBox.height + margin * 2,
  };

  return (
    <div className={fill ? 'flex min-h-0 flex-1 flex-col' : undefined}>
      {!compact && (
        <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
          <p className="text-xs text-on-surface-variant">
            Sơ đồ vẽ theo toạ độ thật (mét) · {plan.rooms.length} phòng đã đặt vị trí
            {missing.length > 0 && ` · ${missing.length} phòng chưa có toạ độ`}
          </p>
          <ZoomControls zoom={zoom} onChange={setZoom} onReset={resetZoom} />
        </div>
      )}

      <div
        className={
          'overflow-hidden rounded-lg border border-outline-variant bg-white' +
          (fill ? ' min-h-0 flex-1' : '')
        }
      >
        <svg
          viewBox={`${framed.x} ${framed.y} ${framed.width} ${framed.height}`}
          className={fill ? 'h-full w-full' : 'h-auto w-full'}
          style={fill ? undefined : { aspectRatio: `${framed.width} / ${framed.height}` }}
          preserveAspectRatio={fill ? 'xMidYMid meet' : undefined}
          role="group"
          aria-label="Sơ đồ mặt bằng tầng"
        >
          <defs>
            <pattern id="plan-grid" width="1" height="1" patternUnits="userSpaceOnUse">
              <path d="M 1 0 L 0 0 0 1" fill="none" stroke="#eef2f7" strokeWidth="0.02" />
            </pattern>
          </defs>

          <rect
            x={framed.x}
            y={framed.y}
            width={framed.width}
            height={framed.height}
            fill="url(#plan-grid)"
          />

          <PlanRulers viewBox={framed} step={tickStep(bounds, zoom)} />

          {/* Architecture sits under the rooms: a corridor or a stairwell is
              floor, not something laid on top of a room. */}
          {plan.items
            .filter((floorItem) => floorItem.kind !== 12 && !isFurniture(floorItem.kind))
            .map((floorItem) => (
              <LayoutItemShape key={floorItem.id} item={floorItem} />
            ))}

          {plan.rooms.map((room) => (
            <RoomShape
              key={room.id}
              room={room}
              selected={room.id === selectedId}
              onSelect={onSelect}
            />
          ))}

          {/* Furniture paints over its room's floor — it stands inside the
              room, so drawing it under the room would hide every piece. */}
          {plan.items
            .filter((floorItem) => isFurniture(floorItem.kind))
            .map((floorItem) => (
              <LayoutItemShape key={floorItem.id} item={floorItem} />
            ))}

          {/* The room's number and area go on last, above the furniture. */}
          {plan.rooms.map((room) => (
            <RoomLabel key={`label-${room.id}`} room={room} titleSize={titleSize} metaSize={metaSize} />
          ))}

          {/* Walls the owner placed paint last, so a wall laid along a room
              edge stays visible instead of being covered by the room. */}
          {plan.items
            .filter((floorItem) => floorItem.kind === 12)
            .map((floorItem) => (
              <LayoutItemShape key={floorItem.id} item={floorItem} />
            ))}

          <PlanDimensions bounds={bounds} inset={margin * 0.62} />
          <PlanCompass viewBox={framed} size={margin * 0.85} />
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
}: {
  room: PlanRoom;
  selected: boolean;
  onSelect: (roomId: string) => void;
}) {
  const style = statusStyles[room.status] ?? statusStyles[5]!;
  const rotation = room.layoutRotationDegrees ?? 0;
  const centreX = room.x + room.width / 2;
  const centreY = room.y + room.height / 2;
  const polygon = parsePolygon(room.layoutPoints);
  const wall = wallThickness(room);
  const door = doorWidth(room);

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
      // Chrome paints its default focus ring on an SVG <g> as a black disc
      // centred on the box, which covered the room the owner had just clicked.
      // The group takes no outline; the shapes inside carry the focus colour.
      style={{ cursor: 'pointer', outline: 'none' }}
      transform={rotation ? `rotate(${rotation} ${centreX} ${centreY})` : undefined}
    >
      {polygon ? (
        <>
          <polygon
            points={polygon
              .map((point) => `${room.x + point.x},${room.y + point.y}`)
              .join(' ')}
            fill={style.fill}
          />
          <polygon
            points={polygon
              .map((point) => `${room.x + point.x},${room.y + point.y}`)
              .join(' ')}
            fill="none"
            stroke={selected ? SELECTED_STROKE : WALL_COLOR}
            strokeWidth={selected ? wall * 1.6 : wall}
            strokeLinejoin="miter"
          />
        </>
      ) : (
        <>
          <rect
            x={room.x}
            y={room.y}
            width={room.width}
            height={room.height}
            fill={style.fill}
          />
          {/* One stroke per edge. Neighbours that meet wall-to-wall put theirs
              on the same line, so a shared partition stays one wall thick
              instead of doubling up. */}
          <rect
            x={room.x}
            y={room.y}
            width={room.width}
            height={room.height}
            fill="none"
            stroke={selected ? SELECTED_STROKE : WALL_COLOR}
            strokeWidth={selected ? wall * 1.15 : wall}
            strokeLinejoin="round"
          />
          {/* The opening is cut back out of the bottom wall, then the leaf is
              swept from its hinge. */}
          <rect
            x={room.x + (room.width - door) / 2}
            y={room.y + room.height - wall}
            width={door}
            height={wall}
            fill="#ffffff"
          />
          <path
            d={doorArcPath(room, wall, door)}
            fill="none"
            stroke={selected ? SELECTED_STROKE : LINE_COLOR}
            strokeWidth={Math.max(0.02, wall * 0.2)}
          />
        </>
      )}

      {/* The room's name is a separate pass, painted after the furniture: a bed
          or a desk laid against the wall would otherwise cover it. */}
    </g>
  );
}

/** The room number and its area, drawn over the room's furniture. */
function RoomLabel({
  room,
  titleSize,
  metaSize,
}: {
  room: PlanRoom;
  titleSize: number;
  metaSize: number;
}) {
  const centreX = room.x + room.width / 2;
  const centreY = room.y + room.height / 2;
  const rotation = room.layoutRotationDegrees ?? 0;
  const sub = room.area ? `${room.area} m²` : null;

  return (
    <g
      pointerEvents="none"
      transform={
        rotation
          ? `rotate(${rotation} ${centreX} ${centreY})`
          : undefined
      }
    >
      {/* A white halo keeps the text legible where it crosses a piece of
          furniture, the way a drawing labels a room over the plan beneath. */}
      <text
        x={centreX}
        y={sub ? centreY - metaSize * 0.15 : centreY + titleSize * 0.35}
        textAnchor="middle"
        fontSize={titleSize}
        fontWeight={700}
        fill="#0b1c30"
        stroke="#ffffff"
        strokeWidth={titleSize * 0.16}
        paintOrder="stroke"
        strokeLinejoin="round"
        fontFamily="Plus Jakarta Sans, sans-serif"
      >
        {room.roomNumber}
      </text>
      {sub && (
        <text
          x={centreX}
          y={centreY + titleSize * 0.85}
          textAnchor="middle"
          fontSize={metaSize}
          fill="#475569"
          stroke="#ffffff"
          strokeWidth={metaSize * 0.16}
          paintOrder="stroke"
          strokeLinejoin="round"
          fontFamily="Inter, sans-serif"
        >
          {sub}
        </text>
      )}
    </g>
  );
}

function LayoutItemShape({ item }: { item: FloorLayoutItemDto }) {
  const meta = floorLayoutKindMeta[item.kind];
  const centreX = item.x + item.width / 2;
  const text = item.label ?? meta?.label ?? '';
  const points = parsePoints(item.points);
  // The mockup names the architecture — "Hành lang chung", "Cầu thang" — but
  // leaves the furniture unlabelled: a bed drawn on a plan does not need the
  // word "Giường" across it, and printing one covered the drawing at this size.
  // A bent wall is unlabelled for a different reason, which shouldCaption
  // explains.
  const captioned = shouldCaption(points, item.kind) ? objectLabel(item, text) : null;

  return (
    <g pointerEvents="none">
      <LayoutObjectArt
        box={item}
        kind={item.kind}
        caption={captioned?.caption}
        points={points}
        thickness={item.thickness}
        cornerRadius={item.cornerRadius}
      />
      {captioned && (
        <text
          x={centreX}
          y={captioned.baselineY}
          textAnchor="middle"
          fontSize={captioned.fontSize}
          textLength={captioned.textLength}
          lengthAdjust={captioned.textLength === undefined ? undefined : 'spacingAndGlyphs'}
          fontWeight={600}
          fill="#475569"
          fontFamily="Inter, sans-serif"
        >
          {text}
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
