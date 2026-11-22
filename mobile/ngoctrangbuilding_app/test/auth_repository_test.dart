import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

import 'package:ngoctrangbuilding_app/core/auth/auth_repository.dart';
import 'package:ngoctrangbuilding_app/core/network/api_client.dart';
import 'package:ngoctrangbuilding_app/core/network/api_exception.dart';

void main() {
  test('rejects a non-positive token expiry', () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    server.listen((request) {
      request.response
        ..statusCode = HttpStatus.ok
        ..headers.contentType = ContentType.json
        ..write(
          jsonEncode(<String, dynamic>{
            'access_token': 'access-token',
            'expires_in': 0,
          }),
        )
        ..close();
    });
    addTearDown(server.close);

    final client = ApiClient(baseUrl: 'http://127.0.0.1:${server.port}');
    addTearDown(client.dispose);

    expect(
      AuthRepository(client).signIn(username: 'user', password: 'password'),
      throwsA(
        isA<ApiException>().having(
          (error) => error.code,
          'code',
          'Common:InvalidTokenExpiry',
        ),
      ),
    );
  });

  test('rejects a malformed refresh token', () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    server.listen((request) {
      request.response
        ..statusCode = HttpStatus.ok
        ..headers.contentType = ContentType.json
        ..write(
          jsonEncode(<String, dynamic>{
            'access_token': 'access-token',
            'expires_in': 3600,
            'refresh_token': 42,
          }),
        )
        ..close();
    });
    addTearDown(server.close);

    final client = ApiClient(baseUrl: 'http://127.0.0.1:${server.port}');
    addTearDown(client.dispose);

    expect(
      AuthRepository(client).signIn(username: 'user', password: 'password'),
      throwsA(
        isA<ApiException>().having(
          (error) => error.code,
          'code',
          'Common:InvalidTokenResponse',
        ),
      ),
    );
  });

  test(
    'blocks requests after the access token safety window expires',
    () async {
      final client = ApiClient(baseUrl: 'http://127.0.0.1:1');
      addTearDown(client.dispose);
      client.setAccessToken(
        'expired-token',
        expiresAt: DateTime.now().subtract(const Duration(seconds: 1)),
      );

      expect(
        client.getJson('/api/mobile/rental-account/me'),
        throwsA(
          isA<ApiException>().having(
            (error) => error.code,
            'code',
            'Auth:TokenExpired',
          ),
        ),
      );
    },
  );
}
