import { describe, expect, it } from 'vitest';
import { ApiError } from '../http';
import { describeMutationError } from './mutationErrors';

function apiError(
  code: string,
  message: string,
  status: number,
  validationErrors: { field: string | null; message: string }[] = [],
): ApiError {
  return new ApiError(code, message, status, validationErrors, 'trace-1');
}

describe('describeMutationError', () => {
  it('returns null for no error', () => {
    expect(describeMutationError(null, 'room')).toBeNull();
  });

  it('lower-cases the DTO property names ABP reports', () => {
    const view = describeMutationError(
      apiError('Volo.Abp.Validation:0001', 'Dữ liệu không hợp lệ.', 400, [
        { field: 'Name', message: 'Tên không được để trống.' },
        { field: 'Address', message: 'Địa chỉ không được để trống.' },
      ]),
      'building',
    );
    expect(view?.fieldErrors).toEqual({
      name: 'Tên không được để trống.',
      address: 'Địa chỉ không được để trống.',
    });
  });

  it('keeps field-less validation errors at form level only', () => {
    const view = describeMutationError(
      apiError('Volo.Abp.Validation:0001', 'Dữ liệu không hợp lệ.', 400, [
        { field: null, message: 'Có lỗi.' },
      ]),
      'building',
    );
    expect(view?.fieldErrors).toEqual({});
    expect(view?.formError).toBe('Dữ liệu không hợp lệ.');
  });

  it('attaches business codes to the right field', () => {
    const view = describeMutationError(
      apiError('BuildingManagement:RoomNumberAlreadyExists', 'Mã phòng đã tồn tại.', 409),
      'room',
    );
    expect(view?.fieldErrors.roomNumber).toBe('Mã phòng đã tồn tại.');
    expect(view?.formError).toBe('Mã phòng đã tồn tại.');
  });

  it('attaches the floor-number duplicate to floorNumber', () => {
    const view = describeMutationError(
      apiError('BuildingManagement:FloorNumberAlreadyExists', 'Số tầng đã tồn tại.', 409),
      'floor',
    );
    expect(view?.fieldErrors.floorNumber).toBe('Số tầng đã tồn tại.');
  });

  it('reports a 403 as an authorization refusal, not a business guard', () => {
    const view = describeMutationError(apiError('HTTP:403', 'Bị từ chối.', 403), 'building');
    expect(view?.formError).toContain('từ chối quyền ghi');
  });

  it('explains a 500 on room/floor create as a reused soft-deleted code', () => {
    const view = describeMutationError(
      apiError('Common:InternalServerError', 'Có lỗi nội bộ.', 500),
      'room',
      'create',
    );
    expect(view?.formError).toContain('đã xoá trước đó');
  });

  it('does not apply the soft-delete wording to building create', () => {
    const view = describeMutationError(
      apiError('Common:InternalServerError', 'Có lỗi nội bộ.', 500),
      'building',
      'create',
    );
    expect(view?.formError).toBe('Có lỗi nội bộ.');
  });

  it('appends a remediation hint to delete guard codes', () => {
    const view = describeMutationError(
      apiError('BuildingManagement:FloorHasRooms', 'Tầng còn phòng.', 409),
      'floor',
      'delete',
    );
    expect(view?.formError).toContain('Tầng còn phòng.');
    expect(view?.formError).toContain('xoá hết các phòng');
  });

  it('passes the server message through untouched', () => {
    const view = describeMutationError(
      apiError('BuildingManagement:RoomHasActiveContract', 'Phòng đang có hợp đồng.', 409),
      'room',
      'delete',
    );
    expect(view?.formError.startsWith('Phòng đang có hợp đồng.')).toBe(true);
  });

  it('keeps the traceId', () => {
    const view = describeMutationError(apiError('X', 'y', 400), 'room');
    expect(view?.traceId).toBe('trace-1');
  });

  it('explains a rejected photo upload in the owner-facing words', () => {
    const view = describeMutationError(
      apiError(
        'BuildingManagement:InvalidRoomPhotoContent',
        'Nội dung tệp không phải ảnh hợp lệ.',
        400,
      ),
      'photo',
      'create',
    );
    expect(view?.formError).toContain('SVG');
    expect(view?.formError).toContain('Nội dung tệp không phải ảnh hợp lệ.');
    expect(view?.fieldErrors.photo).toBe('Nội dung tệp không phải ảnh hợp lệ.');
  });

  it('reports an oversized photo', () => {
    const view = describeMutationError(
      apiError('BuildingManagement:RoomPhotoTooLarge', 'Tệp quá lớn.', 400),
      'photo',
      'create',
    );
    expect(view?.formError).toContain('5 MB');
  });

  it('does not treat an equipment create failure as a reused room code', () => {
    const view = describeMutationError(
      apiError('Common:InternalServerError', 'Có lỗi nội bộ.', 500),
      'equipment',
      'create',
    );
    expect(view?.formError).toBe('Có lỗi nội bộ.');
  });

  it('maps an invalid layout payload to the layout field', () => {
    const view = describeMutationError(
      apiError('BuildingManagement:InvalidRoomLayout', 'Toạ độ sai.', 400),
      'layout',
      'edit',
    );
    expect(view?.fieldErrors.layout).toBe('Toạ độ sai.');
    expect(view?.formError).toContain('X/Y/rộng/cao');
  });

  it('maps a missing equipment row to the equipmentId-free message', () => {
    const view = describeMutationError(
      apiError('BuildingManagement:RoomEquipmentNotFound', 'Không tìm thấy.', 404),
      'equipment',
      'delete',
    );
    expect(view?.formError).toContain('thiết bị');
  });
});
