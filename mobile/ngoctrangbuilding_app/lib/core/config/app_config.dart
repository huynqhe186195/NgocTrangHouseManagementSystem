import 'dart:io';

class AppConfig {
  const AppConfig._();

  /// Set this at build/run time with --dart-define=NGOC_TRANG_API_BASE_URL=...
  /// Android emulators reach a host machine through 10.0.2.2. iOS simulators
  /// and desktop use localhost; physical devices must use a reachable LAN URL.
  static const _configuredApiBaseUrl = String.fromEnvironment(
    'NGOC_TRANG_API_BASE_URL',
  );

  static String get apiBaseUrl {
    if (_configuredApiBaseUrl.isNotEmpty) return _configuredApiBaseUrl;
    if (Platform.isAndroid) return 'https://10.0.2.2:44311';
    return 'https://127.0.0.1:44311';
  }

  static const clientId = 'NgocTrangHouseManagementSystem_App';
  static const scopes = <String>[
    'openid',
    'profile',
    'email',
    'roles',
    'NgocTrangHouseManagementSystem',
  ];
}
