import { useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { Alert } from '../../components/ui/Alert';
import { ConfirmDialog } from '../../components/ui/ConfirmDialog';
import { Dialog } from '../../components/ui/Dialog';
import { EmptyState } from '../../components/ui/EmptyState';
import { PageHeader } from '../../components/ui/PageHeader';
import { StatTile } from '../../components/ui/StatTile';
import { StatusBadge } from '../../components/ui/StatusBadge';
import { useAuth } from '../../lib/auth/AuthProvider';
import { BuildingPolicies } from '../../lib/auth/policies';
import { ApiError } from '../../lib/http';
import {
  useBuildingPhotoContent,
  useBuildingSnapshot,
  useBuildings,
  useDeleteBuilding,
  useDeleteFloor,
  useDeleteBuildingPhoto,
  useFloorLayoutItemsFor,
  useUploadBuildingPhoto,
} from '../../lib/api/buildingQueries';
import { fileToBase64 } from '../../lib/api/buildingApi';
import { describeMutationError } from '../../lib/api/mutationErrors';
import { floorStatusMeta } from '../../lib/api/types';
import type { FloorDto, FloorStatus } from '../../lib/api/types';
import { BuildingFormDialog } from './BuildingFormDialog';
import { BuildingPublicInfoDialog } from './BuildingPublicInfoDialog';
import { FloorFormDialog } from './FloorFormDialog';
import { FloorMiniPlan } from './FloorMiniPlan';

const PAGE_SIZE = 5;

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
  const [publicInfoOpen, setPublicInfoOpen] = useState(false);
  const [floorDialog, setFloorDialog] = useState<
    { mode: 'create' } | { mode: 'edit'; floor: FloorDto } | null
  >(null);
  const [floorDeleteTarget, setFloorDeleteTarget] = useState<FloorDto | null>(null);
  const [buildingDeleteOpen, setBuildingDeleteOpen] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [photoError, setPhotoError] = useState<string | null>(null);

  const deleteFloor = useDeleteFloor();
  const deleteBuilding = useDeleteBuilding();
  const removePhoto = useDeleteBuildingPhoto(buildingId ?? '');

  const floorDeleteError = describeMutationError(deleteFloor.error, 'floor', 'delete');
  const buildingDeleteError = describeMutationError(deleteBuilding.error, 'building', 'delete');

  const floors = snapshot?.floors ?? [];
  const floorIds = floors.map((floor) => floor.id);
  const layoutByFloor = useFloorLayoutItemsFor(floorIds);

  const pageCount = Math.max(1, Math.ceil(floors.length / PAGE_SIZE));
  const currentPage = Math.min(page, pageCount);
  const from = floors.length === 0 ? 0 : (currentPage - 1) * PAGE_SIZE + 1;
  const to = Math.min(currentPage * PAGE_SIZE, floors.length);
  const visibleFloors = floors.slice(from === 0 ? 0 : from - 1, to);

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
        breadcrumbAlign="right"
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
            <div className="flex flex-col gap-5 lg:flex-row lg:items-start">
              <BuildingCover
                buildingId={snapshot.building.id}
                hasPhoto={snapshot.building.hasPhoto}
                canManage={canManageBuilding}
                onError={setPhotoError}
              />

              <div className="min-w-0 flex-1">
                <div className="flex flex-col gap-3 md:flex-row md:flex-wrap md:items-start md:justify-between">
                  <div className="min-w-[220px] flex-1">
                    <h2 className="font-display text-2xl font-bold tracking-tight text-on-surface">
                      {snapshot.building.name}
                    </h2>
                    <p className="mt-1.5 flex items-start gap-1.5 text-sm text-on-surface-variant">
                      <span className="material-symbols-outlined text-base">location_on</span>
                      <span>{snapshot.building.address}</span>
                    </p>
                  </div>

                  <div className="flex shrink-0 flex-wrap items-center gap-2">
                    <ActionButton
                      onClick={() => setBuildingDialogOpen(true)}
                      allowed={canManageBuilding}
                      deniedHint="Tài khoản này chưa được cấp quyền quản lý tòa nhà trên máy chủ."
                      icon="edit"
                    >
                      Chỉnh sửa tòa nhà
                    </ActionButton>
                    <ActionButton
                      onClick={() => setFloorDialog({ mode: 'create' })}
                      allowed={canManageFloors}
                      deniedHint="Tài khoản này chưa được cấp quyền quản lý tầng trên máy chủ."
                      icon="add"
                      primary
                    >
                      Thêm tầng
                    </ActionButton>
                    <RowMenu
                      label="Thao tác khác của tòa nhà"
                      items={[
                        {
                          label: 'Giá và nội quy công bố',
                          icon: 'sell',
                          disabled: !canManageBuilding,
                          disabledHint:
                            'Tài khoản này chưa được cấp quyền quản lý tòa nhà trên máy chủ.',
                          onSelect: () => setPublicInfoOpen(true),
                        },
                        {
                          label: 'Xoá ảnh tòa nhà',
                          icon: 'hide_image',
                          disabled: !canManageBuilding || !snapshot.building.hasPhoto,
                          disabledHint: !canManageBuilding
                            ? 'Tài khoản này chưa được cấp quyền quản lý tòa nhà trên máy chủ.'
                            : 'Tòa nhà chưa có ảnh để xoá.',
                          onSelect: () =>
                            removePhoto.mutate(undefined, {
                              onError: (error) => {
                                const view = describeMutationError(
                                  error,
                                  'buildingPhoto',
                                  'delete',
                                );
                                setPhotoError(view?.formError ?? 'Không xoá được ảnh tòa nhà.');
                              },
                            }),
                        },
                        {
                          label: 'Xoá tòa nhà',
                          icon: 'delete',
                          danger: true,
                          disabled: !canManageBuilding,
                          disabledHint:
                            'Tài khoản này chưa được cấp quyền quản lý tòa nhà trên máy chủ.',
                          onSelect: () => setBuildingDeleteOpen(true),
                        },
                      ]}
                    />
                  </div>
                </div>

                {snapshot.building.description && (
                  <p className="mt-3 rounded-lg bg-surface-low px-4 py-3 text-sm leading-relaxed text-on-surface-variant">
                    {snapshot.building.description}
                  </p>
                )}

                <div className="mt-4 grid grid-cols-2 gap-3 xl:grid-cols-4">
                  <StatTile
                    icon="domain"
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
                    chip={false}
                  />
                </div>
              </div>
            </div>

            {photoError && (
              <div className="mt-4">
                <Alert tone="error">{photoError}</Alert>
              </div>
            )}
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

            {floors.length === 0 ? (
              <div className="p-6">
                <EmptyState
                  icon="layers"
                  title="Tòa nhà chưa có tầng nào"
                  description="Các tầng sẽ xuất hiện ở đây sau khi được tạo trên máy chủ."
                />
              </div>
            ) : (
              <>
                {/* Below `xl` the six-column table cannot hold the plan drawing
                    and the three row actions at once — at 1024 the action cell
                    was being clipped by the card edge. These cards carry the
                    same fields and drive the same handlers, so no function is
                    lost; from `md` they sit two across, which keeps the storey
                    list short at tablet width. */}
                <ul className="grid grid-cols-1 divide-y divide-outline-variant md:grid-cols-2 md:divide-y-0 md:[&>li:nth-child(2n)]:border-l md:[&>li]:border-t xl:hidden">
                  {visibleFloors.map((floor) => {
                    const floorRooms = snapshot.roomsByFloor.get(floor.id) ?? [];
                    return (
                      <li key={floor.id} className="flex flex-col gap-3 border-outline-variant p-4">
                        <div className="flex items-start justify-between gap-3">
                          <div className="min-w-0">
                            <p className="font-semibold text-on-surface">
                              Tầng {floor.floorNumber}
                            </p>
                            {floor.name !== `Tầng ${floor.floorNumber}` && (
                              <p className="text-xs text-on-surface-variant">{floor.name}</p>
                            )}
                            <p className="mt-1 text-xs text-on-surface-variant">
                              {floorRooms.length} phòng
                            </p>
                          </div>
                          <FloorStatusBadge status={floor.status} />
                        </div>

                        <FloorMiniPlan
                          rooms={floorRooms}
                          items={layoutByFloor.get(floor.id) ?? []}
                          height={72}
                        />

                        {floor.description && (
                          <p className="text-xs leading-relaxed text-on-surface-variant">
                            {floor.description}
                          </p>
                        )}

                        <div className="flex flex-wrap items-center gap-2">
                          <FloorActions
                            buildingId={snapshot.building.id}
                            floor={floor}
                            canManageFloors={canManageFloors}
                            onEdit={() => setFloorDialog({ mode: 'edit', floor })}
                            onDesign={() =>
                              navigate(
                                `/buildings/${snapshot.building.id}/floors/${floor.id}/design`,
                              )
                            }
                            onDelete={() => {
                              deleteFloor.reset();
                              setFloorDeleteTarget(floor);
                            }}
                          />
                        </div>
                      </li>
                    );
                  })}
                </ul>

                <div className="hidden overflow-x-auto xl:block">
                  <table className="w-full border-collapse text-sm">
                    <thead>
                      <tr className="bg-[#EFF4FF] text-left text-xs font-semibold text-on-surface-variant">
                        <th className="whitespace-nowrap px-4 py-3">Tầng</th>
                        {/* 194 px of column minus the 2 x 16 px cell padding
                            leaves the mockup's 162 px-wide plan drawing. */}
                        <th className="w-[194px] whitespace-nowrap px-4 py-3">Sơ đồ mặt bằng</th>
                        <th className="whitespace-nowrap px-4 py-3">Số phòng</th>
                        <th className="whitespace-nowrap px-4 py-3">Trạng thái</th>
                        <th className="whitespace-nowrap px-4 py-3">Ghi chú</th>
                        <th className="whitespace-nowrap px-4 py-3">Hành động</th>
                      </tr>
                    </thead>
                    <tbody>
                      {visibleFloors.map((floor) => {
                        const floorRooms = snapshot.roomsByFloor.get(floor.id) ?? [];
                        return (
                          <tr key={floor.id} className="border-t border-outline-variant">
                            <td className="px-4 py-3.5">
                              <p className="whitespace-nowrap font-semibold text-on-surface">
                                Tầng {floor.floorNumber}
                              </p>
                              {floor.name !== `Tầng ${floor.floorNumber}` && (
                                <p className="whitespace-nowrap text-xs text-on-surface-variant">
                                  {floor.name}
                                </p>
                              )}
                            </td>
                            <td className="px-4 py-3.5">
                              <FloorMiniPlan
                                rooms={floorRooms}
                                items={layoutByFloor.get(floor.id) ?? []}
                              />
                            </td>
                            <td className="px-4 py-3.5 tnum text-on-surface">{floorRooms.length}</td>
                            <td className="px-4 py-3.5">
                              <FloorStatusBadge status={floor.status} />
                            </td>
                            <td
                              className="max-w-[240px] px-4 py-3.5 text-xs leading-relaxed text-on-surface-variant"
                              title={floor.description ?? undefined}
                            >
                              {floor.description ?? '—'}
                            </td>
                            <td className="px-4 py-3.5">
                              <div className="flex items-center gap-2 whitespace-nowrap">
                                <FloorActions
                                  buildingId={snapshot.building.id}
                                  floor={floor}
                                  canManageFloors={canManageFloors}
                                  onEdit={() => setFloorDialog({ mode: 'edit', floor })}
                                  onDesign={() =>
                                    navigate(
                                      `/buildings/${snapshot.building.id}/floors/${floor.id}/design`,
                                    )
                                  }
                                  onDelete={() => {
                                    deleteFloor.reset();
                                    setFloorDeleteTarget(floor);
                                  }}
                                />
                              </div>
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>

                <footer className="flex flex-wrap items-center justify-between gap-3 border-t border-outline-variant px-5 py-3.5 text-xs text-on-surface-variant">
                  <span>
                    Hiển thị {from} - {to} của {floors.length} tầng
                  </span>
                  <div className="flex items-center gap-1">
                    <button
                      type="button"
                      aria-label="Trang trước"
                      disabled={currentPage <= 1}
                      onClick={() => setPage(currentPage - 1)}
                      className="flex h-8 w-8 items-center justify-center rounded-lg border border-outline-variant text-on-surface-variant transition hover:bg-surface-low disabled:cursor-not-allowed disabled:opacity-40"
                    >
                      <span className="material-symbols-outlined text-base">chevron_left</span>
                    </button>
                    {Array.from({ length: pageCount }, (_, index) => index + 1).map((number) => (
                      <button
                        key={number}
                        type="button"
                        aria-label={`Trang ${number}`}
                        aria-current={number === currentPage ? 'page' : undefined}
                        onClick={() => setPage(number)}
                        className={
                          'flex h-8 min-w-8 items-center justify-center rounded-lg px-2 text-xs font-semibold transition ' +
                          (number === currentPage
                            ? 'bg-[#1d4ed8] text-white'
                            : 'border border-outline-variant text-on-surface-variant hover:bg-surface-low')
                        }
                      >
                        {number}
                      </button>
                    ))}
                    <button
                      type="button"
                      aria-label="Trang sau"
                      disabled={currentPage >= pageCount}
                      onClick={() => setPage(currentPage + 1)}
                      className="flex h-8 w-8 items-center justify-center rounded-lg border border-outline-variant text-on-surface-variant transition hover:bg-surface-low disabled:cursor-not-allowed disabled:opacity-40"
                    >
                      <span className="material-symbols-outlined text-base">chevron_right</span>
                    </button>
                  </div>
                </footer>
              </>
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

      {snapshot && (
        <BuildingPublicInfoDialog
          open={publicInfoOpen}
          buildingId={snapshot.building.id}
          onClose={() => setPublicInfoOpen(false)}
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

/** The three per-row controls, shared by the table and the phone card list. */
function FloorActions({
  buildingId,
  floor,
  canManageFloors,
  onEdit,
  onDesign,
  onDelete,
}: {
  buildingId: string;
  floor: FloorDto;
  canManageFloors: boolean;
  onEdit: () => void;
  onDesign: () => void;
  onDelete: () => void;
}) {
  return (
    <>
      <Link
        to={`/buildings/${buildingId}/floors/${floor.id}`}
        className="inline-flex h-9 items-center gap-1.5 whitespace-nowrap rounded-lg border border-outline-variant px-3 text-xs font-semibold text-[#1d4ed8] transition hover:bg-surface-low"
      >
        <span className="material-symbols-outlined text-base">visibility</span>
        Xem sơ đồ
      </Link>
      <SmallButton
        onClick={onEdit}
        allowed={canManageFloors}
        deniedHint="Tài khoản này chưa được cấp quyền quản lý tầng trên máy chủ."
        icon="edit"
      >
        Chỉnh sửa
      </SmallButton>
      <RowMenu
        label={`Thao tác khác của tầng ${floor.floorNumber}`}
        items={[
          { label: 'Thiết kế sơ đồ', icon: 'design_services', onSelect: onDesign },
          {
            label: 'Xoá tầng',
            icon: 'delete',
            danger: true,
            disabled: !canManageFloors,
            disabledHint: 'Tài khoản này chưa được cấp quyền quản lý tầng trên máy chủ.',
            onSelect: onDelete,
          },
        ]}
      />
    </>
  );
}

function FloorStatusBadge({ status }: { status: FloorStatus }) {
  const meta = floorStatusMeta[status] ?? floorStatusMeta[1];
  return (
    <StatusBadge tone={meta.tone} withDot>
      {meta.label}
    </StatusBadge>
  );
}

/**
 * The cover image. Uploads go through the same base64 JSON envelope the room
 * photos use, and the server sniffs the payload — a renamed text file is
 * rejected there, not here.
 *
 * The mockup draws one control on the photograph ("Thay đổi ảnh") and nothing
 * else, so removal lives in the building's "..." menu instead of as a second
 * button over the picture.
 */
function BuildingCover({
  buildingId,
  hasPhoto,
  canManage,
  onError,
}: {
  buildingId: string;
  hasPhoto: boolean;
  canManage: boolean;
  onError: (message: string | null) => void;
}) {
  const fileInput = useRef<HTMLInputElement>(null);
  const photo = useBuildingPhotoContent(buildingId, hasPhoto);
  const upload = useUploadBuildingPhoto(buildingId);
  const [zoomed, setZoomed] = useState(false);

  const src = photo.data
    ? `data:${photo.data.contentType};base64,${photo.data.content}`
    : null;

  async function handleFile(file: File | undefined) {
    onError(null);
    if (!file) {
      return;
    }
    if (file.size > 5 * 1024 * 1024) {
      onError('Ảnh vượt quá giới hạn 5 MB.');
      return;
    }
    try {
      const base64Content = await fileToBase64(file);
      upload.mutate(
        { base64Content, fileName: file.name },
        {
          onSuccess: () => onError(null),
          onError: (error) => {
            const view = describeMutationError(error, 'buildingPhoto', 'edit');
            onError(view?.formError ?? 'Không tải được ảnh tòa nhà.');
          },
        },
      );
    } catch {
      onError('Không đọc được tệp ảnh.');
    }
  }

  return (
    <div className="relative h-56 w-full shrink-0 overflow-hidden rounded-lg bg-surface-low lg:h-[259px] lg:w-[254px]">
      {src ? (
        <img src={src} alt={`Ảnh tòa nhà`} className="h-full w-full object-cover" />
      ) : (
        <div className="flex h-full w-full flex-col items-center justify-center gap-1 bg-gradient-to-br from-[#e8eef7] to-[#d7e2f0] text-on-surface-variant">
          <span className="material-symbols-outlined text-4xl text-[#94a3b8]">apartment</span>
          <span className="text-[11px]">
            {photo.isLoading ? 'Đang tải ảnh…' : 'Chưa có ảnh tòa nhà'}
          </span>
          {/* Without a photograph the only way in was the pill in the corner,
              which reads as "replace" and is easy to miss on an empty frame.
              The frame itself offers the upload when there is nothing to
              replace, and says so rather than the screen appearing unfinished. */}
          {!photo.isLoading && (
            <button
              type="button"
              disabled={!canManage || upload.isPending}
              title={canManage ? undefined : 'Tài khoản này chưa được cấp quyền quản lý tòa nhà.'}
              onClick={() => fileInput.current?.click()}
              className="mt-1 inline-flex items-center gap-1.5 rounded-lg border border-[#b6c6da] bg-white/70 px-2.5 py-1.5 text-[11px] font-semibold text-[#334155] transition hover:bg-white disabled:cursor-not-allowed disabled:opacity-60"
            >
              <span className="material-symbols-outlined text-sm">upload</span>
              {upload.isPending ? 'Đang tải lên…' : 'Tải ảnh lên'}
            </button>
          )}
        </div>
      )}

      {/* The whole photograph opens full size: the mockup shows no chrome on
          the cover, so the zoom affordance has to be the image itself. */}
      {src && (
        <button
          type="button"
          aria-label="Xem ảnh tòa nhà cỡ lớn"
          onClick={() => setZoomed(true)}
          className="absolute inset-0 cursor-zoom-in"
        />
      )}

      <Dialog
        open={zoomed && src !== null}
        onClose={() => setZoomed(false)}
        title="Ảnh tòa nhà"
        size="lg"
      >
        {src && (
          <img
            src={src}
            alt="Ảnh tòa nhà cỡ lớn"
            className="max-h-[70vh] w-full rounded-lg object-contain"
          />
        )}
      </Dialog>

      <input
        ref={fileInput}
        type="file"
        accept="image/png,image/jpeg,image/webp"
        className="hidden"
        onChange={(event) => {
          void handleFile(event.target.files?.[0]);
          event.target.value = '';
        }}
      />

      {/* Only with a photograph: on an empty frame the upload lives inside the
          frame itself, and two controls for one action is noise. */}
      {src && (
        <div className="absolute bottom-3 left-3">
          <button
            type="button"
            disabled={!canManage || upload.isPending}
            title={canManage ? undefined : 'Tài khoản này chưa được cấp quyền quản lý tòa nhà.'}
            onClick={() => fileInput.current?.click()}
            className="inline-flex items-center gap-2 rounded-lg bg-[#1F2937]/85 px-3 py-2 text-xs font-semibold text-white transition hover:bg-[#1F2937] disabled:cursor-not-allowed disabled:opacity-60"
          >
            <span className="material-symbols-outlined text-base">photo_camera</span>
            {upload.isPending ? 'Đang tải lên…' : 'Thay đổi ảnh'}
          </button>
        </div>
      )}
    </div>
  );
}

interface MenuItem {
  label: string;
  icon: string;
  danger?: boolean;
  disabled?: boolean;
  disabledHint?: string;
  onSelect: () => void;
}

/**
 * The mockup's "..." button. It is a real menu: it opens on click, closes on
 * outside click or Escape, and every entry performs the action it names.
 */
function RowMenu({ label, items }: { label: string; items: MenuItem[] }) {
  const [open, setOpen] = useState(false);
  const wrapper = useRef<HTMLDivElement>(null);

  return (
    <div
      ref={wrapper}
      className="relative"
      onBlur={(event) => {
        if (!wrapper.current?.contains(event.relatedTarget as Node)) {
          setOpen(false);
        }
      }}
      onKeyDown={(event) => {
        if (event.key === 'Escape') {
          setOpen(false);
        }
      }}
    >
      <button
        type="button"
        aria-label={label}
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((value) => !value)}
        className="flex h-9 w-9 items-center justify-center rounded-lg border border-outline-variant text-on-surface-variant transition hover:bg-surface-low"
      >
        <span className="material-symbols-outlined text-base">more_horiz</span>
      </button>

      {open && (
        <div
          role="menu"
          className="absolute right-0 z-20 mt-1 w-44 overflow-hidden rounded-lg border border-outline-variant bg-surface-lowest shadow-card"
        >
          {items.map((item) => (
            <button
              key={item.label}
              type="button"
              role="menuitem"
              disabled={item.disabled}
              title={item.disabled ? item.disabledHint : undefined}
              onClick={() => {
                setOpen(false);
                item.onSelect();
              }}
              className={
                'flex w-full items-center gap-2 px-3 py-2 text-left text-xs font-medium transition disabled:cursor-not-allowed disabled:opacity-50 ' +
                (item.danger
                  ? 'text-[#B91C1C] hover:bg-[#FFF1F2]'
                  : 'text-on-surface hover:bg-surface-low')
              }
            >
              <span className="material-symbols-outlined text-base">{item.icon}</span>
              {item.label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

const BUTTON_BASE =
  'inline-flex h-10 items-center gap-2 whitespace-nowrap rounded-lg px-4 text-sm font-semibold transition ' +
  'disabled:cursor-not-allowed disabled:opacity-50';

const BUTTON_LOOKS = {
  primary: 'bg-[#1d4ed8] text-white hover:bg-[#1e40af]',
  secondary: 'border border-outline-variant bg-surface-lowest text-on-surface hover:bg-surface-low',
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
  children,
}: {
  onClick: () => void;
  allowed: boolean;
  deniedHint: string;
  icon: string;
  primary?: boolean;
  children: ReactNode;
}) {
  const look = primary ? BUTTON_LOOKS.primary : BUTTON_LOOKS.secondary;
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
  children,
}: {
  onClick: () => void;
  allowed: boolean;
  deniedHint: string;
  icon: string;
  children: ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={!allowed}
      title={allowed ? undefined : deniedHint}
      className="inline-flex h-9 items-center gap-1.5 rounded-lg border border-outline-variant px-3 text-xs font-semibold text-on-surface-variant transition hover:bg-surface-low disabled:cursor-not-allowed disabled:opacity-55"
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
