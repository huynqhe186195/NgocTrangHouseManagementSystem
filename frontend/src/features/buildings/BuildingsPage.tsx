import { Link, useParams } from 'react-router-dom';
import { Alert } from '../../components/ui/Alert';
import { EmptyState } from '../../components/ui/EmptyState';
import { PageHeader } from '../../components/ui/PageHeader';
import { StatTile } from '../../components/ui/StatTile';
import { StatusBadge } from '../../components/ui/StatusBadge';
import { ApiError } from '../../lib/http';
import { useBuildingSnapshot, useBuildings } from '../../lib/api/buildingQueries';
import type { RoomDto } from '../../lib/api/types';

export function BuildingsPage() {
  const { data: buildings, isLoading, error } = useBuildings();

  return (
    <div className="mx-auto max-w-[1400px]">
      <PageHeader
        title="Quản lý tòa nhà"
        description="Quản lý thông tin tòa nhà và các tầng"
        breadcrumb={['Trang chủ', 'Tòa nhà']}
        actions={
          <button
            type="button"
            disabled
            title="Tạo tòa nhà cần quy trình cấp quyền chủ sở hữu chưa được phê duyệt"
            className="flex h-10 items-center gap-2 rounded-lg bg-[#1d4ed8] px-4 text-sm font-semibold text-white opacity-50"
          >
            <span className="material-symbols-outlined text-lg">add</span>
            Thêm tòa nhà
          </button>
        }
      />

      <GateNotice error={error} />

      {isLoading && <LoadingBlock label="Đang tải danh sách tòa nhà…" />}

      {!isLoading && !error && buildings && buildings.length === 0 && (
        <EmptyState
          icon="apartment"
          title="Chưa có tòa nhà nào"
          description="Máy chủ chưa trả về tòa nhà nào cho tài khoản này. Quyền truy cập tòa nhà phụ thuộc vào tư cách thành viên đang được cấp trên máy chủ."
        />
      )}

      {buildings && buildings.length > 0 && (
        <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          {buildings.map((building) => (
            <Link
              key={building.id}
              to={`/buildings/${building.id}`}
              className="group flex flex-col gap-3 rounded-xl border border-outline-variant bg-surface-lowest p-5 shadow-sm transition hover:border-primary/40 hover:shadow-card"
            >
              <div className="flex items-start gap-3">
                <span className="flex h-11 w-11 shrink-0 items-center justify-center rounded-lg bg-surface-low text-primary">
                  <span className="material-symbols-outlined text-2xl">apartment</span>
                </span>
                <div className="min-w-0">
                  <p className="truncate font-display text-base font-bold text-on-surface">
                    {building.name}
                  </p>
                  <p className="mt-0.5 flex items-start gap-1 text-xs text-on-surface-variant">
                    <span className="material-symbols-outlined text-sm">location_on</span>
                    <span className="line-clamp-2">{building.address}</span>
                  </p>
                </div>
              </div>
              {building.description && (
                <p className="line-clamp-2 text-xs leading-relaxed text-on-surface-variant">
                  {building.description}
                </p>
              )}
              <span className="mt-auto flex items-center gap-1 text-xs font-semibold text-primary">
                Xem chi tiết
                <span className="material-symbols-outlined text-base transition group-hover:translate-x-0.5">
                  arrow_forward
                </span>
              </span>
            </Link>
          ))}
        </div>
      )}
    </div>
  );
}

export function BuildingDetailPage() {
  const { buildingId } = useParams<{ buildingId: string }>();
  const { snapshot, loading, error } = useBuildingSnapshot(buildingId);

  return (
    <div className="mx-auto max-w-[1400px]">
      <PageHeader
        title="Quản lý tòa nhà"
        description="Quản lý thông tin tòa nhà và các tầng"
        breadcrumb={['Trang chủ', 'Tòa nhà', snapshot?.building.name ?? 'Chi tiết']}
        actions={
          <>
            <button
              type="button"
              disabled
              title="Chỉnh sửa tòa nhà cần quyền quản lý được cấp trên máy chủ"
              className="flex h-10 items-center gap-2 rounded-lg border border-outline-variant px-4 text-sm font-semibold text-on-surface-variant opacity-60"
            >
              <span className="material-symbols-outlined text-lg">edit</span>
              Chỉnh sửa tòa nhà
            </button>
            <button
              type="button"
              disabled
              title="Thêm tầng cần quyền quản lý được cấp trên máy chủ"
              className="flex h-10 items-center gap-2 rounded-lg bg-[#1d4ed8] px-4 text-sm font-semibold text-white opacity-50"
            >
              <span className="material-symbols-outlined text-lg">add</span>
              Thêm tầng
            </button>
          </>
        }
      />

      <GateNotice error={error} />

      {loading && <LoadingBlock label="Đang tải thông tin tòa nhà…" />}

      {!loading && snapshot && (
        <>
          <section className="mb-4 rounded-xl border border-outline-variant bg-surface-lowest p-5">
            <div className="flex flex-col gap-5 lg:flex-row">
              <div className="relative flex h-48 w-full shrink-0 items-center justify-center overflow-hidden rounded-lg bg-gradient-to-br from-[#0f766e] via-[#0b5f5a] to-[#0b1c30] lg:h-44 lg:w-72">
                <span className="material-symbols-outlined text-6xl text-white/25">apartment</span>
                <div className="absolute inset-0 flex items-end justify-start bg-gradient-to-t from-black/45 to-transparent p-2.5">
                  <span className="inline-flex items-center gap-1.5 rounded-md bg-black/40 px-2.5 py-1 text-[11px] font-semibold text-white backdrop-blur-sm">
                    <span className="material-symbols-outlined text-sm">image</span>
                    Chưa có ảnh tòa nhà
                  </span>
                </div>
              </div>

              <div className="min-w-0 flex-1">
                <h2 className="font-display text-2xl font-bold tracking-tight text-on-surface">
                  {snapshot.building.name}
                </h2>
                <p className="mt-1.5 flex items-start gap-1.5 text-sm text-on-surface-variant">
                  <span className="material-symbols-outlined text-base">location_on</span>
                  <span>{snapshot.building.address}</span>
                </p>
                {snapshot.building.description && (
                  <p className="mt-3 rounded-lg bg-surface-low px-4 py-3 text-sm leading-relaxed text-on-surface-variant">
                    {snapshot.building.description}
                  </p>
                )}

                <div className="mt-4 grid grid-cols-2 gap-3 xl:grid-cols-4">
                  <StatTile
                    icon="layers"
                    value={String(snapshot.floors.length)}
                    label="Tổng số tầng"
                    tone="primary"
                  />
                  <StatTile
                    icon="meeting_room"
                    value={String(snapshot.roomCount)}
                    label="Tổng số phòng"
                    tone="primary"
                  />
                  <StatTile
                    icon="group"
                    value={String(snapshot.occupiedCount)}
                    label="Đã thuê"
                    tone="green"
                  />
                  <StatTile
                    icon="pie_chart"
                    value={`${snapshot.occupancyRate}%`}
                    label="Tỷ lệ lấp đầy"
                    tone="orange"
                  />
                </div>
              </div>
            </div>
          </section>

          <section className="overflow-hidden rounded-xl border border-outline-variant bg-surface-lowest">
            <header className="flex items-center gap-2.5 border-b border-outline-variant px-5 py-4">
              <span className="flex h-9 w-9 items-center justify-center rounded-lg bg-surface-low text-primary">
                <span className="material-symbols-outlined text-xl">apartment</span>
              </span>
              <div className="leading-tight">
                <p className="font-display text-base font-bold text-on-surface">Danh sách tầng</p>
                <p className="text-xs text-on-surface-variant">Quản lý các tầng trong tòa nhà</p>
              </div>
            </header>

            {snapshot.floors.length === 0 ? (
              <div className="p-6">
                <EmptyState
                  icon="layers"
                  title="Tòa nhà chưa có tầng nào"
                  description="Các tầng sẽ xuất hiện ở đây sau khi được tạo trên máy chủ."
                />
              </div>
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full min-w-[900px] border-collapse text-sm">
                  <thead>
                    <tr className="bg-surface-low text-left text-xs font-semibold text-on-surface-variant">
                      <th className="px-5 py-3">Tầng</th>
                      <th className="px-5 py-3">Sơ đồ mặt bằng</th>
                      <th className="px-5 py-3">Số phòng</th>
                      <th className="px-5 py-3">Trạng thái</th>
                      <th className="px-5 py-3">Ghi chú</th>
                      <th className="px-5 py-3">Hành động</th>
                    </tr>
                  </thead>
                  <tbody>
                    {snapshot.floors.map((floor) => {
                      const floorRooms = snapshot.roomsByFloor.get(floor.id) ?? [];
                      return (
                        <tr key={floor.id} className="border-t border-outline-variant">
                          <td className="px-5 py-3.5">
                            <p className="font-semibold text-on-surface">Tầng {floor.floorNumber}</p>
                            <p className="text-xs text-on-surface-variant">
                              {floor.floorNumber === 0 ? 'Tầng trệt' : floor.name}
                            </p>
                          </td>
                          <td className="px-5 py-3.5">
                            <FloorThumbnail rooms={floorRooms} />
                          </td>
                          <td className="px-5 py-3.5 tnum text-on-surface">{floorRooms.length}</td>
                          <td className="px-5 py-3.5">
                            <StatusBadge tone="success" withDot>
                              Hoạt động
                            </StatusBadge>
                          </td>
                          <td className="max-w-[260px] px-5 py-3.5 text-xs leading-relaxed text-on-surface-variant">
                            {floor.description ?? '—'}
                          </td>
                          <td className="px-5 py-3.5">
                            <div className="flex items-center gap-2">
                              <Link
                                to={`/buildings/${snapshot.building.id}/floors/${floor.id}`}
                                className="inline-flex h-9 items-center gap-1.5 rounded-lg border border-outline-variant px-3 text-xs font-semibold text-[#1d4ed8] transition hover:bg-surface-low"
                              >
                                <span className="material-symbols-outlined text-base">visibility</span>
                                Xem sơ đồ
                              </Link>
                              <button
                                type="button"
                                disabled
                                title="Cần quyền quản lý tầng trên máy chủ"
                                className="inline-flex h-9 items-center gap-1.5 rounded-lg border border-outline-variant px-3 text-xs font-semibold text-on-surface-variant opacity-55"
                              >
                                <span className="material-symbols-outlined text-base">edit</span>
                                Chỉnh sửa
                              </button>
                            </div>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        </>
      )}

      {!loading && !error && !snapshot && (
        <EmptyState
          icon="apartment"
          title="Không tìm thấy tòa nhà"
          description="Tòa nhà không tồn tại hoặc tài khoản này không có quyền truy cập."
          action={
            <Link to="/buildings" className="text-sm font-semibold text-primary hover:underline">
              Quay lại danh sách tòa nhà
            </Link>
          }
        />
      )}
    </div>
  );
}

/** Miniature floor plan: one tinted block per room, ordered by room number. */
function FloorThumbnail({ rooms }: { rooms: RoomDto[] }) {
  if (rooms.length === 0) {
    return <span className="text-xs text-outline">Chưa có phòng</span>;
  }
  return (
    <div className="flex max-w-[190px] flex-wrap gap-0.5 rounded border border-outline bg-surface p-1">
      {rooms.slice(0, 24).map((room) => (
        <span
          key={room.id}
          title={`${room.roomNumber} — ${room.area ?? '?'} m²`}
          className={
            'h-3.5 min-w-3.5 rounded-[1px] ' +
            (room.status === 2
              ? 'bg-[#C6E7C1]'
              : room.status === 1
                ? 'bg-[#FBE7A1]'
                : room.status === 3
                  ? 'bg-[#F7C6B8]'
                  : room.status === 4
                    ? 'bg-[#CBD5E1]'
                    : 'bg-[#E2E8F0]')
          }
        />
      ))}
    </div>
  );
}

/**
 * The building scope gate denies every caller today (no operator-grant
 * store exists), so a 403 here is the expected state — say so plainly
 * instead of showing an empty list as if the building had no data.
 */
function GateNotice({ error }: { error: unknown }) {
  if (!error) {
    return null;
  }
  const apiError = error instanceof ApiError ? error : null;
  const forbidden = apiError?.status === 403;

  if (forbidden) {
    return (
      <div className="mb-4">
        <Alert tone="warning">
          <p className="font-semibold">Máy chủ đang từ chối truy cập tòa nhà (403).</p>
          <p className="mt-1 text-sm leading-relaxed">
            Cổng phân quyền theo tòa nhà hiện chưa có nguồn dữ liệu thành viên, nên mọi
            yêu cầu đọc đều bị từ chối. Dữ liệu sẽ hiển thị khi quy trình cấp quyền
            chủ sở hữu được phê duyệt và bật trên máy chủ.
          </p>
          {apiError?.traceId && (
            <p className="mt-2 font-mono text-[11px] text-on-surface-variant">
              traceId: {apiError.traceId}
            </p>
          )}
        </Alert>
      </div>
    );
  }

  return (
    <div className="mb-4">
      <Alert tone="error">
        {apiError?.message ?? 'Không tải được dữ liệu tòa nhà.'}
        {apiError?.traceId && (
          <span className="mt-1 block font-mono text-[11px]">traceId: {apiError.traceId}</span>
        )}
      </Alert>
    </div>
  );
}

function LoadingBlock({ label }: { label: string }) {
  return (
    <div className="flex items-center gap-2.5 rounded-xl border border-outline-variant bg-surface-lowest px-5 py-8 text-sm text-on-surface-variant">
      <span className="material-symbols-outlined animate-spin text-lg">sync</span>
      {label}
    </div>
  );
}
