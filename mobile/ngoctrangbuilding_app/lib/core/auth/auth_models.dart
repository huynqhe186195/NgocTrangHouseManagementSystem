import '../network/api_exception.dart';

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
      !DateTime.now().isBefore(expiresAt.subtract(const Duration(seconds: 30)));
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
    if (current != null && current is! Map<String, dynamic>) {
      throw const ApiException(
        message: 'Máy chủ trả về cấu hình tài khoản không hợp lệ.',
        code: 'Common:InvalidConfiguration',
      );
    }
    final currentMap = current as Map<String, dynamic>?;
    final isAuthenticated = currentMap?['isAuthenticated'] == true;
    final id = currentMap?['id'];
    final rawUserName = currentMap?['userName'];
    final rawEmail = currentMap?['email'];
    final rawName = currentMap?['name'];
    final roles = currentMap?['roles'];
    final auth = json['auth'];
    if (isAuthenticated &&
        (id is! String ||
            id.trim().isEmpty ||
            (rawUserName != null && rawUserName is! String) ||
            (rawEmail != null && rawEmail is! String) ||
            (rawName != null && rawName is! String) ||
            (roles != null &&
                (roles is! List || roles.any((role) => role is! String))))) {
      throw const ApiException(
        message: 'Máy chủ trả về thông tin tài khoản không hợp lệ.',
        code: 'Common:InvalidCurrentUser',
      );
    }
    if (auth != null && auth is! Map<String, dynamic>) {
      throw const ApiException(
        message: 'Máy chủ trả về cấu hình quyền không hợp lệ.',
        code: 'Common:InvalidConfiguration',
      );
    }
    final authMap = auth as Map<String, dynamic>?;
    final rawPolicies = authMap?['grantedPolicies'];
    if (rawPolicies != null && rawPolicies is! Map<String, dynamic>) {
      throw const ApiException(
        message: 'Máy chủ trả về cấu hình quyền không hợp lệ.',
        code: 'Common:InvalidConfiguration',
      );
    }
    final policies = <String, bool>{};
    if (rawPolicies is Map<String, dynamic>) {
      rawPolicies.forEach((key, value) {
        if (value is! bool) {
          throw const ApiException(
            message: 'Máy chủ trả về cấu hình quyền không hợp lệ.',
            code: 'Common:InvalidConfiguration',
          );
        }
        policies[key] = value;
      });
    }

    return ApplicationConfiguration(
      user: isAuthenticated && id is String
          ? CurrentUser(
              id: id,
              userName: rawUserName is String && rawUserName.isNotEmpty
                  ? rawUserName
                  : rawEmail is String
                  ? rawEmail
                  : '',
              email: rawEmail as String?,
              name: rawName as String?,
              roles: roles is List
                  ? roles.whereType<String>().toList(growable: false)
                  : const <String>[],
            )
          : null,
      grantedPolicies: policies,
    );
  }
}
