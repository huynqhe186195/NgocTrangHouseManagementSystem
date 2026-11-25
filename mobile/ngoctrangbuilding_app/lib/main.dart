import 'package:flutter/material.dart';

import 'app.dart';
import 'core/config/app_config.dart';
import 'core/network/api_client.dart';

void main() async {
  WidgetsFlutterBinding.ensureInitialized();
  final apiClient = await ApiClient.create(baseUrl: AppConfig.apiBaseUrl);
  runApp(NgocTrangBuildingApp(apiClient: apiClient));
}
