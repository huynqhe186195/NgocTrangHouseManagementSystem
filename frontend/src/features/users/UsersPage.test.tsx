import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import type { OperatorMembershipSummaryDto } from '../../lib/api/types';
import { ApiError } from '../../lib/http';
import { UsersPage } from './UsersPage';

const membershipsState: {
  data: { totalCount: number; items: OperatorMembershipSummaryDto[] } | undefined;
  isLoading: boolean;
  error: unknown;
} = { data: { totalCount: 0, items: [] }, isLoading: false, error: null };

vi.mock('../../lib/api/buildingQueries', () => ({
  useOperatorMemberships: () => membershipsState,
}));

function membership(overrides: Partial<OperatorMembershipSummaryDto> = {}): OperatorMembershipSummaryDto {
  return {
    id: 'grant-1',
    creationTime: '2026-01-01T00:00:00Z',
    creatorId: null,
    lastModificationTime: null,
    lastModifierId: null,
    operatorUserId: 'operator-11111111-2222-3333-4444-555555555555',
    buildingId: 'b1',
    buildingName: 'Ngọc Trang Building',
    grantedByUserId: 'issuer-11111111-2222-3333-4444-555555555555',
    grantedAtUtc: '2026-01-01T08:00:00Z',
    expiresAtUtc: null,
    revokedAtUtc: null,
    revisionNumber: 1,
    status: 1,
    ...overrides,
  };
}

function renderPage() {
  return render(
    <MemoryRouter>
      <UsersPage />
    </MemoryRouter>,
  );
}

describe('UsersPage', () => {
  it('prints the scoped membership facts on table and mobile card surfaces', () => {
    membershipsState.data = { totalCount: 1, items: [membership()] };
    membershipsState.isLoading = false;
    membershipsState.error = null;

    renderPage();

    expect(screen.getAllByText(/operator…5555/).length).toBeGreaterThan(0);
    expect(screen.getAllByText('Ngọc Trang Building').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Đang hiệu lực').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Không thời hạn').length).toBeGreaterThan(0);
    expect(screen.getByRole('table').textContent).toContain('rev 1');

    const table = screen.getByRole('table');
    expect(within(table).getAllByRole('columnheader').map((header) => header.textContent)).toEqual([
      'Operator',
      'Tòa nhà',
      'Trạng thái',
      'Cấp quyền',
      'Hết hạn',
      'Thu hồi',
      'Người cấp / revision',
    ]);
    expect(table.parentElement?.className).toContain('xl:block');
    expect(screen.getByRole('list').className).toContain('xl:hidden');
  });

  it('filters by a building supplied by the server and resets to the first page', () => {
    membershipsState.data = {
      totalCount: 2,
      items: [
        membership(),
        membership({
          id: 'grant-2',
          buildingId: 'b2',
          buildingName: 'Tòa B',
          operatorUserId: 'operator-2',
        }),
      ],
    };
    membershipsState.isLoading = false;
    membershipsState.error = null;

    renderPage();

    const select = screen.getByRole('combobox', { name: 'Lọc theo tòa nhà' });
    expect(within(select).getByRole('option', { name: 'Ngọc Trang Building' })).toBeTruthy();
    expect(within(select).getByRole('option', { name: 'Tòa B' })).toBeTruthy();

    fireEvent.change(select, { target: { value: 'b2' } });
    expect((select as HTMLSelectElement).value).toBe('b2');
    expect(screen.getByText((_, element) => element?.textContent === '2 membership · chưa mở thao tác cấp hoặc thu hồi quyền')).toBeTruthy();
  });

  it('shows status metadata for expired, revoked and scheduled history', () => {
    membershipsState.data = {
      totalCount: 3,
      items: [
        membership({ id: 'expired', status: 2, expiresAtUtc: '2025-12-31T17:00:00Z' }),
        membership({ id: 'revoked', status: 3, revokedAtUtc: '2026-02-01T08:00:00Z' }),
        membership({ id: 'scheduled', status: 4, grantedAtUtc: '2027-01-01T08:00:00Z' }),
      ],
    };
    membershipsState.isLoading = false;
    membershipsState.error = null;

    renderPage();

    expect(screen.getAllByText('Đã hết hạn').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Đã thu hồi').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Sắp có hiệu lực').length).toBeGreaterThan(0);
  });

  it('explains an empty result instead of showing a blank panel', () => {
    membershipsState.data = { totalCount: 0, items: [] };
    membershipsState.isLoading = false;
    membershipsState.error = null;

    renderPage();

    expect(screen.getByText('Chưa có membership nào')).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('keeps 403 separate from an empty result', () => {
    membershipsState.data = undefined;
    membershipsState.isLoading = false;
    membershipsState.error = new ApiError('Forbidden', 'Forbidden', 403);

    renderPage();

    const alert = screen.getByRole('status');
    expect(alert.textContent).toContain('403');
    expect(alert.textContent).toContain('từ chối');
    expect(screen.queryByText('Chưa có membership nào')).toBeNull();
  });

  it('shows loading instead of empty while the server request is pending', () => {
    membershipsState.data = undefined;
    membershipsState.isLoading = true;
    membershipsState.error = null;

    renderPage();

    expect(screen.getByText('Đang tải danh sách người dùng…')).toBeTruthy();
    expect(screen.queryByText('Chưa có membership nào')).toBeNull();
  });

  it('does not render assign, revoke or transfer controls', () => {
    membershipsState.data = { totalCount: 1, items: [membership()] };
    membershipsState.isLoading = false;
    membershipsState.error = null;

    renderPage();

    expect(screen.queryByRole('button', { name: /thêm|cấp|thu hồi|đổi chủ/i })).toBeNull();
    expect(screen.getByText(/chưa mở thao tác cấp hoặc thu hồi quyền/)).toBeTruthy();
  });
});
