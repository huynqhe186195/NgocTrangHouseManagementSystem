import 'package:flutter_test/flutter_test.dart';

import 'package:ngoctrangbuilding_app/core/auth/auth_models.dart';
import 'package:ngoctrangbuilding_app/core/navigation/role_navigation.dart';
import 'package:ngoctrangbuilding_app/core/permissions/building_policies.dart';

void main() {
  const owner = CurrentUser(
    id: 'owner-id',
    userName: 'owner',
    roles: ['Owner'],
  );
  const tenant = CurrentUser(
    id: 'tenant-id',
    userName: 'tenant',
    roles: ['Tenant'],
  );

  test('resolves server-issued Owner role', () {
    expect(resolveRole(owner, (_) => false), AppRole.owner);
  });

  test('resolves operator scope without trusting a client-selected role', () {
    expect(
      resolveRole(
        const CurrentUser(id: 'staff-id', userName: 'staff'),
        (permission) => permission == BuildingPolicies.roomsManage,
      ),
      AppRole.staff,
    );
  });

  test('resolves Tenant role separately', () {
    expect(resolveRole(tenant, (_) => false), AppRole.tenant);
  });
}
