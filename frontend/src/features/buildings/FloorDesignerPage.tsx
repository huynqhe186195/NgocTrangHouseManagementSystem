import { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { Alert } from '../../components/ui/Alert';
import { Button } from '../../components/ui/Button';
import { EmptyState } from '../../components/ui/EmptyState';
import { Field } from '../../components/ui/Field';
import { PageHeader } from '../../components/ui/PageHeader';
import { StatusBadge } from '../../components/ui/StatusBadge';
import { useAuth } from '../../lib/auth/AuthProvider';
import { BuildingPolicies } from '../../lib/auth/policies';
import { ApiError } from '../../lib/http';
import {
  useBuildingSnapshot,
  useCreateFloorLayoutItem,
  useDeleteFloorLayoutItem,
  useFloorLayoutItems,
  useSaveRoomLayouts,
  useUpdateFloorLayoutItem,
} from '../../lib/api/buildingQueries';
import { describeMutationError } from '../../lib/api/mutationErrors';
import { formatVnd } from '../../lib/format';
import { floorLayoutKindGroup, floorLayoutKindMeta, roomStatusMeta } from '../../lib/api/types';
import type { FloorLayoutItemDto, FloorLayoutItemKind, RoomDto } from '../../lib/api/types';
import {
  MIN_SIZE_METRES,
  boxOfPolyline,
  boxesEqual,
  clampBoxToBounds,
  defaultRoomBox,
  formatMetres,
  hasGeometry,
  insertPointOnPolyline,
  movePolyline,
  parsePoints,
  serializePoints,
  snapToGrid,
  type Box,
  type PlanBounds,
  type PlanPoint,
} from '../../lib/floorGeometry';
import {
  FloorDesignerCanvas,
  type DesignerItem,
  type DesignerRoom,
  type DesignerSelection,
  type MeasureInfo,
} from './FloorDesignerCanvas';

const PADDING_METRES = 1.5;
const DEFAULT_ROOM_SIZE = { width: 4, height: 3.5 };

/**
 * The size each palette object lands at. A stairwell is not the same shape as
 * a WC, and dropping every kind as a 1.6 m cube made the plan look like a pile
 * of identical blocks; these are the real footprints from the mockup.
 */
const ITEM_DEFAULT_SIZE: Record<FloorLayoutItemKind, { width: number; height: number }> = {
  1: { width: 8, height: 1.8 }, // Hành lang
  2: { width: 2.6, height: 4.2 }, // Cầu thang
  3: { width: 1.8, height: 2.2 }, // Thang máy
  4: { width: 3, height: 3 }, // Khu dịch vụ
  5: { width: 2, height: 2.6 }, // Nhà vệ sinh
  6: { width: 1.2, height: 0.3 }, // Cửa ra vào
  7: { width: 2.4, height: 0.8 }, // Nhãn chú thích
  8: { width: 2.4, height: 2.6 }, // Phòng giặt
  9: { width: 2.4, height: 2.6 }, // Kho
  10: { width: 3.4, height: 1.5 }, // Ban công
  11: { width: 1.2, height: 1.2 }, // Cây xanh
  12: { width: 8, height: 0.3 }, // Tường
  // Furniture lands at the footprint it occupies on a real plan, so a bed
  // dropped into a 3.6 m room already fits without the owner scaling it up.
  // A bed on a real plan is 1.6 m wide by 2 m long, and the mockup draws it at
  // that size: the long side runs against the wall, the headboard at one end.
  13: { width: 2, height: 1.6 }, // Giường
  14: { width: 1.6, height: 0.6 }, // Tủ quần áo
  15: { width: 1.4, height: 0.65 }, // Bàn làm việc
  16: { width: 0.5, height: 0.5 }, // Ghế
  17: { width: 1.9, height: 0.85 }, // Sofa
  18: { width: 0.9, height: 0.3 }, // Điều hoà
  19: { width: 1.8, height: 0.6 }, // Bếp
  20: { width: 0.7, height: 0.7 }, // Tủ lạnh
  21: { width: 1.2, height: 0.25 }, // Tivi
};
const EMPTY_FLOOR_BOUNDS: PlanBounds = {
  minX: 0,
  minY: 0,
  maxX: 14,
  maxY: 10,
  width: 14,
  height: 10,
};

/** A room's box as the designer sees it: null when it has never been placed. */
function roomBox(room: RoomDto): Box | null {
  if (!hasGeometry(room)) {
    return null;
  }
  return {
    x: room.layoutX as number,
    y: room.layoutY as number,
    width: room.layoutWidth as number,
    height: room.layoutHeight as number,
  };
}

function itemBox(item: FloorLayoutItemDto): Box {
  return { x: item.x, y: item.y, width: item.width, height: item.height };
}

/**
 * The drawing grows with whatever is on it. A floor with nothing placed still
 * gets a usable canvas instead of a zero-sized one.
 */
function designerBounds(boxes: Box[]): PlanBounds {
  if (boxes.length === 0) {
    return EMPTY_FLOOR_BOUNDS;
  }
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const box of boxes) {
    minX = Math.min(minX, box.x);
    minY = Math.min(minY, box.y);
    maxX = Math.max(maxX, box.x + box.width);
    maxY = Math.max(maxY, box.y + box.height);
  }
  minX -= PADDING_METRES;
  minY -= PADDING_METRES;
  maxX += PADDING_METRES;
  maxY += PADDING_METRES;
  return { minX, minY, maxX, maxY, width: maxX - minX, height: maxY - minY };
}

interface DraftItem {
  /** Server id, or a temporary id for an object that has not been created. */
  id: string;
  kind: FloorLayoutItemKind;
  label: string | null;
  box: Box;
  isNew: boolean;
  /** A wall's centreline. Null for every other kind and for box-only walls. */
  points: PlanPoint[] | null;
  thickness: number | null;
  cornerRadius: number | null;
}

/** Where a room sits and how far it is turned; both are saved together. */
interface RoomDraft {
  box: Box | null;
  rotation: number;
}

/** Everything unsaved on this floor, in the form the history stack stores. */
interface DraftState {
  rooms: Record<string, RoomDraft>;
  items: Record<string, DraftItem>;
  removed: string[];
}

const EMPTY_DRAFTS: DraftState = { rooms: {}, items: {}, removed: [] };

/** Deep enough to cover an editing session, shallow enough to stay in memory. */
const HISTORY_LIMIT = 50;

interface DraftHistory {
  past: DraftState[];
  present: DraftState;
  future: DraftState[];
}

const EMPTY_HISTORY: DraftHistory = { past: [], present: EMPTY_DRAFTS, future: [] };

/** Drops the drafts that just reached the server, keeping the failed ones. */
function withoutKeys<T>(record: Record<string, T>, keys: string[]): Record<string, T> {
  if (keys.length === 0) {
    return record;
  }
  const next = { ...record };
  for (const key of keys) {
    delete next[key];
  }
  return next;
}

/** Turns an angle into (-180, 180] so the stored column stays readable. */
function normaliseDegrees(degrees: number): number {
  const wrapped = ((degrees % 360) + 360) % 360;
  return wrapped > 180 ? wrapped - 360 : wrapped;
}

export function FloorDesignerPage() {
  const { buildingId, floorId } = useParams<{ buildingId: string; floorId: string }>();
  const { snapshot, loading, error } = useBuildingSnapshot(buildingId);
  const layoutItems = useFloorLayoutItems(floorId);
  const saveLayouts = useSaveRoomLayouts();
  const createItem = useCreateFloorLayoutItem(floorId ?? '');
  const updateItem = useUpdateFloorLayoutItem(floorId ?? '');
  const deleteItem = useDeleteFloorLayoutItem(floorId ?? '');
  const { hasPermission } = useAuth();

  const canDesign =
    hasPermission(BuildingPolicies.roomsManage) && hasPermission(BuildingPolicies.floorsManage);

  const [history, setHistory] = useState<DraftHistory>(EMPTY_HISTORY);
  const [selection, setSelection] = useState<DesignerSelection>(null);
  const [draft, setDraft] = useState<{ label: string; box: Box } | null>(null);
  const [saveErrors, setSaveErrors] = useState<string[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [measure, setMeasure] = useState<MeasureInfo | null>(null);

  const { rooms: roomDrafts, items: itemDrafts, removed: removedItemIds } = history.present;

  const floor = snapshot?.floors.find((item) => item.id === floorId) ?? null;
  const serverRooms = useMemo(
    () => (floorId ? (snapshot?.roomsByFloor.get(floorId) ?? []) : []),
    [snapshot, floorId],
  );

  // Server state is the baseline; drafts are everything not yet saved.
  const baselineItems = useMemo<DraftItem[]>(
    () =>
      (layoutItems.data ?? []).map((item) => ({
        id: item.id,
        kind: item.kind,
        label: item.label,
        box: itemBox(item),
        isNew: false,
        points: parsePoints(item.points),
        thickness: item.thickness,
        cornerRadius: item.cornerRadius,
      })),
    [layoutItems.data],
  );

  const allItems = useMemo(() => {
    const merged = baselineItems
      .filter((item) => !removedItemIds.includes(item.id))
      .map((item) => itemDrafts[item.id] ?? item);
    const added = Object.values(itemDrafts).filter((item) => item.isNew);
    return [...merged, ...added];
  }, [baselineItems, itemDrafts, removedItemIds]);

  const allRooms = useMemo(
    () =>
      serverRooms.map((room) => {
        const roomDraft = roomDrafts[room.id];
        return {
          room,
          box: roomDraft ? roomDraft.box : roomBox(room),
          rotation: roomDraft
            ? roomDraft.rotation
            : normaliseDegrees(room.layoutRotationDegrees ?? 0),
        };
      }),
    [serverRooms, roomDrafts],
  );

  // Bounds follow only saved geometry: if they grew while a box was being
  // dragged, the SVG viewBox would rescale mid-drag and the box would jump away
  // from the pointer. The drawing grows once the change is saved instead.
  const bounds = useMemo(
    () =>
      designerBounds([
        ...serverRooms.flatMap((room) => {
          const box = roomBox(room);
          return box ? [box] : [];
        }),
        ...baselineItems.map((item) => item.box),
      ]),
    [serverRooms, baselineItems],
  );

  const dirtyRoomIds = useMemo(
    () =>
      serverRooms
        .filter((room) => {
          const roomDraft = roomDrafts[room.id];
          if (!roomDraft) {
            return false;
          }
          const before = roomBox(room);
          const after = roomDraft.box;
          if (before === null || after === null) {
            return before !== after;
          }
          return (
            !boxesEqual(before, after) ||
            normaliseDegrees(room.layoutRotationDegrees ?? 0) !== roomDraft.rotation
          );
        })
        .map((room) => room.id),
    [serverRooms, roomDrafts],
  );

  const dirtyItemIds = useMemo(() => {
    const changed = baselineItems
      .filter((item) => {
        const draftItem = itemDrafts[item.id];
        if (!draftItem) {
          return false;
        }
        return (
          !boxesEqual(draftItem.box, item.box) ||
          draftItem.kind !== item.kind ||
          (draftItem.label ?? '') !== (item.label ?? '') ||
          (draftItem.points === null) !== (item.points === null) ||
          (draftItem.points !== null &&
            item.points !== null &&
            serializePoints(draftItem.points) !== serializePoints(item.points)) ||
          draftItem.thickness !== item.thickness ||
          draftItem.cornerRadius !== item.cornerRadius
        );
      })
      .map((item) => item.id);
    return changed;
  }, [baselineItems, itemDrafts]);

  const newItems = useMemo(
    () => Object.values(itemDrafts).filter((item) => item.isNew),
    [itemDrafts],
  );

  const dirtyCount =
    dirtyRoomIds.length + dirtyItemIds.length + newItems.length + removedItemIds.length;

  // Losing a layout to a browser refresh would be silent, so ask first.
  useEffect(() => {
    if (dirtyCount === 0) {
      return;
    }
    function onBeforeUnload(event: BeforeUnloadEvent) {
      event.preventDefault();
      event.returnValue = '';
    }
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => window.removeEventListener('beforeunload', onBeforeUnload);
  }, [dirtyCount]);

  /**
   * Every draft edit goes through here, so the history stack always holds the
   * state before the edit — that is what makes Undo a real undo rather than a
   * reset of the whole drawing.
   */
  function commitDraft(update: (current: DraftState) => DraftState) {
    setNotice(null);
    setHistory((current) => {
      const next = update(current.present);
      if (next === current.present) {
        return current;
      }
      return {
        past: [...current.past, current.present].slice(-HISTORY_LIMIT),
        present: next,
        future: [],
      };
    });
  }

  function undo() {
    setNotice(null);
    setHistory((current) => {
      const previous = current.past.at(-1);
      if (!previous) {
        return current;
      }
      return {
        past: current.past.slice(0, -1),
        present: previous,
        future: [current.present, ...current.future],
      };
    });
  }

  function redo() {
    setNotice(null);
    setHistory((current) => {
      const [next, ...rest] = current.future;
      if (!next) {
        return current;
      }
      return {
        past: [...current.past, current.present].slice(-HISTORY_LIMIT),
        present: next,
        future: rest,
      };
    });
  }

  function setRoomBox(roomId: string, box: Box | null) {
    commitDraft((current) => {
      const existing = current.rooms[roomId];
      const room = serverRooms.find((item) => item.id === roomId);
      const rotation = existing
        ? existing.rotation
        : normaliseDegrees(room?.layoutRotationDegrees ?? 0);
      return { ...current, rooms: { ...current.rooms, [roomId]: { box, rotation } } };
    });
  }

  function rotateSelectedRoom() {
    if (selection?.kind !== 'room') {
      return;
    }
    const roomId = selection.id;
    commitDraft((current) => {
      const entry = allRooms.find((candidate) => candidate.room.id === roomId);
      if (!entry || !entry.box) {
        return current;
      }
      return {
        ...current,
        rooms: {
          ...current.rooms,
          [roomId]: { box: entry.box, rotation: normaliseDegrees(entry.rotation + 90) },
        },
      };
    });
  }

  function setItemBox(itemId: string, box: Box) {
    commitDraft((current) => {
      const existing = current.items[itemId];
      const baseline = baselineItems.find((item) => item.id === itemId);
      const source = existing ?? baseline;
      if (!source) {
        return current;
      }
      // A bent wall's shape *is* its points. Dragging its box would leave the
      // two disagreeing, so a box drag shifts the centreline instead.
      if (source.points && source.points.length >= 2) {
        const dx = box.x - source.box.x + (box.width - source.box.width) / 2;
        const dy = box.y - source.box.y + (box.height - source.box.height) / 2;
        const shifted =
          dx === 0 && dy === 0 ? source.points : movePolyline(source.points, dx, dy);
        return {
          ...current,
          items: {
            ...current.items,
            [itemId]: {
              ...source,
              points: shifted,
              box: boxOfPolyline(shifted, source.thickness ?? Math.min(box.width, box.height)),
              isNew: existing?.isNew ?? false,
            },
          },
        };
      }
      return {
        ...current,
        items: {
          ...current.items,
          [itemId]: { ...source, box, isNew: existing?.isNew ?? false },
        },
      };
    });
  }

  /** Stores a new vertex list, keeping the wall's stored box in step with it. */
  function setItemPoints(itemId: string, points: PlanPoint[]) {
    commitDraft((current) => {
      const existing = current.items[itemId];
      const baseline = baselineItems.find((item) => item.id === itemId);
      const source = existing ?? baseline;
      if (!source || source.kind !== 12) {
        return current;
      }
      const thickness = source.thickness;
      return {
        ...current,
        items: {
          ...current.items,
          [itemId]: {
            ...source,
            points,
            box: boxOfPolyline(points, thickness ?? Math.min(source.box.width, source.box.height)),
            isNew: existing?.isNew ?? false,
          },
        },
      };
    });
  }

  /** Double-click on a wall: bend it at the nearest point on its run. */
  function insertWallPoint(itemId: string, point: { x: number; y: number }) {
    const source = itemDrafts[itemId] ?? baselineItems.find((item) => item.id === itemId);
    if (!source || source.kind !== 12 || !source.points) {
      return;
    }
    const result = insertPointOnPolyline(source.points, point);
    if (!result) {
      setNotice('Không chèn được điểm gãy ở đây — chỗ đó đã có một điểm khác.');
      return;
    }
    setItemPoints(itemId, result.points);
  }

  function setItemWallShape(
    itemId: string,
    patch: { thickness?: number | null; cornerRadius?: number | null },
  ) {
    commitDraft((current) => {
      const existing = current.items[itemId];
      const baseline = baselineItems.find((item) => item.id === itemId);
      const source = existing ?? baseline;
      if (!source || source.kind !== 12) {
        return current;
      }
      const thickness = patch.thickness !== undefined ? patch.thickness : source.thickness;
      const next = { ...source, ...patch };
      // Widening the band changes the box that covers it.
      const box =
        source.points && patch.thickness !== undefined
          ? boxOfPolyline(source.points, thickness ?? Math.min(source.box.width, source.box.height))
          : source.box;
      return {
        ...current,
        items: {
          ...current.items,
          [itemId]: { ...next, box, isNew: existing?.isNew ?? false },
        },
      };
    });
  }

  /** Back to a single straight band, the way a wall drawn before bends was. */
  function straightenWall(itemId: string) {
    commitDraft((current) => {
      const existing = current.items[itemId];
      const baseline = baselineItems.find((item) => item.id === itemId);
      const source = existing ?? baseline;
      if (!source || source.kind !== 12 || !source.points || source.points.length < 2) {
        return current;
      }
      const first = source.points[0]!;
      const last = source.points[source.points.length - 1]!;
      const box = boxOfPolyline(
        [first, last],
        source.thickness ?? Math.min(source.box.width, source.box.height),
      );
      return {
        ...current,
        items: {
          ...current.items,
          [itemId]: {
            ...source,
            points: [first, last],
            box,
            cornerRadius: 0,
            isNew: existing?.isNew ?? false,
          },
        },
      };
    });
  }

  function setItemLabel(itemId: string, label: string | null) {
    commitDraft((current) => {
      const existing = current.items[itemId];
      const baseline = baselineItems.find((item) => item.id === itemId);
      const source = existing ?? baseline;
      if (!source) {
        return current;
      }
      return { ...current, items: { ...current.items, [itemId]: { ...source, label } } };
    });
  }

  function dropRoom(roomId: string, point: { x: number; y: number }) {
    const room = serverRooms.find((item) => item.id === roomId);
    if (!room) {
      return;
    }
    const existing = allRooms.find((entry) => entry.room.id === roomId)?.box;
    const size = existing ?? defaultRoomBox(bounds, DEFAULT_ROOM_SIZE.width, DEFAULT_ROOM_SIZE.height);
    const box = clampBoxToBounds(
      {
        width: size.width,
        height: size.height,
        x: snapToGrid(point.x - size.width / 2),
        y: snapToGrid(point.y - size.height / 2),
      },
      bounds,
    );
    setRoomBox(roomId, box);
    setSelection({ kind: 'room', id: roomId });
  }

  function dropItem(kind: FloorLayoutItemKind, point: { x: number; y: number }) {
    const size = ITEM_DEFAULT_SIZE[kind] ?? { width: 2, height: 2 };
    const box = clampBoxToBounds(
      {
        ...size,
        x: snapToGrid(point.x - size.width / 2),
        y: snapToGrid(point.y - size.height / 2),
      },
      bounds,
    );
    const id = `new-${crypto.randomUUID()}`;
    // A wall comes with a straight centreline down the middle of its box, so a
    // newly dropped wall is already bendable: one double-click adds the corner.
    const points =
      kind === 12
        ? [
            { x: box.x, y: snapToGrid(box.y + box.height / 2) },
            { x: snapToGrid(box.x + box.width), y: snapToGrid(box.y + box.height / 2) },
          ]
        : null;
    const thickness = kind === 12 ? snapToGrid(box.height) : null;
    commitDraft((current) => ({
      ...current,
      items: {
        ...current.items,
        [id]: {
          id,
          kind,
          label: null,
          box,
          isNew: true,
          points,
          thickness,
          cornerRadius: 0,
        },
      },
    }));
    setSelection({ kind: 'item', id });
  }

  function removeSelectedItem(itemId: string) {
    commitDraft((current) => {
      const draftItem = current.items[itemId];
      if (draftItem?.isNew) {
        return { ...current, items: withoutKeys(current.items, [itemId]) };
      }
      return { ...current, removed: [...current.removed, itemId] };
    });
    setSelection(null);
  }

  function deleteSelection() {
    if (!selection) {
      return;
    }
    if (selection.kind === 'item') {
      removeSelectedItem(selection.id);
      return;
    }
    // Removing a room from the plan is a draft like any other, so Undo restores it.
    setRoomBox(selection.id, null);
    setSelection(null);
  }

  /** Throws the whole draft away — the one action Undo must not be confused with. */
  function discardDrafts() {
    setHistory({ past: [], present: EMPTY_DRAFTS, future: [] });
    setSaveErrors([]);
    setNotice(null);
  }

  async function saveAll() {
    setSaving(true);
    setSaveErrors([]);
    setNotice(null);
    const failures: string[] = [];

    // Each hook awaits its own list refetch, so by the time these land the
    // server state is already in the cache and dropping the draft is seamless.
    const savedRooms: string[] = [];
    const savedUpdates: string[] = [];
    const savedNewItems: string[] = [];
    const savedDeletes: string[] = [];

    try {
      for (const roomId of dirtyRoomIds) {
        const room = serverRooms.find((item) => item.id === roomId);
        const roomDraft = roomDrafts[roomId];
        const box = roomDraft?.box ?? null;
        try {
          await saveLayouts.mutateAsync({
            roomId,
            input: box
              ? {
                  x: box.x,
                  y: box.y,
                  width: box.width,
                  height: box.height,
                  // A room drawn as a polygon keeps its extra fields; the
                  // designer only ever edits the bounding rectangle.
                  shape: room?.layoutShape ?? 1,
                  rotationDegrees: roomDraft?.rotation ?? 0,
                  points: room?.layoutPoints ?? null,
                }
              : {
                  x: null,
                  y: null,
                  width: null,
                  height: null,
                  shape: null,
                  rotationDegrees: null,
                  points: null,
                },
          });
          savedRooms.push(roomId);
        } catch (mutationError) {
          const view = describeMutationError(mutationError, 'layout', 'edit');
          failures.push(`Phòng ${room?.roomNumber ?? roomId}: ${view?.formError ?? 'lỗi'}`);
        }
      }

      for (const itemId of dirtyItemIds) {
        if (removedItemIds.includes(itemId)) {
          continue;
        }
        const item = itemDrafts[itemId];
        if (!item) {
          continue;
        }        try {
          await updateItem.mutateAsync({
            itemId,
            input: {
              kind: item.kind,
              label: item.label,
              x: item.box.x,
              y: item.box.y,
              width: item.box.width,
              height: item.box.height,
              points: item.points ? serializePoints(item.points) : null,
              thickness: item.thickness,
              cornerRadius: item.cornerRadius,
            },
          });
          savedUpdates.push(itemId);
        } catch (mutationError) {
          const view = describeMutationError(mutationError, 'layout', 'edit');
          failures.push(
            `Vật thể «${item.label ?? floorLayoutKindMeta[item.kind]?.label ?? itemId}»: ${
              view?.formError ?? 'lỗi'
            }`,
          );
        }
      }

      for (const item of newItems) {
        try {
          await createItem.mutateAsync({
            kind: item.kind,
            label: item.label,
            x: item.box.x,
            y: item.box.y,
            width: item.box.width,
            height: item.box.height,
            points: item.points ? serializePoints(item.points) : null,
            thickness: item.thickness,
            cornerRadius: item.cornerRadius,
          });
          // Dropping a created draft matters more than the others: leaving it
          // behind would draw it twice and create a duplicate on the next save.
          savedNewItems.push(item.id);
        } catch (mutationError) {
          const view = describeMutationError(mutationError, 'layout', 'create');
          failures.push(
            `Vật thể mới «${floorLayoutKindMeta[item.kind]?.label ?? 'vật thể'}»: ${
              view?.formError ?? 'lỗi'
            }`,
          );
        }
      }

      for (const itemId of removedItemIds) {
        try {
          await deleteItem.mutateAsync(itemId);
          savedDeletes.push(itemId);
        } catch (mutationError) {
          const view = describeMutationError(mutationError, 'layout', 'delete');
          failures.push(`Xoá vật thể: ${view?.formError ?? 'lỗi'}`);
        }
      }
    } finally {
      setSaving(false);
    }

    // Saved drafts leave the stack for good: their baseline is now the server
    // state, and undoing back to a stale box would show a drawing the server
    // no longer agrees with.
    setHistory((current) => ({
      past: [],
      present: {
        rooms: withoutKeys(current.present.rooms, savedRooms),
        items: withoutKeys(current.present.items, [...savedUpdates, ...savedNewItems]),
        removed: current.present.removed.filter((id) => !savedDeletes.includes(id)),
      },
      future: [],
    }));

    if (failures.length === 0) {
      setNotice('Đã lưu sơ đồ tầng.');
      return;
    }
    setSaveErrors(failures);
  }

  if (loading) {
    return (
      <div className="mx-auto max-w-[1720px]">
        <div className="flex items-center gap-2.5 rounded-xl border border-outline-variant bg-surface-lowest px-5 py-8 text-sm text-on-surface-variant">
          <span className="material-symbols-outlined animate-spin text-lg">sync</span>
          Đang tải sơ đồ tầng…
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="mx-auto max-w-[1720px]">
        <Alert tone={error instanceof ApiError && error.status === 403 ? 'warning' : 'error'}>
          {error instanceof ApiError && error.status === 403
            ? 'Máy chủ đang từ chối truy cập dữ liệu tòa nhà (403).'
            : error instanceof ApiError
              ? error.message
              : 'Không tải được sơ đồ tầng.'}
        </Alert>
      </div>
    );
  }

  if (!floor) {
    return (
      <div className="mx-auto max-w-[1720px]">
        <EmptyState
          icon="layers"
          title="Không tìm thấy tầng"
          description="Tầng này không tồn tại trong tòa nhà hoặc bạn không có quyền truy cập."
          action={
            <Link
              to={buildingId ? `/buildings/${buildingId}` : '/buildings'}
              className="text-sm font-semibold text-primary hover:underline"
            >
              Quay lại tòa nhà
            </Link>
          }
        />
      </div>
    );
  }

  const selectedRoom =
    selection?.kind === 'room'
      ? (serverRooms.find((room) => room.id === selection.id) ?? null)
      : null;
  const selectedItem: DesignerItem | null = selection?.kind === 'item'
    ? (() => {
        const found = allItems.find((entry) => entry.id === selection.id);
        if (!found) {
          return null;
        }
        return {
          id: found.id,
          kind: found.kind,
          label: found.label,
          points: found.points,
          thickness: found.thickness,
          cornerRadius: found.cornerRadius,
          ...found.box,
        };
      })()
    : null;
  const selectedWall = selectedItem?.kind === 12 ? selectedItem : null;

  return (
    <div className="mx-auto max-w-[1500px]">
      <PageHeader
        title={`Thiết kế sơ đồ — Tầng ${floor.floorNumber}`}
        description="Kéo phòng và vật thể, đổi kích thước, rồi lưu lại."
        breadcrumb={[
          'Trang chủ',
          'Tòa nhà',
          snapshot?.building.name ?? '...',
          `Tầng ${floor.floorNumber}`,
          'Thiết kế sơ đồ',
        ]}
        actions={
          <>
            <Link
              to={`/buildings/${buildingId}/floors/${floorId}`}
              className="flex h-10 items-center gap-2 rounded-lg border border-outline-variant px-4 text-sm font-semibold text-on-surface-variant transition hover:bg-surface-low"
            >
              <span className="material-symbols-outlined text-lg">map</span>
              Xem sơ đồ
            </Link>
            <div className="flex items-center gap-1 rounded-lg border border-outline-variant p-0.5">
              <button
                type="button"
                onClick={undo}
                disabled={history.past.length === 0 || saving}
                aria-label="Hoàn tác bước trước"
                title={
                  history.past.length === 0
                    ? 'Chưa có thay đổi nào để hoàn tác'
                    : `Hoàn tác (còn ${history.past.length} bước)`
                }
                className="flex h-9 w-9 items-center justify-center rounded-md text-on-surface-variant transition hover:bg-surface-low disabled:opacity-40"
              >
                <span className="material-symbols-outlined text-lg">undo</span>
              </button>
              <button
                type="button"
                onClick={redo}
                disabled={history.future.length === 0 || saving}
                aria-label="Làm lại bước vừa hoàn tác"
                title={
                  history.future.length === 0
                    ? 'Không còn bước nào để làm lại'
                    : `Làm lại (còn ${history.future.length} bước)`
                }
                className="flex h-9 w-9 items-center justify-center rounded-md text-on-surface-variant transition hover:bg-surface-low disabled:opacity-40"
              >
                <span className="material-symbols-outlined text-lg">redo</span>
              </button>
            </div>
            <Button
              variant="secondary"
              onClick={discardDrafts}
              disabled={dirtyCount === 0 || saving}
              title="Bỏ toàn bộ thay đổi chưa lưu — khác với Hoàn tác từng bước"
            >
              Bỏ thay đổi
            </Button>
            <Button
              onClick={saveAll}
              loading={saving}
              disabled={!canDesign || dirtyCount === 0}
              title={canDesign ? undefined : 'Tài khoản này chưa được cấp quyền thiết kế sơ đồ.'}
            >
              Lưu sơ đồ{dirtyCount > 0 ? ` (${dirtyCount})` : ''}
            </Button>
          </>
        }
      />

      {!canDesign && (
        <div className="mb-4">
          <Alert tone="warning">
            Tài khoản này chưa được cấp cả quyền quản lý phòng và quản lý tầng, nên chỉ xem được
            sơ đồ. Mọi thay đổi trên màn này sẽ bị máy chủ từ chối.
          </Alert>
        </div>
      )}
      {notice && (
        <div className="mb-4">
          <Alert tone="success">{notice}</Alert>
        </div>
      )}
      {saveErrors.length > 0 && (
        <div className="mb-4">
          <Alert tone="error">
            <p className="font-semibold">Một số thay đổi chưa lưu được:</p>
            <ul className="mt-1 list-disc pl-5">
              {saveErrors.map((message) => (
                <li key={message}>{message}</li>
              ))}
            </ul>
          </Alert>
        </div>
      )}

      <div className="grid gap-4 xl:grid-cols-[230px_minmax(0,1fr)_290px]">
        <Palette
          rooms={serverRooms}
          placedIds={new Set(allRooms.filter((entry) => entry.box).map((entry) => entry.room.id))}
          canDesign={canDesign}
        />

        {/* Below xl the columns stack; the canvas goes first so the drawing is
            not pushed off-screen by the room and object palettes. */}
        <section className="order-first rounded-xl border border-outline-variant bg-surface-lowest p-4 xl:order-none">
          <FloorDesignerCanvas
            rooms={allRooms.map<DesignerRoom>(({ room, box, rotation }) => ({
              id: room.id,
              roomNumber: room.roomNumber,
              area: room.area,
              status: room.status,
              box,
              rotationDegrees: rotation,
            }))}
            items={allItems.map<DesignerItem>((item) => ({
              id: item.id,
              kind: item.kind,
              label: item.label,
              points: item.points,
              thickness: item.thickness,
              cornerRadius: item.cornerRadius,
              ...item.box,
            }))}
            bounds={bounds}
            selection={selection}
            onSelect={setSelection}
            onChangeRoom={(roomId, box) => setRoomBox(roomId, box)}
            onChangeItem={setItemBox}
            onDropRoom={dropRoom}
            onDropItem={dropItem}
            onInsertWallPoint={canDesign ? insertWallPoint : undefined}
            onChangeItemPoints={canDesign ? setItemPoints : undefined}
            onDraftChange={setDraft}
            onRotate={canDesign ? rotateSelectedRoom : undefined}
            onDeleteSelection={canDesign ? deleteSelection : undefined}
            onMeasure={setMeasure}
          />
          <p className="mt-3 rounded-lg bg-surface-low px-3 py-2 text-[11px] leading-relaxed text-on-surface-variant">
            {draft
              ? `${draft.label}: x ${formatMetres(draft.box.x)}, y ${formatMetres(draft.box.y)} · ${formatMetres(draft.box.width)} × ${formatMetres(draft.box.height)}`
              : measure
                ? `Đo được ${formatMetres(measure.distance)} (Δx ${formatMetres(Math.abs(measure.dx))}, Δy ${formatMetres(Math.abs(measure.dy))}).`
                : 'Mười hai loại vật thể (hành lang, cầu thang, thang máy, nhà vệ sinh, ban công, cây xanh, tường…) vẽ bằng ký hiệu kiến trúc riêng. Tường là một vật thể bạn tự kéo ra, hình dạng do các điểm gãy quyết định: nhấp đúp lên tường để thêm điểm, kéo điểm tròn để bẻ góc, và chỉnh độ dày cùng bán kính bo ở bảng bên phải. Phòng vẫn là hình chữ nhật; vẽ tay đa giác chưa hỗ trợ.'}
          </p>
        </section>

        <PropertyPanel
          selection={selection}
          room={selectedRoom}
          item={selectedItem}
          roomBox={selectedRoom ? (allRooms.find((e) => e.room.id === selectedRoom.id)?.box ?? null) : null}
          rotation={
            selectedRoom
              ? (allRooms.find((e) => e.room.id === selectedRoom.id)?.rotation ?? 0)
              : 0
          }
          canDesign={canDesign}
          onChangeRoomBox={(box) => selectedRoom && setRoomBox(selectedRoom.id, box)}
          onChangeItemLabel={(label) => selectedItem && setItemLabel(selectedItem.id, label)}
          onChangeItemBox={(box) => selectedItem && setItemBox(selectedItem.id, box)}
          wall={selectedWall}
          onChangeWallThickness={(value) =>
            selectedItem && setItemWallShape(selectedItem.id, { thickness: value })
          }
          onChangeWallCornerRadius={(value) =>
            selectedItem && setItemWallShape(selectedItem.id, { cornerRadius: value })
          }
          onStraightenWall={() => selectedItem && straightenWall(selectedItem.id)}
          onRotate={rotateSelectedRoom}
          onRemoveFromPlan={() => selectedRoom && setRoomBox(selectedRoom.id, null)}
          onDeleteItem={() => selectedItem && removeSelectedItem(selectedItem.id)}
        />
      </div>
    </div>
  );
}

function Palette({
  rooms,
  placedIds,
  canDesign,
}: {
  rooms: RoomDto[];
  placedIds: Set<string>;
  canDesign: boolean;
}) {
  return (
    <aside className="flex flex-col gap-4">
      <section className="rounded-xl border border-outline-variant bg-surface-lowest p-4">
        <h2 className="mb-2 font-display text-sm font-bold text-on-surface">Phòng của tầng</h2>
        <p className="mb-2 text-[11px] leading-relaxed text-on-surface-variant">
          {`Kéo một phòng vào sơ đồ để đặt vị trí. ${placedIds.size}/${rooms.length} phòng đã đặt.`}
        </p>
        <ul className="flex max-h-[300px] flex-col gap-1.5 overflow-y-auto">
          {rooms.map((room) => {
            const placed = placedIds.has(room.id);
            return (
              <li key={room.id}>
                <div
                  draggable={canDesign}
                  onDragStart={(event) => {
                    event.dataTransfer.setData('application/x-room-id', room.id);
                    event.dataTransfer.effectAllowed = 'copy';
                  }}
                  className={
                    'flex items-center justify-between gap-2 rounded-lg border px-2.5 py-2 text-xs transition ' +
                    (canDesign ? 'cursor-grab ' : 'cursor-not-allowed opacity-60 ') +
                    (placed
                      ? 'border-outline-variant bg-surface-lowest hover:border-primary/40'
                      : 'border-dashed border-[#EBCB7A] bg-[#FFFBEB]')
                  }
                  title={placed ? 'Đã có trên sơ đồ' : 'Chưa có toạ độ — kéo vào sơ đồ'}
                >
                  <span className="font-semibold text-on-surface">{room.roomNumber}</span>
                  <span className="text-[11px] text-on-surface-variant">
                    {placed ? 'Đã đặt' : 'Chưa đặt'}
                  </span>
                </div>
              </li>
            );
          })}
        </ul>
      </section>

      <section className="rounded-xl border border-outline-variant bg-surface-lowest p-4">
        <h2 className="mb-2 font-display text-sm font-bold text-on-surface">Thành phần</h2>
        <p className="mb-3 text-[11px] leading-relaxed text-on-surface-variant">
          Kéo vào sơ đồ để thêm hành lang, cầu thang, kho, ban công, cây xanh, tường, và
          đồ nội thất đặt trong phòng.
        </p>
        {(
          [
            { group: 'architecture', title: 'Kiến trúc' },
            { group: 'furniture', title: 'Nội thất trong phòng' },
          ] as const
        ).map(({ group, title }) => {
          const kinds = (Object.keys(floorLayoutKindMeta) as unknown as string[])
            .map((key) => Number(key) as FloorLayoutItemKind)
            .filter((kind) => floorLayoutKindGroup[kind] === group);
          return (
            <div key={group} className="mb-3 last:mb-0">
              <h3 className="mb-1.5 text-[11px] font-semibold tracking-wide text-on-surface-variant uppercase">
                {title}
              </h3>
              <ul className="grid grid-cols-2 gap-1.5">
                {kinds.map((kind) => {
                  const meta = floorLayoutKindMeta[kind];
                  return (
                    <li key={kind}>
                      <div
                        draggable={canDesign}
                        onDragStart={(event) => {
                          event.dataTransfer.setData('application/x-item-kind', String(kind));
                          event.dataTransfer.effectAllowed = 'copy';
                        }}
                        className={
                          'flex items-center gap-1.5 rounded-lg border border-outline-variant px-2 py-1.5 text-[11px] text-on-surface-variant transition ' +
                          (canDesign
                            ? 'cursor-grab hover:border-primary/40'
                            : 'cursor-not-allowed opacity-60')
                        }
                      >
                        <span className="material-symbols-outlined text-base">{meta.icon}</span>
                        {meta.label}
                      </div>
                    </li>
                  );
                })}
              </ul>
            </div>
          );
        })}
      </section>
    </aside>
  );
}

function PropertyPanel({
  selection,
  room,
  item,
  roomBox,
  rotation,
  canDesign,
  wall,
  onChangeRoomBox,
  onChangeItemLabel,
  onChangeItemBox,
  onChangeWallThickness,
  onChangeWallCornerRadius,
  onStraightenWall,
  onRotate,
  onRemoveFromPlan,
  onDeleteItem,
}: {
  selection: DesignerSelection;
  room: RoomDto | null;
  item: DesignerItem | null;
  roomBox: Box | null;
  rotation: number;
  canDesign: boolean;
  /** Set when the selected object is a wall with a centreline. */
  wall: DesignerItem | null;
  onChangeRoomBox: (box: Box) => void;
  onChangeItemLabel: (label: string | null) => void;
  onChangeItemBox: (box: Box) => void;
  onChangeWallThickness: (value: number | null) => void;
  onChangeWallCornerRadius: (value: number | null) => void;
  onStraightenWall: () => void;
  onRotate: () => void;
  onRemoveFromPlan: () => void;
  onDeleteItem: () => void;
}) {
  if (!selection) {
    return (
      <aside className="rounded-xl border border-outline-variant bg-surface-lowest p-5">
        <div className="flex h-full flex-col items-center justify-center gap-2 py-10 text-center">
          <span className="material-symbols-outlined text-3xl text-outline">touch_app</span>
          <p className="text-sm font-semibold text-on-surface">Chọn một phòng hoặc vật thể</p>
          <p className="max-w-[220px] text-xs leading-relaxed text-on-surface-variant">
            Thuộc tính và toạ độ sẽ hiện ở đây để chỉnh bằng số.
          </p>
        </div>
      </aside>
    );
  }

  if (room) {
    const meta = roomStatusMeta[room.status];
    return (
      <aside className="rounded-xl border border-outline-variant bg-surface-lowest p-5">
        <div className="mb-3 flex items-start justify-between gap-2">
          <div>
            <p className="text-[11px] font-semibold tracking-[0.06em] text-on-surface-variant uppercase">
              Thông tin phòng
            </p>
            <h2 className="mt-0.5 font-display text-lg font-bold text-on-surface">
              {room.roomNumber}
            </h2>
          </div>
          <StatusBadge tone={meta?.tone ?? 'neutral'} withDot>
            {meta?.label ?? '—'}
          </StatusBadge>
        </div>

        {/* Read-only profile, the same rows mockup 3 shows above the geometry. */}
        <dl className="mb-4 flex flex-col gap-1.5 border-b border-outline-variant pb-3 text-xs">
          <SummaryRow label="Tên phòng" value={room.name} />
          <SummaryRow label="Diện tích" value={room.area ? `${room.area} m²` : '—'} />
          <SummaryRow
            label="Giá thuê"
            value={room.monthlyRent === null ? '—' : `${formatVnd(room.monthlyRent)}/tháng`}
          />
          <SummaryRow label="Sức chứa" value={`${room.capacity} người`} />
          <SummaryRow
            label="Hình dạng"
            value={
              room.layoutShape === 2 ? 'Đa giác' : room.layoutShape === 1 ? 'Hình chữ nhật' : '—'
            }
          />
        </dl>

        {roomBox ? (
          <>
            <BoxFields
              box={roomBox}
              disabled={!canDesign}
              onChange={onChangeRoomBox}
            />
            <div className="mt-3 flex items-center justify-between gap-2 rounded-lg bg-surface-low px-3 py-2">
              <div>
                <p className="text-[11px] font-semibold text-on-surface">Hướng xoay</p>
                <p className="tnum text-xs text-on-surface-variant">{rotation}°</p>
              </div>
              <button
                type="button"
                onClick={onRotate}
                disabled={!canDesign}
                className="flex h-8 items-center gap-1 rounded-md border border-outline-variant bg-surface-lowest px-2.5 text-[11px] font-semibold text-on-surface-variant transition hover:bg-surface-low disabled:opacity-40"
              >
                <span className="material-symbols-outlined text-base">rotate_90_degrees_cw</span>
                Xoay 90°
              </button>
            </div>
          </>
        ) : (
          <p className="rounded-lg bg-[#FFFBEB] px-3 py-2 text-xs leading-relaxed text-[#B45309]">
            Phòng này chưa có toạ độ. Kéo từ danh sách bên trái vào sơ đồ để đặt vị trí.
          </p>
        )}

        <p className="mt-3 text-[11px] leading-relaxed text-on-surface-variant">
          Diện tích khai báo và kích thước vẽ là hai thông tin độc lập — sơ đồ không tự sửa diện
          tích.
          {room.description ? ` Ghi chú: ${room.description}` : ''}
        </p>

        {canDesign && roomBox && (
          <button
            type="button"
            onClick={onRemoveFromPlan}
            className="mt-4 flex h-9 w-full items-center justify-center gap-1.5 rounded-lg border border-[#FECDD3] text-xs font-semibold text-[#B91C1C] transition hover:bg-[#FFF1F2]"
          >
            <span className="material-symbols-outlined text-base">remove_circle</span>
            Bỏ phòng khỏi sơ đồ
          </button>
        )}
      </aside>
    );
  }

  if (!item) {
    return <aside className="rounded-xl border border-outline-variant bg-surface-lowest p-5" />;
  }

  return (
    <aside className="rounded-xl border border-outline-variant bg-surface-lowest p-5">
      <p className="text-[11px] font-semibold tracking-[0.06em] text-on-surface-variant uppercase">
        Vật thể
      </p>
      <h2 className="mt-0.5 mb-3 font-display text-lg font-bold text-on-surface">
        {floorLayoutKindMeta[item.kind]?.label ?? 'Vật thể'}
      </h2>

      <div className="mb-3 flex flex-col gap-2">
        <Field
          label="Nhãn hiển thị"
          value={item.label ?? ''}
          disabled={!canDesign}
          maxLength={64}
          placeholder={floorLayoutKindMeta[item.kind]?.label ?? ''}
          onChange={(event) => onChangeItemLabel(event.target.value || null)}
          hint="Để trống sẽ dùng tên loại vật thể."
        />
      </div>

      <BoxFields box={item} disabled={!canDesign} onChange={onChangeItemBox} />

      {wall && (
        <WallControls
          wall={wall}
          disabled={!canDesign}
          onChangeThickness={onChangeWallThickness}
          onChangeCornerRadius={onChangeWallCornerRadius}
          onStraighten={onStraightenWall}
        />
      )}

      {canDesign && (
        <button
          type="button"
          onClick={onDeleteItem}
          className="mt-4 flex h-9 w-full items-center justify-center gap-1.5 rounded-lg border border-[#FECDD3] text-xs font-semibold text-[#B91C1C] transition hover:bg-[#FFF1F2]"
        >
          <span className="material-symbols-outlined text-base">delete</span>
          Xoá vật thể
        </button>
      )}
    </aside>
  );
}

/**
 * Wall shape controls: how thick the band is, and whether a bend is chamfered
 * the way the mockup draws it or rounded off with an arc.
 *
 * The mockup's storey outline is chamfered at every corner, so the radius
 * starts at zero and the owner raises it when a wall really is curved. The
 * thickness matters for the same reason the mockup draws two: an exterior wall
 * is a solid band, a partition between two rooms is a thin one.
 */
function WallControls({
  wall,
  disabled,
  onChangeThickness,
  onChangeCornerRadius,
  onStraighten,
}: {
  wall: DesignerItem;
  disabled: boolean;
  onChangeThickness: (value: number | null) => void;
  onChangeCornerRadius: (value: number | null) => void;
  onStraighten: () => void;
}) {
  const vertexCount = wall.points?.length ?? 0;
  const thickness = wall.thickness ?? Math.min(wall.width, wall.height);
  const cornerRadius = wall.cornerRadius ?? 0;

  return (
    <div className="mt-4 flex flex-col gap-3 rounded-lg border border-outline-variant bg-surface-low p-3">
      <div>
        <div className="flex items-center justify-between gap-2">
          <h3 className="text-[11px] font-semibold tracking-[0.06em] text-on-surface-variant uppercase">
            Hình dạng tường
          </h3>
          <span className="tnum text-[11px] text-on-surface-variant">{vertexCount} điểm</span>
        </div>
        <p className="mt-1 text-[11px] leading-relaxed text-on-surface-variant">
          Nhấp đúp lên tường để thêm điểm gãy, rồi kéo điểm tròn cho tới khi tường khớp hình.
        </p>
      </div>

      <Field
        label="Độ dày (m)"
        type="number"
        step={0.02}
        min={0.05}
        max={2}
        value={String(thickness)}
        disabled={disabled}
        hint="Tường bao ngoài dày hơn vách ngăn giữa hai phòng."
        onChange={(event) => {
          const parsed = Number(event.target.value);
          if (!Number.isFinite(parsed)) {
            return;
          }
          onChangeThickness(Math.min(2, Math.max(0.05, Number(parsed.toFixed(2)))));
        }}
      />

      <div>
        <label
          htmlFor="wall-corner-radius"
          className="mb-1.5 block text-[11px] font-semibold tracking-[0.06em] text-on-surface-variant uppercase"
        >
          Bo góc (m)
        </label>
        <div className="flex items-center gap-2">
          <input
            id="wall-corner-radius"
            type="range"
            min={0}
            max={2}
            step={0.05}
            value={cornerRadius}
            disabled={disabled}
            aria-label="Bán kính bo góc tường"
            onChange={(event) => onChangeCornerRadius(Number(event.target.value))}
            className="h-1.5 flex-1 accent-[#0f766e] disabled:opacity-40"
          />
          <span className="tnum w-12 text-right text-xs text-on-surface">
            {formatMetres(cornerRadius)}
          </span>
        </div>
        <p className="mt-1 text-[11px] leading-relaxed text-on-surface-variant">
          Bằng 0 thì mỗi chỗ gãy là một góc vát sắc, đúng như mockup.
        </p>
      </div>

      <button
        type="button"
        onClick={onStraighten}
        disabled={disabled || vertexCount <= 2}
        title={
          vertexCount <= 2
            ? 'Tường này đã là một đường thẳng'
            : 'Bỏ hết điểm gãy, giữ lại hai đầu'
        }
        className="flex h-8 items-center justify-center gap-1.5 rounded-md border border-outline-variant bg-surface-lowest text-[11px] font-semibold text-on-surface-variant transition hover:bg-surface-low disabled:opacity-40"
      >
        <span className="material-symbols-outlined text-base">straighten</span>
        Về đường thẳng
      </button>
    </div>
  );
}

/** Numeric geometry editor; every keystroke is snapped so the plan matches. */
function BoxFields({
  box,
  disabled,
  onChange,
}: {
  box: Box;
  disabled: boolean;
  onChange: (box: Box) => void;
}) {
  const [localError, setLocalError] = useState<string | null>(null);

  function field(label: string, key: keyof Box) {
    return (
      <Field
        label={label}
        type="number"
        step={0.1}
        value={String(box[key])}
        disabled={disabled}
        min={key === 'width' || key === 'height' ? MIN_SIZE_METRES : -10000}
        max={10000}
        onChange={(event) => {
          const parsed = Number(event.target.value);
          if (!Number.isFinite(parsed)) {
            setLocalError('Chỉ nhập được số.');
            return;
          }
          setLocalError(null);
          const next = { ...box };
          if (key === 'width' || key === 'height') {
            next[key] = Math.max(MIN_SIZE_METRES, snapToGrid(parsed));
          } else {
            next[key] = snapToGrid(parsed);
          }
          onChange(next);
        }}
      />
    );
  }

  return (
    <div className="flex flex-col gap-2">
      <div className="grid grid-cols-2 gap-2">
        {field('X (m)', 'x')}
        {field('Y (m)', 'y')}
      </div>
      <div className="grid grid-cols-2 gap-2">
        {field('Rộng (m)', 'width')}
        {field('Cao (m)', 'height')}
      </div>
      {localError && <Alert tone="error">{localError}</Alert>}
    </div>
  );
}

function SummaryRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-baseline justify-between gap-3">
      <dt className="shrink-0 text-on-surface-variant">{label}</dt>
      <dd className="truncate text-right font-medium text-on-surface" title={value}>
        {value}
      </dd>
    </div>
  );
}
