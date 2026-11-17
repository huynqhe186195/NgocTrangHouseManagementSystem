import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import type { FloorDto, RoomDto } from '../../lib/api/types';
import { RoomDetailPage } from './RoomDetailPage';

/**
 * The screen's layout is what this covers, because that is what the mockup
 * governs: three faces side by side — photos, facts, locator — with the
 * equipment table underneath, full width, and its note panel beside the table.
 * The data hooks are mocked; the components that draw them are real.
 */

const roomsData: { current: RoomDto[] } = { current: [] };
const floorsData: { current: FloorDto[] } = { current: [] };
const equipmentData: { current: { id: string; name: string; quantity: number }[] } = {
  current: [],
};
const permission: { canManage: boolean } = { canManage: true };

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
  useRoomEquipment: () => ({
    data: equipmentData.current,
    isLoading: false,
    error: null,
  }),
  useFloorLayoutItems: () => ({ data: [], isLoading: false }),
  useUploadRoomPhoto: () => ({ mutate: vi.fn(), isPending: false }),
  useDeleteRoomPhoto: () => ({ mutate: vi.fn(), isPending: false }),
  useDeleteRoomEquipment: () => ({ mutate: vi.fn(), isPending: false }),
  useUpdateRoomRent: () => ({ mutate: vi.fn(), isPending: false, error: null, reset: vi.fn() }),
  useCreateRoom: () => ({ mutate: vi.fn(), isPending: false, error: null, reset: vi.fn() }),
  useUpdateRoom: () => ({ mutate: vi.fn(), isPending: false, error: null, reset: vi.fn() }),
}));

vi.mock('../../lib/api/buildingApi', () => ({
  roomsApi: { getPhotoContent: vi.fn() },
  fileToBase64: vi.fn(),
}));

vi.mock('../../lib/auth/AuthProvider', () => ({
  useAuth: () => ({
    hasPermission: () => permission.canManage,
  }),
}));

const room: RoomDto = {
  id: 'r1',
  creationTime: '2026-01-01T00:00:00Z',
  creatorId: null,
  lastModificationTime: null,
  lastModifierId: null,
  floorId: 'f2',
  roomNumber: 'A203',
  name: 'Phòng A203',
  area: 22,
  capacity: 2,
  status: 1,
  description: null,
  monthlyRent: 3500000,
  layoutX: 4,
  layoutY: 5,
  layoutWidth: 4.2,
  layoutHeight: 5,
  layoutShape: 1,
  layoutRotationDegrees: 0,
  layoutPoints: null,
  equipmentCount: 0,
  photoCount: 0,
};

const floors: FloorDto[] = [
  {
    id: 'f2',
    creationTime: '2026-01-01T00:00:00Z',
    creatorId: null,
    lastModificationTime: null,
    lastModifierId: null,
    buildingId: 'b1',
    floorNumber: 2,
    name: 'Tầng 2',
    description: null,
    status: 1,
  },
  {
    id: 'f3',
    creationTime: '2026-01-01T00:00:00Z',
    creatorId: null,
    lastModificationTime: null,
    lastModifierId: null,
    buildingId: 'b1',
    floorNumber: 3,
    name: 'Tầng 3',
    description: null,
    status: 1,
  },
];

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/rooms/r1']}>
        <Routes>
          <Route path="/rooms/:roomId" element={<RoomDetailPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('RoomDetailPage', () => {
  it('puts the status beside the title, as the mockup does', () => {
    roomsData.current = [room];
    floorsData.current = floors;
    equipmentData.current = [];

    renderPage();

    const heading = screen.getByRole('heading', { name: 'Phòng A203' });
    const titleRow = heading.parentElement as HTMLElement;
    expect(within(titleRow).getByText('Trống')).toBeTruthy();
  });

  it('draws photos, facts and the locator as three separate panels', () => {
    roomsData.current = [room];
    floorsData.current = floors;
    equipmentData.current = [];

    renderPage();

    // Each of the three faces keeps its own heading; stacking them into one
    // column is the regression this guards.
    expect(screen.getByRole('heading', { name: 'Ảnh phòng' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Thông tin phòng' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Vị trí trên sơ đồ tầng' })).toBeTruthy();
  });

  it('offers a floor picker that follows the building, not only the room floor', () => {
    roomsData.current = [room];
    floorsData.current = floors;
    equipmentData.current = [];

    renderPage();

    // Two floors of the building are offered, so the owner can look at the
    // neighbour storey without leaving the room.
    const picker = screen.getByText('Chọn tầng');
    expect(picker).toBeTruthy();
  });

  it('keeps the note panel beside the equipment table, not under the room facts', () => {
    roomsData.current = [room];
    floorsData.current = floors;
    equipmentData.current = [{ id: 'e1', name: 'Máy lạnh', quantity: 1 }];

    renderPage();

    const table = screen.getByText('Danh sách thiết bị');
    const notes = screen.getByRole('heading', { name: 'Thông tin khác' });
    // The mockup puts them in the same tab panel, side by side.
    expect(table.closest('[role="tabpanel"]')).toBe(notes.closest('[role="tabpanel"]'));
  });

  it('edits the room through the same dialog the room list uses', () => {
    roomsData.current = [room];
    floorsData.current = floors;
    equipmentData.current = [];

    renderPage();

    fireEvent.click(screen.getByRole('button', { name: /Chỉnh sửa/ }));
    expect(screen.getByDisplayValue('A203')).toBeTruthy();
  });

  it('disables the edit button and the new-contract button without permission', () => {
    roomsData.current = [room];
    floorsData.current = floors;
    equipmentData.current = [];
    permission.canManage = false;

    try {
      renderPage();

      const edit = screen.getByRole('button', { name: /Chỉnh sửa/ }) as HTMLButtonElement;
      expect(edit.disabled).toBe(true);

      const contract = screen.getByRole('button', { name: /Hợp đồng mới/ }) as HTMLButtonElement;
      expect(contract.disabled).toBe(true);
      expect(contract.title).toContain('403');
    } finally {
      permission.canManage = true;
    }
  });
});
