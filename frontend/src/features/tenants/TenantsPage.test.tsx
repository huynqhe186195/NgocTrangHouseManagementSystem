import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import type { TenantSummaryDto } from '../../lib/api/types';
import { ApiError } from '../../lib/http';
import {
  TenantsPage,
  highlightContract,
  isExpiringSoon,
  matchesFilter,
  tenantCounts,
} from './TenantsPage';

/**
 * The roster has to answer two questions honestly: which people exist in the
 * caller's buildings, and what each one's live contract says. The data hook is
 * mocked; everything that draws it is real.
 */

const tenantsState: {
  data: TenantSummaryDto[] | undefined;
  isLoading: boolean;
  error: unknown;
} = { data: [], isLoading: false, error: null };

vi.mock('../../lib/api/buildingQueries', () => ({
  useTenants: () => tenantsState,
}));

function tenant(overrides: Partial<TenantSummaryDto> = {}): TenantSummaryDto {
  return {
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
    notes: null,
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
    ...overrides,
  };
}

function renderPage() {
  return render(
    <MemoryRouter>
      <TenantsPage />
    </MemoryRouter>,
  );
}

describe('TenantsPage', () => {
  it('prints the person, phone, where they rent and the contract state', () => {
    tenantsState.data = [tenant()];
    tenantsState.isLoading = false;
    tenantsState.error = null;
    renderPage();

    // Both the desktop table and the mobile card are in the DOM.
    expect(screen.getAllByText('Nguyễn Văn An').length).toBeGreaterThan(0);
    expect(screen.getAllByText('0901234567').length).toBeGreaterThan(0);
    expect(screen.getAllByText(/A101 · Tầng 1 · Ngọc Trang Building/).length).toBeGreaterThan(0);
    expect(screen.getAllByText('Đang hiệu lực').length).toBeGreaterThan(0);

    const link = screen.getAllByRole('link', { name: 'Nguyễn Văn An' })[0]!;
    expect(link.getAttribute('href')).toBe('/tenants/t1');
  });

  it('never hides a column to make the table fit', () => {
    tenantsState.data = [tenant()];
    tenantsState.isLoading = false;
    tenantsState.error = null;
    renderPage();

    const table = screen.getByRole('table');
    const headers = within(table).getAllByRole('columnheader');
    expect(headers.map((th) => th.textContent)).toEqual([
      'Khách thuê',
      'Điện thoại',
      'Nơi thuê',
      'Hợp đồng',
      'Hết hạn',
      'Trạng thái',
    ]);
    for (const header of headers) {
      expect(header.className).not.toMatch(/\bhidden\b/);
    }
    // Below `xl` the cards must carry every fact instead.
    expect(table.parentElement!.className).toContain('xl:block');
    expect(screen.getByRole('list').className).toContain('xl:hidden');
  });

  it('filters to people who left when the "Đã kết thúc" pill is chosen', () => {
    tenantsState.data = [
      tenant({ id: 't1', fullName: 'Nguyễn Văn An', contracts: [
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
      ] }),
      tenant({
        id: 't2',
        fullName: 'Trần Thị Bình',
        contracts: [
          {
            contractId: 'c2',
            contractNumber: 'HD-A-009',
            status: 3,
            endDate: '2025-06-30',
            roomNumber: 'A102',
            floorName: 'Tầng 1',
            buildingName: 'Ngọc Trang Building',
            roomId: 'r2',
            buildingId: 'b1',
          },
        ],
      }),
    ];
    tenantsState.isLoading = false;
    tenantsState.error = null;
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Đã kết thúc' }));

    expect(screen.queryByText('Nguyễn Văn An')).toBeNull();
    expect(screen.getAllByText('Trần Thị Bình').length).toBeGreaterThan(0);
  });

  it('searches by name, phone and building', () => {
    tenantsState.data = [
      tenant({ id: 't1', fullName: 'Nguyễn Văn An' }),
      tenant({
        id: 't2',
        fullName: 'Trần Thị Bình',
        phoneNumber: '0912345678',
        contracts: [
          {
            contractId: 'c2',
            contractNumber: 'HD-B-001',
            status: 2,
            endDate: '2027-01-31',
            roomNumber: 'B201',
            floorName: 'Tầng 2',
            buildingName: 'Tòa B',
            roomId: 'r2',
            buildingId: 'b2',
          },
        ],
      }),
    ];
    tenantsState.isLoading = false;
    tenantsState.error = null;
    renderPage();

    const search = screen.getByRole('searchbox');

    fireEvent.change(search, { target: { value: 'bình' } });
    expect(screen.queryByText('Nguyễn Văn An')).toBeNull();
    expect(screen.getAllByText('Trần Thị Bình').length).toBeGreaterThan(0);

    fireEvent.change(search, { target: { value: '0912345678' } });
    expect(screen.queryByText('Nguyễn Văn An')).toBeNull();
    expect(screen.getAllByText('Trần Thị Bình').length).toBeGreaterThan(0);

    fireEvent.change(search, { target: { value: 'tòa b' } });
    expect(screen.queryByText('Nguyễn Văn An')).toBeNull();
    expect(screen.getAllByText('Trần Thị Bình').length).toBeGreaterThan(0);
  });

  it('explains an empty roster instead of showing a blank page', () => {
    tenantsState.data = [];
    tenantsState.isLoading = false;
    tenantsState.error = null;
    renderPage();

    expect(screen.getByText('Chưa có khách thuê nào')).toBeTruthy();
  });

  it('says the account lacks rights on 403 rather than blaming the feature', () => {
    tenantsState.data = undefined;
    tenantsState.isLoading = false;
    tenantsState.error = new ApiError('Forbidden', 'Forbidden', 403);
    renderPage();

    const alert = screen.getByRole('status');
    expect(alert.textContent).toContain('403');
    expect(alert.textContent).toContain('từ chối');
    expect(screen.queryByText('Chưa có khách thuê nào')).toBeNull();
  });

  it('shows a loading row instead of an empty state while fetching', () => {
    tenantsState.data = undefined;
    tenantsState.isLoading = true;
    tenantsState.error = null;
    renderPage();

    expect(screen.getByText('Đang tải danh sách khách thuê…')).toBeTruthy();
    expect(screen.queryByText('Chưa có khách thuê nào')).toBeNull();
  });
});

describe('roster helpers', () => {
  const now = new Date('2026-06-01T00:00:00Z');

  function contract(
    overrides: Partial<TenantSummaryDto['contracts'][number]> = {},
  ): TenantSummaryDto['contracts'][number] {
    return {
      contractId: 'c1',
      contractNumber: 'HD-A-001',
      status: 2,
      endDate: '2026-12-31',
      roomNumber: 'A101',
      floorName: 'Tầng 1',
      buildingName: 'Ngọc Trang Building',
      roomId: 'r1',
      buildingId: 'b1',
      ...overrides,
    };
  }

  it('labels a person by their live contract, not an old finished one', () => {
    const highlighted = highlightContract(
      tenant({
        contracts: [
          contract({ contractId: 'old', contractNumber: 'HD-OLD', status: 3 }),
          contract({ contractId: 'live', contractNumber: 'HD-NEW', status: 2 }),
        ],
      }),
    );

    expect(highlighted?.contractNumber).toBe('HD-NEW');
  });

  it('counts only windows that have not already passed', () => {
    expect(isExpiringSoon(contract({ endDate: '2026-06-20' }), now)).toBe(true);
    expect(isExpiringSoon(contract({ endDate: '2026-05-01' }), now)).toBe(false);
    expect(isExpiringSoon(contract({ endDate: '2027-01-01' }), now)).toBe(false);
    // A finished contract has already ended; it is not "sắp hết hạn".
    expect(isExpiringSoon(contract({ status: 3, endDate: '2026-06-20' }), now)).toBe(false);
  });

  it('counts renting, expiring and multi-room tenants', () => {
    const counts = tenantCounts(
      [
        tenant({ id: 'a', contracts: [contract({ endDate: '2026-06-20' })] }),
        tenant({ id: 'b', contracts: [contract({ endDate: '2027-01-01' })] }),
        tenant({
          id: 'c',
          contracts: [
            contract({ contractId: 'c1' }),
            contract({ contractId: 'c2', roomNumber: 'A102' }),
          ],
        }),
        tenant({ id: 'd', contracts: [contract({ status: 3 })] }),
      ],
      now,
    );

    expect(counts.total).toBe(4);
    expect(counts.renting).toBe(3);
    expect(counts.expiringSoon).toBe(1);
    expect(counts.multi).toBe(1);
  });

  it('treats a person with no contract as neither renting nor finished', () => {
    const nobody = tenant({ contracts: [] });
    expect(matchesFilter(nobody, 'renting', now)).toBe(false);
    expect(matchesFilter(nobody, 'finished', now)).toBe(false);
    expect(matchesFilter(nobody, 'all', now)).toBe(true);
  });
});
