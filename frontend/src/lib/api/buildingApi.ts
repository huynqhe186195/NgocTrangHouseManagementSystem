import { request } from '../http';
import type {
  BuildingDto,
  CreateBuildingInput,
  CreateFloorInput,
  CreateRoomInput,
  FloorDto,
  RoomDto,
  UpdateBuildingInput,
  UpdateFloorInput,
  UpdateRoomInput,
} from './types';

/**
 * ABP conventional controllers generated in the host:
 *   api/building-management/building|floor|room
 * Each GetList returns a plain array (no paging on the server today).
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
};

export const floorsApi = {
  list: () => request<FloorDto[]>(`${base}/floor`),
  get: (id: string) => request<FloorDto>(`${base}/floor/${id}`),
  create: (input: CreateFloorInput) =>
    request<FloorDto>(`${base}/floor`, { method: 'POST', body: input }),
  update: (id: string, input: UpdateFloorInput) =>
    request<FloorDto>(`${base}/floor/${id}`, { method: 'PUT', body: input }),
  remove: (id: string) => request<void>(`${base}/floor/${id}`, { method: 'DELETE' }),
};

export const roomsApi = {
  list: () => request<RoomDto[]>(`${base}/room`),
  get: (id: string) => request<RoomDto>(`${base}/room/${id}`),
  create: (input: CreateRoomInput) =>
    request<RoomDto>(`${base}/room`, { method: 'POST', body: input }),
  update: (id: string, input: UpdateRoomInput) =>
    request<RoomDto>(`${base}/room/${id}`, { method: 'PUT', body: input }),
  remove: (id: string) => request<void>(`${base}/room/${id}`, { method: 'DELETE' }),
};
