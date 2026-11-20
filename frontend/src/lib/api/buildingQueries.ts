import { useMutation, useQuery, useQueries, useQueryClient } from '@tanstack/react-query';
import { buildingsApi, contractsApi, floorsApi, operatorMembershipsApi, roomsApi, tenantsApi } from '../api/buildingApi';
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
  OperatorMembershipSummaryDto,
  PagedResultDto,
  UpdateBuildingInput,
  UpdateBuildingPublicInfoInput,
  UpdateFloorInput,
  UpdateFloorLayoutItemInput,
  UpdateRoomEquipmentInput,
  UpdateRoomInput,
  UpdateRoomLayoutInput,
  UploadBuildingPhotoInput,
  UploadRoomPhotoInput,
} from '../api/types';

const keys = {
  buildings: ['buildings'] as const,
  floors: ['floors'] as const,
  rooms: ['rooms'] as const,
  contracts: ['contracts'] as const,
  contract: (contractId: string) => ['contract', contractId] as const,
  tenants: ['tenants'] as const,
  tenant: (tenantId: string) => ['tenant', tenantId] as const,
  operatorMemberships: (buildingId: string | undefined, skipCount: number, maxResultCount: number) =>
    ['operator-memberships', buildingId ?? 'all', skipCount, maxResultCount] as const,
  roomEquipment: (roomId: string) => ['room-equipment', roomId] as const,
  roomContracts: (roomId: string) => ['room-contracts', roomId] as const,
  roomPhotos: (roomId: string) => ['room-photos', roomId] as const,
  roomPhotoContent: (roomId: string, photoId: string) =>
    ['room-photo-content', roomId, photoId] as const,
  floorLayout: (floorId: string) => ['floor-layout', floorId] as const,
  buildingPhotoContent: (buildingId: string) =>
    ['building-photo-content', buildingId] as const,
  buildingPublicInfo: (buildingId: string) => ['building-public-info', buildingId] as const,
};

/**
 * Every screen is assembled from the three flat lists (useBuildingSnapshot
 * joins them client-side), so any write has to refresh all three or the
 * detail page keeps showing stale room counts.
 */
function useInvalidateAll() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: keys.buildings }),
      queryClient.invalidateQueries({ queryKey: keys.floors }),
      queryClient.invalidateQueries({ queryKey: keys.rooms }),
    ]);
}

/**
 * A room write also changes that room's own sub-resources surface (counts on
 * the card, the detail tabs), so the room-scoped keys are refreshed too.
 */
function useInvalidateRoom(roomId: string) {
  const invalidateAll = useInvalidateAll();
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      invalidateAll(),
      queryClient.invalidateQueries({ queryKey: keys.roomEquipment(roomId) }),
      queryClient.invalidateQueries({ queryKey: keys.roomPhotos(roomId) }),
      queryClient.invalidateQueries({ queryKey: ['room-photo-content', roomId] }),
    ]);
}

function useInvalidateFloor(floorId: string) {
  const invalidateAll = useInvalidateAll();
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      invalidateAll(),
      queryClient.invalidateQueries({ queryKey: keys.floorLayout(floorId) }),
    ]);
}

export function useBuildings() {
  return useQuery<BuildingDto[]>({
    queryKey: keys.buildings,
    queryFn: () => buildingsApi.list(),
  });
}

export function useFloors() {
  return useQuery<FloorDto[]>({
    queryKey: keys.floors,
    queryFn: () => floorsApi.list(),
  });
}

export function useRooms() {
  return useQuery<RoomDto[]>({
    queryKey: keys.rooms,
    queryFn: () => roomsApi.list(),
  });
}

export function useCreateBuilding() {
  const invalidate = useInvalidateAll();
  return useMutation<BuildingDto, unknown, CreateBuildingInput>({
    mutationFn: (input) => buildingsApi.create(input),
    onSuccess: invalidate,
  });
}

export function useUpdateBuilding() {
  const invalidate = useInvalidateAll();
  return useMutation<BuildingDto, unknown, { id: string; input: UpdateBuildingInput }>({
    mutationFn: ({ id, input }) => buildingsApi.update(id, input),
    onSuccess: invalidate,
  });
}

export function useDeleteBuilding() {
  const invalidate = useInvalidateAll();
  return useMutation<void, unknown, string>({
    mutationFn: (id) => buildingsApi.remove(id),
    onSuccess: invalidate,
  });
}

/**
 * The cover image bytes. A building has at most one, so there is no photo id
 * in the key. `enabled` lets the screen skip the call when the server already
 * said `hasPhoto` is false.
 */
export function useBuildingPhotoContent(buildingId: string | undefined, enabled = true) {
  return useQuery<BuildingPhotoFileDto>({
    queryKey: keys.buildingPhotoContent(buildingId ?? ''),
    queryFn: () => buildingsApi.getPhotoContent(buildingId as string),
    enabled: Boolean(buildingId) && enabled,
  });
}

export function useUploadBuildingPhoto(buildingId: string) {
  const queryClient = useQueryClient();
  const invalidate = useInvalidateAll();
  return useMutation<BuildingPhotoDto, unknown, UploadBuildingPhotoInput>({
    mutationFn: (input) => buildingsApi.uploadPhoto(buildingId, input),
    onSuccess: () =>
      Promise.all([
        invalidate(),
        queryClient.invalidateQueries({ queryKey: keys.buildingPhotoContent(buildingId) }),
      ]),
  });
}

export function useDeleteBuildingPhoto(buildingId: string) {
  const queryClient = useQueryClient();
  const invalidate = useInvalidateAll();
  return useMutation<void, unknown, void>({
    mutationFn: () => buildingsApi.removePhoto(buildingId),
    onSuccess: () =>
      Promise.all([
        invalidate(),
        queryClient.invalidateQueries({ queryKey: keys.buildingPhotoContent(buildingId) }),
      ]),
  });
}

export function useCreateFloor() {
  const invalidate = useInvalidateAll();
  return useMutation<FloorDto, unknown, CreateFloorInput>({
    mutationFn: (input) => floorsApi.create(input),
    onSuccess: invalidate,
  });
}

export function useUpdateFloor() {
  const invalidate = useInvalidateAll();
  return useMutation<FloorDto, unknown, { id: string; input: UpdateFloorInput }>({
    mutationFn: ({ id, input }) => floorsApi.update(id, input),
    onSuccess: invalidate,
  });
}

export function useDeleteFloor() {
  const invalidate = useInvalidateAll();
  return useMutation<void, unknown, string>({
    mutationFn: (id) => floorsApi.remove(id),
    onSuccess: invalidate,
  });
}

export function useCreateRoom() {
  const invalidate = useInvalidateAll();
  return useMutation<RoomDto, unknown, CreateRoomInput>({
    mutationFn: (input) => roomsApi.create(input),
    onSuccess: invalidate,
  });
}

export function useUpdateRoom() {
  const invalidate = useInvalidateAll();
  return useMutation<RoomDto, unknown, { id: string; input: UpdateRoomInput }>({
    mutationFn: ({ id, input }) => roomsApi.update(id, input),
    onSuccess: invalidate,
  });
}

export function useDeleteRoom() {
  const invalidate = useInvalidateAll();
  return useMutation<void, unknown, string>({
    mutationFn: (id) => roomsApi.remove(id),
    onSuccess: invalidate,
  });
}

export function useBuildingPublicInfo(buildingId: string | undefined) {
  return useQuery<BuildingPublicInfoDto | null>({
    queryKey: keys.buildingPublicInfo(buildingId ?? ''),
    queryFn: () => buildingsApi.getPublicInfo(buildingId!),
    enabled: Boolean(buildingId),
  });
}

export function useUpdateBuildingPublicInfo(buildingId: string) {
  const queryClient = useQueryClient();
  return useMutation<BuildingPublicInfoDto, unknown, UpdateBuildingPublicInfoInput>({
    mutationFn: (input) => buildingsApi.updatePublicInfo(buildingId, input),
    onSuccess: (data) => {
      queryClient.setQueryData(keys.buildingPublicInfo(buildingId), data);
    },
  });
}

export function useUpdateRoomRent(roomId: string) {
  const invalidate = useInvalidateRoom(roomId);
  return useMutation<RoomDto, unknown, number | null>({
    mutationFn: (monthlyRent) => roomsApi.updateRent(roomId, monthlyRent),
    onSuccess: invalidate,
  });
}

export function useUpdateRoomLayout(roomId: string) {
  const invalidate = useInvalidateRoom(roomId);
  return useMutation<RoomDto, unknown, UpdateRoomLayoutInput>({
    mutationFn: (input) => roomsApi.updateLayout(roomId, input),
    onSuccess: invalidate,
  });
}

/**
 * The designer saves a whole floor at once, so the room id arrives with the
 * call instead of being bound when the hook is created. One mutation still
 * refreshes every affected list, exactly like the per-room hook.
 */
export function useSaveRoomLayouts() {
  const invalidateAll = useInvalidateAll();
  return useMutation<RoomDto, unknown, { roomId: string; input: UpdateRoomLayoutInput }>({
    mutationFn: ({ roomId, input }) => roomsApi.updateLayout(roomId, input),
    onSuccess: invalidateAll,
  });
}

export function useRoomEquipment(roomId: string | undefined) {
  return useQuery<RoomEquipmentDto[]>({
    queryKey: keys.roomEquipment(roomId ?? ''),
    queryFn: () => roomsApi.listEquipment(roomId as string),
    enabled: Boolean(roomId),
  });
}

export function useRoomContracts(roomId: string | undefined) {
  return useQuery<ContractDto[]>({
    queryKey: keys.roomContracts(roomId ?? ''),
    queryFn: () => roomsApi.listContracts(roomId as string),
    enabled: Boolean(roomId),
  });
}

export function useContracts() {
  return useQuery<ContractSummaryDto[]>({
    queryKey: keys.contracts,
    queryFn: () => contractsApi.list(),
  });
}

export function useContract(contractId: string | undefined) {
  return useQuery<ContractSummaryDto>({
    queryKey: keys.contract(contractId ?? ''),
    queryFn: () => contractsApi.get(contractId as string),
    enabled: Boolean(contractId),
  });
}

export function useTenants() {
  return useQuery<TenantSummaryDto[]>({
    queryKey: keys.tenants,
    queryFn: () => tenantsApi.list(),
  });
}

export function useTenant(tenantId: string | undefined) {
  return useQuery<TenantSummaryDto>({
    queryKey: keys.tenant(tenantId ?? ''),
    queryFn: () => tenantsApi.get(tenantId as string),
    enabled: Boolean(tenantId),
  });
}

export function useOperatorMemberships(
  buildingId: string | undefined,
  skipCount = 0,
  maxResultCount = 50,
) {
  return useQuery<PagedResultDto<OperatorMembershipSummaryDto>>({
    queryKey: keys.operatorMemberships(buildingId, skipCount, maxResultCount),
    queryFn: () => operatorMembershipsApi.list(buildingId, skipCount, maxResultCount),
  });
}


export function useCreateRoomEquipment(roomId: string) {
  const invalidate = useInvalidateRoom(roomId);
  return useMutation<RoomEquipmentDto, unknown, CreateRoomEquipmentInput>({
    mutationFn: (input) => roomsApi.createEquipment(roomId, input),
    onSuccess: invalidate,
  });
}

export function useUpdateRoomEquipment(roomId: string) {
  const invalidate = useInvalidateRoom(roomId);
  return useMutation<
    RoomEquipmentDto,
    unknown,
    { equipmentId: string; input: UpdateRoomEquipmentInput }
  >({
    mutationFn: ({ equipmentId, input }) =>
      roomsApi.updateEquipment(roomId, equipmentId, input),
    onSuccess: invalidate,
  });
}

export function useDeleteRoomEquipment(roomId: string) {
  const invalidate = useInvalidateRoom(roomId);
  return useMutation<void, unknown, string>({
    mutationFn: (equipmentId) => roomsApi.removeEquipment(roomId, equipmentId),
    onSuccess: invalidate,
  });
}

export function useRoomPhotos(roomId: string | undefined) {
  return useQuery<RoomPhotoDto[]>({
    queryKey: keys.roomPhotos(roomId ?? ''),
    queryFn: () => roomsApi.listPhotos(roomId as string),
    enabled: Boolean(roomId),
  });
}

/** Photo bytes; fetched only for the photos the gallery actually renders. */
export function useRoomPhotoContent(roomId: string, photoId: string, enabled = true) {
  return useQuery<RoomPhotoFileDto>({
    queryKey: keys.roomPhotoContent(roomId, photoId),
    queryFn: () => roomsApi.getPhotoContent(roomId, photoId),
    enabled,
    staleTime: 5 * 60 * 1000,
  });
}

export function useUploadRoomPhoto(roomId: string) {
  const invalidate = useInvalidateRoom(roomId);
  return useMutation<RoomPhotoDto, unknown, UploadRoomPhotoInput>({
    mutationFn: (input) => roomsApi.uploadPhoto(roomId, input),
    onSuccess: invalidate,
  });
}

export function useDeleteRoomPhoto(roomId: string) {
  const invalidate = useInvalidateRoom(roomId);
  return useMutation<void, unknown, string>({
    mutationFn: (photoId) => roomsApi.removePhoto(roomId, photoId),
    onSuccess: invalidate,
  });
}

export function useFloorLayoutItems(floorId: string | undefined) {
  return useQuery<FloorLayoutItemDto[]>({
    queryKey: keys.floorLayout(floorId ?? ''),
    queryFn: () => floorsApi.listLayoutItems(floorId as string),
    enabled: Boolean(floorId),
  });
}

/**
 * Layout objects for several floors at once, keyed by floor id. The building
 * screen renders one miniature plan per row and each needs its own objects;
 * one `useQueries` call keeps that to a single hook instead of N components
 * each fetching on their own.
 */
export function useFloorLayoutItemsFor(floorIds: string[]) {
  const results = useQueries({
    queries: floorIds.map((floorId) => ({
      queryKey: keys.floorLayout(floorId),
      queryFn: () => floorsApi.listLayoutItems(floorId),
    })),
  });

  const byFloor = new Map<string, FloorLayoutItemDto[]>();
  results.forEach((result, index) => {
    const floorId = floorIds[index];
    if (floorId && result.data) {
      byFloor.set(floorId, result.data);
    }
  });
  return byFloor;
}

export function useCreateFloorLayoutItem(floorId: string) {
  const invalidate = useInvalidateFloor(floorId);
  return useMutation<FloorLayoutItemDto, unknown, CreateFloorLayoutItemInput>({
    mutationFn: (input) => floorsApi.createLayoutItem(floorId, input),
    onSuccess: invalidate,
  });
}

export function useUpdateFloorLayoutItem(floorId: string) {
  const invalidate = useInvalidateFloor(floorId);
  return useMutation<
    FloorLayoutItemDto,
    unknown,
    { itemId: string; input: UpdateFloorLayoutItemInput }
  >({
    mutationFn: ({ itemId, input }) => floorsApi.updateLayoutItem(floorId, itemId, input),
    onSuccess: invalidate,
  });
}

export function useDeleteFloorLayoutItem(floorId: string) {
  const invalidate = useInvalidateFloor(floorId);
  return useMutation<void, unknown, string>({
    mutationFn: (itemId) => floorsApi.removeLayoutItem(floorId, itemId),
    onSuccess: invalidate,
  });
}

export interface BuildingSnapshot {
  building: BuildingDto;
  floors: FloorDto[];
  rooms: RoomDto[];
  /** Rooms that belong to floors of this building. */
  roomsByFloor: Map<string, RoomDto[]>;
  roomCount: number;
  occupiedCount: number;
  occupancyRate: number;
}

/**
 * The server has no per-building filter and Room links to Floor (not
 * Building), so the aggregation happens here after three flat lists.
 */
export function useBuildingSnapshot(buildingId: string | undefined) {
  const buildings = useBuildings();
  const floors = useFloors();
  const rooms = useRooms();

  const loading = buildings.isLoading || floors.isLoading || rooms.isLoading;
  const error = buildings.error ?? floors.error ?? rooms.error;

  let snapshot: BuildingSnapshot | null = null;
  if (buildingId && buildings.data && floors.data && rooms.data) {
    const building = buildings.data.find((item) => item.id === buildingId);
    if (building) {
      const buildingFloors = floors.data
        .filter((floor) => floor.buildingId === buildingId)
        .sort((a, b) => a.floorNumber - b.floorNumber);
      const floorIds = new Set(buildingFloors.map((floor) => floor.id));
      const buildingRooms = rooms.data.filter((room) => floorIds.has(room.floorId));

      const roomsByFloor = new Map<string, RoomDto[]>();
      for (const room of buildingRooms) {
        const bucket = roomsByFloor.get(room.floorId) ?? [];
        bucket.push(room);
        roomsByFloor.set(room.floorId, bucket);
      }
      for (const bucket of roomsByFloor.values()) {
        bucket.sort((a, b) => a.roomNumber.localeCompare(b.roomNumber, 'vi', { numeric: true }));
      }

      const roomCount = buildingRooms.length;
      const occupiedCount = buildingRooms.filter((room) => room.status === 2).length;

      snapshot = {
        building,
        floors: buildingFloors,
        rooms: buildingRooms,
        roomsByFloor,
        roomCount,
        occupiedCount,
        occupancyRate: roomCount === 0 ? 0 : Math.round((occupiedCount / roomCount) * 100),
      };
    }
  }

  return {
    snapshot,
    loading,
    error,
    refetch: () => {
      void buildings.refetch();
      void floors.refetch();
      void rooms.refetch();
    },
  };
}
