import { request } from '../http';
import type {
  BuildingDto,
  BuildingPhotoDto,
  BuildingPhotoFileDto,
  BuildingPublicInfoDto,
  ContractDto,
  ContractSummaryDto,
  CreateBuildingInput,
  CreateFloorInput,
  CreateFloorLayoutItemInput,
  CreateRoomEquipmentInput,
  CreateRoomInput,
  FloorDto,
  FloorLayoutItemDto,
  RoomDto,
  RoomEquipmentDto,
  RoomPhotoDto,
  RoomPhotoFileDto,
  TenantSummaryDto,
  UpdateBuildingInput,
  UpdateBuildingPublicInfoInput,
  UpdateFloorInput,
  UpdateFloorLayoutItemInput,
  UpdateRoomEquipmentInput,
  UpdateRoomInput,
  UpdateRoomLayoutInput,
  UploadBuildingPhotoInput,
  UploadRoomPhotoInput,
} from './types';

/**
 * ABP conventional controllers generated in the host:
 *   api/building-management/building|floor|room
 * Each GetList returns a plain array (no paging on the server today).
 *
 * The sub-resource action names determine their route segment, and these
 * were read off the running host rather than guessed (see the P4.5 harness):
 *   GetEquipmentAsync        -> /equipment
 *   CreateEquipmentAsync     -> /equipment
 *   UpdateEquipmentAsync     -> /equipment/{equipmentId}
 *   GetPhotosAsync           -> /photos
 *   UploadPhotoAsync         -> /upload-photo
 *   GetPhotoContentAsync     -> /photo-content/{photoId}
 *   DeletePhotoAsync         -> /photo/{photoId}
 *   UpdateLayoutAsync        -> /layout
 *   UpdateRentAsync          -> /rent
 *   GetLayoutItemsAsync      -> /layout-items
 *   CreateLayoutItemAsync    -> /layout-item
 */
const base = '/api/building-management';

export const buildingsApi = {
  list: () => request<BuildingDto[]>(`${base}/building`),
  get: (id: string) => request<BuildingDto>(`${base}/building/${id}`),
  create: (input: CreateBuildingInput) =>
    request<BuildingDto>(`${base}/building`, { method: 'POST', body: input }),
  update: (id: string, input: UpdateBuildingInput) =>
    request<BuildingDto>(`${base}/building/${id}`, { method: 'PUT', body: input }),
  remove: (id: string) => request<void>(`${base}/building/${id}`, { method: 'DELETE' }),

  /**
   * Route segments come from the method names, matching the room photo
   * convention the P4.5 harness read off the running host:
   *   GetPhotoAsync         -> /photo
   *   UploadPhotoAsync      -> /upload-photo
   *   GetPhotoContentAsync  -> /photo-content
   *   DeletePhotoAsync      -> /photo
   */
  getPhoto: (id: string) => request<BuildingPhotoDto | null>(`${base}/building/${id}/photo`),
  uploadPhoto: (id: string, input: UploadBuildingPhotoInput) =>
    request<BuildingPhotoDto>(`${base}/building/${id}/upload-photo`, {
      method: 'POST',
      body: input,
    }),
  getPhotoContent: (id: string) =>
    request<BuildingPhotoFileDto>(`${base}/building/${id}/photo-content`),
  removePhoto: (id: string) =>
    request<void>(`${base}/building/${id}/photo`, { method: 'DELETE' }),

  /**
   * Conventional-controller segments: GetAsync -> /building-public-info/{id},
   * UpdateAsync -> PUT /building-public-info/{id}. Returns null until the
   * owner publishes anything, so the caller can say "chưa công bố" instead of
   * showing zero.
   */
  getPublicInfo: (buildingId: string) =>
    request<BuildingPublicInfoDto | null>(`${base}/building-public-info/${buildingId}`),
  updatePublicInfo: (buildingId: string, input: UpdateBuildingPublicInfoInput) =>
    request<BuildingPublicInfoDto>(`${base}/building-public-info/${buildingId}`, {
      method: 'PUT',
      body: input,
    }),
};

export const floorsApi = {
  list: () => request<FloorDto[]>(`${base}/floor`),
  get: (id: string) => request<FloorDto>(`${base}/floor/${id}`),
  create: (input: CreateFloorInput) =>
    request<FloorDto>(`${base}/floor`, { method: 'POST', body: input }),
  update: (id: string, input: UpdateFloorInput) =>
    request<FloorDto>(`${base}/floor/${id}`, { method: 'PUT', body: input }),
  remove: (id: string) => request<void>(`${base}/floor/${id}`, { method: 'DELETE' }),

  listLayoutItems: (floorId: string) =>
    request<FloorLayoutItemDto[]>(`${base}/floor/${floorId}/layout-items`),
  createLayoutItem: (floorId: string, input: CreateFloorLayoutItemInput) =>
    request<FloorLayoutItemDto>(`${base}/floor/${floorId}/layout-item`, {
      method: 'POST',
      body: input,
    }),
  updateLayoutItem: (floorId: string, itemId: string, input: UpdateFloorLayoutItemInput) =>
    request<FloorLayoutItemDto>(`${base}/floor/${floorId}/layout-item/${itemId}`, {
      method: 'PUT',
      body: input,
    }),
  removeLayoutItem: (floorId: string, itemId: string) =>
    request<void>(`${base}/floor/${floorId}/layout-item/${itemId}`, { method: 'DELETE' }),
};

export const roomsApi = {
  list: () => request<RoomDto[]>(`${base}/room`),
  get: (id: string) => request<RoomDto>(`${base}/room/${id}`),
  create: (input: CreateRoomInput) =>
    request<RoomDto>(`${base}/room`, { method: 'POST', body: input }),
  update: (id: string, input: UpdateRoomInput) =>
    request<RoomDto>(`${base}/room/${id}`, { method: 'PUT', body: input }),
  remove: (id: string) => request<void>(`${base}/room/${id}`, { method: 'DELETE' }),

  updateRent: (id: string, monthlyRent: number | null) =>
    request<RoomDto>(`${base}/room/${id}/rent`, {
      method: 'PUT',
      body: { monthlyRent },
    }),
  updateLayout: (id: string, input: UpdateRoomLayoutInput) =>
    request<RoomDto>(`${base}/room/${id}/layout`, { method: 'PUT', body: input }),

  listEquipment: (roomId: string) =>
    request<RoomEquipmentDto[]>(`${base}/room/${roomId}/equipment`),
  listContracts: (roomId: string) =>
    request<ContractDto[]>(`${base}/room/${roomId}/contracts`),
  createEquipment: (roomId: string, input: CreateRoomEquipmentInput) =>
    request<RoomEquipmentDto>(`${base}/room/${roomId}/equipment`, {
      method: 'POST',
      body: input,
    }),
  updateEquipment: (roomId: string, equipmentId: string, input: UpdateRoomEquipmentInput) =>
    request<RoomEquipmentDto>(`${base}/room/${roomId}/equipment/${equipmentId}`, {
      method: 'PUT',
      body: input,
    }),
  removeEquipment: (roomId: string, equipmentId: string) =>
    request<void>(`${base}/room/${roomId}/equipment/${equipmentId}`, { method: 'DELETE' }),

  listPhotos: (roomId: string) => request<RoomPhotoDto[]>(`${base}/room/${roomId}/photos`),
  /**
   * The server accepts base64 inside its JSON envelope, not multipart: the
   * conventional controller has no multipart pipeline. `content` comes back
   * base64-encoded for the same reason.
   */
  uploadPhoto: (roomId: string, input: UploadRoomPhotoInput) =>
    request<RoomPhotoDto>(`${base}/room/${roomId}/upload-photo`, {
      method: 'POST',
      body: input,
    }),
  getPhotoContent: (roomId: string, photoId: string) =>
    request<RoomPhotoFileDto>(`${base}/room/${roomId}/photo-content/${photoId}`),
  removePhoto: (roomId: string, photoId: string) =>
    request<void>(`${base}/room/${roomId}/photo/${photoId}`, { method: 'DELETE' }),
};

/**
 * Read-only contract list. Separate from the room's `/room/{id}/contracts`
 * sub-resource: that one is scoped by room, this one by the buildings the
 * caller holds a grant on. Both routes are asserted by APIAUTO17.
 */
export const contractsApi = {
  list: () => request<ContractSummaryDto[]>(`${base}/contracts`),
  get: (id: string) => request<ContractSummaryDto>(`${base}/contracts/${id}`),
};

/**
 * Read-only tenant roster. Separate from the legacy `/tenant` service, which
 * returns every tenant unscoped and also writes; both paths are asserted by
 * APIAUTO18.
 */
export const tenantsApi = {
  list: () => request<TenantSummaryDto[]>(`${base}/tenants`),
  get: (id: string) => request<TenantSummaryDto>(`${base}/tenants/${id}`),
};

/**
 * Read a File into base64 without the `data:...;base64,` prefix the server
 * would reject. FileReader is used instead of `arrayBuffer()` so the same
 * path works in jsdom, where Blob.arrayBuffer is not always available.
 */
export function fileToBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      const result = reader.result;
      if (typeof result !== 'string') {
        reject(new Error('Không đọc được tệp ảnh.'));
        return;
      }
      const comma = result.indexOf(',');
      resolve(comma >= 0 ? result.slice(comma + 1) : result);
    };
    reader.onerror = () => reject(reader.error ?? new Error('Không đọc được tệp ảnh.'));
    reader.readAsDataURL(file);
  });
}

