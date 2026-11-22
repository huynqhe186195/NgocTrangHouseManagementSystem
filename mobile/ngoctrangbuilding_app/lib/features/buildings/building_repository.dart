import '../../core/network/api_client.dart';
import '../../core/network/api_exception.dart';

class BuildingSummary {
  const BuildingSummary({
    required this.id,
    required this.name,
    required this.address,
    this.description,
  });

  final String id;
  final String name;
  final String address;
  final String? description;

  factory BuildingSummary.fromJson(Map<String, dynamic> json) {
    final id = json['id'];
    final name = json['name'];
    final address = json['address'];
    if (id is! String || name is! String || address is! String) {
      throw const ApiException(
        message: 'Máy chủ trả về tòa nhà không hợp lệ.',
        code: 'Common:InvalidBuilding',
      );
    }
    return BuildingSummary(
      id: id,
      name: name,
      address: address,
      description: json['description'] as String?,
    );
  }
}

class BuildingRepository {
  BuildingRepository(this._api);

  final ApiClient _api;

  Future<List<BuildingSummary>> list() async {
    final payload = await _api.getJson('/api/building-management/building');
    if (payload is! List) {
      throw const ApiException(
        message: 'Máy chủ trả về danh sách tòa nhà không hợp lệ.',
        code: 'Common:InvalidBuildingList',
      );
    }
    return payload
        .whereType<Map<String, dynamic>>()
        .map(BuildingSummary.fromJson)
        .toList(growable: false);
  }
}
