import { useQuery } from '@tanstack/react-query';
import { buildingsApi, floorsApi, roomsApi } from '../api/buildingApi';
import type { BuildingDto, FloorDto, RoomDto } from '../api/types';

const keys = {
  buildings: ['buildings'] as const,
  floors: ['floors'] as const,
  rooms: ['rooms'] as const,
};

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
