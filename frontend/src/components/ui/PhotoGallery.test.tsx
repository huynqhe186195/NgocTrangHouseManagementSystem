import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { PhotoGallery } from './PhotoGallery';

const photos = [
  { id: 'p1', src: 'data:image/png;base64,AAA', alt: 'anh-1.png' },
  { id: 'p2', src: 'data:image/png;base64,BBB', alt: 'anh-2.png' },
  { id: 'p3', src: 'data:image/png;base64,CCC', alt: 'anh-3.png' },
];

describe('PhotoGallery', () => {
  beforeEach(() => {
    document.body.style.overflow = '';
  });

  it('moves through photos with the arrows and wraps around', () => {
    render(<PhotoGallery photos={photos} />);

    expect(screen.getByText('1 / 3')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Ảnh tiếp theo' }));
    expect(screen.getByText('2 / 3')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Ảnh trước' }));
    fireEvent.click(screen.getByRole('button', { name: 'Ảnh trước' }));
    expect(screen.getByText('3 / 3')).toBeTruthy();
  });

  it('lists every photo in the strip so none is silently unreachable', () => {
    render(<PhotoGallery photos={photos} />);

    for (const photo of photos) {
      expect(screen.getByRole('button', { name: `Xem ${photo.alt}` })).toBeTruthy();
    }
  });

  it('opens the full-photo view and closes it with Escape', () => {
    render(<PhotoGallery photos={photos} />);

    fireEvent.click(screen.getByRole('button', { name: 'Mở ảnh lớn: anh-1.png' }));
    const dialog = screen.getByRole('dialog');
    expect(dialog).toBeTruthy();

    fireEvent.keyDown(window, { key: 'Escape' });
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('steps forward inside the full-photo view with the right arrow key', () => {
    render(<PhotoGallery photos={photos} />);

    fireEvent.click(screen.getByRole('button', { name: 'Mở ảnh lớn: anh-1.png' }));
    fireEvent.keyDown(window, { key: 'ArrowRight' });

    expect(screen.getByRole('dialog', { name: 'Ảnh lớn 2 trên 3' })).toBeTruthy();
  });

  it('offers no arrows when there is a single photo', () => {
    render(<PhotoGallery photos={[photos[0]!]} />);

    expect(screen.queryByRole('button', { name: 'Ảnh tiếp theo' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Ảnh trước' })).toBeNull();
    expect(screen.getByText('1 / 1')).toBeTruthy();
  });

  it('does not pretend to zoom into an empty gallery', () => {
    render(<PhotoGallery photos={[]} emptyLabel="Phòng chưa có ảnh" />);

    expect(screen.getByText('Phòng chưa có ảnh')).toBeTruthy();
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(screen.queryByText(/\d+ \/ \d+/)).toBeNull();
  });
});

describe('PhotoGallery delete', () => {
  it('passes the active photo id to the delete handler', () => {
    const onDelete = vi.fn();
    render(<PhotoGallery photos={photos} onDelete={onDelete} />);

    fireEvent.click(screen.getByRole('button', { name: 'Xoá anh-1.png' }));
    expect(onDelete).toHaveBeenCalledWith('p1');
  });
});
