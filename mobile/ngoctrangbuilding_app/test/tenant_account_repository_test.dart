import 'package:flutter_test/flutter_test.dart';

import 'package:ngoctrangbuilding_app/core/auth/app_session.dart';
import 'package:ngoctrangbuilding_app/core/auth/auth_models.dart';
import 'package:ngoctrangbuilding_app/core/auth/auth_repository.dart';
import 'package:ngoctrangbuilding_app/core/network/api_client.dart';
import 'package:ngoctrangbuilding_app/core/network/api_exception.dart';
import 'package:ngoctrangbuilding_app/features/tenant/tenant_account_repository.dart';

void main() {
  test(
    'parses the scoped account, active contract and member summaries',
    () async {
      final repository = TenantAccountRepository((path) async {
        expect(path, '/api/mobile/rental-account/me');
        return <String, dynamic>{
          'currentUser': <String, dynamic>{
            'identityUserId': 'user-1',
            'tenantId': 'tenant-1',
            'displayName': 'Nguyễn An',
            'accessRole': 1,
          },
          'account': <String, dynamic>{
            'id': 'account-1',
            'accountNumber': 'RA-001',
            'displayName': 'Phòng 301',
            'isActive': true,
          },
          'activeContract': <String, dynamic>{
            'id': 'contract-1',
            'contractNumber': 'HD-001',
            'startDate': '2026-01-01T00:00:00Z',
            'endDate': null,
            'room': <String, dynamic>{
              'id': 'room-1',
              'roomNumber': '301',
              'name': 'Phòng 301',
              'floorId': 'floor-1',
              'floorName': 'Tầng 3',
              'buildingId': 'building-1',
              'buildingName': 'Ngọc Trang',
            },
          },
          'members': <Map<String, dynamic>>[
            <String, dynamic>{
              'tenantId': 'tenant-1',
              'displayName': 'Nguyễn An',
              'role': 'PrimaryTenant',
            },
          ],
        };
      });

      final result = await repository.getMe();

      expect(result.account.accountNumber, 'RA-001');
      expect(result.currentUser.accessRole, 'PrimaryTenant');
      expect(result.activeContract?.room.roomNumber, '301');
      expect(result.members.single.displayName, 'Nguyễn An');
    },
  );

  test('preserves an empty active contract response', () async {
    final repository = TenantAccountRepository(
      (_) async => <String, dynamic>{
        'currentUser': <String, dynamic>{
          'identityUserId': 'user-1',
          'tenantId': null,
          'displayName': null,
          'accessRole': 'Viewer',
        },
        'account': <String, dynamic>{
          'id': 'account-1',
          'accountNumber': 'RA-001',
          'displayName': 'Tài khoản thuê',
          'isActive': true,
        },
        'activeContract': null,
        'members': <dynamic>[],
      },
    );

    final result = await repository.getMe();

    expect(result.activeContract, isNull);
    expect(result.members, isEmpty);
  });

  test('rejects a malformed non-null contract end date', () async {
    final repository = TenantAccountRepository(
      (_) async => <String, dynamic>{
        'currentUser': <String, dynamic>{
          'identityUserId': 'user-1',
          'tenantId': 'tenant-1',
          'displayName': 'Nguyễn An',
          'accessRole': 'PrimaryTenant',
        },
        'account': <String, dynamic>{
          'id': 'account-1',
          'accountNumber': 'RA-001',
          'displayName': 'Phòng 301',
          'isActive': true,
        },
        'activeContract': <String, dynamic>{
          'id': 'contract-1',
          'contractNumber': 'HD-001',
          'startDate': '2026-01-01T00:00:00Z',
          'endDate': 'not-a-date',
          'room': <String, dynamic>{
            'id': 'room-1',
            'roomNumber': '301',
            'name': 'Phòng 301',
            'floorId': 'floor-1',
            'floorName': 'Tầng 3',
            'buildingId': 'building-1',
            'buildingName': 'Ngọc Trang',
          },
        },
        'members': <dynamic>[],
      },
    );

    expect(
      repository.getMe(),
      throwsA(
        isA<ApiException>().having(
          (error) => error.code,
          'code',
          'Common:InvalidRentalContractDate',
        ),
      ),
    );
  });

  test('keeps a forbidden response as an authorization error', () async {
    final repository = TenantAccountRepository((_) async {
      throw const ApiException(
        message: 'Bạn không có quyền thực hiện thao tác này.',
        statusCode: 403,
        code: 'HTTP:403',
      );
    });

    expect(
      repository.getMe(),
      throwsA(
        isA<ApiException>().having(
          (error) => error.isForbidden,
          'isForbidden',
          isTrue,
        ),
      ),
    );
  });

  test('loads the rental account after an authenticated login', () async {
    final accountRepository = TenantAccountRepository(
      (_) async => <String, dynamic>{
        'currentUser': <String, dynamic>{
          'identityUserId': 'user-1',
          'tenantId': 'tenant-1',
          'displayName': 'Nguyễn An',
          'accessRole': 'PrimaryTenant',
        },
        'account': <String, dynamic>{
          'id': 'account-1',
          'accountNumber': 'RA-001',
          'displayName': 'Phòng 301',
          'isActive': true,
        },
        'activeContract': null,
        'members': <dynamic>[],
      },
    );
    final session = AppSession(_FakeAuthRepository(), accountRepository);

    await session.signIn('user', 'password');

    expect(session.isAuthenticated, isTrue);
    expect(session.tenantOverview?.account.accountNumber, 'RA-001');
    expect(session.tenantAccountError, isNull);
  });
}

class _FakeAuthRepository extends AuthRepository {
  _FakeAuthRepository() : super(ApiClient(baseUrl: 'http://127.0.0.1'));

  @override
  Future<TokenSet> signIn({
    required String username,
    required String password,
  }) async => TokenSet(
    accessToken: 'test-access-token',
    expiresAt: DateTime.now().add(const Duration(hours: 1)),
  );

  @override
  Future<ApplicationConfiguration> loadConfiguration() async =>
      const ApplicationConfiguration(
        user: CurrentUser(
          id: 'user-1',
          userName: 'user',
          name: 'Nguyễn An',
          roles: <String>['tenant'],
        ),
        grantedPolicies: <String, bool>{},
      );

  @override
  void clearSession() {}
}
