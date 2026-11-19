import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { Alert } from '../../components/ui/Alert';
import { EmptyState } from '../../components/ui/EmptyState';
import { PageHeader } from '../../components/ui/PageHeader';
import { SelectField } from '../../components/ui/SelectField';
import { StatusBadge } from '../../components/ui/StatusBadge';
import { useAuth } from '../../lib/auth/AuthProvider';
import { BuildingPolicies } from '../../lib/auth/policies';
import { ApiError } from '../../lib/http';
import {
  useBuildingSnapshot,
  useBuildings,
  useFloorLayoutItems,
  useRoomEquipment,
  useRoomPhotoContent,
  useRoomPhotos,
} from '../../lib/api/buildingQueries';
import { equipmentConditionMeta, roomDirectionLabels, roomStatusMeta } from '../../lib/api/types';
import type { RoomDto } from '../../lib/api/types';
import { formatVnd } from '../../lib/format';
import {
  MAX_ZOOM,
  MIN_ZOOM,
  formatZoom,
  zoomIn,
  zoomOut,
} from '../../lib/floorGeometry';
import { equipmentIcon } from '../rooms/equipmentIcon';
import { RoomFormDialog } from '../rooms/RoomFormDialog';
import { FloorPlan } from './FloorPlan';

const BOOKING_BLOCKED_HINT = 'Chức năng đặt phòng chưa được mở trên máy chủ.';

export function FloorPlanPage() {
  const { buildingId, floorId } = useParams<{ buildingId: string; floorId: string }>();
  const navigate = useNavigate();
  const { snapshot, loading, error } = useBuildingSnapshot(buildingId);
  const buildings = useBuildings();
  const layoutItems = useFloorLayoutItems(floorId);
  const { hasPermission } = useAuth();
  const [selectedRoomId, setSelectedRoomId] = useState<string | null>(null);
  const [fullscreen, setFullscreen] = useState(false);
  const [zoom, setZoom] = useState(1);
  const [showGrid, setShowGrid] = useState(true);
  const planFrameRef = useRef<HTMLDivElement>(null);

  // Escape and the browser's own "leave fullscreen" both fire this, so the
  // button's label stays truthful whichever way the owner exits.
  useEffect(() => {
    function sync() {
      setFullscreen(document.fullscreenElement === planFrameRef.current);
    }
    document.addEventListener('fullscreenchange', sync);
    return () => document.removeEventListener('fullscreenchange', sync);
  }, []);

  const toggleFullscreen = useCallback(async () => {
    const frame = planFrameRef.current;
    if (!frame) {
      return;
    }
    try {
      if (document.fullscreenElement) {
        await document.exitFullscreen();
      } else {
        await frame.requestFullscreen();
      }
    } catch {
      setFullscreen(Boolean(document.fullscreenElement));
    }
  }, []);

  const canManageRooms = hasPermission(BuildingPolicies.roomsManage);

  const floor = snapshot?.floors.find((item) => item.id === floorId) ?? null;
  const rooms = floorId ? snapshot?.roomsByFloor.get(floorId) ?? [] : [];
  const selectedRoom = rooms.find((room) => room.id === selectedRoomId) ?? null;

  const unplacedCount = useMemo(
    () =>
      rooms.filter(
        (room) =>
          room.layoutX === null ||
          room.layoutY === null ||
          room.layoutWidth === null ||
          room.layoutHeight === null,
      ).length,
    [rooms],
  );

  return (
    <div className="mx-auto max-w-[1400px]">
      <PageHeader
        title="Xem sơ đồ tầng"
        description="Xem và quản lý bố trí các phòng trong tòa nhà"
        breadcrumb={[
          'Trang chủ',
          'Tòa nhà',
          snapshot?.building.name ?? '...',
          'Xem sơ đồ tầng',
        ]}
        actions={
          // The mockup pairs the two pickers on the title row, so a visit to
          // another storey is one click from the top of the screen.
          <div className="flex flex-wrap items-start gap-3">
            <div className="w-[240px]">
              <SelectField
                label="Tòa nhà"
                value={buildingId ?? ''}
                onChange={(event) => {
                  setSelectedRoomId(null);
                  navigate(`/buildings/${event.target.value}`);
                }}
                options={[
                  ...(buildings.data ?? []).map((building) => ({
                    value: building.id,
                    label: building.name,
                  })),
                  ...(buildingId &&
                  !(buildings.data ?? []).some((b) => b.id === buildingId)
                    ? [
                        {
                          value: buildingId,
                          label: snapshot?.building.name ?? 'Tòa nhà hiện tại',
                        },
                      ]
                    : []),
                ]}
              />
            </div>
            <div className="w-[190px]">
              <SelectField
                label="Tầng"
                value={floorId ?? ''}
                disabled={!snapshot || snapshot.floors.length === 0}
                onChange={(event) => {
                  setSelectedRoomId(null);
                  navigate(`/buildings/${buildingId}/floors/${event.target.value}`);
                }}
                options={[
                  ...(floor ? [] : [{ value: '', label: 'Chọn tầng' }]),
                  ...(snapshot?.floors ?? []).map((item) => ({
                    value: item.id,
                    label:
                      item.name !== `Tầng ${item.floorNumber}`
                        ? `Tầng ${item.floorNumber} — ${item.name}`
                        : `Tầng ${item.floorNumber}`,
                  })),
                ]}
              />
            </div>
          </div>
        }
      />

      {error && (
        <div className="mb-4">
          <Alert tone={error instanceof ApiError && error.status === 403 ? 'warning' : 'error'}>
            {error instanceof ApiError && error.status === 403
              ? 'Máy chủ đang từ chối truy cập dữ liệu tòa nhà (403). Sơ đồ sẽ hiển thị khi quyền thành viên tòa nhà được cấp.'
              : error instanceof ApiError
                ? error.message
                : 'Không tải được sơ đồ tầng.'}
          </Alert>
        </div>
      )}

      {loading && (
        <div className="flex items-center gap-2.5 rounded-xl border border-outline-variant bg-surface-lowest px-5 py-8 text-sm text-on-surface-variant">
          <span className="material-symbols-outlined animate-spin text-lg">sync</span>
          Đang tải sơ đồ tầng…
        </div>
      )}

      {!loading && snapshot && !floor && (
        <EmptyState
          icon="layers"
          title="Không tìm thấy tầng"
          description="Tầng này không tồn tại trong tòa nhà hoặc bạn không có quyền truy cập."
          action={
            <Link
              to={`/buildings/${buildingId}`}
              className="text-sm font-semibold text-primary hover:underline"
            >
              Quay lại tòa nhà
            </Link>
          }
        />
      )}

      {!loading && floor && (
        <div className="grid items-start gap-4 xl:grid-cols-[minmax(0,1fr)_320px]">
          <section className="rounded-xl border border-outline-variant bg-surface-lowest p-5">
            <header className="mb-4 flex flex-wrap items-center justify-between gap-3">
              <div className="flex items-center gap-2.5">
                <span className="flex h-9 w-9 items-center justify-center rounded-lg bg-surface-low text-primary">
                  <span className="material-symbols-outlined text-xl">map</span>
                </span>
                <div className="leading-tight">
                  <p className="font-display text-base font-bold text-on-surface">
                    Sơ đồ mặt bằng - Tầng {floor.floorNumber}
                  </p>
                  <p className="text-xs text-on-surface-variant">
                    Nhấn vào phòng để xem thông tin chi tiết
                  </p>
                </div>
              </div>
              {/* The mockup draws the zoom as one pill and the two view
                  buttons as separate squares beside it. Every button does
                  something — the percentage is the reset, so no dead icon. */}
              <div className="flex items-center gap-1.5">
                <div className="flex items-center gap-0.5 rounded-full border border-outline-variant bg-surface-lowest py-0.5 pr-0.5 pl-1.5">
                  <button
                    type="button"
                    onClick={() => setZoom(zoomOut(zoom))}
                    aria-label="Thu nhỏ sơ đồ"
                    className="flex h-7 w-7 items-center justify-center rounded-full text-on-surface-variant transition hover:bg-surface-low disabled:opacity-40"
                    disabled={zoom <= MIN_ZOOM}
                  >
                    <span className="material-symbols-outlined text-base">remove</span>
                  </button>
                  <button
                    type="button"
                    onClick={() => setZoom(1)}
                    title="Đặt lại tỷ lệ"
                    className="tnum min-w-[52px] rounded-full text-center text-xs font-semibold text-on-surface transition hover:bg-surface-low"
                  >
                    {formatZoom(zoom)}
                  </button>
                  <button
                    type="button"
                    onClick={() => setZoom(zoomIn(zoom))}
                    aria-label="Phóng to sơ đồ"
                    className="flex h-7 w-7 items-center justify-center rounded-full text-on-surface-variant transition hover:bg-surface-low disabled:opacity-40"
                    disabled={zoom >= MAX_ZOOM}
                  >
                    <span className="material-symbols-outlined text-base">add</span>
                  </button>
                </div>
                <button
                  type="button"
                  onClick={() => void toggleFullscreen()}
                  aria-label={fullscreen ? 'Thoát toàn màn hình' : 'Xem sơ đồ toàn màn hình'}
                  title={fullscreen ? 'Thoát toàn màn hình' : 'Xem sơ đồ toàn màn hình'}
                  className="flex h-9 w-10 items-center justify-center rounded-lg border border-outline-variant text-on-surface-variant transition hover:bg-surface-low"
                >
                  <span className="material-symbols-outlined text-base">
                    {fullscreen ? 'fullscreen_exit' : 'fullscreen'}
                  </span>
                </button>
                <button
                  type="button"
                  onClick={() => setShowGrid((current) => !current)}
                  aria-pressed={showGrid}
                  aria-label={showGrid ? 'Ẩn lưới mét' : 'Hiện lưới mét'}
                  title={showGrid ? 'Ẩn lưới mét' : 'Hiện lưới mét'}
                  className="flex h-9 w-10 items-center justify-center rounded-lg border border-outline-variant text-on-surface-variant transition hover:bg-surface-low"
                >
                  <span className="material-symbols-outlined text-base">grid_on</span>
                </button>
              </div>
            </header>

            <div
              ref={planFrameRef}
              className={
                fullscreen
                  ? 'flex h-full flex-col overflow-auto bg-surface-lowest p-5'
                  : undefined
              }
            >
              <FloorPlan
                rooms={rooms}
                items={layoutItems.data ?? []}
                selectedId={selectedRoomId}
                onSelect={setSelectedRoomId}
                fill={fullscreen}
                zoom={zoom}
                onZoomChange={setZoom}
                showGrid={showGrid}
                showLegend={false}
              />

              {unplacedCount > 0 && unplacedCount < rooms.length && (
                <p className="mt-3 rounded-lg bg-[#FFFBEB] px-3 py-2 text-xs leading-relaxed text-[#B45309]">
                  {unplacedCount} phòng chưa có toạ độ trên sơ đồ nên không được vẽ.
                </p>
              )}
            </div>
          </section>

          <aside className="rounded-xl border border-outline-variant bg-surface-lowest p-5">
            {selectedRoom ? (
              <RoomPanel
                key={selectedRoom.id}
                room={selectedRoom}
                floorNumber={floor.floorNumber}
                canManage={canManageRooms}
                onClose={() => setSelectedRoomId(null)}
              />
            ) : (
              <div className="flex h-full flex-col items-center justify-center gap-2 py-10 text-center">
                <span className="material-symbols-outlined text-3xl text-outline">touch_app</span>
                <p className="text-sm font-semibold text-on-surface">Chọn một phòng</p>
                <p className="max-w-[220px] text-xs leading-relaxed text-on-surface-variant">
                  Thông tin chi tiết của phòng sẽ xuất hiện ở đây.
                </p>
              </div>
            )}
          </aside>
        </div>
      )}
    </div>
  );
}

function RoomPanel({
  room,
  floorNumber,
  canManage,
  onClose,
}: {
  room: RoomDto;
  floorNumber: number;
  canManage: boolean;
  onClose: () => void;
}) {
  const meta = roomStatusMeta[room.status];
  const equipment = useRoomEquipment(room.id);
  const equipmentRows = equipment.data ?? [];
  const photos = useRoomPhotos(room.id);
  const firstPhoto = useMemo(
    () => [...(photos.data ?? [])].sort((a, b) => a.displayOrder - b.displayOrder)[0],
    [photos.data],
  );
  const photo = useRoomPhotoContent(room.id, firstPhoto?.id ?? '', Boolean(firstPhoto));
  const photoSrc = photo.data
    ? `data:${photo.data.contentType};base64,${photo.data.content}`
    : null;
  const [editOpen, setEditOpen] = useState(false);

  return (
    <div>
      <div className="mb-3 flex items-start justify-between gap-2">
        <h3 className="font-display text-lg font-bold text-on-surface">Thông tin phòng</h3>
        <button
          type="button"
          onClick={onClose}
          aria-label="Đóng"
          className="rounded-lg p-1 text-outline transition hover:bg-surface-low hover:text-on-surface"
        >
          <span className="material-symbols-outlined text-lg">close</span>
        </button>
      </div>

      {/* The mockup opens the rail with the room's own photograph. A room
          nobody has photographed keeps a plain frame that says so, rather than
          borrowing a stock image. */}
      <div className="mb-3 aspect-[16/9] overflow-hidden rounded-lg border border-outline-variant bg-surface-low">
        {photoSrc ? (
          <img
            src={photoSrc}
            alt={room.name || `Phòng ${room.roomNumber}`}
            className="h-full w-full object-cover"
          />
        ) : (
          <div className="flex h-full flex-col items-center justify-center gap-1.5 text-center">
            <span className="material-symbols-outlined text-2xl text-outline">image</span>
            <p className="text-xs text-on-surface-variant">
              {photos.isPending ? 'Đang tải ảnh…' : 'Phòng chưa có ảnh.'}
            </p>
          </div>
        )}
      </div>

      <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
        <p className="font-display text-xl font-bold text-on-surface">
          {room.name || `Phòng ${room.roomNumber}`}
        </p>
        <StatusBadge tone={meta?.tone ?? 'neutral'} withDot>
          {meta?.label ?? 'Không xác định'}
        </StatusBadge>
      </div>

      <dl className="flex flex-col gap-1.5 text-sm">
        <Row icon="layers" label="Tầng" value={`Tầng ${floorNumber}`} />
        <Row
          icon="square_foot"
          label="Diện tích"
          value={room.area ? `${room.area} m²` : 'Chưa cập nhật'}
        />
        <Row icon="category" label="Hình dạng" value={shapeLabel(room)} />
        <Row
          icon="payments"
          label="Giá thuê"
          value={
            room.monthlyRent === null
              ? 'Chưa cập nhật'
              : `${formatVnd(room.monthlyRent)}/tháng`
          }
        />
        <Row
          icon="check_circle"
          label="Trạng thái"
          value={
            <span className="inline-flex items-center gap-1.5 font-medium text-[#047857]">
              <span aria-hidden className="h-2 w-2 rounded-full bg-[#10b981]" />
              {meta?.label ?? 'Không xác định'}
            </span>
          }
        />
        <Row icon="location_on" label="Vị trí" value="Chưa cập nhật" />
        <Row
          icon="explore"
          label="Hướng"
          value={
            room.direction === null
              ? 'Chưa cập nhật'
              : roomDirectionLabels[room.direction]
          }
        />
      </dl>

      <p className="mt-2 text-[11px] leading-snug text-on-surface-variant">
        Vị trí chưa có trong dữ liệu phòng trên máy chủ nên để trống thay vì tự điền.
      </p>

      {/* The equipment the room actually holds, as the server records it. The
          mockup lists these beside the plan; a bed drawn on the floor is the
          furniture the owner placed, which is a different thing from what the
          room is equipped with. */}
      <div className="mt-4 border-t border-outline-variant pt-3">
        <h4 className="mb-2 text-xs font-semibold tracking-wide text-[#1d4ed8] uppercase">
          Thiết bị trong phòng
        </h4>
        {equipment.isPending && (
          <p className="py-2 text-xs text-on-surface-variant">Đang tải thiết bị…</p>
        )}
        {!equipment.isPending && equipmentRows.length === 0 && (
          <p className="py-2 text-xs text-on-surface-variant">
            Phòng này chưa được ghi nhận thiết bị nào.
          </p>
        )}
        {equipmentRows.length > 0 && (
          <ul className="flex flex-col divide-y divide-outline-variant rounded-lg border border-outline-variant">
            {equipmentRows.map((item) => {
              const condition = equipmentConditionMeta[item.condition];
              return (
                <li
                  key={item.id}
                  className="flex items-center justify-between gap-2 px-2.5 py-1.5 text-xs"
                >
                  <span className="flex min-w-0 items-center gap-1.5 text-on-surface">
                    <span className="material-symbols-outlined text-base text-on-surface-variant">
                      {equipmentIcon(item)}
                    </span>
                    <span className="truncate">{item.name}</span>
                  </span>
                  <span className="flex shrink-0 items-center gap-2">
                    {condition && condition.tone !== 'success' && (
                      <span className="text-[10px] font-semibold text-[#b45309]">
                        {condition.label}
                      </span>
                    )}
                    <span className="font-semibold text-on-surface tnum">×{item.quantity}</span>
                  </span>
                </li>
              );
            })}
          </ul>
        )}
      </div>

      <div className="mt-3 flex gap-2">
        <button
          type="button"
          onClick={() => setEditOpen(true)}
          disabled={!canManage}
          title={
            canManage ? undefined : 'Tài khoản này chưa được cấp quyền quản lý phòng trên máy chủ.'
          }
          className="flex h-9 flex-1 items-center justify-center gap-1.5 rounded-lg border border-outline-variant text-sm font-semibold text-on-surface-variant transition hover:bg-surface-low disabled:cursor-not-allowed disabled:opacity-50"
        >
          <span className="material-symbols-outlined text-base">edit</span>
          Chỉnh sửa
        </button>
        <button
          type="button"
          disabled
          title={BOOKING_BLOCKED_HINT}
          className="flex h-9 flex-1 items-center justify-center gap-1.5 rounded-lg bg-[#1d4ed8] text-sm font-semibold text-white disabled:cursor-not-allowed disabled:opacity-60"
        >
          <span className="material-symbols-outlined text-base">event_available</span>
          Đặt phòng
        </button>
      </div>

      <RoomFormDialog
        open={editOpen}
        mode="edit"
        initial={room}
        onClose={() => setEditOpen(false)}
      />
    </div>
  );
}

/** `layoutShape` is the only shape fact the server keeps about a room. */
function shapeLabel(room: RoomDto): string {
  if (room.layoutShape === 2) {
    return 'Đa giác';
  }
  if (room.layoutShape === 1) {
    return 'Hình chữ nhật';
  }
  return 'Chưa cập nhật';
}

function Row({
  icon,
  label,
  value,
}: {
  icon: string;
  label: string;
  value: ReactNode;
}) {
  return (
    <div className="flex items-center justify-between gap-3">
      <span className="flex items-center gap-1.5 text-on-surface-variant">
        <span className="material-symbols-outlined text-base">{icon}</span>
        {label}
      </span>
      <span className="text-right font-medium text-on-surface">{value}</span>
    </div>
  );
}
