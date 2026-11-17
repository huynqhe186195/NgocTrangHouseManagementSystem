import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { FloorLayoutItemDto } from '../../lib/api/types';
import { floorLayoutKindMeta, isFurniture } from '../../lib/api/types';
import {
  MAX_ZOOM,
  MIN_ZOOM,
  MIN_SIZE_METRES,
  clampBoxToBounds,
  clampZoom,
  fitViewBoxToAspect,
  formatMetres,
  formatZoom,
  moveBox,
  movePolyline,
  movePolylinePoint,
  pointToMetres,
  resizeBox,
  viewBoxRectFor,
  zoomIn,
  zoomOut,
  type Box,
  type PlanBounds,
  type PlanPoint,
  type ResizeHandle,
} from '../../lib/floorGeometry';
import {
  LayoutObjectArt,
  LINE_COLOR,
  PlanCompass,
  PlanDimensions,
  PlanRulers,
  SELECTED_STROKE,
  WALL_COLOR,
  WallPolylineArt,
  doorArcPath,
  doorWidth,
  objectLabel,
  tickStep,
  wallThickness,
} from './planDrawing';

export interface DesignerRoom {
  id: string;
  roomNumber: string;
  area: number | null;
  status: number;
  /** null when the room has never been placed on the plan. */
  box: Box | null;
  /** Degrees clockwise about the box centre; the stored column is -360..360. */
  rotationDegrees?: number;
}

export interface DesignerItem extends Box {
  id: string;
  kind: FloorLayoutItemDto['kind'];
  label: string | null;
  /** A wall's centreline; null for every other kind and for box-only walls. */
  points?: PlanPoint[] | null;
  thickness?: number | null;
  cornerRadius?: number | null;
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
  /** A double-click on a wall asked for a new bend at this point. */
  onInsertWallPoint?: (itemId: string, point: { x: number; y: number }) => void;
  /** A wall's vertices were dragged: the page stores them and refits the box. */
  onChangeItemPoints?: (itemId: string, points: PlanPoint[]) => void;
  /** "Room dragging still in progress" — the page shows the running numbers. */
  onDraftChange?: (draft: { label: string; box: Box } | null) => void;
  /** Turns the selected room a quarter turn and records it as a draft change. */
  onRotate?: () => void;
  /** Removes the selected room or layout item from the plan. */
  onDeleteSelection?: () => void;
  /** Reports the running tape while measuring; `null` clears it. */
  onMeasure?: (info: MeasureInfo | null) => void;
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

type DragState =
  | {
      type: 'move';
      target: 'room' | 'item';
      id: string;
      origin: Box;
      /** Gesture start in client pixels, and the metres-per-pixel at that moment. */
      clientX: number;
      clientY: number;
      scale: number;
    }
  | { type: 'resize'; target: 'room' | 'item'; id: string; handle: ResizeHandle; origin: Box }
  | {
      /** Dragging one vertex of a bent wall. */
      type: 'wallVertex';
      id: string;
      index: number;
      origin: PlanPoint[];
    }
  | {
      /** Dragging the whole wall: every vertex shifts together. */
      type: 'wallMove';
      id: string;
      origin: PlanPoint[];
      clientX: number;
      clientY: number;
      scale: number;
    }
  | {
      type: 'pan';
      clientX: number;
      clientY: number;
      originOffset: { x: number; y: number };
      scale: number;
    }
  | { type: 'measure'; startX: number; startY: number; endX: number; endY: number };

/** The three pointer modes the toolbar switches between. */
export type DesignerMode = 'select' | 'move' | 'measure';

const DESIGNER_MODES: { mode: DesignerMode; icon: string; label: string; hint: string }[] = [
  { mode: 'select', icon: 'arrow_selector_tool', label: 'Chọn', hint: 'Bấm để chọn, không kéo' },
  { mode: 'move', icon: 'open_with', label: 'Di chuyển', hint: 'Kéo để dời phòng và vật thể' },
  { mode: 'measure', icon: 'straighten', label: 'Đo', hint: 'Kéo một đường để đo chiều dài' },
];

export interface MeasureInfo {
  dx: number;
  dy: number;
  distance: number;
}

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
  onInsertWallPoint,
  onChangeItemPoints,
  onDraftChange,
  onRotate,
  onDeleteSelection,
  onMeasure,
}: FloorDesignerCanvasProps) {
  const svgRef = useRef<SVGSVGElement>(null);
  const frameRef = useRef<HTMLDivElement>(null);
  const [zoom, setZoom] = useState(1);
  const [offset, setOffset] = useState({ x: 0, y: 0 });
  const [drag, setDrag] = useState<DragState | null>(null);
  const [mode, setMode] = useState<DesignerMode>('select');
  const [fullscreen, setFullscreen] = useState(false);
  const [preview, setPreview] = useState<{ kind: 'room' | 'item'; id: string; box: Box } | null>(
    null,
  );
  // A bent wall is dragged by its vertices, so the live geometry is a point
  // list rather than a box: the preview has to hold that too.
  const [wallPreview, setWallPreview] = useState<{ id: string; points: PlanPoint[] } | null>(null);
  const [measure, setMeasure] = useState<{ x1: number; y1: number; x2: number; y2: number } | null>(
    null,
  );
  const [frameSize, setFrameSize] = useState({ width: 0, height: 0 });

  // The canvas is sized by CSS, so its pixel size is only known after layout.
  // Watching the element keeps the drawing fitted through every resize.
  useEffect(() => {
    const frame = frameRef.current;
    if (!frame || typeof ResizeObserver === 'undefined') {
      return;
    }
    const observer = new ResizeObserver((entries) => {
      const rect = entries[0]?.contentRect;
      if (rect) {
        setFrameSize({ width: rect.width, height: rect.height });
      }
    });
    observer.observe(frame);
    return () => observer.disconnect();
  }, []);

  // The drawing fills the available height instead of shrinking to its own
  // aspect ratio: a shallow floor used to paint as a thin strip with empty
  // space above and below it. Widening the viewBox to the frame's ratio keeps
  // metres square and fills the whole canvas with grid.
  const viewBox = useMemo(() => {
    const box = viewBoxRectFor(bounds, zoom, offset);
    return frameSize.width > 0 && frameSize.height > 0
      ? fitViewBoxToAspect(box, frameSize.width / frameSize.height)
      : box;
  }, [bounds, zoom, offset, frameSize]);
  const gridStep = useMemo(() => tickStep(bounds, zoom), [bounds.width, zoom]);

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

  /**
   * Metres per client pixel, measured once when a gesture starts.
   *
   * Panning must not ask the live CTM where the pointer is: moving the offset
   * changes the viewBox, which changes the CTM, which changes the next delta —
   * the drag feeds back into itself and the drawing shakes. Reading the scale
   * at gesture start and multiplying raw pixel movement by it breaks the loop;
   * the viewBox changing underneath then has no effect on the delta.
   */
  const scaleAtStart = useCallback((): number => {
    const svg = svgRef.current;
    const ctm = svg?.getScreenCTM?.();
    if (!ctm || !Number.isFinite(ctm.a) || ctm.a === 0) {
      return 1;
    }
    return 1 / Math.abs(ctm.a);
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

  /** The live vertex list for a wall: its drag preview, else what was handed in. */
  const currentPoints = useCallback(
    (item: DesignerItem): PlanPoint[] | null =>
      wallPreview && wallPreview.id === item.id ? wallPreview.points : (item.points ?? null),
    [wallPreview],
  );

  function startMove(
    event: React.PointerEvent,
    target: 'room' | 'item',
    id: string,
    box: Box,
  ) {
    event.stopPropagation();
    // In "select" the pointer only picks; in "move" it drags. Both modes let
    // the corner handles resize, so a selected box is always adjustable.
    onSelect({ kind: target, id });
    if (mode === 'select') {
      return;
    }
    (event.target as Element).setPointerCapture?.(event.pointerId);
    setDrag({
      type: 'move',
      target,
      id,
      origin: box,
      clientX: event.clientX,
      clientY: event.clientY,
      scale: scaleAtStart(),
    });
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

  function startWallVertex(event: React.PointerEvent, id: string, index: number, points: PlanPoint[]) {
    event.stopPropagation();
    onSelect({ kind: 'item', id });
    (event.target as Element).setPointerCapture?.(event.pointerId);
    setDrag({ type: 'wallVertex', id, index, origin: points });
  }

  function startWallMove(
    event: React.PointerEvent,
    id: string,
    points: PlanPoint[],
  ) {
    event.stopPropagation();
    onSelect({ kind: 'item', id });
    if (mode === 'select') {
      return;
    }
    (event.target as Element).setPointerCapture?.(event.pointerId);
    setDrag({
      type: 'wallMove',
      id,
      origin: points,
      clientX: event.clientX,
      clientY: event.clientY,
      scale: scaleAtStart(),
    });
  }

  function handlePointerMove(event: React.PointerEvent) {
    if (!drag) {
      return;
    }

    if (drag.type === 'pan') {
      // Delta in raw client pixels, scaled by the metre-per-pixel measured at
      // gesture start. Re-deriving it from the live CTM would make the pan
      // chase its own viewBox change and the drawing would shake.
      setOffset({
        x: drag.originOffset.x - (event.clientX - drag.clientX) * drag.scale,
        y: drag.originOffset.y - (event.clientY - drag.clientY) * drag.scale,
      });
      return;
    }

    if (drag.type === 'measure') {
      const point = toMetres(event.clientX, event.clientY);
      const next = { x1: drag.startX, y1: drag.startY, x2: point.x, y2: point.y };
      setMeasure(next);
      onMeasure?.(measureInfo(next));
      return;
    }

    if (drag.type === 'wallVertex') {
      const point = toMetres(event.clientX, event.clientY);
      setWallPreview({
        id: drag.id,
        points: movePolylinePoint(drag.origin, drag.index, point),
      });
      return;
    }

    if (drag.type === 'wallMove') {
      // Pixel delta scaled by the gesture-start ratio, exactly like a box move:
      // re-reading the CTM here would drift as the preview re-renders.
      setWallPreview({
        id: drag.id,
        points: movePolyline(
          drag.origin,
          (event.clientX - drag.clientX) * drag.scale,
          (event.clientY - drag.clientY) * drag.scale,
        ),
      });
      return;
    }

    const point = toMetres(event.clientX, event.clientY);
    let box: Box;
    if (drag.type === 'move') {
      // Pan/zoom cannot change mid-drag, so a pixel delta is just as exact as
      // two CTM lookups and does not drift when the preview re-renders.
      box = moveBox(
        drag.origin,
        (event.clientX - drag.clientX) * drag.scale,
        (event.clientY - drag.clientY) * drag.scale,
      );
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
    if (drag.type === 'pan' || drag.type === 'measure') {
      setDrag(null);
      return;
    }
    if (drag.type === 'wallVertex' || drag.type === 'wallMove') {
      // The preview is what the owner saw under the pointer; committing the
      // origin would snap the wall back at the moment of release.
      const next = wallPreview?.id === drag.id ? wallPreview.points : drag.origin;
      onChangeItemPoints?.(drag.id, next);
      setDrag(null);
      setWallPreview(null);
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

    if (mode === 'measure') {
      (event.target as Element).setPointerCapture?.(event.pointerId);
      const next = { x1: start.x, y1: start.y, x2: start.x, y2: start.y };
      setMeasure(next);
      onMeasure?.(measureInfo(next));
      setDrag({ type: 'measure', startX: start.x, startY: start.y, endX: start.x, endY: start.y });
      return;
    }

    setDrag({
      type: 'pan',
      clientX: event.clientX,
      clientY: event.clientY,
      originOffset: offset,
      scale: scaleAtStart(),
    });
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

  function chooseMode(next: DesignerMode) {
    setMode(next);
    if (next !== 'measure') {
      setMeasure(null);
      onMeasure?.(null);
    }
  }

  async function toggleFullscreen() {
    const frame = frameRef.current;
    if (!frame) {
      return;
    }
    try {
      if (document.fullscreenElement) {
        await document.exitFullscreen();
        setFullscreen(false);
      } else {
        await frame.requestFullscreen();
        setFullscreen(true);
      }
    } catch {
      // A denied or unsupported request leaves the canvas where it was; the
      // toolbar still shows the plan at its normal size.
      setFullscreen(Boolean(document.fullscreenElement));
    }
  }

  const hasSelection = selection !== null;

  return (
    <div>
      <div className="mb-2 flex flex-col gap-2 xl:flex-row xl:items-center xl:justify-between">
        <div className="flex flex-wrap items-center gap-1 rounded-lg border border-outline-variant bg-surface-lowest p-0.5">
          {DESIGNER_MODES.map((option) => (
            <button
              key={option.mode}
              type="button"
              onClick={() => chooseMode(option.mode)}
              aria-pressed={mode === option.mode}
              title={option.hint}
              className={`flex h-7 items-center gap-1 rounded-md px-2 text-xs font-medium transition ${
                mode === option.mode
                  ? 'bg-primary-container text-on-primary-container'
                  : 'text-on-surface-variant hover:bg-surface-low'
              }`}
            >
              <span className="material-symbols-outlined text-base">{option.icon}</span>
              {option.label}
            </button>
          ))}
          <span className="mx-1 h-5 w-px bg-outline-variant" />
          <ToolbarButton
            icon="rotate_90_degrees_cw"
            label="Xoay 90°"
            onClick={() => onRotate?.()}
            disabled={!hasSelection || selection.kind !== 'room' || !onRotate}
          />
          <ToolbarButton
            icon="delete"
            label="Xoá vật thể"
            onClick={() => onDeleteSelection?.()}
            disabled={!hasSelection || !onDeleteSelection}
          />
          <span className="mx-1 h-5 w-px bg-outline-variant" />
          <ToolbarButton
            icon="fullscreen"
            label={fullscreen ? 'Thoát toàn màn hình' : 'Toàn màn hình'}
            onClick={() => void toggleFullscreen()}
          />
        </div>

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

      <p className="mb-2 text-xs text-on-surface-variant">
        {mode === 'select' && 'Chọn: bấm để chọn phòng hoặc vật thể. Kéo nền để di chuyển khung nhìn.'}
        {mode === 'move' && 'Di chuyển: kéo phòng để đổi vị trí, kéo tay nắm góc để đổi kích thước.'}
        {mode === 'measure' && 'Đo: kéo một đường trên sơ đồ để đọc chiều dài thật (mét).'}
        {' '}Lưới bám 0,1 m.
      </p>

      <div
        ref={frameRef}
        className="overflow-hidden rounded-lg border border-outline-variant bg-white"
        onDragOver={(event) => event.preventDefault()}
        onDrop={handleDrop}
      >
        <svg
          ref={svgRef}
          viewBox={`${viewBox.x} ${viewBox.y} ${viewBox.width} ${viewBox.height}`}
          preserveAspectRatio="xMidYMid meet"
          className="h-[clamp(480px,calc(100vh-310px),900px)] w-full touch-none select-none"
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
              <path d="M 1 0 L 0 0 0 1" fill="none" stroke="#e4eaf2" strokeWidth="0.018" />
            </pattern>
            <pattern
              id="designer-grid-major"
              width={5}
              height={5}
              patternUnits="userSpaceOnUse"
            >
              <path d="M 5 0 L 0 0 0 5" fill="none" stroke="#cbd6e4" strokeWidth="0.04" />
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

          <PlanRulers viewBox={viewBox} step={gridStep} />
          <PlanDimensions bounds={bounds} inset={Math.max(0.8, Math.min(bounds.width, bounds.height) * 0.06)} />
          <PlanCompass
            viewBox={viewBox}
            size={Math.max(0.9, Math.min(viewBox.width, viewBox.height) * 0.09)}
          />

          {items
            .filter((item) => !isFurniture(item.kind) && item.kind !== 12)
            .map((item) => (
              <DesignerShape
                key={item.id}
                box={currentBox('item', item.id) ?? item}
                kind={item.kind}
                selected={selection?.kind === 'item' && selection.id === item.id}
                interactive={mode !== 'measure'}
                label={item.label ?? floorLayoutKindMeta[item.kind]?.label ?? ''}
                onPointerDown={(event) =>
                  startMove(event, 'item', item.id, currentBox('item', item.id) ?? item)
                }
                onResizeStart={(event, handle) =>
                  startResize(event, 'item', item.id, handle, currentBox('item', item.id) ?? item)
                }
              />
            ))}

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
                rotation={room.rotationDegrees ?? 0}
                interactive={mode !== 'measure'}
                label={room.roomNumber}
                sublabel={
                  room.area === null
                    ? formatMetres(box.width * box.height)
                    : `${room.area} m²`
                }
                walled
                showCaption={false}
                onPointerDown={(event) => startMove(event, 'room', room.id, box)}
                onResizeStart={(event, handle) => startResize(event, 'room', room.id, handle, box)}
              />
            );
          })}

          {/* Furniture belongs on a room's floor, so it paints after the rooms;
              walls paint last so a wall along a room edge stays visible. A
              wall with a centreline is drawn by its own component: it is a
              polyline, not a box, so its vertices need their own hit areas. */}
          {items
            .filter((item) => isFurniture(item.kind))
            .map((item) => (
              <DesignerShape
                key={item.id}
                box={currentBox('item', item.id) ?? item}
                kind={item.kind}
                selected={selection?.kind === 'item' && selection.id === item.id}
                interactive={mode !== 'measure'}
                label={item.label ?? floorLayoutKindMeta[item.kind]?.label ?? ''}
                onPointerDown={(event) =>
                  startMove(event, 'item', item.id, currentBox('item', item.id) ?? item)
                }
                onResizeStart={(event, handle) =>
                  startResize(event, 'item', item.id, handle, currentBox('item', item.id) ?? item)
                }
              />
            ))}

          {items
            .filter((item) => item.kind === 12)
            .map((item) => {
              const points = currentPoints(item);
              const selected = selection?.kind === 'item' && selection.id === item.id;
              if (!points || points.length < 2) {
                return (
                  <DesignerShape
                    key={item.id}
                    box={currentBox('item', item.id) ?? item}
                    kind={item.kind}
                    selected={selected}
                    interactive={mode !== 'measure'}
                    label={item.label ?? floorLayoutKindMeta[item.kind]?.label ?? ''}
                    onPointerDown={(event) =>
                      startMove(event, 'item', item.id, currentBox('item', item.id) ?? item)
                    }
                    onResizeStart={(event, handle) =>
                      startResize(event, 'item', item.id, handle, currentBox('item', item.id) ?? item)
                    }
                  />
                );
              }
              return (
                <DesignerWall
                  key={item.id}
                  item={item}
                  points={points}
                  box={currentBox('item', item.id) ?? item}
                  selected={selected}
                  interactive={mode !== 'measure'}
                  canInsert={Boolean(onInsertWallPoint)}
                  onPointerDown={(event) => startWallMove(event, item.id, points)}
                  onVertexPointerDown={(event, index) =>
                    startWallVertex(event, item.id, index, points)
                  }
                  onInsertPoint={(point) => onInsertWallPoint?.(item.id, point)}
                />
              );
            })}

          {/* The room number goes on above the furniture, as it does on the
              finished plan: a bed laid against the wall would otherwise sit on
              top of the number. */}
          {rooms.map((room) => {
            const box = currentBox('room', room.id) ?? room.box;
            if (!box) {
              return null;
            }
            return (
              <DesignerRoomLabel
                key={`label-${room.id}`}
                box={box}
                rotation={room.rotationDegrees ?? 0}
                label={room.roomNumber}
                sublabel={
                  room.area === null ? formatMetres(box.width * box.height) : `${room.area} m²`
                }
              />
            );
          })}

          {measure && <MeasureOverlay segment={measure} viewBox={viewBox} />}
        </svg>
      </div>
    </div>
  );
}

/** A toolbar button that says why it is disabled instead of silently doing nothing. */
function ToolbarButton({
  icon,
  label,
  onClick,
  disabled = false,
}: {
  icon: string;
  label: string;
  onClick: () => void;
  disabled?: boolean;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      title={label}
      className="flex h-7 items-center gap-1 rounded-md px-2 text-xs font-medium text-on-surface-variant transition hover:bg-surface-low disabled:cursor-not-allowed disabled:opacity-40"
    >
      <span className="material-symbols-outlined text-base">{icon}</span>
      {label}
    </button>
  );
}

/** The running tape: a dashed line between the two points and its length. */
function MeasureOverlay({
  segment,
  viewBox,
}: {
  segment: { x1: number; y1: number; x2: number; y2: number };
  viewBox: { x: number; y: number; width: number; height: number };
}) {
  const info = measureInfo(segment);
  if (info.distance < 0.05) {
    return null;
  }
  const fontSize = Math.max(0.3, viewBox.width * 0.014);
  const midX = (segment.x1 + segment.x2) / 2;
  const midY = (segment.y1 + segment.y2) / 2;

  const label = `${formatMetres(info.distance)}  ·  Δx ${formatMetres(Math.abs(info.dx))}  Δy ${formatMetres(Math.abs(info.dy))}`;

  // The label sits just off the line's midpoint, on the side the segment
  // leans away from, so it never covers the measurement it describes.
  const nx = -info.dy / info.distance;
  const ny = info.dx / info.distance;
  const offset = fontSize * 2.1;
  const labelX = midX + nx * offset;
  const labelY = midY + ny * offset;

  return (
    <g pointerEvents="none" fontFamily="Inter, sans-serif">
      <line
        x1={segment.x1}
        y1={segment.y1}
        x2={segment.x2}
        y2={segment.y2}
        stroke={SELECTED_STROKE}
        strokeWidth={fontSize * 0.09}
        strokeDasharray={`${fontSize * 1.4} ${fontSize * 0.9}`}
      />
      <circle cx={segment.x1} cy={segment.y1} r={fontSize * 0.22} fill={SELECTED_STROKE} />
      <circle cx={segment.x2} cy={segment.y2} r={fontSize * 0.22} fill={SELECTED_STROKE} />
      <MeasureLabel label={label} fontSize={fontSize} cx={labelX} cy={labelY} />
    </g>
  );
}

/**
 * The measurement readout. A white halo is painted under the glyphs instead of
 * a sized plate: the halo tracks the text exactly whatever the font metrics,
 * so the number stays readable over corridors and room labels without a box
 * that could be too small on some machine.
 */
function MeasureLabel({
  label,
  fontSize,
  cx,
  cy,
}: {
  label: string;
  fontSize: number;
  cx: number;
  cy: number;
}) {
  return (
    <text
      x={cx}
      y={cy}
      dominantBaseline="middle"
      textAnchor="middle"
      fontSize={fontSize}
      fontWeight={700}
      fill={SELECTED_STROKE}
      stroke="#ffffff"
      strokeWidth={fontSize * 0.34}
      strokeLinejoin="round"
      paintOrder="stroke"
    >
      {label}
    </text>
  );
}

function DesignerShape({
  box,
  kind,
  fill = 'transparent',
  stroke = 'transparent',
  selected,
  label,
  sublabel,
  walled = false,
  rotation = 0,
  interactive = true,
  showCaption = true,
  onPointerDown,
  onResizeStart,
}: {
  box: Box;
  /** Set for palette objects; rooms leave it undefined and pass `walled`. */
  kind?: FloorLayoutItemDto['kind'];
  fill?: string;
  stroke?: string;
  selected: boolean;
  label: string;
  sublabel?: string;
  /** Rooms read as walls + door; objects read as a light outlined box. */
  walled?: boolean;
  /** Degrees clockwise about the box centre, matching the stored column. */
  rotation?: number;
  interactive?: boolean;
  /** False when the label is drawn by a later pass, above the furniture. */
  showCaption?: boolean;
  onPointerDown: (event: React.PointerEvent) => void;
  onResizeStart: (event: React.PointerEvent, handle: ResizeHandle) => void;
}) {
  const unit = Math.min(box.width, box.height);
  const titleSize = Math.max(0.22, Math.min(unit * 0.3, box.width * 0.16));
  const subSize = titleSize * 0.72;
  const wall = wallThickness(box);
  const door = doorWidth(box);
  const centreX = box.x + box.width / 2;
  const centreY = box.y + box.height / 2;
  const isNote = kind === 7;
  // Room numbers centre in the room; a floor object captions a strip under its
  // own symbol, the way the mockup labels the stairwell and the communal WC.
  // A note *is* its text, so it is centred in its own box instead. Furniture is
  // left unlabelled, as the finished plan leaves it: the symbol already says
  // what the piece is, and the word would sit on top of it.
  const caption =
    isNote || (kind !== undefined && isFurniture(kind))
      ? null
      : kind === undefined
        ? null
        : objectLabel(box, label);

  return (
    <g transform={rotation ? `rotate(${rotation} ${centreX} ${centreY})` : undefined}>
      <rect
        x={box.x}
        y={box.y}
        width={box.width}
        height={box.height}
        rx={0.08}
        fill={walled || kind !== undefined ? 'transparent' : fill}
        stroke={walled ? 'none' : kind !== undefined ? 'none' : selected ? SELECTED_STROKE : stroke}
        strokeWidth={walled || kind !== undefined ? 0 : selected ? 0.1 : 0.05}
        style={{ cursor: interactive ? 'move' : 'default' }}
        onPointerDown={interactive ? onPointerDown : undefined}
        role={interactive ? 'button' : undefined}
        aria-label={interactive ? label : undefined}
        aria-pressed={interactive ? selected : undefined}
      />

      {kind !== undefined ? (
        // The same symbols the finished plan draws, so what is dropped here is
        // what the owner sees on the floor-plan screen.
        <g pointerEvents="none">
          <LayoutObjectArt box={box} kind={kind} caption={caption?.caption} />
        </g>
      ) : (
        walled && (
          <g pointerEvents="none">
            {/* Walls drawn as one stroke per room edge, so neighbouring rooms
                meeting wall-to-wall give a single wall rather than a doubled
                band; the outer half of an exterior edge is added by
                `PlanPerimeter`. */}
            <rect
              x={box.x}
              y={box.y}
              width={box.width}
              height={box.height}
              fill="none"
              stroke={WALL_COLOR}
              strokeWidth={wall}
            />
            <rect
              x={box.x + (box.width - door) / 2}
              y={box.y + box.height - wall}
              width={door}
              height={wall}
              fill="#ffffff"
            />
            <path
              d={doorArcPath(box, wall, door)}
              fill="none"
              stroke={LINE_COLOR}
              strokeWidth={Math.max(0.02, wall * 0.2)}
            />
          </g>
        )
      )}

      {/* Counter-rotated so a turned room keeps its label readable instead of
          printing the number on its side. */}
      {showCaption && (
        <g
          transform={
            rotation ? `rotate(${-rotation} ${centreX} ${centreY})` : undefined
          }
        >
          <text
            x={centreX}
            y={caption ? caption.baselineY : centreY + titleSize * 0.35}
            textAnchor="middle"
            fontSize={caption ? caption.fontSize : titleSize}
            textLength={caption?.textLength}
            lengthAdjust={caption?.textLength === undefined ? undefined : 'spacingAndGlyphs'}
            fontWeight={caption ? 600 : 700}
            fill={caption ? '#475569' : '#0b1c30'}
            fontFamily={caption ? 'Inter, sans-serif' : 'Plus Jakarta Sans, sans-serif'}
            pointerEvents="none"
            opacity={kind !== undefined && caption === null && !isNote ? 0 : 1}
          >
            {label}
          </text>
          {sublabel && box.height > 1.6 && kind === undefined && (
            <text
              x={centreX}
              y={centreY + titleSize * 0.35 + subSize * 1.5}
              textAnchor="middle"
              fontSize={subSize}
              fill="#475569"
              fontFamily="Inter, sans-serif"
              pointerEvents="none"
            >
              {sublabel}
            </text>
          )}
        </g>
      )}

      {/* The selected outline sits inside the wall so it never hides it. */}
      <rect
        x={box.x}
        y={box.y}
        width={box.width}
        height={box.height}
        rx={0.08}
        fill="none"
        stroke={selected ? SELECTED_STROKE : 'transparent'}
        strokeWidth={isNote ? 0.03 : 0.05}
        strokeDasharray={selected ? '0.4 0.3' : undefined}
        pointerEvents="none"
      />

      {selected &&
        interactive &&
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

/**
 * A wall with a centreline. It is not a box: its shape is the vertex list, so
 * it carries its own hit area (the thick stroke the plan draws, made grabbable)
 * and one round handle per vertex. Dragging a vertex bends the wall; double-
 * clicking anywhere along it adds a bend at that spot.
 *
 * The four corner handles a box has are deliberately absent — resizing the
 * bounding rectangle of a bent wall would move geometry the owner never pointed
 * at, and the box is only a storage detail once a centreline exists.
 */
function DesignerWall({
  item,
  points,
  box,
  selected,
  interactive,
  canInsert,
  onPointerDown,
  onVertexPointerDown,
  onInsertPoint,
}: {
  item: DesignerItem;
  points: PlanPoint[];
  box: Box;
  selected: boolean;
  interactive: boolean;
  canInsert: boolean;
  onPointerDown: (event: React.PointerEvent) => void;
  onVertexPointerDown: (event: React.PointerEvent, index: number) => void;
  onInsertPoint: (point: { x: number; y: number }) => void;
}) {
  const thickness = item.thickness ?? Math.min(box.width, box.height);
  const label = item.label ?? floorLayoutKindMeta[item.kind]?.label ?? 'Tường';
  // A wall's box is the extent of its whole run — 26 m across on a real floor —
  // so a handle sized as a fraction of it becomes a disc that swallows the
  // drawing. It reuses the box handles' fixed size instead, so a vertex is the
  // same size on every wall and every floor.
  const handle = HANDLE_SIZE;

  function handleDoubleClick(event: React.MouseEvent) {
    if (!canInsert) {
      return;
    }
    event.stopPropagation();
    const svg = (event.currentTarget as SVGElement).ownerSVGElement;
    const ctm = svg?.getScreenCTM?.();
    if (!ctm) {
      return;
    }
    const point = new DOMPoint(event.clientX, event.clientY).matrixTransform(ctm.inverse());
    onInsertPoint(pointToMetres(point));
  }

  return (
    <g>
      <WallPolylineArt points={points} thickness={thickness} cornerRadius={item.cornerRadius ?? 0} />

      {/* The grabbable band. Transparent so the wall itself stays the drawing,
          and wider than the wall so a thin partition is still easy to hit. */}
      <path
        d={polylinePath(points)}
        fill="none"
        stroke="transparent"
        strokeWidth={Math.max(thickness, 0.5)}
        strokeLinejoin="miter"
        strokeLinecap="butt"
        style={{ cursor: interactive ? 'move' : 'default' }}
        onPointerDown={interactive ? onPointerDown : undefined}
        onDoubleClick={handleDoubleClick}
        role={interactive ? 'button' : undefined}
        aria-label={interactive ? label : undefined}
        aria-pressed={interactive ? selected : undefined}
      >
        <title>{canInsert ? 'Nhấp đúp để thêm điểm gãy' : label}</title>
      </path>

      {selected && (
        <>
          <path
            d={polylinePath(points)}
            fill="none"
            stroke={SELECTED_STROKE}
            strokeWidth={Math.max(0.03, thickness * 0.12)}
            strokeDasharray="0.35 0.25"
            strokeLinejoin="miter"
            pointerEvents="none"
          />
          {interactive &&
            points.map((point, index) => (
              <circle
                key={`${point.x}-${point.y}-${index}`}
                cx={point.x}
                cy={point.y}
                r={handle / 2}
                fill="#ffffff"
                stroke={SELECTED_STROKE}
                strokeWidth={Math.max(0.02, handle * 0.12)}
                style={{ cursor: 'crosshair' }}
                onPointerDown={(event) => onVertexPointerDown(event, index)}
              >
                <title>{`Điểm gãy ${index + 1}`}</title>
              </circle>
            ))}
        </>
      )}
    </g>
  );
}

/** A plain polyline path, for the hit band and the selection outline. */
function polylinePath(points: PlanPoint[]): string {
  if (points.length === 0) {
    return '';
  }
  return points
    .map((point, index) => `${index === 0 ? 'M' : 'L'} ${point.x} ${point.y}`)
    .join(' ');
}

/**
 * A room's number and area, drawn on its own so it can paint above the
 * furniture. A white halo keeps both legible where a bed or a desk runs under
 * the text, the way the finished plan labels a room.
 */
function DesignerRoomLabel({
  box,
  rotation,
  label,
  sublabel,
}: {
  box: Box;
  rotation: number;
  label: string;
  sublabel?: string;
}) {
  const unit = Math.min(box.width, box.height);
  const titleSize = Math.max(0.22, Math.min(unit * 0.3, box.width * 0.16));
  const subSize = titleSize * 0.72;
  const centreX = box.x + box.width / 2;
  const centreY = box.y + box.height / 2;

  return (
    <g
      pointerEvents="none"
      transform={rotation ? `rotate(${-rotation} ${centreX} ${centreY})` : undefined}
    >
      <text
        x={centreX}
        y={centreY + titleSize * 0.35}
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
        {label}
      </text>
      {sublabel && box.height > 1.6 && (
        <text
          x={centreX}
          y={centreY + titleSize * 0.35 + subSize * 1.5}
          textAnchor="middle"
          fontSize={subSize}
          fill="#475569"
          stroke="#ffffff"
          strokeWidth={subSize * 0.16}
          paintOrder="stroke"
          strokeLinejoin="round"
          fontFamily="Inter, sans-serif"
        >
          {sublabel}
        </text>
      )}
    </g>
  );
}

function measureInfo(segment: { x1: number; y1: number; x2: number; y2: number }): MeasureInfo {
  const dx = segment.x2 - segment.x1;
  const dy = segment.y2 - segment.y1;
  return { dx, dy, distance: Math.hypot(dx, dy) };
}

const RESIZE_HANDLES: { handle: ResizeHandle; cx: number; cy: number }[] = [
  { handle: 'nw', cx: 0, cy: 0 },
  { handle: 'ne', cx: 1, cy: 0 },
  { handle: 'se', cx: 1, cy: 1 },
  { handle: 'sw', cx: 0, cy: 1 },
];
