# ngoctrangbuilding_app

Ứng dụng Flutter mobile dùng chung cho Owner, Staff và Tenant của Ngọc Trang Building.

## SDK

- Flutter `3.50.0`
- Dart `3.14.0`

## Chạy

```powershell
flutter pub get
flutter analyze
flutter test
```

API base URL có thể thay bằng `--dart-define`:

```powershell
# Android emulator -> host machine
flutter run --dart-define=NGOC_TRANG_API_BASE_URL=https://10.0.2.2:44311

# iOS simulator / desktop
flutter run --dart-define=NGOC_TRANG_API_BASE_URL=https://127.0.0.1:44311

# Thiết bị thật: dùng hostname/IP LAN có chứng chỉ HTTPS được thiết bị tin cậy.
```

Nếu không truyền `--dart-define`, app dùng `10.0.2.2` trên Android và `127.0.0.1` trên iOS simulator/desktop. Không tắt kiểm tra chứng chỉ TLS để vượt qua lỗi HTTPS. Bản release hiện vẫn dùng application ID mẫu và debug signing của scaffold; chưa được phát hành production.

Mobile không tự chọn role. Sau khi đăng nhập, app đọc identity và granted policies từ `/api/abp/application-configuration`, sau đó chọn shell phù hợp. Dữ liệu nghiệp vụ phải đi qua API backend; không có fallback fixture trong runtime.

## Shell hiện có

- Owner/Staff: shell điều hướng chung, danh sách tòa nhà đọc từ API và các trạng thái loading/error/forbidden/empty.
- Tenant: bottom navigation theo handoff mobile gồm Home, Phòng, Hóa đơn và Hồ sơ; các màn Tổng quan, Hóa đơn và Yêu cầu trả phòng đã dựng theo design.
- Invoice và move-out chỉ hiển thị boundary an toàn khi backend chưa cung cấp read model tenant-scoped/production-safe endpoint; không tự tạo số tiền, công nợ, trạng thái thanh toán hoặc kết thúc hợp đồng.
- Khảo sát backend hiện xác nhận chưa có endpoint production-safe để Tenant tự đọc profile/contract/room/invoice/utility hoặc tự tạo move-out request. Các route legacy là admin/unscoped hoặc bị default-deny; Slice B/C là staging/operator-scoped. Mobile không gọi các route này.
- Khi backend bổ sung contract mobile, endpoint phải bind tenant từ authenticated subject và kiểm tra active ContractTenant link ở server; client không gửi TenantId/BuildingId để làm authority.
- Không dùng query/header phía client để quyết định role hoặc tenant scope.
