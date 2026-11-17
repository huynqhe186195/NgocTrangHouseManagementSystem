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

  it('fills the mockup structure and blanks the fields the server does not have', () => {
    floorsData.current = [floor];
    roomsData.current = [room()];

    renderPage();

    expect(
      screen.getByText('Tòa nhà Ngọc Trang Building · Tầng 2 · Mã phòng: A203'),
    ).toBeTruthy();

    // Direction and room type have no column on the server, so they stay blank
    // instead of being invented.
    expect(screen.getAllByText('Chưa cập nhật')).toHaveLength(2);

    for (const heading of [
      'Thông tin phòng',
      'Vị trí phòng trên mặt bằng',
      'Tiện ích phòng',
      'Quy định nhà trọ',
      'Thông tin khác',
    ]) {
      expect(screen.getByText(heading)).toBeTruthy();
    }

    expect(screen.getByText(/Đơn giá điện, nước và phí dịch vụ chưa được công bố/)).toBeTruthy();
  });

  it('opens the room named in the URL so a shared link lands on it', () => {
    floorsData.current = [floor];
    roomsData.current = [room(), room({ id: 'r2', roomNumber: 'A205', name: 'Phòng A205' })];

    renderPage('/search?room=r2');

    expect(screen.getAllByText('Phòng A205').length).toBeGreaterThan(0);
    expect(screen.getByText('Phòng đang trống khác (1)')).toBeTruthy();
  });
});
