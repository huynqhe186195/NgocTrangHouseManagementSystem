// Flutter 3.50 deprecates the legacy Material import in favor of material_ui.
// This app intentionally uses the SDK Material API until the package policy is approved.
// ignore_for_file: deprecated_member_use

import 'package:flutter/material.dart';

import 'core/auth/app_session.dart';
import 'core/auth/auth_repository.dart';
import 'core/config/app_config.dart';
import 'core/network/api_client.dart';
import 'core/theme/app_theme.dart';
import 'features/buildings/building_repository.dart';
import 'features/tenant/tenant_account_repository.dart';
import 'features/shell/role_shell.dart';

class NgocTrangBuildingApp extends StatefulWidget {
  const NgocTrangBuildingApp({this.apiClient, super.key});

  final ApiClient? apiClient;

  @override
  State<NgocTrangBuildingApp> createState() => _NgocTrangBuildingAppState();
}

class _NgocTrangBuildingAppState extends State<NgocTrangBuildingApp> {
  late final ApiClient _apiClient;
  late final AppSession _session;

  @override
  void initState() {
    super.initState();
    _apiClient = widget.apiClient ?? ApiClient(baseUrl: AppConfig.apiBaseUrl);
    _session = AppSession(
      AuthRepository(_apiClient),
      TenantAccountRepository.fromApi(_apiClient),
    );
  }

  @override
  void dispose() {
    _session.dispose();
    _apiClient.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
    animation: _session,
    builder: (context, _) => MaterialApp(
      debugShowCheckedModeBanner: false,
      title: 'Ngọc Trang Building',
      theme: AppTheme.light,
      home: _session.isAuthenticated
          ? RoleShell(
              session: _session,
              buildingRepository: BuildingRepository(_apiClient),
            )
          : LoginScreen(session: _session),
    ),
  );
}

class LoginScreen extends StatefulWidget {
  const LoginScreen({required this.session, super.key});

  final AppSession session;

  @override
  State<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends State<LoginScreen> {
  final _formKey = GlobalKey<FormState>();
  final _username = TextEditingController();
  final _password = TextEditingController();
  bool _obscure = true;

  @override
  void dispose() {
    _username.dispose();
    _password.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!_formKey.currentState!.validate() || widget.session.busy) return;
    try {
      await widget.session.signIn(_username.text.trim(), _password.text);
    } catch (error) {
      if (!mounted) return;
      final message = error.toString().contains('invalid_grant')
          ? 'Tên đăng nhập hoặc mật khẩu không đúng.'
          : widget.session.lastError?.message ?? 'Đăng nhập thất bại.';
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text(message)));
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    body: SafeArea(
      child: Center(
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24),
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 440),
            child: Card(
              child: Padding(
                padding: const EdgeInsets.all(24),
                child: Form(
                  key: _formKey,
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      const Icon(
                        Icons.apartment,
                        size: 54,
                        color: AppTheme.blue,
                      ),
                      const SizedBox(height: 12),
                      Text(
                        'Ngọc Trang Building',
                        textAlign: TextAlign.center,
                        style: Theme.of(context).textTheme.headlineSmall
                            ?.copyWith(
                              fontWeight: FontWeight.w800,
                              color: AppTheme.navy,
                            ),
                      ),
                      const SizedBox(height: 8),
                      Text(
                        'Ứng dụng mobile cho Owner, Staff và Tenant',
                        textAlign: TextAlign.center,
                        style: Theme.of(context).textTheme.bodyMedium,
                      ),
                      const SizedBox(height: 28),
                      TextFormField(
                        controller: _username,
                        decoration: const InputDecoration(
                          labelText: 'Tên đăng nhập',
                        ),
                        validator: (value) =>
                            value == null || value.trim().isEmpty
                            ? 'Nhập tên đăng nhập.'
                            : null,
                      ),
                      const SizedBox(height: 14),
                      TextFormField(
                        controller: _password,
                        obscureText: _obscure,
                        decoration: InputDecoration(
                          labelText: 'Mật khẩu',
                          suffixIcon: IconButton(
                            onPressed: () =>
                                setState(() => _obscure = !_obscure),
                            icon: Icon(
                              _obscure
                                  ? Icons.visibility
                                  : Icons.visibility_off,
                            ),
                          ),
                        ),
                        validator: (value) => value == null || value.isEmpty
                            ? 'Nhập mật khẩu.'
                            : null,
                      ),
                      const SizedBox(height: 22),
                      FilledButton(
                        onPressed: widget.session.busy ? null : _submit,
                        child: widget.session.busy
                            ? const SizedBox(
                                height: 20,
                                width: 20,
                                child: CircularProgressIndicator(
                                  strokeWidth: 2,
                                ),
                              )
                            : const Text('Đăng nhập'),
                      ),
                      const SizedBox(height: 18),
                      Text(
                        'Dữ liệu và quyền được lấy từ backend. Không chọn role thủ công trên thiết bị.',
                        textAlign: TextAlign.center,
                        style: Theme.of(context).textTheme.bodySmall,
                      ),
                    ],
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    ),
  );
}
