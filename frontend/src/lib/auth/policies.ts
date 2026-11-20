/**
 * Mirrors BuildingManagementPermissions in
 * modules/buildingmanagement/src/BuildingManagement.Application.Contracts.
 * The server also enforces building membership; a granted policy alone does
 * not authorize a write.
 */
export const BuildingPolicies = {
  buildingsRead: 'BuildingManagement.Buildings.Read',
  buildingsManage: 'BuildingManagement.Buildings.Manage',
  buildingsReadOperators: 'BuildingManagement.Buildings.ReadOperators',
  /** First-owner bootstrap. Never granted automatically. */
  buildingsBootstrapOwner: 'BuildingManagement.Buildings.BootstrapOwner',
  floorsManage: 'BuildingManagement.Floors.Manage',
  roomsManage: 'BuildingManagement.Rooms.Manage',
  contractsRead: 'BuildingManagement.Contracts.Read',
  /**
   * Separate from contractsRead: a tenant record carries citizen id, address
   * and emergency contact, so the server grants the roster read on its own.
   */
  tenantsRead: 'BuildingManagement.Tenants.Read',
} as const;
