import 'package:flutter/foundation.dart';

import '../../features/tenant/tenant_account_models.dart';
import '../../features/tenant/tenant_account_repository.dart';
import '../network/api_exception.dart';
import 'auth_models.dart';
import 'auth_repository.dart';

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

  ApplicationConfiguration? get configuration => _configuration;
  CurrentUser? get user => _configuration?.user;
  Map<String, bool> get grantedPolicies =>
      _configuration?.grantedPolicies ?? const <String, bool>{};
  TenantMobileOverview? get tenantOverview => _tenantOverview;
  ApiException? get lastError => _lastError;
  ApiException? get tenantAccountError => _tenantAccountError;
  bool get busy => _busy;
  bool get tenantAccountBusy => _tenantAccountBusy;
  bool get isAuthenticated => _tokens != null && user != null;

  bool hasPermission(String permission) => grantedPolicies[permission] == true;

  Future<void> signIn(String username, String password) async {
    _busy = true;
    _lastError = null;
    _tenantOverview = null;
    _tenantAccountError = null;
    notifyListeners();
    try {
      _tokens = await _authRepository.signIn(
        username: username,
        password: password,
      );
      final configuration = await _authRepository.loadConfiguration();
      if (configuration.user == null) {
        throw const ApiException(
          message: 'Tài khoản chưa được máy chủ xác nhận.',
          code: 'Auth:Unauthenticated',
        );
      }
      _configuration = configuration;
      // This endpoint is attempted for every authenticated role. Operators may
      // legitimately have no rental-account link, so a 403 is kept as state
      // instead of turning a valid operator login into a failed login.
      await loadTenantAccount();
    } on ApiException catch (error) {
      _tokens = null;
      _configuration = null;
      _tenantOverview = null;
      _tenantAccountError = null;
      _lastError = error;
      _authRepository.clearSession();
      rethrow;
    } finally {
      _busy = false;
      notifyListeners();
    }
  }

  Future<void> loadTenantAccount() async {
    if (!isAuthenticated) return;
    _tenantAccountBusy = true;
    _tenantAccountError = null;
    notifyListeners();
    try {
      _tenantOverview = await _tenantAccountRepository.getMe();
    } on ApiException catch (error) {
      _tenantOverview = null;
      _tenantAccountError = error;
    } finally {
      _tenantAccountBusy = false;
      notifyListeners();
    }
  }

  void signOut() {
    _tokens = null;
    _configuration = null;
    _tenantOverview = null;
    _lastError = null;
    _tenantAccountError = null;
    _authRepository.clearSession();
    notifyListeners();
  }
}
