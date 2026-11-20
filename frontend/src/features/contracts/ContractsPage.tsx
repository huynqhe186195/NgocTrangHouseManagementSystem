import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { Alert } from '../../components/ui/Alert';
import { EmptyState } from '../../components/ui/EmptyState';
import { PageHeader } from '../../components/ui/PageHeader';
import { StatusBadge } from '../../components/ui/StatusBadge';
import { useContracts } from '../../lib/api/buildingQueries';
import { contractStatusMeta } from '../../lib/api/types';
import type { ContractStatus, ContractSummaryDto } from '../../lib/api/types';
import { formatDate, formatVnd } from '../../lib/format';
import { ApiError } from '../../lib/http';

const statusFilters: { value: ContractStatus | 'all'; label: string }[] = [
  { value: 'all', label: 'Tất cả' },
  { value: 1, label: 'Nháp' },
  { value: 5, label: 'Đã ký' },
  { value: 2, label: 'Đang hiệu lực' },
  { value: 3, label: 'Đã kết thúc' },
  { value: 4, label: 'Đã huỷ' },
];

const EXPIRING_SOON_DAYS = 30;
const DAY_MS = 24 * 60 * 60 * 1000;

/**
 * Counts for the KPI row. `today` is a parameter so the "sắp hết hạn" figure
 * is testable without freezing the clock.
 */
export function contractCounts(contracts: ContractSummaryDto[], today: Date = new Date()) {
  const limit = today.getTime() + EXPIRING_SOON_DAYS * DAY_MS;
  const tally = { total: 0, active: 0, expiringSoon: 0, ended: 0 };
  for (const contract of contracts) {
    tally.total += 1;
    if (contract.status === 2) {
      tally.active += 1;
      if (contract.endDate) {
        const endsAt = new Date(contract.endDate).getTime();
        if (endsAt >= today.getTime() && endsAt <= limit) tally.expiringSoon += 1;
      }
    }
    if (contract.status === 3) tally.ended += 1;
  }
  return tally;
}

export function ContractsPage() {
  const contracts = useContracts();

  const [status, setStatus] = useState<ContractStatus | 'all'>('all');
  const [query, setQuery] = useState('');

  const loading = contracts.isLoading;
  const error = contracts.error;
  const rows = contracts.data ?? [];

  const visible = useMemo(() => {
    const term = query.trim().toLowerCase();
    return rows
      .filter((contract) => (status === 'all' ? true : contract.status === status))
      .filter((contract) => {
        if (!term) {
          return true;
        }
        const tenantNames = contract.tenants.map((tenant) => tenant.fullName).join(' ');
        return (
          contract.contractNumber.toLowerCase().includes(term) ||
          contract.roomNumber.toLowerCase().includes(term) ||
          contract.buildingName.toLowerCase().includes(term) ||
          tenantNames.toLowerCase().includes(term)
        );
      });
  }, [rows, status, query]);

  const counts = useMemo(() => contractCounts(rows), [rows]);

  return (
    <div className="mx-auto max-w-[1400px]">
      <PageHeader
        title="Hợp đồng"
        description="Tra cứu hợp đồng thuê theo tòa nhà, phòng và trạng thái"
        breadcrumb={['Trang chủ', 'Hợp đồng']}
      />

      {error && (
        <div className="mb-4">
          <Alert tone={error instanceof ApiError && error.status === 403 ? 'warning' : 'error'}>
            {error instanceof ApiError && error.status === 403
              ? 'Máy chủ đang từ chối truy cập danh sách hợp đồng (403) cho tài khoản này. Giao diện không tự mở quyền — quyền xem hợp đồng của từng tòa nhà do máy chủ quyết định.'
              : error instanceof ApiError
                ? error.message
                : 'Không tải được danh sách hợp đồng.'}
          </Alert>
        </div>
      )}

      {loading && (
        <div className="flex items-center gap-2.5 rounded-xl border border-outline-variant bg-surface-lowest px-5 py-8 text-sm text-on-surface-variant">
          <span className="material-symbols-outlined animate-spin text-lg">sync</span>
          Đang tải danh sách hợp đồng…
        </div>
      )}

      {!loading && !error && (
        <>
          <div className="mb-4 grid grid-cols-2 gap-3 sm:grid-cols-4 sm:max-w-2xl">
            <Summary label="Tổng số hợp đồng" value={counts.total} />
            <Summary label="Đang hiệu lực" value={counts.active} tone="green" />
            <Summary
              label={`Hết hạn trong ${EXPIRING_SOON_DAYS} ngày`}
              value={counts.expiringSoon}
              tone="orange"
            />
            <Summary label="Đã kết thúc" value={counts.ended} tone="slate" />
          </div>

          <div className="mb-4 flex flex-col gap-3 rounded-xl border border-outline-variant bg-surface-lowest p-3 sm:flex-row sm:items-center sm:justify-between">
            <div className="flex flex-wrap gap-1.5">
              {statusFilters.map((option) => {
                const active = status === option.value;
                return (
                  <button
                    key={String(option.value)}
                    type="button"
                    aria-pressed={active}
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
            <div className="relative sm:w-72">
              <span className="material-symbols-outlined pointer-events-none absolute top-1/2 left-3 -translate-y-1/2 text-lg text-outline">
                search
              </span>
              <input
                type="search"
                value={query}
                onChange={(event) => setQuery(event.target.value)}
                placeholder="Tìm số hợp đồng, phòng, tòa nhà, khách thuê…"
                className="h-9 w-full rounded-lg border border-outline-variant bg-surface-low pl-10 pr-3 text-sm text-on-surface placeholder:text-outline focus:border-primary focus:bg-surface-lowest focus:outline-none"
              />
            </div>
          </div>

          {visible.length === 0 ? (
            <EmptyState
              icon="description"
              title={rows.length === 0 ? 'Chưa có hợp đồng nào' : 'Không có hợp đồng phù hợp'}
              description={
                rows.length === 0
                  ? 'Danh sách này đọc thẳng từ máy chủ. Nếu bạn đã có hợp đồng trong phần mềm mà vẫn trống, tài khoản này có thể chưa được cấp quyền xem hợp đồng ở tòa nhà đó.'
                  : 'Thử bỏ bộ lọc trạng thái hoặc từ khóa tìm kiếm khác.'
              }
            />
          ) : (
            <>
              {/* Below `xl` the seven-column table overflows its panel — at
                  1024 the status column was cut off, and hiding it instead
                  would hide the tenant who rents the room. So the cards run
                  all the way to `xl` and the full table appears above it. */}
              <ul className="space-y-3 xl:hidden">
                {visible.map((contract) => {
                  const meta = contractStatusMeta[contract.status];
                  const primary = contract.tenants[0];
                  return (
                    <li
                      key={contract.id}
                      className="rounded-xl border border-outline-variant bg-surface-lowest p-4"
                    >
                      <div className="flex items-start justify-between gap-2">
                        <Link
                          to={`/contracts/${contract.id}`}
                          className="font-display font-bold text-on-surface hover:text-primary hover:underline"
                        >
                          {contract.contractNumber}
                        </Link>
                        <StatusBadge tone={meta.tone} withDot>
                          {meta.label}
                        </StatusBadge>
                      </div>
                      <dl className="mt-2 space-y-1 text-sm">
                        <div className="flex justify-between gap-3">
                          <dt className="text-on-surface-variant">Vị trí</dt>
                          <dd className="text-right text-on-surface">
                            {contract.roomNumber} · {contract.floorName} · {contract.buildingName}
                          </dd>
                        </div>
                        <div className="flex justify-between gap-3">
                          <dt className="text-on-surface-variant">Khách thuê</dt>
                          <dd className="text-right text-on-surface">
                            {primary ? primary.fullName : '—'}
                            {contract.tenants.length > 1
                              ? ` +${contract.tenants.length - 1}`
                              : ''}
                          </dd>
                        </div>
                        <div className="flex justify-between gap-3">
                          <dt className="text-on-surface-variant">Thời hạn</dt>
                          <dd className="text-right text-on-surface">
                            {formatDate(contract.startDate)} –{' '}
                            {contract.endDate ? formatDate(contract.endDate) : 'chưa định'}
                          </dd>
                        </div>
                        <div className="flex justify-between gap-3">
                          <dt className="text-on-surface-variant">Giá thuê</dt>
                          <dd className="text-right font-medium text-on-surface tnum">
                            {formatVnd(contract.monthlyRent)}
                          </dd>
                        </div>
                      </dl>
                    </li>
                  );
                })}
              </ul>

              <div className="hidden overflow-x-auto rounded-xl border border-outline-variant bg-surface-lowest xl:block">
                <table className="w-full border-collapse text-sm">
                  <thead>
                    <tr className="bg-surface-low text-left text-xs font-semibold text-on-surface-variant">
                      <th className="px-3 py-2.5">Số hợp đồng</th>
                      <th className="px-3 py-2.5">Phòng</th>
                      <th className="px-3 py-2.5">Tòa nhà</th>
                      <th className="px-3 py-2.5">Khách thuê</th>
                      <th className="px-3 py-2.5">Thời hạn</th>
                      <th className="px-3 py-2.5 text-right">Giá thuê</th>
                      <th className="px-3 py-2.5">Trạng thái</th>
                    </tr>
                  </thead>
                  <tbody>
                    {visible.map((contract) => {
                      const meta = contractStatusMeta[contract.status];
                      const primary = contract.tenants[0];
                      return (
                        <tr
                          key={contract.id}
                          className="border-t border-outline-variant transition hover:bg-surface-low"
                        >
                          <td className="px-3 py-3 font-medium whitespace-nowrap">
                            <Link
                              to={`/contracts/${contract.id}`}
                              className="text-on-surface hover:text-primary hover:underline"
                            >
                              {contract.contractNumber}
                            </Link>
                          </td>
                          <td className="px-3 py-3 whitespace-nowrap text-on-surface-variant">
                            {contract.roomNumber}
                          </td>
                          <td className="px-3 py-3 whitespace-nowrap text-on-surface-variant">
                            {contract.buildingName}
                          </td>
                          <td className="px-3 py-3 text-on-surface">
                            {primary ? primary.fullName : '—'}
                            {contract.tenants.length > 1 && (
                              <span className="ml-1 text-xs text-on-surface-variant">
                                +{contract.tenants.length - 1}
                              </span>
                            )}
                          </td>
                          <td className="px-3 py-3 whitespace-nowrap text-on-surface-variant">
                            {formatDate(contract.startDate)} –{' '}
                            {contract.endDate ? formatDate(contract.endDate) : 'chưa định'}
                          </td>
                          <td className="px-3 py-3 text-right whitespace-nowrap text-on-surface tnum">
                            {formatVnd(contract.monthlyRent)}
                          </td>
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
          )}
        </>
      )}
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
  tone?: 'primary' | 'green' | 'orange' | 'slate';
}) {
  const tones = {
    primary: 'bg-[#EFF6FF] text-[#1D4ED8]',
    green: 'bg-[#ECFDF5] text-[#047857]',
    orange: 'bg-[#FFF7ED] text-[#C2410C]',
    slate: 'bg-surface-container text-on-surface-variant',
  } as const;
  return (
    <div className={`rounded-xl px-4 py-3 ${tones[tone]}`}>
      <p className="font-display text-xl font-bold tnum">{value}</p>
      <p className="text-xs opacity-80">{label}</p>
    </div>
  );
}
