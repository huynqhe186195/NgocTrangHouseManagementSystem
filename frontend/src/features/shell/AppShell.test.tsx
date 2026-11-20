import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, within, fireEvent, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { AppShell } from './AppShell';
import type { CurrentUser } from '../../lib/auth/AuthProvider';
import type { BuildingDto, FloorDto, RoomDto } from '../../lib/api/types';
/**
 * The mockup draws a different sidebar per role, a hamburger that really
 * collapses the rail, a search box that has to answer, and a bell that may
 * only show a count when the data really holds something to count. Those are
 * the claims under test here — the data screens themselves are covered by
 * their own files.
 */

let user: CurrentUser | null;
let permissions: string[];
let buildings: { current: BuildingDto[] };
let floors: { current: FloorDto[] };
let rooms: { current: RoomDto[] };
let listFails: { current: boolean };
let listLoading: { current: boolean };

vi.mock('../../lib/auth/AuthProvider', () => ({
  useAuth: () => ({
    user,
    signOut: vi.fn(),
    hasPermission: (policy: string) => permissions.includes(policy),
  }),
}));

vi.mock('../../lib/api/buildingQueries', () => ({
  useBuildings: () => ({
    data: listFails.current ? undefined : buildings.current,
    isLoading: listLoading.current,
    error: listFails.current ? new Error('denied') : null,
  }),
  useFloors: () => ({
    data: listFails.current ? undefined : floors.current,
    isLoading: listLoading.current,
    error: null,
  }),
  useRooms: () => ({
    data: listFails.current ? undefined : rooms.current,
    isLoading: listLoading.current,
    error: null,
  }),
}));

function renderShell() {
  return render(
    <MemoryRouter initialEntries={['/buildings']}>
      <Routes>
        <Route element={<AppShell />}>
          <Route path="/buildings" element={<p>danh sách tòa nhà</p>} />
          <Route path="/rooms/:roomId" element={<p>chi tiết phòng</p>} />
        </Route>
      </Routes>
    </MemoryRouter>,
  );
}

function navLabels(): string[] {
  const nav = screen.getByRole('navigation');
  return within(nav)
    .getAllByRole('listitem')
    .map((node) => node.textContent ?? '');
}

const building: BuildingDto = {
  id: 'b1',
  creationTime: '2026-01-01T00:00:00Z',
  creatorId: null,
  lastModificationTime: null,
  lastModifierId: null,
  name: 'Ngọc Trang Building',
  address: '123 Đường ABC',
  description: null,
  hasPhoto: false,
};

const floor: FloorDto = {
  id: 'f1',
  creationTime: '2026-01-01T00:00:00Z',
  creatorId: null,
  lastModificationTime: null,
  lastModifierId: null,
  buildingId: 'b1',
  floorNumber: 1,
  name: 'Tầng 1',
  description: null,
  status: 1,
};

function room(overrides: Partial<RoomDto> = {}): RoomDto {
  return {
    id: 'r1',
    creationTime: '2026-01-01T00:00:00Z',
    creatorId: null,
    lastModificationTime: null,
    lastModifierId: null,
    floorId: 'f1',
    roomNumber: '101',
    name: 'Phòng 101',
    area: 24,
    capacity: 2,
    status: 1,
    description: null,
    roomType: 'Phòng tiêu chuẩn',
    locationNote: null,
    direction: null,
    monthlyRent: null,
    layoutX: null,
    layoutY: null,
    layoutWidth: null,
    layoutHeight: null,
    layoutShape: null,
    layoutRotationDegrees: null,
    layoutPoints: null,
    equipmentCount: 0,
    photoCount: 0,
    ...overrides,
  };
}

beforeEach(() => {
  // jsdom reports every media query as unmatched, which would put the shell in
  // its phone mode; the rail is the desktop behaviour under test here.
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query.includes('min-width'),
    media: query,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  }));

  user = {
    id: 'u1',
    userName: 'owner',
    name: 'Chủ nhà',
    email: 'owner@example.com',
    roles: ['Owner'],
  } as CurrentUser;
  permissions = [];
  buildings = { current: [building] };
  floors = { current: [floor] };
  rooms = { current: [room()] };
  listFails = { current: false };
  listLoading = { current: false };
});

describe('AppShell', () => {
  it('shows the administration sidebar to a user who manages buildings', () => {
    permissions = ['BuildingManagement.Buildings.Manage'];
    renderShell();

    expect(screen.getByText('Quản trị viên')).toBeTruthy();
    const labels = navLabels();
    for (const expected of ['Tổng quan', 'Tòa nhà', 'Phòng', 'Khách thuê', 'Hợp đồng']) {
      expect(labels.some((label) => label.includes(expected))).toBe(true);
    }
    expect(labels.join(' ')).not.toContain('Tìm phòng');
  });

  it('shows the tenant sidebar to a user with no management grant', () => {
    permissions = [];
    renderShell();

    expect(screen.getByText('Người thuê')).toBeTruthy();
    const labels = navLabels();
    for (const expected of ['Trang chủ', 'Tìm phòng', 'Tòa nhà', 'Tiện ích', 'Thanh toán']) {
      expect(labels.some((label) => label.includes(expected))).toBe(true);
    }
    expect(labels.join(' ')).not.toContain('Thu chi');
  });

  it('renders unbuilt modules as inert rows, never as links', () => {
    permissions = ['BuildingManagement.Buildings.Manage'];
    renderShell();

    const nav = screen.getByRole('navigation');
    const invoices = within(nav).getByText('Hóa đơn').closest('li') as HTMLElement;

    expect(within(invoices).queryByRole('link')).toBeNull();
    expect(invoices.textContent).toContain('Hóa đơn');
  });

  it('hides the users link when the membership permission is absent', () => {
    permissions = ['BuildingManagement.Buildings.Manage'];
    renderShell();

    expect(within(screen.getByRole('navigation')).queryByText('Người dùng')).toBeNull();
  });

  it('shows the users read-only link when the server grants membership permission', () => {
    permissions = ['BuildingManagement.Buildings.ReadOperators'];
    renderShell();

    const users = within(screen.getByRole('navigation')).getByText('Người dùng').closest('li') as HTMLElement;
    expect(within(users).getByRole('link').getAttribute('href')).toBe('/users');
  });

  it('opens the contract list from the admin sidebar', () => {
    permissions = ['BuildingManagement.Buildings.Manage'];
    renderShell();

    const nav = screen.getByRole('navigation');
    const item = within(nav).getByText('Hợp đồng').closest('li') as HTMLElement;
    const link = within(item).getByRole('link');

    expect(link.textContent).toContain('Hợp đồng');
    expect(link.getAttribute('href')).toBe('/contracts');
  });

  it('puts an account that may only read contracts in the admin shell', () => {
    permissions = ['BuildingManagement.Contracts.Read'];
    renderShell();

    expect(screen.getByText('Quản trị viên')).toBeTruthy();
    expect(navLabels().some((label) => label.includes('Hợp đồng'))).toBe(true);
  });

  it('opens the tenant roster from the admin sidebar', () => {
    permissions = ['BuildingManagement.Buildings.Manage'];
    renderShell();

    const nav = screen.getByRole('navigation');
    const item = within(nav).getByText('Khách thuê').closest('li') as HTMLElement;
    const link = within(item).getByRole('link');

    expect(link.textContent).toContain('Khách thuê');
    expect(link.getAttribute('href')).toBe('/tenants');
  });

  it('puts an account that may only read tenants in the admin shell', () => {
    permissions = ['BuildingManagement.Tenants.Read'];
    renderShell();

    expect(screen.getByText('Quản trị viên')).toBeTruthy();
    expect(navLabels().some((label) => label.includes('Khách thuê'))).toBe(true);
  });

  it('collapses and expands the sidebar with the hamburger', () => {
    permissions = ['BuildingManagement.Buildings.Manage'];
    renderShell();

    const toggle = screen.getByRole('button', { name: 'Thu gọn menu' });
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(document.querySelector('aside')?.className).toContain('lg:w-[238px]');

    fireEvent.click(toggle);

    expect(screen.getByRole('button', { name: 'Mở menu' }).getAttribute('aria-expanded')).toBe(
      'false',
    );
    // Rail mode: the sidebar keeps its icons but shrinks to a narrow strip.
    expect(document.querySelector('aside')?.className).toContain('lg:w-[76px]');

    fireEvent.click(screen.getByRole('button', { name: 'Mở menu' }));
    expect(document.querySelector('aside')?.className).toContain('lg:w-[238px]');
  });

  it('searches the loaded lists and opens the room it offers', async () => {
    renderShell();

    const search = screen.getByRole('combobox', { name: 'Tìm kiếm tòa nhà, tầng, phòng' });
    expect((search as HTMLInputElement).disabled).toBe(false);

    fireEvent.change(search, { target: { value: '101' } });

    const options = await screen.findAllByRole('option');
    expect(options).toHaveLength(1);
    expect(options[0]!.textContent).toContain('Phòng 101');
    expect(options[0]!.textContent).toContain('Tầng 1');

    fireEvent.click(options[0]!);

    await waitFor(() => expect(screen.getByText('chi tiết phòng')).toBeTruthy());
  });

  it('says nothing matched rather than showing an empty box', () => {
    renderShell();

    fireEvent.change(screen.getByRole('combobox'), { target: { value: 'khong co gi' } });

    expect(screen.getByText(/Không có tòa nhà, tầng hay phòng nào khớp/)).toBeTruthy();
    expect(screen.queryAllByRole('option')).toHaveLength(0);
  });

  it('explains a refused list instead of pretending the search found nothing', () => {
    listFails.current = true;
    renderShell();

    fireEvent.change(screen.getByRole('combobox'), { target: { value: '101' } });

    expect(screen.getByText(/Cần quyền đọc tòa nhà/)).toBeTruthy();
  });

  it('moves through the results with the arrow keys and opens on Enter', async () => {
    rooms.current = [room(), room({ id: 'r2', roomNumber: '102', name: 'Phòng 102' })];
    renderShell();

    const search = screen.getByRole('combobox');
    fireEvent.change(search, { target: { value: 'phong' } });

    const options = await screen.findAllByRole('option');
    expect(options).toHaveLength(2);
    expect(options[0]!.getAttribute('aria-selected')).toBe('true');

    fireEvent.keyDown(search, { key: 'ArrowDown' });
    expect(screen.getAllByRole('option')[1]!.getAttribute('aria-selected')).toBe('true');

    fireEvent.keyDown(search, { key: 'Enter' });
    await waitFor(() => expect(screen.getByText('chi tiết phòng')).toBeTruthy());
  });

  it('shows no notification badge when nothing needs attention, and no invented count', () => {
    renderShell();

    const bell = screen.getByRole('button', { name: 'Thông báo' });
    expect(bell.textContent?.trim()).toBe('notifications');
  });

  it('counts what actually needs attention and lists those rows', () => {
    rooms.current = [room({ status: 4 }), room({ id: 'r2', roomNumber: '102', status: 3 })];
    renderShell();

    const bell = screen.getByRole('button', { name: 'Thông báo (2)' });
    expect(bell.textContent).toContain('2');

    fireEvent.click(bell);

    const dialog = screen.getByRole('dialog', { name: 'Thông báo' });
    expect(within(dialog).getByText('Phòng 101 đang bảo trì')).toBeTruthy();
    expect(within(dialog).getByText('Phòng 102 đã được đặt')).toBeTruthy();
    // The count is a count of open items, and the panel says so.
    expect(dialog.textContent).toContain('không phải số thông báo chưa đọc');
  });

  it('opens the row the bell names', async () => {
    rooms.current = [room({ status: 4 })];
    renderShell();

    fireEvent.click(screen.getByRole('button', { name: 'Thông báo (1)' }));
    fireEvent.click(
      within(screen.getByRole('dialog', { name: 'Thông báo' })).getByText(
        'Phòng 101 đang bảo trì',
      ),
    );

    await waitFor(() => expect(screen.getByText('chi tiết phòng')).toBeTruthy());
  });

  it('says the list is empty rather than showing a blank panel', () => {
    renderShell();

    fireEvent.click(screen.getByRole('button', { name: 'Thông báo' }));

    expect(
      screen.getByText('Không có phòng bảo trì, phòng đã đặt hay tầng bảo trì nào.'),
    ).toBeTruthy();
  });
});
