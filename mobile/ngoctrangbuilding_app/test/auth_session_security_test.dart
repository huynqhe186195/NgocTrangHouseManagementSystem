import 'dart:async';

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
      addTearDown(session.dispose);

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
      addTearDown(session.dispose);

      await session.signIn('user', 'password');

      expect(session.isAuthenticated, isTrue);
      expect(session.tenantAccountError?.isForbidden, isTrue);
      expect(auth.clearSessionCalls, 1);
    },
  );

  test('owner login does not call tenant self-service endpoint', () async {
    final auth = _FakeAuthRepository(role: 'owner');
    var tenantRequestCount = 0;
    final session = AppSession(
      auth,
      TenantAccountRepository((_) async {
        tenantRequestCount++;
        return _overviewJson('owner');
      }),
    );
    addTearDown(session.dispose);

    await session.signIn('owner', 'password');

    expect(session.isAuthenticated, isTrue);
    expect(session.user?.roles, <String>['owner']);
    expect(tenantRequestCount, 0);
  });

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
      addTearDown(session.dispose);

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

  test('application configuration rejects malformed roles and policies', () {
    expect(
      () => ApplicationConfiguration.fromJson(<String, dynamic>{
        'currentUser': <String, dynamic>{
          'isAuthenticated': true,
          'id': 'user-1',
          'roles': <dynamic>['tenant', 42],
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
    expect(
      () => ApplicationConfiguration.fromJson(<String, dynamic>{
        'currentUser': <String, dynamic>{
          'isAuthenticated': true,
          'id': 'user-1',
        },
        'auth': <String, dynamic>{
          'grantedPolicies': <String, dynamic>{'Tenant.Read': 'true'},
        },
      }),
      throwsA(
        isA<ApiException>().having(
          (error) => error.code,
          'code',
          'Common:InvalidConfiguration',
        ),
      ),
    );
  });
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

  test('ignores a stale concurrent sign-in result', () async {
    final auth = _DeferredAuthRepository();
    final session = AppSession(
      auth,
      TenantAccountRepository((_) async => _overviewJson('first')),
    );
    addTearDown(session.dispose);

    final first = session.signIn('first', 'password');
    await auth.signInStarted(0);
    final second = session.signIn('second', 'password');
    await auth.signInStarted(1);

    auth.completeSignIn(1, _token('second'));
    await auth.configurationStarted(0);
    auth.completeConfiguration(0, _configuration('second'));
    await second;
    expect(session.user?.id, 'second');
    expect(session.busy, isFalse);

    auth.completeSignIn(0, _token('first'));
    await first;
    expect(session.user?.id, 'second');
    expect(session.busy, isFalse);
  });

  test('sign-out invalidates an in-flight sign-in', () async {
    final auth = _DeferredAuthRepository();
    final session = AppSession(
      auth,
      TenantAccountRepository((_) async => _overviewJson('late')),
    );
    addTearDown(session.dispose);

    final login = session.signIn('user', 'password');
    await auth.signInStarted(0);
    session.signOut();
    auth.completeSignIn(0, _token('late'));

    await login;
    expect(session.isAuthenticated, isFalse);
    expect(session.user, isNull);
    expect(session.tenantOverview, isNull);
    expect(session.busy, isFalse);
  });

  test('dispose invalidates an in-flight sign-in without notifying', () async {
    final auth = _DeferredAuthRepository();
    final session = AppSession(
      auth,
      TenantAccountRepository((_) async => _overviewJson('late')),
    );
    var notifications = 0;
    session.addListener(() => notifications++);

    final login = session.signIn('user', 'password');
    await auth.signInStarted(0);
    final beforeDispose = notifications;
    session.dispose();
    auth.completeSignIn(0, _token('late'));

    await login;
    expect(notifications, beforeDispose);
  });

  test('a stale tenant load cannot overwrite a newer tenant load', () async {
    final auth = _FakeAuthRepository();
    final requests = <Completer<dynamic>>[];
    var call = 0;
    final session = AppSession(
      auth,
      TenantAccountRepository((_) {
        if (call++ == 0) return Future.value(_overviewJson('initial'));
        final request = Completer<dynamic>();
        requests.add(request);
        return request.future;
      }),
    );
    addTearDown(session.dispose);
    await session.signIn('user', 'password');

    final first = session.loadTenantAccount();
    final second = session.loadTenantAccount();
    expect(requests, hasLength(2));
    requests[1].complete(_overviewJson('new'));
    await second;
    requests[0].complete(_overviewJson('old'));
    await first;

    expect(session.tenantOverview?.account.id, 'new');
    expect(session.tenantAccountBusy, isFalse);
  });

  test('dispose invalidates an in-flight tenant load', () async {
    final auth = _FakeAuthRepository();
    final request = Completer<dynamic>();
    var call = 0;
    final session = AppSession(
      auth,
      TenantAccountRepository((_) {
        if (call++ == 0) return Future.value(_overviewJson('initial'));
        return request.future;
      }),
    );
    await session.signIn('user', 'password');
    final load = session.loadTenantAccount();
    var notifications = 0;
    session.addListener(() => notifications++);
    final beforeDispose = notifications;
    session.dispose();
    request.complete(_overviewJson('late'));
    await load;
    expect(notifications, beforeDispose);
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

class _DeferredAuthRepository extends AuthRepository {
  _DeferredAuthRepository() : super(ApiClient(baseUrl: 'http://127.0.0.1'));

  final _signInResults = <Completer<TokenSet>>[];
  final _configurationResults = <Completer<ApplicationConfiguration>>[];

  Future<void> signInStarted(int index) async {
    while (_signInResults.length <= index) {
      await Future<void>.delayed(Duration.zero);
    }
  }

  Future<void> configurationStarted(int index) async {
    while (_configurationResults.length <= index) {
      await Future<void>.delayed(Duration.zero);
    }
  }

  void completeSignIn(int index, TokenSet token) {
    _signInResults[index].complete(token);
  }

  void completeConfiguration(int index, ApplicationConfiguration value) {
    _configurationResults[index].complete(value);
  }

  @override
  Future<TokenSet> signIn({
    required String username,
    required String password,
  }) {
    final result = Completer<TokenSet>();
    _signInResults.add(result);
    return result.future;
  }

  @override
  Future<ApplicationConfiguration> loadConfiguration() {
    final result = Completer<ApplicationConfiguration>();
    _configurationResults.add(result);
    return result.future;
  }

  @override
  void clearSession() {}
}

class _FakeAuthRepository extends AuthRepository {
  _FakeAuthRepository({this.role = 'tenant'})
    : super(ApiClient(baseUrl: 'http://127.0.0.1'));

  final String role;
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
      ApplicationConfiguration(
        user: CurrentUser(
          id: 'user-1',
          userName: 'user',
          name: 'Nguyễn An',
          roles: <String>[role],
        ),
        grantedPolicies: <String, bool>{},
      );

  @override
  void clearSession() {
    clearSessionCalls++;
  }
}

TokenSet _token(String id) => TokenSet(
  accessToken: '$id-token',
  expiresAt: DateTime.now().add(const Duration(hours: 1)),
);

ApplicationConfiguration _configuration(String id) => ApplicationConfiguration(
  user: CurrentUser(
    id: id,
    userName: id,
    name: id,
    roles: const <String>['tenant'],
  ),
  grantedPolicies: const <String, bool>{},
);

Map<String, dynamic> _overviewJson(String id) => <String, dynamic>{
  'currentUser': <String, dynamic>{
    'identityUserId': id,
    'tenantId': id,
    'displayName': id,
    'accessRole': 'PrimaryTenant',
  },
  'account': <String, dynamic>{
    'id': id,
    'accountNumber': 'RA-$id',
    'displayName': id,
    'isActive': true,
  },
  'activeContract': null,
  'members': <dynamic>[],
};
