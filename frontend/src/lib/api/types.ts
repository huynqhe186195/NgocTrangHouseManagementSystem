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

export interface RoomDto extends AuditedEntity {
  floorId: string;
  roomNumber: string;
  name: string;
  area: number | null;
  capacity: number;
  status: RoomStatus;
  description: string | null;
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

export const roomStatusMeta: Record<RoomStatus, { label: string; tone: 'success' | 'error' | 'warning' | 'neutral' }> = {
  1: { label: 'Trống', tone: 'success' },
  2: { label: 'Đang thuê', tone: 'error' },
  3: { label: 'Đã đặt', tone: 'warning' },
  4: { label: 'Bảo trì', tone: 'warning' },
  5: { label: 'Ngừng dùng', tone: 'neutral' },
};
