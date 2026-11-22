import '../config/app_config.dart';
import '../network/api_client.dart';
import '../network/api_exception.dart';
import 'auth_models.dart';

class AuthRepository {
  AuthRepository(this._api);

  final ApiClient _api;

  Future<TokenSet> signIn({
    required String username,
    required String password,
  }) async {
    final payload = await _api.postForm('/connect/token', {
      'grant_type': 'password',
      'client_id': AppConfig.clientId,
      'username': username,
      'password': password,
      'scope': AppConfig.scopes.join(' '),
    });
    if (payload is! Map<String, dynamic>) {
      throw const ApiException(
        message: 'Máy chủ trả về phiên đăng nhập không hợp lệ.',
        code: 'Common:InvalidTokenResponse',
      );
    }
    final accessToken = payload['access_token'];
    if (accessToken is! String || accessToken.trim().isEmpty) {
      throw const ApiException(
        message: 'Máy chủ trả về phiên đăng nhập không hợp lệ.',
        code: 'Common:InvalidTokenResponse',
      );
    }
    final rawExpiresIn = payload['expires_in'];
    if (rawExpiresIn is! num ||
        !rawExpiresIn.isFinite ||
        rawExpiresIn <= 0 ||
        rawExpiresIn != rawExpiresIn.truncateToDouble()) {
      throw const ApiException(
        message: 'Máy chủ trả về thời hạn phiên đăng nhập không hợp lệ.',
        code: 'Common:InvalidTokenExpiry',
      );
    }
    final expiresIn = rawExpiresIn.toInt();
    final refreshToken = payload['refresh_token'];
    if (refreshToken != null && refreshToken is! String) {
      throw const ApiException(
        message: 'Máy chủ trả về refresh token không hợp lệ.',
        code: 'Common:InvalidTokenResponse',
      );
    }
    final token = TokenSet(
      accessToken: accessToken,
      refreshToken: refreshToken as String?,
      expiresAt: DateTime.now().add(Duration(seconds: expiresIn)),
    );
    _api.setAccessToken(token.accessToken, expiresAt: token.expiresAt);
    return token;
  }

  Future<ApplicationConfiguration> loadConfiguration() async {
    final payload = await _api.getJson('/api/abp/application-configuration');
    if (payload is! Map<String, dynamic>) {
      throw const ApiException(
        message: 'Máy chủ trả về cấu hình tài khoản không hợp lệ.',
        code: 'Common:InvalidConfiguration',
      );
    }
    return ApplicationConfiguration.fromJson(payload);
  }

  void clearSession() => _api.setAccessToken(null);
}
