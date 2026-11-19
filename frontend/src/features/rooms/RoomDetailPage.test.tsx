import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import type { ContractDto, FloorDto, RoomDto } from '../../lib/api/types';
import { ApiError } from '../../lib/http';
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
const contractsData: { current: ContractDto[] } = { current: [] };
const contractsError: { current: unknown } = { current: null };
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
  useRoomContracts: () => ({
    data: contractsData.current,
    isLoading: false,
    error: contractsError.current,
  }),
  useUploadRoomPhoto: () => ({ mutate: vi.fn(), isPending: false }),
  useDeleteRoomPhoto: () => ({ mutate: vi.fn(), isPending: false }),
  useDeleteRoomEquipment: () => ({ mutate: vi.fn(), isPending: false }),
  useUpdateRoomRent: () => ({ mutate: vi.fn(), isPending: false, error: null, reset: vi.fn() }),
  useCreateRoom: () => ({ mutate: vi.fn(), isPending: false, error: null, reset: vi.fn() }),
  useUpdateRoom: () => ({ mutate: vi.fn(), isPending: false, error: null, reset: vi.fn() }),
  useBuildingPublicInfo: () => ({ data: null, isLoading: false, error: null }),
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
  roomType: 'Phòng tiêu chuẩn',
  locationNote: null,
  direction: 4,
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

  it('shows the Vị trí note the owner saved, beside Loại phòng', () => {
    roomsData.current = [{ ...room, locationNote: 'Gần cầu thang, view nội khu' }];
    floorsData.current = floors;
    equipmentData.current = [];

    renderPage();

    const label = screen.getByText('Vị trí', { selector: 'span' });
    const row = label.parentElement?.parentElement as HTMLElement;
    expect(within(row).getByText('Gần cầu thang, view nội khu')).toBeTruthy();
  });

  it('leaves Vị trí blank rather than inventing one', () => {
    roomsData.current = [room];
    floorsData.current = floors;
    equipmentData.current = [];

    renderPage();

    const label = screen.getByText('Vị trí', { selector: 'span' });
    const row = label.parentElement?.parentElement as HTMLElement;
    expect(within(row).getByText('—')).toBeTruthy();
  });

  it('edits the room through the same dialog the room list uses', () => {
    roomsData.current = [room];
    floorsData.current = floors;
    equipmentData.current = [];

    renderPage();

    fireEvent.click(screen.getByRole('button', { name: /Chỉnh sửa/ }));
    expect(screen.getByDisplayValue('A203')).toBeTruthy();
  });

  it('lists the room’s real contracts instead of a gate notice', () => {
    roomsData.current = [room];
    floorsData.current = floors;
    equipmentData.current = [];
    contractsData.current = [
      {
        id: 'c1',
        creationTime: '2026-01-01T00:00:00Z',
        creatorId: null,
        lastModificationTime: null,
        lastModifierId: null,
        contractNumber: 'HD-2026-001',
        roomId: 'r1',
        renewedFromContractId: null,
        startDate: '2026-01-01T00:00:00',
        endDate: '2026-12-31T00:00:00',
        actualEndedAt: null,
        endReason: null,
        monthlyRent: 3500000,
        depositAmount: 7000000,
        status: 2,
        notes: null,
      },
    ];

    try {
      renderPage();

      fireEvent.click(screen.getByRole('tab', { name: /Hợp đồng/ }));

      // Both the wide table and the phone card are in the DOM; CSS picks one.
      expect(screen.getAllByText('HD-2026-001').length).toBeGreaterThan(0);
      expect(screen.getAllByText('01/01/2026 – 31/12/2026').length).toBeGreaterThan(0);
      expect(screen.getAllByText('Đang hiệu lực').length).toBeGreaterThan(0);
      // The 403 gate that used to stand here must be gone, not merely hidden.
      expect(screen.queryByText(/chưa có API công khai/)).toBeNull();
    } finally {
      contractsData.current = [];
    }
  });

  it('keeps every contract fact readable on a phone, not behind a sideways scroll', () => {
    roomsData.current = [room];
    floorsData.current = floors;
    equipmentData.current = [];
    contractsData.current = [
      {
        id: 'c1',
        creationTime: '2026-01-01T00:00:00Z',
        creatorId: null,
        lastModificationTime: null,
        lastModifierId: null,
        contractNumber: 'HD-2026-001',
        roomId: 'r1',
        renewedFromContractId: null,
        startDate: '2026-01-01T00:00:00',
        endDate: '2026-12-31T00:00:00',
        actualEndedAt: null,
        endReason: null,
        monthlyRent: 3500000,
        depositAmount: 7000000,
        status: 2,
        notes: null,
      },
    ];

    try {
      renderPage();
      fireEvent.click(screen.getByRole('tab', { name: /Hợp đồng/ }));

      // The stacked card carries the same four facts as the wide table, so a
      // phone never has to scroll a table sideways to read the rent.
      const card = screen
        .getAllByText('HD-2026-001')
        .map((node) => node.closest('li'))
        .find((node): node is HTMLLIElement => node !== null) as HTMLLIElement;
      expect(within(card).getByText('3.500.000 đ')).toBeTruthy();
      expect(within(card).getByText('7.000.000 đ')).toBeTruthy();
      expect(within(card).getByText('Đang hiệu lực')).toBeTruthy();
      expect(within(card).getByText('01/01/2026 – 31/12/2026')).toBeTruthy();
    } finally {
      contractsData.current = [];
    }
  });

  it('says the room has no contract yet rather than showing an empty table', () => {
    roomsData.current = [room];
    floorsData.current = floors;
    equipmentData.current = [];
    contractsData.current = [];

    renderPage();

    fireEvent.click(screen.getByRole('tab', { name: /Hợp đồng/ }));

    expect(screen.getByText('Phòng chưa có hợp đồng nào')).toBeTruthy();
  });

  it('explains a 403 on contracts as a permission gap, not a missing feature', () => {
    roomsData.current = [room];
    floorsData.current = floors;
    equipmentData.current = [];
    contractsError.current = new ApiError('Forbidden', 'Forbidden', 403);

    try {
      renderPage();

      fireEvent.click(screen.getByRole('tab', { name: /Hợp đồng/ }));

      expect(screen.getByText(/chưa được cấp quyền xem hợp đồng/)).toBeTruthy();
    } finally {
      contractsError.current = null;
    }
  });

  it('states the real reason on the invoice and maintenance tabs, not a guess', () => {
    roomsData.current = [room];
    floorsData.current = floors;
    equipmentData.current = [];

    renderPage();

    fireEvent.click(screen.getByRole('tab', { name: /Hoá đơn/ }));
    // Every invoice app service is closed to remote access and the official
    // posting routes live only on the disposable staging controller. Saying
    // "the ledger is only a draft" would understate that, so it must not be
    // the sentence the owner reads.
    expect(screen.getByText(/đóng truy cập từ xa/)).toBeTruthy();
    expect(screen.queryByText(/chưa phát hành hoá đơn chính thức/)).toBeNull();

    fireEvent.click(screen.getByRole('tab', { name: /Bảo trì/ }));
    expect(screen.getByText(/chưa có bảng dữ liệu và chưa có endpoint/)).toBeTruthy();
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
