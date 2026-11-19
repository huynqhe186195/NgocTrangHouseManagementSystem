import { useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useQueries } from '@tanstack/react-query';
import { Alert } from '../../components/ui/Alert';
import { Button } from '../../components/ui/Button';
import { ConfirmDialog } from '../../components/ui/ConfirmDialog';
import { EmptyState } from '../../components/ui/EmptyState';
import { MoneyInput } from '../../components/ui/MoneyInput';
import { PageHeader } from '../../components/ui/PageHeader';
import { PhotoGallery } from '../../components/ui/PhotoGallery';
import type { GalleryPhoto } from '../../components/ui/PhotoGallery';
import { SelectField } from '../../components/ui/SelectField';
import { StatusBadge } from '../../components/ui/StatusBadge';
import { TabPanel, Tabs } from '../../components/ui/Tabs';
import type { TabItem } from '../../components/ui/Tabs';
import { useAuth } from '../../lib/auth/AuthProvider';
import { BuildingPolicies } from '../../lib/auth/policies';
import { ApiError } from '../../lib/http';
import {
  useBuildings,
  useDeleteRoomEquipment,
  useDeleteRoomPhoto,
  useFloorLayoutItems,
  useFloors,
  useRoomEquipment,
  useRoomContracts,
  useRoomPhotos,
  useRooms,
  useUpdateRoomRent,
  useUploadRoomPhoto,
} from '../../lib/api/buildingQueries';
import { describeMutationError } from '../../lib/api/mutationErrors';
import { fileToBase64, roomsApi } from '../../lib/api/buildingApi';
import { contractStatusMeta, equipmentConditionMeta, roomDirectionLabels, roomStatusMeta } from '../../lib/api/types';
import type { ContractDto, RoomEquipmentDto, RoomPhotoDto } from '../../lib/api/types';
import { formatDate, formatVnd } from '../../lib/format';
import { FloorPlan } from '../buildings/FloorPlan';
import { EquipmentFormDialog } from './EquipmentFormDialog';
import { equipmentIcon } from './equipmentIcon';
import { RoomFormDialog } from './RoomFormDialog';

const TABS: TabItem[] = [
  { id: 'equipment', label: 'Thiết bị trong phòng' },
  { id: 'contracts', label: 'Hợp đồng' },
  { id: 'invoices', label: 'Hoá đơn' },
  { id: 'maintenance', label: 'Bảo trì' },
];

const MAX_PHOTO_BYTES = 5 * 1024 * 1024;
const GALLERY_PHOTOS = 4;

type EquipmentDialogState =
  | { mode: 'create' }
  | { mode: 'edit'; equipment: RoomEquipmentDto }
  | null;

export function RoomDetailPage() {
  const { roomId } = useParams<{ roomId: string }>();
  const rooms = useRooms();
  const buildings = useBuildings();
  const floors = useFloors();
  const { hasPermission } = useAuth();
  const canManage = hasPermission(BuildingPolicies.roomsManage);

  const [activeTab, setActiveTab] = useState('equipment');
  const [equipmentDialog, setEquipmentDialog] = useState<EquipmentDialogState>(null);
  const [equipmentDeleteTarget, setEquipmentDeleteTarget] = useState<RoomEquipmentDto | null>(null);
  const [photoError, setPhotoError] = useState<string | null>(null);
  const [rentEditing, setRentEditing] = useState(false);
  const [rentDraft, setRentDraft] = useState<number | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [roomFormOpen, setRoomFormOpen] = useState(false);
  const [planFloorId, setPlanFloorId] = useState<string | null>(null);

  const equipment = useRoomEquipment(roomId);
  const contracts = useRoomContracts(roomId);
  const photos = useRoomPhotos(roomId);
  const uploadPhoto = useUploadRoomPhoto(roomId ?? '');
  const deletePhoto = useDeleteRoomPhoto(roomId ?? '');
  const deleteEquipment = useDeleteRoomEquipment(roomId ?? '');
  const updateRent = useUpdateRoomRent(roomId ?? '');

  const room = useMemo(
    () => (rooms.data ?? []).find((item) => item.id === roomId) ?? null,
    [rooms.data, roomId],
  );

  const floor = useMemo(
    () => (room ? ((floors.data ?? []).find((item) => item.id === room.floorId) ?? null) : null),
    [floors.data, room],
  );

  const building = useMemo(
    () =>
      floor
        ? ((buildings.data ?? []).find((item) => item.id === floor.buildingId) ?? null)
        : null,
    [buildings.data, floor],
  );

  const layoutItems = useFloorLayoutItems(floor?.id);

  const equipmentDeleteError = describeMutationError(
    deleteEquipment.error,
    'equipment',
    'delete',
  );
  const rentError = describeMutationError(updateRent.error, 'rent', 'edit');

  const loading = rooms.isLoading || floors.isLoading || buildings.isLoading;
  const error = rooms.error ?? floors.error ?? buildings.error;

  async function handleUpload(file: File) {
    setPhotoError(null);
    if (!roomId) {
      return;
    }
    if (file.size > MAX_PHOTO_BYTES) {
      setPhotoError('Ảnh vượt quá giới hạn 5 MB. Hãy chọn ảnh nhỏ hơn.');
      return;
    }
    try {
      const base64Content = await fileToBase64(file);
      uploadPhoto.mutate(
        { base64Content, fileName: file.name },
        {
          onError: (mutationError) => {
            const view = describeMutationError(mutationError, 'photo', 'create');
            setPhotoError(view?.formError ?? 'Không tải được ảnh lên.');
          },
        },
      );
    } catch {
      setPhotoError('Không đọc được tệp ảnh đã chọn.');
    }
  }

  function confirmEquipmentDelete() {
    if (!equipmentDeleteTarget) {
      return;
    }
    const target = equipmentDeleteTarget;
    deleteEquipment.mutate(target.id, {
      onSuccess: () => {
        setEquipmentDeleteTarget(null);
        setNotice(`Đã xoá thiết bị «${target.name}».`);
      },
    });
  }

  function saveRent() {
    const draft = rentDraft;
    if (draft !== null && Number.isNaN(draft)) {
      return;
    }
    updateRent.mutate(draft, {
      onSuccess: () => {
        setRentEditing(false);
        setNotice('Đã cập nhật giá thuê.');
      },
    });
  }

  if (loading) {
    return (
      <div className="mx-auto max-w-[1400px]">
        <div className="flex items-center gap-2.5 rounded-xl border border-outline-variant bg-surface-lowest px-5 py-8 text-sm text-on-surface-variant">
          <span className="material-symbols-outlined animate-spin text-lg">sync</span>
          Đang tải thông tin phòng…
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="mx-auto max-w-[1400px]">
        <Alert tone={error instanceof ApiError && error.status === 403 ? 'warning' : 'error'}>
          {error instanceof ApiError && error.status === 403
            ? 'Máy chủ đang từ chối truy cập dữ liệu phòng (403) cho tài khoản này.'
            : error instanceof ApiError
              ? error.message
              : 'Không tải được thông tin phòng.'}
        </Alert>
      </div>
    );
  }

  if (!room) {
    return (
      <div className="mx-auto max-w-[1400px]">
        <EmptyState
          icon="meeting_room"
          title="Không tìm thấy phòng"
          description="Phòng không tồn tại hoặc tài khoản này không có quyền truy cập."
          action={
            <Link to="/rooms" className="text-sm font-semibold text-primary hover:underline">
              Quay lại danh sách phòng
            </Link>
          }
        />
      </div>
    );
  }

  const meta = roomStatusMeta[room.status];
  const buildingFloors = building
    ? (floors.data ?? []).filter((item) => item.buildingId === building.id)
    : [];
  const selectedPlanFloorId = planFloorId ?? floor?.id ?? '';
  const planFloor = buildingFloors.find((item) => item.id === selectedPlanFloorId) ?? floor;
  const planRooms = (rooms.data ?? []).filter((item) => item.floorId === planFloor?.id);

  return (
    <div className="mx-auto max-w-[1400px]">
      <PageHeader
        title={`Phòng ${room.roomNumber}`}
        titleAddon={
          <StatusBadge tone={meta?.tone ?? 'neutral'} withDot>
            {meta?.label ?? '—'}
          </StatusBadge>
        }
        breadcrumb={[
          'Trang chủ',
          'Tòa nhà',
          building?.name ?? '...',
          floor ? `Tầng ${floor.floorNumber}` : '...',
          `Phòng ${room.roomNumber}`,
        ]}
        actions={
          <>
            <Button
              variant="secondary"
              icon={<span className="material-symbols-outlined text-base">edit</span>}
              disabled={!canManage}
              title={
                canManage
                  ? undefined
                  : 'Tài khoản này chưa được cấp quyền quản lý phòng trên máy chủ.'
              }
              onClick={() => setRoomFormOpen(true)}
            >
              Chỉnh sửa
            </Button>
            <Button
              disabled
              icon={<span className="material-symbols-outlined text-base">add</span>}
              title="Tạo hợp đồng thuộc phân hệ hợp đồng, hiện chưa mở trên máy chủ (403). Danh sách hợp đồng vẫn đọc được ở tab bên dưới."
            >
              Hợp đồng mới
            </Button>
          </>
        }
      />

      {notice && (
        <div className="mb-4">
          <Alert tone="success">{notice}</Alert>
        </div>
      )}
      {photoError && (
        <div className="mb-4">
          <Alert tone="error">{photoError}</Alert>
        </div>
      )}

      {/* The mockup sets the room's three faces side by side — photos, then the
          facts, then where it sits on the floor — and puts the equipment table
          underneath, full width. Stacking them in one column squeezed the table
          and pushed the locator out of sight. Between 1024 and 1280 there is
          only room for two, so the locator takes the full width under the
          other two rather than leaving a third of the screen empty. */}
      <div className="grid gap-4 lg:grid-cols-2 xl:grid-cols-[minmax(0,1.05fr)_minmax(0,0.95fr)_minmax(0,1fr)]">
        <PhotoSection
          roomId={room.id}
          photoList={photos.data ?? []}
          loading={photos.isLoading}
          canManage={canManage}
          uploading={uploadPhoto.isPending}
          deletingId={deletePhoto.isPending ? (deletePhoto.variables ?? null) : null}
          onUpload={handleUpload}
          onDelete={(photoId) =>
            deletePhoto.mutate(photoId, {
              onError: (mutationError) => {
                const view = describeMutationError(mutationError, 'photo', 'delete');
                setPhotoError(view?.formError ?? 'Không xoá được ảnh.');
              },
            })
          }
        />

        <section className="rounded-xl border border-outline-variant bg-surface-lowest p-5">
          <h2 className="mb-3 font-display text-lg font-bold text-on-surface">Thông tin phòng</h2>

          <dl className="flex flex-col gap-2.5 text-sm">
            <Row icon="apartment" label="Tòa nhà" value={building?.name ?? '—'} />
            <Row icon="layers" label="Tầng" value={floor ? `Tầng ${floor.floorNumber}` : '—'} />
            <Row
              icon="square_foot"
              label="Diện tích"
              value={room.area ? `${room.area} m²` : '—'}
            />
            <Row
              icon="category"
              label="Hình dạng"
              value={
                room.layoutShape === 2
                  ? 'Đa giác'
                  : room.layoutShape === 1
                    ? 'Hình chữ nhật'
                    : '—'
              }
            />
            <Row icon="group" label="Sức chứa" value={`${room.capacity} người`} />
            <Row
              icon="explore"
              label="Hướng"
              value={room.direction === null ? '—' : roomDirectionLabels[room.direction]}
            />
            <Row icon="meeting_room" label="Loại phòng" value={room.roomType ?? '—'} />
            <Row icon="location_on" label="Vị trí" value={room.locationNote ?? '—'} />
            <Row icon="photo_camera" label="Ảnh" value={`${room.photoCount} ảnh`} />
            <Row icon="handyman" label="Thiết bị" value={`${room.equipmentCount} món`} />
          </dl>

          <div className="mt-4 border-t border-outline-variant pt-4">
            <div className="flex items-center justify-between gap-2">
              <span className="text-[11px] font-semibold tracking-[0.06em] text-on-surface-variant uppercase">
                Giá thuê
              </span>
              {canManage && !rentEditing && (
                <button
                  type="button"
                  onClick={() => {
                    setNotice(null);
                    updateRent.reset();
                    setRentDraft(room.monthlyRent);
                    setRentEditing(true);
                  }}
                  className="text-xs font-semibold text-primary hover:underline"
                >
                  Sửa
                </button>
              )}
            </div>

            {rentEditing ? (
              <div className="mt-2 flex flex-col gap-2">
                <MoneyInput
                  label="Giá thuê mỗi tháng"
                  value={rentDraft}
                  onChange={setRentDraft}
                  error={rentError?.fieldErrors.monthlyRent}
                  hint="Để trống nếu phòng chưa có giá."
                />
                {rentError && !rentError.fieldErrors.monthlyRent && (
                  <Alert tone="error">{rentError.formError}</Alert>
                )}
                <div className="flex justify-end gap-2">
                  <Button
                    variant="secondary"
                    onClick={() => setRentEditing(false)}
                    disabled={updateRent.isPending}
                  >
                    Hủy
                  </Button>
                  <Button
                    onClick={saveRent}
                    loading={updateRent.isPending}
                    disabled={rentDraft !== null && Number.isNaN(rentDraft)}
                  >
                    Lưu giá
                  </Button>
                </div>
              </div>
            ) : (
              <p className="mt-1 font-display text-2xl font-bold text-on-surface tnum">
                {room.monthlyRent === null ? '—' : `${formatVnd(room.monthlyRent)}/tháng`}
              </p>
            )}
          </div>

          <div className="mt-4 border-t border-outline-variant pt-4">
            <p className="text-[11px] font-semibold tracking-[0.06em] text-on-surface-variant uppercase">
              Mô tả
            </p>
            <p className="mt-1 text-sm leading-relaxed text-on-surface-variant">
              {room.description ?? 'Không có mô tả.'}
            </p>
          </div>
        </section>

        <section className="rounded-xl border border-outline-variant bg-surface-lowest p-5 lg:col-span-2 xl:col-span-1">
          <h2 className="mb-3 font-display text-lg font-bold text-on-surface">
            Vị trí trên sơ đồ tầng
          </h2>

          {buildingFloors.length > 1 ? (
            <SelectField
              label="Chọn tầng"
              options={buildingFloors.map((item) => ({
                value: item.id,
                label: `Tầng ${item.floorNumber}${item.name ? ` — ${item.name}` : ''}`,
              }))}
              value={selectedPlanFloorId}
              onChange={(event) => setPlanFloorId(event.target.value)}
            />
          ) : (
            planFloor && (
              <p className="mb-2 text-sm text-on-surface-variant">
                Tầng {planFloor.floorNumber}
                {planFloor.name ? ` — ${planFloor.name}` : ''}
              </p>
            )
          )}

          <div className="mt-3">
            {planFloor ? (
              <FloorPlan
                rooms={planRooms}
                items={planFloor.id === floor?.id ? (layoutItems.data ?? []) : []}
                selectedId={room.id}
                onSelect={() => undefined}
                compact
              />
            ) : (
              <p className="text-sm text-on-surface-variant">
                Chưa xác định được tầng của phòng.
              </p>
            )}
          </div>

          <p className="mt-3 flex items-start gap-1.5 rounded-lg bg-surface-low p-2.5 text-[11px] leading-relaxed text-on-surface-variant">
            <span className="material-symbols-outlined text-base">info</span>
            {room.layoutX === null || room.layoutY === null
              ? `Phòng ${room.roomNumber} chưa được đặt toạ độ trên sơ đồ. Mở màn thiết kế để kéo phòng vào vị trí thật.`
              : `Phòng ${room.roomNumber} được tô viền xanh trên sơ đồ tầng ${planFloor?.floorNumber ?? ''}.`}
          </p>
        </section>
      </div>

      <section className="mt-4 rounded-xl border border-outline-variant bg-surface-lowest p-4">
        <Tabs
          label="Chi tiết phòng"
          items={TABS.map((item) =>
            item.id === 'equipment'
              ? { ...item, badge: equipment.data?.length ?? undefined }
              : item,
          )}
          activeId={activeTab}
          onChange={setActiveTab}
          action={
            activeTab === 'equipment' ? (
              <Button
                variant="secondary"
                icon={<span className="material-symbols-outlined text-base">add</span>}
                disabled={!canManage}
                title={
                  canManage
                    ? undefined
                    : 'Tài khoản này chưa được cấp quyền quản lý phòng trên máy chủ.'
                }
                onClick={() => {
                  setNotice(null);
                  setEquipmentDialog({ mode: 'create' });
                }}
              >
                Thêm thiết bị
              </Button>
            ) : undefined
          }
        />

        <TabPanel id="equipment" activeId={activeTab}>
          <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1fr)_320px]">
            <div className="min-w-0 rounded-lg border border-outline-variant">
              <div className="flex items-center gap-2 border-b border-outline-variant px-4 py-3">
                <span className="material-symbols-outlined text-lg text-primary">devices</span>
                <h3 className="font-display text-sm font-bold text-on-surface">
                  Danh sách thiết bị
                </h3>
              </div>
              <EquipmentTab
                items={equipment.data ?? []}
                loading={equipment.isLoading}
                error={equipment.error}
                canManage={canManage}
                deletingId={deleteEquipment.isPending ? (deleteEquipment.variables ?? null) : null}
                onEdit={(item) => {
                  setNotice(null);
                  setEquipmentDialog({ mode: 'edit', equipment: item });
                }}
                onDelete={(item) => {
                  setNotice(null);
                  deleteEquipment.reset();
                  setEquipmentDeleteTarget(item);
                }}
              />
            </div>

            <section className="min-w-0 rounded-lg border border-outline-variant p-4">
              <h3 className="mb-2 font-display text-sm font-bold text-on-surface">Thông tin khác</h3>
              <p className="text-[11px] font-semibold tracking-[0.06em] text-on-surface-variant uppercase">
                Mô tả
              </p>
              <p className="mt-1 text-sm leading-relaxed text-on-surface-variant">
                {room.description ?? 'Không có mô tả.'}
              </p>
              <p className="mt-4 text-[11px] font-semibold tracking-[0.06em] text-on-surface-variant uppercase">
                Ghi chú
              </p>
              <p className="mt-1 text-sm leading-relaxed text-on-surface-variant">
                Chưa có trường ghi chú riêng trên máy chủ.
              </p>
            </section>
          </div>
        </TabPanel>

        <TabPanel id="contracts" activeId={activeTab}>
          <ContractsTab
            contracts={contracts.data ?? []}
            loading={contracts.isLoading}
            error={contracts.error}
          />
        </TabPanel>

        <TabPanel id="invoices" activeId={activeTab}>
          <GateNotice
            icon="receipt_long"
            title="Hoá đơn chưa có đường đọc cho giao diện"
            body="Toàn bộ dịch vụ hoá đơn trên máy chủ đều đang đóng truy cập từ xa, và các route phát hành / ghi công nợ chính thức chỉ chạy trên làn staging dùng một lần. Vì vậy giao diện chưa có số liệu hoá đơn nào để hiển thị, và không có số liệu giả được đặt vào đây."
          />
        </TabPanel>

        <TabPanel id="maintenance" activeId={activeTab}>
          <GateNotice
            icon="build"
            title="Bảo trì chưa được xây dựng"
            body="Hệ thống chưa có phiếu yêu cầu bảo trì: chưa có bảng dữ liệu và chưa có endpoint. Dùng tab Thiết bị để ghi nhận tình trạng từng món cho tới khi phân hệ này được làm."
          />
        </TabPanel>
      </section>

      {roomFormOpen && (
        <RoomFormDialog open mode="edit" initial={room} onClose={() => setRoomFormOpen(false)} />
      )}

      {equipmentDialog && (
        <EquipmentFormDialog
          open
          roomId={room.id}
          mode={equipmentDialog.mode}
          initial={equipmentDialog.mode === 'edit' ? equipmentDialog.equipment : null}
          onClose={() => setEquipmentDialog(null)}
        />
      )}

      <ConfirmDialog
        open={equipmentDeleteTarget !== null}
        title={
          equipmentDeleteTarget ? `Xoá thiết bị «${equipmentDeleteTarget.name}»?` : 'Xoá thiết bị?'
        }
        description="Thiết bị sẽ bị xoá khỏi phòng. Thao tác này không khôi phục được từ giao diện."
        pending={deleteEquipment.isPending}
        error={equipmentDeleteError?.formError ?? null}
        traceId={equipmentDeleteError?.traceId ?? null}
        onCancel={() => setEquipmentDeleteTarget(null)}
        onConfirm={confirmEquipmentDelete}
      />
    </div>
  );
}

/**
 * Fetches bytes for the four photos the gallery can show at once. The rest
 * are counted, not downloaded, so a room with many photos still paints fast.
 */
function PhotoSection({
  roomId,
  photoList,
  loading,
  canManage,
  uploading,
  deletingId,
  onUpload,
  onDelete,
}: {
  roomId: string;
  photoList: RoomPhotoDto[];
  loading: boolean;
  canManage: boolean;
  uploading: boolean;
  deletingId: string | null;
  onUpload: (file: File) => void;
  onDelete: (photoId: string) => void;
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
    <section className="flex flex-col rounded-xl border border-outline-variant bg-surface-lowest p-4">
      <div className="mb-3 flex items-center justify-between gap-2">
        <h2 className="font-display text-base font-bold text-on-surface">Ảnh phòng</h2>
        {canManage && (
          <label
            className={
              'inline-flex h-9 cursor-pointer items-center gap-1.5 rounded-lg border border-outline-variant px-3 text-xs font-semibold text-on-surface-variant transition hover:bg-surface-low ' +
              (uploading ? 'pointer-events-none opacity-55' : '')
            }
          >
            <span className="material-symbols-outlined text-base">
              {uploading ? 'hourglass' : 'upload'}
            </span>
            {uploading ? 'Đang tải…' : 'Tải ảnh lên'}
            <input
              type="file"
              accept="image/png,image/jpeg,image/webp"
              className="sr-only"
              disabled={uploading}
              onChange={(event) => {
                const file = event.target.files?.[0];
                event.target.value = '';
                if (file) {
                  onUpload(file);
                }
              }}
            />
          </label>
        )}
      </div>

      <PhotoGallery
        photos={gallery}
        loading={loading || (visible.length > 0 && gallery.length === 0)}
        emptyLabel={ordered.length === 0 ? 'Phòng chưa có ảnh nào' : 'Đang tải ảnh…'}
        onDelete={canManage ? onDelete : undefined}
        deletingId={deletingId}
      />

      {hidden > 0 && (
        <p className="mt-2 text-xs text-on-surface-variant">
          Còn {hidden} ảnh nữa chưa hiển thị trong khung xem nhanh này.
        </p>
      )}

      <p className="mt-3 text-[11px] leading-relaxed text-on-surface-variant">
        Chỉ nhận ảnh PNG, JPEG hoặc WebP tối đa 5 MB. Máy chủ kiểm tra nội dung tệp thật chứ
        không tin phần mở rộng.
      </p>
    </section>
  );
}

/**
 * The mockup gives every equipment row its own little line icon. The server
 * only stores a free-text name and category, so the icon is inferred from the
 * words the owner typed — an unmatched name falls back to a generic tool.
 */

function EquipmentTab({
  items,
  loading,
  error,
  canManage,
  deletingId,
  onEdit,
  onDelete,
}: {
  items: RoomEquipmentDto[];
  loading: boolean;
  error: unknown;
  canManage: boolean;
  deletingId: string | null;
  onEdit: (item: RoomEquipmentDto) => void;
  onDelete: (item: RoomEquipmentDto) => void;
}) {
  if (loading) {
    return (
      <div className="flex items-center gap-2 py-6 text-sm text-on-surface-variant">
        <span className="material-symbols-outlined animate-spin text-lg">sync</span>
        Đang tải thiết bị…
      </div>
    );
  }

  if (error) {
    return (
      <Alert tone={error instanceof ApiError && error.status === 403 ? 'warning' : 'error'}>
        {error instanceof ApiError ? error.message : 'Không tải được danh sách thiết bị.'}
      </Alert>
    );
  }

  if (items.length === 0) {
    return (
      <EmptyState
        icon="handyman"
        title="Chưa có thiết bị nào"
        description="Thêm máy lạnh, giường, tủ… để khách xem phòng biết phòng có gì."
      />
    );
  }

  return (
    <div className="overflow-x-auto">
      <table className="w-full border-collapse text-sm sm:min-w-[420px]">
        <thead>
          <tr className="bg-surface-low text-left text-xs font-semibold text-on-surface-variant">
            <th className="hidden px-3 py-2.5 sm:table-cell">STT</th>
            <th className="px-2 py-2.5 sm:px-3">Thiết bị</th>
            <th className="w-8 px-1 py-2.5 text-center sm:w-auto sm:px-3 sm:text-left">
              <span className="sm:hidden">SL</span>
              <span className="hidden sm:inline">Số lượng</span>
            </th>
            <th className="px-2 py-2.5 sm:px-3">Tình trạng</th>
            <th className="px-2 py-2.5 text-right sm:px-3 sm:text-left">Hành động</th>
          </tr>
        </thead>
        <tbody>
          {items.map((item, index) => {
            const condition = equipmentConditionMeta[item.condition];
            return (
              <tr key={item.id} className="border-t border-outline-variant">
                <td className="hidden px-3 py-2.5 tnum text-on-surface-variant sm:table-cell">
                  {index + 1}
                </td>
                <td className="px-2 py-2.5 sm:px-3">
                  <div className="flex items-start gap-2">
                    <span className="material-symbols-outlined hidden shrink-0 text-lg text-on-surface-variant sm:inline">
                      {equipmentIcon(item)}
                    </span>
                    <div className="min-w-0">
                      <p className="font-medium text-on-surface">{item.name}</p>
                      {item.category && (
                        <p className="text-xs text-on-surface-variant">{item.category}</p>
                      )}
                      {item.notes && (
                        <p className="mt-0.5 text-[11px] text-on-surface-variant">{item.notes}</p>
                      )}
                    </div>
                  </div>
                </td>
                <td className="w-8 px-1 py-2.5 text-center tnum text-on-surface sm:w-auto sm:px-3 sm:text-left">
                  {item.quantity}
                </td>
                <td className="px-2 py-2.5 sm:px-3">
                  <StatusBadge tone={condition?.tone ?? 'neutral'} withDot>
                    {condition?.label ?? '—'}
                  </StatusBadge>
                </td>
                <td className="px-2 py-2.5 sm:px-3">
                  <div className="flex items-center justify-end gap-1.5 sm:justify-start">
                    <button
                      type="button"
                      onClick={() => onEdit(item)}
                      disabled={!canManage}
                      aria-label={`Sửa ${item.name}`}
                      title={
                        canManage
                          ? undefined
                          : 'Tài khoản này chưa được cấp quyền quản lý phòng trên máy chủ.'
                      }
                      className="inline-flex h-8 items-center gap-1 rounded-lg border border-outline-variant px-2 text-xs font-semibold text-on-surface-variant transition hover:bg-surface-low disabled:cursor-not-allowed disabled:opacity-55 sm:px-2.5"
                    >
                      <span className="material-symbols-outlined text-base">edit</span>
                      <span className="hidden sm:inline">Sửa</span>
                    </button>
                    <button
                      type="button"
                      onClick={() => onDelete(item)}
                      disabled={!canManage || deletingId === item.id}
                      aria-label={`Xoá ${item.name}`}
                      title={
                        canManage
                          ? undefined
                          : 'Tài khoản này chưa được cấp quyền quản lý phòng trên máy chủ.'
                      }
                      className="inline-flex h-8 items-center gap-1 rounded-lg border border-[#FECDD3] px-2 text-xs font-semibold text-[#B91C1C] transition hover:bg-[#FFF1F2] disabled:cursor-not-allowed disabled:opacity-55 sm:px-2.5"
                    >
                      <span className="material-symbols-outlined text-base">delete</span>
                      <span className="hidden sm:inline">Xoá</span>
                    </button>
                  </div>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function ContractsTab({
  contracts,
  loading,
  error,
}: {
  contracts: ContractDto[];
  loading: boolean;
  error: unknown;
}) {
  if (loading) {
    return (
      <div className="flex items-center gap-2 py-6 text-sm text-on-surface-variant">
        <span className="material-symbols-outlined animate-spin text-lg">sync</span>
        Đang tải hợp đồng…
      </div>
    );
  }

  if (error) {
    // The server answers 403 when the account holds ReadRoom but not
    // ReadContract: knowing a room exists is a different fact from knowing
    // who rents it.
    return (
      <Alert tone={error instanceof ApiError && error.status === 403 ? 'warning' : 'error'}>
        {error instanceof ApiError && error.status === 403
          ? 'Tài khoản này được xem phòng nhưng chưa được cấp quyền xem hợp đồng trên máy chủ.'
          : error instanceof ApiError
            ? error.message
            : 'Không tải được danh sách hợp đồng.'}
      </Alert>
    );
  }

  if (contracts.length === 0) {
    return (
      <EmptyState
        icon="description"
        title="Phòng chưa có hợp đồng nào"
        description="Hợp đồng được tạo ở phân hệ hợp đồng; danh sách này chỉ đọc những hợp đồng máy chủ đã ghi cho phòng này."
      />
    );
  }

  return (
    <>
      {/* A phone would have to scroll a five-column table sideways to reach the
          rent and the status, which are the two facts the owner opens this tab
          for. Below `md` each contract becomes a stacked card instead. */}
      <ul className="space-y-3 md:hidden">
        {contracts.map((contract) => {
          const meta = contractStatusMeta[contract.status];
          return (
            <li
              key={contract.id}
              className="rounded-lg border border-outline-variant bg-surface-lowest p-3"
            >
              <div className="flex items-start justify-between gap-2">
                <span className="font-medium text-on-surface">{contract.contractNumber}</span>
                <StatusBadge tone={meta.tone}>{meta.label}</StatusBadge>
              </div>
              <dl className="mt-2 space-y-1 text-sm">
                <div className="flex justify-between gap-3">
                  <dt className="text-on-surface-variant">Thời hạn</dt>
                  <dd className="text-right text-on-surface">
                    {formatDate(contract.startDate)} –{' '}
                    {contract.endDate ? formatDate(contract.endDate) : 'chưa định'}
                  </dd>
                </div>
                <div className="flex justify-between gap-3">
                  <dt className="text-on-surface-variant">Giá thuê</dt>
                  <dd className="text-right font-medium text-on-surface">
                    {formatVnd(contract.monthlyRent)}
                  </dd>
                </div>
                <div className="flex justify-between gap-3">
                  <dt className="text-on-surface-variant">Tiền cọc</dt>
                  <dd className="text-right text-on-surface">{formatVnd(contract.depositAmount)}</dd>
                </div>
              </dl>
            </li>
          );
        })}
      </ul>

      <div className="hidden overflow-x-auto md:block">
        <table className="w-full border-collapse text-sm">
          <thead>
            <tr className="bg-surface-low text-left text-xs font-semibold text-on-surface-variant">
              <th className="px-3 py-2.5">Số hợp đồng</th>
              <th className="px-3 py-2.5">Thời hạn</th>
              <th className="px-3 py-2.5">Giá thuê</th>
              <th className="px-3 py-2.5">Tiền cọc</th>
              <th className="px-3 py-2.5">Trạng thái</th>
            </tr>
          </thead>
          <tbody>
            {contracts.map((contract) => {
              const meta = contractStatusMeta[contract.status];
              return (
                <tr key={contract.id} className="border-t border-outline-variant">
                  <td className="px-3 py-3 font-medium text-on-surface">{contract.contractNumber}</td>
                  <td className="px-3 py-3 whitespace-nowrap text-on-surface-variant">
                    {formatDate(contract.startDate)} –{' '}
                    {contract.endDate ? formatDate(contract.endDate) : 'chưa định'}
                  </td>
                  <td className="px-3 py-3 whitespace-nowrap text-on-surface">{formatVnd(contract.monthlyRent)}</td>
                  <td className="px-3 py-3 whitespace-nowrap text-on-surface">{formatVnd(contract.depositAmount)}</td>
                  <td className="px-3 py-3">
                    <StatusBadge tone={meta.tone}>{meta.label}</StatusBadge>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </>
  );
}

function GateNotice({ icon, title, body }: { icon: string; title: string; body: string }) {
  return (
    <div className="flex flex-col items-center gap-2 rounded-lg border border-dashed border-outline-variant bg-surface-low px-4 py-8 text-center">
      <span className="material-symbols-outlined text-3xl text-outline">{icon}</span>
      <p className="text-sm font-semibold text-on-surface">{title}</p>
      <p className="max-w-[420px] text-xs leading-relaxed text-on-surface-variant">{body}</p>
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
