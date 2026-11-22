// Flutter 3.50 deprecates the legacy Material import in favor of material_ui.
// Keep the SDK Material API until the package policy is approved.
// ignore_for_file: deprecated_member_use

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:ngoctrangbuilding_app/core/auth/app_session.dart';
import 'package:ngoctrangbuilding_app/core/auth/auth_models.dart';
import 'package:ngoctrangbuilding_app/core/auth/auth_repository.dart';
import 'package:ngoctrangbuilding_app/core/network/api_client.dart';
import 'package:ngoctrangbuilding_app/core/network/api_exception.dart';
import 'package:ngoctrangbuilding_app/core/theme/app_theme.dart';
import 'package:ngoctrangbuilding_app/features/tenant/tenant_account_repository.dart';
import 'package:ngoctrangbuilding_app/features/tenant/tenant_invoices_screen.dart';
import 'package:ngoctrangbuilding_app/features/tenant/tenant_move_out_screen.dart';
import 'package:ngoctrangbuilding_app/features/tenant/tenant_overview_screen.dart';

void main() {
  testWidgets(
    'renders backend rental account data without inventing invoice values',
    (tester) async {
      final session = AppSession(
        _FakeAuthRepository(),
        TenantAccountRepository(
          (_) async => <String, dynamic>{
            'currentUser': <String, dynamic>{
              'identityUserId': 'user-1',
              'tenantId': 'tenant-1',
              'displayName': 'Nguyễn An',
              'accessRole': 'PrimaryTenant',
            },
            'account': <String, dynamic>{
              'id': 'account-1',
              'accountNumber': 'RA-001',
              'displayName': 'Phòng 301',
              'isActive': true,
            },
            'activeContract': <String, dynamic>{
              'id': 'contract-1',
              'contractNumber': 'HD-001',
              'startDate': '2026-01-01T00:00:00Z',
              'room': <String, dynamic>{
                'id': 'room-1',
                'roomNumber': '301',
                'name': 'Phòng 301',
                'floorId': 'floor-1',
                'floorName': 'Tầng 3',
                'buildingId': 'building-1',
                'buildingName': 'Ngọc Trang',
              },
            },
            'members': <dynamic>[],
          },
        ),
      );
      await session.signIn('user', 'password');

      await tester.pumpWidget(
        MaterialApp(
          theme: AppTheme.light,
          home: TenantOverviewScreen(session: session, onMoveOut: () {}),
        ),
      );

      expect(find.text('Mã tài khoản: RA-001'), findsOneWidget);
      expect(find.text('301'), findsOneWidget);
      expect(find.text('API invoice chưa mở'), findsOneWidget);
      expect(find.text('Chưa tải dữ liệu'), findsNothing);
    },
  );

  testWidgets('renders a forbidden rental account state', (tester) async {
    final session = AppSession(
      _FakeAuthRepository(),
      TenantAccountRepository(
        (_) async => throw const ApiException(
          message: 'Bạn không có quyền thực hiện thao tác này.',
          statusCode: 403,
        ),
      ),
    );
    await session.signIn('user', 'password');

    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light,
        home: TenantOverviewScreen(session: session, onMoveOut: () {}),
      ),
    );

    expect(find.text('Chưa có quyền đọc phạm vi thuê'), findsOneWidget);
    expect(
      find.text('Bạn không có quyền thực hiện thao tác này.'),
      findsOneWidget,
    );
  });

  testWidgets('renders invoice API boundary without fabricated amount', (
    tester,
  ) async {
    await tester.pumpWidget(
      MaterialApp(theme: AppTheme.light, home: const TenantInvoicesScreen()),
    );

    expect(find.text('Hóa đơn của tôi'), findsOneWidget);
    expect(
      find.text(
        'Chưa có API tenant-scoped cho invoice chính thức. Mobile không tự tạo số tiền, trạng thái đã thanh toán hoặc công nợ.',
      ),
      findsOneWidget,
    );
    expect(find.text('—'), findsNWidgets(2));
  });

  testWidgets(
    'keeps move-out submission disabled until backend contract is available',
    (tester) async {
      await tester.pumpWidget(
        MaterialApp(theme: AppTheme.light, home: const TenantMoveOutScreen()),
      );

      expect(find.text('Yêu cầu trả phòng'), findsOneWidget);
      expect(find.text('Gửi yêu cầu · chưa mở'), findsOneWidget);
      final button = tester.widget<FilledButton>(
        find.widgetWithText(FilledButton, 'Gửi yêu cầu · chưa mở'),
      );
      expect(button.onPressed, isNull);
    },
  );
}

class _FakeAuthRepository extends AuthRepository {
  _FakeAuthRepository() : super(ApiClient(baseUrl: 'http://127.0.0.1'));

  @override
  Future<TokenSet> signIn({
    required String username,
    required String password,
  }) async => TokenSet(
    accessToken: 'test-access-token',
    expiresAt: DateTime.now().add(const Duration(hours: 1)),
  );

  @override
  Future<ApplicationConfiguration> loadConfiguration() async =>
      const ApplicationConfiguration(
        user: CurrentUser(
          id: 'user-1',
          userName: 'user',
          name: 'Nguyễn An',
          roles: <String>['tenant'],
        ),
        grantedPolicies: <String, bool>{},
      );

  @override
  void clearSession() {}
}
