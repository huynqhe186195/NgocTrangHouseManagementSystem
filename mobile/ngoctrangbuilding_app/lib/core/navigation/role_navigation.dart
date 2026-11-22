import '../auth/auth_models.dart';
import '../permissions/building_policies.dart';

enum AppRole { owner, staff, tenant, unknown }

AppRole resolveRole(CurrentUser user, bool Function(String) hasPermission) {
  final roles = user.roles.map((role) => role.toLowerCase()).toSet();
  if (roles.contains('admin') || roles.contains('owner')) return AppRole.owner;
  final hasOperatorScope =
      hasPermission(BuildingPolicies.buildingsManage) ||
      hasPermission(BuildingPolicies.floorsManage) ||
      hasPermission(BuildingPolicies.roomsManage) ||
      hasPermission(BuildingPolicies.contractsRead) ||
      hasPermission(BuildingPolicies.tenantsRead) ||
      hasPermission(BuildingPolicies.buildingsReadOperators);
  if (roles.contains('staff') || hasOperatorScope) return AppRole.staff;
  if (roles.contains('tenant') || roles.isNotEmpty) return AppRole.tenant;
  return AppRole.unknown;
}

String roleLabel(AppRole role) => switch (role) {
  AppRole.owner => 'Owner',
  AppRole.staff => 'Staff',
  AppRole.tenant => 'Tenant',
  AppRole.unknown => 'Chưa xác định',
};
