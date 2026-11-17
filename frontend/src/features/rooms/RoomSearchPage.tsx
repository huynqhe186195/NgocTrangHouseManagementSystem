import { useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useQueries } from '@tanstack/react-query';
import { Alert } from '../../components/ui/Alert';
import { Button } from '../../components/ui/Button';
import { EmptyState } from '../../components/ui/EmptyState';
import { PageHeader } from '../../components/ui/PageHeader';
import { PhotoGallery } from '../../components/ui/PhotoGallery';
import type { GalleryPhoto } from '../../components/ui/PhotoGallery';
import { SelectField } from '../../components/ui/SelectField';
import { StatusBadge } from '../../components/ui/StatusBadge';
import { ApiError } from '../../lib/http';
import { roomsApi } from '../../lib/api/buildingApi';
import {
  useBuildings,
  useFloorLayoutItems,
  useFloors,
  useRoomEquipment,
  useRoomPhotos,
  useRooms,
} from '../../lib/api/buildingQueries';
import { equipmentConditionMeta, roomStatusMeta } from '../../lib/api/types';
import type { RoomPhotoDto } from '../../lib/api/types';
import { formatVnd } from '../../lib/format';
import { FloorPlan } from '../buildings/FloorPlan';
import { equipmentIcon } from './equipmentIcon';
import { RoomSaveShare } from './RoomSaveShare';

const LEASE_MONTHS = [3, 6, 12];
const DEFAULT_LEASE_MONTHS = 6;
const DEPOSIT_MONTHS = 1;
const GALLERY_PHOTOS = 6;

const BOOKING_BLOCKED_HINT = 'Chức năng đặt phòng chưa được mở trên máy chủ.';

export interface FirstPaymentEstimate {
  rent: number;
  deposit: number;
  total: number;
}

/**
 * The booking flow itself lives behind a server app service the gateway
 * denies, so the only thing this screen can honestly show is an estimate
 * computed from the published rent.
 */
export function estimateFirstPayment(monthlyRent: number, months: number): FirstPaymentEstimate {
  const rent = monthlyRent * months;
  const deposit = monthlyRent * DEPOSIT_MONTHS;
  return { rent, deposit, total: rent + deposit };
}

function todayIso(): string {
  const now = new Date();
  const local = new Date(now.getTime() - now.getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 10);
}

export function RoomSearchPage() {
  const buildings = useBuildings();
  const floors = useFloors();
  const rooms = useRooms();

  const [buildingId, setBuildingId] = useState('');
  const [query, setQuery] = useState('');
  // The room lives in the URL so a shared link opens the same room, not the
  // first available one.
  const [searchParams, setSearchParams] = useSearchParams();
  const selectedRoomId = searchParams.get('room');
  const selectRoom = (roomId: string) => {
    setSearchParams(
      (previous) => {
        const next = new URLSearchParams(previous);
        next.set('room', roomId);
        return next;
      },
      { replace: true },
    );
  };
  const [moveInDate, setMoveInDate] = useState(todayIso);
  const [leaseMonths, setLeaseMonths] = useState(DEFAULT_LEASE_MONTHS);
  /** The floor drawn in the locator; null follows the room's own floor. */
  const [planFloorId, setPlanFloorId] = useState<string | null>(null);

  const loading = buildings.isLoading || floors.isLoading || rooms.isLoading;
  const error = buildings.error ?? floors.error ?? rooms.error;

  const floorById = useMemo(() => {
    const map = new Map<string, { buildingId: string; floorNumber: number }>();
    for (const floor of floors.data ?? []) {
      map.set(floor.id, { buildingId: floor.buildingId, floorNumber: floor.floorNumber });
    }
    return map;
  }, [floors.data]);

  const buildingNameById = useMemo(() => {
    const map = new Map<string, string>();
    for (const building of buildings.data ?? []) {
      map.set(building.id, building.name);
    }
    return map;
  }, [buildings.data]);

  const available = useMemo(() => {
    const term = query.trim().toLowerCase();
    return (rooms.data ?? [])
      .filter((room) => room.status === 1)
      .filter((room) => (buildingId ? floorById.get(room.floorId)?.buildingId === buildingId : true))
      .filter((room) => {
        if (!term) {
          return true;
        }
        const buildingName = buildingNameById.get(
          floorById.get(room.floorId)?.buildingId ?? '',
        );
        return (
          room.roomNumber.toLowerCase().includes(term) ||
          room.name.toLowerCase().includes(term) ||
          (buildingName ?? '').toLowerCase().includes(term)
        );
      })
      .sort((a, b) => a.roomNumber.localeCompare(b.roomNumber, 'vi', { numeric: true }));
  }, [rooms.data, buildingId, query, floorById, buildingNameById]);

  // A filter can remove the selected room; falling back to the first result
  // keeps the detail pane filled instead of leaving a dangling id.
  const room = available.find((item) => item.id === selectedRoomId) ?? available[0] ?? null;
  const floor = room ? (floors.data ?? []).find((item) => item.id === room.floorId) ?? null : null;
  const building = floor
    ? (buildings.data ?? []).find((item) => item.id === floor.buildingId) ?? null
    : null;

  const photos = useRoomPhotos(room?.id);
  const equipment = useRoomEquipment(room?.id);

  const planFloorCandidates = useMemo(
    () =>
      (floors.data ?? [])
        .filter((item) => item.buildingId === building?.id)
        .sort((a, b) => a.floorNumber - b.floorNumber),
    [floors.data, building?.id],
  );
  // A stale pick from another building falls back to the room's own floor.
  const planFloor = planFloorCandidates.find((item) => item.id === planFloorId) ?? floor;
  const layoutItems = useFloorLayoutItems(planFloor?.id);

  const planRooms = useMemo(
    () => (planFloor ? (rooms.data ?? []).filter((item) => item.floorId === planFloor.id) : []),
    [rooms.data, planFloor],
  );

  const others = useMemo(
    () => available.filter((item) => item.id !== room?.id),
    [available, room?.id],
  );

  const buildingOptions = useMemo(
    () => (buildings.data ?? []).map((item) => ({ value: item.id, label: item.name })),
    [buildings.data],
  );

  const planFloorOptions = planFloorCandidates.map((item) => ({
    value: item.id,
    label: item.name ?? `Tầng ${item.floorNumber}`,
  }));

  const estimate =
    room?.monthlyRent != null ? estimateFirstPayment(room.monthlyRent, leaseMonths) : null;

  const breadcrumb = building
    ? [
        'Trang chủ',
        'Tìm phòng',
        building.name,
        ...(room ? [room.name || `Phòng ${room.roomNumber}`] : []),
      ]
    : ['Trang chủ', 'Tìm phòng'];

  const subtitle =
    room && floor && building
      ? `Tòa nhà ${building.name} · Tầng ${floor.floorNumber} · Mã phòng: ${room.roomNumber}`
      : room && floor
        ? `Tầng ${floor.floorNumber} · Mã phòng: ${room.roomNumber}`
        : (room?.name ?? 'Xem các phòng đang trống và ước tính chi phí nhận phòng');

  const filters = (
    <RoomFilters
      buildingId={buildingId}
      onBuildingChange={setBuildingId}
      buildingOptions={buildingOptions}
      query={query}
      onQueryChange={setQuery}
    />
  );

  return (
    <div className="mx-auto max-w-[1400px]">
      <PageHeader
        title={room ? room.name || `Phòng ${room.roomNumber}` : 'Tìm phòng'}
        description={subtitle}
        breadcrumb={breadcrumb}
        actions={
          room ? (
            <RoomSaveShare
              roomId={room.id}
              roomNumber={room.roomNumber}
              href={`/search?room=${room.id}`}
            />
          ) : undefined
        }
      />

      {error && (
        <div className="mb-4">
          <Alert tone={error instanceof ApiError && error.status === 403 ? 'warning' : 'error'}>
            {error instanceof ApiError && error.status === 403
              ? 'Máy chủ đang từ chối truy cập dữ liệu phòng (403) cho tài khoản này.'
              : error instanceof ApiError
                ? error.message
                : 'Không tải được danh sách phòng.'}
          </Alert>
        </div>
      )}

      {loading && (
        <div className="flex items-center gap-2.5 rounded-xl border border-outline-variant bg-surface-lowest px-5 py-8 text-sm text-on-surface-variant">
          <span className="material-symbols-outlined animate-spin text-lg">sync</span>
          Đang tải danh sách phòng…
        </div>
      )}

      {!loading && !error && !room && (
        <>
          <div className="mb-4 rounded-xl border border-outline-variant bg-surface-lowest p-3">
            {filters}
          </div>
          <EmptyState
            icon="meeting_room"
            title="Chưa có phòng trống phù hợp"
            description="Thử bỏ từ khóa tìm kiếm hoặc chọn tòa nhà khác. Chỉ phòng đang ở trạng thái «Trống» mới hiện ở đây."
          />
        </>
      )}

      {!loading && !error && room && (
        <div className="flex flex-col gap-4">
          <div className="grid gap-4 sm:max-[1399px]:grid-cols-2 min-[1400px]:grid-cols-[minmax(0,1.75fr)_minmax(0,1fr)_minmax(0,1.25fr)]">
            <section className="min-w-0 rounded-xl border border-outline-variant bg-surface-lowest p-4 sm:max-[1399px]:col-span-2 min-[1400px]:col-span-1">
              <div className="mx-auto w-full max-w-[680px]">
                <RoomGallery
                  roomId={room.id}
                  photoList={photos.data ?? []}
                  loading={photos.isLoading}
                />
              </div>
            </section>

            <section className="min-w-0 rounded-xl border border-outline-variant bg-surface-lowest p-5">
              <h2 className="mb-4 font-display text-lg font-bold text-on-surface">Thông tin phòng</h2>
              <div className="flex flex-col gap-3">
                <InfoRow
                  icon="square_foot"
                  label="Diện tích"
                  value={room.area ? `${room.area} m²` : 'Chưa cập nhật'}
                />
                <InfoRow icon="payments" label="Giá thuê">
                  <span className="font-semibold whitespace-nowrap text-primary">
                    {room.monthlyRent === null
                      ? 'Liên hệ'
                      : `${formatVnd(room.monthlyRent)}/tháng`}
                  </span>
                </InfoRow>
                <InfoRow
                  icon="apartment"
                  label="Tầng"
                  value={floor ? `Tầng ${floor.floorNumber}` : 'Chưa cập nhật'}
                />
                <InfoRow icon="explore" label="Hướng" value="Chưa cập nhật" />
                <InfoRow icon="check_circle" label="Tình trạng">
                  <StatusBadge tone="success" withDot>
                    {roomStatusMeta[room.status].label}
                  </StatusBadge>
                </InfoRow>
                <InfoRow icon="meeting_room" label="Loại phòng" value="Chưa cập nhật" />
                <InfoRow icon="group" label="Sức chứa" value={`Tối đa ${room.capacity} người`} />
              </div>

              {room.description && (
                <p className="mt-4 border-t border-outline-variant pt-3 text-xs leading-relaxed text-on-surface-variant">
                  {room.description}
                </p>
              )}

              <p className="mt-3 border-t border-outline-variant pt-3 text-[11px] leading-relaxed text-on-surface-variant">
                Hướng và loại phòng chưa có trong dữ liệu phòng trên máy chủ, nên để trống thay vì
                tự điền.
              </p>
            </section>

            <section className="min-w-0 rounded-xl border border-outline-variant bg-surface-lowest p-5">
              <div className="mb-4 flex items-start justify-between gap-3">
                <p className="font-display text-2xl font-bold whitespace-nowrap text-primary tnum">
                  {room.monthlyRent === null ? 'Liên hệ' : formatVnd(room.monthlyRent)}
                  {room.monthlyRent !== null && (
                    <span className="text-sm font-semibold text-primary">/tháng</span>
                  )}
                </p>
                <StatusBadge tone="success" withDot>
                  {roomStatusMeta[room.status].label}
                </StatusBadge>
              </div>

              <div className="flex flex-col gap-3">
                <label className="flex flex-col gap-2">
                  <span className="text-[11px] font-semibold tracking-[0.06em] text-on-surface-variant uppercase">
                    Ngày nhận phòng
                  </span>
                  <input
                    type="date"
                    value={moveInDate}
                    onChange={(event) => setMoveInDate(event.target.value)}
                    className="h-10 w-full rounded-[0.5rem] border border-outline-variant bg-surface-lowest px-3 text-sm text-on-surface focus:border-primary focus:outline-none focus:ring-3 focus:ring-primary/15"
                  />
                </label>

                <SelectField
                  label="Thời hạn thuê"
                  value={String(leaseMonths)}
                  onChange={(event) => setLeaseMonths(Number(event.target.value))}
                  options={LEASE_MONTHS.map((months) => ({
                    value: String(months),
                    label: `${months} tháng`,
                  }))}
                />
              </div>

              <div className="mt-4 rounded-lg border border-outline-variant bg-surface-low p-3">
                <p className="mb-2 text-[11px] font-semibold tracking-[0.06em] text-on-surface-variant uppercase">
                  Tổng tiền dự kiến
                </p>
                <dl className="flex flex-col gap-1.5 text-sm">
                  <div className="flex items-center justify-between gap-3">
                    <dt className="text-on-surface-variant">Tiền phòng ({leaseMonths} tháng)</dt>
                    <dd className="font-medium whitespace-nowrap text-on-surface tnum">
                      {estimate ? formatVnd(estimate.rent) : '—'}
                    </dd>
                  </div>
                  <div className="flex items-center justify-between gap-3">
                    <dt className="text-on-surface-variant">Đặt cọc ({DEPOSIT_MONTHS} tháng)</dt>
                    <dd className="font-medium whitespace-nowrap text-on-surface tnum">
                      {estimate ? formatVnd(estimate.deposit) : '—'}
                    </dd>
                  </div>
                  <div className="mt-1 flex items-center justify-between gap-3 border-t border-outline-variant pt-2">
                    <dt className="font-semibold text-on-surface">Tổng thanh toán lần đầu</dt>
                    <dd className="font-display font-bold whitespace-nowrap text-primary tnum">
                      {estimate ? formatVnd(estimate.total) : '—'}
                    </dd>
                  </div>
                </dl>
              </div>

              <Button
                fullWidth
                className="mt-4"
                disabled
                title={BOOKING_BLOCKED_HINT}
                icon={<span className="material-symbols-outlined text-base">event_available</span>}
              >
                Đặt phòng ngay
              </Button>

              <p className="mt-2 text-center text-[11px] leading-relaxed text-on-surface-variant">
                {BOOKING_BLOCKED_HINT} Số tiền trên là ước tính, chưa phải hoá đơn.
              </p>
            </section>
          </div>

          <div className="grid gap-4 lg:grid-cols-2">
            <section className="min-w-0 rounded-xl border border-outline-variant bg-surface-lowest p-4">
              <h2 className="font-display text-base font-bold text-on-surface">
                Vị trí phòng trên mặt bằng
              </h2>

              {planFloorOptions.length > 0 && (
                <div className="mt-3 mb-3 w-36">
                  <SelectField
                    label="Tầng hiển thị"
                    hideLabel
                    value={planFloor?.id ?? ''}
                    onChange={(event) => setPlanFloorId(event.target.value)}
                    options={planFloorOptions}
                  />
                </div>
              )}

              {planFloor ? (
                <>
                  <FloorPlan
                    rooms={planRooms}
                    items={layoutItems.data ?? []}
                    selectedId={room.floorId === planFloor.id ? room.id : null}
                    onSelect={() => undefined}
                    compact
                  />
                  <p className="mt-2 text-[11px] leading-relaxed text-on-surface-variant">
                    {room.floorId === planFloor.id
                      ? `Phòng ${room.roomNumber} được tô nổi trên sơ đồ tầng ${planFloor.floorNumber}.`
                      : `Phòng ${room.roomNumber} nằm ở tầng ${floor?.floorNumber ?? '—'}; sơ đồ đang hiển thị tầng ${planFloor.floorNumber}.`}
                  </p>
                </>
              ) : (
                <p className="text-sm text-on-surface-variant">Chưa xác định được tầng của phòng.</p>
              )}
            </section>

            <section className="min-w-0 rounded-xl border border-outline-variant bg-surface-lowest p-4">
              <h2 className="mb-3 font-display text-base font-bold text-on-surface">Tiện ích phòng</h2>
              {equipment.isLoading ? (
                <p className="text-sm text-on-surface-variant">Đang tải tiện ích…</p>
              ) : (equipment.data ?? []).length === 0 ? (
                <p className="rounded-lg border border-dashed border-outline-variant bg-surface-low px-3 py-4 text-sm text-on-surface-variant">
                  Chủ nhà chưa ghi nhận thiết bị nào cho phòng này.
                </p>
              ) : (
                <ul className="grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
                  {(equipment.data ?? []).map((item) => {
                    const condition = equipmentConditionMeta[item.condition];
                    return (
                      <li
                        key={item.id}
                        className="flex items-center gap-2 rounded-lg border border-outline-variant px-3 py-2.5 text-sm"
                      >
                        <span
                          className="material-symbols-outlined shrink-0 text-lg text-on-surface-variant"
                          aria-hidden
                        >
                          {equipmentIcon(item)}
                        </span>
                        <span className="min-w-0 flex-1 text-on-surface">
                          {item.name}
                          {item.quantity > 1 && (
                            <span className="text-on-surface-variant"> ×{item.quantity}</span>
                          )}
                        </span>
                        {condition && condition.tone !== 'success' && (
                          <StatusBadge tone={condition.tone}>{condition.label}</StatusBadge>
                        )}
                      </li>
                    );
                  })}
                </ul>
              )}
            </section>
          </div>

          <div className="grid gap-4 lg:grid-cols-2">
            <section className="min-w-0 rounded-xl border border-outline-variant bg-surface-lowest p-4">
              <h2 className="mb-3 font-display text-base font-bold text-on-surface">
                Quy định nhà trọ
              </h2>
              <p className="rounded-lg border border-dashed border-outline-variant bg-surface-low px-3 py-4 text-sm leading-relaxed text-on-surface-variant">
                Chủ nhà chưa công bố quy định nhà trọ trên máy chủ, nên màn hình không hiển thị quy
                định nào.
              </p>
            </section>

            <section className="min-w-0 rounded-xl border border-outline-variant bg-surface-lowest p-4">
              <h2 className="mb-3 font-display text-base font-bold text-on-surface">
                Thông tin khác
              </h2>
              <p className="rounded-lg border border-dashed border-outline-variant bg-surface-low px-3 py-4 text-sm leading-relaxed text-on-surface-variant">
                Đơn giá điện, nước và phí dịch vụ chưa được công bố trên máy chủ, nên màn hình không
                hiển thị con số nào.
              </p>
            </section>
          </div>

          <section className="rounded-xl border border-outline-variant bg-surface-lowest p-4">
            <div className="mb-4 flex flex-col gap-3 xl:flex-row xl:items-end xl:justify-between">
              <h2 className="font-display text-base font-bold text-on-surface">
                Phòng đang trống khác ({others.length})
              </h2>
              {filters}
            </div>

            {others.length === 0 ? (
              <p className="text-sm text-on-surface-variant">
                Không còn phòng trống nào khác khớp bộ lọc hiện tại.
              </p>
            ) : (
              <ul className="grid gap-2 sm:grid-cols-2 xl:grid-cols-4">
                {others.map((item) => {
                  const itemFloor = floorById.get(item.floorId);
                  return (
                    <li key={item.id}>
                      <button
                        type="button"
                        onClick={() => {
                          selectRoom(item.id);
                          setPlanFloorId(null);
                        }}
                        className="flex w-full items-center justify-between gap-2 rounded-lg border border-outline-variant px-3 py-2 text-left text-sm transition hover:bg-surface-low focus:outline-none focus-visible:ring-3 focus-visible:ring-primary/25"
                      >
                        <span className="min-w-0">
                          <span className="block truncate font-semibold text-on-surface">
                            {item.name || `Phòng ${item.roomNumber}`}
                          </span>
                          <span className="block truncate text-xs text-on-surface-variant">
                            {buildingNameById.get(itemFloor?.buildingId ?? '') ?? '—'}
                            {itemFloor ? ` · Tầng ${itemFloor.floorNumber}` : ''}
                          </span>
                        </span>
                        <span className="shrink-0 text-xs font-semibold text-on-surface tnum">
                          {item.monthlyRent === null ? 'Liên hệ' : formatVnd(item.monthlyRent)}
                        </span>
                      </button>
                    </li>
                  );
                })}
              </ul>
            )}
          </section>
        </div>
      )}
    </div>
  );
}

function RoomFilters({
  buildingId,
  onBuildingChange,
  buildingOptions,
  query,
  onQueryChange,
}: {
  buildingId: string;
  onBuildingChange: (value: string) => void;
  buildingOptions: { value: string; label: string }[];
  query: string;
  onQueryChange: (value: string) => void;
}) {
  return (
    <div className="flex flex-col gap-2 sm:flex-row sm:items-end">
      <div className="sm:w-56">
        <SelectField
          label="Tòa nhà"
          value={buildingId}
          onChange={(event) => onBuildingChange(event.target.value)}
          options={buildingOptions}
          placeholder="Tất cả tòa nhà"
        />
      </div>
      <div className="relative sm:w-72">
        <span className="material-symbols-outlined pointer-events-none absolute top-1/2 left-3 -translate-y-1/2 text-lg text-outline">
          search
        </span>
        <input
          type="search"
          value={query}
          onChange={(event) => onQueryChange(event.target.value)}
          aria-label="Tìm phòng"
          placeholder="Tìm mã phòng, tên phòng, tòa nhà…"
          className="h-10 w-full rounded-lg border border-outline-variant bg-surface-low pl-10 pr-3 text-sm text-on-surface placeholder:text-outline focus:border-primary focus:bg-surface-lowest focus:outline-none"
        />
      </div>
    </div>
  );
}

/** Bytes are fetched only for the thumbnails the gallery actually paints. */
function RoomGallery({
  roomId,
  photoList,
  loading,
}: {
  roomId: string;
  photoList: RoomPhotoDto[];
  loading: boolean;
}) {
  const ordered = useMemo(
    () => [...photoList].sort((a, b) => a.displayOrder - b.displayOrder),
    [photoList],
  );
  const visible = ordered.slice(0, GALLERY_PHOTOS);
  const hidden = ordered.length - visible.length;

  const contents = useQueries({
    queries: visible.map((photo) => ({
      queryKey: ['room-photo-content', roomId, photo.id] as const,
      queryFn: () => roomsApi.getPhotoContent(roomId, photo.id),
      staleTime: 5 * 60 * 1000,
    })),
  });

  const gallery: GalleryPhoto[] = visible.flatMap((photo, index) => {
    const content = contents[index]?.data;
    if (!content) {
      return [];
    }
    return [
      {
        id: photo.id,
        src: `data:${content.contentType};base64,${content.content}`,
        alt: photo.fileName,
      },
    ];
  });

  return (
    <>
      <PhotoGallery
        photos={gallery}
        loading={loading || (visible.length > 0 && gallery.length === 0)}
        emptyLabel="Phòng chưa có ảnh"
      />
      {hidden > 0 && (
        <p className="mt-2 text-xs text-on-surface-variant">Còn {hidden} ảnh nữa chưa hiển thị.</p>
      )}
    </>
  );
}

function InfoRow({
  icon,
  label,
  value,
  children,
}: {
  icon: string;
  label: string;
  value?: string;
  children?: React.ReactNode;
}) {
  return (
    <div className="flex items-center justify-between gap-3">
      <span className="flex items-center gap-2 text-sm text-on-surface-variant">
        <span className="material-symbols-outlined text-base" aria-hidden>
          {icon}
        </span>
        {label}
      </span>
      <span className="text-right text-sm font-medium text-on-surface">
        {children ?? value}
      </span>
    </div>
  );
}