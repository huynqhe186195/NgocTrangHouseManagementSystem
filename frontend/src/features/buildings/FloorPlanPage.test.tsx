import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import type { BuildingDto, FloorDto, RoomDto } from '../../lib/api/types';
import { FloorPlanPage } from './FloorPlanPage';

/**
 * The rail printed a hard-coded "Chưa cập nhật" for Hướng long after
 * `RoomDto.Direction` existed (P4.13), and the note under the facts block said
 * the server held no direction at all. Both are wrong, and neither `tsc` nor a
 * build could tell — so this pins the two states against the real component:
 * a room that carries a direction prints its label, and one that does not still
 * falls back to the dash.
 */

const rooms: { current: RoomDto[] } = { current: [] };

vi.mock('../../lib/api/buildingQueries', () => ({
  useBuildings: () => ({ data: [building], isLoading: false, error: null }),
  useFloors: () => ({ data: [floor], isLoading: false, error: null }),
  useRooms: () => ({ data: rooms.current, isLoading: false, error: null }),
  useBuildingSnapshot: () => ({
    snapshot: {
      building,
      floors: [floor],
      roomsByFloor: new Map([[floor.id, rooms.current]]),
    },
    loading: false,
    error: null,
  }),
  useFloorLayoutItems: () => ({ data: [], isLoading: false }),
  useRoomEquipment: () => ({ data: [], isLoading: false, error: null }),
  useRoomPhotos: () => ({ data: [], isLoading: false }),
  useRoomPhotoContent: () => ({ data: null }),
  useUpdateRoom: () => ({ mutate: vi.fn(), isPending: false, error: null, reset: vi.fn() }),
  useCreateRoom: () => ({ mutate: vi.fn(), isPending: false, error: null, reset: vi.fn() }),
  useDeleteRoomEquipment: () => ({ mutate: vi.fn(), isPending: false }),
  useUploadRoomPhoto: () => ({ mutate: vi.fn(), isPending: false }),
  useDeleteRoomPhoto: () => ({ mutate: vi.fn(), isPending: false }),
  useUpdateRoomRent: () => ({ mutate: vi.fn(), isPending: false, error: null, reset: vi.fn() }),
  useBuildingPublicInfo: () => ({ data: null, isLoading: false, error: null }),
}));

vi.mock('../../lib/api/buildingApi', () => ({
  roomsApi: { getPhotoContent: vi.fn() },
  fileToBase64: vi.fn(),
}));

vi.mock('../../lib/auth/AuthProvider', () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));

const building: BuildingDto = {
  id: 'b1',
  creationTime: '2026-01-01T00:00:00Z',
  creatorId: null,
  lastModificationTime: null,
  lastModifierId: null,
  name: 'Ngọc Trang Building',
  address: '12 Nguyễn Trãi',
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

function makeRoom(
  id: string,
  number: string,
  direction: RoomDto['direction'],
  locationNote: string | null = null,
): RoomDto {
  return {
    id,
    creationTime: '2026-01-01T00:00:00Z',
    creatorId: null,
    lastModificationTime: null,
    lastModifierId: null,
    floorId: 'f1',
    roomNumber: number,
    name: `Phòng ${number}`,
    area: 22.5,
    capacity: 2,
    status: 1,
    description: null,
    roomType: null,
    locationNote,
    direction,
    monthlyRent: 3500000,
    layoutX: 0.2,
    layoutY: 0.2,
    layoutWidth: 5,
    layoutHeight: 4.5,
    layoutShape: 1,
    layoutRotationDegrees: 0,
    layoutPoints: null,
    equipmentCount: 0,
    photoCount: 0,
  };
}

function renderPage(roomNumber: string) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/buildings/b1/floors/f1']}>
        <Routes>
          <Route path="/buildings/:buildingId/floors/:floorId" element={<FloorPlanPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
  // The rail only exists once a room is picked, the way the owner picks one.
  fireEvent.click(screen.getByLabelText(new RegExp(`^Phòng ${roomNumber} —`)));
}

function directionRow(): HTMLElement {
  return factRow('Hướng');
}

function factRow(label: string): HTMLElement {
  const facts = document.querySelector('dl');
  if (!facts) throw new Error('the facts list did not render');
  const row = Array.from(facts.querySelectorAll('div')).find(
    (candidate) => candidate.children.length === 2 && new RegExp(`${label}$`).test(
      candidate.children[0]?.textContent?.trim() ?? '',
    ),
  );
  if (!row) throw new Error(`the ${label} row did not render`);
  return row as HTMLElement;
}

describe('FloorPlanPage — the Hướng row', () => {
  it('prints the stored direction instead of the empty-state dash', () => {
    rooms.current = [makeRoom('r1', '101', 3)];

    renderPage("101");

    expect(within(directionRow()).getByText('Đông')).toBeTruthy();
    expect(within(directionRow()).queryByText('Chưa cập nhật')).toBeNull();
  });

  it('still falls back to the dash when the owner has not set a direction', () => {
    rooms.current = [makeRoom('r2', '102', null)];

    renderPage("102");

    expect(within(directionRow()).getByText('Chưa cập nhật')).toBeTruthy();
  });

  it('no longer explains away the Vị trí row it now fills from the server', () => {
    rooms.current = [makeRoom('r1', '101', 3)];

    renderPage('101');

    expect(screen.queryByText(/Vị trí và hướng chưa có trong dữ liệu/i)).toBeNull();
    expect(screen.queryByText(/Vị trí chưa có trong dữ liệu phòng/i)).toBeNull();
  });
});

describe('FloorPlanPage — the Vị trí row', () => {
  it('prints the note the owner saved', () => {
    rooms.current = [makeRoom('r1', '101', 3, 'Gần cầu thang, view nội khu')];

    renderPage('101');

    expect(within(factRow('Vị trí')).getByText('Gần cầu thang, view nội khu')).toBeTruthy();
  });

  it('falls back to the dash when the owner has not written one', () => {
    rooms.current = [makeRoom('r2', '102', 3)];

    renderPage('102');

    expect(within(factRow('Vị trí')).getByText('Chưa cập nhật')).toBeTruthy();
  });
});
