// Flutter 3.50 deprecates the legacy Material import in favor of material_ui.
// Keep the SDK Material API until the package policy is approved.
// ignore_for_file: deprecated_member_use

import 'package:flutter/material.dart';

import '../../core/auth/app_session.dart';
import '../../core/network/api_exception.dart';
import '../../core/theme/app_theme.dart';
import 'tenant_account_models.dart';

class TenantOverviewScreen extends StatelessWidget {
  const TenantOverviewScreen({
    required this.session,
    required this.onMoveOut,
    super.key,
  });

  final AppSession session;
  final VoidCallback onMoveOut;

  @override
  Widget build(BuildContext context) {
    final user = session.user;
    final overview = session.tenantOverview;
    return _TenantScaffold(
      title: 'Xin chào, ${user?.name ?? user?.userName ?? 'bạn'}',
      subtitle: overview == null
          ? 'Thông tin thuê được lấy theo tài khoản đang đăng nhập.'
          : 'Thông tin thuê của ${overview.account.displayName}.',
      child: RefreshIndicator(
        onRefresh: session.loadTenantAccount,
        child: ListView(
          padding: const EdgeInsets.fromLTRB(18, 18, 18, 24),
          children: [
            if (session.tenantAccountBusy && overview == null)
              const Padding(
                padding: EdgeInsets.all(36),
                child: Center(child: CircularProgressIndicator()),
              )
            else if (session.tenantAccountError != null && overview == null)
              _TenantAccountError(
                error: session.tenantAccountError!,
                onRetry: session.loadTenantAccount,
              )
            else if (overview == null)
              const _TenantApiBoundaryCard(
                title: 'Chưa có phạm vi thuê',
                message: 'Tài khoản hiện tại chưa được liên kết với RentalAccount. Mobile không tự nhận TenantId hoặc chọn tài khoản thay cho backend.',
              )
            else
              _TenantAccountContent(overview: overview, onMoveOut: onMoveOut),
          ],
        ),
      ),
    );
  }
}

class _TenantAccountContent extends StatelessWidget {
  const _TenantAccountContent({
    required this.overview,
    required this.onMoveOut,
  });

  final TenantMobileOverview overview;
  final VoidCallback onMoveOut;

  @override
  Widget build(BuildContext context) {
    final contract = overview.activeContract;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        _AccountCard(account: overview.account),
        const SizedBox(height: 14),
        if (contract == null)
          const _TenantApiBoundaryCard(
            title: 'Chưa có hợp đồng đang hiệu lực',
            message: 'Backend đã xác định tài khoản thuê nhưng chưa trả về lease/room data. Không hiển thị dữ liệu phòng giả.',
          )
        else ...[
          _RoomCard(contract: contract),
          const SizedBox(height: 14),
          _MembersCard(members: overview.members),
        ],
        const SizedBox(height: 16),
        Row(
          children: const [
            Expanded(
              child: _MetricCard(
                label: 'HÓA ĐƠN KỲ NÀY',
                value: '—',
                hint: 'API invoice chưa mở',
              ),
            ),
            SizedBox(width: 14),
            Expanded(
              child: _MetricCard(
                label: 'CẦN THANH TOÁN',
                value: '—',
                hint: 'Không suy diễn công nợ',
              ),
            ),
          ],
        ),
        const SizedBox(height: 16),
        Card(
          child: Padding(
            padding: const EdgeInsets.all(18),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  'Yêu cầu trả phòng',
                  style: Theme.of(context).textTheme.titleLarge?.copyWith(
                    fontWeight: FontWeight.w800,
                    color: AppTheme.navy,
                  ),
                ),
                const SizedBox(height: 8),
                const Text(
                  'Form mutation chưa mở. Mobile chỉ hiển thị UI, không tự kết thúc hợp đồng hoặc tạo closeout.',
                ),
                const SizedBox(height: 14),
                OutlinedButton.icon(
                  onPressed: onMoveOut,
                  icon: const Icon(Icons.exit_to_app),
                  label: const Text('Xem quy trình trả phòng'),
                ),
              ],
            ),
          ),
        ),
      ],
    );
  }
}

class _AccountCard extends StatelessWidget {
  const _AccountCard({required this.account});

  final TenantMobileAccount account;

  @override
  Widget build(BuildContext context) => Card(
    color: const Color(0xFFEAF2FF),
    child: Padding(
      padding: const EdgeInsets.all(18),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'TÀI KHOẢN THUÊ',
            style: Theme.of(context).textTheme.labelMedium
                ?.copyWith(color: AppTheme.blue, fontWeight: FontWeight.w800),
          ),
          const SizedBox(height: 8),
          Text(
            account.displayName,
            style: Theme.of(context).textTheme.headlineSmall
                ?.copyWith(fontWeight: FontWeight.w800, color: AppTheme.navy),
          ),
          const SizedBox(height: 4),
          Text('Mã tài khoản: ${account.accountNumber}'),
        ],
      ),
    ),
  );
}

class _RoomCard extends StatelessWidget {
  const _RoomCard({required this.contract});

  final TenantMobileContract contract;

  @override
  Widget build(BuildContext context) {
    final room = contract.room;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(18),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'PHÒNG CỦA TÔI',
              style: Theme.of(context).textTheme.labelMedium
                  ?.copyWith(color: AppTheme.blue, fontWeight: FontWeight.w800),
            ),
            const SizedBox(height: 8),
            Text(
              room.roomNumber,
              style: Theme.of(context).textTheme.headlineMedium
                  ?.copyWith(fontWeight: FontWeight.w800, color: AppTheme.navy),
            ),
            const SizedBox(height: 4),
            Text('${room.name} · ${room.buildingName} · ${room.floorName}'),
            const SizedBox(height: 10),
            Text('Hợp đồng ${contract.contractNumber}'),
          ],
        ),
      ),
    );
  }
}

class _MembersCard extends StatelessWidget {
  const _MembersCard({required this.members});

  final List<TenantMobileMember> members;

  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'THÀNH VIÊN TRONG TÀI KHOẢN',
            style: Theme.of(context).textTheme.labelMedium
                ?.copyWith(color: AppTheme.blue, fontWeight: FontWeight.w800),
          ),
          const SizedBox(height: 8),
          if (members.isEmpty)
            const Text('Chưa có thành viên tenant được trả về.')
          else
            ...members.map(
              (member) => ListTile(
                contentPadding: EdgeInsets.zero,
                leading: const CircleAvatar(child: Icon(Icons.person_outline)),
                title: Text(member.displayName),
                subtitle: Text(member.role),
              ),
            ),
        ],
      ),
    ),
  );
}

class _TenantAccountError extends StatelessWidget {
  const _TenantAccountError({required this.error, required this.onRetry});

  final ApiException error;
  final Future<void> Function() onRetry;

  @override
  Widget build(BuildContext context) {
    final forbidden = error.isForbidden;
    return Card(
      color: forbidden ? const Color(0xFFFFF2D8) : const Color(0xFFFFE8EC),
      child: Padding(
        padding: const EdgeInsets.all(18),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Icon(
              forbidden ? Icons.lock_outline : Icons.cloud_off,
              color: forbidden ? AppTheme.warning : AppTheme.error,
            ),
            const SizedBox(height: 10),
            Text(
              forbidden
                  ? 'Chưa có quyền đọc phạm vi thuê'
                  : 'Không tải được phạm vi thuê',
              style: Theme.of(context).textTheme.titleMedium
                  ?.copyWith(fontWeight: FontWeight.w800, color: AppTheme.navy),
            ),
            const SizedBox(height: 6),
            Text(error.message),
            if (!forbidden) ...[
              const SizedBox(height: 12),
              OutlinedButton(onPressed: onRetry, child: const Text('Thử lại')),
            ],
          ],
        ),
      ),
    );
  }
}

class _TenantScaffold extends StatelessWidget {
  const _TenantScaffold({
    required this.title,
    required this.subtitle,
    required this.child,
  });

  final String title;
  final String subtitle;
  final Widget child;

  @override
  Widget build(BuildContext context) => ColoredBox(
    color: AppTheme.background,
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Container(
          color: Colors.white,
          padding: const EdgeInsets.fromLTRB(18, 18, 18, 16),
          child: SafeArea(
            bottom: false,
            child: Row(
              children: [
                const Icon(Icons.menu, color: AppTheme.navy),
                const SizedBox(width: 18),
                Text(
                  'Ngọc Trang',
                  style: Theme.of(context).textTheme.titleLarge?.copyWith(
                    fontWeight: FontWeight.w800,
                    color: AppTheme.navy,
                  ),
                ),
                const Spacer(),
                DecoratedBox(
                  decoration: BoxDecoration(
                    color: const Color(0xFFEAF2FF),
                    borderRadius: BorderRadius.circular(20),
                  ),
                  child: const Padding(
                    padding: EdgeInsets.symmetric(horizontal: 14, vertical: 7),
                    child: Text(
                      'TENANT',
                      style: TextStyle(
                        color: AppTheme.blue,
                        fontSize: 11,
                        fontWeight: FontWeight.w800,
                      ),
                    ),
                  ),
                ),
              ],
            ),
          ),
        ),
        Padding(
          padding: const EdgeInsets.fromLTRB(18, 16, 18, 0),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                title,
                style: Theme.of(context).textTheme.headlineSmall?.copyWith(
                  fontWeight: FontWeight.w800,
                  color: AppTheme.navy,
                ),
              ),
              const SizedBox(height: 4),
              Text(
                subtitle,
                style: Theme.of(context).textTheme.bodySmall
                    ?.copyWith(color: const Color(0xFF7890B2)),
              ),
            ],
          ),
        ),
        Expanded(child: child),
      ],
    ),
  );
}

class _TenantApiBoundaryCard extends StatelessWidget {
  const _TenantApiBoundaryCard({required this.title, required this.message});

  final String title;
  final String message;

  @override
  Widget build(BuildContext context) => Card(
    color: const Color(0xFFFFF2D8),
    child: Padding(
      padding: const EdgeInsets.all(14),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Icon(Icons.info_outline, color: AppTheme.warning),
          const SizedBox(width: 10),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  title,
                  style: const TextStyle(
                    color: AppTheme.warning,
                    fontWeight: FontWeight.w800,
                  ),
                ),
                const SizedBox(height: 4),
                Text(message, style: const TextStyle(color: Color(0xFF704C18))),
              ],
            ),
          ),
        ],
      ),
    ),
  );
}

class _MetricCard extends StatelessWidget {
  const _MetricCard({
    required this.label,
    required this.value,
    required this.hint,
  });

  final String label;
  final String value;
  final String hint;

  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: const EdgeInsets.all(14),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            label,
            style: Theme.of(context).textTheme.labelSmall
                ?.copyWith(color: const Color(0xFF7890B2)),
          ),
          const SizedBox(height: 8),
          Text(
            value,
            style: Theme.of(context).textTheme.headlineSmall
                ?.copyWith(fontWeight: FontWeight.w800, color: AppTheme.navy),
          ),
          const SizedBox(height: 4),
          Text(
            hint,
            style: const TextStyle(color: AppTheme.warning, fontSize: 11),
          ),
        ],
      ),
    ),
  );
}
