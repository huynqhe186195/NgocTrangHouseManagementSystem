import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, within, fireEvent } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import type { FloorDto, RoomDto } from '../../lib/api/types';
import type { BuildingSnapshot } from '../../lib/api/buildingQueries';
import { BuildingDetailPage } from './BuildingsPage';

/**
 * What this file covers is the remake against the mockup: the KPI row is
 * computed from the server payload (the mockup's 5 / 80 / 72 / 90% are gone),
 * the floor table carries exactly the six mockup columns, the status badge
 * reads the stored floor status, and an unplaced floor says so instead of
 * drawing a plan it does not have.
 */

let snapshot: BuildingSnapshot;

vi.mock('../../lib/auth/AuthProvider', () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));

vi.mock('../../lib/api/buildingQueries', () => ({
  useBuildingSnapshot: () => ({ snapshot, loading: false, error: null }),
  useBuildingPhotoContent: () => ({ data: undefined, isLoading: false }),
  useUploadBuildingPhoto: () => ({ mutate: vi.fn(), isPending: false, error: null }),
  useDeleteBuildingPhoto: () => ({ mutate: vi.fn(), isPending: false, error: null }),
  useDeleteBuilding: () => ({ mutate: vi.fn(), isPending: false, error: null, reset: vi.fn() }),
  useDeleteFloor: () => ({ mutate: vi.fn(), isPending: false, error: null, reset: vi.fn() }),
  // No floor has saved coordinates in these fixtures, so every row must fall
  // through to the "Chưa có sơ đồ" state.
  useFloorLayoutItemsFor: () => new Map(),
  useBuildingPublicInfo: () => ({ data: null, isLoading: false, error: null }),
  useUpdateBuildingPublicInfo: () => ({
    mutate: vi.fn(),
    isPending: false,
    error: null,
    reset: vi.fn(),
  }),
}));

// The dialogs are separate screens with their own wiring; keeping them out of
// this tree leaves the assertions below about the page itself.
vi.mock('./BuildingFormDialog', () => ({
  BuildingFormDialog: () => null,
}));

vi.mock('./FloorFormDialog', () => ({
  FloorFormDialog: () => null,
}));

function floor(overrides: Partial<FloorDto> = {}): FloorDto {
  return {
    id: 'fl1',
    creationTime: '2026-01-01T00:00:00Z',
    creatorId: null,
    lastModificationTime: null,
    lastModifierId: null,
    buildingId: 'b1',
    floorNumber: 1,
    name: 'Tầng 1',
    description: null,
    status: 1,
    ...overrides,
  };
}

function room(
  id: string,
  floorId: string,
  status: 1 | 2 | 3 | 4 | 5,
  box?: { x: number; y: number; width: number; height: number },
): RoomDto {
  return {
    id,
    creationTime: '2026-01-01T00:00:00Z',
    creatorId: null,
    lastModificationTime: null,
    lastModifierId: null,
    floorId,
    roomNumber: id,
    name: id,
    area: 20,
    capacity: 2,
    status,
    description: null,
    roomType: null,
    locationNote: null,
    direction: null,
    monthlyRent: null,
    layoutX: box?.x ?? null,
    layoutY: box?.y ?? null,
    layoutWidth: box?.width ?? null,
    layoutHeight: box?.height ?? null,
    layoutShape: box ? 1 : null,
    layoutRotationDegrees: box ? 0 : null,
    layoutPoints: null,
    equipmentCount: 0,
    photoCount: 0,
  };
}

function makeSnapshot(floors: FloorDto[], rooms: RoomDto[]): BuildingSnapshot {
  const floorIds = new Set(floors.map((item) => item.id));
  const buildingRooms = rooms.filter((item) => floorIds.has(item.floorId));
  const roomsByFloor = new Map<string, RoomDto[]>();
  for (const item of buildingRooms) {
    const bucket = roomsByFloor.get(item.floorId) ?? [];
    bucket.push(item);
    roomsByFloor.set(item.floorId, bucket);
  }
  const occupiedCount = buildingRooms.filter((item) => item.status === 2).length;
  return {
    building: {
      id: 'b1',
      creationTime: '2026-01-01T00:00:00Z',
      creatorId: null,
      lastModificationTime: null,
      lastModifierId: null,
      name: 'Tòa nhà Ngọc Trang',
      address: '12 Nguyễn Trãi, Hà Nội',
      description: 'Tòa nhà văn phòng cho thuê.',
      hasPhoto: false,
    },
    floors,
    rooms: buildingRooms,
    roomsByFloor,
    roomCount: buildingRooms.length,
    occupiedCount,
    occupancyRate:
      buildingRooms.length === 0
        ? 0
        : Math.round((occupiedCount / buildingRooms.length) * 100),
  };
}

function renderPage() {
  return render(
    <MemoryRouter initialEntries={['/buildings/b1']}>
      <Routes>
        <Route path="/buildings/:buildingId" element={<BuildingDetailPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  // Deliberately not the mockup's numbers: three floors, seven rooms, four
  // occupied, so 57% — a value that could only come from the payload.
  const floors = [
    floor({ id: 'fl1', floorNumber: 1, status: 1 }),
    floor({ id: 'fl2', floorNumber: 2, status: 2 }),
    floor({ id: 'fl3', floorNumber: 3, status: 3 }),
  ];
  snapshot = makeSnapshot(floors, [
    room('101', 'fl1', 2),
    room('102', 'fl1', 2),
    room('103', 'fl1', 1),
    room('201', 'fl2', 2),
    room('202', 'fl2', 1),
    room('301', 'fl3', 2),
    room('302', 'fl3', 4),
  ]);
});

describe('BuildingDetailPage', () => {
  it('computes the KPI row from the server payload, not the mockup constants', () => {
    renderPage();

    expect(screen.getByText('Tổng số tầng')).toBeTruthy();
    expect(screen.getByText('Tổng số phòng')).toBeTruthy();
    expect(screen.getByText('Đã thuê')).toBeTruthy();
    expect(screen.getByText('Tỷ lệ lấp đầy')).toBeTruthy();

    const values = Array.from(
      document.querySelectorAll('.tnum'),
      (node) => node.textContent,
    );
    expect(values).toContain('3');
    expect(values).toContain('7');
    expect(values).toContain('4');
    expect(values).toContain('57%');
    expect(values).not.toContain('5');
    expect(values).not.toContain('90%');
  });

  it('renders exactly the six mockup columns and drops "Giá thuê từ"', () => {
    renderPage();

    const table = screen.getByRole('table');
    const headers = within(table)
      .getAllByRole('columnheader')
      .map((node) => node.textContent);

    expect(headers).toEqual([
      'Tầng',
      'Sơ đồ mặt bằng',
      'Số phòng',
      'Trạng thái',
      'Ghi chú',
      'Hành động',
    ]);
    expect(headers).not.toContain('Giá thuê từ');
  });

  it('shows the status stored on each floor', () => {
    renderPage();

    const table = screen.getByRole('table');
    const body = table.querySelector('tbody') as HTMLElement;
    const rows = within(body).getAllByRole('row');

    expect(within(rows[0]!).getByText('Hoạt động')).toBeTruthy();
    expect(within(rows[1]!).getByText('Bảo trì')).toBeTruthy();
    expect(within(rows[2]!).getByText('Ngừng dùng')).toBeTruthy();
  });

  it('says a floor has no plan instead of inventing one', () => {
    renderPage();

    // The storey list renders twice — as the `xl` table and as the card list
    // below it — so the message appears once per storey in each view.
    const table = screen.getByRole('table');
    expect(within(table).getAllByText('Chưa có sơ đồ')).toHaveLength(3);
  });

  it('reports how many rooms of a half-designed storey the thumbnail draws', () => {
    const floors = [floor({ id: 'fl1', floorNumber: 1, status: 1 })];
    snapshot = makeSnapshot(floors, [
      room('101', 'fl1', 1, { x: 1, y: 1, width: 5, height: 4 }),
      room('102', 'fl1', 2, { x: 6, y: 1, width: 5, height: 4 }),
      room('103', 'fl1', 1),
    ]);

    renderPage();

    // Only two of the three rooms have coordinates. Drawing the placed two and
    // saying nothing would understate the storey by a third.
    const table = screen.getByRole('table');
    expect(within(table).getByText('2/3 phòng có toạ độ')).toBeTruthy();
    expect(
      within(table).getByRole('img', { name: 'Sơ đồ mặt bằng thu nhỏ: 2 trên 3 phòng có toạ độ' }),
    ).toBeTruthy();
  });

  it('offers an upload on an empty cover instead of only the replace pill', () => {
    renderPage();

    // The frame has no photograph and the pill underneath says "Thay đổi ảnh",
    // which reads as replace. An owner with no photo needs to be told where to
    // start, so the empty frame carries its own upload action.
    const button = screen.getByRole('button', { name: /Tải ảnh lên/ });
    expect(button).toBeTruthy();
  });

  it('paginates five rows at a time and reports the visible range', () => {
    const floors = Array.from({ length: 7 }, (_, index) =>
      floor({ id: `fl${index + 1}`, floorNumber: index + 1 }),
    );
    snapshot = makeSnapshot(
      floors,
      floors.flatMap((item, index) => [
        room(`${index}1`, item.id, 1),
        room(`${index}2`, item.id, 2),
      ]),
    );

    renderPage();

    expect(screen.getByText('Hiển thị 1 - 5 của 7 tầng')).toBeTruthy();
    const table = screen.getByRole('table');
    expect(within(table).getAllByRole('row')).toHaveLength(6); // header + 5

    fireEvent.click(screen.getByRole('button', { name: 'Trang sau' }));

    expect(screen.getByText('Hiển thị 6 - 7 của 7 tầng')).toBeTruthy();
    expect(within(screen.getByRole('table')).getAllByRole('row')).toHaveLength(3);
  });

  it('offers the two mockup actions plus a real overflow menu', () => {
    renderPage();

    expect(screen.getByRole('button', { name: /Chỉnh sửa tòa nhà/ })).toBeTruthy();
    expect(screen.getByRole('button', { name: /Thêm tầng/ })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Thao tác khác của tòa nhà' })).toBeTruthy();
  });
});
