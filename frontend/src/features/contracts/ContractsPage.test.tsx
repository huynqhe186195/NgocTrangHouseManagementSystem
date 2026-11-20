import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import type { ContractSummaryDto } from '../../lib/api/types';
import { ApiError } from '../../lib/http';
import { ContractsPage, contractCounts } from './ContractsPage';

/**
 * This screen is read-only, so the two things that can be wrong are which
 * rows it prints and whether a refusal is explained honestly. The data hook
 * is mocked; everything that draws it is real.
 */

const contractsState: {
  data: ContractSummaryDto[] | undefined;
  isLoading: boolean;
  error: unknown;
} = { data: [], isLoading: false, error: null };

vi.mock('../../lib/api/buildingQueries', () => ({
  useContracts: () => contractsState,
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
    <MemoryRouter>
      <ContractsPage />
    </MemoryRouter>,
  );
}

describe('ContractsPage', () => {
  it('prints the server row: number, location, tenant, money, status', () => {
    contractsState.data = [contract()];
    contractsState.isLoading = false;
    contractsState.error = null;
    renderPage();

    // Both the desktop table and the mobile card are in the DOM; the CSS
    // breakpoint decides which one a reader sees.
    expect(screen.getAllByText('HD-A-001').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Nguyễn Văn An').length).toBeGreaterThan(0);
    expect(screen.getAllByText('3.500.000 đ').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Đang hiệu lực').length).toBeGreaterThan(0);

    const row = screen.getAllByRole('link', { name: 'HD-A-001' })[0]!;
    expect(row.getAttribute('href')).toBe('/contracts/c1');
  });

  it('carries the building name into the desktop table', () => {
    contractsState.data = [contract()];
    contractsState.isLoading = false;
    contractsState.error = null;
    renderPage();

    const table = screen.getByRole('table');
    expect(within(table).getByText('Ngọc Trang Building')).toBeTruthy();
    expect(within(table).queryByText('Tầng 1')).toBeNull();
    expect(within(table).getByText('A101')).toBeTruthy();
  });

  it('keeps the status column inside the table at every width it is drawn', () => {
    contractsState.data = [contract()];
    contractsState.isLoading = false;
    contractsState.error = null;
    renderPage();

    const table = screen.getByRole('table');
    const headers = within(table).getAllByRole('columnheader');
    // Trạng thái is the last column and must never be hidden by a breakpoint.
    expect(headers[headers.length - 1]!.textContent).toBe('Trạng thái');
    for (const header of headers) {
      expect(header.className).not.toMatch(/\bhidden\b/);
    }
    // The whole table is `xl`-only: below it the cards carry every fact, so no
    // column has to be sacrificed to fit. At 1024 the seven columns overflowed
    // the panel and the status sat past the right edge.
    expect(table.parentElement!.className).toContain('xl:block');
    expect(screen.getByRole('list').className).toContain('xl:hidden');

    const tenantHeader = headers.find((th) => th.textContent === 'Khách thuê')!;
    expect(tenantHeader.className).not.toMatch(/\bhidden\b/);
  });

  it('filters by status pill', () => {
    contractsState.data = [
      contract({ id: 'c1', contractNumber: 'HD-A-001', status: 2 }),
      contract({
        id: 'c2',
        contractNumber: 'HD-DRAFT-9',
        status: 1,
        tenants: [],
      }),
    ];
    contractsState.isLoading = false;
    contractsState.error = null;
    renderPage();

    expect(screen.getAllByText('HD-DRAFT-9').length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole('button', { name: 'Nháp' }));

    expect(screen.queryByText('HD-A-001')).toBeNull();
    expect(screen.getAllByText('HD-DRAFT-9').length).toBeGreaterThan(0);
  });

  it('searches by contract number, room and tenant name', () => {
    contractsState.data = [
      contract({ id: 'c1', contractNumber: 'HD-A-001' }),
      contract({
        id: 'c2',
        contractNumber: 'HD-B-777',
        roomNumber: 'B777',
        buildingName: 'Tòa B',
        tenants: [{ tenantId: 't9', fullName: 'Trần Thị Bình', role: 1 }],
      }),
    ];
    contractsState.isLoading = false;
    contractsState.error = null;
    renderPage();

    const search = screen.getByRole('searchbox');
    fireEvent.change(search, { target: { value: 'bình' } });

    expect(screen.queryByText('HD-A-001')).toBeNull();
    expect(screen.getAllByText('HD-B-777').length).toBeGreaterThan(0);

    fireEvent.change(search, { target: { value: 'b777' } });
    expect(screen.queryByText('HD-A-001')).toBeNull();
    expect(screen.getAllByText('HD-B-777').length).toBeGreaterThan(0);
  });

  it('explains an empty contract table instead of showing a blank page', () => {
    contractsState.data = [];
    contractsState.isLoading = false;
    contractsState.error = null;
    renderPage();

    expect(screen.getByText('Chưa có hợp đồng nào')).toBeTruthy();
  });

  it('says the account lacks rights on 403 rather than blaming the feature', () => {
    contractsState.data = undefined;
    contractsState.isLoading = false;
    contractsState.error = new ApiError('Forbidden', 'Forbidden', 403);
    renderPage();

    const alert = screen.getByRole('status');
    expect(alert.textContent).toContain('403');
    expect(alert.textContent).toContain('từ chối');
    // A missing grant must not be reported as an empty database.
    expect(screen.queryByText('Chưa có hợp đồng nào')).toBeNull();
  });

  it('shows a loading row instead of an empty state while fetching', () => {
    contractsState.data = undefined;
    contractsState.isLoading = true;
    contractsState.error = null;
    renderPage();

    expect(screen.getByText('Đang tải danh sách hợp đồng…')).toBeTruthy();
    expect(screen.queryByText('Chưa có hợp đồng nào')).toBeNull();
  });
});

describe('contractCounts', () => {
  const now = new Date('2026-06-01T00:00:00Z');

  it('counts active, ending within 30 days, and ended', () => {
    const counts = contractCounts(
      [
        contract({ id: 'a', status: 2, endDate: '2026-06-20' }),
        contract({ id: 'b', status: 2, endDate: '2026-12-31' }),
        contract({ id: 'c', status: 2, endDate: '2026-05-01' }),
        contract({ id: 'd', status: 3 }),
        contract({ id: 'e', status: 1 }),
      ],
      now,
    );

    expect(counts.total).toBe(5);
    expect(counts.active).toBe(3);
    // Only the one ending inside the window counts; the already-past one does not.
    expect(counts.expiringSoon).toBe(1);
    expect(counts.ended).toBe(1);
  });
});
