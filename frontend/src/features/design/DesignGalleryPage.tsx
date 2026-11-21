import { useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Alert } from '../../components/ui/Alert';
import { EmptyState } from '../../components/ui/EmptyState';
import { StatusBadge } from '../../components/ui/StatusBadge';

type PreviewState = 'data' | 'loading' | 'empty' | 'forbidden' | 'error';
type Group = 'owner-staff' | 'tenant';

type MetricFixture = {
  label: string;
  value: string;
  hint: string;
  icon: string;
  tone: 'blue' | 'success' | 'warning' | 'error';
};

type Screen = {
  id: string;
  group: Group;
  label: string;
  title: string;
  icon: string;
  subtitle: string;
  facts: string[];
  kind: 'table' | 'detail' | 'form' | 'dashboard';
  metrics?: MetricFixture[];
};

const OWNER_STAFF: Screen[] = [
  {
    id: '07-thu-chi', group: 'owner-staff', label: '07 · Thu chi', title: 'Quản lý thu chi', icon: 'account_balance_wallet',
    subtitle: 'Theo dõi phiếu thu, tiền cọc và trạng thái đối soát',
    facts: ['Mã giao dịch', 'Phòng', 'Khách thuê', 'Loại giao dịch', 'Số tiền', 'Ngày tạo', 'Trạng thái', 'Thao tác'], kind: 'table',
    metrics: [
      { label: 'Phiếu thu trong kỳ', value: '128', hint: 'Kỳ 11/2026 · minh họa', icon: 'receipt_long', tone: 'blue' },
      { label: 'Đã ghi nhận', value: '96,8 tr', hint: 'Dữ liệu minh họa', icon: 'check_circle', tone: 'success' },
      { label: 'Chờ đối soát', value: '18,4 tr', hint: 'Chưa xác minh', icon: 'schedule', tone: 'warning' },
      { label: 'Chờ hoàn cọc', value: '6,5 tr', hint: 'Chưa mở quy trình', icon: 'undo', tone: 'error' },
    ],
  },
  {
    id: '08-dat-phong', group: 'owner-staff', label: '08 · Đặt phòng', title: 'Đặt phòng', icon: 'event_available',
    subtitle: 'Theo dõi giữ chỗ và các khoản cọc trước khi lập hợp đồng',
    facts: ['Mã giữ chỗ', 'Phòng', 'Khách dự kiến', 'Ngày tạo', 'Hạn giữ chỗ', 'Cọc yêu cầu', 'Trạng thái', 'Thao tác'], kind: 'table',
    metrics: [
      { label: 'Yêu cầu giữ chỗ', value: '18', hint: 'Trong tháng · minh họa', icon: 'bookmark', tone: 'blue' },
      { label: 'Đang giữ chỗ', value: '07', hint: 'Chưa lập hợp đồng', icon: 'check_circle', tone: 'success' },
      { label: 'Chờ cọc', value: '04', hint: 'Chưa đủ điều kiện', icon: 'schedule', tone: 'warning' },
      { label: 'Hết hạn', value: '02', hint: 'Cần rà soát', icon: 'error', tone: 'error' },
    ],
  },
  { id: '09-phan-quyen', group: 'owner-staff', label: '09 · Người dùng', title: 'Người dùng & phân quyền', icon: 'admin_panel_settings', subtitle: 'Quản lý thành viên vận hành theo tòa nhà', facts: ['Tài khoản', 'Tòa nhà', 'Vai trò', 'Trạng thái', 'Cập nhật'], kind: 'table', metrics: [{ label: 'Người dùng mẫu', value: '24', hint: 'Tài khoản minh họa', icon: 'group', tone: 'blue' }, { label: 'Đang được giao', value: '09', hint: 'Có phạm vi tòa nhà', icon: 'verified_user', tone: 'success' }, { label: 'Sắp hết quyền', value: '02', hint: 'Trong 30 ngày tới', icon: 'schedule', tone: 'warning' }, { label: 'Đã thu hồi', value: '03', hint: 'Lịch sử minh họa', icon: 'block', tone: 'error' }] },
  { id: '10-hop-dong', group: 'owner-staff', label: '10 · Hợp đồng', title: 'Chi tiết hợp đồng', icon: 'description', subtitle: 'Thông tin hợp đồng và các bên liên quan', facts: ['Mã hợp đồng', 'Phòng', 'Thời hạn', 'Trạng thái', 'Người thuê'], kind: 'detail' },
  { id: '11-dien-nuoc', group: 'owner-staff', label: '11 · Điện nước', title: 'Ghi chỉ số điện nước', icon: 'water_drop', subtitle: 'Nhập và kiểm tra số mới của từng đồng hồ · Kỳ 11/2026', facts: ['Phòng', 'Loại', 'Mã công tơ', 'Chỉ số cũ', 'Chỉ số mới', 'Tiêu thụ', 'Trạng thái', 'Thao tác'], kind: 'form', metrics: [{ label: 'Đồng hồ hoạt động', value: '80', hint: 'Thuộc phạm vi minh họa', icon: 'bolt', tone: 'blue' }, { label: 'Đã ghi kỳ này', value: '62', hint: 'Cần kiểm tra nguồn', icon: 'check_circle', tone: 'success' }, { label: 'Chưa ghi', value: '14', hint: 'Cần nhập số mới', icon: 'schedule', tone: 'warning' }, { label: 'Cảnh báo', value: '04', hint: 'Chỉ số bất thường', icon: 'warning', tone: 'error' }] },
];

const TENANT: Screen[] = [
  { id: 'TEN-01', group: 'tenant', label: 'TEN-01 · Tổng quan', title: 'Tổng quan', icon: 'home', subtitle: 'Thông tin thuê và các mục cần chú ý', facts: ['Phòng đang thuê', 'Hợp đồng', 'Hóa đơn gần nhất', 'Thông báo'], kind: 'dashboard' },
  { id: 'TEN-02', group: 'tenant', label: 'TEN-02 · Phòng của tôi', title: 'Phòng của tôi', icon: 'meeting_room', subtitle: 'Thông tin căn phòng và tiện ích đang sử dụng', facts: ['Tòa nhà', 'Phòng', 'Giá công bố', 'Tiện ích', 'Nội quy'], kind: 'detail' },
  { id: 'TEN-03', group: 'tenant', label: 'TEN-03 · Hợp đồng', title: 'Hợp đồng của tôi', icon: 'description', subtitle: 'Thời hạn, giá thuê và các bên trong hợp đồng', facts: ['Mã hợp đồng', 'Ngày bắt đầu', 'Ngày kết thúc', 'Tiền cọc', 'Trạng thái'], kind: 'detail' },
  { id: 'TEN-04', group: 'tenant', label: 'TEN-04 · Hóa đơn', title: 'Hóa đơn của tôi', icon: 'receipt_long', subtitle: 'Các kỳ hóa đơn được máy chủ cung cấp', facts: ['Kỳ hóa đơn', 'Phòng', 'Tổng cộng', 'Hạn thanh toán', 'Trạng thái'], kind: 'table' },
  { id: 'TEN-05', group: 'tenant', label: 'TEN-05 · Chi tiết hóa đơn', title: 'Chi tiết hóa đơn', icon: 'receipt', subtitle: 'Phân rã tiền thuê và các dịch vụ trong kỳ', facts: ['Kỳ hóa đơn', 'Tiền thuê', 'Điện', 'Nước', 'Dịch vụ'], kind: 'detail' },
  { id: 'TEN-06', group: 'tenant', label: 'TEN-06 · Điện nước', title: 'Điện nước', icon: 'bolt', subtitle: 'Lịch sử chỉ số theo phòng của chính bạn', facts: ['Kỳ ghi', 'Chỉ số điện', 'Chỉ số nước', 'Ngày ghi', 'Trạng thái'], kind: 'table' },
  { id: 'TEN-07', group: 'tenant', label: 'TEN-07 · Thanh toán', title: 'Thanh toán', icon: 'payments', subtitle: 'Trạng thái thanh toán và đối soát', facts: ['Kỳ', 'Số tiền minh họa', 'Trạng thái', 'Cập nhật'], kind: 'form' },
  { id: 'TEN-08', group: 'tenant', label: 'TEN-08 · Trả phòng', title: 'Yêu cầu trả phòng', icon: 'logout', subtitle: 'Theo dõi yêu cầu và các bước closeout', facts: ['Phòng', 'Ngày dự kiến', 'Bước hiện tại', 'Trạng thái'], kind: 'form' },
  { id: 'TEN-09', group: 'tenant', label: 'TEN-09 · Tìm phòng', title: 'Tìm & đặt phòng', icon: 'search', subtitle: 'Tìm phòng trống và xem thông tin công bố', facts: ['Tòa nhà', 'Phòng', 'Diện tích', 'Giá công bố', 'Tình trạng'], kind: 'table' },
  { id: 'TEN-10', group: 'tenant', label: 'TEN-10 · Hồ sơ', title: 'Hồ sơ & hỗ trợ', icon: 'support_agent', subtitle: 'Thông tin tài khoản và kênh hỗ trợ', facts: ['Họ tên', 'Liên hệ', 'Hướng dẫn', 'Trạng thái hỗ trợ'], kind: 'detail' },
];

const STATES: PreviewState[] = ['data', 'loading', 'empty', 'forbidden', 'error'];

const fixtureRows: Record<string, string[][]> = {
  '07-thu-chi': [
    ['PT-DEMO-042', 'A203', 'Khách thuê minh họa 01', 'Thu tiền thuê · minh họa', 'Số tiền minh họa', '18/11/2026', 'Chưa xác minh', 'Chi tiết · chưa mở'],
    ['PT-DEMO-041', 'A205', 'Khách thuê minh họa 02', 'Thu một phần · minh họa', 'Số tiền minh họa', '17/11/2026', 'Chờ đối soát', 'Chi tiết · chưa mở'],
    ['COC-DEMO-012', 'B102', 'Khách thuê minh họa 03', 'Tiền cọc · minh họa', 'Số tiền minh họa', '16/11/2026', 'Chưa mở', 'Chi tiết · chưa mở'],
    ['PT-DEMO-040', 'A301', 'Khách thuê minh họa 04', 'Thu tiền thuê · minh họa', 'Số tiền minh họa', '15/11/2026', 'Cần chứng từ', 'Chi tiết · chưa mở'],
  ],
  '08-dat-phong': [
    ['RES-DEMO-041', 'A207', 'Khách thuê minh họa 01', '18/11/2026', '25/11/2026', 'Cọc minh họa', 'Đang giữ chỗ', 'Xem · chưa mở'],
    ['RES-DEMO-040', 'B104', 'Khách thuê minh họa 02', '17/11/2026', '22/11/2026', 'Cọc minh họa', 'Chờ cọc', 'Xem · chưa mở'],
    ['RES-DEMO-039', 'C305', 'Khách thuê minh họa 03', '15/11/2026', '20/11/2026', 'Cọc minh họa', 'Đang giữ chỗ', 'Xem · chưa mở'],
    ['RES-DEMO-038', 'B201', 'Khách thuê minh họa 04', '10/11/2026', '15/11/2026', 'Cọc minh họa', 'Hết hạn', 'Xem · chưa mở'],
  ],
  '09-phan-quyen': [
    ['Tài khoản minh họa 01', 'Ngọc Trang A', 'Chủ sở hữu', 'Quản lý toàn tòa', 'Đang hiệu lực'],
    ['Tài khoản minh họa 02', 'Ngọc Trang A', 'Nhân viên', 'Phòng, điện nước', 'Đang hiệu lực'],
    ['Tài khoản minh họa 03', 'Ngọc Trang B', 'Nhân viên', 'Hợp đồng · đọc', 'Sắp hết hạn'],
    ['Tài khoản minh họa 04', 'Ngọc Trang A', 'Nhân viên', 'Quản lý phòng', 'Đã thu hồi'],
  ],
  '11-dien-nuoc': [
    ['A201', 'Điện', 'EV-DEMO-201', 'Chỉ số minh họa', 'Chỉ số minh họa', 'Tiêu thụ minh họa', 'Đã ghi · minh họa', 'Chi tiết · chưa mở'],
    ['A203', 'Nước', 'WT-DEMO-203', 'Chỉ số minh họa', 'Chỉ số minh họa', 'Tiêu thụ minh họa', 'Đã ghi · minh họa', 'Chi tiết · chưa mở'],
    ['A204', 'Điện', 'EV-DEMO-204', 'Chỉ số minh họa', '—', '—', 'Chưa ghi', 'Nhập số · chưa mở'],
    ['A208', 'Điện', 'EV-DEMO-208', 'Chỉ số minh họa', 'Chỉ số minh họa', 'Tiêu thụ minh họa', 'Cần kiểm tra', 'Kiểm tra · chưa mở'],
  ],
  'TEN-04': [
    ['INV-DEMO-021', '11/2026', 'Số tiền minh họa', 'Chưa xác minh', 'Xem · chưa mở'],
    ['INV-DEMO-020', '10/2026', 'Số tiền minh họa', 'Đã đối soát · minh họa', 'Xem · chưa mở'],
    ['INV-DEMO-019', '09/2026', 'Số tiền minh họa', 'Đã đối soát · minh họa', 'Xem · chưa mở'],
  ],
  'TEN-06': [
    ['11/2026', 'Chỉ số điện minh họa', 'Chỉ số nước minh họa', '18/11/2026', 'Đã ghi · minh họa'],
    ['10/2026', 'Chỉ số điện minh họa', 'Chỉ số nước minh họa', '18/10/2026', 'Đã ghi · minh họa'],
  ],
  'TEN-09': [
    ['A207', 'Tòa mẫu A', 'Diện tích minh họa', 'Giá công bố minh họa', 'Phòng trống · minh họa'],
    ['B104', 'Tòa mẫu B', 'Diện tích minh họa', 'Giá công bố minh họa', 'Đang giữ chỗ · minh họa'],
    ['C305', 'Tòa mẫu C', 'Diện tích minh họa', 'Giá công bố minh họa', 'Phòng trống · minh họa'],
  ],
  default: [
    ['Tòa mẫu A', 'Phòng A203', 'Đang xử lý', '18/11/2026'],
    ['Tòa mẫu B', 'Phòng B112', 'Đã xác nhận', '17/11/2026'],
    ['Tòa mẫu C', 'Phòng C305', 'Chưa mở', '16/11/2026'],
  ],
};

export function DesignGalleryPage() {
  const [params, setParams] = useSearchParams();
  const [group, setGroup] = useState<Group>((params.get('group') as Group) === 'tenant' ? 'tenant' : 'owner-staff');
  const [selectedId, setSelectedId] = useState(params.get('screen') ?? '07-thu-chi');
  const [state, setState] = useState<PreviewState>(getState(params.get('state')));
  const [query, setQuery] = useState('');
  const screens = group === 'tenant' ? TENANT : OWNER_STAFF;
  const selected = screens.find((screen) => screen.id === selectedId) ?? screens[0]!;
  const sourceRows = fixtureRows[selected.id] ?? fixtureRows.default!;
  const filteredRows = sourceRows.filter((row) => row.join(' ').toLocaleLowerCase('vi').includes(query.trim().toLocaleLowerCase('vi')));

  function selectGroup(next: Group) {
    const nextScreen = (next === 'tenant' ? TENANT[0] : OWNER_STAFF[0])!;
    setGroup(next);
    setSelectedId(nextScreen.id);
    setParams({ group: next, screen: nextScreen.id, state });
  }

  function selectScreen(id: string) {
    setSelectedId(id);
    setParams({ group, screen: id, state });
  }

  function selectState(next: PreviewState) {
    setState(next);
    setParams({ group, screen: selected.id, state: next });
  }

  return (
    <div className="min-h-screen bg-[#F5F8FC]">
      <header className="flex h-[70px] items-center gap-4 border-b border-[#DFE7F2] bg-white px-4 sm:px-6">
        <div className="flex items-center gap-3">
          <span className="flex h-9 w-9 items-center justify-center rounded-lg bg-[#13BE7B] text-xl font-bold text-[#132337]">⌂</span>
          <div className="leading-tight">
            <p className="font-display text-base font-bold text-[#152640] sm:text-lg">Ngọc Trang</p>
            <p className="hidden text-[11px] text-[#71829B] sm:block">{group === 'tenant' ? 'Cổng thông tin khách thuê · UI review' : 'Hệ thống quản lý nhà trọ · UI review'}</p>
          </div>
        </div>
        <div className="ml-auto flex items-center gap-3">
          <span className="hidden rounded-full bg-[#EDF5FF] px-3 py-1.5 text-[10px] font-bold tracking-wide text-[#2264C7] sm:inline-flex">DỮ LIỆU MẪU</span>
          <span className="flex h-9 w-9 items-center justify-center rounded-full bg-[#EAF2FF] text-xs font-bold text-[#2264C7]">NT</span>
        </div>
      </header>
      <div className={`mx-auto max-w-[1440px] space-y-4 p-4 sm:p-5 lg:p-6 ${group === 'tenant' ? 'pb-24 lg:pb-6' : ''}`}>
      <section className="rounded-2xl border border-[#D5E5FB] bg-[#EDF5FF] p-4 sm:p-5" aria-label="Phạm vi bản thiết kế">
        <div className="flex items-start gap-3">
          <span className="material-symbols-outlined mt-0.5 text-xl text-primary">design_services</span>
          <div className="min-w-0">
            <p className="font-semibold text-[#173E70]">Bản thiết kế UI · Chưa tích hợp API · Dữ liệu minh họa</p>
            <p className="mt-1 text-xs leading-relaxed text-[#4C729F]">Gallery này chuyển các frame Figma đã bàn giao thành preview cô lập trong development. Không có thao tác tài chính, đặt phòng, ghi chỉ số, trả phòng hoặc cấp quyền nào được gửi lên máy chủ.</p>
          </div>
        </div>
      </section>

      <div className="grid gap-4 xl:grid-cols-[250px_minmax(0,1fr)]">
        <aside className="rounded-2xl border border-[#243A55] bg-[#132337] p-3 text-white shadow-sm" aria-label="Chọn màn thiết kế">
          <div className="mb-3 grid grid-cols-2 gap-1 rounded-xl bg-[#0D1B2A] p-1">
            <button type="button" className={group === 'owner-staff' ? selectedTabDark : tabDark} onClick={() => selectGroup('owner-staff')}>Owner / Staff</button>
            <button type="button" className={group === 'tenant' ? selectedTabDark : tabDark} onClick={() => selectGroup('tenant')}>Tenant</button>
          </div>
          <p className="px-2 pb-2 text-[10px] font-bold tracking-widest text-white/45 uppercase">{group === 'tenant' ? 'Tenant Portal · 10 desktop frame' : 'Quản lý · 5 frame'}</p>
          <nav aria-label="Danh sách frame" className="space-y-1">
            {screens.map((screen) => (
              <button key={screen.id} type="button" onClick={() => selectScreen(screen.id)} className={`flex w-full items-center gap-2 rounded-lg px-2.5 py-2 text-left text-xs transition ${selected.id === screen.id ? 'bg-[#2264C7] font-semibold text-white' : 'text-white/75 hover:bg-[#1D3552] hover:text-white'}`}>
                <span className="material-symbols-outlined text-base">{screen.icon}</span><span className="min-w-0 truncate">{screen.label}</span>
              </button>
            ))}
          </nav>
          <p className="mt-5 border-t border-white/10 px-2 pt-3 text-[10px] leading-relaxed text-white/45">{group === 'tenant' ? 'KHÁCH THUÊ · DEMO UI' : 'BẢN MẪU · CHƯA NỐI BACKEND'}<br />Chỉ dùng để review bố cục Figma</p>
        </aside>

        <main className="min-w-0 space-y-4">
          <header className="flex flex-col gap-3 rounded-2xl border border-outline-variant bg-surface-lowest p-4 sm:flex-row sm:items-start sm:justify-between sm:p-5">
            <div className="min-w-0">
              <p className="text-xs text-on-surface-variant">Trang chủ / Bản thiết kế / {selected.label}</p>
              <div className="mt-1 flex flex-wrap items-center gap-2"><h1 className="font-display text-2xl font-bold tracking-tight text-on-surface">{selected.title}</h1><StatusBadge tone="info">Bản thiết kế</StatusBadge></div>
              <p className="mt-1 text-sm text-on-surface-variant">{selected.subtitle}</p>
            </div>
            <label className="flex shrink-0 items-center gap-2 text-xs text-on-surface-variant"><span>Trạng thái preview</span><select aria-label="Trạng thái preview" value={state} onChange={(event) => selectState(event.target.value as PreviewState)} className="h-9 rounded-lg border border-outline-variant bg-surface-lowest px-2 text-xs text-on-surface"><option value="data">Có dữ liệu mẫu</option><option value="loading">Đang tải</option><option value="empty">Rỗng</option><option value="forbidden">403</option><option value="error">Lỗi</option></select></label>
          </header>

          {state === 'loading' && <StatePanel icon="sync" text="Đang tải bản thiết kế minh họa…" />}
          {state === 'empty' && <EmptyState icon="inbox" title="Chưa có dữ liệu minh họa" description="Đây là trạng thái rỗng của UI handoff. Chưa có API production nào được gọi." />}
          {state === 'forbidden' && <Alert tone="warning">Máy chủ chưa mở quyền cho flow này (403). Preview không tự mở quyền và không đại diện cho API thật.</Alert>}
          {state === 'error' && <Alert tone="error">Không tải được dữ liệu trong preview. Đây là state minh họa, không phải lỗi kết nối production.</Alert>}
          {state === 'data' && <DesignSurface screen={selected} rows={filteredRows} query={query} onQueryChange={setQuery} />}
        </main>
      </div>
      </div>
    </div>
  );
}

const tabDark = 'rounded-lg px-2 py-1.5 text-[11px] font-semibold text-white/60 transition hover:bg-[#1D3552]';
const selectedTabDark = 'rounded-lg bg-white px-2 py-1.5 text-[11px] font-bold text-[#132337] shadow-sm';

function DesignSurface({ screen, rows, query, onQueryChange }: { screen: Screen; rows: string[][]; query: string; onQueryChange: (value: string) => void }) {
  const metrics = screen.metrics ?? screen.facts.slice(0, 4).map((fact, index) => ({
    label: fact,
    value: index === 0 ? '24' : index === 1 ? '12' : index === 2 ? 'Kỳ mẫu' : 'Theo dõi',
    hint: 'Dữ liệu minh họa',
    icon: ['apartment', 'description', 'schedule', 'verified'][index] ?? 'info',
    tone: (index === 3 ? 'warning' : index === 1 ? 'success' : 'blue') as MetricFixture['tone'],
  }));

  if (screen.id === '09-phan-quyen') return <OwnerAccessSurface screen={screen} rows={rows} />;
  if (screen.id === '10-hop-dong') return <OwnerContractSurface screen={screen} />;
  if (screen.id === 'TEN-01') {
    return <TenantOverviewSurface screen={screen} />;
  }
  if (screen.id === 'TEN-02') {
    return <TenantRoomSurface screen={screen} />;
  }
  if (screen.id === 'TEN-04') {
    return <TenantInvoiceSurface screen={screen} rows={rows} query={query} onQueryChange={onQueryChange} />;
  }
  if (screen.id === 'TEN-03') return <TenantContractSurface screen={screen} />;
  if (screen.id === 'TEN-05') return <TenantInvoiceDetailSurface screen={screen} />;
  if (screen.id === 'TEN-06') return <TenantUtilitiesSurface screen={screen} rows={rows} />;
  if (screen.id === 'TEN-07') return <TenantPaymentSurface screen={screen} />;
  if (screen.id === 'TEN-08') return <TenantMoveOutSurface screen={screen} />;
  if (screen.id === 'TEN-09') return <TenantBookingSurface screen={screen} rows={rows} query={query} onQueryChange={onQueryChange} />;
  if (screen.id === 'TEN-10') return <TenantProfileSupportSurface screen={screen} />;

  return (
    <section className="space-y-4" aria-label={`${screen.title} bản thiết kế`}>
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        {metrics.map((metric) => <Metric key={metric.label} {...metric} />)}
      </div>
      {screen.id === '07-thu-chi' && <InfoCallout title="Phân biệt bằng chứng thanh toán:">Tiền đã khai báo không đồng nghĩa giao dịch đã đối soát. Preview không hiển thị chứng từ ngân hàng hoặc trạng thái máy chủ.</InfoCallout>}
      {screen.id === '08-dat-phong' && <InfoCallout title="Kiểm tra điều kiện giữ chỗ:">Các khoản cọc và tình trạng giữ chỗ dưới đây chỉ là fixture, không ghi nhận tiền hoặc tạo reservation.</InfoCallout>}
      {screen.id === '11-dien-nuoc' && <InfoCallout title="Nguyên tắc ghi số:">Chỉ số mới và tiêu thụ là dữ liệu minh họa. Preview không ghi meter, thay đồng hồ hoặc tạo hóa đơn.</InfoCallout>}
      <div className="rounded-2xl border border-outline-variant bg-surface-lowest p-4 sm:p-5">
        <div className="flex flex-col gap-3 border-b border-outline-variant pb-4 sm:flex-row sm:items-center sm:justify-between"><div><h2 className="font-display text-base font-bold text-on-surface">{screen.id === '07-thu-chi' ? 'Danh sách giao dịch' : screen.id === '08-dat-phong' ? 'Danh sách đặt phòng' : screen.id === '11-dien-nuoc' ? 'Bảng ghi chỉ số kỳ 11/2026' : screen.kind === 'dashboard' ? 'Tổng quan minh họa' : screen.title}</h2><p className="mt-1 text-xs text-on-surface-variant">Các trường dưới đây là fixture UI cô lập · Dữ liệu minh họa</p></div><input type="search" aria-label="Tìm trong bản thiết kế" value={query} onChange={(event) => onQueryChange(event.target.value)} placeholder="Tìm minh họa…" className="h-10 w-full rounded-lg border border-outline-variant bg-surface-low px-3 text-sm text-on-surface sm:max-w-[230px]" /></div>
        {screen.kind === 'table' && <PreviewTable screen={screen} rows={rows} />}
        {screen.kind === 'dashboard' && <Dashboard screen={screen} />}
        {screen.kind === 'detail' && <DetailSurface screen={screen} />}
        {screen.kind === 'form' && <FormSurface screen={screen} />}
      </div>
    </section>
  );
}

function TenantOverviewSurface({ screen }: { screen: Screen }) {
  return <section className="space-y-4" aria-label={`${screen.title} bản thiết kế`}>
    <div className="rounded-2xl border border-[#D5E5FB] bg-[#EAF3FF] p-5 sm:p-7">
      <p className="text-[11px] font-bold tracking-widest text-[#075DE8] uppercase">Chào mừng bạn trở lại</p>
      <h2 className="mt-2 font-display text-2xl font-bold text-[#152640]">Phòng minh họa đang chờ bạn</h2>
      <p className="mt-2 text-sm text-[#486487]">Tòa mẫu A · Tầng 2 · Thông tin thuê chỉ dùng để review UI</p>
      <button type="button" disabled className="mt-5 rounded-lg bg-[#075DE8] px-4 py-2.5 text-xs font-semibold text-white opacity-60" title="Chưa có tenant API production">Xem phòng của tôi · chưa mở</button>
    </div>
    <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
      <Metric label="Phòng đang thuê" value="A203" hint="Tầng 2 · dữ liệu mẫu" icon="meeting_room" tone="blue" />
      <Metric label="Hợp đồng" value="Minh họa" hint="Chưa xác minh" icon="description" tone="success" />
      <Metric label="Hóa đơn gần nhất" value="01" hint="Dữ liệu minh họa" icon="receipt_long" tone="warning" />
      <Metric label="Trạng thái tài chính" value="Chưa mở" hint="Không phải công nợ" icon="payments" tone="error" />
    </div>
    <div className="grid gap-4 lg:grid-cols-[1.2fr_0.8fr]">
      <section className="rounded-2xl border border-outline-variant bg-surface-lowest p-4 sm:p-5">
        <div className="flex items-start justify-between gap-3 border-b border-outline-variant pb-4"><div><h2 className="font-display text-base font-bold text-on-surface">Hóa đơn cần chú ý</h2><p className="mt-1 text-xs text-on-surface-variant">Kỳ minh họa · Không phải khoản phải thu thật</p></div><StatusBadge tone="warning" withDot>Chưa xác minh</StatusBadge></div>
        <p className="mt-5 text-[10px] font-semibold tracking-wide text-on-surface-variant uppercase">Tổng số tiền minh họa</p><p className="mt-1 text-2xl font-bold text-on-surface">Số tiền minh họa</p>
        <dl className="mt-5 divide-y divide-outline-variant text-sm"><div className="flex justify-between gap-3 py-3"><dt className="text-on-surface-variant">Tiền thuê phòng</dt><dd className="font-semibold text-on-surface">Dữ liệu minh họa</dd></div><div className="flex justify-between gap-3 py-3"><dt className="text-on-surface-variant">Điện · nước · dịch vụ</dt><dd className="font-semibold text-on-surface">Dữ liệu minh họa</dd></div></dl>
        <button type="button" disabled className="mt-5 rounded-lg bg-[#075DE8] px-4 py-2.5 text-xs font-semibold text-white opacity-60" title="Chưa có invoice API production">Xem chi tiết · chưa mở</button>
      </section>
      <section className="rounded-2xl border border-outline-variant bg-surface-lowest p-4 sm:p-5"><h2 className="font-display text-base font-bold text-on-surface">Cập nhật gần đây</h2><p className="mt-1 text-xs text-on-surface-variant">Tổng hợp minh họa riêng cho phòng</p><div className="mt-4 divide-y divide-outline-variant">{['Hóa đơn kỳ mẫu được dựng trong UI', 'Chỉ số điện nước · dữ liệu minh họa', 'Hợp đồng · chưa lấy từ tài khoản thật'].map((item) => <div key={item} className="flex gap-3 py-3 text-sm"><span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-[#EAF2FF] text-[#2264C7]"><span className="material-symbols-outlined text-base">info</span></span><span className="font-semibold text-on-surface">{item}</span></div>)}</div></section>
    </div>
  </section>;
}

function TenantRoomSurface({ screen }: { screen: Screen }) {
  return <section className="space-y-4" aria-label={`${screen.title} bản thiết kế`}>
    <div className="grid gap-4 rounded-2xl border border-outline-variant bg-surface-lowest p-4 sm:p-5 lg:grid-cols-[0.9fr_1.1fr]">
      <div className="flex min-h-[220px] items-center justify-center rounded-xl bg-[#EAF3FF] p-6"><div className="relative h-32 w-56 rounded-xl border-2 border-[#9CC6F5] bg-white"><div className="absolute left-10 top-12 h-12 w-20 rounded-lg bg-[#CFE3F8]" /><div className="absolute right-10 top-6 h-20 w-14 rounded-lg bg-[#D9EDF8]" /></div></div>
      <div className="flex flex-col justify-center"><p className="text-[11px] font-bold tracking-widest text-[#075DE8] uppercase">Phòng đang thuê</p><div className="mt-2 flex flex-wrap items-center gap-3"><h2 className="font-display text-3xl font-bold text-on-surface">A203</h2><StatusBadge tone="success" withDot>Đang thuê · minh họa</StatusBadge></div><p className="mt-2 text-sm text-on-surface-variant">Tòa mẫu A · Tầng 2 · Chỉ để review bố cục</p><dl className="mt-6 grid grid-cols-2 gap-4 border-t border-outline-variant pt-5 sm:grid-cols-4"><div><dt className="text-xs text-on-surface-variant">Diện tích</dt><dd className="mt-1 font-semibold text-on-surface">Minh họa</dd></div><div><dt className="text-xs text-on-surface-variant">Giá thuê</dt><dd className="mt-1 font-semibold text-on-surface">Chưa mở</dd></div><div><dt className="text-xs text-on-surface-variant">Sức chứa</dt><dd className="mt-1 font-semibold text-on-surface">Minh họa</dd></div><div><dt className="text-xs text-on-surface-variant">Hướng</dt><dd className="mt-1 font-semibold text-on-surface">Chưa xác minh</dd></div></dl></div>
    </div>
    <div className="grid gap-4 lg:grid-cols-2"><section className="rounded-2xl border border-outline-variant bg-surface-lowest p-4 sm:p-5"><h2 className="font-display text-base font-bold text-on-surface">Giá dịch vụ công bố</h2><p className="mt-1 text-xs text-on-surface-variant">Không thay thế đơn giá hóa đơn thật</p>{['Điện / kWh', 'Nước / m³', 'Phí dịch vụ'].map((label) => <div key={label} className="flex justify-between gap-3 border-b border-outline-variant py-4 text-sm"><span className="font-semibold text-on-surface">{label}</span><span className="font-semibold text-on-surface-variant">Dữ liệu minh họa</span></div>)}<InfoCallout title="Đơn giá minh họa:">Xem dữ liệu được máy chủ xác minh khi API tenant được mở.</InfoCallout></section><section className="rounded-2xl border border-outline-variant bg-surface-lowest p-4 sm:p-5"><h2 className="font-display text-base font-bold text-on-surface">Nội quy và lưu ý</h2><p className="mt-1 text-xs text-on-surface-variant">Bố cục thông tin công bố</p>{['Giờ yên tĩnh', 'An toàn', 'Vệ sinh', 'Phản hồi'].map((label) => <div key={label} className="mt-3 rounded-xl bg-[#F5F8FC] p-3"><p className="font-semibold text-on-surface">{label}</p><p className="mt-1 text-xs text-on-surface-variant">Nội dung minh họa · chưa lấy từ tòa nhà thật</p></div>)}</section></div>
  </section>;
}

function TenantInvoiceSurface({ screen, rows, query, onQueryChange }: { screen: Screen; rows: string[][]; query: string; onQueryChange: (value: string) => void }) {
  return <section className="space-y-4" aria-label={`${screen.title} bản thiết kế`}>
    <div className="grid grid-cols-2 gap-3 lg:grid-cols-4"><Metric label="Hóa đơn trong kỳ" value="01" hint="Kỳ minh họa" icon="receipt_long" tone="blue" /><Metric label="Cần xác minh" value="Số tiền mẫu" hint="Không phải công nợ" icon="schedule" tone="warning" /><Metric label="Đã đối soát" value="08" hint="Fixture UI" icon="check_circle" tone="success" /><Metric label="Bản nháp" value="0" hint="Không có trạng thái thật" icon="draft" tone="error" /></div>
    <InfoCallout title="Hóa đơn phát hành khác với bản nháp:">Trạng thái tiền chỉ cập nhật khi hệ thống xác minh chính thức. Preview không hiển thị QR, chứng từ hoặc số tiền thật.</InfoCallout>
    <section className="rounded-2xl border border-outline-variant bg-surface-lowest p-4 sm:p-5"><div className="flex flex-col gap-3 border-b border-outline-variant pb-4 sm:flex-row sm:items-center sm:justify-between"><div><h2 className="font-display text-base font-bold text-on-surface">Danh sách hóa đơn</h2><p className="mt-1 text-xs text-on-surface-variant">Chỉ là fixture local · Không có thao tác ghi</p></div><input type="search" aria-label="Tìm trong bản thiết kế" value={query} onChange={(event) => onQueryChange(event.target.value)} placeholder="Tìm mã hóa đơn, kỳ…" className="h-10 w-full rounded-lg border border-outline-variant bg-surface-low px-3 text-sm text-on-surface sm:max-w-[230px]" /></div><div className="mt-4 space-y-3 lg:hidden">{rows.map((row, index) => <article key={`${row[0]}-${index}`} className="rounded-xl border border-outline-variant p-4"><div className="flex items-start justify-between gap-3"><div><h3 className="font-semibold text-on-surface">{row[0]}</h3><p className="mt-1 text-xs text-on-surface-variant">Kỳ {row[1]} · Dữ liệu minh họa</p></div><StatusBadge tone={/đối soát/i.test(row[3] ?? '') ? 'success' : 'warning'} withDot>{row[3]}</StatusBadge></div><div className="mt-3 grid grid-cols-2 gap-2 text-xs"><div><p className="text-on-surface-variant">Tổng cộng</p><p className="mt-1 font-semibold text-on-surface">Số tiền minh họa</p></div><div><p className="text-on-surface-variant">Trạng thái</p><p className="mt-1 font-semibold text-on-surface">Chưa xác minh</p></div></div></article>)}</div><div className="mt-4 hidden overflow-x-auto lg:block"><table className="w-full min-w-[700px] border-collapse text-sm"><thead><tr className="bg-surface-low text-left text-xs font-semibold text-on-surface-variant"><th className="px-3 py-3">Mã hóa đơn</th><th className="px-3 py-3">Kỳ</th><th className="px-3 py-3">Tổng cộng</th><th className="px-3 py-3">Trạng thái</th><th className="px-3 py-3">Chi tiết</th></tr></thead><tbody>{rows.map((row, index) => <tr key={`${row[0]}-${index}`} className="border-t border-outline-variant"><td className="px-3 py-3 font-semibold text-on-surface">{row[0]}</td><td className="px-3 py-3 text-on-surface-variant">{row[1]}</td><td className="px-3 py-3 text-on-surface-variant">Số tiền minh họa</td><td className="px-3 py-3"><StatusBadge tone={/đối soát/i.test(row[3] ?? '') ? 'success' : 'warning'} withDot>{row[3]}</StatusBadge></td><td className="px-3 py-3"><button type="button" disabled className="font-semibold text-primary opacity-60" title="Chưa có API production">Xem · chưa mở</button></td></tr>)}</tbody></table></div><p className="mt-4 text-xs text-on-surface-variant">{rows.length} hàng · Dữ liệu minh họa · Không phải công nợ production</p></section>
  </section>;
}

function OwnerAccessSurface({ screen, rows }: { screen: Screen; rows: string[][] }) {
  return <section className="space-y-4" aria-label={`${screen.title} bản thiết kế`}>
    <div className="grid grid-cols-2 gap-3 lg:grid-cols-4"><Metric label="Người dùng mẫu" value="24" hint="Tài khoản minh họa" icon="group" tone="blue" /><Metric label="Đang được giao" value="09" hint="Có phạm vi tòa nhà" icon="verified_user" tone="success" /><Metric label="Sắp hết quyền" value="02" hint="Trong 30 ngày tới" icon="schedule" tone="warning" /><Metric label="Đã thu hồi" value="03" hint="Lịch sử minh họa" icon="block" tone="error" /></div>
    <InfoCallout title="Phân quyền là dữ liệu máy chủ:">Bảng chỉ minh họa roster và phạm vi được giao. Preview không cấp, thu hồi hoặc khôi phục quyền.</InfoCallout>
    <section className="rounded-2xl border border-outline-variant bg-surface-lowest p-4 sm:p-5"><div className="flex items-start justify-between gap-3 border-b border-outline-variant pb-4"><div><h2 className="font-display text-base font-bold text-on-surface">Danh sách người dùng vận hành</h2><p className="mt-1 text-xs text-on-surface-variant">Vai trò, phạm vi và trạng thái chỉ là fixture.</p></div><button type="button" disabled className="rounded-lg bg-primary px-3 py-2 text-xs font-semibold text-white opacity-50" title="Chưa mở grant API">Cấp quyền · chưa mở</button></div><PreviewTable screen={screen} rows={rows} /></section>
  </section>;
}

function OwnerContractSurface({ screen }: { screen: Screen }) {
  return <section className="space-y-4" aria-label={`${screen.title} bản thiết kế`}>
    <div className="flex flex-wrap items-start justify-between gap-4 rounded-2xl border border-outline-variant bg-surface-lowest p-5"><div><p className="text-xs text-on-surface-variant">Chi tiết bản thiết kế · không phải hợp đồng API</p><h2 className="mt-1 font-display text-2xl font-bold text-on-surface">HD-DEMO-2026-001</h2><p className="mt-1 text-sm text-on-surface-variant">Phòng A203 · Tòa mẫu A · Khách thuê minh họa</p></div><StatusBadge tone="success" withDot>Đang hiệu lực · mẫu</StatusBadge></div>
    <div className="grid gap-4 lg:grid-cols-[1.2fr_0.8fr]"><section className="rounded-2xl border border-outline-variant bg-surface-lowest p-5"><h2 className="font-display text-base font-bold text-on-surface">Thông tin hợp đồng</h2><p className="mt-1 text-xs text-on-surface-variant">Facts dùng để review bố cục, chưa lấy từ máy chủ.</p><dl className="mt-5 grid gap-5 sm:grid-cols-2">{[['Người thuê chính','Khách thuê minh họa'],['Mã phòng','A203 · Tầng 2'],['Ngày bắt đầu','01/03/2026 · mẫu'],['Ngày hết hạn','28/02/2027 · mẫu'],['Tiền thuê','Số tiền minh họa'],['Tiền cọc','Số tiền minh họa']].map(([label,value]) => <div key={label}><dt className="text-xs text-on-surface-variant">{label}</dt><dd className="mt-1 font-semibold text-on-surface">{value}</dd></div>)}</dl></section><section className="rounded-2xl border border-outline-variant bg-surface-lowest p-5"><h2 className="font-display text-base font-bold text-on-surface">Hiệu lực và thao tác</h2><StatusBadge tone="success" withDot>Đang hiệu lực · mẫu</StatusBadge><p className="mt-5 text-sm leading-relaxed text-on-surface-variant">Không kết thúc trực tiếp từ màn này. Closeout phải đi qua quy trình trả phòng được backend cho phép.</p><button type="button" disabled className="mt-5 w-full rounded-lg border border-outline-variant px-3 py-2 text-xs font-semibold text-on-surface-variant opacity-60" title="Backend chưa mở closeout">Kết thúc hợp đồng · chưa mở</button></section></div>
    <section className="rounded-2xl border border-outline-variant bg-surface-lowest p-5"><h2 className="font-display text-base font-bold text-on-surface">Các mốc hợp đồng</h2><div className="mt-6 grid grid-cols-4 gap-2">{['Bắt đầu · mẫu','Ghi nhận cọc · mẫu','Đang hiệu lực · mẫu','Hết hạn dự kiến · mẫu'].map((label,index) => <div key={label} className="relative text-center text-xs text-on-surface-variant"><span className={`mx-auto flex h-8 w-8 items-center justify-center rounded-full ${index === 2 ? 'bg-primary text-white' : 'bg-[#DCEBFA] text-primary'}`}>{index + 1}</span><p className="mt-2">{label}</p></div>)}</div></section>
  </section>;
}

function TenantContractSurface({ screen }: { screen: Screen }) {
  return <section className="space-y-4" aria-label={`${screen.title} bản thiết kế`}><div className="flex flex-wrap items-start justify-between gap-4 rounded-2xl border border-outline-variant bg-surface-lowest p-5"><div><p className="text-xs text-on-surface-variant">Thông tin hợp đồng gắn với phòng minh họa</p><h2 className="mt-1 font-display text-2xl font-bold text-on-surface">HD-2026-001</h2><p className="mt-1 text-sm text-on-surface-variant">Phòng A203 · Tòa mẫu A · Bắt đầu 01/03/2026</p></div><StatusBadge tone="success" withDot>Đang hiệu lực · mẫu</StatusBadge></div><div className="grid gap-4 lg:grid-cols-[1.2fr_0.8fr]"><section className="rounded-2xl border border-outline-variant bg-surface-lowest p-5"><h2 className="font-display text-base font-bold text-on-surface">Thông tin hợp đồng</h2><p className="mt-1 text-xs text-on-surface-variant">Dữ liệu minh họa, chưa lấy từ tài khoản thật.</p><dl className="mt-5 grid gap-5 sm:grid-cols-2">{[['Người thuê chính','Khách thuê minh họa'],['Mã phòng','A203 · Tầng 2'],['Ngày bắt đầu','01/03/2026'],['Ngày hết hạn','28/02/2027'],['Tiền thuê hằng tháng','Số tiền minh họa'],['Tiền cọc hợp đồng','Số tiền minh họa']].map(([label,value]) => <div key={label}><dt className="text-xs text-on-surface-variant">{label}</dt><dd className="mt-1 font-semibold text-on-surface">{value}</dd></div>)}</dl></section><section className="rounded-2xl border border-outline-variant bg-surface-lowest p-5"><h2 className="font-display text-base font-bold text-on-surface">Hiệu lực & thời hạn</h2><StatusBadge tone="success" withDot>Đang hiệu lực · mẫu</StatusBadge><p className="mt-5 text-xs text-on-surface-variant">Hợp đồng còn hiệu lực đến</p><p className="mt-1 text-2xl font-bold text-on-surface">28/02/2027 · mẫu</p><InfoCallout title="Không kết thúc trực tiếp:">Hợp đồng chỉ đóng qua quy trình trả phòng.</InfoCallout><button type="button" disabled className="mt-4 w-full rounded-lg border border-outline-variant px-3 py-2 text-xs font-semibold text-on-surface-variant opacity-60">Xem quy trình trả phòng · chưa mở</button></section></div><section className="rounded-2xl border border-outline-variant bg-surface-lowest p-5"><h2 className="font-display text-base font-bold text-on-surface">Các mốc hợp đồng</h2><div className="mt-6 grid grid-cols-4 gap-2">{['01/03/2026 · Bắt đầu','15/03/2026 · Ghi nhận cọc','18/11/2026 · Đang hiệu lực','28/02/2027 · Hết hạn mẫu'].map((label,index) => <div key={label} className="text-center text-xs text-on-surface-variant"><span className={`mx-auto flex h-8 w-8 items-center justify-center rounded-full ${index === 2 ? 'bg-primary text-white' : 'bg-[#DCEBFA] text-primary'}`}>{index + 1}</span><p className="mt-2">{label}</p></div>)}</div></section></section>;
}

function TenantInvoiceDetailSurface({ screen }: { screen: Screen }) {
  return <section className="space-y-4" aria-label={`${screen.title} bản thiết kế`}><div className="flex flex-wrap items-start justify-between gap-4 rounded-2xl border border-outline-variant bg-surface-lowest p-5"><div><p className="text-xs text-on-surface-variant">Hóa đơn kỳ 11/2026 · Chi tiết minh họa</p><h2 className="mt-1 font-display text-2xl font-bold text-on-surface">INV-DEMO-021</h2><p className="mt-1 text-sm text-on-surface-variant">Phòng A203 · Hạn mẫu 25/11/2026</p></div><StatusBadge tone="warning" withDot>Chưa xác minh · mẫu</StatusBadge></div><div className="grid gap-4 lg:grid-cols-[1.2fr_0.8fr]"><section className="rounded-2xl border border-outline-variant bg-surface-lowest p-5"><h2 className="font-display text-base font-bold text-on-surface">Chi tiết các khoản</h2><p className="mt-1 text-xs text-on-surface-variant">Số tiền minh họa, chỉ để đối chiếu bố cục.</p><dl className="mt-4 divide-y divide-outline-variant">{[['Tiền thuê phòng · kỳ mẫu','Số tiền minh họa'],['Tiền điện · chỉ số mẫu','Số tiền minh họa'],['Tiền nước · chỉ số mẫu','Số tiền minh họa'],['Phí dịch vụ · kỳ mẫu','Số tiền minh họa']].map(([label,value]) => <div key={label} className="flex justify-between gap-4 py-4 text-sm"><dt className="text-on-surface-variant">{label}</dt><dd className="font-semibold text-on-surface">{value}</dd></div>)}</dl><div className="mt-4 flex items-center justify-between rounded-xl bg-[#EAF3FF] p-4"><span className="text-xs font-bold text-[#315D8F]">TỔNG MINH HỌA</span><strong className="text-xl text-primary">Số tiền minh họa</strong></div></section><section className="rounded-2xl border border-outline-variant bg-surface-lowest p-5"><h2 className="font-display text-base font-bold text-on-surface">Thanh toán</h2><p className="mt-1 text-xs text-on-surface-variant">Trạng thái chỉ mang tính minh họa.</p><dl className="mt-5 space-y-5"><div><dt className="text-xs text-on-surface-variant">Đã xác minh</dt><dd className="mt-1 text-xl font-bold text-on-surface">Chưa mở</dd></div><div><dt className="text-xs text-on-surface-variant">Còn chờ xử lý</dt><dd className="mt-1 text-xl font-bold text-on-surface">Chưa xác minh</dd></div></dl><button type="button" disabled className="mt-8 w-full rounded-lg bg-primary px-3 py-2.5 text-xs font-semibold text-white opacity-50" title="Payment API chưa mở">Thanh toán trực tuyến · chưa mở</button></section></div><InfoCallout title="Không xác nhận tiền bằng thao tác giao diện:">Trạng thái tiền chỉ đến từ quy trình thu/đối soát được backend cho phép.</InfoCallout></section>;
}

function TenantUtilitiesSurface({ screen, rows }: { screen: Screen; rows: string[][] }) {
  const trend = [['Th.06','55'],['Th.07','70'],['Th.08','62'],['Th.09','81'],['Th.10','68'],['Th.11','72']];
  return <section className="space-y-4" aria-label={`${screen.title} bản thiết kế`}><div className="grid grid-cols-2 gap-3 lg:grid-cols-3"><Metric label="Điện tháng này" value="Chỉ số mẫu" hint="Đã ghi · minh họa" icon="bolt" tone="blue" /><Metric label="Nước tháng này" value="Chỉ số mẫu" hint="Đã ghi · minh họa" icon="water_drop" tone="success" /><Metric label="Kỳ đang xem" value="11/2026" hint="Dữ liệu minh họa" icon="calendar_month" tone="warning" /></div><div className="grid gap-4 lg:grid-cols-[1.2fr_0.8fr]"><section className="rounded-2xl border border-outline-variant bg-surface-lowest p-5"><h2 className="font-display text-base font-bold text-on-surface">Điện năng tiêu thụ</h2><p className="mt-1 text-xs text-on-surface-variant">Xu hướng minh họa · không phải chỉ số thật</p><div className="mt-6 flex h-40 items-end gap-3 border-b border-outline-variant px-2">{trend.map(([label,value]) => <div key={label} className="flex flex-1 flex-col items-center gap-2"><span className="text-[10px] font-semibold text-on-surface-variant">{value}</span><div className="w-full max-w-10 rounded-t-md bg-[#7FB3F5]" style={{ height: `${Number(value) * 1.2}px` }} aria-label={`${label}: ${value} đơn vị minh họa`} /><span className="text-[10px] text-on-surface-variant">{label}</span></div>)}</div><p className="mt-3 text-[10px] text-on-surface-variant">Bảng lịch sử bên dưới là cách đọc chính xác hơn khi API được mở.</p></section><section className="rounded-2xl border border-outline-variant bg-surface-lowest p-5"><h2 className="font-display text-base font-bold text-on-surface">Công tơ điện và nước</h2><p className="mt-1 text-xs text-on-surface-variant">Chỉ số gần nhất · fixture</p>{[['Điện · EV-DEMO-203','Chỉ số cũ → mới · minh họa'],['Nước · WT-DEMO-203','Chỉ số cũ → mới · minh họa']].map(([label,value]) => <div key={label} className="mt-4 rounded-xl bg-[#F5F8FC] p-4"><p className="font-semibold text-on-surface">{label}</p><p className="mt-1 text-xs text-on-surface-variant">{value}</p></div>)}<InfoCallout title="Dữ liệu theo kỳ:">Thay công tơ và điều chỉnh chỉ số phải được server xác minh.</InfoCallout></section></div><section className="rounded-2xl border border-outline-variant bg-surface-lowest p-5"><h2 className="font-display text-base font-bold text-on-surface">Lịch sử chỉ số</h2><p className="mt-1 text-xs text-on-surface-variant">Chỉ là các hàng minh họa của phòng.</p><PreviewTable screen={screen} rows={rows} /></section></section>;
}

function TenantPaymentSurface({ screen }: { screen: Screen }) {
  return <section className="space-y-4" aria-label={`${screen.title} bản thiết kế`}><InfoCallout title="Thanh toán trực tuyến chưa kích hoạt:">Không có QR hoặc tài khoản nhận tiền mặc định; preview không thực hiện tiền thật.</InfoCallout><div className="grid grid-cols-2 gap-3 lg:grid-cols-3"><Metric label="Cần thanh toán" value="Số tiền minh họa" hint="Kỳ 11/2026" icon="payments" tone="warning" /><Metric label="Đã xác nhận" value="Dữ liệu mẫu" hint="Không phải chứng từ" icon="check_circle" tone="success" /><Metric label="Đang rà soát" value="0" hint="Không có giao dịch thực" icon="schedule" tone="blue" /></div><div className="grid gap-4 lg:grid-cols-[1.2fr_0.8fr]"><section className="rounded-2xl border border-outline-variant bg-surface-lowest p-5"><h2 className="font-display text-base font-bold text-on-surface">Lịch sử thanh toán</h2><p className="mt-1 text-xs text-on-surface-variant">Chỉ dữ liệu fixture gắn với hợp đồng minh họa.</p>{[['INV-DEMO-021','Kỳ 11/2026','Chưa xác minh'],['INV-DEMO-020','Kỳ 10/2026','Đã đối soát · minh họa'],['INV-DEMO-019','Kỳ 09/2026','Đã đối soát · minh họa']].map(([id,period,status]) => <div key={id} className="flex flex-wrap items-center justify-between gap-3 border-b border-outline-variant py-4"><div><p className="font-semibold text-primary">{id}</p><p className="mt-1 text-xs text-on-surface-variant">{period}</p></div><div className="text-right"><p className="text-sm font-semibold text-on-surface">Số tiền minh họa</p><StatusBadge tone={/đối soát/i.test(status ?? '') ? 'success' : 'warning'} withDot>{status}</StatusBadge></div></div>)}</section><section className="rounded-2xl border border-outline-variant bg-surface-lowest p-5"><h2 className="font-display text-base font-bold text-on-surface">Quy trình xác minh</h2>{['Xem hóa đơn đã phát hành','Ghi nhận chứng từ · khi API mở','Đối soát ngân hàng · server','Cập nhật sổ chính thức'].map((label,index) => <div key={label} className="flex gap-3 py-3"><span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-[#EAF2FF] text-xs font-bold text-primary">{index + 1}</span><p className="pt-1 text-sm font-semibold text-on-surface">{label}</p></div>)}<button type="button" disabled className="mt-4 w-full rounded-lg border border-outline-variant px-3 py-2.5 text-xs font-semibold text-on-surface-variant opacity-60">Thanh toán trực tuyến · chưa mở</button></section></div></section>;
}

function TenantMoveOutSurface({ screen }: { screen: Screen }) {
  return <section className="space-y-4" aria-label={`${screen.title} bản thiết kế`}><InfoCallout title="Yêu cầu trả phòng chỉ khởi tạo quy trình:">Đóng hợp đồng cần chỉ số cuối, kiểm tra phòng, hóa đơn và tiền cọc được server xử lý.</InfoCallout><div className="grid gap-4 lg:grid-cols-[1.1fr_0.9fr]"><form className="rounded-2xl border border-outline-variant bg-surface-lowest p-5" onSubmit={(event) => event.preventDefault()}><h2 className="font-display text-base font-bold text-on-surface">Tạo yêu cầu trả phòng</h2><p className="mt-1 text-xs text-on-surface-variant">Biểu mẫu mô phỏng · chưa gửi lên API.</p><div className="mt-5 grid gap-4"><label className="text-xs font-semibold text-on-surface-variant">Phòng thuê<input aria-label="Phòng thuê" readOnly value="A203 · Tòa mẫu A" className="mt-1.5 h-10 w-full rounded-lg border border-outline-variant bg-surface-low px-3 text-sm font-normal text-on-surface" /></label><label className="text-xs font-semibold text-on-surface-variant">Ngày dự kiến trả phòng<input aria-label="Ngày dự kiến trả phòng" type="date" defaultValue="2026-11-30" className="mt-1.5 h-10 w-full rounded-lg border border-outline-variant bg-surface-low px-3 text-sm font-normal text-on-surface" /></label><label className="text-xs font-semibold text-on-surface-variant">Lý do trả phòng<select aria-label="Lý do trả phòng" defaultValue="" className="mt-1.5 h-10 w-full rounded-lg border border-outline-variant bg-surface-low px-3 text-sm font-normal text-on-surface"><option value="">Chọn lý do minh họa</option><option>Hết thời hạn thuê</option><option>Chuyển chỗ ở</option></select></label><label className="text-xs font-semibold text-on-surface-variant">Ghi chú thêm<textarea aria-label="Ghi chú thêm" rows={4} placeholder="Thông tin cần bàn giao · minh họa" className="mt-1.5 w-full rounded-lg border border-outline-variant bg-surface-low px-3 py-2 text-sm font-normal text-on-surface" /></label></div><div className="mt-4 rounded-lg bg-[#FFF7E8] p-3 text-xs text-[#A06405]">Lưu nháp giao diện, chưa có thao tác ghi thật.</div><button type="submit" disabled className="mt-4 rounded-lg bg-primary px-4 py-2.5 text-xs font-semibold text-white opacity-50">Gửi yêu cầu · chưa mở</button></form><section className="rounded-2xl border border-outline-variant bg-surface-lowest p-5"><div className="flex items-start justify-between gap-3"><div><h2 className="font-display text-base font-bold text-on-surface">Tiến trình xử lý</h2><p className="mt-1 text-xs text-on-surface-variant">Ví dụ một yêu cầu mẫu</p></div><StatusBadge tone="warning" withDot>Chờ bổ sung · mẫu</StatusBadge></div>{['Tiếp nhận yêu cầu · đã ghi nhận','Duyệt và lên lịch · đã duyệt','Chốt chỉ số cuối · đang chuẩn bị','Kiểm tra phòng · chưa thực hiện','Công nợ và tiền cọc · chưa thực hiện','Đóng quy trình · chưa thực hiện'].map((label,index) => <div key={label} className="flex gap-3 py-3"><span className={`flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-xs font-bold ${index < 2 ? 'bg-[#E3F8EE] text-[#079356]' : index === 2 ? 'bg-[#EAF2FF] text-primary' : 'bg-surface-container text-on-surface-variant'}`}>{index < 2 ? '✓' : index + 1}</span><p className="pt-1 text-sm font-semibold text-on-surface">{label}</p></div>)}</section></div></section>;
}

function TenantBookingSurface({ screen, rows, query, onQueryChange }: { screen: Screen; rows: string[][]; query: string; onQueryChange: (value: string) => void }) {
  return <section className="space-y-4" aria-label={`${screen.title} bản thiết kế`}><div className="flex flex-wrap items-center justify-between gap-4 rounded-2xl border border-[#D5E5FB] bg-[#EAF3FF] p-5"><div><p className="text-[11px] font-bold tracking-widest text-primary uppercase">Khám phá</p><h2 className="mt-1 font-display text-xl font-bold text-on-surface">Khám phá phòng Ngọc Trang</h2><p className="mt-1 text-sm text-on-surface-variant">Giá và trạng thái cần được xác minh lại khi đăng ký.</p></div><button type="button" disabled className="rounded-lg bg-primary px-8 py-2.5 text-xs font-semibold text-white opacity-50">Tìm phòng · chưa mở</button></div><section className="rounded-2xl border border-outline-variant bg-surface-lowest p-5"><h2 className="font-display text-base font-bold text-on-surface">Phòng phù hợp với bạn</h2><p className="mt-1 text-xs text-on-surface-variant">Các phòng dưới đây là mẫu giao diện.</p><input type="search" aria-label="Tìm trong bản thiết kế" value={query} onChange={(event) => onQueryChange(event.target.value)} placeholder="Tìm tòa nhà, mã phòng, tiện ích…" className="mt-4 h-10 w-full rounded-lg border border-outline-variant bg-surface-low px-3 text-sm text-on-surface" /><div className="mt-4 grid gap-4 lg:grid-cols-3">{rows.map((row) => <article key={row[0]} className="rounded-xl border border-outline-variant p-3"><div className="flex h-32 items-center justify-center rounded-lg bg-[#EAF3FF]"><div className="h-16 w-32 rounded-lg border-2 border-[#A9CFF6] bg-white" /></div><div className="mt-3 flex items-center justify-between gap-2"><h3 className="font-display text-lg font-bold text-on-surface">Phòng {row[0]}</h3><StatusBadge tone={/trống/i.test(row[4] ?? '') ? 'success' : 'warning'} withDot>{row[4]}</StatusBadge></div><p className="mt-1 text-xs text-on-surface-variant">{row[1]} · {row[2]}</p><p className="mt-4 font-bold text-primary">{row[3]}</p><button type="button" disabled className="mt-4 w-full rounded-lg border border-outline-variant px-3 py-2 text-xs font-semibold text-on-surface-variant opacity-60">Xem thông tin · chưa mở</button></article>)}</div><p className="mt-5 text-xs text-on-surface-variant">Tạo đặt phòng/cọc giữ chỗ chỉ dùng được sau khi backend xác thực điều kiện và quyền thao tác.</p></section></section>;
}

function TenantProfileSupportSurface({ screen }: { screen: Screen }) {
  return <section className="space-y-4" aria-label={`${screen.title} bản thiết kế`}><section className="flex flex-wrap items-center gap-4 rounded-2xl border border-outline-variant bg-surface-lowest p-5"><span className="flex h-20 w-20 items-center justify-center rounded-full bg-[#EAF2FF] text-2xl font-bold text-[#152640]">DM</span><div className="flex-1"><h2 className="font-display text-2xl font-bold text-on-surface">Khách thuê minh họa</h2><p className="mt-1 text-sm text-on-surface-variant">Khách thuê · Phòng A203 · Tòa mẫu A</p></div><StatusBadge tone="success" withDot>Đang thuê · mẫu</StatusBadge></section><div className="grid gap-4 lg:grid-cols-2"><section className="rounded-2xl border border-outline-variant bg-surface-lowest p-5"><h2 className="font-display text-base font-bold text-on-surface">Thông tin tài khoản</h2><p className="mt-1 text-xs text-on-surface-variant">Dữ liệu minh họa, không phải hồ sơ thực.</p><dl className="mt-4 divide-y divide-outline-variant">{[['Họ tên','Khách thuê minh họa'],['Email','tenant.demo@example.test'],['Số điện thoại','09•• ••• ••12'],['Phòng liên kết','A203 · Tòa mẫu A'],['Trạng thái xác minh','Chỉ hiển thị khi backend cho phép']].map(([label,value]) => <div key={label} className="py-3"><dt className="text-xs text-on-surface-variant">{label}</dt><dd className="mt-1 font-semibold text-on-surface">{value}</dd></div>)}</dl></section><section className="rounded-2xl border border-outline-variant bg-surface-lowest p-5"><h2 className="font-display text-base font-bold text-on-surface">Trung tâm hỗ trợ</h2><p className="mt-1 text-xs text-on-surface-variant">Chưa có ticket/support API tương ứng.</p>{[['Sự cố thiết bị','Điện, nước, điều hòa và vật dụng'],['Câu hỏi thanh toán','Thắc mắc hóa đơn, chứng từ, cọc'],['Thông tin hợp đồng','Ngày thuê, nội quy, gia hạn'],['Liên hệ ban quản lý','Chờ cấu hình kênh hỗ trợ chính thức']].map(([label,description]) => <div key={label} className="mt-3 rounded-xl bg-[#F5F8FC] p-4"><p className="font-semibold text-on-surface">{label}</p><p className="mt-1 text-xs text-on-surface-variant">{description}</p></div>)}<button type="button" disabled className="mt-4 w-full rounded-lg border border-outline-variant px-3 py-2.5 text-xs font-semibold text-on-surface-variant opacity-60">Gửi yêu cầu hỗ trợ · chưa mở</button></section></div></section>;
}

function PreviewTable({ screen, rows }: { screen: Screen; rows: string[][] }) {
  const statusIndex = ['07-thu-chi', '08-dat-phong', '11-dien-nuoc'].includes(screen.id)
    ? 6
    : ['09-phan-quyen', 'TEN-06', 'TEN-09'].includes(screen.id)
      ? screen.id === 'TEN-06' ? 4 : 4
      : ['TEN-04'].includes(screen.id)
        ? 3
        : 2;
  const toneFor = (value: string): 'success' | 'warning' | 'error' | 'neutral' => {
    if (/đang|đã ghi|đang hiệu lực|đối soát|phòng trống/i.test(value)) return 'success';
    if (/chờ|sắp|chưa ghi|giữ chỗ/i.test(value)) return 'warning';
    if (/hết|cần chứng từ|thu hồi|kiểm tra/i.test(value)) return 'error';
    return 'neutral';
  };
  const valueFor = (row: string[], index: number) => row[index] ?? 'Dữ liệu minh họa';

  return <>
    <div className="mt-4 space-y-3 lg:hidden">{rows.length === 0 ? <EmptyState icon="search_off" title="Không có hàng phù hợp" description="Thử đổi từ khóa local." /> : rows.map((row, rowIndex) => <article key={`${row[0]}-${rowIndex}`} className="rounded-xl border border-outline-variant p-4"><div className="flex items-start justify-between gap-3"><div><p className="font-semibold text-on-surface">{valueFor(row, 0)}</p><p className="mt-1 text-xs text-on-surface-variant">{valueFor(row, 1)} · Dữ liệu minh họa</p></div><StatusBadge tone={toneFor(valueFor(row, statusIndex))} withDot>{valueFor(row, statusIndex)}</StatusBadge></div><dl className="mt-3 grid grid-cols-2 gap-2 text-xs">{screen.facts.slice(1, 5).map((fact, index) => <div key={fact}><dt className="text-on-surface-variant">{fact}</dt><dd className="mt-0.5 font-semibold text-on-surface">{valueFor(row, index + 1)}</dd></div>)}</dl></article>)}</div>
    <div className="mt-4 hidden overflow-x-auto lg:block"><table className="w-full min-w-[840px] border-collapse text-sm"><thead><tr className="bg-surface-low text-left text-xs font-semibold text-on-surface-variant">{screen.facts.map((fact) => <th key={fact} className="whitespace-nowrap px-3 py-3">{fact}</th>)}</tr></thead><tbody>{rows.map((row, rowIndex) => <tr key={`${row[0]}-${rowIndex}`} className="border-t border-outline-variant">{screen.facts.map((fact, index) => <td key={fact} className={`px-3 py-3 ${index === 0 ? 'font-semibold text-on-surface' : 'text-on-surface-variant'}`}>{index === statusIndex ? <StatusBadge tone={toneFor(valueFor(row, index))} withDot>{valueFor(row, index)}</StatusBadge> : index === screen.facts.length - 1 ? <button type="button" disabled className="font-semibold text-primary opacity-60" title="Chưa có API production">{valueFor(row, index)}</button> : valueFor(row, index)}</td>)}</tr>)}</tbody></table></div>
    <p className="mt-4 text-xs text-on-surface-variant">{rows.length} hàng · Không phải dữ liệu production · Không có thao tác ghi</p>
  </>;
}

function Dashboard({ screen }: { screen: Screen }) {
  return <div className="mt-4 grid gap-4 lg:grid-cols-[1.2fr_0.8fr]"><div className="rounded-xl bg-[#F5F8FC] p-5"><p className="text-xs text-on-surface-variant">Xin chào, khách thuê minh họa</p><h3 className="mt-2 font-display text-xl font-bold text-on-surface">Tổng quan thuê phòng</h3><div className="mt-5 grid grid-cols-2 gap-3">{screen.facts.map((fact, i) => <div key={fact} className="rounded-xl bg-surface-lowest p-3"><p className="text-xs text-on-surface-variant">{fact}</p><p className="mt-2 font-bold text-on-surface">{i === 0 ? 'A203' : 'Dữ liệu mẫu'}</p></div>)}</div></div><div className="rounded-xl border border-outline-variant p-5"><h3 className="font-semibold text-on-surface">Cần chú ý</h3><p className="mt-2 text-sm leading-relaxed text-on-surface-variant">Thông báo, hóa đơn và trạng thái hợp đồng chỉ là các vùng bố cục minh họa. Chưa có tenant read model được gọi.</p><button type="button" disabled className="mt-5 w-full rounded-lg bg-primary px-3 py-2 text-xs font-semibold text-white opacity-60" title="Chưa có API production">Xem tất cả · chưa mở</button></div></div>;
}

function DetailSurface({ screen }: { screen: Screen }) {
  return <div className="mt-4 grid gap-4 lg:grid-cols-[1.1fr_0.9fr]"><div className="rounded-xl bg-[#F5F8FC] p-5"><div className="flex items-start justify-between gap-3"><div><p className="text-xs text-on-surface-variant">Mã minh họa · DEMO-203</p><h3 className="mt-1 font-display text-xl font-bold text-on-surface">{screen.title}</h3></div><StatusBadge tone="info">Minh họa</StatusBadge></div><dl className="mt-5 grid gap-4 sm:grid-cols-2">{screen.facts.map((fact, index) => <div key={fact}><dt className="text-xs text-on-surface-variant">{fact}</dt><dd className="mt-1 font-semibold text-on-surface">{index === 0 ? 'Tòa mẫu A · A203' : 'Chưa tích hợp API'}</dd></div>)}</dl></div><div className="rounded-xl border border-outline-variant p-5"><h3 className="font-semibold text-on-surface">Nguồn và quyền</h3><p className="mt-2 text-sm leading-relaxed text-on-surface-variant">Fixture UI cô lập. Không dùng dữ liệu này để suy ra quyền tenant, trạng thái hợp đồng, hóa đơn phải trả hoặc chứng từ thanh toán.</p><button type="button" disabled className="mt-5 rounded-lg border border-outline-variant px-3 py-2 text-xs font-semibold text-on-surface-variant opacity-60" title="Backend chưa mở thao tác">Thao tác · chưa mở</button></div></div>;
}

function FormSurface({ screen }: { screen: Screen }) {
  return <div className="mt-4 grid gap-4 lg:grid-cols-[1fr_0.75fr]"><form className="rounded-xl bg-[#F5F8FC] p-5" onSubmit={(event) => event.preventDefault()}><div className="grid gap-4 sm:grid-cols-2">{screen.facts.map((fact) => <label key={fact} className="text-xs font-semibold text-on-surface-variant">{fact}<input aria-label={fact} defaultValue="Dữ liệu minh họa" className="mt-1.5 h-10 w-full rounded-lg border border-outline-variant bg-surface-lowest px-3 text-sm font-normal text-on-surface" /></label>)}</div><div className="mt-5 flex flex-wrap gap-2"><button type="submit" disabled className="rounded-lg bg-primary px-4 py-2 text-xs font-semibold text-white opacity-60" title="Chưa có API production">Lưu · chưa mở</button><span className="self-center text-xs text-on-surface-variant">Nhập liệu chỉ sống trong preview, không submit.</span></div></form><div className="rounded-xl border border-[#FDE68A] bg-[#FFFBEB] p-5"><h3 className="font-semibold text-[#92400E]">Boundary an toàn</h3><p className="mt-2 text-sm leading-relaxed text-[#92400E]">Không ghi chỉ số, không tạo reservation, không xác nhận thanh toán và không mở closeout từ màn thiết kế này.</p></div></div>;
}

function InfoCallout({ title, children }: { title: string; children: string }) {
  return <div className="flex items-start gap-3 rounded-xl border border-[#D5E5FB] bg-[#EDF5FF] px-4 py-3 text-xs text-[#315D8F]"><span className="material-symbols-outlined text-base text-[#2264C7]">info</span><p><strong className="text-[#173E70]">{title}</strong> {children}</p></div>;
}

function Metric({ label, value, hint, icon, tone }: MetricFixture) {
  const colors = { blue: 'bg-[#EAF2FF] text-[#2264C7]', success: 'bg-[#E8F8F0] text-[#079356]', warning: 'bg-[#FFF5E5] text-[#B97710]', error: 'bg-[#FDECEF] text-[#D83C51]' };
  return <article className="flex min-h-[104px] items-center gap-3 rounded-xl border border-outline-variant bg-surface-lowest p-4"><span className={`flex h-11 w-11 shrink-0 items-center justify-center rounded-xl ${colors[tone]}`}><span className="material-symbols-outlined">{icon}</span></span><div className="min-w-0"><p className="truncate text-xs text-on-surface-variant">{label}</p><p className="mt-1 text-xl font-bold text-on-surface">{value}</p><p className="truncate text-[10px] text-on-surface-variant">{hint}</p></div></article>;
}

function StatePanel({ icon, text }: { icon: string; text: string }) {
  return <div className="flex items-center gap-2.5 rounded-xl border border-outline-variant bg-surface-lowest px-5 py-10 text-sm text-on-surface-variant"><span className="material-symbols-outlined animate-spin text-lg">{icon}</span>{text}</div>;
}

function getState(value: string | null): PreviewState {
  return value && STATES.includes(value as PreviewState) ? value as PreviewState : 'data';
}
