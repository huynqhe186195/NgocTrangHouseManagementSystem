import 'package:flutter_test/flutter_test.dart';

import 'package:ngoctrangbuilding_app/features/buildings/building_repository.dart';

void main() {
  test('maps a backend building DTO', () {
    final building = BuildingSummary.fromJson({
      'id': 'building-1',
      'name': 'Tòa nhà mẫu',
      'address': '123 Đường Mẫu',
      'description': 'Dữ liệu backend',
    });

    expect(building.id, 'building-1');
    expect(building.name, 'Tòa nhà mẫu');
    expect(building.address, '123 Đường Mẫu');
    expect(building.description, 'Dữ liệu backend');
  });
}
