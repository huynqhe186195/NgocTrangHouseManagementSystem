import { ApiError } from '../http';

export type MutationEntity =
  | 'building'
  | 'floor'
  | 'room'
  | 'equipment'
  | 'photo'
  | 'layout'
  | 'rent';

export interface MutationErrorView {
  /** Per-field messages keyed by the camelCase DTO property the form uses. */
  fieldErrors: Record<string, string>;
  /** Message for the form-level Alert; always non-empty when there is an error. */
  formError: string;
  traceId: string | null;
}

const CODE_TO_FIELD: Record<string, string> = {
  'BuildingManagement:FloorNumberAlreadyExists': 'floorNumber',
  'BuildingManagement:RoomNumberAlreadyExists': 'roomNumber',
  'BuildingManagement:RoomNumberDoesNotMatchFloor': 'roomNumber',
  'BuildingManagement:InvalidRoomNumber': 'roomNumber',
  'BuildingManagement:InvalidRoomEquipmentQuantity': 'quantity',
  'BuildingManagement:InvalidMonthlyRentAmount': 'monthlyRent',
  'BuildingManagement:InvalidRoomLayout': 'layout',
  'BuildingManagement:InvalidFloorLayoutItem': 'layout',
  'BuildingManagement:InvalidRoomPhotoContent': 'photo',
};

const DELETE_GUARD_HINTS: Record<string, string> = {
  'BuildingManagement:BuildingHasFloors': 'Hãy xoá hết các tầng của tòa nhà trước.',
  'BuildingManagement:FloorHasRooms': 'Hãy xoá hết các phòng của tầng trước.',
  'BuildingManagement:RoomHasActiveContract': 'Kết thúc hợp đồng đang hoạt động rồi thử lại.',
  'BuildingManagement:RoomHasActiveUtilityMeter':
    'Ngừng hoạt động đồng hồ điện/nước của phòng rồi thử lại.',
};

/**
 * Codes the server rejects with a plain-language explanation of its own, but
 * where the raw message ("Có một lỗi nội bộ xảy ra…" is the ABP envelope
 * text) is less useful than what the screen can say.
 */
const CODE_MESSAGES: Record<string, string> = {
  'BuildingManagement:InvalidRoomPhotoContent':
    'Tệp không phải ảnh PNG, JPEG hoặc WebP hợp lệ. Ảnh SVG và tệp đổi đuôi không được chấp nhận.',
  'BuildingManagement:RoomPhotoTooLarge': 'Ảnh vượt quá giới hạn 5 MB.',
  'BuildingManagement:InvalidRoomLayout':
    'Toạ độ sơ đồ không hợp lệ: cần đủ X/Y/rộng/cao trong khoảng cho phép.',
  'BuildingManagement:InvalidMonthlyRentAmount': 'Giá thuê không hợp lệ.',
  'BuildingManagement:InvalidFloorLayoutItem': 'Vật thể sơ đồ không hợp lệ.',
  'BuildingManagement:RoomPhotoNotFound': 'Không tìm thấy ảnh này.',
  'BuildingManagement:RoomEquipmentNotFound': 'Không tìm thấy thiết bị này.',
  'BuildingManagement:FloorLayoutItemNotFound': 'Không tìm thấy vật thể sơ đồ này.',
};

/** Entities whose create failures can be the soft-deleted-index 500. */
const RENAMEABLE_CREATE_ENTITIES: MutationEntity[] = ['floor', 'room'];

const FORBIDDEN_MESSAGE = 'Máy chủ từ chối quyền ghi trên tài nguyên này.';

/**
 * A soft-deleted row still occupies the unfiltered unique index, so re-creating
 * the same room/floor number fails at the DB with a 500 rather than the 409 the
 * application check would have produced. Until the index is filtered this is the
 * only honest explanation to show.
 */
const REUSED_SOFT_DELETED_CODE_MESSAGE =
  'Không tạo được: mã này có thể vẫn thuộc về một bản ghi đã xoá trước đó. Hãy chọn mã khác.';

function toCamelCase(key: string): string {
  return key.length === 0 ? key : key.charAt(0).toLowerCase() + key.slice(1);
}

export function describeMutationError(
  error: unknown,
  entity: MutationEntity,
  action: 'create' | 'edit' | 'delete' = 'create',
): MutationErrorView | null {
  if (!error) {
    return null;
  }

  if (!(error instanceof ApiError)) {
    return {
      fieldErrors: {},
      formError: 'Đã xảy ra lỗi không xác định. Vui lòng thử lại.',
      traceId: null,
    };
  }

  // ABP reports DataAnnotation failures under the DTO property name (PascalCase),
  // the forms key their inputs in camelCase.
  const fieldErrors: Record<string, string> = {};
  for (const item of error.validationErrors) {
    if (item.field) {
      fieldErrors[toCamelCase(item.field)] = item.message;
    }
  }

  const mappedField = CODE_TO_FIELD[error.code];
  if (mappedField && error.message) {
    fieldErrors[mappedField] = fieldErrors[mappedField] ?? error.message;
  }

  let formError: string;
  if (error.status === 403) {
    formError = FORBIDDEN_MESSAGE;
  } else if (action === 'create' && RENAMEABLE_CREATE_ENTITIES.includes(entity) && error.status >= 500) {
    formError = REUSED_SOFT_DELETED_CODE_MESSAGE;
  } else {
    const hint = DELETE_GUARD_HINTS[error.code];
    const explanation = CODE_MESSAGES[error.code];
    formError = hint
      ? `${error.message} ${hint}`
      : explanation
        ? `${explanation} (${error.message})`
        : error.message;
  }

  return { fieldErrors, formError, traceId: error.traceId };
}
