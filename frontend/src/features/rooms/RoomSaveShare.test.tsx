import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { RoomSaveShare } from './RoomSaveShare';

describe('RoomSaveShare', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('says plainly that saving stays in this browser', () => {
    render(<RoomSaveShare roomId="r1" roomNumber="A101" />);

    expect(screen.getByText(/không đồng bộ vào tài khoản/)).toBeTruthy();
    expect(screen.getByRole('button', { name: /^Lưu$/ })).toBeTruthy();
  });

  it('toggles the browser-local saved state and persists it', () => {
    render(<RoomSaveShare roomId="r1" roomNumber="A101" />);

    const save = screen.getByRole('button', { name: /^Lưu$/ });
    fireEvent.click(save);

    expect(screen.getByRole('button', { name: /Đã lưu/ })).toBeTruthy();
    expect(JSON.parse(localStorage.getItem('ngoctrang.savedRooms') ?? '[]')).toEqual(['r1']);

    fireEvent.click(screen.getByRole('button', { name: /Đã lưu/ }));
    expect(JSON.parse(localStorage.getItem('ngoctrang.savedRooms') ?? '[]')).toEqual([]);
  });

  it('copies the room link when the platform share sheet is unavailable', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.assign(navigator, { clipboard: { writeText } });
    // jsdom ships no navigator.share, which is the case this test covers.
    Object.assign(navigator, { share: undefined });

    render(<RoomSaveShare roomId="room-42" roomNumber="A101" />);
    fireEvent.click(screen.getByRole('button', { name: /Chia sẻ/ }));

    await waitFor(() => expect(writeText).toHaveBeenCalledTimes(1));
    expect(writeText.mock.calls[0]![0]).toBe(`${window.location.origin}/rooms/room-42`);
    await waitFor(() => expect(screen.getByRole('status').textContent).toMatch(/sao chép/));
  });

  it('reports the link instead of claiming success when the clipboard is blocked', async () => {
    Object.assign(navigator, {
      clipboard: { writeText: vi.fn().mockRejectedValue(new Error('blocked')) },
    });
    Object.assign(navigator, { share: undefined });

    render(<RoomSaveShare roomId="room-42" roomNumber="A101" />);
    fireEvent.click(screen.getByRole('button', { name: /Chia sẻ/ }));

    await waitFor(() =>
      expect(screen.getByRole('status').textContent).toMatch(/rooms\/room-42/),
    );
  });
});
