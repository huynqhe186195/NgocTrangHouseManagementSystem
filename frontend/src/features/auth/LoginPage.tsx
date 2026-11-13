import { useState } from 'react';
import { Link, Navigate, useLocation, useNavigate } from 'react-router-dom';
import { Alert } from '../../components/ui/Alert';
import { ApiError } from '../../lib/http';
import { useAuth } from '../../lib/auth/AuthProvider';
import { AuthLayout } from './AuthLayout';

type RoleTab = 'owner' | 'staff' | 'tenant';

interface RoleOption {
  id: RoleTab;
  label: string;
  icon: string;
  available: boolean;
}

const roles: RoleOption[] = [
  { id: 'owner', label: 'Chủ cơ sở', icon: 'real_estate_agent', available: true },
  { id: 'staff', label: 'Nhân viên', icon: 'support_agent', available: true },
  { id: 'tenant', label: 'Khách thuê', icon: 'person', available: false },
];

export function LoginPage() {
  const { signIn, user } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [role, setRole] = useState<RoleTab>('owner');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [remember, setRemember] = useState(false);
  const [showPassword, setShowPassword] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});

  const from = (location.state as { from?: string } | null)?.from ?? '/';

  if (user) {
    return <Navigate to={from} replace />;
  }

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    setError(null);
    setFieldErrors({});

    if (!username.trim()) {
      setFieldErrors({ username: 'Vui lòng nhập email hoặc số điện thoại.' });
      return;
    }
    if (!password) {
      setFieldErrors({ password: 'Vui lòng nhập mật khẩu.' });
      return;
    }

    setSubmitting(true);
    try {
      await signIn(username.trim(), password, remember);
      navigate(from, { replace: true });
    } catch (caught) {
      if (caught instanceof ApiError) {
        setError(caught.message);
      } else {
        setError('Đăng nhập thất bại. Vui lòng thử lại.');
      }
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <AuthLayout>
      <div className="mx-auto w-full max-w-md space-y-6">
        <div className="space-y-1.5">
          <h1 className="font-display text-2xl font-bold tracking-tight text-on-surface sm:text-3xl">
            Đăng nhập hệ thống
          </h1>
          <p className="text-sm text-on-surface-variant">
            Nhập email hoặc số điện thoại đã đăng ký để tiếp tục
          </p>
        </div>

        <div
          role="tablist"
          aria-label="Vai trò"
          className="flex items-center gap-1 rounded-xl border border-outline-variant/60 bg-surface-low p-1"
        >
          {roles.map((item) => {
            const selected = role === item.id;
            return (
              <button
                key={item.id}
                role="tab"
                type="button"
                aria-selected={selected}
                disabled={!item.available}
                title={item.available ? undefined : 'Cổng khách thuê chưa được mở'}
                onClick={() => setRole(item.id)}
                className={
                  'flex flex-1 items-center justify-center gap-1.5 rounded-lg px-3 py-1.5 text-xs transition-all ' +
                  (selected
                    ? 'bg-surface-lowest font-semibold text-primary shadow-sm'
                    : 'font-medium text-on-surface-variant hover:text-on-surface ') +
                  (item.available ? '' : ' cursor-not-allowed opacity-45')
                }
              >
                <span className="material-symbols-outlined text-sm">{item.icon}</span>
                {item.label}
              </button>
            );
          })}
        </div>

        {error && <Alert tone="error">{error}</Alert>}

        <form className="space-y-4" onSubmit={handleSubmit} noValidate>
          <div className="space-y-1.5">
            <label
              htmlFor="identifier"
              className="block text-xs font-semibold tracking-wide text-on-surface uppercase"
            >
              Email hoặc Số điện thoại <span className="text-error">*</span>
            </label>
            <div className="relative flex items-center">
              <span className="material-symbols-outlined pointer-events-none absolute left-3.5 text-xl text-outline">
                mail
              </span>
              <input
                id="identifier"
                type="text"
                required
                autoComplete="username"
                placeholder="name@example.com hoặc 0988xxxxxx"
                value={username}
                onChange={(event) => setUsername(event.target.value)}
                className="h-11 w-full rounded-xl border border-outline-variant bg-surface pl-11 pr-4 text-sm text-on-surface shadow-sm transition-all placeholder:text-outline focus:border-primary focus:ring-2 focus:ring-primary/20 focus:outline-none"
              />
            </div>
            {fieldErrors.username && (
              <p className="text-xs text-error">{fieldErrors.username}</p>
            )}
          </div>

          <div className="space-y-1.5">
            <div className="flex items-center justify-between">
              <label
                htmlFor="password"
                className="block text-xs font-semibold tracking-wide text-on-surface uppercase"
              >
                Mật khẩu <span className="text-error">*</span>
              </label>
              <Link
                to="/forgot-password"
                className="text-xs font-semibold text-primary transition-colors hover:text-primary-dark hover:underline"
              >
                Quên mật khẩu?
              </Link>
            </div>
            <div className="relative flex items-center">
              <span className="material-symbols-outlined pointer-events-none absolute left-3.5 text-xl text-outline">
                lock
              </span>
              <input
                id="password"
                type={showPassword ? 'text' : 'password'}
                required
                autoComplete="current-password"
                placeholder="Nhập mật khẩu của bạn"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
                className="h-11 w-full rounded-xl border border-outline-variant bg-surface pl-11 pr-11 text-sm text-on-surface shadow-sm transition-all placeholder:text-outline focus:border-primary focus:ring-2 focus:ring-primary/20 focus:outline-none"
              />
              <button
                type="button"
                onClick={() => setShowPassword((value) => !value)}
                aria-label={showPassword ? 'Ẩn mật khẩu' : 'Hiện mật khẩu'}
                title="Hiện/ẩn mật khẩu"
                className="absolute right-3 p-1 text-outline transition-colors hover:text-on-surface"
              >
                <span className="material-symbols-outlined text-lg">
                  {showPassword ? 'visibility_off' : 'visibility'}
                </span>
              </button>
            </div>
            {fieldErrors.password && (
              <p className="text-xs text-error">{fieldErrors.password}</p>
            )}
          </div>

          <div className="flex items-center justify-between pt-1">
            <label className="flex cursor-pointer select-none items-center gap-2">
              <input
                type="checkbox"
                checked={remember}
                onChange={(event) => setRemember(event.target.checked)}
                className="h-4 w-4 cursor-pointer rounded border-outline-variant accent-primary focus:ring-primary/30"
              />
              <span className="text-xs font-medium text-on-surface-variant">
                Ghi nhớ đăng nhập trên thiết bị này
              </span>
            </label>
          </div>

          <div className="pt-2">
            <button
              type="submit"
              disabled={submitting}
              className="shadow-btn flex h-11 w-full items-center justify-center gap-2 rounded-xl bg-primary font-display text-sm font-bold text-white transition-all hover:bg-[#0d645e] active:scale-[0.99] disabled:pointer-events-none disabled:opacity-80"
            >
              {submitting ? (
                <>
                  <span className="material-symbols-outlined animate-spin text-base">sync</span>
                  <span>Đang xác thực...</span>
                </>
              ) : (
                <>
                  <span>Đăng nhập vào hệ thống</span>
                  <span className="material-symbols-outlined text-base">arrow_forward</span>
                </>
              )}
            </button>
          </div>
        </form>

        <div className="relative my-4 flex items-center justify-center">
          <div className="w-full border-t border-outline-variant" />
          <span className="bg-surface-lowest px-3 text-xs font-medium tracking-wider text-outline uppercase">
            Hoặc tiếp tục với
          </span>
        </div>

        <div className="grid grid-cols-2 gap-3">
          <button
            type="button"
            disabled
            title="Đăng nhập Google chưa được cấu hình trên máy chủ"
            className="flex h-10 items-center justify-center gap-2 rounded-xl border border-outline-variant px-4 text-xs font-semibold text-on-surface transition-colors hover:bg-surface-low disabled:cursor-not-allowed disabled:opacity-50"
          >
            <svg className="h-4 w-4" viewBox="0 0 24 24">
              <path fill="#4285F4" d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92c-.26 1.37-1.04 2.53-2.21 3.31v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.09z" />
              <path fill="#34A853" d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z" />
              <path fill="#FBBC05" d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.06H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.94l2.85-2.22.81-.63z" />
              <path fill="#EA4335" d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.06l3.66 2.84c.87-2.6 3.3-4.52 6.16-4.52z" />
            </svg>
            <span>Google Tài khoản</span>
          </button>
          <button
            type="button"
            disabled
            title="Đăng nhập Zalo chưa được cấu hình trên máy chủ"
            className="flex h-10 items-center justify-center gap-2 rounded-xl border border-outline-variant px-4 text-xs font-semibold text-on-surface transition-colors hover:bg-surface-low disabled:cursor-not-allowed disabled:opacity-50"
          >
            <span className="flex h-4 w-4 items-center justify-center rounded-full bg-blue-600 text-[10px] font-bold text-white">
              Z
            </span>
            <span>Zalo OA ID</span>
          </button>
        </div>

        <div className="pt-2 text-center text-xs text-on-surface-variant">
          Bạn chưa có tài khoản?
          <Link
            to="/forgot-password"
            className="ml-1 font-bold text-primary hover:text-primary-dark hover:underline"
          >
            Liên hệ quản trị viên
          </Link>
        </div>
      </div>
    </AuthLayout>
  );
}
