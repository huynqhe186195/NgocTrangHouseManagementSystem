import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { Alert } from '../../components/ui/Alert';
import { EmptyState } from '../../components/ui/EmptyState';
import { PageHeader } from '../../components/ui/PageHeader';
import { StatusBadge } from '../../components/ui/StatusBadge';
import { useTenants } from '../../lib/api/buildingQueries';
import { contractStatusMeta } from '../../lib/api/types';
import type { TenantContractSummary, TenantSummaryDto } from '../../lib/api/types';
import { formatDate } from '../../lib/format';
import { ApiError } from '../../lib/http';

const EXPIRING_SOON_DAYS = 30;
const DAY_MS = 24 * 60 * 60 * 1000;

type RosterFilter = 'all' | 'renting' | 'expiringSoon' | 'finished';

const filters: { value: RosterFilter; label: string }[] = [
  { value: 'all', label: 'Tất cả' },
  { value: 'renting', label: 'Đang thuê' },
  { value: 'expiringSoon', label: `Hết hạn trong ${EXPIRING_SOON_DAYS} ngày` },
  { value: 'finished', label: 'Đã kết thúc' },
];

/**
 * The contract that decides how a row is labelled: a live one wins over a
 * finished one, so somebody who renewed does not read as "đã kết thúc".
 */
export function highlightContract(
  tenant: TenantSummaryDto,
): TenantContractSummary | null {
  if (tenant.contracts.length === 0) {
    return null;
  }
  const active = tenant.contracts.find((contract) => contract.status === 2);
  if (active) {
    return active;
  }
  const signed = tenant.contracts.find((contract) => contract.status === 5);
  return signed ?? tenant.contracts[0]!;
}

export function isExpiringSoon(contract: TenantContractSummary, today: Date): boolean {
  if (contract.status !== 2 || !contract.endDate) {
    return false;
  }
  const endsAt = new Date(contract.endDate).getTime();
  return endsAt >= today.getTime() && endsAt <= today.getTime() + EXPIRING_SOON_DAYS * DAY_MS;
}

/** A tenant is "đang thuê" when at least one of their contracts is live. */
export function isRenting(tenant: TenantSummaryDto): boolean {
  return tenant.contracts.some((contract) => contract.status === 2);
}

export function matchesFilter(
  tenant: TenantSummaryDto,
  filter: RosterFilter,
  today: Date = new Date(),
): boolean {
  if (filter === 'all') {
    return true;
  }
  if (filter === 'renting') {
    return isRenting(tenant);
  }
  if (filter === 'expiringSoon') {
    return tenant.contracts.some((contract) => isExpiringSoon(contract, today));
  }
  const highlight = highlightContract(tenant);
  return highlight !== null && highlight.status !== 2 && highlight.status !== 5;
}

export function tenantCounts(tenants: TenantSummaryDto[], today: Date = new Date()) {
  const tally = { total: 0, renting: 0, expiringSoon: 0, multi: 0 };
  for (const tenant of tenants) {
    tally.total += 1;
    if (isRenting(tenant)) tally.renting += 1;
    if (tenant.contracts.some((contract) => isExpiringSoon(contract, today))) {
      tally.expiringSoon += 1;
    }
    if (tenant.contracts.length > 1) tally.multi += 1;
  }
  return tally;
}

function locationOf(contract: TenantContractSummary | null): string {
  if (!contract) {
    return '—';
  }
  return `${contract.roomNumber} · ${contract.floorName} · ${contract.buildingName}`;
}

export function TenantsPage() {
  const tenants = useTenants();

  const [filter, setFilter] = useState<RosterFilter>('all');
  const [query, setQuery] = useState('');

  const loading = tenants.isLoading;
  const error = tenants.error;
  const rows = tenants.data ?? [];

  const visible = useMemo(() => {
    const term = query.trim().toLowerCase();
    return rows
      .filter((tenant) => matchesFilter(tenant, filter))
      .filter((tenant) => {
        if (!term) {
          return true;
        }
        const places = tenant.contracts
          .map((contract) => `${contract.roomNumber} ${contract.buildingName}`)
          .join(' ');
        return (
          tenant.fullName.toLowerCase().includes(term) ||
          tenant.phoneNumber.toLowerCase().includes(term) ||
          (tenant.email ?? '').toLowerCase().includes(term) ||
          places.toLowerCase().includes(term)
        );
      });
  }, [rows, filter, query]);

  const counts = useMemo(() => tenantCounts(rows), [rows]);

  return (
    <div className="mx-auto max-w-[1400px]">
      <PageHeader
        title="Khách thuê"
        description="Danh sách người đang thuê và đã từng thuê trong các tòa nhà bạn được xem"
        breadcrumb={['Trang chủ', 'Khách thuê']}
      />

      {error && (
        <div className="mb-4">
          <Alert tone={error instanceof ApiError && error.status === 403 ? 'warning' : 'error'}>
            {error instanceof ApiError && error.status === 403
              ? 'Máy chủ đang từ chối truy cập danh sách khách thuê (403) cho tài khoản này. Danh sách này chỉ hiện người có hợp đồng trong tòa nhà mà tài khoản được cấp quyền, và quyền đó do máy chủ quyết định.'
              : error instanceof ApiError
                ? error.message
                : 'Không tải được danh sách khách thuê.'}
          </Alert>
        </div>
      )}

      {loading && (
        <div className="flex items-center gap-2.5 rounded-xl border border-outline-variant bg-surface-lowest px-5 py-8 text-sm text-on-surface-variant">
          <span className="material-symbols-outlined animate-spin text-lg">sync</span>
          Đang tải danh sách khách thuê…
        </div>
      )}

      {!loading && !error && (
        <>
          <div className="mb-4 grid grid-cols-2 gap-3 sm:max-w-2xl sm:grid-cols-4">
            <Summary label="Tổng số khách thuê" value={counts.total} />
            <Summary label="Đang thuê" value={counts.renting} tone="green" />
            <Summary
              label={`Hết hạn trong ${EXPIRING_SOON_DAYS} ngày`}
              value={counts.expiringSoon}
              tone="orange"
            />
            <Summary label="Thuê nhiều phòng" value={counts.multi} tone="slate" />
          </div>

          <div className="mb-4 flex flex-col gap-3 rounded-xl border border-outline-variant bg-surface-lowest p-3 sm:flex-row sm:items-center sm:justify-between">
            <div className="flex flex-wrap gap-1.5">
              {filters.map((option) => {
                const active = filter === option.value;
                return (
                  <button
                    key={option.value}
                    type="button"
                    aria-pressed={active}
                    onClick={() => setFilter(option.value)}
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
                placeholder="Tìm tên, số điện thoại, phòng, tòa nhà…"
                className="h-9 w-full rounded-lg border border-outline-variant bg-surface-low pl-10 pr-3 text-sm text-on-surface placeholder:text-outline focus:border-primary focus:bg-surface-lowest focus:outline-none"
              />
            </div>
          </div>

          {visible.length === 0 ? (
            <EmptyState
              icon="group"
              title={rows.length === 0 ? 'Chưa có khách thuê nào' : 'Không có khách thuê phù hợp'}
              description={
                rows.length === 0
                  ? 'Danh sách này đọc thẳng từ máy chủ và chỉ gồm người có hợp đồng trong tòa nhà bạn được cấp quyền. Nếu đã có khách trong phần mềm mà vẫn trống, tài khoản này có thể chưa được cấp quyền xem khách thuê.'
                  : 'Thử bỏ bộ lọc hoặc từ khóa tìm kiếm khác.'
              }
            />
          ) : (
            <>
              {/* The same idiom as the contract list: below `xl` the table
                  overflows its panel, so the cards carry every fact and no
                  column is hidden to make it fit. */}
              <ul className="space-y-3 xl:hidden">
                {visible.map((tenant) => {
                  const highlight = highlightContract(tenant);
                  const meta = highlight ? contractStatusMeta[highlight.status] : null;
                  return (
                    <li
                      key={tenant.id}
                      className="rounded-xl border border-outline-variant bg-surface-lowest p-4"
                    >
                      <div className="flex items-start justify-between gap-2">
                        <Link
                          to={`/tenants/${tenant.id}`}
                          className="font-display font-bold text-on-surface hover:text-primary hover:underline"
                        >
                          {tenant.fullName}
                        </Link>
                        {meta && (
                          <StatusBadge tone={meta.tone} withDot>
                            {meta.label}
                          </StatusBadge>
                        )}
                      </div>
                      <dl className="mt-2 space-y-1 text-sm">
                        <div className="flex justify-between gap-3">
                          <dt className="text-on-surface-variant">Điện thoại</dt>
                          <dd className="text-right text-on-surface tnum">
                            {tenant.phoneNumber}
                          </dd>
                        </div>
                        <div className="flex justify-between gap-3">
                          <dt className="text-on-surface-variant">Nơi thuê</dt>
                          <dd className="text-right text-on-surface">{locationOf(highlight)}</dd>
                        </div>
                        <div className="flex justify-between gap-3">
                          <dt className="text-on-surface-variant">Hợp đồng</dt>
                          <dd className="text-right text-on-surface">
                            {highlight ? highlight.contractNumber : '—'}
                            {tenant.contracts.length > 1
                              ? ` +${tenant.contracts.length - 1}`
                              : ''}
                          </dd>
                        </div>
                        {highlight?.endDate && (
                          <div className="flex justify-between gap-3">
                            <dt className="text-on-surface-variant">Hết hạn</dt>
                            <dd className="text-right text-on-surface">
                              {formatDate(highlight.endDate)}
                            </dd>
                          </div>
                        )}
                      </dl>
                    </li>
                  );
                })}
              </ul>

              <div className="hidden overflow-x-auto rounded-xl border border-outline-variant bg-surface-lowest xl:block">
                <table className="w-full border-collapse text-sm">
                  <thead>
                    <tr className="bg-surface-low text-left text-xs font-semibold text-on-surface-variant">
                      <th className="px-3 py-2.5">Khách thuê</th>
                      <th className="px-3 py-2.5">Điện thoại</th>
                      <th className="px-3 py-2.5">Nơi thuê</th>
                      <th className="px-3 py-2.5">Hợp đồng</th>
                      <th className="px-3 py-2.5">Hết hạn</th>
                      <th className="px-3 py-2.5">Trạng thái</th>
                    </tr>
                  </thead>
                  <tbody>
                    {visible.map((tenant) => {
                      const highlight = highlightContract(tenant);
                      const meta = highlight ? contractStatusMeta[highlight.status] : null;
                      return (
                        <tr
                          key={tenant.id}
                          className="border-t border-outline-variant transition hover:bg-surface-low"
                        >
                          <td className="px-3 py-3 font-medium whitespace-nowrap">
                            <Link
                              to={`/tenants/${tenant.id}`}
                              className="text-on-surface hover:text-primary hover:underline"
                            >
                              {tenant.fullName}
                            </Link>
                          </td>
                          <td className="px-3 py-3 whitespace-nowrap text-on-surface-variant tnum">
                            {tenant.phoneNumber}
                          </td>
                          <td className="px-3 py-3 whitespace-nowrap text-on-surface-variant">
                            {locationOf(highlight)}
                          </td>
                          <td className="px-3 py-3 whitespace-nowrap text-on-surface">
                            {highlight ? highlight.contractNumber : '—'}
                            {tenant.contracts.length > 1 && (
                              <span className="ml-1 text-xs text-on-surface-variant">
                                +{tenant.contracts.length - 1}
                              </span>
                            )}
                          </td>
                          <td className="px-3 py-3 whitespace-nowrap text-on-surface-variant">
                            {formatDate(highlight?.endDate)}
                          </td>
                          <td className="px-3 py-3">
                            {meta ? (
                              <StatusBadge tone={meta.tone}>{meta.label}</StatusBadge>
                            ) : (
                              <span className="text-on-surface-variant">—</span>
                            )}
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
