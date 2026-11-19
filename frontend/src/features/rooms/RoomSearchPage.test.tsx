import { describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { FloorDto, RoomDto } from '../../lib/api/types';
import { RoomSearchPage, estimateFirstPayment } from './RoomSearchPage';

/**
 * The screen is read-only — booking is disabled by design — so these tests
 * cover the two things that can actually be wrong: which rooms it lists, and
 * the client-side money estimate.
 */

const roomsData: { current: RoomDto[] } = { current: [] };
const floorsData: { current: FloorDto[] } = { current: [] };

vi.mock('../../lib/api/buildingQueries', () => ({
  useBuildings: () => ({
    data: [
      {
        id: 'b1',
        creationTime: '2026-01-01T00:00:00Z',
        creatorId: null,
        lastModificationTime: null,
        lastModifierId: null,
        name: 'Ngọc Trang Building',
        address: '1 Đường A',
        description: null,
      },
    ],
    isLoading: false,
    error: null,
  }),
  useFloors: () => ({ data: floorsData.current, isLoading: false, error: null }),
  useRooms: () => ({ data: roomsData.current, isLoading: false, error: null }),
  useRoomPhotos: () => ({ data: [], isLoading: false }),
  useRoomEquipment: () => ({ data: [], isLoading: false }),
  useFloorLayoutItems: () => ({ data: [], isLoading: false }),
  useBuildingPublicInfo: () => ({
    data: {
      buildingId: 'b1',
      electricityUnitPrice: 3500,
      waterUnitPrice: 100000,
      serviceFee: 150000,
      otherInfo: null,
      houseRules: [
        { icon: 1, text: 'Không nuôi thú cưng' },
        { icon: 2, text: 'Giờ yên tĩnh 22:00-06:00' },
      ],
    },
    isLoading: false,
    error: null,
  }),
}));

vi.mock('../../lib/api/buildingApi', () => ({
  roomsApi: { getPhotoContent: vi.fn() },
}));

function room(overrides: Partial<RoomDto> = {}): RoomDto {
  return {
    id: 'r1',
    creationTime: '2026-01-01T00:00:00Z',
    creatorId: null,
    lastModificationTime: null,
    lastModifierId: null,
    floorId: 'f1',
    roomNumber: 'A203',
    name: 'Phòng A203',
    area: 22,
    capacity: 2,
    status: 1,
    description: null,
    roomType: 'Phòng tiêu chuẩn',
    locationNote: null,
    direction: 4,
    monthlyRent: 3500000,
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

const floor: FloorDto = {
  id: 'f1',
  creationTime: '2026-01-01T00:00:00Z',
  creatorId: null,
  lastModificationTime: null,
  lastModifierId: null,
  buildingId: 'b1',
  floorNumber: 2,
  name: 'Tầng 2',
  description: null,
  status: 1,
};

function renderPage(entry = '/search') {
  // The photo gallery fetches thumbnail bytes through `useQueries`, so a
  // provider is needed even though every data hook here is mocked.
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[entry]}>
        <RoomSearchPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('estimateFirstPayment', () => {
  it('adds one month of deposit to the lease total', () => {
    expect(estimateFirstPayment(3500000, 6)).toEqual({
      rent: 21000000,
      deposit: 3500000,
      total: 24500000,
    });
  });
});

describe('RoomSearchPage', () => {
  it('lists only available rooms and disables the booking button', () => {
    floorsData.current = [floor];
    roomsData.current = [
      room(),
      room({ id: 'r2', roomNumber: 'A204', status: 2 }),
      room({ id: 'r3', roomNumber: 'A205', status: 4 }),
    ];

    renderPage();

    expect(screen.getByText('Phòng đang trống khác (0)')).toBeTruthy();
    // The room also titles the page now, so it appears more than once.
    expect(screen.getAllByText('Phòng A203').length).toBeGreaterThan(0);
    expect(screen.queryByText('Phòng A204')).toBeNull();
    expect(screen.queryByText('Phòng A205')).toBeNull();

    const book = screen.getByRole('button', { name: /Đặt phòng ngay/ }) as HTMLButtonElement;
    expect(book.disabled).toBe(true);
    expect(book.title).toBe('Chức năng đặt phòng chưa được mở trên máy chủ.');
  });

  it('recomputes the estimate when the lease length changes', async () => {
    floorsData.current = [floor];
    roomsData.current = [room()];

    renderPage();

    // 6 months is the default: 3.500.000 x 6 + 3.500.000 deposit.
    expect(screen.getAllByText(/24\.500\.000/).length).toBeGreaterThan(0);

    // The lease length is an antd Select: open it, then pick the option.
    fireEvent.mouseDown(screen.getByLabelText(/Thời hạn thuê/));
    fireEvent.click(await screen.findByTitle('12 tháng'));
    expect(screen.getAllByText(/45\.500\.000/).length).toBeGreaterThan(0);
  });

  it('shows an empty state when every room is taken', () => {
    floorsData.current = [floor];
    roomsData.current = [room({ status: 2 })];

    renderPage();

    expect(screen.getByText('Chưa có phòng trống phù hợp')).toBeTruthy();
  });

  it('fills the mockup structure from the server payload', () => {
    floorsData.current = [floor];
    roomsData.current = [room({ locationNote: 'Gần cầu thang, view nội khu' })];

    renderPage();

    expect(
      screen.getByText('Tòa nhà Ngọc Trang Building · Tầng 2 · Mã phòng: A203'),
    ).toBeTruthy();

    // Direction and room type now have real columns, so the pane reads them
    // instead of falling back to a blank.
    expect(screen.getAllByText('Đông Nam').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Phòng tiêu chuẩn').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Gần cầu thang, view nội khu').length).toBeGreaterThan(0);
    expect(screen.queryByText('Chưa cập nhật')).toBeNull();

    for (const heading of [
      'Thông tin phòng',
      'Vị trí phòng trên mặt bằng',
      'Tiện ích phòng',
      'Quy định nhà trọ',
      'Thông tin khác',
    ]) {
      expect(screen.getByText(heading)).toBeTruthy();
    }

    // The published prices and rules come from the settings table.
    expect(screen.getByText('3.500 đ/kWh')).toBeTruthy();
    expect(screen.getByText('100.000 đ/người/tháng')).toBeTruthy();
    expect(screen.getByText('150.000 đ/phòng/tháng')).toBeTruthy();
    expect(screen.getByText('Không nuôi thú cưng')).toBeTruthy();
    expect(screen.getByText('Giờ yên tĩnh 22:00-06:00')).toBeTruthy();
  });

  it('opens the room named in the URL so a shared link lands on it', () => {
    floorsData.current = [floor];
    roomsData.current = [room(), room({ id: 'r2', roomNumber: 'A205', name: 'Phòng A205' })];

    renderPage('/search?room=r2');

    expect(screen.getAllByText('Phòng A205').length).toBeGreaterThan(0);
    expect(screen.getByText('Phòng đang trống khác (1)')).toBeTruthy();
  });
});
