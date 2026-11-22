class TokenSet {
  const TokenSet({
    required this.accessToken,
    this.refreshToken,
    required this.expiresAt,
  });

  final String accessToken;
  final String? refreshToken;
  final DateTime expiresAt;

  bool get isExpired =>
      DateTime.now().isAfter(expiresAt.subtract(const Duration(seconds: 30)));
}

class CurrentUser {
  const CurrentUser({
    required this.id,
    required this.userName,
    this.email,
    this.name,
    this.roles = const <String>[],
  });

  final String id;
  final String userName;
  final String? email;
  final String? name;
  final List<String> roles;
}

class ApplicationConfiguration {
  const ApplicationConfiguration({
    required this.user,
    required this.grantedPolicies,
  });

  final CurrentUser? user;
  final Map<String, bool> grantedPolicies;

  factory ApplicationConfiguration.fromJson(Map<String, dynamic> json) {
    final current = json['currentUser'];
    final currentMap = current is Map<String, dynamic> ? current : null;
    final isAuthenticated = currentMap?['isAuthenticated'] == true;
    final id = currentMap?['id'];
    final roles = currentMap?['roles'];
    final auth = json['auth'];
    final authMap = auth is Map<String, dynamic> ? auth : null;
    final rawPolicies = authMap?['grantedPolicies'];
    final policies = <String, bool>{};
    if (rawPolicies is Map<String, dynamic>) {
      rawPolicies.forEach((key, value) {
        if (value is bool) policies[key] = value;
      });
    }

    return ApplicationConfiguration(
      user: isAuthenticated && id is String
          ? CurrentUser(
              id: id,
              userName:
                  (currentMap?['userName'] ?? currentMap?['email'] ?? '')
                      as String,
              email: currentMap?['email'] as String?,
              name: currentMap?['name'] as String?,
              roles: roles is List
                  ? roles.whereType<String>().toList(growable: false)
                  : const <String>[],
            )
          : null,
      grantedPolicies: policies,
    );
  }
}
