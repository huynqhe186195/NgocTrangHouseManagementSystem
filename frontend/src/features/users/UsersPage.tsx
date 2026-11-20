import { useMemo, useState } from 'react';
import { Alert } from '../../components/ui/Alert';
import { EmptyState } from '../../components/ui/EmptyState';
import { PageHeader } from '../../components/ui/PageHeader';
import { StatusBadge } from '../../components/ui/StatusBadge';
import { useOperatorMemberships } from '../../lib/api/buildingQueries';
import {
  operatorMembershipStatusMeta,
  type OperatorMembershipSummaryDto,
} from '../../lib/api/types';
import { ApiError } from '../../lib/http';

const PAGE_SIZE = 50;

function formatUtcDateTime(value: string | null | undefined): string {
  if (!value) {
    return '—';
  }
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return '—';
  }
  return new Intl.DateTimeFormat('vi-VN', {
    dateStyle: 'short',
    timeStyle: 'short',
    timeZone: 'Asia/Ho_Chi_Minh',
  }).format(date);
}

function shortId(value: string): string {
  return value.length > 12 ? `${value.slice(0, 8)}…${value.slice(-4)}` : value;
}

function statusOf(row: OperatorMembershipSummaryDto) {
  return operatorMembershipStatusMeta[row.status];
}

export function UsersPage() {
  const [buildingId, setBuildingId] = useState<string | undefined>();
  const [page, setPage] = useState(0);
  const memberships = useOperatorMemberships(buildingId, page * PAGE_SIZE, PAGE_SIZE);

  const rows = memberships.data?.items ?? [];
  const totalCount = memberships.data?.totalCount ?? 0;
  const pageCount = Math.max(1, Math.ceil(totalCount / PAGE_SIZE));
  const buildingOptions = useMemo(() => {
    const buildings = new Map<string, string>();
    for (const row of rows) {
      buildings.set(row.buildingId, row.buildingName);
    }
    return [...buildings.entries()]
      .map(([id, name]) => ({ id, name }))
      .sort((left, right) => left.name.localeCompare(right.name, 'vi'));
  }, [rows]);

  function changeBuilding(next: string) {
    setBuildingId(next || undefined);
    setPage(0);
  }

  const error = memberships.error;
  const forbidden = error instanceof ApiError && error.status === 403;
  const loading = memberships.isLoading;

  return (
    <div className="mx-auto max-w-[1400px]">
      <PageHeader
        title="Người dùng"
        titleAddon={<StatusBadge tone="info">Chỉ xem</StatusBadge>}
        description="Danh sách tài khoản operator được cấp quyền theo tòa nhà bạn được phép xem"
        breadcrumb={['Trang chủ', 'Người dùng']}
      />

      {error && (
        <div className="mb-4">
          <Alert tone={forbidden ? 'warning' : 'error'}>
            {forbidden
              ? 'Máy chủ đang từ chối truy cập danh sách người dùng (403). Quyền xem membership do máy chủ quyết định; giao diện không tự mở rộng phạm vi tòa nhà.'
              : error instanceof ApiError
                ? error.message
                : 'Không tải được danh sách người dùng.'}
          </Alert>
        </div>
      )}

      {loading && !error && (
        <div className="flex items-center gap-2.5 rounded-xl border border-outline-variant bg-surface-lowest px-5 py-8 text-sm text-on-surface-variant">
          <span className="material-symbols-outlined animate-spin text-lg">sync</span>
          Đang tải danh sách người dùng…
        </div>
      )}

      {!loading && !error && (
        <>
          <div className="mb-4 flex flex-col gap-3 rounded-xl border border-outline-variant bg-surface-lowest p-3 sm:flex-row sm:items-center sm:justify-between">
            <label className="flex items-center gap-2 text-sm text-on-surface-variant">
              <span className="material-symbols-outlined text-lg">apartment</span>
              <span>Tòa nhà</span>
              <select
                aria-label="Lọc theo tòa nhà"
                value={buildingId ?? ''}
                onChange={(event) => changeBuilding(event.target.value)}
                className="h-9 min-w-48 rounded-lg border border-outline-variant bg-surface-lowest px-3 text-sm text-on-surface focus:border-primary focus:outline-none"
              >
                <option value="">Tất cả tòa được cấp quyền</option>
                {buildingOptions.map((building) => (
                  <option key={building.id} value={building.id}>
                    {building.name}
                  </option>
                ))}
              </select>
            </label>
            <p className="text-xs text-on-surface-variant">
              {totalCount} membership · chưa mở thao tác cấp hoặc thu hồi quyền
            </p>
          </div>

          {rows.length === 0 ? (
            <EmptyState
              icon="group"
              title="Chưa có membership nào"
              description="Danh sách này chỉ hiển thị quyền operator do máy chủ trả về trong các tòa nhà bạn được cấp quyền xem. Không có dữ liệu không đồng nghĩa với việc giao diện tự cấp thêm quyền."
            />
          ) : (
            <>
              <ul className="space-y-3 xl:hidden">
                {rows.map((row) => (
                  <MembershipCard key={row.id} row={row} />
                ))}
              </ul>

              <div className="hidden overflow-x-auto rounded-xl border border-outline-variant bg-surface-lowest xl:block">
                <table className="w-full border-collapse text-sm">
                  <thead>
                    <tr className="bg-surface-low text-left text-xs font-semibold text-on-surface-variant">
                      <th className="px-3 py-2.5">Operator</th>
                      <th className="px-3 py-2.5">Tòa nhà</th>
                      <th className="px-3 py-2.5">Trạng thái</th>
                      <th className="px-3 py-2.5">Cấp quyền</th>
                      <th className="px-3 py-2.5">Hết hạn</th>
                      <th className="px-3 py-2.5">Thu hồi</th>
                      <th className="px-3 py-2.5">Người cấp / revision</th>
                    </tr>
                  </thead>
                  <tbody>
                    {rows.map((row) => (
                      <MembershipRow key={row.id} row={row} />
                    ))}
                  </tbody>
                </table>
              </div>

              {pageCount > 1 && (
                <div className="mt-4 flex items-center justify-between gap-3 text-sm text-on-surface-variant">
                  <span>Trang {page + 1} / {pageCount}</span>
                  <div className="flex gap-2">
                    <button
                      type="button"
                      disabled={page === 0}
                      onClick={() => setPage((current) => Math.max(0, current - 1))}
                      className="rounded-lg border border-outline-variant px-3 py-1.5 font-semibold disabled:cursor-not-allowed disabled:opacity-40"
                    >
                      Trước
                    </button>
                    <button
                      type="button"
                      disabled={page >= pageCount - 1}
                      onClick={() => setPage((current) => Math.min(pageCount - 1, current + 1))}
                      className="rounded-lg border border-outline-variant px-3 py-1.5 font-semibold disabled:cursor-not-allowed disabled:opacity-40"
                    >
                      Sau
                    </button>
                  </div>
                </div>
              )}
            </>
          )}
        </>
      )}
    </div>
  );
}

function MembershipCard({ row }: { row: OperatorMembershipSummaryDto }) {
  const meta = statusOf(row);
  return (
    <li className="rounded-xl border border-outline-variant bg-surface-lowest p-4">
      <div className="flex items-start justify-between gap-3">
        <div>
          <p className="font-display font-bold text-on-surface">Operator {shortId(row.operatorUserId)}</p>
          <p className="mt-0.5 text-xs text-on-surface-variant">ID membership: {shortId(row.id)}</p>
        </div>
        <StatusBadge tone={meta.tone} withDot>{meta.label}</StatusBadge>
      </div>
      <dl className="mt-3 space-y-1.5 text-sm">
        <Detail label="Tòa nhà" value={row.buildingName} />
        <Detail label="Cấp quyền" value={formatUtcDateTime(row.grantedAtUtc)} />
        <Detail label="Hết hạn" value={row.expiresAtUtc ? formatUtcDateTime(row.expiresAtUtc) : 'Không thời hạn'} />
        <Detail label="Thu hồi" value={formatUtcDateTime(row.revokedAtUtc)} />
        <Detail label="Người cấp" value={shortId(row.grantedByUserId)} />
        <Detail label="Revision" value={String(row.revisionNumber)} />
      </dl>
    </li>
  );
}

function MembershipRow({ row }: { row: OperatorMembershipSummaryDto }) {
  const meta = statusOf(row);
  return (
    <tr className="border-t border-outline-variant transition hover:bg-surface-low">
      <td className="px-3 py-3 font-medium text-on-surface">{shortId(row.operatorUserId)}</td>
      <td className="px-3 py-3 whitespace-nowrap text-on-surface-variant">{row.buildingName}</td>
      <td className="px-3 py-3"><StatusBadge tone={meta.tone}>{meta.label}</StatusBadge></td>
      <td className="px-3 py-3 whitespace-nowrap text-on-surface-variant tnum">{formatUtcDateTime(row.grantedAtUtc)}</td>
      <td className="px-3 py-3 whitespace-nowrap text-on-surface-variant tnum">{row.expiresAtUtc ? formatUtcDateTime(row.expiresAtUtc) : 'Không thời hạn'}</td>
      <td className="px-3 py-3 whitespace-nowrap text-on-surface-variant tnum">{formatUtcDateTime(row.revokedAtUtc)}</td>
      <td className="px-3 py-3 text-on-surface-variant"><span className="tnum">{shortId(row.grantedByUserId)}</span> · rev {row.revisionNumber}</td>
    </tr>
  );
}

function Detail({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex justify-between gap-3">
      <dt className="text-on-surface-variant">{label}</dt>
      <dd className="text-right text-on-surface">{value}</dd>
    </div>
  );
}
