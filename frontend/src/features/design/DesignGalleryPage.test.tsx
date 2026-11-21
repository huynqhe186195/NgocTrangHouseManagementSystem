import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { DesignGalleryPage } from './DesignGalleryPage';

function renderPage(initialEntry = '/__design/figma') {
  return render(
    <MemoryRouter initialEntries={[initialEntry]}>
      <DesignGalleryPage />
    </MemoryRouter>,
  );
}

describe('DesignGalleryPage', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it('renders the design-only boundary and all five Owner / Staff frames', () => {
    renderPage();

    expect(screen.getByText('Bản thiết kế UI · Chưa tích hợp API · Dữ liệu minh họa')).toBeTruthy();
    expect(screen.getAllByRole('heading', { name: 'Quản lý thu chi' }).length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: /07 · Thu chi/ })).toBeTruthy();
    expect(screen.getByRole('button', { name: /08 · Đặt phòng/ })).toBeTruthy();
    expect(screen.getByRole('button', { name: /09 · Người dùng/ })).toBeTruthy();
    expect(screen.getByRole('button', { name: /10 · Hợp đồng/ })).toBeTruthy();
    expect(screen.getByRole('button', { name: /11 · Điện nước/ })).toBeTruthy();
  });

  it('switches to all ten desktop Tenant frames and keeps mobile for Flutter scope', () => {
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Tenant' }));

    const navigation = screen.getByRole('navigation', { name: 'Danh sách frame' });
    expect(within(navigation).getAllByRole('button')).toHaveLength(10);
    expect(screen.getAllByRole('heading', { name: 'Tổng quan' }).length).toBeGreaterThan(0);
    expect(within(navigation).getByRole('button', { name: /TEN-10/ })).toBeTruthy();
    expect(within(navigation).queryByRole('button', { name: /TEN-M01/ })).toBeNull();
    expect(within(navigation).queryByRole('button', { name: /TEN-M02/ })).toBeNull();
    expect(within(navigation).queryByRole('button', { name: /TEN-M03/ })).toBeNull();

    fireEvent.click(within(navigation).getByRole('button', { name: /TEN-07/ }));
    expect(screen.getAllByRole('heading', { name: 'Thanh toán' }).length).toBeGreaterThan(0);
    expect(screen.getByText(/Không có QR hoặc tài khoản nhận tiền mặc định/)).toBeTruthy();
  });

  it('filters only local fixture rows without making an API request', () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: /08 · Đặt phòng/ }));
    fireEvent.change(screen.getByRole('searchbox', { name: 'Tìm trong bản thiết kế' }), {
      target: { value: 'B104' },
    });

    const table = screen.getByRole('table');
    expect(within(table).getByText('B104')).toBeTruthy();
    expect(within(table).queryByText('A207')).toBeNull();
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('keeps forms local and disables dangerous actions', () => {
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: /11 · Điện nước/ }));
    expect(screen.getByRole('button', { name: /Lưu · chưa mở/ })).toHaveProperty('disabled', true);
    expect(screen.getByText('Nhập liệu chỉ sống trong preview, không submit.')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Tenant' }));
    fireEvent.click(within(screen.getByRole('navigation', { name: 'Danh sách frame' })).getByRole('button', { name: /TEN-08/ }));
    expect(screen.getByRole('button', { name: /Gửi yêu cầu · chưa mở/ })).toHaveProperty('disabled', true);
    expect(screen.getByText('Yêu cầu trả phòng chỉ khởi tạo quy trình:')).toBeTruthy();
  });

  it.each([
    ['loading', 'Đang tải bản thiết kế minh họa…'],
    ['empty', 'Chưa có dữ liệu minh họa'],
    ['forbidden', 'Máy chủ chưa mở quyền cho flow này (403).'],
    ['error', 'Không tải được dữ liệu trong preview.'],
  ] as const)('renders the %s state without the fixture surface', (state, expected) => {
    renderPage(`/__design/figma?state=${state}`);

    if (state === 'forbidden' || state === 'error') {
      expect(screen.getByRole(state === 'forbidden' ? 'status' : 'alert').textContent).toContain(expected.replace(/\.$/, ''));
    } else {
      expect(screen.getByText(expected)).toBeTruthy();
    }
    expect(screen.queryByRole('heading', { name: 'Tổng quan thuê phòng' })).toBeNull();
  });

  it('renders the web Tenant desktop surfaces without generic fallback', () => {
    renderPage('/__design/figma?group=tenant&screen=TEN-03');
    expect(screen.getByRole('heading', { name: 'Thông tin hợp đồng' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /TEN-05/ }));
    expect(screen.getByRole('heading', { name: 'Chi tiết các khoản' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /TEN-06/ }));
    expect(screen.getByRole('heading', { name: 'Điện năng tiêu thụ' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /TEN-08/ }));
    expect(screen.getByRole('heading', { name: 'Tạo yêu cầu trả phòng' })).toBeTruthy();
    expect(screen.getByRole('button', { name: /Gửi yêu cầu · chưa mở/ })).toHaveProperty('disabled', true);

    fireEvent.click(screen.getByRole('button', { name: /TEN-09/ }));
    expect(screen.getByRole('heading', { name: 'Phòng phù hợp với bạn' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /TEN-10/ }));
    expect(screen.getByRole('heading', { name: 'Trung tâm hỗ trợ' })).toBeTruthy();
  });

});
