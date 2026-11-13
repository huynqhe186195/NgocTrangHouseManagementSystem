import { useState } from 'react';
import { Link, Navigate, useLocation, useNavigate } from 'react-router-dom';
import { Alert } from '../../components/ui/Alert';
import { Button } from '../../components/ui/Button';
import { Field } from '../../components/ui/Field';
import { ApiError } from '../../lib/http';
import { useAuth } from '../../lib/auth/AuthProvider';
import { AuthLayout } from './AuthLayout';

type RoleTab = 'owner' | 'staff' | 'tenant';

const roles: { id: RoleTab; label: string; available: boolean }[] = [
  { id: 'owner', label: 'Chủ cơ sở', available: true },
  { id: 'staff', label: 'Nhân viên', available: true },
  { id: 'tenant', label: 'Khách thuê', available: false },
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
      <div className="mx-auto flex max-w-md flex-col gap-6">
        <div>
          <h1 className="font-display text-2xl font-bold tracking-tight text-on-surface">
            Đăng nhập hệ thống
          </h1>
          <p className="mt-1.5 text-sm text-on-surface-variant">
            Nhập email hoặc số điện thoại đã đăng ký để tiếp tục
          </p>
        </div>

        <div role="tablist" aria-label="Vai trò" className="flex rounded-[0.5rem] bg-surface-low p-1">
          {roles.map((item) => (
            <button
              key={item.id}
              role="tab"
              type="button"
              aria-selected={role === item.id}
              disabled={!item.available}
              title={item.available ? undefined : 'Cổng khách thuê chưa được mở'}
              onClick={() => setRole(item.id)}
              className={
                'flex-1 rounded-[0.4rem] px-3 py-2 text-sm transition ' +
                (role === item.id
                  ? 'bg-surface-lowest font-semibold text-primary shadow-sm'
                  : 'text-on-surface-variant hover:text-on-surface ') +
                (item.available ? '' : 'cursor-not-allowed opacity-45')
              }
            >
              {item.label}
            </button>
          ))}
        </div>

        {error && <Alert tone="error">{error}</Alert>}

        <form className="flex flex-col gap-4" onSubmit={handleSubmit} noValidate>
          <Field
            label="Email hoặc số điện thoại"
            required
            type="text"
            autoComplete="username"
            placeholder="chuna.ngoctrang@gmail.com"
            value={username}
            error={fieldErrors.username}
            onChange={(event) => setUsername(event.target.value)}
          />

          <Field
            label="Mật khẩu"
            required
            type={showPassword ? 'text' : 'password'}
            autoComplete="current-password"
            placeholder="••••••••••"
            value={password}
            error={fieldErrors.password}
            onChange={(event) => setPassword(event.target.value)}
            labelAction={
              <Link to="/forgot-password" className="text-xs font-semibold text-primary hover:underline">
                Quên mật khẩu?
              </Link>
            }
            trailing={
              <button
                type="button"
                onClick={() => setShowPassword((value) => !value)}
                aria-label={showPassword ? 'Ẩn mật khẩu' : 'Hiện mật khẩu'}
                className="text-outline hover:text-on-surface-variant"
              >
                <EyeIcon open={showPassword} />
              </button>
            }
          />

          <label className="flex cursor-pointer items-center gap-2.5 text-sm text-on-surface-variant">
            <input
              type="checkbox"
              checked={remember}
              onChange={(event) => setRemember(event.target.checked)}
              className="h-4 w-4 rounded border-outline-variant accent-[var(--color-primary)]"
            />
            Ghi nhớ đăng nhập trên thiết bị này
          </label>

          <Button type="submit" fullWidth loading={submitting} icon={<ArrowRightIcon />}>
            Đăng nhập vào hệ thống
          </Button>
        </form>

        <p className="text-center text-xs text-on-surface-variant">
          Chưa có tài khoản? Liên hệ quản trị viên để được cấp quyền truy cập.
        </p>
      </div>
    </AuthLayout>
  );
}

function EyeIcon({ open }: { open: boolean }) {
  return (
    <svg viewBox="0 0 20 20" className="h-5 w-5" fill="none" stroke="currentColor" strokeWidth="1.6">
      <path d="M1.8 10S4.9 4.8 10 4.8 18.2 10 18.2 10 15.1 15.2 10 15.2 1.8 10 1.8 10z" strokeLinejoin="round" />
      <circle cx="10" cy="10" r="2.4" />
      {!open && <path d="M3 3l14 14" strokeLinecap="round" />}
    </svg>
  );
}

function ArrowRightIcon() {
  return (
    <svg viewBox="0 0 20 20" className="h-4 w-4" fill="none" stroke="currentColor" strokeWidth="1.9">
      <path d="M4 10h11M10.5 5.5L15 10l-4.5 4.5" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}
