import 'package:flutter_test/flutter_test.dart';

import 'package:ngoctrangbuilding_app/core/auth/app_session.dart';
import 'package:ngoctrangbuilding_app/core/auth/auth_models.dart';
import 'package:ngoctrangbuilding_app/core/auth/auth_repository.dart';
import 'package:ngoctrangbuilding_app/core/network/api_client.dart';
import 'package:ngoctrangbuilding_app/core/network/api_exception.dart';
import 'package:ngoctrangbuilding_app/features/tenant/tenant_account_repository.dart';

void main() {
  test(
    '401 while loading rental account clears the authenticated session',
    () async {
      final auth = _FakeAuthRepository();
      final session = AppSession(
        auth,
        TenantAccountRepository(
          (_) async => throw const ApiException(
            message: 'Phiên đăng nhập không hợp lệ hoặc đã hết hạn.',
            statusCode: 401,
            code: 'HTTP:401',
          ),
        ),
      );

      await expectLater(
        session.signIn('user', 'password'),
        throwsA(
          isA<ApiException>().having(
            (error) => error.isUnauthorized,
            'isUnauthorized',
            isTrue,
          ),
        ),
      );

      expect(session.isAuthenticated, isFalse);
      expect(session.user, isNull);
      expect(session.lastError?.isUnauthorized, isTrue);
      expect(auth.clearSessionCalls, 2);
    },
  );

  test(
    '403 while loading rental account remains a nonfatal account state',
    () async {
      final auth = _FakeAuthRepository();
      final session = AppSession(
        auth,
        TenantAccountRepository(
          (_) async => throw const ApiException(
            message: 'Bạn không có quyền thực hiện thao tác này.',
            statusCode: 403,
            code: 'HTTP:403',
          ),
        ),
      );

      await session.signIn('user', 'password');

      expect(session.isAuthenticated, isTrue);
      expect(session.tenantAccountError?.isForbidden, isTrue);
      expect(auth.clearSessionCalls, 1);
    },
  );

  test(
    'unexpected account parsing errors are normalized and clear the session',
    () async {
      final auth = _FakeAuthRepository();
      final session = AppSession(
        auth,
        TenantAccountRepository(
          (_) async => throw const FormatException('bad payload'),
        ),
      );

      await expectLater(
        session.signIn('user', 'password'),
        throwsA(
          isA<ApiException>().having(
            (error) => error.code,
            'code',
            'Common:UnexpectedError',
          ),
        ),
      );

      expect(session.isAuthenticated, isFalse);
      expect(auth.clearSessionCalls, 2);
    },
  );

  test(
    'application configuration rejects malformed authenticated user fields',
    () {
      expect(
        () => ApplicationConfiguration.fromJson(<String, dynamic>{
          'currentUser': <String, dynamic>{
            'isAuthenticated': true,
            'id': 'user-1',
            'userName': 42,
          },
        }),
        throwsA(
          isA<ApiException>().having(
            (error) => error.code,
            'code',
            'Common:InvalidCurrentUser',
          ),
        ),
      );
    },
  );

  test('application configuration accepts nullable optional user fields', () {
    final configuration = ApplicationConfiguration.fromJson(<String, dynamic>{
      'currentUser': <String, dynamic>{
        'isAuthenticated': true,
        'id': 'user-1',
        'userName': null,
        'email': null,
        'name': null,
      },
    });

    expect(configuration.user?.id, 'user-1');
    expect(configuration.user?.userName, isEmpty);
    expect(configuration.user?.email, isNull);
    expect(configuration.user?.name, isNull);
  });

  test('token expiry uses the safety skew', () {
    expect(
      TokenSet(
        accessToken: 'expired',
        expiresAt: DateTime.now().subtract(const Duration(seconds: 1)),
      ).isExpired,
      isTrue,
    );
    expect(
      TokenSet(
        accessToken: 'valid',
        expiresAt: DateTime.now().add(const Duration(minutes: 5)),
      ).isExpired,
      isFalse,
    );
  });
}

class _FakeAuthRepository extends AuthRepository {
  _FakeAuthRepository() : super(ApiClient(baseUrl: 'http://127.0.0.1'));

  int clearSessionCalls = 0;

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
  void clearSession() {
    clearSessionCalls++;
  }
}
