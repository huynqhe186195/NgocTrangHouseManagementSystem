import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { InvoiceDesignPreviewPage } from './InvoiceDesignPreviewPage';

function renderPage(initialState?: Parameters<typeof InvoiceDesignPreviewPage>[0]['initialState']) {
  return render(
    <MemoryRouter>
      <InvoiceDesignPreviewPage initialState={initialState} />
    </MemoryRouter>,
  );
}

describe('InvoiceDesignPreviewPage', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it('marks the screen as a Figma-only design and renders the admin listing facts', () => {
    renderPage();

    expect(screen.getByRole('heading', { name: 'Hóa đơn' })).toBeTruthy();
    expect(screen.getByText('Bản thiết kế UI · Chưa tích hợp API · Dữ liệu minh họa')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Danh sách hóa đơn' })).toBeTruthy();
    expect(screen.getAllByText('MẪU-A101-01').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Tòa mẫu A').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Người thuê minh họa').length).toBeGreaterThan(0);
  });

  it('filters the local fixture without making an API request', () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    renderPage();

    fireEvent.change(screen.getByRole('searchbox', { name: 'Tìm hóa đơn minh họa' }), {
      target: { value: 'B301' },
    });

    expect(screen.getAllByText('MẪU-B301-04').length).toBeGreaterThan(0);
    expect(screen.queryByText('MẪU-A101-01')).toBeNull();
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('filters by status and keeps the preview label visible', () => {
    renderPage();

    fireEvent.change(screen.getByRole('combobox', { name: 'Lọc trạng thái hóa đơn' }), {
      target: { value: 'paid' },
    });

    expect(screen.getAllByText('MẪU-A203-02').length).toBeGreaterThan(0);
    expect(screen.queryByText('MẪU-A101-01')).toBeNull();
    expect(screen.getByText('Bản thiết kế UI · Chưa tích hợp API · Dữ liệu minh họa')).toBeTruthy();
  });

  it('opens a local detail panel without exposing financial mutation controls', () => {
    renderPage();

    const table = screen.getByRole('table');
    fireEvent.click(within(table).getAllByRole('button', { name: 'Xem chi tiết' })[0]!);

    expect(screen.getByRole('heading', { name: 'MẪU-A101-01' })).toBeTruthy();
    expect(screen.getByText('Fixture UI cô lập')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /phát hành|ghi nhận thanh toán|hoàn tiền|thu hồi/i })).toBeNull();
  });

  it.each([
    ['loading', 'Đang tải bản thiết kế hóa đơn…'],
    ['empty', 'Chưa có dữ liệu minh họa'],
    ['forbidden', 'Máy chủ đang từ chối quyền xem hóa đơn (403).'],
    ['error', 'Không tải được dữ liệu hóa đơn trong preview.'],
  ] as const)('renders the %s state separately', (state, expected) => {
    renderPage(state);

    if (state === 'forbidden' || state === 'error') {
      expect(screen.getByRole('alert').textContent).toContain(expected.replace(/\.$/, ''));
    } else {
      expect(screen.getByText(expected)).toBeTruthy();
    }
    expect(screen.queryByRole('heading', { name: 'Danh sách hóa đơn' })).toBeNull();
  });
});
