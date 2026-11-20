import { Link, useParams } from 'react-router-dom';
import { Alert } from '../../components/ui/Alert';
import { EmptyState } from '../../components/ui/EmptyState';
import { PageHeader } from '../../components/ui/PageHeader';
import { StatusBadge } from '../../components/ui/StatusBadge';
import { useTenant } from '../../lib/api/buildingQueries';
import { contractStatusMeta, genderLabels } from '../../lib/api/types';
import { formatDate } from '../../lib/format';
import { ApiError } from '../../lib/http';

export function TenantDetailPage() {
  const { tenantId } = useParams<{ tenantId: string }>();
  const tenant = useTenant(tenantId);

  if (tenant.isLoading) {
    return (
      <div className="mx-auto max-w-[1400px]">
        <div className="flex items-center gap-2.5 rounded-xl border border-outline-variant bg-surface-lowest px-5 py-8 text-sm text-on-surface-variant">
          <span className="material-symbols-outlined animate-spin text-lg">sync</span>
          Đang tải thông tin khách thuê…
        </div>
      </div>
    );
  }

  if (tenant.error) {
    // A tenant who rents only outside the caller's buildings comes back 404,
    // not 403: the server refuses to confirm that the person exists.
    const forbidden = tenant.error instanceof ApiError && tenant.error.status === 403;
    const missing = tenant.error instanceof ApiError && tenant.error.status === 404;
    return (
      <div className="mx-auto max-w-[1400px]">
        <Alert tone={forbidden || missing ? 'warning' : 'error'}>
          {forbidden
            ? 'Máy chủ từ chối quyền xem khách thuê này (403).'
            : missing
              ? 'Không tìm thấy khách thuê này trong các tòa nhà tài khoản được cấp quyền. Người này có thể chỉ thuê ở tòa nhà khác.'
              : tenant.error instanceof ApiError
                ? tenant.error.message
                : 'Không tải được thông tin khách thuê.'}
        </Alert>
      </div>
    );
  }

  const data = tenant.data;
  if (!data) {
    return (
      <div className="mx-auto max-w-[1400px]">
        <EmptyState
          icon="group"
          title="Không tìm thấy khách thuê"
          description="Khách thuê không tồn tại hoặc chỉ thuê ở tòa nhà tài khoản này không được xem."
          action={
            <Link to="/tenants" className="text-sm font-semibold text-primary hover:underline">
              Quay lại danh sách khách thuê
            </Link>
          }
        />
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-[1400px]">
      <PageHeader
        title={data.fullName}
        description={
          data.contracts.length === 0
            ? 'Không còn hợp đồng nào trong tòa nhà bạn được xem'
            : `${data.contracts.length} hợp đồng trong tòa nhà bạn được xem`
        }
        breadcrumb={['Trang chủ', 'Khách thuê', data.fullName]}
      />

      <div className="grid gap-4 lg:grid-cols-2">
        <section className="rounded-xl border border-outline-variant bg-surface-lowest p-5">
          <h2 className="mb-3 font-display text-lg font-bold text-on-surface">
            Thông tin liên hệ
          </h2>
          <dl className="flex flex-col gap-2.5 text-sm">
            <Row
              icon="call"
              label="Điện thoại"
              value={<span className="tnum">{data.phoneNumber}</span>}
            />
            <Row icon="mail" label="Email" value={data.email?.trim() || '—'} />
            <Row
              icon="wc"
              label="Giới tính"
              value={data.gender === null ? '—' : genderLabels[data.gender]}
            />
            <Row icon="home_pin" label="Địa chỉ thường trú" value={data.permanentAddress || '—'} />
          </dl>
        </section>

        <section className="rounded-xl border border-outline-variant bg-surface-lowest p-5">
          <h2 className="mb-3 font-display text-lg font-bold text-on-surface">
            Nơi đang thuê ({data.contracts.length})
          </h2>
          {data.contracts.length === 0 ? (
            <p className="text-sm text-on-surface-variant">
              Khách thuê này không còn hợp đồng nào trong tòa nhà bạn được xem.
            </p>
          ) : (
            <ul className="flex flex-col gap-2">
              {data.contracts.map((contract) => {
                const meta = contractStatusMeta[contract.status];
                return (
                  <li
                    key={contract.contractId}
                    className="rounded-lg border border-outline-variant px-3 py-2.5"
                  >
                    <div className="flex items-start justify-between gap-2">
                      <Link
                        to={`/contracts/${contract.contractId}`}
                        className="text-sm font-semibold text-primary hover:underline"
                      >
                        {contract.contractNumber}
                      </Link>
                      <StatusBadge tone={meta.tone}>{meta.label}</StatusBadge>
                    </div>
                    <p className="mt-1 text-sm text-on-surface-variant">
                      {contract.buildingName} · {contract.floorName} · Phòng {contract.roomNumber}
                      {contract.endDate ? ` · đến ${formatDate(contract.endDate)}` : ''}
                    </p>
                  </li>
                );
              })}
            </ul>
          )}
          <p className="mt-4 border-t border-outline-variant pt-3 text-xs leading-relaxed text-on-surface-variant">
            Chỉ những hợp đồng thuộc tòa nhà tài khoản này được xem mới xuất hiện ở đây. Nếu khách
            thuê còn thuê ở tòa nhà khác, chỗ đó không hiện ra.
          </p>
        </section>

        <section className="rounded-xl border border-outline-variant bg-surface-lowest p-5 lg:col-span-2">
          <h2 className="mb-3 font-display text-lg font-bold text-on-surface">Ghi chú</h2>
          <p className="text-sm leading-relaxed whitespace-pre-line text-on-surface-variant">
            {data.notes?.trim() ? data.notes : 'Khách thuê không có ghi chú.'}
          </p>
        </section>
      </div>

      <div className="mt-5">
        <Link
          to="/tenants"
          className="inline-flex items-center gap-1.5 text-sm font-semibold text-primary hover:underline"
        >
          <span className="material-symbols-outlined text-base">arrow_back</span>
          Danh sách khách thuê
        </Link>
      </div>
    </div>
  );
}

function Row({
  icon,
  label,
  value,
}: {
  icon: string;
  label: string;
  value: React.ReactNode;
}) {
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
