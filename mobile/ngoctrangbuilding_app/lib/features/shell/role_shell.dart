// Flutter 3.50 deprecates the legacy Material import in favor of material_ui.
// Keep the SDK Material API until the package policy is approved.
// ignore_for_file: deprecated_member_use

import 'package:flutter/material.dart';

import '../../core/auth/app_session.dart';
import '../../core/network/api_exception.dart';
import '../../core/navigation/role_navigation.dart';
import '../../core/theme/app_theme.dart';
import '../buildings/building_repository.dart';
import '../tenant/tenant_invoices_screen.dart';
import '../tenant/tenant_move_out_screen.dart';
import '../tenant/tenant_overview_screen.dart';

class RoleShell extends StatefulWidget {
  const RoleShell({
    required this.session,
    required this.buildingRepository,
    super.key,
  });

  final AppSession session;
  final BuildingRepository buildingRepository;

  @override
  State<RoleShell> createState() => _RoleShellState();
}

class _RoleShellState extends State<RoleShell> {
  int _index = 0;
  bool _showMoveOut = false;

  AppRole get role =>
      resolveRole(widget.session.user!, widget.session.hasPermission);
  bool get isTenant => role == AppRole.tenant;

  @override
  Widget build(BuildContext context) {
    if (role == AppRole.unknown) {
      return _UnknownRoleScreen(onSignOut: widget.session.signOut);
    }
    if (_showMoveOut) {
      return Scaffold(
        body: const TenantMoveOutScreen(),
        floatingActionButton: FloatingActionButton.small(
          onPressed: () => setState(() => _showMoveOut = false),
          tooltip: 'Quay lại',
          child: const Icon(Icons.arrow_back),
        ),
      );
    }

    final pages = isTenant
        ? <Widget>[
            TenantOverviewScreen(
              session: widget.session,
              onMoveOut: () => setState(() => _showMoveOut = true),
            ),
            const _TenantRoomPlaceholder(),
            const TenantInvoicesScreen(),
            _ProfileScreen(session: widget.session),
          ]
        : <Widget>[
            _OperatorDashboard(
              session: widget.session,
              repository: widget.buildingRepository,
            ),
            _OperatorModuleScreen(role: role),
            _OperatorModuleScreen(role: role, detail: true),
            _ProfileScreen(session: widget.session),
          ];

    final labels = isTenant
        ? const ['Home', 'Phòng', 'Hóa đơn', 'Hồ sơ']
        : const ['Tổng quan', 'Tòa nhà', 'Vận hành', 'Hồ sơ'];
    final icons = isTenant
        ? const [
            Icons.home_outlined,
            Icons.meeting_room_outlined,
            Icons.receipt_long_outlined,
            Icons.person_outline,
          ]
        : const [
            Icons.home_outlined,
            Icons.apartment_outlined,
            Icons.assignment_outlined,
            Icons.person_outline,
          ];

    return Scaffold(
      body: IndexedStack(index: _index, children: pages),
      bottomNavigationBar: NavigationBar(
        selectedIndex: _index,
        onDestinationSelected: (index) => setState(() => _index = index),
        destinations: [
          for (var i = 0; i < labels.length; i++)
            NavigationDestination(icon: Icon(icons[i]), label: labels[i]),
        ],
      ),
    );
  }
}

class _OperatorDashboard extends StatefulWidget {
  const _OperatorDashboard({required this.session, required this.repository});
  final AppSession session;
  final BuildingRepository repository;
  @override
  State<_OperatorDashboard> createState() => _OperatorDashboardState();
}

class _OperatorDashboardState extends State<_OperatorDashboard> {
  late Future<List<BuildingSummary>> _future;
  @override
  void initState() {
    super.initState();
    _future = widget.repository.list();
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(
      title: const Text('Ngọc Trang Building'),
      actions: [
        IconButton(
          onPressed: widget.session.signOut,
          icon: const Icon(Icons.logout),
        ),
      ],
    ),
    body: RefreshIndicator(
      onRefresh: () async => setState(() => _future = widget.repository.list()),
      child: ListView(
        padding: const EdgeInsets.all(20),
        children: [
          Text(
            'Tổng quan vận hành',
            style: Theme.of(context).textTheme.headlineSmall
                ?.copyWith(fontWeight: FontWeight.w800, color: AppTheme.navy),
          ),
          const SizedBox(height: 6),
          Text(
            'Role: ${roleLabel(resolveRole(widget.session.user!, widget.session.hasPermission))} · dữ liệu từ backend',
            style: const TextStyle(color: Color(0xFF7890B2)),
          ),
          const SizedBox(height: 16),
          const _OperatorBoundary(),
          const SizedBox(height: 18),
          FutureBuilder<List<BuildingSummary>>(
            future: _future,
            builder: (context, snapshot) {
              if (snapshot.connectionState == ConnectionState.waiting) {
                return const Center(
                  child: Padding(
                    padding: EdgeInsets.all(28),
                    child: CircularProgressIndicator(),
                  ),
                );
              }
              if (snapshot.hasError) {
                return _OperatorState(
                  title: 'Không tải được tòa nhà',
                  message: snapshot.error is ApiException
                      ? (snapshot.error as ApiException).message
                      : 'Lỗi kết nối API.',
                  onRetry: () =>
                      setState(() => _future = widget.repository.list()),
                );
              }
              final buildings = snapshot.data ?? const <BuildingSummary>[];
              if (buildings.isEmpty) {
                return const _OperatorState(
                  title: 'Chưa có tòa nhà',
                  message:
                      'Backend không trả về dữ liệu trong phạm vi tài khoản.',
                );
              }
              return Column(
                children: buildings
                    .map(
                      (building) => Card(
                        child: ListTile(
                          leading: const Icon(
                            Icons.apartment,
                            color: AppTheme.blue,
                          ),
                          title: Text(building.name),
                          subtitle: Text(building.address),
                        ),
                      ),
                    )
                    .toList(),
              );
            },
          ),
        ],
      ),
    ),
  );
}

class _OperatorBoundary extends StatelessWidget {
  const _OperatorBoundary();
  @override
  Widget build(BuildContext context) => Card(
    color: const Color(0xFFEAF2FF),
    child: const Padding(
      padding: EdgeInsets.all(14),
      child: Text(
        'Role và permission do backend cấp. Mobile chỉ điều hướng và hiển thị dữ liệu được API cho phép; không tự mở quyền thao tác.',
      ),
    ),
  );
}

class _OperatorState extends StatelessWidget {
  const _OperatorState({
    required this.title,
    required this.message,
    this.onRetry,
  });
  final String title;
  final String message;
  final VoidCallback? onRetry;
  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: const EdgeInsets.all(20),
      child: Column(
        children: [
          const Icon(Icons.cloud_off, size: 38, color: AppTheme.navy),
          const SizedBox(height: 10),
          Text(title, style: Theme.of(context).textTheme.titleMedium),
          const SizedBox(height: 6),
          Text(message, textAlign: TextAlign.center),
          if (onRetry != null) ...[
            const SizedBox(height: 12),
            OutlinedButton(onPressed: onRetry, child: const Text('Thử lại')),
          ],
        ],
      ),
    ),
  );
}

class _OperatorModuleScreen extends StatelessWidget {
  const _OperatorModuleScreen({required this.role, this.detail = false});
  final AppRole role;
  final bool detail;
  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: Text(detail ? 'Vận hành' : 'Tòa nhà')),
    body: Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Card(
          child: Padding(
            padding: const EdgeInsets.all(20),
            child: Text(
              'Màn ${detail ? 'vận hành' : 'tòa nhà'} sẽ gọi API theo permission của ${roleLabel(role)}. Chưa có endpoint mobile-specific được mở thêm trong phase này.',
              textAlign: TextAlign.center,
            ),
          ),
        ),
      ),
    ),
  );
}

class _UnknownRoleScreen extends StatelessWidget {
  const _UnknownRoleScreen({required this.onSignOut});

  final VoidCallback onSignOut;

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Ngọc Trang Building')),
    body: Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Card(
          child: Padding(
            padding: const EdgeInsets.all(20),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                const Icon(
                  Icons.lock_outline,
                  size: 42,
                  color: AppTheme.warning,
                ),
                const SizedBox(height: 12),
                Text(
                  'Chưa xác định được role',
                  style: Theme.of(context).textTheme.titleLarge,
                ),
                const SizedBox(height: 8),
                const Text(
                  'Backend chưa cấp role hoặc permission hợp lệ cho tài khoản này. Mobile không tự chọn role thay cho máy chủ.',
                  textAlign: TextAlign.center,
                ),
                const SizedBox(height: 16),
                FilledButton.tonalIcon(
                  onPressed: onSignOut,
                  icon: const Icon(Icons.logout),
                  label: const Text('Đăng xuất'),
                ),
              ],
            ),
          ),
        ),
      ),
    ),
  );
}

class _ProfileScreen extends StatelessWidget {
  const _ProfileScreen({required this.session});
  final AppSession session;
  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Hồ sơ')),
    body: ListView(
      padding: const EdgeInsets.all(20),
      children: [
        ListTile(
          leading: const CircleAvatar(child: Icon(Icons.person)),
          title: Text(
            session.user?.name ?? session.user?.userName ?? 'Tài khoản',
          ),
          subtitle: Text(session.user?.email ?? session.user?.userName ?? ''),
        ),
        const SizedBox(height: 16),
        const Card(
          child: Padding(
            padding: EdgeInsets.all(16),
            child: Text(
              'Thông tin hồ sơ và hỗ trợ sẽ đọc từ identity/backend khi API tương ứng được mở.',
            ),
          ),
        ),
        const SizedBox(height: 16),
        FilledButton.tonalIcon(
          onPressed: session.signOut,
          icon: const Icon(Icons.logout),
          label: const Text('Đăng xuất'),
        ),
      ],
    ),
  );
}

class _TenantRoomPlaceholder extends StatelessWidget {
  const _TenantRoomPlaceholder();
  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Phòng của tôi')),
    body: const Center(
      child: Padding(
        padding: EdgeInsets.all(24),
        child: Text(
          'Chưa có tenant-scoped room endpoint. Không hiển thị dữ liệu phòng giả.',
          textAlign: TextAlign.center,
        ),
      ),
    ),
  );
}
