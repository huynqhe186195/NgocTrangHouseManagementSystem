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
  test('rejects an expiry beyond the supported lifetime', () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    server.listen((request) {
      request.response
        ..statusCode = HttpStatus.ok
        ..headers.contentType = ContentType.json
        ..write(
          jsonEncode(<String, dynamic>{
            'access_token': 'access-token',
            'expires_in': 365 * 24 * 60 * 60 + 1,
          }),
        )
        ..close();
    });
    addTearDown(server.close);

    final client = ApiClient(baseUrl: 'http://127.0.0.1:${server.port}');
    addTearDown(client.dispose);

    await expectLater(
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

  test('rejects whitespace-bearing access tokens', () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    server.listen((request) {
      request.response
        ..statusCode = HttpStatus.ok
        ..headers.contentType = ContentType.json
        ..write(
          jsonEncode(<String, dynamic>{
            'access_token': ' access-token',
            'expires_in': 3600,
          }),
        )
        ..close();
    });
    addTearDown(server.close);

    final client = ApiClient(baseUrl: 'http://127.0.0.1:${server.port}');
    addTearDown(client.dispose);

    await expectLater(
      AuthRepository(client).signIn(username: 'user', password: 'password'),
      throwsA(isA<ApiException>()),
    );
  });

  test('rejects an empty refresh token', () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    server.listen((request) {
      request.response
        ..statusCode = HttpStatus.ok
        ..headers.contentType = ContentType.json
        ..write(
          jsonEncode(<String, dynamic>{
            'access_token': 'access-token',
            'expires_in': 3600,
            'refresh_token': '   ',
          }),
        )
        ..close();
    });
    addTearDown(server.close);

    final client = ApiClient(baseUrl: 'http://127.0.0.1:${server.port}');
    addTearDown(client.dispose);

    await expectLater(
      AuthRepository(client).signIn(username: 'user', password: 'password'),
      throwsA(isA<ApiException>()),
    );
  });

  test('rejects a non-http API URI as a typed exception', () async {
    final client = ApiClient(baseUrl: 'ftp://example.test');
    addTearDown(client.dispose);

    await expectLater(
      client.getJson('/api/test'),
      throwsA(
        isA<ApiException>().having(
          (error) => error.code,
          'code',
          'Common:InvalidApiBaseUrl',
        ),
      ),
    );
  });

  test('rejects a relative API URI as a typed exception', () async {
    final client = ApiClient(baseUrl: 'api.example.test');
    addTearDown(client.dispose);

    await expectLater(
      client.getJson('/api/test'),
      throwsA(isA<ApiException>()),
    );
  });

  test('normalizes malformed UTF-8 responses', () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    server.listen((request) {
      request.response
        ..statusCode = HttpStatus.ok
        ..headers.contentType = ContentType.json;
      request.response.add(<int>[0xff, 0xfe]);
      request.response.close();
    });
    addTearDown(server.close);

    final client = ApiClient(baseUrl: 'http://127.0.0.1:${server.port}');
    addTearDown(client.dispose);

    await expectLater(
      client.getJson('/api/test'),
      throwsA(
        isA<ApiException>().having(
          (error) => error.code,
          'code',
          'Common:InvalidResponse',
        ),
      ),
    );
  });

  test('rejects an empty successful response for a typed API path', () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    server.listen((request) {
      request.response
        ..statusCode = HttpStatus.ok
        ..headers.contentType = ContentType.json
        ..close();
    });
    addTearDown(server.close);

    final client = ApiClient(baseUrl: 'http://127.0.0.1:${server.port}');
    addTearDown(client.dispose);

    await expectLater(
      client.getJson('/api/test'),
      throwsA(
        isA<ApiException>().having(
          (error) => error.code,
          'code',
          'Common:InvalidResponse',
        ),
      ),
    );
  });

  test('rejects malformed JSON for a typed API path', () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    server.listen((request) {
      request.response
        ..statusCode = HttpStatus.ok
        ..headers.contentType = ContentType.json
        ..write('{not-json')
        ..close();
    });
    addTearDown(server.close);

    final client = ApiClient(baseUrl: 'http://127.0.0.1:${server.port}');
    addTearDown(client.dispose);

    await expectLater(
      client.getJson('/api/test'),
      throwsA(isA<ApiException>()),
    );
  });

  test('rejects a malformed response envelope', () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    server.listen((request) {
      request.response
        ..statusCode = HttpStatus.ok
        ..headers.contentType = ContentType.json
        ..write(jsonEncode(<String, dynamic>{'success': 'yes'}))
        ..close();
    });
    addTearDown(server.close);

    final client = ApiClient(baseUrl: 'http://127.0.0.1:${server.port}');
    addTearDown(client.dispose);

    await expectLater(
      client.getJson('/api/test'),
      throwsA(
        isA<ApiException>().having(
          (error) => error.code,
          'code',
          'Common:InvalidResponse',
        ),
      ),
    );
  });

  test('normalizes non-string envelope error fields', () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    server.listen((request) {
      request.response
        ..statusCode = HttpStatus.ok
        ..headers.contentType = ContentType.json
        ..write(
          jsonEncode(<String, dynamic>{
            'success': false,
            'error': <String, dynamic>{'message': 42, 'code': <String>[]},
          }),
        )
        ..close();
    });
    addTearDown(server.close);

    final client = ApiClient(baseUrl: 'http://127.0.0.1:${server.port}');
    addTearDown(client.dispose);

    await expectLater(
      client.getJson('/api/test'),
      throwsA(
        isA<ApiException>()
            .having((error) => error.statusCode, 'statusCode', 400)
            .having((error) => error.code, 'code', isNull),
      ),
    );
  });

  test('rejects base URLs with query or fragment components', () async {
    for (final baseUrl in <String>[
      'http://127.0.0.1:1?tenant=spoofed',
      'http://127.0.0.1:1#fragment',
      'http://:invalid',
      '',
    ]) {
      final client = ApiClient(baseUrl: baseUrl);
      addTearDown(client.dispose);

      await expectLater(
        client.getJson('/api/test'),
        throwsA(
          isA<ApiException>().having(
            (error) => error.code,
            'code',
            'Common:InvalidApiBaseUrl',
          ),
        ),
      );
    }
  });

  test('parses valid UTF-8 JSON responses', () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    server.listen((request) {
      request.response
        ..statusCode = HttpStatus.ok
        ..headers.contentType = ContentType.json
        ..write(jsonEncode(<String, dynamic>{'message': 'Ngọc Trang'}))
        ..close();
    });
    addTearDown(server.close);

    final client = ApiClient(baseUrl: 'http://127.0.0.1:${server.port}');
    addTearDown(client.dispose);

    expect(await client.getJson('/api/test'), <String, dynamic>{
      'message': 'Ngọc Trang',
    });
  });
}
