import { Link } from 'react-router-dom';

export function ForbiddenPage() {
  return (
    <div className="mx-auto flex max-w-md flex-col items-center gap-4 py-20 text-center">
      <p className="font-display text-5xl font-extrabold text-primary">403</p>
      <h1 className="font-display text-xl font-bold text-on-surface">Không đủ quyền truy cập</h1>
      <p className="text-sm text-on-surface-variant">
        Tài khoản của bạn không được cấp quyền cho màn hình này. Liên hệ quản trị viên nếu cần bổ sung.
      </p>
      <Link
        to="/"
        className="rounded-[0.5rem] bg-primary px-4 py-2.5 text-sm font-semibold text-on-primary"
      >
        Về trang tổng quan
      </Link>
    </div>
  );
}
