import { useMemo, useState } from 'react';
import { Alert } from '../../components/ui/Alert';
import { ConfirmDialog } from '../../components/ui/ConfirmDialog';
import { EmptyState } from '../../components/ui/EmptyState';
import { PageHeader } from '../../components/ui/PageHeader';
import { StatusBadge } from '../../components/ui/StatusBadge';
import { useAuth } from '../../lib/auth/AuthProvider';
import { BuildingPolicies } from '../../lib/auth/policies';
import { ApiError } from '../../lib/http';
import { useBuildings, useDeleteRoom, useFloors, useRooms } from '../../lib/api/buildingQueries';
import { describeMutationError } from '../../lib/api/mutationErrors';
import { roomStatusMeta } from '../../lib/api/types';
import type { RoomDto, RoomStatus } from '../../lib/api/types';
import { RoomFormDialog } from './RoomFormDialog';

const statusFilters: { value: RoomStatus | 'all'; label: string }[] = [
  { value: 'all', label: 'Tất cả' },
  { value: 1, label: 'Trống' },
  { value: 2, label: 'Đang thuê' },
  { value: 3, label: 'Đã đặt' },
  { value: 4, label: 'Bảo trì' },
  { value: 5, label: 'Ngừng dùng' },
];

export function RoomsPage() {
  const buildings = useBuildings();
  const floors = useFloors();
  const rooms = useRooms();
  const { hasPermission } = useAuth();
  const canManage = hasPermission(BuildingPolicies.roomsManage);

  const [status, setStatus] = useState<RoomStatus | 'all'>('all');
  const [query, setQuery] = useState('');
  const [form, setForm] = useState<{ mode: 'create' } | { mode: 'edit'; room: RoomDto } | null>(
    null,
  );
  const [deleteTarget, setDeleteTarget] = useState<RoomDto | null>(null);
  const [flash, setFlash] = useState<{ tone: 'success' | 'warning'; text: string } | null>(null);

  const deleteRoom = useDeleteRoom();
  const deleteError = describeMutationError(deleteRoom.error, 'room', 'delete');

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

  const visible = useMemo(() => {
    const term = query.trim().toLowerCase();
    return (rooms.data ?? [])
      .filter((room) => (status === 'all' ? true : room.status === status))
      .filter((room) => {
        if (!term) {
          return true;
        }
        return (
          room.roomNumber.toLowerCase().includes(term) ||
          room.name.toLowerCase().includes(term)
        );
      })
      .sort((a, b) => a.roomNumber.localeCompare(b.roomNumber, 'vi', { numeric: true }));
  }, [rooms.data, status, query]);

  const counts = useMemo(() => {
    const tally = { total: 0, available: 0, occupied: 0 };
    for (const room of rooms.data ?? []) {
      tally.total += 1;
      if (room.status === 1) tally.available += 1;
      if (room.status === 2) tally.occupied += 1;
    }
    return tally;
  }, [rooms.data]);

  function confirmDelete() {
    if (!deleteTarget) {
      return;
    }
    const target = deleteTarget;
    deleteRoom.mutate(target.id, {
      onSuccess: async () => {
        setDeleteTarget(null);
        // A room with contract/meter history is not removed; the server only
        // flips it to Inactive. Read the refreshed list to tell them apart.
        const { data: fresh } = await rooms.refetch();
        const stillThere = (fresh ?? []).find((room) => room.id === target.id);
        if (stillThere && stillThere.status === 5) {
          setFlash({
            tone: 'warning',
            text: `Phòng ${target.roomNumber} có lịch sử hợp đồng/đồng hồ nên đã được chuyển sang «Ngừng dùng» thay vì xoá.`,
          });
        } else {
          setFlash({ tone: 'success', text: `Đã xoá phòng ${target.roomNumber}.` });
        }
      },
    });
  }

  return (
    <div className="mx-auto max-w-[1400px]">
      <PageHeader
        title="Quản lý phòng"
        description="Tra cứu toàn bộ phòng theo tòa nhà, tầng và trạng thái"
        breadcrumb={['Trang chủ', 'Phòng']}
        actions={
          <button
            type="button"
            onClick={() => {
              setFlash(null);
              setForm({ mode: 'create' });
            }}
            disabled={!canManage}
            title={
              canManage
                ? undefined
                : 'Tài khoản này chưa được cấp quyền quản lý phòng trên máy chủ.'
            }
            className="flex h-10 items-center gap-2 rounded-lg bg-[#1d4ed8] px-4 text-sm font-semibold text-white transition hover:bg-[#1e40af] disabled:cursor-not-allowed disabled:opacity-50"
          >
            <span className="material-symbols-outlined text-lg">add</span>
            Thêm phòng
          </button>
        }
      />

      {flash && (
        <div className="mb-4">
          <Alert tone={flash.tone}>{flash.text}</Alert>
        </div>
      )}

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

      {!loading && !error && rooms.data && (
        <>
          <div className="mb-4 grid grid-cols-3 gap-3 sm:max-w-xl">
            <Summary label="Tổng số phòng" value={counts.total} />
            <Summary label="Đang trống" value={counts.available} tone="green" />
            <Summary label="Đang thuê" value={counts.occupied} tone="orange" />
          </div>

          <div className="mb-4 flex flex-col gap-3 rounded-xl border border-outline-variant bg-surface-lowest p-3 sm:flex-row sm:items-center sm:justify-between">
            <div className="flex flex-wrap gap-1.5">
              {statusFilters.map((option) => {
                const active = status === option.value;
                return (
                  <button
                    key={String(option.value)}
                    type="button"
                    onClick={() => setStatus(option.value)}
                    className={
                      'rounded-lg px-3 py-1.5 text-xs font-semibold transition ' +
                      (active
                        ? 'bg-[#1d4ed8] text-white'
                        : 'text-on-surface-variant hover:bg-surface-low')
                    }
                  >
                    {option.label}
                  </button>
                );
              })}
            </div>
            <div className="relative sm:w-64">
              <span className="material-symbols-outlined pointer-events-none absolute top-1/2 left-3 -translate-y-1/2 text-lg text-outline">
                search
              </span>
              <input
                type="search"
                value={query}
                onChange={(event) => setQuery(event.target.value)}
                placeholder="Tìm mã hoặc tên phòng…"
                className="h-9 w-full rounded-lg border border-outline-variant bg-surface-low pl-10 pr-3 text-sm text-on-surface placeholder:text-outline focus:border-primary focus:bg-surface-lowest focus:outline-none"
              />
            </div>
          </div>

          {visible.length === 0 ? (
            <EmptyState
              icon="meeting_room"
              title="Không có phòng phù hợp"
              description="Thử bỏ bộ lọc trạng thái hoặc từ khóa tìm kiếm khác."
            />
          ) : (
            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
              {visible.map((room) => {
                const meta = roomStatusMeta[room.status];
                const floor = floorById.get(room.floorId);
                const buildingName = floor ? buildingNameById.get(floor.buildingId) : undefined;
                return (
                  <article
                    key={room.id}
                    className="flex flex-col gap-2 rounded-xl border border-outline-variant bg-surface-lowest p-4 transition hover:border-primary/40 hover:shadow-sm"
                  >
                    <div className="flex items-start justify-between gap-2">
                      <div className="min-w-0">
                        <p className="font-display text-base font-bold text-on-surface">
                          Phòng {room.roomNumber}
                        </p>
                        <p className="truncate text-xs text-on-surface-variant">{room.name}</p>
                      </div>
                      <StatusBadge tone={meta?.tone ?? 'neutral'} withDot>
                        {meta?.label ?? '—'}
                      </StatusBadge>
                    </div>
                    <dl className="grid grid-cols-2 gap-1.5 text-xs text-on-surface-variant">
                      <div className="flex items-center gap-1">
                        <span className="material-symbols-outlined text-sm">apartment</span>
                        <span className="truncate">{buildingName ?? '—'}</span>
                      </div>
                      <div className="flex items-center gap-1">
                        <span className="material-symbols-outlined text-sm">layers</span>
                        <span>Tầng {floor?.floorNumber ?? '—'}</span>
                      </div>
                      <div className="flex items-center gap-1">
                        <span className="material-symbols-outlined text-sm">square_foot</span>
                        <span>{room.area ? `${room.area} m²` : '—'}</span>
                      </div>
                      <div className="flex items-center gap-1">
                        <span className="material-symbols-outlined text-sm">group</span>
                        <span>{room.capacity} người</span>
                      </div>
                    </dl>
                    <div className="mt-1 flex items-center gap-2 border-t border-outline-variant pt-2">
                      <button
                        type="button"
                        onClick={() => {
                          setFlash(null);
                          setForm({ mode: 'edit', room });
                        }}
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
                        onClick={() => {
                          setFlash(null);
                          deleteRoom.reset();
                          setDeleteTarget(room);
                        }}
                        disabled={!canManage}
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
                  </article>
                );
              })}
            </div>
          )}
        </>
      )}

      {form && (
        <RoomFormDialog
          open
          mode={form.mode}
          initial={form.mode === 'edit' ? form.room : null}
          onClose={() => setForm(null)}
        />
      )}

      <ConfirmDialog
        open={deleteTarget !== null}
        title={deleteTarget ? `Xoá phòng ${deleteTarget.roomNumber}?` : 'Xoá phòng?'}
        description={
          <>
            <p>Phòng sẽ bị xoá khỏi danh sách. Thao tác này không khôi phục được từ giao diện.</p>
            <p className="mt-2">
              Máy chủ chỉ cho xoá khi phòng không còn hợp đồng đang hiệu lực và không còn đồng hồ
              điện/nước đang hoạt động.
            </p>
          </>
        }
        pending={deleteRoom.isPending}
        error={deleteError?.formError ?? null}
        traceId={deleteError?.traceId ?? null}
        onCancel={() => setDeleteTarget(null)}
        onConfirm={confirmDelete}
      />
    </div>
  );
}

function Summary({
  label,
  value,
  tone = 'primary',
}: {
  label: string;
  value: number;
  tone?: 'primary' | 'green' | 'orange';
}) {
  const tones = {
    primary: 'bg-[#EFF6FF] text-[#1D4ED8]',
    green: 'bg-[#ECFDF5] text-[#047857]',
    orange: 'bg-[#FFF7ED] text-[#C2410C]',
  } as const;
  return (
    <div className={`rounded-xl px-4 py-3 ${tones[tone]}`}>
      <p className="font-display text-xl font-bold tnum">{value}</p>
      <p className="text-xs opacity-80">{label}</p>
    </div>
  );
}
