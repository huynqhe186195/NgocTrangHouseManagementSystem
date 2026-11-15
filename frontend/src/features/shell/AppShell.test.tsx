import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, within, fireEvent } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { AppShell } from './AppShell';
import type { CurrentUser } from '../../lib/auth/AuthProvider';
/**
 * The mockup draws a different sidebar per role, a hamburger that really
 * collapses the rail, and no notification badge (there is no notification
 * source on the server). Those are the claims under test here — the data
 * screens themselves are covered by their own files.
 */

let user: CurrentUser | null;
let permissions: string[];

vi.mock('../../lib/auth/AuthProvider', () => ({
  useAuth: () => ({
    user,
    signOut: vi.fn(),
    hasPermission: (policy: string) => permissions.includes(policy),
  }),
}));

function renderShell() {
  return render(
    <MemoryRouter initialEntries={['/buildings']}>
      <AppShell />
    </MemoryRouter>,
  );
}

function navLabels(): string[] {
  const nav = screen.getByRole('navigation');
  return within(nav)
    .getAllByRole('listitem')
    .map((node) => node.textContent ?? '');
}

beforeEach(() => {
  // jsdom reports every media query as unmatched, which would put the shell in
  // its phone mode; the rail is the desktop behaviour under test here.
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query.includes('min-width'),
    media: query,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  }));

  user = {
    id: 'u1',
    userName: 'owner',
    name: 'Chủ nhà',
    email: 'owner@example.com',
    roles: ['Owner'],
  } as CurrentUser;
  permissions = [];
});

describe('AppShell', () => {
  it('shows the administration sidebar to a user who manages buildings', () => {
    permissions = ['BuildingManagement.Buildings.Manage'];
    renderShell();

    expect(screen.getByText('Quản trị viên')).toBeTruthy();
    const labels = navLabels();
    for (const expected of ['Tổng quan', 'Tòa nhà', 'Phòng', 'Khách thuê', 'Hợp đồng']) {
      expect(labels.some((label) => label.includes(expected))).toBe(true);
    }
    expect(labels.join(' ')).not.toContain('Tìm phòng');
  });

  it('shows the tenant sidebar to a user with no management grant', () => {
    permissions = [];
    renderShell();

    expect(screen.getByText('Người thuê')).toBeTruthy();
    const labels = navLabels();
    for (const expected of ['Trang chủ', 'Tìm phòng', 'Tòa nhà', 'Tiện ích', 'Thanh toán']) {
      expect(labels.some((label) => label.includes(expected))).toBe(true);
    }
    expect(labels.join(' ')).not.toContain('Thu chi');
  });

  it('renders unbuilt modules as inert rows, never as links', () => {
    permissions = ['BuildingManagement.Buildings.Manage'];
    renderShell();

    const nav = screen.getByRole('navigation');
    const invoices = within(nav).getByText('Hóa đơn').closest('li') as HTMLElement;

    expect(within(invoices).queryByRole('link')).toBeNull();
    expect(invoices.textContent).toContain('Hóa đơn');
  });

  it('collapses and expands the sidebar with the hamburger', () => {
    permissions = ['BuildingManagement.Buildings.Manage'];
    renderShell();

    const toggle = screen.getByRole('button', { name: 'Thu gọn menu' });
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(document.querySelector('aside')?.className).toContain('lg:w-[238px]');

    fireEvent.click(toggle);

    expect(screen.getByRole('button', { name: 'Mở menu' }).getAttribute('aria-expanded')).toBe(
      'false',
    );
    // Rail mode: the sidebar keeps its icons but shrinks to a narrow strip.
    expect(document.querySelector('aside')?.className).toContain('lg:w-[76px]');

    fireEvent.click(screen.getByRole('button', { name: 'Mở menu' }));
    expect(document.querySelector('aside')?.className).toContain('lg:w-[238px]');
  });

  it('shows no notification badge and does not fake a search', () => {
    renderShell();

    const bell = screen.getByRole('button', { name: 'Thông báo' });
    expect(bell.textContent?.trim()).toBe('notifications');

    const search = screen.getByRole('searchbox');
    expect((search as HTMLInputElement).disabled).toBe(true);
  });
});
