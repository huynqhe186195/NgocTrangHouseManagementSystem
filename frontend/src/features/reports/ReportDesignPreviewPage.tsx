import { useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Alert } from '../../components/ui/Alert';
import { EmptyState } from '../../components/ui/EmptyState';
import { PageHeader } from '../../components/ui/PageHeader';
import { StatusBadge } from '../../components/ui/StatusBadge';
import { formatDate } from '../../lib/format';

type PreviewState = 'data' | 'loading' | 'empty' | 'forbidden' | 'error';
type ReportGroup = 'occupancy' | 'contracts';
type GroupFilter = 'all' | ReportGroup;

export interface ReportDesignPreviewRow {
  id: string;
  building: string;
  period: string;
  asOf: string;
  totalRooms: number;
  occupiedRooms: number;
  activeContracts: number;
  expiringContracts: number;
  group: ReportGroup;
  status: 'stable' | 'attention';
  note: string;
}

const PREVIEW_ROWS: ReportDesignPreviewRow[] = [
  {
    id: 'report-preview-a-11',
    building: 'Tòa mẫu A',
    period: 'Kỳ minh họa 11/2026',
    asOf: '2026-11-18',
    totalRooms: 24,
    occupiedRooms: 19,
    activeContracts: 18,
    expiringContracts: 2,
    group: 'occupancy',
    status: 'stable',
    note: 'Bản tổng hợp minh họa cho bố cục báo cáo phòng và tỷ lệ sử dụng.',
  },
  {
    id: 'report-preview-b-11',
    building: 'Tòa mẫu B',
    period: 'Kỳ minh họa 11/2026',
    asOf: '2026-11-18',
    totalRooms: 18,
    occupiedRooms: 12,
    activeContracts: 11,
    expiringContracts: 3,
    group: 'contracts',
    status: 'attention',
    note: 'Trạng thái chỉ minh họa vùng cần chú ý, chưa phải cảnh báo từ máy chủ.',
  },
  {
    id: 'report-preview-a-10',
    building: 'Tòa mẫu A',
    period: 'Kỳ minh họa 10/2026',
    asOf: '2026-10-31',
    totalRooms: 24,
    occupiedRooms: 17,
    activeContracts: 16,
    expiringContracts: 1,
    group: 'contracts',
    status: 'stable',
    note: 'Hàng fixture lịch sử để minh họa bộ lọc kỳ báo cáo.',
  },
  {
    id: 'report-preview-c-11',
    building: 'Tòa mẫu C',
    period: 'Kỳ minh họa 11/2026',
    asOf: '2026-11-18',
    totalRooms: 32,
    occupiedRooms: 25,
    activeContracts: 24,
    expiringContracts: 4,
    group: 'occupancy',
    status: 'attention',
    note: 'Dữ liệu minh họa; không đại diện cho tỷ lệ sử dụng thực tế.',
  },
];

const VALID_STATES: PreviewState[] = ['data', 'loading', 'empty', 'forbidden', 'error'];
const GROUP_OPTIONS: { value: GroupFilter; label: string }[] = [
  { value: 'all', label: 'Tất cả nhóm' },
  { value: 'occupancy', label: 'Sử dụng phòng' },
  { value: 'contracts', label: 'Hợp đồng' },
];
const STATUS_META = {
  stable: { label: 'Ổn định · mẫu', tone: 'success' as const },
  attention: { label: 'Cần xem lại · mẫu', tone: 'warning' as const },
};

export function ReportDesignPreviewPage({
  initialState,
}: {
  initialState?: PreviewState;
}) {
  const [searchParams] = useSearchParams();
  const [query, setQuery] = useState('');
  const [building, setBuilding] = useState('all');
  const [period, setPeriod] = useState('all');
  const [group, setGroup] = useState<GroupFilter>('all');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const state = initialState ?? getPreviewState(searchParams.get('state'));

  const visibleRows = useMemo(() => {
    const normalized = query.trim().toLocaleLowerCase('vi');
    return PREVIEW_ROWS.filter((row) => building === 'all' || row.building === building)
      .filter((row) => period === 'all' || row.period === period)
      .filter((row) => group === 'all' || row.group === group)
      .filter((row) => !normalized || [row.building, row.period, row.group].join(' ').toLocaleLowerCase('vi').includes(normalized));
  }, [building, group, period, query]);

  const selected = visibleRows.find((row) => row.id === selectedId) ?? null;
  const buildings = [...new Set(PREVIEW_ROWS.map((row) => row.building))];
  const periods = [...new Set(PREVIEW_ROWS.map((row) => row.period))];

  return (
    <div className="mx-auto max-w-[1400px]">
      <PageHeader
        title="Báo cáo"
        titleAddon={<StatusBadge tone="info">Bản thiết kế</StatusBadge>}
        description="Theo dõi tổng quan phòng, hợp đồng và kỳ báo cáo theo tòa nhà"
        breadcrumb={['Trang chủ', 'Báo cáo · bản thiết kế']}
      />

      <div className="mb-5 flex items-start gap-3 rounded-xl border border-[#D5E5FB] bg-[#EDF5FF] px-4 py-3 text-sm text-[#275A9E]">
        <span aria-hidden className="material-symbols-outlined mt-0.5 text-lg">info</span>
        <div>
          <p className="font-semibold">Bản thiết kế UI · Chưa tích hợp API · Dữ liệu minh họa</p>
          <p className="mt-1 text-xs text-[#4C729F]">
            Màn này là inventory visual suy ra từ dữ liệu read-only hiện có. Chưa có số liệu báo cáo production, số tiền hoặc trạng thái tài chính được hiển thị.
          </p>
        </div>
      </div>

      {state === 'loading' && <LoadingState />}
      {state === 'forbidden' && (
        <div className="rounded-xl border border-[#FDE68A] bg-[#FFFBEB] p-5 text-sm text-[#92400E]" role="alert">
          Máy chủ chưa mở quyền xem báo cáo (403). Preview này không tự mở quyền và không đại diện cho API thật.
        </div>
      )}
      {state === 'error' && <Alert tone="error">Không tải được dữ liệu báo cáo trong preview. Đây không phải lỗi kết nối API production.</Alert>}
      {state === 'empty' && (
        <EmptyState
          icon="bar_chart"
          title="Chưa có dữ liệu minh họa"
          description="Trạng thái rỗng của bản thiết kế. Khi có read model được duyệt, UI thật sẽ hiển thị dữ liệu do máy chủ scope."
        />
      )}

      {state === 'data' && (
        <>
          <PreviewMetrics rows={visibleRows} />
          <section className="mt-5 overflow-hidden rounded-xl border border-outline-variant bg-surface-lowest" aria-labelledby="report-list-title">
            <div className="border-b border-outline-variant px-4 py-4 sm:px-5">
              <h2 id="report-list-title" className="font-display text-base font-bold text-on-surface">Tổng quan theo tòa nhà</h2>
              <p className="mt-1 text-xs text-on-surface-variant">Bảng minh họa cho dimension phòng và hợp đồng; mọi hàng đều là fixture cô lập.</p>
            </div>
            <div className="grid gap-3 border-b border-outline-variant p-4 sm:grid-cols-2 xl:grid-cols-4">
              <label className="relative min-w-0 sm:col-span-2 xl:col-span-1">
                <span aria-hidden className="material-symbols-outlined pointer-events-none absolute top-1/2 left-3 -translate-y-1/2 text-lg text-outline">search</span>
                <input
                  aria-label="Tìm báo cáo minh họa"
                  type="search"
                  value={query}
                  onChange={(event) => setQuery(event.target.value)}
                  placeholder="Tìm tòa nhà, kỳ báo cáo…"
                  className="h-10 w-full rounded-lg border border-outline-variant bg-surface-low pl-10 pr-3 text-sm text-on-surface placeholder:text-outline focus:border-primary focus:bg-surface-lowest focus:outline-none"
                />
              </label>
              <label className="flex items-center gap-2 text-sm text-on-surface-variant">
                <span className="sr-only">Lọc tòa nhà</span>
                <select aria-label="Lọc tòa nhà báo cáo" value={building} onChange={(event) => setBuilding(event.target.value)} className="h-10 w-full rounded-lg border border-outline-variant bg-surface-lowest px-3 text-sm text-on-surface focus:border-primary focus:outline-none">
                  <option value="all">Tất cả tòa nhà</option>
                  {buildings.map((item) => <option key={item} value={item}>{item}</option>)}
                </select>
              </label>
              <label className="flex items-center gap-2 text-sm text-on-surface-variant">
                <span className="sr-only">Lọc kỳ báo cáo</span>
                <select aria-label="Lọc kỳ báo cáo" value={period} onChange={(event) => setPeriod(event.target.value)} className="h-10 w-full rounded-lg border border-outline-variant bg-surface-lowest px-3 text-sm text-on-surface focus:border-primary focus:outline-none">
                  <option value="all">Tất cả kỳ</option>
                  {periods.map((item) => <option key={item} value={item}>{item}</option>)}
                </select>
              </label>
              <label className="flex items-center gap-2 text-sm text-on-surface-variant">
                <span className="sr-only">Lọc nhóm báo cáo</span>
                <select aria-label="Lọc nhóm báo cáo" value={group} onChange={(event) => setGroup(event.target.value as GroupFilter)} className="h-10 w-full rounded-lg border border-outline-variant bg-surface-lowest px-3 text-sm text-on-surface focus:border-primary focus:outline-none">
                  {GROUP_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
                </select>
              </label>
            </div>

            {visibleRows.length === 0 ? (
              <div className="p-4"><EmptyState icon="search_off" title="Không có hàng minh họa phù hợp" description="Thử xóa từ khóa hoặc chọn lại bộ lọc." /></div>
            ) : (
              <>
                <ul className="space-y-3 p-4 xl:hidden">
                  {visibleRows.map((row) => <ReportCard key={row.id} row={row} onSelect={setSelectedId} />)}
                </ul>
                <div className="hidden overflow-x-auto xl:block">
                  <table className="w-full border-collapse text-sm">
                    <thead><tr className="bg-surface-low text-left text-xs font-semibold text-on-surface-variant">
                      <th className="px-4 py-3">Tòa nhà / kỳ</th><th className="px-4 py-3">Phòng</th><th className="px-4 py-3">Đang sử dụng</th><th className="px-4 py-3">Hợp đồng hiệu lực</th><th className="px-4 py-3">Sắp hết hạn</th><th className="px-4 py-3">Cập nhật</th><th className="px-4 py-3">Trạng thái</th><th className="px-4 py-3" />
                    </tr></thead>
                    <tbody>{visibleRows.map((row) => <ReportTableRow key={row.id} row={row} onSelect={setSelectedId} />)}</tbody>
                  </table>
                </div>
                <div className="flex items-center justify-between border-t border-outline-variant px-4 py-3 text-xs text-on-surface-variant sm:px-5">
                  <span>Hiển thị {visibleRows.length} hàng · Dữ liệu minh họa</span>
                  <span className="tnum">1 / 1</span>
                </div>
              </>
            )}
          </section>
          {selected && <ReportDetailPreview row={selected} onClose={() => setSelectedId(null)} />}
        </>
      )}
    </div>
  );
}

function getPreviewState(value: string | null): PreviewState {
  return value && VALID_STATES.includes(value as PreviewState) ? value as PreviewState : 'data';
}

function LoadingState() {
  return <div className="flex items-center gap-2.5 rounded-xl border border-outline-variant bg-surface-lowest px-5 py-10 text-sm text-on-surface-variant"><span className="material-symbols-outlined animate-spin text-lg">sync</span>Đang tải bản thiết kế báo cáo…</div>;
}

function PreviewMetrics({ rows }: { rows: ReportDesignPreviewRow[] }) {
  const totalRooms = rows.reduce((sum, row) => sum + row.totalRooms, 0);
  const occupiedRooms = rows.reduce((sum, row) => sum + row.occupiedRooms, 0);
  const activeContracts = rows.reduce((sum, row) => sum + row.activeContracts, 0);
  const expiringContracts = rows.reduce((sum, row) => sum + row.expiringContracts, 0);
  const metrics = [
    ['apartment', 'Tổng số phòng', `${totalRooms || 0}`, 'Dữ liệu minh họa', 'info'],
    ['meeting_room', 'Đang sử dụng', `${occupiedRooms || 0}`, 'Không phải số liệu thật', 'success'],
    ['description', 'Hợp đồng hiệu lực', `${activeContracts || 0}`, 'Trạng thái minh họa', 'neutral'],
    ['schedule', 'Sắp hết hạn', `${expiringContracts || 0}`, 'Cần xem lại · mẫu', 'warning'],
  ] as const;
  return <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">{metrics.map(([icon, label, value, hint, tone]) => <div key={label} className="flex min-h-[104px] items-center gap-3 rounded-xl border border-outline-variant bg-surface-lowest p-4"><span className={`flex h-11 w-11 shrink-0 items-center justify-center rounded-xl text-xl ${tone === 'success' ? 'bg-[#E4F8ED] text-[#079356]' : tone === 'warning' ? 'bg-[#FFF4D8] text-[#B97710]' : tone === 'neutral' ? 'bg-surface-container text-on-surface-variant' : 'bg-[#EAF2FF] text-[#075DE8]'}`}><span className="material-symbols-outlined">{icon}</span></span><div className="min-w-0"><p className="truncate text-xs text-on-surface-variant">{label}</p><p className="mt-1 text-xl font-bold text-on-surface tnum">{value}</p><p className="truncate text-[10px] text-on-surface-variant">{hint}</p></div></div>)}</div>;
}

function ReportTableRow({ row, onSelect }: { row: ReportDesignPreviewRow; onSelect: (id: string) => void }) {
  const meta = STATUS_META[row.status];
  return <tr className="border-t border-outline-variant transition hover:bg-surface-low"><td className="px-4 py-3"><span className="font-semibold text-on-surface">{row.building}</span><span className="block text-xs text-on-surface-variant">{row.period}</span></td><td className="px-4 py-3 whitespace-nowrap text-on-surface">{row.occupiedRooms} / {row.totalRooms}</td><td className="px-4 py-3 whitespace-nowrap text-on-surface">{Math.round((row.occupiedRooms / row.totalRooms) * 100)}%</td><td className="px-4 py-3 whitespace-nowrap text-on-surface">{row.activeContracts}</td><td className="px-4 py-3 whitespace-nowrap text-on-surface">{row.expiringContracts}</td><td className="px-4 py-3 whitespace-nowrap text-on-surface-variant tnum">{formatDate(row.asOf)}</td><td className="px-4 py-3"><StatusBadge tone={meta.tone} withDot>{meta.label}</StatusBadge></td><td className="px-4 py-3"><button type="button" onClick={() => onSelect(row.id)} className="rounded-lg border border-outline-variant px-3 py-1.5 text-xs font-semibold text-on-surface-variant hover:bg-surface-low">Xem tóm tắt</button></td></tr>;
}

function ReportCard({ row, onSelect }: { row: ReportDesignPreviewRow; onSelect: (id: string) => void }) {
  const meta = STATUS_META[row.status];
  return <li className="rounded-xl border border-outline-variant bg-surface-lowest p-4"><div className="flex items-start justify-between gap-3"><div><p className="font-display font-bold text-on-surface">{row.building}</p><p className="mt-1 text-xs text-on-surface-variant">{row.period}</p></div><StatusBadge tone={meta.tone} withDot>{meta.label}</StatusBadge></div><dl className="mt-4 grid grid-cols-2 gap-x-4 gap-y-3 text-sm"><Detail label="Phòng sử dụng" value={`${row.occupiedRooms} / ${row.totalRooms}`} /><Detail label="Tỷ lệ sử dụng" value={`${Math.round((row.occupiedRooms / row.totalRooms) * 100)}%`} /><Detail label="Hợp đồng hiệu lực" value={`${row.activeContracts}`} /><Detail label="Sắp hết hạn" value={`${row.expiringContracts}`} /><Detail label="Cập nhật" value={formatDate(row.asOf)} /></dl><button type="button" onClick={() => onSelect(row.id)} className="mt-4 w-full rounded-lg border border-outline-variant px-3 py-2 text-xs font-semibold text-on-surface-variant hover:bg-surface-low">Xem tóm tắt mẫu</button></li>;
}

function ReportDetailPreview({ row, onClose }: { row: ReportDesignPreviewRow; onClose: () => void }) {
  const meta = STATUS_META[row.status];
  return <section className="mt-5 rounded-xl border border-outline-variant bg-surface-lowest p-4 sm:p-5" aria-labelledby="report-preview-detail"><div className="flex flex-wrap items-start justify-between gap-3"><div><p className="text-xs text-on-surface-variant">Tóm tắt bản thiết kế · không phải report API</p><h2 id="report-preview-detail" className="mt-1 font-display text-lg font-bold text-on-surface">{row.building}</h2><p className="mt-1 text-xs text-on-surface-variant">{row.period} · cập nhật {formatDate(row.asOf)}</p></div><button type="button" onClick={onClose} className="rounded-lg border border-outline-variant px-3 py-1.5 text-xs font-semibold text-on-surface-variant hover:bg-surface-low">Đóng</button></div><div className="mt-4 grid gap-4 border-t border-outline-variant pt-4 sm:grid-cols-3"><Detail label="Tổng số phòng" value={`${row.totalRooms}`} /><Detail label="Đang sử dụng" value={`${row.occupiedRooms} (${Math.round((row.occupiedRooms / row.totalRooms) * 100)}%)`} /><Detail label="Hợp đồng hiệu lực" value={`${row.activeContracts}`} /><Detail label="Sắp hết hạn" value={`${row.expiringContracts}`} /><Detail label="Trạng thái mẫu" value={meta.label} /><Detail label="Nguồn" value="Fixture UI cô lập" /></div><div className="mt-4 rounded-lg border border-[#D5E5FB] bg-[#EDF5FF] p-3 text-xs leading-relaxed text-[#4C729F]">{row.note}</div></section>;
}

function Detail({ label, value }: { label: string; value: string }) {
  return <div><dt className="text-xs text-on-surface-variant">{label}</dt><dd className="mt-1 font-semibold text-on-surface">{value}</dd></div>;
}
