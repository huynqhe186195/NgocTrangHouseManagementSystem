import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { Alert } from '../../components/ui/Alert';
import { EmptyState } from '../../components/ui/EmptyState';
import { PageHeader } from '../../components/ui/PageHeader';
import { StatusBadge } from '../../components/ui/StatusBadge';
import { ApiError } from '../../lib/http';
import { useBuildingSnapshot } from '../../lib/api/buildingQueries';
import { roomStatusMeta } from '../../lib/api/types';
import type { RoomDto } from '../../lib/api/types';
import { FloorPlan } from './FloorPlan';

export function FloorPlanPage() {
  const { buildingId, floorId } = useParams<{ buildingId: string; floorId: string }>();
  const { snapshot, loading, error } = useBuildingSnapshot(buildingId);
  const [selectedRoomId, setSelectedRoomId] = useState<string | null>(null);

  const floor = snapshot?.floors.find((item) => item.id === floorId) ?? null;
  const rooms = floorId ? snapshot?.roomsByFloor.get(floorId) ?? [] : [];
  const selectedRoom = rooms.find((room) => room.id === selectedRoomId) ?? null;

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
            <header className="mb-4 flex items-center justify-between gap-3">
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
                  disabled
                  title="Chức năng chỉnh sửa sơ đồ chưa được mở"
                  className="flex h-9 items-center gap-1.5 rounded-lg border border-outline-variant px-3 text-xs font-semibold text-on-surface-variant opacity-60"
                >
                  <span className="material-symbols-outlined text-base">edit</span>
                  Sửa sơ đồ
                </button>
              </div>
            </header>

            <FloorPlan
              rooms={rooms.map((room) => ({
                id: room.id,
                roomNumber: room.roomNumber,
                area: room.area ?? 0,
                status: room.status,
                capacity: room.capacity,
              }))}
              selectedId={selectedRoomId}
              onSelect={setSelectedRoomId}
            />
          </section>

          <aside className="rounded-xl border border-outline-variant bg-surface-lowest p-5">
            {selectedRoom ? (
              <RoomPanel
                room={selectedRoom}
                floorNumber={floor.floorNumber}
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
  onClose,
}: {
  room: RoomDto;
  floorNumber: number;
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
        <Row icon="square_foot" label="Diện tích" value={room.area ? `${room.area} m²` : 'Chưa khai báo'} />
        <Row icon="category" label="Tên phòng" value={room.name} />
        <Row icon="group" label="Sức chứa" value={`${room.capacity} người`} />
        <Row icon="tag" label="Mã phòng" value={room.roomNumber} />
      </dl>

      {room.description && (
        <p className="mt-4 rounded-lg bg-surface-low px-3 py-2.5 text-xs leading-relaxed text-on-surface-variant">
          {room.description}
        </p>
      )}

      <p className="mt-4 border-t border-outline-variant pt-3 text-[11px] leading-relaxed text-on-surface-variant">
        Giá thuê, hợp đồng và thiết bị chưa có API công khai trên máy chủ nên chưa hiển
        thị ở đây.
      </p>
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
