/**
 * Mirrors BuildingManagementPermissions in
 * modules/buildingmanagement/src/BuildingManagement.Application.Contracts.
 * The server also enforces building membership; a granted policy alone does
 * not authorize a write.
 */
export const BuildingPolicies = {
  buildingsRead: 'BuildingManagement.Buildings.Read',
  buildingsManage: 'BuildingManagement.Buildings.Manage',
  /** First-owner bootstrap. Never granted automatically. */
  buildingsBootstrapOwner: 'BuildingManagement.Buildings.BootstrapOwner',
  floorsManage: 'BuildingManagement.Floors.Manage',
  roomsManage: 'BuildingManagement.Rooms.Manage',
} as const;
