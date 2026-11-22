import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'api_exception.dart';

class ApiClient {
  ApiClient({required String baseUrl, HttpClient? httpClient})
    : _baseUrl = baseUrl.replaceFirst(RegExp(r'/*$'), ''),
      _httpClient = httpClient ?? HttpClient();

  final String _baseUrl;
  final HttpClient _httpClient;
  String? _accessToken;
  DateTime? _accessTokenExpiresAt;

  void setAccessToken(String? token, {DateTime? expiresAt}) {
    _accessToken = token;
    _accessTokenExpiresAt = token == null ? null : expiresAt;
  }

  void dispose() => _httpClient.close(force: true);

  Future<dynamic> getJson(String path) => _request('GET', path);

  Future<dynamic> postForm(String path, Map<String, String> form) =>
      _request('POST', path, form: form);

  Future<dynamic> postJson(String path, Map<String, dynamic> body) =>
      _request('POST', path, body: body);

  Future<dynamic> _request(
    String method,
    String path, {
    Map<String, String>? form,
    Map<String, dynamic>? body,
  }) async {
    late final Uri uri;
    HttpClientRequest request;
    try {
      uri = Uri.parse('$_baseUrl$path');
      if (_accessTokenExpiresAt != null &&
          !DateTime.now().isBefore(
            _accessTokenExpiresAt!.subtract(const Duration(seconds: 30)),
          )) {
        throw const ApiException(
          message: 'Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.',
          statusCode: 401,
          code: 'Auth:TokenExpired',
        );
      }
      request = await _httpClient
          .openUrl(method, uri)
          .timeout(const Duration(seconds: 20));
      request.headers.set(HttpHeaders.acceptHeader, 'application/json');
      if (_accessToken != null) {
        request.headers.set(
          HttpHeaders.authorizationHeader,
          'Bearer $_accessToken',
        );
      }
      if (form != null) {
        request.headers.contentType = ContentType(
          'application',
          'x-www-form-urlencoded',
          charset: 'utf-8',
        );
        request.write(
          form.entries
              .map(
                (entry) =>
                    '${Uri.encodeQueryComponent(entry.key)}=${Uri.encodeQueryComponent(entry.value)}',
              )
              .join('&'),
        );
      } else if (body != null) {
        request.headers.contentType = ContentType.json;
        request.write(jsonEncode(body));
      }
      final response = await request.close().timeout(
        const Duration(seconds: 20),
      );
      final text = await utf8.decoder.bind(response).join();
      final payload = text.isEmpty ? null : _decode(text);
      if (response.statusCode < 200 || response.statusCode >= 300) {
        throw _toException(response.statusCode, payload);
      }
      return _unwrapEnvelope(path, payload);
    } on ApiException {
      rethrow;
    } on SocketException {
      throw const ApiException(
        message: 'Không kết nối được máy chủ. Kiểm tra mạng hoặc địa chỉ API.',
        code: 'Common:NetworkError',
      );
    } on HandshakeException {
      throw const ApiException(
        message: 'Không xác thực được chứng chỉ HTTPS của máy chủ.',
        code: 'Common:CertificateError',
      );
    } on TimeoutException {
      throw const ApiException(
        message: 'Máy chủ phản hồi quá lâu. Vui lòng thử lại.',
        code: 'Common:Timeout',
      );
    } on FormatException {
      throw const ApiException(
        message: 'Máy chủ trả về dữ liệu không hợp lệ.',
        code: 'Common:InvalidResponse',
      );
    } catch (_) {
      throw const ApiException(
        message: 'Không thể hoàn tất yêu cầu tới máy chủ.',
        code: 'Common:NetworkError',
      );
    }
  }

  dynamic _decode(String text) {
    try {
      return jsonDecode(text);
    } on FormatException {
      return text;
    }
  }

  dynamic _unwrapEnvelope(String path, dynamic payload) {
    if (_isRawPath(path)) return payload;
    if (payload is! Map<String, dynamic> || payload['success'] is! bool) {
      return payload;
    }
    if (payload['success'] == false) {
      final error = payload['error'];
      final errorMap = error is Map<String, dynamic>
          ? error
          : const <String, dynamic>{};
      throw ApiException(
        message: errorMap['message'] is String
            ? errorMap['message'] as String
            : 'Máy chủ từ chối yêu cầu.',
        statusCode: 400,
        code: errorMap['code'] is String ? errorMap['code'] as String : null,
      );
    }
    return payload['data'];
  }

  bool _isRawPath(String path) =>
      path.startsWith('/connect/') ||
      path.startsWith('/api/abp/') ||
      path.startsWith('/.well-known/');

  ApiException _toException(int statusCode, dynamic payload) {
    final map = payload is Map<String, dynamic>
        ? payload
        : const <String, dynamic>{};
    final code = map['error'] is String
        ? map['error'] as String
        : map['errorCode'] is String
        ? map['errorCode'] as String
        : 'HTTP:$statusCode';
    final description = map['error_description'] ?? map['message'];
    final message = switch (statusCode) {
      401 => 'Phiên đăng nhập không hợp lệ hoặc đã hết hạn.',
      403 => 'Bạn không có quyền thực hiện thao tác này.',
      404 => 'Không tìm thấy dữ liệu yêu cầu.',
      _ =>
        description is String ? description : 'Máy chủ trả về lỗi $statusCode.',
    };
    return ApiException(message: message, statusCode: statusCode, code: code);
  }
}
