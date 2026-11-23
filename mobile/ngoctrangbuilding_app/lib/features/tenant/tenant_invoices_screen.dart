// Flutter 3.50 deprecates the legacy Material import in favor of material_ui.
// Keep the SDK Material API until the package policy is approved.
// ignore_for_file: deprecated_member_use

import 'package:flutter/material.dart';

import '../../core/theme/app_theme.dart';

class TenantInvoicesScreen extends StatelessWidget {
  const TenantInvoicesScreen({super.key});

  @override
  Widget build(BuildContext context) => ColoredBox(
    color: AppTheme.background,
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const _TenantHeader(),
        Padding(
          padding: const EdgeInsets.fromLTRB(18, 16, 18, 0),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                'Hóa đơn của tôi',
                style: Theme.of(context).textTheme.headlineSmall?.copyWith(
                  fontWeight: FontWeight.w800,
                  color: AppTheme.navy,
                ),
              ),
              const SizedBox(height: 4),
              const Text(
                'Hóa đơn được phân loại theo kỳ và trạng thái.',
                style: TextStyle(color: Color(0xFF7890B2)),
              ),
            ],
          ),
        ),
        Expanded(
          child: ListView(
            padding: const EdgeInsets.fromLTRB(18, 16, 18, 24),
            children: const [
              _InvoiceBoundaryCard(),
              SizedBox(height: 14),
              _InvoiceCard(
                title: 'Hóa đơn kỳ hiện tại',
                status: 'Chưa có dữ liệu từ API',
              ),
              _InvoiceCard(
                title: 'Lịch sử hóa đơn',
                status: 'Chưa có dữ liệu từ API',
              ),
            ],
          ),
        ),
      ],
    ),
  );
}

class _TenantHeader extends StatelessWidget {
  const _TenantHeader();

  @override
  Widget build(BuildContext context) => Container(
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
            style: Theme.of(context).textTheme.titleLarge
                ?.copyWith(fontWeight: FontWeight.w800, color: AppTheme.navy),
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
  );
}

class _InvoiceBoundaryCard extends StatelessWidget {
  const _InvoiceBoundaryCard();

  @override
  Widget build(BuildContext context) => Card(
    color: const Color(0xFFFFF2D8),
    child: const Padding(
      padding: EdgeInsets.all(14),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(Icons.lock_outline, color: AppTheme.warning),
          SizedBox(width: 10),
          Expanded(
            child: Text(
              'Chưa có API tenant-scoped cho invoice chính thức. Mobile không tự tạo số tiền, trạng thái đã thanh toán hoặc công nợ.',
            ),
          ),
        ],
      ),
    ),
  );
}

class _InvoiceCard extends StatelessWidget {
  const _InvoiceCard({required this.title, required this.status});

  final String title;
  final String status;

  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            title,
            style: Theme.of(context).textTheme.titleMedium
                ?.copyWith(fontWeight: FontWeight.w800, color: AppTheme.navy),
          ),
          const SizedBox(height: 10),
          Text(
            '—',
            style: Theme.of(context).textTheme.headlineSmall
                ?.copyWith(fontWeight: FontWeight.w800, color: AppTheme.navy),
          ),
          const SizedBox(height: 8),
          DecoratedBox(
            decoration: BoxDecoration(
              color: const Color(0xFFF0F3F8),
              borderRadius: BorderRadius.circular(18),
            ),
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 7),
              child: Text(
                status,
                style: const TextStyle(color: Color(0xFF7890B2), fontSize: 12),
              ),
            ),
          ),
        ],
      ),
    ),
  );
}
