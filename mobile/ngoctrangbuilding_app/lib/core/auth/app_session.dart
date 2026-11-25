import 'dart:async';

import 'package:flutter/foundation.dart';

import '../../features/tenant/tenant_account_models.dart';
import '../../features/tenant/tenant_account_repository.dart';
import '../network/api_exception.dart';
import 'auth_models.dart';
import 'auth_repository.dart';
import '../navigation/role_navigation.dart';

class AppSession extends ChangeNotifier {
  AppSession(this._authRepository, this._tenantAccountRepository);

  final AuthRepository _authRepository;
  final TenantAccountRepository _tenantAccountRepository;
  TokenSet? _tokens;
  ApplicationConfiguration? _configuration;
  TenantMobileOverview? _tenantOverview;
  ApiException? _lastError;
  ApiException? _tenantAccountError;
  bool _busy = false;
  bool _tenantAccountBusy = false;
  bool _disposed = false;
  int _authGeneration = 0;
  int _tenantGeneration = 0;
  Timer? _expiryTimer;

  ApplicationConfiguration? get configuration => _configuration;
  CurrentUser? get user => _configuration?.user;
  Map<String, bool> get grantedPolicies =>
      _configuration?.grantedPolicies ?? const <String, bool>{};
  TenantMobileOverview? get tenantOverview => _tenantOverview;
  ApiException? get lastError => _lastError;
  ApiException? get tenantAccountError => _tenantAccountError;
  bool get busy => _busy;
  bool get tenantAccountBusy => _tenantAccountBusy;
  bool get isAuthenticated =>
      _tokens != null && !_tokens!.isExpired && user != null;

  bool hasPermission(String permission) => grantedPolicies[permission] == true;

  @override
  void dispose() {
    _disposed = true;
    _expiryTimer?.cancel();
    _expiryTimer = null;
    _authGeneration++;
    _tenantGeneration++;
    super.dispose();
  }

  Future<void> signIn(String username, String password) async {
    final generation = ++_authGeneration;
    _tenantGeneration++;
    _expiryTimer?.cancel();
    _expiryTimer = null;
    _tokens = null;
    _configuration = null;
    _tenantOverview = null;
    _tenantAccountError = null;
    _busy = true;
    _lastError = null;
    _authRepository.clearSession();
    _notify();
    try {
      final tokens = await _authRepository.signIn(
        username: username,
        password: password,
      );
      if (!_isCurrentAuth(generation)) return;
      _authRepository.setSession(tokens);
      _tokens = tokens;
      _scheduleExpiry(generation, tokens.expiresAt);

      final configuration = await _authRepository.loadConfiguration();
      if (!_isCurrentAuth(generation)) return;
      if (configuration.user == null) {
        throw const ApiException(
          message: 'Tài khoản chưa được máy chủ xác nhận.',
          code: 'Auth:Unauthenticated',
        );
      }
      _configuration = configuration;
      // Only tenant users should call the tenant self-service endpoint. Operators
      // authenticate through their configuration and must not lose their session
      // because they have no rental-account association.
      if (resolveRole(configuration.user!, hasPermission) == AppRole.tenant) {
        await _loadTenantAccount(
          authGeneration: generation,
          clearTransportOnUnauthorized: false,
        );
      }
    } catch (error, stackTrace) {
      if (!_isCurrentAuth(generation)) return;
      _tokens = null;
      _configuration = null;
      _tenantOverview = null;
      _tenantAccountError = null;
      _authRepository.clearSession();
      final normalized = _asApiException(error);
      _lastError = normalized;
      Error.throwWithStackTrace(normalized, stackTrace);
    } finally {
      if (_isCurrentAuth(generation)) {
        _busy = false;
        _notify();
      }
    }
  }

  Future<void> loadTenantAccount() async {
    final authGeneration = _authGeneration;
    await _loadTenantAccount(
      authGeneration: authGeneration,
      clearTransportOnUnauthorized: true,
    );
  }

  Future<void> _loadTenantAccount({
    required int authGeneration,
    required bool clearTransportOnUnauthorized,
  }) async {
    if (!_isCurrentAuth(authGeneration)) return;
    if (_tokens?.isExpired == true) {
      _clearSessionState(clearTransport: clearTransportOnUnauthorized);
      throw const ApiException(
        message: 'Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.',
        statusCode: 401,
        code: 'Auth:TokenExpired',
      );
    }
    if (_tokens == null || user == null) return;

    final tenantGeneration = ++_tenantGeneration;
    _tenantAccountBusy = true;
    _tenantAccountError = null;
    _notify();
    try {
      final overview = await _tenantAccountRepository.getMe();
      if (_isCurrentTenant(authGeneration, tenantGeneration)) {
        _tenantOverview = overview;
      }
    } on ApiException catch (error) {
      if (!_isCurrentTenant(authGeneration, tenantGeneration)) return;
      _tenantOverview = null;
      if (error.isForbidden) {
        _tenantAccountError = error;
        return;
      }
      if (error.isUnauthorized) {
        _clearSessionState(clearTransport: clearTransportOnUnauthorized);
      }
      rethrow;
    } catch (error, stackTrace) {
      if (!_isCurrentTenant(authGeneration, tenantGeneration)) return;
      _tenantOverview = null;
      _clearSessionState(clearTransport: clearTransportOnUnauthorized);
      final normalized = _asApiException(error);
      Error.throwWithStackTrace(normalized, stackTrace);
    } finally {
      if (_isCurrentTenant(authGeneration, tenantGeneration)) {
        _tenantAccountBusy = false;
        _notify();
      }
    }
  }

  ApiException _asApiException(Object error) => error is ApiException
      ? error
      : const ApiException(
          message: 'Đăng nhập thất bại do dữ liệu máy chủ không hợp lệ.',
          code: 'Common:UnexpectedError',
        );

  void signOut() {
    _authGeneration++;
    _tenantGeneration++;
    _clearSessionState(clearTransport: true);
    _notify();
  }

  void _scheduleExpiry(int generation, DateTime expiresAt) {
    _expiryTimer?.cancel();
    final delay = expiresAt
        .subtract(const Duration(seconds: 30))
        .difference(DateTime.now());
    _expiryTimer = Timer(delay.isNegative ? Duration.zero : delay, () {
      if (_isCurrentAuth(generation) && _tokens?.isExpired == true) {
        signOut();
      }
    });
  }

  void _clearSessionState({required bool clearTransport}) {
    _expiryTimer?.cancel();
    _expiryTimer = null;
    _tokens = null;
    _configuration = null;
    _tenantOverview = null;
    _lastError = null;
    _tenantAccountError = null;
    _busy = false;
    _tenantAccountBusy = false;
    if (clearTransport) _authRepository.clearSession();
  }

  bool _isCurrentAuth(int generation) =>
      !_disposed && generation == _authGeneration;

  bool _isCurrentTenant(int authGeneration, int tenantGeneration) =>
      _isCurrentAuth(authGeneration) && tenantGeneration == _tenantGeneration;

  void _notify() {
    if (!_disposed) notifyListeners();
  }
}
