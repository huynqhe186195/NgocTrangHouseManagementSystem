import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import type { FloorLayoutItemDto, FloorDto, RoomDto } from '../../lib/api/types';
import type { BuildingSnapshot } from '../../lib/api/buildingQueries';
import { FloorDesignerPage } from './FloorDesignerPage';

/**
 * The designer's drag maths is covered by floorGeometry.test.ts. What this
 * file covers is the page's wiring: a drop reaches the right mutation with the
 * right payload, and a save drops only the drafts that actually reached the
 * server.
 */

const saveRoomLayout = vi.fn();
const createLayoutItem = vi.fn();
const updateLayoutItem = vi.fn();
const deleteLayoutItem = vi.fn();

let snapshot: BuildingSnapshot;
let layoutItems: FloorLayoutItemDto[];
let saveFails: Set<string>;

vi.mock('../../lib/auth/AuthProvider', () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));

vi.mock('../../lib/api/buildingQueries', () => ({
  useBuildingSnapshot: () => ({ snapshot, loading: false, error: null }),
  useFloorLayoutItems: () => ({ data: layoutItems, isLoading: false, error: null }),
  useSaveRoomLayouts: () => ({ mutateAsync: saveRoomLayout }),
  useCreateFloorLayoutItem: () => ({ mutateAsync: createLayoutItem }),
  useUpdateFloorLayoutItem: () => ({ mutateAsync: updateLayoutItem }),
  useDeleteFloorLayoutItem: () => ({ mutateAsync: deleteLayoutItem }),
}));

function room(overrides: Partial<RoomDto> = {}): RoomDto {
  return {
    id: 'r1',
    creationTime: '2026-01-01T00:00:00Z',
    creatorId: null,
    lastModificationTime: null,
    lastModifierId: null,
    floorId: 'f1',
    roomNumber: 'A101',
    name: 'Phòng A101',
    area: 22,
    capacity: 2,
    status: 1,
    description: null,
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
};

function renderPage() {
  return render(
    <MemoryRouter initialEntries={['/buildings/b1/floors/f1/design']}>
      <Routes>
        <Route
          path="/buildings/:buildingId/floors/:floorId/design"
          element={<FloorDesignerPage />}
        />
      </Routes>
    </MemoryRouter>,
  );
}

/** jsdom has no DataTransfer constructor, so the drop event carries a stub. */
function dropOnCanvas(data: Record<string, string>) {
  const canvas = screen.getByRole('application');
  fireEvent.drop(canvas, {
    dataTransfer: {
      getData: (key: string) => data[key] ?? '',
      setData: () => undefined,
      effectAllowed: 'copy',
    },
    clientX: 100,
    clientY: 100,
  });
}

beforeEach(() => {
  saveRoomLayout.mockReset().mockResolvedValue(undefined);
  createLayoutItem.mockReset().mockResolvedValue(undefined);
  updateLayoutItem.mockReset().mockResolvedValue(undefined);
  deleteLayoutItem.mockReset().mockResolvedValue(undefined);
  saveFails = new Set();

  // A rejected room save is how the page learns to keep that draft.
  saveRoomLayout.mockImplementation(({ roomId }: { roomId: string }) =>
    saveFails.has(roomId) ? Promise.reject(new Error('boom')) : Promise.resolve(undefined),
  );

  snapshot = {
    building: {
      id: 'b1',
      creationTime: '2026-01-01T00:00:00Z',
      creatorId: null,
      lastModificationTime: null,
      lastModifierId: null,
      name: 'Tòa nhà A',
      address: '1 Đường A',
      description: null,
    },
    floors: [floor],
    rooms: [room()],
    roomsByFloor: new Map([['f1', [room()]]]),
    roomCount: 1,
    occupiedCount: 0,
    occupancyRate: 0,
  };
  layoutItems = [];
});

describe('FloorDesignerPage', () => {
  it('lists the floor rooms as unplaced until they are dropped onto the plan', () => {
    renderPage();

    expect(screen.getByText('Thiết kế sơ đồ — Tầng 1')).toBeTruthy();
    expect(screen.getByText(/0\/1 phòng đã đặt\./)).toBeTruthy();
    expect(screen.getByText('Chưa đặt')).toBeTruthy();
  });

  it('saves a dropped room through the layout endpoint with a snapped box', async () => {
    renderPage();

    dropOnCanvas({ 'application/x-room-id': 'r1' });

    expect(screen.getByText(/1\/1 phòng đã đặt\./)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Lưu sơ đồ/ }));

    await waitFor(() => expect(saveRoomLayout).toHaveBeenCalledTimes(1));
    const call = saveRoomLayout.mock.calls[0]![0] as {
      roomId: string;
      input: { x: number; y: number; width: number; height: number };
    };
    expect(call.roomId).toBe('r1');
    // jsdom has no `getScreenCTM`, so the pointer resolves to the origin and the
    // box is clamped to the default empty-floor plan (14 x 10 m, starting at 0,0).
    expect(call.input).toMatchObject({ x: 0, y: 0, width: 4, height: 3.5 });
    await waitFor(() => expect(screen.getByText('Đã lưu sơ đồ tầng.')).toBeTruthy());
  });

  it('creates a new floor object from the palette and reports its kind', async () => {
    renderPage();

    dropOnCanvas({ 'application/x-item-kind': '2' });
    fireEvent.click(screen.getByRole('button', { name: /Lưu sơ đồ/ }));

    await waitFor(() => expect(createLayoutItem).toHaveBeenCalledTimes(1));
    expect(createLayoutItem.mock.calls[0]![0]).toMatchObject({ kind: 2, label: null });
  });

  it('keeps the failed draft so a retry does not duplicate a created object', async () => {
    createLayoutItem
      .mockRejectedValueOnce(new Error('boom'))
      .mockResolvedValue(undefined);
    renderPage();

    dropOnCanvas({ 'application/x-item-kind': '2' });
    fireEvent.click(screen.getByRole('button', { name: /Lưu sơ đồ/ }));

    await waitFor(() => expect(screen.getByText(/Một số thay đổi chưa lưu được/)).toBeTruthy());
    expect(screen.getByRole('button', { name: /Lưu sơ đồ \(1\)/ })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Lưu sơ đồ/ }));
    await waitFor(() => expect(createLayoutItem).toHaveBeenCalledTimes(2));
    expect(screen.getByRole('button', { name: 'Lưu sơ đồ' })).toBeTruthy();
  });

  it('drops a saved draft but keeps the one the server rejected', async () => {
    saveFails.add('r2');
    snapshot = {
      ...snapshot,
      rooms: [room(), room({ id: 'r2', roomNumber: 'A102' })],
      roomsByFloor: new Map([['f1', [room(), room({ id: 'r2', roomNumber: 'A102' })]]]),
    };
    renderPage();

    dropOnCanvas({ 'application/x-room-id': 'r1' });
    dropOnCanvas({ 'application/x-room-id': 'r2' });
    expect(screen.getByText(/2\/2 phòng đã đặt\./)).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Lưu sơ đồ/ }));

    await waitFor(() => expect(saveRoomLayout).toHaveBeenCalledTimes(2));
    await waitFor(() =>
      expect(screen.getByRole('button', { name: /Lưu sơ đồ \(1\)/ })).toBeTruthy(),
    );
  });
});
