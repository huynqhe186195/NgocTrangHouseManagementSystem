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
  useRoomPhotos,
  useRooms,
  useUpdateRoomRent,
  useUploadRoomPhoto,
} from '../../lib/api/buildingQueries';
import { describeMutationError } from '../../lib/api/mutationErrors';
import { fileToBase64, roomsApi } from '../../lib/api/buildingApi';
import { equipmentConditionMeta, roomStatusMeta } from '../../lib/api/types';
import type { RoomEquipmentDto, RoomPhotoDto } from '../../lib/api/types';
import { formatVnd } from '../../lib/format';
import { FloorPlan } from '../buildings/FloorPlan';
import { EquipmentFormDialog } from './EquipmentFormDialog';

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

  const equipment = useRoomEquipment(roomId);
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

  const floorRooms = useMemo(
    () => (room ? (rooms.data ?? []).filter((item) => item.floorId === room.floorId) : []),
    [rooms.data, room],
  );

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

  return (
    <div className="mx-auto max-w-[1400px]">
      <PageHeader
        title={`Phòng ${room.roomNumber}`}
        description={room.name}
        breadcrumb={[
          'Trang chủ',
          'Tòa nhà',
          building?.name ?? '...',
          floor ? `Tầng ${floor.floorNumber}` : '...',
          `Phòng ${room.roomNumber}`,
        ]}
        actions={
          <>
            <Link
              to={`/buildings/${building?.id ?? ''}/floors/${floor?.id ?? ''}`}
              className="flex h-10 items-center gap-2 rounded-lg border border-outline-variant px-4 text-sm font-semibold text-on-surface-variant transition hover:bg-surface-low"
            >
              <span className="material-symbols-outlined text-lg">map</span>
              Xem sơ đồ tầng
            </Link>
            <Link
              to="/rooms"
              className="flex h-10 items-center gap-2 rounded-lg border border-outline-variant px-4 text-sm font-semibold text-on-surface-variant transition hover:bg-surface-low"
            >
              <span className="material-symbols-outlined text-lg">arrow_back</span>
              Danh sách phòng
            </Link>
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

      <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_360px]">
        <section className="flex flex-col gap-4">
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

          <section className="rounded-xl border border-outline-variant bg-surface-lowest p-4">
            <Tabs
              label="Chi tiết phòng"
              items={TABS}
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
            </TabPanel>

            <TabPanel id="contracts" activeId={activeTab}>
              <GateNotice
                icon="description"
                title="Hợp đồng chưa có API công khai"
                body="Danh sách hợp đồng thuộc app service hợp đồng, hiện bị cổng phân quyền chặn 403 trên máy chủ. Màn này sẽ hiển thị khi API đó được mở."
              />
            </TabPanel>

            <TabPanel id="invoices" activeId={activeTab}>
              <GateNotice
                icon="receipt_long"
                title="Hoá đơn chưa có API công khai"
                body="Hoá đơn thuộc app service tài chính, hiện bị chặn 403. Không có dữ liệu giả được hiển thị thay thế."
              />
            </TabPanel>

            <TabPanel id="maintenance" activeId={activeTab}>
              <GateNotice
                icon="build"
                title="Bảo trì chưa có API công khai"
                body="Yêu cầu bảo trì chưa có endpoint đọc trên máy chủ. Dùng tab Thiết bị để ghi nhận tình trạng từng món."
              />
            </TabPanel>
          </section>
        </section>

        <aside className="flex flex-col gap-4">
          <section className="rounded-xl border border-outline-variant bg-surface-lowest p-5">
            <div className="mb-3 flex items-start justify-between gap-2">
              <h2 className="font-display text-lg font-bold text-on-surface">Thông tin phòng</h2>
              <StatusBadge tone={meta?.tone ?? 'neutral'} withDot>
                {meta?.label ?? '—'}
              </StatusBadge>
            </div>

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
          </section>

          <section className="rounded-xl border border-outline-variant bg-surface-lowest p-5">
            <h2 className="mb-2 font-display text-base font-bold text-on-surface">
              Vị trí trên mặt bằng
            </h2>
            {floor ? (
              <>
                <FloorPlan
                  rooms={floorRooms}
                  items={layoutItems.data ?? []}
                  selectedId={room.id}
                  onSelect={() => undefined}
                  compact
                />
                <p className="mt-2 text-[11px] leading-relaxed text-on-surface-variant">
                  Phòng được tô viền xanh trên sơ đồ tầng {floor.floorNumber}.
                </p>
              </>
            ) : (
              <p className="text-sm text-on-surface-variant">Chưa xác định được tầng của phòng.</p>
            )}
          </section>

          <section className="rounded-xl border border-outline-variant bg-surface-lowest p-5">
            <h2 className="mb-2 font-display text-base font-bold text-on-surface">Thông tin khác</h2>
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
        </aside>
      </div>

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
    <section className="rounded-xl border border-outline-variant bg-surface-lowest p-4">
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
      <table className="w-full min-w-[600px] border-collapse text-sm">
        <thead>
          <tr className="bg-surface-low text-left text-xs font-semibold text-on-surface-variant">
            <th className="px-3 py-2.5">STT</th>
            <th className="px-3 py-2.5">Thiết bị</th>
            <th className="px-3 py-2.5">Số lượng</th>
            <th className="px-3 py-2.5">Tình trạng</th>
            <th className="px-3 py-2.5">Hành động</th>
          </tr>
        </thead>
        <tbody>
          {items.map((item, index) => {
            const condition = equipmentConditionMeta[item.condition];
            return (
              <tr key={item.id} className="border-t border-outline-variant">
                <td className="px-3 py-2.5 tnum text-on-surface-variant">{index + 1}</td>
                <td className="px-3 py-2.5">
                  <p className="font-medium text-on-surface">{item.name}</p>
                  {item.category && (
                    <p className="text-xs text-on-surface-variant">{item.category}</p>
                  )}
                  {item.notes && (
                    <p className="mt-0.5 text-[11px] text-on-surface-variant">{item.notes}</p>
                  )}
                </td>
                <td className="px-3 py-2.5 tnum text-on-surface">{item.quantity}</td>
                <td className="px-3 py-2.5">
                  <StatusBadge tone={condition?.tone ?? 'neutral'} withDot>
                    {condition?.label ?? '—'}
                  </StatusBadge>
                </td>
                <td className="px-3 py-2.5">
                  <div className="flex items-center gap-1.5">
                    <button
                      type="button"
                      onClick={() => onEdit(item)}
                      disabled={!canManage}
                      title={
                        canManage
                          ? undefined
                          : 'Tài khoản này chưa được cấp quyền quản lý phòng trên máy chủ.'
                      }
                      className="inline-flex h-8 items-center gap-1 rounded-lg border border-outline-variant px-2.5 text-xs font-semibold text-on-surface-variant transition hover:bg-surface-low disabled:cursor-not-allowed disabled:opacity-55"
                    >
                      <span className="material-symbols-outlined text-base">edit</span>
                      Sửa
                    </button>
                    <button
                      type="button"
                      onClick={() => onDelete(item)}
                      disabled={!canManage || deletingId === item.id}
                      title={
                        canManage
                          ? undefined
                          : 'Tài khoản này chưa được cấp quyền quản lý phòng trên máy chủ.'
                      }
                      className="inline-flex h-8 items-center gap-1 rounded-lg border border-[#FECDD3] px-2.5 text-xs font-semibold text-[#B91C1C] transition hover:bg-[#FFF1F2] disabled:cursor-not-allowed disabled:opacity-55"
                    >
                      <span className="material-symbols-outlined text-base">delete</span>
                      Xoá
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
