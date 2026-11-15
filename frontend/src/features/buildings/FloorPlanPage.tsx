import { useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { Alert } from '../../components/ui/Alert';
import { EmptyState } from '../../components/ui/EmptyState';
import { PageHeader } from '../../components/ui/PageHeader';
import { StatusBadge } from '../../components/ui/StatusBadge';
import { useAuth } from '../../lib/auth/AuthProvider';
import { BuildingPolicies } from '../../lib/auth/policies';
import { ApiError } from '../../lib/http';
import {
  useBuildingSnapshot,
  useBuildings,
  useFloorLayoutItems,
} from '../../lib/api/buildingQueries';
import { roomStatusMeta } from '../../lib/api/types';
import type { RoomDto } from '../../lib/api/types';
import { formatMillions, formatNumber } from '../../lib/format';
import { RoomFormDialog } from '../rooms/RoomFormDialog';
import { FloorPlan } from './FloorPlan';

export function FloorPlanPage() {
  const { buildingId, floorId } = useParams<{ buildingId: string; floorId: string }>();
  const navigate = useNavigate();
  const { snapshot, loading, error } = useBuildingSnapshot(buildingId);
  const buildings = useBuildings();
  const layoutItems = useFloorLayoutItems(floorId);
  const { hasPermission } = useAuth();
  const [selectedRoomId, setSelectedRoomId] = useState<string | null>(null);
  const [addRoomOpen, setAddRoomOpen] = useState(false);

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
          <Link
            to={buildingId ? `/buildings/${buildingId}` : '/buildings'}
            className="flex h-10 items-center gap-2 rounded-lg border border-outline-variant px-4 text-sm font-semibold text-on-surface-variant transition hover:bg-surface-low"
          >
            <span className="material-symbols-outlined text-lg">arrow_back</span>
            Quay lại tòa nhà
          </Link>
        }
      />

      <div className="mb-4 grid gap-3 rounded-xl border border-outline-variant bg-surface-lowest p-3 sm:grid-cols-2 lg:max-w-xl">
        <label className="flex flex-col gap-1.5">
          <span className="text-[11px] font-semibold tracking-[0.06em] text-on-surface-variant uppercase">
            Tòa nhà
          </span>
          <select
            value={buildingId ?? ''}
            onChange={(event) => {
              setSelectedRoomId(null);
              navigate(`/buildings/${event.target.value}`);
            }}
            className="h-9 w-full rounded-lg border border-outline-variant bg-surface-lowest px-2.5 text-sm text-on-surface focus:border-primary focus:outline-none"
          >
            {(buildings.data ?? []).map((building) => (
              <option key={building.id} value={building.id}>
                {building.name}
              </option>
            ))}
            {buildingId && !(buildings.data ?? []).some((b) => b.id === buildingId) && (
              <option value={buildingId}>{snapshot?.building.name ?? 'Tòa nhà hiện tại'}</option>
            )}
          </select>
        </label>

        <label className="flex flex-col gap-1.5">
          <span className="text-[11px] font-semibold tracking-[0.06em] text-on-surface-variant uppercase">
            Tầng
          </span>
          <select
            value={floorId ?? ''}
            onChange={(event) => {
              setSelectedRoomId(null);
              navigate(`/buildings/${buildingId}/floors/${event.target.value}`);
            }}
            disabled={!snapshot || snapshot.floors.length === 0}
            className="h-9 w-full rounded-lg border border-outline-variant bg-surface-lowest px-2.5 text-sm text-on-surface focus:border-primary focus:outline-none disabled:opacity-60"
          >
            {!floor && <option value="">Chọn tầng</option>}
            {(snapshot?.floors ?? []).map((item) => (
              <option key={item.id} value={item.id}>
                Tầng {item.floorNumber} — {item.name}
              </option>
            ))}
          </select>
        </label>
      </div>

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
        <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_320px]">
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
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  onClick={() => setAddRoomOpen(true)}
                  disabled={!canManageRooms}
                  title={
                    canManageRooms
                      ? undefined
                      : 'Tài khoản này chưa được cấp quyền quản lý phòng trên máy chủ.'
                  }
                  className="flex h-9 items-center gap-1.5 rounded-lg bg-[#1d4ed8] px-3 text-xs font-semibold text-white transition hover:bg-[#1e40af] disabled:cursor-not-allowed disabled:opacity-50"
                >
                  <span className="material-symbols-outlined text-base">add</span>
                  Thêm phòng
                </button>
                <button
                  type="button"
                  disabled
                  title="Màn thiết kế sơ đồ (kéo thả, lưới) sẽ được mở ở giai đoạn tiếp theo."
                  className="flex h-9 cursor-not-allowed items-center gap-1.5 rounded-lg border border-outline-variant px-3 text-xs font-semibold text-on-surface-variant opacity-60"
                >
                  <span className="material-symbols-outlined text-base">edit</span>
                  Sửa sơ đồ
                </button>
              </div>
            </header>

            <FloorPlan
              rooms={rooms}
              items={layoutItems.data ?? []}
              selectedId={selectedRoomId}
              onSelect={setSelectedRoomId}
            />

            {unplacedCount > 0 && unplacedCount < rooms.length && (
              <p className="mt-3 rounded-lg bg-[#FFFBEB] px-3 py-2 text-xs leading-relaxed text-[#B45309]">
                {unplacedCount} phòng chưa có toạ độ trên sơ đồ nên không được vẽ.
              </p>
            )}
          </section>

          <aside className="rounded-xl border border-outline-variant bg-surface-lowest p-5">
            {selectedRoom ? (
              <RoomPanel
                room={selectedRoom}
                floorNumber={floor.floorNumber}
                buildingId={buildingId ?? ''}
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

      {floorId && (
        <RoomFormDialog
          open={addRoomOpen}
          mode="create"
          floorId={floorId}
          onClose={() => setAddRoomOpen(false)}
        />
      )}
    </div>
  );
}

function RoomPanel({
  room,
  floorNumber,
  buildingId,
  onClose,
}: {
  room: RoomDto;
  floorNumber: number;
  buildingId: string;
  onClose: () => void;
}) {
  const meta = roomStatusMeta[room.status];

  return (
    <div>
      <div className="mb-3 flex items-start justify-between gap-2">
        <div>
          <p className="text-xs font-semibold tracking-wide text-on-surface-variant uppercase">
            Thông tin phòng
          </p>
          <h3 className="mt-1 font-display text-xl font-bold text-on-surface">
            Phòng {room.roomNumber}
          </h3>
        </div>
        <button
          type="button"
          onClick={onClose}
          aria-label="Đóng"
          className="rounded-lg p-1 text-outline transition hover:bg-surface-low hover:text-on-surface"
        >
          <span className="material-symbols-outlined text-lg">close</span>
        </button>
      </div>

      <div className="mb-4">
        <StatusBadge tone={meta?.tone ?? 'neutral'} withDot>
          {meta?.label ?? 'Không xác định'}
        </StatusBadge>
      </div>

      <dl className="flex flex-col gap-2.5 text-sm">
        <Row icon="layers" label="Tầng" value={`Tầng ${floorNumber}`} />
        <Row
          icon="square_foot"
          label="Diện tích"
          value={room.area ? `${room.area} m²` : 'Chưa khai báo'}
        />
        <Row icon="category" label="Tên phòng" value={room.name} />
        <Row
          icon="payments"
          label="Giá thuê"
          value={
            room.monthlyRent === null
              ? 'Chưa khai báo'
              : `${formatNumber(room.monthlyRent)} đ/tháng`
          }
        />
        <Row icon="group" label="Sức chứa" value={`${room.capacity} người`} />
        <Row icon="handyman" label="Thiết bị" value={`${room.equipmentCount} món`} />
        <Row icon="photo_camera" label="Ảnh" value={`${room.photoCount} ảnh`} />
      </dl>

      {room.monthlyRent !== null && (
        <p className="mt-3 rounded-lg bg-[#ECFDF5] px-3 py-2 text-xs font-semibold text-[#047857] tnum">
          Khoảng {formatMillions(room.monthlyRent)} / tháng
        </p>
      )}

      {room.description && (
        <p className="mt-3 rounded-lg bg-surface-low px-3 py-2.5 text-xs leading-relaxed text-on-surface-variant">
          {room.description}
        </p>
      )}

      <Link
        to={`/rooms/${room.id}`}
        className="mt-4 flex h-9 items-center justify-center gap-1.5 rounded-lg bg-[#1d4ed8] text-xs font-semibold text-white transition hover:bg-[#1e40af]"
      >
        <span className="material-symbols-outlined text-base">open_in_new</span>
        Xem chi tiết phòng
      </Link>

      {buildingId && (
        <p className="mt-3 border-t border-outline-variant pt-3 text-[11px] leading-relaxed text-on-surface-variant">
          Hợp đồng, hoá đơn và bảo trì chưa có API công khai trên máy chủ nên chưa hiển thị ở
          đây.
        </p>
      )}
    </div>
  );
}

function Row({ icon, label, value }: { icon: string; label: string; value: string }) {
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
