import { useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Alert } from '../../components/ui/Alert';
import { EmptyState } from '../../components/ui/EmptyState';
import { PageHeader } from '../../components/ui/PageHeader';
import { StatusBadge } from '../../components/ui/StatusBadge';
import { formatDate, formatVnd } from '../../lib/format';

type PreviewState = 'data' | 'loading' | 'empty' | 'forbidden' | 'error';
type InvoiceStatus = 'draft' | 'pending' | 'paid' | 'overdue';
type StatusFilter = 'all' | InvoiceStatus;

export interface InvoiceDesignPreviewRow {
  id: string;
  number: string;
  period: string;
  building: string;
  room: string;
  tenant: string;
  amount: number;
  issuedAt: string;
  dueAt: string;
  status: InvoiceStatus;
  note: string;
}

const PREVIEW_ROWS: InvoiceDesignPreviewRow[] = [
  {
    id: 'preview-a-101',
    number: 'MẪU-A101-01',
    period: 'Kỳ minh họa 01',
    building: 'Tòa mẫu A',
    room: 'A101',
    tenant: 'Người thuê minh họa',
    amount: 3_850_000,
    issuedAt: '2026-11-05',
    dueAt: '2026-11-15',
    status: 'pending',
    note: 'Bản thiết kế tham chiếu, chưa phải công nợ chính thức.',
  },
  {
    id: 'preview-a-203',
    number: 'MẪU-A203-02',
    period: 'Kỳ minh họa 02',
    building: 'Tòa mẫu A',
    room: 'A203',
    tenant: 'Nguyễn Thị Minh Anh (mẫu)',
    amount: 4_185_000,
    issuedAt: '2026-11-06',
    dueAt: '2026-11-16',
    status: 'paid',
    note: 'Trạng thái chỉ minh họa cho layout, chưa nối bằng chứng thanh toán.',
  },
  {
    id: 'preview-b-102',
    number: 'MẪU-B102-03',
    period: 'Kỳ minh họa 03',
    building: 'Tòa mẫu B',
    room: 'B102',
    tenant: 'Lê Ngọc Phương (mẫu)',
    amount: 3_000_000,
    issuedAt: '2026-11-07',
    dueAt: '2026-11-17',
    status: 'overdue',
    note: 'Không dùng trạng thái này để xác nhận khoản phải thu ngoài preview.',
  },
  {
    id: 'preview-b-301',
    number: 'MẪU-B301-04',
    period: 'Kỳ minh họa 04',
    building: 'Tòa mẫu B',
    room: 'B301',
    tenant: 'Trần Quốc Huy (mẫu)',
    amount: 2_750_000,
    issuedAt: '2026-11-08',
    dueAt: '2026-11-18',
    status: 'draft',
    note: 'Nháp minh họa; backend hiện chưa cho phát hành hóa đơn chính thức.',
  },
];

const STATUS_OPTIONS: { value: StatusFilter; label: string }[] = [
  { value: 'all', label: 'Tất cả trạng thái' },
  { value: 'pending', label: 'Chờ thanh toán' },
  { value: 'paid', label: 'Đã thanh toán (mẫu)' },
  { value: 'overdue', label: 'Quá hạn (mẫu)' },
  { value: 'draft', label: 'Bản nháp' },
];

const STATUS_META: Record<InvoiceStatus, { label: string; tone: 'success' | 'warning' | 'error' | 'neutral' }> = {
  draft: { label: 'Bản nháp', tone: 'neutral' },
  pending: { label: 'Chờ thanh toán', tone: 'warning' },
  paid: { label: 'Đã thanh toán · mẫu', tone: 'success' },
  overdue: { label: 'Quá hạn · mẫu', tone: 'error' },
};

const VALID_STATES: PreviewState[] = ['data', 'loading', 'empty', 'forbidden', 'error'];

export function InvoiceDesignPreviewPage({
  initialState,
}: {
  initialState?: PreviewState;
}) {
  const [searchParams] = useSearchParams();
  const [query, setQuery] = useState('');
  const [status, setStatus] = useState<StatusFilter>('all');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const state = initialState ?? getPreviewState(searchParams.get('state'));

  const visibleRows = useMemo(() => {
    const normalized = query.trim().toLocaleLowerCase('vi');
    return PREVIEW_ROWS.filter((row) => status === 'all' || row.status === status).filter((row) => {
      if (!normalized) return true;
      return [row.number, row.period, row.building, row.room, row.tenant]
        .join(' ')
        .toLocaleLowerCase('vi')
        .includes(normalized);
    });
  }, [query, status]);

  const selected = visibleRows.find((row) => row.id === selectedId) ?? null;

  return (
    <div className="mx-auto max-w-[1400px]">
      <PageHeader
        title="Hóa đơn"
        titleAddon={<StatusBadge tone="info">Bản thiết kế</StatusBadge>}
        description="Không gian quản lý hóa đơn theo tòa nhà, phòng và kỳ đối soát"
        breadcrumb={['Trang chủ', 'Hóa đơn · bản thiết kế']}
      />

      <div className="mb-5 flex items-start gap-3 rounded-xl border border-[#D5E5FB] bg-[#EDF5FF] px-4 py-3 text-sm text-[#275A9E]">
        <span aria-hidden className="material-symbols-outlined mt-0.5 text-lg">info</span>
        <div>
          <p className="font-semibold">Bản thiết kế UI · Chưa tích hợp API · Dữ liệu minh họa</p>
          <p className="mt-1 text-xs text-[#4C729F]">
            Màn này mô phỏng visual language admin từ Figma. Chưa có dữ liệu hóa đơn chính thức,
            công nợ hoặc bằng chứng thanh toán được hiển thị.
          </p>
        </div>
      </div>

      {state === 'loading' && <LoadingState />}
      {state === 'forbidden' && (
        <div className="rounded-xl border border-[#FDE68A] bg-[#FFFBEB] p-5 text-sm text-[#92400E]" role="alert">
          Máy chủ đang từ chối quyền xem hóa đơn (403). Preview này không tự mở quyền và không đại diện cho API thật.
        </div>
      )}
      {state === 'error' && <Alert tone="error">Không tải được dữ liệu hóa đơn trong preview. Đây không phải lỗi kết nối API production.</Alert>}
      {state === 'empty' && (
        <EmptyState
          icon="receipt_long"
          title="Chưa có dữ liệu minh họa"
          description="Trạng thái rỗng của bản thiết kế. Khi có read model được duyệt, UI thật sẽ hiển thị dữ liệu do máy chủ scope."
        />
      )}

      {state === 'data' && (
        <>
          <PreviewMetrics />
          <section className="mt-5 overflow-hidden rounded-xl border border-outline-variant bg-surface-lowest" aria-labelledby="invoice-list-title">
            <div className="border-b border-outline-variant px-4 py-4 sm:px-5">
              <h2 id="invoice-list-title" className="font-display text-base font-bold text-on-surface">Danh sách hóa đơn</h2>
              <p className="mt-1 text-xs text-on-surface-variant">Tham chiếu bố cục staff listing; mọi hàng dưới đây đều là dữ liệu mẫu cô lập.</p>
            </div>
            <div className="flex flex-col gap-3 border-b border-outline-variant p-4 sm:flex-row sm:items-center">
              <label className="relative min-w-0 flex-1 sm:max-w-sm">
                <span aria-hidden className="material-symbols-outlined pointer-events-none absolute top-1/2 left-3 -translate-y-1/2 text-lg text-outline">search</span>
                <input
                  aria-label="Tìm hóa đơn minh họa"
                  type="search"
                  value={query}
                  onChange={(event) => setQuery(event.target.value)}
                  placeholder="Tìm mã, phòng, tòa nhà, khách thuê…"
                  className="h-10 w-full rounded-lg border border-outline-variant bg-surface-low pl-10 pr-3 text-sm text-on-surface placeholder:text-outline focus:border-primary focus:bg-surface-lowest focus:outline-none"
                />
              </label>
              <label className="flex items-center gap-2 text-sm text-on-surface-variant">
                <span className="sr-only">Lọc trạng thái</span>
                <select
                  aria-label="Lọc trạng thái hóa đơn"
                  value={status}
                  onChange={(event) => setStatus(event.target.value as StatusFilter)}
                  className="h-10 rounded-lg border border-outline-variant bg-surface-lowest px-3 text-sm text-on-surface focus:border-primary focus:outline-none"
                >
                  {STATUS_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
                </select>
              </label>
            </div>

            {visibleRows.length === 0 ? (
              <div className="p-4"><EmptyState icon="search_off" title="Không có hàng minh họa phù hợp" description="Thử xóa từ khóa hoặc chọn lại trạng thái." /></div>
            ) : (
              <>
                <ul className="space-y-3 p-4 xl:hidden">
                  {visibleRows.map((row) => <InvoiceCard key={row.id} row={row} onSelect={setSelectedId} />)}
                </ul>
                <div className="hidden overflow-x-auto xl:block">
                  <table className="w-full border-collapse text-sm">
                    <thead><tr className="bg-surface-low text-left text-xs font-semibold text-on-surface-variant">
                      <th className="px-4 py-3">Mã hóa đơn</th><th className="px-4 py-3">Kỳ</th><th className="px-4 py-3">Phòng / tòa nhà</th><th className="px-4 py-3">Khách thuê</th><th className="px-4 py-3 text-right">Tổng tiền</th><th className="px-4 py-3">Hạn thanh toán</th><th className="px-4 py-3">Trạng thái</th><th className="px-4 py-3" />
                    </tr></thead>
                    <tbody>{visibleRows.map((row) => <InvoiceTableRow key={row.id} row={row} onSelect={setSelectedId} />)}</tbody>
                  </table>
                </div>
                <div className="flex items-center justify-between border-t border-outline-variant px-4 py-3 text-xs text-on-surface-variant sm:px-5">
                  <span>Hiển thị {visibleRows.length} hàng · Dữ liệu minh họa</span>
                  <span className="tnum">1 / 1</span>
                </div>
              </>
            )}
          </section>

          {selected && <InvoiceDetailPreview row={selected} onClose={() => setSelectedId(null)} />}
        </>
      )}
    </div>
  );
}

function getPreviewState(value: string | null): PreviewState {
  return value && VALID_STATES.includes(value as PreviewState) ? value as PreviewState : 'data';
}

function LoadingState() {
  return <div className="flex items-center gap-2.5 rounded-xl border border-outline-variant bg-surface-lowest px-5 py-10 text-sm text-on-surface-variant"><span className="material-symbols-outlined animate-spin text-lg">sync</span>Đang tải bản thiết kế hóa đơn…</div>;
}

function PreviewMetrics() {
  const metrics = [
    ['receipt_long', 'Hóa đơn trong kỳ', '04', 'Dữ liệu minh họa', 'info'],
    ['payments', 'Tổng giá trị mẫu', '13,8 tr', 'Không phải công nợ', 'success'],
    ['schedule', 'Chờ thanh toán', '01', 'Trạng thái mẫu', 'warning'],
    ['draft', 'Bản nháp', '01', 'Chưa thể phát hành', 'neutral'],
  ] as const;
  return <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">{metrics.map(([icon, label, value, hint, tone]) => <div key={label} className="flex min-h-[104px] items-center gap-3 rounded-xl border border-outline-variant bg-surface-lowest p-4"><span className={`flex h-11 w-11 shrink-0 items-center justify-center rounded-xl text-xl ${tone === 'success' ? 'bg-[#E4F8ED] text-[#079356]' : tone === 'warning' ? 'bg-[#FFF4D8] text-[#B97710]' : tone === 'neutral' ? 'bg-surface-container text-on-surface-variant' : 'bg-[#EAF2FF] text-[#075DE8]'}`}><span className="material-symbols-outlined">{icon}</span></span><div className="min-w-0"><p className="truncate text-xs text-on-surface-variant">{label}</p><p className="mt-1 text-xl font-bold text-on-surface tnum">{value}</p><p className="truncate text-[10px] text-on-surface-variant">{hint}</p></div></div>)}</div>;
}

function InvoiceTableRow({ row, onSelect }: { row: InvoiceDesignPreviewRow; onSelect: (id: string) => void }) {
  const meta = STATUS_META[row.status];
  return <tr className="border-t border-outline-variant transition hover:bg-surface-low"><td className="px-4 py-3 font-semibold text-primary">{row.number}</td><td className="px-4 py-3 whitespace-nowrap text-on-surface-variant">{row.period}</td><td className="px-4 py-3"><span className="font-semibold text-on-surface">{row.room}</span><span className="block text-xs text-on-surface-variant">{row.building}</span></td><td className="px-4 py-3 text-on-surface">{row.tenant}</td><td className="px-4 py-3 text-right whitespace-nowrap font-semibold text-on-surface tnum">{formatVnd(row.amount)}</td><td className="px-4 py-3 whitespace-nowrap text-on-surface-variant tnum">{formatDate(row.dueAt)}</td><td className="px-4 py-3"><StatusBadge tone={meta.tone} withDot>{meta.label}</StatusBadge></td><td className="px-4 py-3"><button type="button" onClick={() => onSelect(row.id)} className="rounded-lg border border-outline-variant px-3 py-1.5 text-xs font-semibold text-on-surface-variant hover:bg-surface-low">Xem chi tiết</button></td></tr>;
}

function InvoiceCard({ row, onSelect }: { row: InvoiceDesignPreviewRow; onSelect: (id: string) => void }) {
  const meta = STATUS_META[row.status];
  return <li className="rounded-xl border border-outline-variant bg-surface-lowest p-4"><div className="flex items-start justify-between gap-3"><div><p className="font-display font-bold text-primary">{row.number}</p><p className="mt-1 text-xs text-on-surface-variant">{row.period} · {row.room} · {row.building}</p></div><StatusBadge tone={meta.tone} withDot>{meta.label}</StatusBadge></div><dl className="mt-4 space-y-2 text-sm"><Detail label="Khách thuê" value={row.tenant} /><Detail label="Tổng tiền mẫu" value={formatVnd(row.amount)} /><Detail label="Hạn thanh toán" value={formatDate(row.dueAt)} /></dl><button type="button" onClick={() => onSelect(row.id)} className="mt-4 w-full rounded-lg border border-outline-variant px-3 py-2 text-xs font-semibold text-on-surface-variant hover:bg-surface-low">Xem chi tiết mẫu</button></li>;
}

function InvoiceDetailPreview({ row, onClose }: { row: InvoiceDesignPreviewRow; onClose: () => void }) {
  const meta = STATUS_META[row.status];
  return <section className="mt-5 rounded-xl border border-outline-variant bg-surface-lowest p-4 sm:p-5" aria-labelledby="invoice-preview-detail"><div className="flex flex-wrap items-start justify-between gap-3"><div><p className="text-xs text-on-surface-variant">Chi tiết bản thiết kế · không phải invoice API</p><h2 id="invoice-preview-detail" className="mt-1 font-display text-lg font-bold text-on-surface">{row.number}</h2><p className="mt-1 text-xs text-on-surface-variant">{row.room} · {row.building} · {row.period}</p></div><button type="button" onClick={onClose} className="rounded-lg border border-outline-variant px-3 py-1.5 text-xs font-semibold text-on-surface-variant hover:bg-surface-low">Đóng</button></div><div className="mt-4 grid gap-4 border-t border-outline-variant pt-4 sm:grid-cols-3"><Detail label="Khách thuê" value={row.tenant} /><Detail label="Tổng tiền minh họa" value={formatVnd(row.amount)} /><Detail label="Trạng thái" value={meta.label} /><Detail label="Ngày phát hành mẫu" value={formatDate(row.issuedAt)} /><Detail label="Hạn thanh toán mẫu" value={formatDate(row.dueAt)} /><Detail label="Nguồn" value="Fixture UI cô lập" /></div><div className="mt-4 rounded-lg border border-[#D5E5FB] bg-[#EDF5FF] p-3 text-xs leading-relaxed text-[#4C729F]">{row.note}</div></section>;
}

function Detail({ label, value }: { label: string; value: string }) {
  return <div><dt className="text-xs text-on-surface-variant">{label}</dt><dd className="mt-1 font-semibold text-on-surface">{value}</dd></div>;
}
