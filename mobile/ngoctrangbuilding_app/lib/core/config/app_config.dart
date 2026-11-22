class AppConfig {
  const AppConfig._();

  /// Set this at build/run time with --dart-define=NGOC_TRANG_API_BASE_URL=...
  /// Android emulators reach a host machine through 10.0.2.2.
  static const apiBaseUrl = String.fromEnvironment(
    'NGOC_TRANG_API_BASE_URL',
    defaultValue: 'https://10.0.2.2:44311',
  );

  static const clientId = 'NgocTrangHouseManagementSystem_App';
  static const scopes = <String>[
    'openid',
    'profile',
    'email',
    'roles',
    'NgocTrangHouseManagementSystem',
  ];
}
