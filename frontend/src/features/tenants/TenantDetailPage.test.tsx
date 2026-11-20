import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import type { TenantSummaryDto } from '../../lib/api/types';
import { ApiError } from '../../lib/http';
import { TenantDetailPage } from './TenantDetailPage';

const tenantState: {
  data: TenantSummaryDto | undefined;
  isLoading: boolean;
  error: unknown;
} = { data: undefined, isLoading: false, error: null };

vi.mock('../../lib/api/buildingQueries', () => ({
  useTenant: () => tenantState,
}));

function renderPage() {
  return render(
    <MemoryRouter initialEntries={['/tenants/t1']}>
      <Routes>
        <Route path="/tenants/:tenantId" element={<TenantDetailPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('TenantDetailPage', () => {
  it('shows contact details and every contract the caller may see', () => {
    tenantState.data = {
      id: 't1',
      creationTime: '2026-01-01T00:00:00Z',
      creatorId: null,
      lastModificationTime: null,
      lastModifierId: null,
      fullName: 'Nguyễn Văn An',
      phoneNumber: '0901234567',
      email: 'an@example.com',
      gender: 1,
      permanentAddress: '12 Lê Lợi, Quận 1',
      notes: 'Khách quen, đóng tiền đúng hạn.',
      contracts: [
        {
          contractId: 'c1',
          contractNumber: 'HD-A-001',
          status: 2,
          endDate: '2026-12-31',
          roomNumber: 'A101',
          floorName: 'Tầng 1',
          buildingName: 'Ngọc Trang Building',
          roomId: 'r1',
          buildingId: 'b1',
        },
      ],
    };
    tenantState.isLoading = false;
    tenantState.error = null;
    renderPage();

    expect(screen.getByRole('heading', { name: 'Nguyễn Văn An' })).toBeTruthy();
    expect(screen.getByText('1 hợp đồng trong tòa nhà bạn được xem')).toBeTruthy();
    expect(screen.getByText('0901234567')).toBeTruthy();
    expect(screen.getByText('an@example.com')).toBeTruthy();
    expect(screen.getByText('Nam')).toBeTruthy();
    expect(screen.getByText('12 Lê Lợi, Quận 1')).toBeTruthy();
    expect(screen.getByText('Khách quen, đóng tiền đúng hạn.')).toBeTruthy();

    const contract = screen.getByRole('link', { name: 'HD-A-001' });
    expect(contract.getAttribute('href')).toBe('/contracts/c1');
    expect(screen.getByText(/Ngọc Trang Building · Tầng 1 · Phòng A101/)).toBeTruthy();
    expect(screen.getByText(/đến 31\/12\/2026/)).toBeTruthy();
  });

  it('says a person has no contract left rather than printing an empty box', () => {
    tenantState.data = {
      id: 't1',
      creationTime: '2026-01-01T00:00:00Z',
      creatorId: null,
      lastModificationTime: null,
      lastModifierId: null,
      fullName: 'Nguyễn Văn An',
      phoneNumber: '0901234567',
      email: null,
      gender: null,
      permanentAddress: null,
      notes: null,
      contracts: [],
    };
    tenantState.isLoading = false;
    tenantState.error = null;
    renderPage();

    expect(
      screen.getByText('Khách thuê này không còn hợp đồng nào trong tòa nhà bạn được xem.'),
    ).toBeTruthy();
    expect(screen.getByText('Khách thuê không có ghi chú.')).toBeTruthy();
  });

  it('explains a 404 as a person outside the caller buildings, not a missing feature', () => {
    tenantState.data = undefined;
    tenantState.isLoading = false;
    tenantState.error = new ApiError('NotFound', 'Not found', 404);
    renderPage();

    const alert = screen.getByRole('status');
    expect(alert.textContent).toContain('Không tìm thấy');
    expect(alert.textContent).toContain('tòa nhà');
  });

  it('shows a loading row while the tenant is fetched', () => {
    tenantState.data = undefined;
    tenantState.isLoading = true;
    tenantState.error = null;
    renderPage();

    expect(screen.getByText('Đang tải thông tin khách thuê…')).toBeTruthy();
  });
});
