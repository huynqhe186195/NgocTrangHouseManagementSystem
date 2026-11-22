// Flutter 3.50 deprecates the legacy Material import in favor of material_ui.
// Keep the SDK Material API until the package policy is approved.
// ignore_for_file: deprecated_member_use

import 'package:flutter/material.dart';

import '../../core/theme/app_theme.dart';

class TenantMoveOutScreen extends StatefulWidget {
  const TenantMoveOutScreen({super.key});

  @override
  State<TenantMoveOutScreen> createState() => _TenantMoveOutScreenState();
}

class _TenantMoveOutScreenState extends State<TenantMoveOutScreen> {
  final _note = TextEditingController();
  String? _reason;

  @override
  void dispose() {
    _note.dispose();
    super.dispose();
  }

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
                'Yêu cầu trả phòng',
                style: Theme.of(context).textTheme.headlineSmall?.copyWith(
                  fontWeight: FontWeight.w800,
                  color: AppTheme.navy,
                ),
              ),
              const SizedBox(height: 4),
              const Text(
                'Quy trình không tự động kết thúc hợp đồng.',
                style: TextStyle(color: Color(0xFF7890B2)),
              ),
            ],
          ),
        ),
        Expanded(
          child: ListView(
            padding: const EdgeInsets.fromLTRB(18, 16, 18, 24),
            children: [
              Card(
                child: Padding(
                  padding: const EdgeInsets.all(16),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        'Tạo yêu cầu',
                        style: Theme.of(context).textTheme.titleLarge?.copyWith(
                          fontWeight: FontWeight.w800,
                          color: AppTheme.navy,
                        ),
                      ),
                      const SizedBox(height: 18),
                      const _FieldLabel('Phòng thuê'),
                      const _ReadOnlyField(
                        text: 'Sẽ lấy từ hợp đồng tenant-scoped',
                      ),
                      const SizedBox(height: 14),
                      const _FieldLabel('Ngày dự kiến'),
                      const _ReadOnlyField(
                        text: 'Chọn sau khi có hợp đồng đang hiệu lực',
                      ),
                      const SizedBox(height: 14),
                      const _FieldLabel('Lý do trả phòng'),
                      DropdownButtonFormField<String>(
                        value: _reason,
                        decoration: const InputDecoration(
                          hintText: 'Chọn lý do',
                        ),
                        items: const [
                          DropdownMenuItem(
                            value: 'end',
                            child: Text('Hết hạn hợp đồng'),
                          ),
                          DropdownMenuItem(
                            value: 'early',
                            child: Text('Chấm dứt trước hạn'),
                          ),
                        ],
                        onChanged: (value) => setState(() => _reason = value),
                      ),
                      const SizedBox(height: 14),
                      const _FieldLabel('Ghi chú'),
                      TextField(
                        controller: _note,
                        maxLines: 4,
                        decoration: const InputDecoration(
                          hintText: 'Nhập ghi chú (nếu có)…',
                        ),
                      ),
                      const SizedBox(height: 16),
                      Container(
                        width: double.infinity,
                        padding: const EdgeInsets.all(12),
                        decoration: BoxDecoration(
                          color: const Color(0xFFFFF2D8),
                          borderRadius: BorderRadius.circular(12),
                        ),
                        child: const Text(
                          'Yêu cầu chưa được gửi lên API. Chưa có đủ contract, final meter, room check và closeout state từ backend.',
                          style: TextStyle(
                            color: AppTheme.warning,
                            fontWeight: FontWeight.w600,
                          ),
                        ),
                      ),
                      const SizedBox(height: 16),
                      SizedBox(
                        width: double.infinity,
                        child: FilledButton(
                          onPressed: null,
                          child: const Text('Gửi yêu cầu · chưa mở'),
                        ),
                      ),
                    ],
                  ),
                ),
              ),
              const SizedBox(height: 14),
              const Card(
                child: Padding(
                  padding: EdgeInsets.all(16),
                  child: Text(
                    'Sau khi backend mở API, mobile chỉ tạo request. Việc final meter → room check → invoice/debt → deposit → closeout vẫn do backend quyết định.',
                  ),
                ),
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

class _FieldLabel extends StatelessWidget {
  const _FieldLabel(this.text);
  final String text;
  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: 7),
    child: Text(
      text,
      style: const TextStyle(fontWeight: FontWeight.w700, color: AppTheme.navy),
    ),
  );
}

class _ReadOnlyField extends StatelessWidget {
  const _ReadOnlyField({required this.text});
  final String text;
  @override
  Widget build(BuildContext context) => Container(
    width: double.infinity,
    padding: const EdgeInsets.all(16),
    decoration: BoxDecoration(
      color: const Color(0xFFF8FAFD),
      border: Border.all(color: const Color(0xFFD9E1EC)),
      borderRadius: BorderRadius.circular(12),
    ),
    child: Text(text, style: const TextStyle(color: Color(0xFF7890B2))),
  );
}
