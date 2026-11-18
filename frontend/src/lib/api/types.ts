/**
 * Mirrors the server DTOs in
 * modules/buildingmanagement/src/BuildingManagement.Application.Contracts.
 * Field names are the camel-cased JSON the ABP conventional controllers emit.
 */

export interface AuditedEntity {
  id: string;
  creationTime: string;
  creatorId: string | null;
  lastModificationTime: string | null;
  lastModifierId: string | null;
}

export interface BuildingDto extends AuditedEntity {
  name: string;
  address: string;
  description: string | null;
  /** True when the server holds a cover image for this building. */
  hasPhoto: boolean;
}

export interface BuildingPhotoDto extends AuditedEntity {
  buildingId: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
}

export interface BuildingPhotoFileDto {
  /** Base64, no data-URL prefix. */
  content: string;
  contentType: string;
  fileName: string;
}

export interface UploadBuildingPhotoInput {
  base64Content: string;
  fileName: string | null;
}

export interface CreateBuildingInput {
  name: string;
  address: string;
  description: string | null;
}

export type UpdateBuildingInput = CreateBuildingInput;

export interface FloorDto extends AuditedEntity {
  buildingId: string;
  floorNumber: number;
  name: string;
  description: string | null;
  status: FloorStatus;
}

export interface CreateFloorInput {
  buildingId: string;
  floorNumber: number;
  name: string;
  description: string | null;
  status: FloorStatus;
}

export interface UpdateFloorInput {
  floorNumber: number;
  name: string;
  description: string | null;
  /** Omit to leave the stored status unchanged. */
  status?: FloorStatus;
}

/** Server enum: Available=1, Occupied=2, Reserved=3, Maintenance=4, Inactive=5. */
export type RoomStatus = 1 | 2 | 3 | 4 | 5;

/** Server enum: Active=1, Maintenance=2, Inactive=3. */
export type FloorStatus = 1 | 2 | 3;

/** Server enum: Rectangle=1, Polygon=2. */
export type RoomLayoutShape = 1 | 2;

/**
 * Server enum, stored as its integer: North=1, NorthEast=2, East=3,
 * SouthEast=4, South=5, SouthWest=6, West=7, NorthWest=8.
 */
export type RoomDirection = 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8;

export const roomDirectionLabels: Record<RoomDirection, string> = {
  1: 'Bắc',
  2: 'Đông Bắc',
  3: 'Đông',
  4: 'Đông Nam',
  5: 'Nam',
  6: 'Tây Nam',
  7: 'Tây',
  8: 'Tây Bắc',
};

/** Server enum: Good=1, NeedsRepair=2, Broken=3. */
export type RoomEquipmentCondition = 1 | 2 | 3;

/**
 * Server enum: Corridor=1, Staircase=2, Elevator=3, ServiceArea=4, Restroom=5,
 * Entrance=6, Label=7, Laundry=8, Storage=9, Balcony=10, Tree=11, Wall=12,
 * Bed=13, Wardrobe=14, Desk=15, Chair=16, Sofa=17, AirConditioner=18,
 * Kitchen=19, Refrigerator=20, Television=21.
 */
export type FloorLayoutItemKind = 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 | 13 | 14 | 15 | 16 | 17 | 18 | 19 | 20 | 21;

export interface RoomDto extends AuditedEntity {
  floorId: string;
  roomNumber: string;
  name: string;
  area: number | null;
  capacity: number;
  status: RoomStatus;
  description: string | null;
  /** Free text, e.g. "Phòng tiêu chuẩn". Null when the owner has not set it. */
  roomType: string | null;
  direction: RoomDirection | null;
  monthlyRent: number | null;
  layoutX: number | null;
  layoutY: number | null;
  layoutWidth: number | null;
  layoutHeight: number | null;
  layoutShape: RoomLayoutShape | null;
  layoutRotationDegrees: number | null;
  layoutPoints: string | null;
  equipmentCount: number;
  photoCount: number;
}

export interface CreateRoomInput {
  floorId: string;
  roomNumber: string;
  name: string;
  area: number | null;
  capacity: number;
  status: RoomStatus;
  description: string | null;
  roomType: string | null;
  direction: RoomDirection | null;
}

export interface UpdateRoomInput {
  roomNumber: string;
  name: string;
  area: number | null;
  capacity: number;
  status: RoomStatus;
  description: string | null;
  roomType: string | null;
  direction: RoomDirection | null;
}

/** Server enum: Prohibition=1, QuietHours=2, Hygiene=3, General=4. */
export type HouseRuleIcon = 1 | 2 | 3 | 4;

export interface HouseRuleItem {
  icon: HouseRuleIcon;
  text: string;
}

/**
 * Prices and rules the owner publishes on the room screen. Distinct from the
 * billing tariff: editing this never changes what a tenant is charged.
 */
export interface BuildingPublicInfoDto {
  buildingId: string;
  electricityUnitPrice: number | null;
  waterUnitPrice: number | null;
  serviceFee: number | null;
  otherInfo: string | null;
  houseRules: HouseRuleItem[];
}

export interface UpdateBuildingPublicInfoInput {
  electricityUnitPrice: number | null;
  waterUnitPrice: number | null;
  serviceFee: number | null;
  otherInfo: string | null;
  houseRules: HouseRuleItem[];
}

export interface RoomEquipmentDto extends AuditedEntity {
  roomId: string;
  name: string;
  category: string | null;
  quantity: number;
  condition: RoomEquipmentCondition;
  installedDate: string | null;
  notes: string | null;
}

export interface CreateRoomEquipmentInput {
  name: string;
  category: string | null;
  quantity: number;
  condition: RoomEquipmentCondition;
  installedDate: string | null;
  notes: string | null;
}

export type UpdateRoomEquipmentInput = CreateRoomEquipmentInput;

/** Metadata only; the bytes live behind `/photo-content/{photoId}`. */
export interface RoomPhotoDto extends AuditedEntity {
  roomId: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  displayOrder: number;
}

export interface RoomPhotoFileDto {
  /** Base64, no data-URL prefix. */
  content: string;
  contentType: string;
  fileName: string;
}

export interface UploadRoomPhotoInput {
  base64Content: string;
  fileName: string | null;
}

export interface UpdateRoomLayoutInput {
  x: number | null;
  y: number | null;
  width: number | null;
  height: number | null;
  shape: RoomLayoutShape | null;
  rotationDegrees: number | null;
  points: string | null;
}

export interface FloorLayoutItemDto extends AuditedEntity {
  floorId: string;
  kind: FloorLayoutItemKind;
  label: string | null;
  x: number;
  y: number;
  width: number;
  height: number;
  /** JSON `[{x,y},…]` centreline; only walls carry one. */
  points: string | null;
  thickness: number | null;
  cornerRadius: number | null;
}

export interface CreateFloorLayoutItemInput {
  kind: FloorLayoutItemKind;
  label: string | null;
  x: number;
  y: number;
  width: number;
  height: number;
  points?: string | null;
  thickness?: number | null;
  cornerRadius?: number | null;
}

export type UpdateFloorLayoutItemInput = CreateFloorLayoutItemInput;

export const roomStatusMeta: Record<RoomStatus, { label: string; tone: 'success' | 'error' | 'warning' | 'neutral' }> = {
  1: { label: 'Trống', tone: 'success' },
  2: { label: 'Đang thuê', tone: 'error' },
  3: { label: 'Đã đặt', tone: 'warning' },
  4: { label: 'Bảo trì', tone: 'warning' },
  5: { label: 'Ngừng dùng', tone: 'neutral' },
};

export const floorStatusMeta: Record<
  FloorStatus,
  { label: string; tone: 'success' | 'warning' | 'neutral' }
> = {
  1: { label: 'Hoạt động', tone: 'success' },
  2: { label: 'Bảo trì', tone: 'warning' },
  3: { label: 'Ngừng dùng', tone: 'neutral' },
};

export const equipmentConditionMeta: Record<
  RoomEquipmentCondition,
  { label: string; tone: 'success' | 'warning' | 'error' }
> = {
  1: { label: 'Tốt', tone: 'success' },
  2: { label: 'Cần sửa', tone: 'warning' },
  3: { label: 'Hỏng', tone: 'error' },
};

export const floorLayoutKindMeta: Record<FloorLayoutItemKind, { label: string; icon: string }> = {
  1: { label: 'Hành lang', icon: 'view_week' },
  2: { label: 'Cầu thang', icon: 'stairs' },
  3: { label: 'Thang máy', icon: 'elevator' },
  4: { label: 'Khu dịch vụ', icon: 'local_laundry_service' },
  5: { label: 'Nhà vệ sinh', icon: 'wc' },
  6: { label: 'Cửa ra vào', icon: 'door_front' },
  7: { label: 'Nhãn chú thích', icon: 'label' },
  8: { label: 'Phòng giặt', icon: 'local_laundry_service' },
  9: { label: 'Kho', icon: 'inventory_2' },
  10: { label: 'Ban công', icon: 'balcony' },
  11: { label: 'Cây xanh', icon: 'park' },
  12: { label: 'Tường', icon: 'horizontal_rule' },
  13: { label: 'Giường', icon: 'bed' },
  14: { label: 'Tủ quần áo', icon: 'checkroom' },
  15: { label: 'Bàn làm việc', icon: 'desk' },
  16: { label: 'Ghế', icon: 'chair' },
  17: { label: 'Sofa', icon: 'weekend' },
  18: { label: 'Điều hoà', icon: 'ac_unit' },
  19: { label: 'Bếp', icon: 'countertops' },
  20: { label: 'Tủ lạnh', icon: 'kitchen' },
  21: { label: 'Tivi', icon: 'tv' },
};

/** True for the kinds that stand inside a room rather than forming the floor. */
export function isFurniture(kind: FloorLayoutItemKind): boolean {
  return floorLayoutKindGroup[kind] === 'furniture';
}

/**
 * The palette groups kinds so the designer lists architecture and furniture
 * separately: a wall is not furniture, and mixing them made the sixteen-item
 * list hard to scan.
 */
export const floorLayoutKindGroup: Record<FloorLayoutItemKind, 'architecture' | 'furniture'> = {
  1: 'architecture',
  2: 'architecture',
  3: 'architecture',
  4: 'architecture',
  5: 'architecture',
  6: 'architecture',
  7: 'architecture',
  8: 'architecture',
  9: 'architecture',
  10: 'architecture',
  11: 'architecture',
  12: 'architecture',
  13: 'furniture',
  14: 'furniture',
  15: 'furniture',
  16: 'furniture',
  17: 'furniture',
  18: 'furniture',
  19: 'furniture',
  20: 'furniture',
  21: 'furniture',
};

