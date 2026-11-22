import 'package:flutter_test/flutter_test.dart';

import 'package:ngoctrangbuilding_app/app.dart';

void main() {
  testWidgets('shows the backend login boundary', (tester) async {
    await tester.pumpWidget(const NgocTrangBuildingApp());

    expect(find.text('Ngọc Trang Building'), findsOneWidget);
    expect(
      find.text('Ứng dụng mobile cho Owner, Staff và Tenant'),
      findsOneWidget,
    );
    expect(find.text('Đăng nhập'), findsOneWidget);
  });
}
