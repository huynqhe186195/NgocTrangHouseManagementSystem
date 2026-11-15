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
}

export interface CreateFloorInput {
  buildingId: string;
  floorNumber: number;
  name: string;
  description: string | null;
}

export interface UpdateFloorInput {
  floorNumber: number;
  name: string;
  description: string | null;
}

/** Server enum: Available=1, Occupied=2, Reserved=3, Maintenance=4, Inactive=5. */
export type RoomStatus = 1 | 2 | 3 | 4 | 5;

/** Server enum: Rectangle=1, Polygon=2. */
export type RoomLayoutShape = 1 | 2;

/** Server enum: Good=1, NeedsRepair=2, Broken=3. */
export type RoomEquipmentCondition = 1 | 2 | 3;

/** Server enum: Corridor=1, Staircase=2, Elevator=3, ServiceArea=4, Restroom=5, Entrance=6, Label=7, Laundry=8, Storage=9, Balcony=10, Tree=11. */
export type FloorLayoutItemKind = 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11;

export interface RoomDto extends AuditedEntity {
  floorId: string;
  roomNumber: string;
  name: string;
  area: number | null;
  capacity: number;
  status: RoomStatus;
  description: string | null;
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
}

export interface UpdateRoomInput {
  roomNumber: string;
  name: string;
  area: number | null;
  capacity: number;
  status: RoomStatus;
  description: string | null;
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
}

export interface CreateFloorLayoutItemInput {
  kind: FloorLayoutItemKind;
  label: string | null;
  x: number;
  y: number;
  width: number;
  height: number;
}

export type UpdateFloorLayoutItemInput = CreateFloorLayoutItemInput;

export const roomStatusMeta: Record<RoomStatus, { label: string; tone: 'success' | 'error' | 'warning' | 'neutral' }> = {
  1: { label: 'Trống', tone: 'success' },
  2: { label: 'Đang thuê', tone: 'error' },
  3: { label: 'Đã đặt', tone: 'warning' },
  4: { label: 'Bảo trì', tone: 'warning' },
  5: { label: 'Ngừng dùng', tone: 'neutral' },
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
};

