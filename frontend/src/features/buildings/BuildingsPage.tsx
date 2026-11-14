import { useState } from 'react';
import type { ReactNode } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { Alert } from '../../components/ui/Alert';
import { ConfirmDialog } from '../../components/ui/ConfirmDialog';
import { EmptyState } from '../../components/ui/EmptyState';
import { PageHeader } from '../../components/ui/PageHeader';
import { StatTile } from '../../components/ui/StatTile';
import { StatusBadge } from '../../components/ui/StatusBadge';
import { useAuth } from '../../lib/auth/AuthProvider';
import { BuildingPolicies } from '../../lib/auth/policies';
import { ApiError } from '../../lib/http';
import {
  useBuildingSnapshot,
  useBuildings,
  useDeleteBuilding,
  useDeleteFloor,
} from '../../lib/api/buildingQueries';
import { describeMutationError } from '../../lib/api/mutationErrors';
import type { FloorDto, RoomDto } from '../../lib/api/types';
import { BuildingFormDialog } from './BuildingFormDialog';
import { FloorFormDialog } from './FloorFormDialog';

export function BuildingsPage() {
  const { data: buildings, isLoading, error } = useBuildings();
  const { hasPermission } = useAuth();
  const [createOpen, setCreateOpen] = useState(false);

  const canCreate = hasPermission(BuildingPolicies.buildingsBootstrapOwner);

  return (
    <div className="mx-auto max-w-[1400px]">
      <PageHeader
        title="Quản lý tòa nhà"
        description="Quản lý thông tin tòa nhà và các tầng"
        breadcrumb={['Trang chủ', 'Tòa nhà']}
        actions={
          <ActionButton
            onClick={() => setCreateOpen(true)}
            allowed={canCreate}
            deniedHint="Tài khoản này chưa được cấp quyền khởi tạo tòa nhà (BootstrapOwner) trên máy chủ."
            icon="add"
            primary
          >
            Thêm tòa nhà
          </ActionButton>
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

      <BuildingFormDialog
        open={createOpen}
        mode="create"
        onClose={() => setCreateOpen(false)}
      />
    </div>
  );
}

export function BuildingDetailPage() {
  const { buildingId } = useParams<{ buildingId: string }>();
  const navigate = useNavigate();
  const { snapshot, loading, error } = useBuildingSnapshot(buildingId);
  const { hasPermission } = useAuth();

  const canManageBuilding = hasPermission(BuildingPolicies.buildingsManage);
  const canManageFloors = hasPermission(BuildingPolicies.floorsManage);

  const [buildingDialogOpen, setBuildingDialogOpen] = useState(false);
  const [floorDialog, setFloorDialog] = useState<
    { mode: 'create' } | { mode: 'edit'; floor: FloorDto } | null
  >(null);
  const [floorDeleteTarget, setFloorDeleteTarget] = useState<FloorDto | null>(null);
  const [buildingDeleteOpen, setBuildingDeleteOpen] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  const deleteFloor = useDeleteFloor();
  const deleteBuilding = useDeleteBuilding();

  const floorDeleteError = describeMutationError(deleteFloor.error, 'floor', 'delete');
  const buildingDeleteError = describeMutationError(deleteBuilding.error, 'building', 'delete');

  function confirmFloorDelete() {
    if (!floorDeleteTarget) {
      return;
    }
    deleteFloor.mutate(floorDeleteTarget.id, {
      onSuccess: () => {
        setNotice(`Đã xoá tầng ${floorDeleteTarget.floorNumber}.`);
        setFloorDeleteTarget(null);
      },
    });
  }

  function confirmBuildingDelete() {
    if (!snapshot) {
      return;
    }
    deleteBuilding.mutate(snapshot.building.id, {
      onSuccess: () => {
        setBuildingDeleteOpen(false);
        navigate('/buildings');
      },
    });
  }

  return (
    <div className="mx-auto max-w-[1400px]">
      <PageHeader
        title="Quản lý tòa nhà"
        description="Quản lý thông tin tòa nhà và các tầng"
        breadcrumb={['Trang chủ', 'Tòa nhà', snapshot?.building.name ?? 'Chi tiết']}
        actions={
          <>
            <ActionButton
              onClick={() => setBuildingDialogOpen(true)}
              allowed={canManageBuilding && snapshot !== null}
              deniedHint="Tài khoản này chưa được cấp quyền quản lý tòa nhà trên máy chủ."
              icon="edit"
              variant="secondary"
            >
              Chỉnh sửa tòa nhà
            </ActionButton>
            <ActionButton
              onClick={() => setBuildingDeleteOpen(true)}
              allowed={canManageBuilding && snapshot !== null}
              deniedHint="Tài khoản này chưa được cấp quyền quản lý tòa nhà trên máy chủ."
              icon="delete"
              variant="danger"
            >
              Xoá tòa nhà
            </ActionButton>
            <ActionButton
              onClick={() => setFloorDialog({ mode: 'create' })}
              allowed={canManageFloors && snapshot !== null}
              deniedHint="Tài khoản này chưa được cấp quyền quản lý tầng trên máy chủ."
              icon="add"
              primary
            >
              Thêm tầng
            </ActionButton>
          </>
        }
      />

      <GateNotice error={error} />

      {notice && (
        <div className="mb-4">
          <Alert tone="success">{notice}</Alert>
        </div>
      )}

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
                              <SmallButton
                                onClick={() => setFloorDialog({ mode: 'edit', floor })}
                                allowed={canManageFloors}
                                deniedHint="Tài khoản này chưa được cấp quyền quản lý tầng trên máy chủ."
                                icon="edit"
                              >
                                Chỉnh sửa
                              </SmallButton>
                              <SmallButton
                                onClick={() => {
                                  deleteFloor.reset();
                                  setFloorDeleteTarget(floor);
                                }}
                                allowed={canManageFloors}
                                deniedHint="Tài khoản này chưa được cấp quyền quản lý tầng trên máy chủ."
                                icon="delete"
                                danger
                              >
                                Xoá
                              </SmallButton>
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

      {snapshot && (
        <BuildingFormDialog
          open={buildingDialogOpen}
          mode="edit"
          initial={snapshot.building}
          onClose={() => setBuildingDialogOpen(false)}
        />
      )}

      {snapshot && floorDialog && (
        <FloorFormDialog
          open
          mode={floorDialog.mode}
          buildingId={snapshot.building.id}
          initial={floorDialog.mode === 'edit' ? floorDialog.floor : null}
          onClose={() => setFloorDialog(null)}
        />
      )}

      <ConfirmDialog
        open={floorDeleteTarget !== null}
        title={floorDeleteTarget ? `Xoá tầng ${floorDeleteTarget.floorNumber}?` : 'Xoá tầng?'}
        description={
          <>
            <p>
              Tầng này sẽ bị xoá khỏi tòa nhà. Thao tác này không khôi phục được từ giao diện.
            </p>
            <p className="mt-2">
              Máy chủ chỉ cho xoá khi tầng không còn phòng nào. Nếu còn phòng, hãy xoá hết phòng
              của tầng trước.
            </p>
          </>
        }
        pending={deleteFloor.isPending}
        error={floorDeleteError?.formError ?? null}
        traceId={floorDeleteError?.traceId ?? null}
        onCancel={() => setFloorDeleteTarget(null)}
        onConfirm={confirmFloorDelete}
      />

      <ConfirmDialog
        open={buildingDeleteOpen}
        title={snapshot ? `Xoá tòa nhà «${snapshot.building.name}»?` : 'Xoá tòa nhà?'}
        description={
          <>
            <p>
              Tòa nhà sẽ bị xoá khỏi danh sách. Thao tác này không khôi phục được từ giao diện.
            </p>
            <p className="mt-2">
              Máy chủ chỉ cho xoá khi tòa nhà không còn tầng nào. Nếu còn tầng, hãy xoá hết tầng
              trước.
            </p>
          </>
        }
        pending={deleteBuilding.isPending}
        error={buildingDeleteError?.formError ?? null}
        traceId={buildingDeleteError?.traceId ?? null}
        onCancel={() => setBuildingDeleteOpen(false)}
        onConfirm={confirmBuildingDelete}
      />
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

const BUTTON_BASE =
  'inline-flex h-10 items-center gap-2 rounded-lg px-4 text-sm font-semibold transition ' +
  'disabled:cursor-not-allowed disabled:opacity-50';

const BUTTON_LOOKS = {
  primary: 'bg-[#1d4ed8] text-white hover:bg-[#1e40af]',
  secondary: 'border border-outline-variant bg-surface-lowest text-on-surface hover:bg-surface-low',
  danger: 'border border-[#FECDD3] bg-[#FFF1F2] text-[#B91C1C] hover:bg-[#FFE4E6]',
} as const;

/**
 * A missing server grant is the normal state on the production DI (deny-all),
 * so the button stays visible but disabled with the reason in its tooltip.
 */
function ActionButton({
  onClick,
  allowed,
  deniedHint,
  icon,
  primary = false,
  variant,
  children,
}: {
  onClick: () => void;
  allowed: boolean;
  deniedHint: string;
  icon: string;
  primary?: boolean;
  variant?: 'secondary' | 'danger';
  children: ReactNode;
}) {
  const look = primary ? BUTTON_LOOKS.primary : BUTTON_LOOKS[variant ?? 'secondary'];
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={!allowed}
      title={allowed ? undefined : deniedHint}
      className={`${BUTTON_BASE} ${look}`}
    >
      <span className="material-symbols-outlined text-lg">{icon}</span>
      {children}
    </button>
  );
}

function SmallButton({
  onClick,
  allowed,
  deniedHint,
  icon,
  danger = false,
  children,
}: {
  onClick: () => void;
  allowed: boolean;
  deniedHint: string;
  icon: string;
  danger?: boolean;
  children: ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={!allowed}
      title={allowed ? undefined : deniedHint}
      className={
        'inline-flex h-9 items-center gap-1.5 rounded-lg border px-3 text-xs font-semibold transition disabled:cursor-not-allowed disabled:opacity-55 ' +
        (danger
          ? 'border-[#FECDD3] text-[#B91C1C] hover:bg-[#FFF1F2]'
          : 'border-outline-variant text-on-surface-variant hover:bg-surface-low')
      }
    >
      <span className="material-symbols-outlined text-base">{icon}</span>
      {children}
    </button>
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
