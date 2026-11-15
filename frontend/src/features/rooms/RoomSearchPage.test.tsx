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

function renderPage() {
  // The photo gallery fetches thumbnail bytes through `useQueries`, so a
  // provider is needed even though every data hook here is mocked.
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/search']}>
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

    expect(screen.getByText('Phòng đang trống (1)')).toBeTruthy();
    expect(screen.getByText('Phòng A203')).toBeTruthy();
    expect(screen.queryByText('Phòng A204')).toBeNull();
    expect(screen.queryByText('Phòng A205')).toBeNull();

    const book = screen.getByRole('button', { name: /Đặt phòng ngay/ }) as HTMLButtonElement;
    expect(book.disabled).toBe(true);
    expect(book.title).toBe('Chức năng đặt phòng chưa được mở trên máy chủ.');
  });

  it('recomputes the estimate when the lease length changes', () => {
    floorsData.current = [floor];
    roomsData.current = [room()];

    renderPage();

    // 6 months is the default: 3.500.000 x 6 + 3.500.000 deposit.
    expect(screen.getAllByText(/24\.500\.000/).length).toBeGreaterThan(0);

    fireEvent.change(screen.getByLabelText(/Thời hạn thuê/), { target: { value: '12' } });
    expect(screen.getAllByText(/45\.500\.000/).length).toBeGreaterThan(0);
  });

  it('shows an empty state when every room is taken', () => {
    floorsData.current = [floor];
    roomsData.current = [room({ status: 2 })];

    renderPage();

    expect(screen.getByText('Chưa có phòng trống phù hợp')).toBeTruthy();
  });
});
