import { describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import type { ContractSummaryDto } from '../../lib/api/types';
import { ApiError } from '../../lib/http';
import { ContractDetailPage } from './ContractDetailPage';

/**
 * Read-only detail. The panels it must draw, and — the part that would be a
 * lie if it were missing — the sentence saying the write actions are locked
 * because the server has not opened them.
 */

const contractState: {
  data: ContractSummaryDto | undefined;
  isLoading: boolean;
  error: unknown;
} = { data: undefined, isLoading: false, error: null };

vi.mock('../../lib/api/buildingQueries', () => ({
  useContract: () => contractState,
}));

function contract(overrides: Partial<ContractSummaryDto> = {}): ContractSummaryDto {
  return {
    id: 'c1',
    creationTime: '2026-01-01T00:00:00Z',
    creatorId: null,
    lastModificationTime: null,
    lastModifierId: null,
    contractNumber: 'HD-A-001',
    status: 2,
    startDate: '2026-01-01',
    endDate: '2026-12-31',
    actualEndedAt: null,
    endReason: null,
    monthlyRent: 3_500_000,
    depositAmount: 7_000_000,
    notes: null,
    roomId: 'r1',
    roomNumber: 'A101',
    floorId: 'f1',
    floorName: 'Tầng 1',
    buildingId: 'b1',
    buildingName: 'Ngọc Trang Building',
    tenants: [{ tenantId: 't1', fullName: 'Nguyễn Văn An', role: 1 }],
    ...overrides,
  };
}

function renderPage() {
  return render(
    <MemoryRouter initialEntries={['/contracts/c1']}>
      <Routes>
        <Route path="/contracts/:contractId" element={<ContractDetailPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('ContractDetailPage', () => {
  it('draws the three panels plus notes, with links back to room and building', () => {
    contractState.data = contract();
    contractState.isLoading = false;
    contractState.error = null;
    renderPage();

    expect(screen.getByRole('heading', { name: 'Thông tin hợp đồng' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Tài chính' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Khách thuê (1)' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Ghi chú' })).toBeTruthy();

    expect(screen.getByText('3.500.000 đ')).toBeTruthy();
    expect(screen.getByText('7.000.000 đ')).toBeTruthy();

    const roomLink = screen.getByRole('link', { name: 'Phòng A101' });
    expect(roomLink.getAttribute('href')).toBe('/rooms/r1');
    const buildingLink = screen.getByRole('link', { name: 'Ngọc Trang Building' });
    expect(buildingLink.getAttribute('href')).toBe('/buildings/b1');
  });

  it('labels each tenant with its role', () => {
    contractState.data = contract({
      tenants: [
        { tenantId: 't1', fullName: 'Nguyễn Văn An', role: 1 },
        { tenantId: 't2', fullName: 'Lê Thị Cẩm', role: 2 },
      ],
    });
    contractState.isLoading = false;
    contractState.error = null;
    renderPage();

    expect(screen.getByText('Khách thuê chính')).toBeTruthy();
    expect(screen.getByText('Thành viên')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Khách thuê (2)' })).toBeTruthy();
  });

  it('shows the end date and reason once the contract has ended', () => {
    contractState.data = contract({
      status: 3,
      actualEndedAt: '2026-09-30',
      endReason: 2,
    });
    contractState.isLoading = false;
    contractState.error = null;
    renderPage();

    expect(screen.getByText('Ngày kết thúc thực tế')).toBeTruthy();
    expect(screen.getByText('30/09/2026')).toBeTruthy();
    expect(screen.getByText('Chấm dứt trước hạn')).toBeTruthy();
  });

  it('locks the write actions and says the server has not opened them', () => {
    contractState.data = contract();
    contractState.isLoading = false;
    contractState.error = null;
    renderPage();

    for (const label of ['Kích hoạt', 'Huỷ hợp đồng', 'Kết thúc hợp đồng']) {
      const button = screen.getByRole('button', {
        name: new RegExp(label),
      }) as HTMLButtonElement;
      expect(button.disabled).toBe(true);
      expect(button.getAttribute('title')).toContain('chưa được mở trên máy chủ');
    }

    expect(screen.getByText(/Các thao tác ghi hợp đồng .* chưa được mở trên máy chủ/)).toBeTruthy();
  });

  it('says a 403 is a missing grant, not a missing contract', () => {
    contractState.data = undefined;
    contractState.isLoading = false;
    contractState.error = new ApiError('Forbidden', 'Forbidden', 403);
    renderPage();

    const alert = screen.getByRole('status');
    expect(alert.textContent).toContain('403');
    expect(alert.textContent).toContain('chưa được cấp quyền');
    expect(screen.queryByText('Không tìm thấy hợp đồng')).toBeNull();
  });

  it('falls back to an empty state when the server returns nothing', () => {
    contractState.data = undefined;
    contractState.isLoading = false;
    contractState.error = null;
    renderPage();

    expect(screen.getByText('Không tìm thấy hợp đồng')).toBeTruthy();
    const back = screen.getByRole('link', { name: 'Quay lại danh sách hợp đồng' });
    expect(back.getAttribute('href')).toBe('/contracts');
  });

  it('prints the notes the server holds, and says so when there are none', () => {
    contractState.data = contract({ notes: 'Khách xin lắp thêm điều hoà.' });
    contractState.isLoading = false;
    contractState.error = null;
    const { unmount } = renderPage();
    expect(screen.getByText('Khách xin lắp thêm điều hoà.')).toBeTruthy();
    unmount();

    contractState.data = contract({ notes: '   ' });
    renderPage();
    const notesPanel = screen.getByRole('heading', { name: 'Ghi chú' }).closest('section')!;
    expect(within(notesPanel).getByText('Hợp đồng không có ghi chú.')).toBeTruthy();
  });
});
