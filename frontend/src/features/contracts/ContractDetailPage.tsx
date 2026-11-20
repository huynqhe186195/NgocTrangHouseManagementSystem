import { Link, useParams } from 'react-router-dom';
import { Alert } from '../../components/ui/Alert';
import { EmptyState } from '../../components/ui/EmptyState';
import { PageHeader } from '../../components/ui/PageHeader';
import { StatusBadge } from '../../components/ui/StatusBadge';
import { useContract } from '../../lib/api/buildingQueries';
import {
  contractEndReasonLabels,
  contractStatusMeta,
  contractTenantRoleLabels,
} from '../../lib/api/types';
import { formatDate, formatVnd } from '../../lib/format';
import { ApiError } from '../../lib/http';

export function ContractDetailPage() {
  const { contractId } = useParams<{ contractId: string }>();
  const contract = useContract(contractId);

  if (contract.isLoading) {
    return (
      <div className="mx-auto max-w-[1400px]">
        <div className="flex items-center gap-2.5 rounded-xl border border-outline-variant bg-surface-lowest px-5 py-8 text-sm text-on-surface-variant">
          <span className="material-symbols-outlined animate-spin text-lg">sync</span>
          Đang tải thông tin hợp đồng…
        </div>
      </div>
    );
  }

  if (contract.error) {
    const forbidden = contract.error instanceof ApiError && contract.error.status === 403;
    return (
      <div className="mx-auto max-w-[1400px]">
        <Alert tone={forbidden ? 'warning' : 'error'}>
          {forbidden
            ? 'Máy chủ từ chối quyền xem hợp đồng này (403). Hợp đồng thuộc một tòa nhà mà tài khoản này chưa được cấp quyền.'
            : contract.error instanceof ApiError
              ? contract.error.message
              : 'Không tải được thông tin hợp đồng.'}
        </Alert>
      </div>
    );
  }

  const data = contract.data;
  if (!data) {
    return (
      <div className="mx-auto max-w-[1400px]">
        <EmptyState
          icon="description"
          title="Không tìm thấy hợp đồng"
          description="Hợp đồng không tồn tại hoặc tài khoản này không có quyền truy cập."
          action={
            <Link to="/contracts" className="text-sm font-semibold text-primary hover:underline">
              Quay lại danh sách hợp đồng
            </Link>
          }
        />
      </div>
    );
  }

  const meta = contractStatusMeta[data.status];
  const finished = data.actualEndedAt !== null || data.status === 3 || data.status === 4;

  return (
    <div className="mx-auto max-w-[1400px]">
      <PageHeader
        title={data.contractNumber}
        titleAddon={
          <StatusBadge tone={meta.tone} withDot>
            {meta.label}
          </StatusBadge>
        }
        description={`${data.buildingName} · ${data.floorName} · Phòng ${data.roomNumber}`}
        breadcrumb={['Trang chủ', 'Hợp đồng', data.contractNumber]}
      />

      <div className="grid gap-4 lg:grid-cols-2">
        <section className="rounded-xl border border-outline-variant bg-surface-lowest p-5">
          <h2 className="mb-3 font-display text-lg font-bold text-on-surface">Thông tin hợp đồng</h2>
          <dl className="flex flex-col gap-2.5 text-sm">
            <Row
              icon="apartment"
              label="Tòa nhà"
              value={
                <Link
                  to={`/buildings/${data.buildingId}`}
                  className="text-primary hover:underline"
                >
                  {data.buildingName}
                </Link>
              }
            />
            <Row icon="layers" label="Tầng" value={data.floorName} />
            <Row
              icon="meeting_room"
              label="Phòng"
              value={
                <Link to={`/rooms/${data.roomId}`} className="text-primary hover:underline">
                  Phòng {data.roomNumber}
                </Link>
              }
            />
            <Row icon="event" label="Ngày bắt đầu" value={formatDate(data.startDate)} />
            <Row
              icon="event_busy"
              label="Ngày kết thúc dự kiến"
              value={formatDate(data.endDate)}
            />
            {data.actualEndedAt && (
              <Row
                icon="event_available"
                label="Ngày kết thúc thực tế"
                value={formatDate(data.actualEndedAt)}
              />
            )}
            {data.endReason !== null && (
              <Row
                icon="info"
                label="Lý do kết thúc"
                value={contractEndReasonLabels[data.endReason]}
              />
            )}
            <Row icon="tag" label="Trạng thái" value={meta.label} />
          </dl>
        </section>

        <section className="rounded-xl border border-outline-variant bg-surface-lowest p-5">
          <h2 className="mb-3 font-display text-lg font-bold text-on-surface">Tài chính</h2>
          <dl className="flex flex-col gap-2.5 text-sm">
            <Row
              icon="payments"
              label="Giá thuê hàng tháng"
              value={<span className="tnum">{formatVnd(data.monthlyRent)}</span>}
            />
            <Row
              icon="savings"
              label="Tiền cọc"
              value={<span className="tnum">{formatVnd(data.depositAmount)}</span>}
            />
          </dl>
          <p className="mt-4 border-t border-outline-variant pt-3 text-xs leading-relaxed text-on-surface-variant">
            Đây là số trên hợp đồng. Hoá đơn, công nợ và các lần điều chỉnh tiền là dữ liệu riêng
            của phân hệ tài chính và chưa có đường đọc cho màn này.
          </p>
        </section>

        <section className="rounded-xl border border-outline-variant bg-surface-lowest p-5">
          <h2 className="mb-3 font-display text-lg font-bold text-on-surface">
            Khách thuê ({data.tenants.length})
          </h2>
          {data.tenants.length === 0 ? (
            <p className="text-sm text-on-surface-variant">
              Hợp đồng này chưa gắn khách thuê nào trên máy chủ.
            </p>
          ) : (
            <ul className="flex flex-col gap-2">
              {data.tenants.map((tenant) => (
                <li
                  key={tenant.tenantId}
                  className="flex items-center justify-between gap-3 rounded-lg border border-outline-variant px-3 py-2.5"
                >
                  <span className="flex items-center gap-2 text-sm text-on-surface">
                    <span className="material-symbols-outlined text-base text-on-surface-variant">
                      person
                    </span>
                    {tenant.fullName}
                  </span>
                  <StatusBadge tone={tenant.role === 1 ? 'info' : 'neutral'}>
                    {contractTenantRoleLabels[tenant.role]}
                  </StatusBadge>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section className="rounded-xl border border-outline-variant bg-surface-lowest p-5">
          <h2 className="mb-3 font-display text-lg font-bold text-on-surface">Ghi chú</h2>
          <p className="text-sm leading-relaxed whitespace-pre-line text-on-surface-variant">
            {data.notes?.trim() ? data.notes : 'Hợp đồng không có ghi chú.'}
          </p>

          <div className="mt-4 border-t border-outline-variant pt-4">
            <div className="flex flex-wrap items-center gap-2">
              <LockedButton icon="play_circle" label="Kích hoạt" />
              <LockedButton icon="block" label="Huỷ hợp đồng" />
              <LockedButton icon="logout" label="Kết thúc hợp đồng" />
            </div>
            <p className="mt-2 text-xs leading-relaxed text-on-surface-variant">
              {finished
                ? 'Các thao tác ghi hợp đồng (kích hoạt, huỷ, kết thúc) chưa được mở trên máy chủ. Hợp đồng này đã kết thúc nên các thao tác đó cũng không còn áp dụng.'
                : 'Các thao tác ghi hợp đồng (kích hoạt, huỷ, kết thúc) chưa được mở trên máy chủ, nên các nút này bị khoá thay vì giả vờ chạy được. Muốn thao tác, cần mở phân hệ ghi hợp đồng có kiểm tra điều kiện nghiệp vụ.'}
            </p>
          </div>
        </section>
      </div>

      <div className="mt-5">
        <Link
          to="/contracts"
          className="inline-flex items-center gap-1.5 text-sm font-semibold text-primary hover:underline"
        >
          <span className="material-symbols-outlined text-base">arrow_back</span>
          Danh sách hợp đồng
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

function LockedButton({ icon, label }: { icon: string; label: string }) {
  return (
    <button
      type="button"
      disabled
      title="Thao tác ghi hợp đồng chưa được mở trên máy chủ."
      className="inline-flex h-9 items-center gap-1.5 rounded-lg border border-outline-variant px-3 text-xs font-semibold text-on-surface-variant"
    >
      <span className="material-symbols-outlined text-base">{icon}</span>
      {label}
    </button>
  );
}
