import { useCallback, useMemo, useRef, useState } from 'react';
import type { FloorLayoutItemDto } from '../../lib/api/types';
import { floorLayoutKindMeta } from '../../lib/api/types';
import {
  MAX_ZOOM,
  MIN_ZOOM,
  MIN_SIZE_METRES,
  clampBoxToBounds,
  clampZoom,
  formatMetres,
  formatZoom,
  moveBox,
  pointToMetres,
  resizeBox,
  viewBoxRectFor,
  zoomIn,
  zoomOut,
  type Box,
  type PlanBounds,
  type ResizeHandle,
} from '../../lib/floorGeometry';

export interface DesignerRoom {
  id: string;
  roomNumber: string;
  area: number | null;
  status: number;
  /** null when the room has never been placed on the plan. */
  box: Box | null;
}

export interface DesignerItem extends Box {
  id: string;
  kind: FloorLayoutItemDto['kind'];
  label: string | null;
}

interface FloorDesignerCanvasProps {
  rooms: DesignerRoom[];
  items: DesignerItem[];
  bounds: PlanBounds;
  selection: DesignerSelection;
  onSelect: (selection: DesignerSelection) => void;
  onChangeRoom: (roomId: string, box: Box) => void;
  onChangeItem: (itemId: string, box: Box) => void;
  onDropRoom: (roomId: string, point: { x: number; y: number }) => void;
  onDropItem: (kind: FloorLayoutItemDto['kind'], point: { x: number; y: number }) => void;
  /** "Room dragging still in progress" — the page shows the running numbers. */
  onDraftChange?: (draft: { label: string; box: Box } | null) => void;
}

export type DesignerSelection =
  | { kind: 'room'; id: string }
  | { kind: 'item'; id: string }
  | null;

const statusFill: Record<number, string> = {
  1: '#E9F7E7',
  2: '#FDE2E1',
  3: '#FDF0CC',
  4: '#E8EEF5',
  5: '#F1F5F9',
};
const statusStroke: Record<number, string> = {
  1: '#9BD69B',
  2: '#F2A9A6',
  3: '#EBCB7A',
  4: '#B3C4D8',
  5: '#CBD5E1',
};
const SELECTED_STROKE = '#1d4ed8';

type DragState =
  | { type: 'move'; target: 'room' | 'item'; id: string; origin: Box; startX: number; startY: number }
  | { type: 'resize'; target: 'room' | 'item'; id: string; handle: ResizeHandle; origin: Box }
  | { type: 'pan'; startX: number; startY: number; originOffset: { x: number; y: number } };

/**
 * The designer's drawing surface. Pointer handling is split between live
 * preview (local state, no server traffic) and commit (`onChangeRoom` /
 * `onChangeItem`), so dragging a room across the canvas issues exactly one
 * write when the pointer is released.
 *
 * Coordinates are converted with `getScreenCTM().inverse()`, which means the
 * maths is identical at any zoom level and while panning.
 */
export function FloorDesignerCanvas({
  rooms,
  items,
  bounds,
  selection,
  onSelect,
  onChangeRoom,
  onChangeItem,
  onDropRoom,
  onDropItem,
  onDraftChange,
}: FloorDesignerCanvasProps) {
  const svgRef = useRef<SVGSVGElement>(null);
  const [zoom, setZoom] = useState(1);
  const [offset, setOffset] = useState({ x: 0, y: 0 });
  const [drag, setDrag] = useState<DragState | null>(null);
  const [preview, setPreview] = useState<{ kind: 'room' | 'item'; id: string; box: Box } | null>(
    null,
  );

  const viewBox = useMemo(() => viewBoxRectFor(bounds, zoom, offset), [bounds, zoom, offset]);

  const toMetres = useCallback((clientX: number, clientY: number) => {
    const svg = svgRef.current;
    if (!svg) {
      return { x: 0, y: 0 };
    }
    // jsdom and a few embedded webviews have no `getScreenCTM`; falling back to
    // the origin keeps the handlers total instead of throwing mid-gesture.
    const ctm = svg.getScreenCTM?.();
    if (!ctm) {
      return { x: 0, y: 0 };
    }
    const point = new DOMPoint(clientX, clientY).matrixTransform(ctm.inverse());
    return pointToMetres(point);
  }, []);

  const currentBox = useCallback(
    (target: 'room' | 'item', id: string): Box | null => {
      if (preview && preview.kind === target && preview.id === id) {
        return preview.box;
      }
      if (target === 'room') {
        return rooms.find((room) => room.id === id)?.box ?? null;
      }
      const item = items.find((candidate) => candidate.id === id);
      return item ? { x: item.x, y: item.y, width: item.width, height: item.height } : null;
    },
    [preview, rooms, items],
  );

  function startMove(
    event: React.PointerEvent,
    target: 'room' | 'item',
    id: string,
    box: Box,
  ) {
    event.stopPropagation();
    (event.target as Element).setPointerCapture?.(event.pointerId);
    const start = toMetres(event.clientX, event.clientY);
    setDrag({ type: 'move', target, id, origin: box, startX: start.x, startY: start.y });
    onSelect({ kind: target, id });
  }

  function startResize(
    event: React.PointerEvent,
    target: 'room' | 'item',
    id: string,
    handle: ResizeHandle,
    box: Box,
  ) {
    event.stopPropagation();
    (event.target as Element).setPointerCapture?.(event.pointerId);
    setDrag({ type: 'resize', target, id, handle, origin: box });
  }

  function handlePointerMove(event: React.PointerEvent) {
    if (!drag) {
      return;
    }

    if (drag.type === 'pan') {
      const start = toMetres(event.clientX, event.clientY);
      setOffset({
        x: drag.originOffset.x - (start.x - drag.startX),
        y: drag.originOffset.y - (start.y - drag.startY),
      });
      return;
    }

    const point = toMetres(event.clientX, event.clientY);
    let box: Box;
    if (drag.type === 'move') {
      box = moveBox(drag.origin, point.x - drag.startX, point.y - drag.startY);
    } else {
      box = resizeBox(drag.origin, drag.handle, point);
    }

    const clamped = drag.type === 'move' ? box : clampBoxToBounds(box, bounds);
    setPreview({ kind: drag.target, id: drag.id, box: clamped });
    onDraftChange?.({
      label: drag.target === 'room'
        ? `Phòng ${rooms.find((room) => room.id === drag.id)?.roomNumber ?? ''}`
        : (items.find((item) => item.id === drag.id)?.label ?? 'Vật thể'),
      box: clamped,
    });
  }

  function handlePointerUp() {
    if (!drag) {
      return;
    }
    if (drag.type === 'pan') {
      setDrag(null);
      return;
    }
    // `currentBox` reads the preview first, so the drop commits exactly what
    // the owner saw under the pointer rather than the last committed box.
    const box = currentBox(drag.target, drag.id);
    if (box) {
      if (drag.target === 'room') {
        onChangeRoom(drag.id, box);
      } else {
        onChangeItem(drag.id, box);
      }
    }
    setDrag(null);
    setPreview(null);
    onDraftChange?.(null);
  }

  function startPan(event: React.PointerEvent) {
    if (event.button !== 0) {
      return;
    }
    const start = toMetres(event.clientX, event.clientY);
    setDrag({ type: 'pan', startX: start.x, startY: start.y, originOffset: offset });
    onSelect(null);
  }

  function handleDrop(event: React.DragEvent) {
    event.preventDefault();
    const roomId = event.dataTransfer.getData('application/x-room-id');
    const itemKind = event.dataTransfer.getData('application/x-item-kind');
    if (!roomId && !itemKind) {
      return;
    }
    const point = toMetres(event.clientX, event.clientY);
    if (roomId) {
      onDropRoom(roomId, point);
      return;
    }
    onDropItem(Number(itemKind) as FloorLayoutItemDto['kind'], point);
  }

  return (
    <div>
      <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
        <p className="text-xs text-on-surface-variant">
          Kéo phòng để di chuyển, kéo tay nắm góc để đổi kích thước, kéo nền để di chuyển khung
          nhìn. Lưới bám 0,1 m.
        </p>
        <div className="flex items-center gap-1 rounded-lg border border-outline-variant bg-surface-lowest p-0.5">
          <button
            type="button"
            onClick={() => setZoom((value) => zoomOut(value))}
            aria-label="Thu nhỏ"
            disabled={zoom <= MIN_ZOOM}
            className="flex h-7 w-7 items-center justify-center rounded-md text-on-surface-variant transition hover:bg-surface-low disabled:opacity-40"
          >
            <span className="material-symbols-outlined text-base">remove</span>
          </button>
          <span className="tnum min-w-[46px] text-center text-xs font-semibold text-on-surface">
            {formatZoom(zoom)}
          </span>
          <button
            type="button"
            onClick={() => setZoom((value) => zoomIn(value))}
            aria-label="Phóng to"
            disabled={zoom >= MAX_ZOOM}
            className="flex h-7 w-7 items-center justify-center rounded-md text-on-surface-variant transition hover:bg-surface-low disabled:opacity-40"
          >
            <span className="material-symbols-outlined text-base">add</span>
          </button>
          <button
            type="button"
            onClick={() => {
              setZoom(clampZoom(1));
              setOffset({ x: 0, y: 0 });
            }}
            aria-label="Đặt lại khung nhìn"
            title="Đặt lại khung nhìn"
            className="ml-0.5 flex h-7 w-7 items-center justify-center rounded-md text-on-surface-variant transition hover:bg-surface-low"
          >
            <span className="material-symbols-outlined text-base">fit_screen</span>
          </button>
        </div>
      </div>

      <div
        className="overflow-hidden rounded-lg border border-outline-variant bg-white"
        onDragOver={(event) => event.preventDefault()}
        onDrop={handleDrop}
      >
        <svg
          ref={svgRef}
          viewBox={`${viewBox.x} ${viewBox.y} ${viewBox.width} ${viewBox.height}`}
          className="h-auto w-full touch-none select-none"
          style={{ aspectRatio: `${bounds.width} / ${bounds.height}` }}
          role="application"
          aria-label="Khu vực thiết kế sơ đồ tầng"
          onPointerDown={startPan}
          onPointerMove={handlePointerMove}
          onPointerUp={handlePointerUp}
          onPointerCancel={handlePointerUp}
        >
          <defs>
            <pattern
              id="designer-grid-minor"
              width={1}
              height={1}
              patternUnits="userSpaceOnUse"
            >
              <path d="M 1 0 L 0 0 0 1" fill="none" stroke="#eef2f7" strokeWidth="0.02" />
            </pattern>
            <pattern
              id="designer-grid-major"
              width={5}
              height={5}
              patternUnits="userSpaceOnUse"
            >
              <path d="M 5 0 L 0 0 0 5" fill="none" stroke="#dbe4ee" strokeWidth="0.04" />
            </pattern>
          </defs>

          <rect
            x={viewBox.x}
            y={viewBox.y}
            width={viewBox.width}
            height={viewBox.height}
            fill="#fbfdff"
          />
          <rect
            x={viewBox.x}
            y={viewBox.y}
            width={viewBox.width}
            height={viewBox.height}
            fill="url(#designer-grid-minor)"
          />
          <rect
            x={viewBox.x}
            y={viewBox.y}
            width={viewBox.width}
            height={viewBox.height}
            fill="url(#designer-grid-major)"
          />

          {items.map((item) => {
            const active = selection?.kind === 'item' && selection.id === item.id;
            const box = currentBox('item', item.id) ?? item;
            return (
              <DesignerShape
                key={item.id}
                box={box}
                fill="#EEF2F7"
                stroke="#cbd5e1"
                selected={active}
                label={item.label ?? floorLayoutKindMeta[item.kind]?.label ?? ''}
                onPointerDown={(event) => startMove(event, 'item', item.id, box)}
                onResizeStart={(event, handle) => startResize(event, 'item', item.id, handle, box)}
              />
            );
          })}

          {rooms.map((room) => {
            const active = selection?.kind === 'room' && selection.id === room.id;
            const box = currentBox('room', room.id) ?? room.box;
            if (!box) {
              return null;
            }
            return (
              <DesignerShape
                key={room.id}
                box={box}
                fill={statusFill[room.status] ?? statusFill[5]!}
                stroke={statusStroke[room.status] ?? statusStroke[5]!}
                selected={active}
                label={room.roomNumber}
                sublabel={
                  room.area === null
                    ? formatMetres(box.width * box.height)
                    : `${room.area} m²`
                }
                onPointerDown={(event) => startMove(event, 'room', room.id, box)}
                onResizeStart={(event, handle) => startResize(event, 'room', room.id, handle, box)}
              />
            );
          })}
        </svg>
      </div>
    </div>
  );
}

function DesignerShape({
  box,
  fill,
  stroke,
  selected,
  label,
  sublabel,
  onPointerDown,
  onResizeStart,
}: {
  box: Box;
  fill: string;
  stroke: string;
  selected: boolean;
  label: string;
  sublabel?: string;
  onPointerDown: (event: React.PointerEvent) => void;
  onResizeStart: (event: React.PointerEvent, handle: ResizeHandle) => void;
}) {
  const unit = Math.min(box.width, box.height);
  const titleSize = Math.max(0.22, Math.min(unit * 0.3, box.width * 0.16));
  const subSize = titleSize * 0.72;

  return (
    <g>
      <rect
        x={box.x}
        y={box.y}
        width={box.width}
        height={box.height}
        rx={0.08}
        fill={fill}
        stroke={selected ? SELECTED_STROKE : stroke}
        strokeWidth={selected ? 0.1 : 0.05}
        style={{ cursor: 'move' }}
        onPointerDown={onPointerDown}
      />

      <text
        x={box.x + box.width / 2}
        y={box.y + box.height / 2 + titleSize * 0.35}
        textAnchor="middle"
        fontSize={titleSize}
        fontWeight={700}
        fill="#0b1c30"
        fontFamily="Plus Jakarta Sans, sans-serif"
        pointerEvents="none"
      >
        {label}
      </text>
      {sublabel && box.height > 1.6 && (
        <text
          x={box.x + box.width / 2}
          y={box.y + box.height / 2 + titleSize * 0.35 + subSize * 1.5}
          textAnchor="middle"
          fontSize={subSize}
          fill="#475569"
          fontFamily="Inter, sans-serif"
          pointerEvents="none"
        >
          {sublabel}
        </text>
      )}

      {selected &&
        RESIZE_HANDLES.map(({ handle, cx, cy }) => (
          <rect
            key={handle}
            x={box.x + cx * box.width - HANDLE_SIZE / 2}
            y={box.y + cy * box.height - HANDLE_SIZE / 2}
            width={HANDLE_SIZE}
            height={HANDLE_SIZE}
            fill="#ffffff"
            stroke={SELECTED_STROKE}
            strokeWidth={0.04}
            style={{ cursor: `${handle}-resize` }}
            onPointerDown={(event) => onResizeStart(event, handle)}
          />
        ))}
    </g>
  );
}

/** Half a metre is the floor; the handles themselves are fixed in metres. */
const HANDLE_SIZE = Math.min(0.32, MIN_SIZE_METRES * 0.4);

const RESIZE_HANDLES: { handle: ResizeHandle; cx: number; cy: number }[] = [
  { handle: 'nw', cx: 0, cy: 0 },
  { handle: 'ne', cx: 1, cy: 0 },
  { handle: 'se', cx: 1, cy: 1 },
  { handle: 'sw', cx: 0, cy: 1 },
];
