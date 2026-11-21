import { describe, expect, it, vi, beforeEach } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { ReportDesignPreviewPage } from './ReportDesignPreviewPage';

function renderPage(initialState?: Parameters<typeof ReportDesignPreviewPage>[0]['initialState']) {
  return render(
    <MemoryRouter>
      <ReportDesignPreviewPage initialState={initialState} />
    </MemoryRouter>,
  );
}

describe('ReportDesignPreviewPage', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it('marks the screen as a design-only report and renders safe inventory facts', () => {
    renderPage();

    expect(screen.getByRole('heading', { name: 'Báo cáo' })).toBeTruthy();
    expect(screen.getByText('Bản thiết kế UI · Chưa tích hợp API · Dữ liệu minh họa')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Tổng quan theo tòa nhà' })).toBeTruthy();
    expect(screen.getAllByText('Tòa mẫu A').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Kỳ minh họa 11/2026').length).toBeGreaterThan(0);
    expect(screen.queryByText(/\d[\d.,]*\s*(?:₫|VND)/i)).toBeNull();
  });

  it('filters local fixture rows without calling an API', () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    renderPage();

    fireEvent.change(screen.getByRole('searchbox', { name: 'Tìm báo cáo minh họa' }), {
      target: { value: 'Tòa mẫu C' },
    });

    const table = screen.getByRole('table');
    expect(within(table).getByText('Tòa mẫu C')).toBeTruthy();
    expect(within(table).queryByText('Tòa mẫu A')).toBeNull();
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('filters by building and report group while keeping the disclaimer', () => {
    renderPage();

    fireEvent.change(screen.getByRole('combobox', { name: 'Lọc tòa nhà báo cáo' }), {
      target: { value: 'Tòa mẫu B' },
    });
    fireEvent.change(screen.getByRole('combobox', { name: 'Lọc nhóm báo cáo' }), {
      target: { value: 'contracts' },
    });

    const table = screen.getByRole('table');
    expect(within(table).getByText('Tòa mẫu B')).toBeTruthy();
    expect(within(table).queryByText('Tòa mẫu A')).toBeNull();
    expect(screen.getByText('Bản thiết kế UI · Chưa tích hợp API · Dữ liệu minh họa')).toBeTruthy();
  });

  it('opens a local summary without exposing financial or mutation controls', () => {
    renderPage();

    const table = screen.getByRole('table');
    fireEvent.click(within(table).getAllByRole('button', { name: 'Xem tóm tắt' })[0]!);

    expect(screen.getByRole('heading', { name: 'Tòa mẫu A' })).toBeTruthy();
    expect(screen.getByText('Fixture UI cô lập')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /xuất|thu tiền|phát hành|duyệt|đối soát|chỉnh sửa/i })).toBeNull();
  });

  it.each([
    ['loading', 'Đang tải bản thiết kế báo cáo…'],
    ['empty', 'Chưa có dữ liệu minh họa'],
    ['forbidden', 'Máy chủ chưa mở quyền xem báo cáo (403).'],
    ['error', 'Không tải được dữ liệu báo cáo trong preview.'],
  ] as const)('renders the %s state separately', (state, expected) => {
    renderPage(state);

    if (state === 'forbidden' || state === 'error') {
      expect(screen.getByRole('alert').textContent).toContain(expected.replace(/\.$/, ''));
    } else {
      expect(screen.getByText(expected)).toBeTruthy();
    }
    expect(screen.queryByRole('heading', { name: 'Tổng quan theo tòa nhà' })).toBeNull();
  });
});
