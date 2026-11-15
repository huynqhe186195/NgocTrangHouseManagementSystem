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
import { floorLayoutKindMeta, roomStatusMeta } from '../../lib/api/types';
import type { FloorLayoutItemDto, FloorLayoutItemKind, RoomDto } from '../../lib/api/types';
import {
  MIN_SIZE_METRES,
  boxesEqual,
  clampBoxToBounds,
  defaultRoomBox,
  formatMetres,
  hasGeometry,
  snapToGrid,
  type Box,
  type PlanBounds,
} from '../../lib/floorGeometry';
import {
  FloorDesignerCanvas,
  type DesignerItem,
  type DesignerRoom,
  type DesignerSelection,
} from './FloorDesignerCanvas';

const PADDING_METRES = 1.5;
/** Corridors are the one object that reads wrong at a square default size. */
const CORRIDOR_KIND: FloorLayoutItemKind = 1;
const DEFAULT_ROOM_SIZE = { width: 4, height: 3.5 };
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
}

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

  const [roomDrafts, setRoomDrafts] = useState<Record<string, Box | null>>({});
  const [itemDrafts, setItemDrafts] = useState<Record<string, DraftItem>>({});
  const [removedItemIds, setRemovedItemIds] = useState<string[]>([]);
  const [selection, setSelection] = useState<DesignerSelection>(null);
  const [draft, setDraft] = useState<{ label: string; box: Box } | null>(null);
  const [saveErrors, setSaveErrors] = useState<string[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

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
      serverRooms.map((room) => ({
        room,
        box: room.id in roomDrafts ? roomDrafts[room.id]! : roomBox(room),
      })),
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
          if (!(room.id in roomDrafts)) {
            return false;
          }
          const before = roomBox(room);
          const after = roomDrafts[room.id]!;
          if (before === null || after === null) {
            return before !== after;
          }
          return !boxesEqual(before, after);
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
          (draftItem.label ?? '') !== (item.label ?? '')
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

  function setRoomBox(roomId: string, box: Box | null) {
    setNotice(null);
    setRoomDrafts((current) => ({ ...current, [roomId]: box }));
  }

  function setItemBox(itemId: string, box: Box) {
    setNotice(null);
    setItemDrafts((current) => {
      const existing = current[itemId];
      const baseline = baselineItems.find((item) => item.id === itemId);
      const source = existing ?? baseline;
      if (!source) {
        return current;
      }
      return { ...current, [itemId]: { ...source, box, isNew: existing?.isNew ?? false } };
    });
  }

  function setItemLabel(itemId: string, label: string | null) {
    setNotice(null);
    setItemDrafts((current) => {
      const existing = current[itemId];
      const baseline = baselineItems.find((item) => item.id === itemId);
      const source = existing ?? baseline;
      if (!source) {
        return current;
      }
      return { ...current, [itemId]: { ...source, label } };
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
    const isCorridor = kind === CORRIDOR_KIND;
    const size = isCorridor ? { width: 6, height: 1.4 } : { width: 1.6, height: 1.6 };
    const box = clampBoxToBounds(
      {
        ...size,
        x: snapToGrid(point.x - size.width / 2),
        y: snapToGrid(point.y - size.height / 2),
      },
      bounds,
    );
    const id = `new-${crypto.randomUUID()}`;
    setItemDrafts((current) => ({
      ...current,
      [id]: { id, kind, label: null, box, isNew: true },
    }));
    setSelection({ kind: 'item', id });
  }

  function removeSelectedItem(itemId: string) {
    const draftItem = itemDrafts[itemId];
    if (draftItem?.isNew) {
      setItemDrafts((current) => {
        const next = { ...current };
        delete next[itemId];
        return next;
      });
    } else {
      setRemovedItemIds((current) => [...current, itemId]);
    }
    setSelection(null);
  }

  function resetDrafts() {
    setRoomDrafts({});
    setItemDrafts({});
    setRemovedItemIds([]);
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
        const box = roomDrafts[roomId]!;
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
                  rotationDegrees: room?.layoutRotationDegrees ?? 0,
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
        }
        try {
          await updateItem.mutateAsync({
            itemId,
            input: {
              kind: item.kind,
              label: item.label,
              x: item.box.x,
              y: item.box.y,
              width: item.box.width,
              height: item.box.height,
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

    setRoomDrafts((current) => withoutKeys(current, savedRooms));
    setItemDrafts((current) => withoutKeys(current, [...savedUpdates, ...savedNewItems]));
    setRemovedItemIds((current) => current.filter((id) => !savedDeletes.includes(id)));

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
        return { id: found.id, kind: found.kind, label: found.label, ...found.box };
      })()
    : null;

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
            <Button
              variant="secondary"
              onClick={resetDrafts}
              disabled={dirtyCount === 0 || saving}
            >
              Hoàn tác thay đổi
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

        <section className="rounded-xl border border-outline-variant bg-surface-lowest p-4">
          <FloorDesignerCanvas
            rooms={allRooms.map<DesignerRoom>(({ room, box }) => ({
              id: room.id,
              roomNumber: room.roomNumber,
              area: room.area,
              status: room.status,
              box,
            }))}
            items={allItems.map<DesignerItem>((item) => ({
              id: item.id,
              kind: item.kind,
              label: item.label,
              ...item.box,
            }))}
            bounds={bounds}
            selection={selection}
            onSelect={setSelection}
            onChangeRoom={(roomId, box) => setRoomBox(roomId, box)}
            onChangeItem={setItemBox}
            onDropRoom={dropRoom}
            onDropItem={dropItem}
            onDraftChange={setDraft}
          />
          <p className="mt-3 rounded-lg bg-surface-low px-3 py-2 text-[11px] leading-relaxed text-on-surface-variant">
            {draft
              ? `${draft.label}: x ${formatMetres(draft.box.x)}, y ${formatMetres(draft.box.y)} · ${formatMetres(draft.box.width)} × ${formatMetres(draft.box.height)}`
              : 'Sơ đồ chỉ vẽ phòng bằng hình chữ nhật. Mười một loại vật thể cố định có thể đặt thêm; hình dạng tuỳ ý (cây, ban công vẽ tay) chưa hỗ trợ.'}
          </p>
        </section>

        <PropertyPanel
          selection={selection}
          room={selectedRoom}
          item={selectedItem}
          roomBox={selectedRoom ? (allRooms.find((e) => e.room.id === selectedRoom.id)?.box ?? null) : null}
          canDesign={canDesign}
          onChangeRoomBox={(box) => selectedRoom && setRoomBox(selectedRoom.id, box)}
          onChangeItemLabel={(label) => selectedItem && setItemLabel(selectedItem.id, label)}
          onChangeItemBox={(box) => selectedItem && setItemBox(selectedItem.id, box)}
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
        <p className="mb-2 text-[11px] leading-relaxed text-on-surface-variant">
          Kéo vào sơ đồ để thêm hành lang, cầu thang, kho, ban công, cây xanh…
        </p>
        <ul className="grid grid-cols-2 gap-1.5">
          {(Object.keys(floorLayoutKindMeta) as unknown as string[]).map((key) => {
            const kind = Number(key) as FloorLayoutItemKind;
            const meta = floorLayoutKindMeta[kind];
            return (
              <li key={key}>
                <div
                  draggable={canDesign}
                  onDragStart={(event) => {
                    event.dataTransfer.setData('application/x-item-kind', String(kind));
                    event.dataTransfer.effectAllowed = 'copy';
                  }}
                  className={
                    'flex items-center gap-1.5 rounded-lg border border-outline-variant px-2 py-1.5 text-[11px] text-on-surface-variant transition ' +
                    (canDesign ? 'cursor-grab hover:border-primary/40' : 'cursor-not-allowed opacity-60')
                  }
                >
                  <span className="material-symbols-outlined text-base">{meta.icon}</span>
                  {meta.label}
                </div>
              </li>
            );
          })}
        </ul>
      </section>
    </aside>
  );
}

function PropertyPanel({
  selection,
  room,
  item,
  roomBox,
  canDesign,
  onChangeRoomBox,
  onChangeItemLabel,
  onChangeItemBox,
  onRemoveFromPlan,
  onDeleteItem,
}: {
  selection: DesignerSelection;
  room: RoomDto | null;
  item: DesignerItem | null;
  roomBox: Box | null;
  canDesign: boolean;
  onChangeRoomBox: (box: Box) => void;
  onChangeItemLabel: (label: string | null) => void;
  onChangeItemBox: (box: Box) => void;
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
          <BoxFields
            box={roomBox}
            disabled={!canDesign}
            onChange={onChangeRoomBox}
          />
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
