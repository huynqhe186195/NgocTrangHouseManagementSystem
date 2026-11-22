import '../../core/network/api_client.dart';
import '../../core/network/api_exception.dart';
import 'tenant_account_models.dart';

typedef JsonGetter = Future<dynamic> Function(String path);

class TenantAccountRepository {
  TenantAccountRepository(this._getJson);

  factory TenantAccountRepository.fromApi(ApiClient api) =>
      TenantAccountRepository(api.getJson);

  final JsonGetter _getJson;

  Future<TenantMobileOverview> getMe() async {
    final payload = await _getJson('/api/mobile/rental-account/me');
    if (payload is! Map<String, dynamic>) {
      throw const ApiException(
        message: 'Máy chủ trả về phạm vi tài khoản thuê không hợp lệ.',
        code: 'Common:InvalidRentalAccountOverview',
      );
    }
    return TenantMobileOverview.fromJson(payload);
  }
}
