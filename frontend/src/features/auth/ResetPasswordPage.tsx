import { useMemo, useState } from 'react';
import { Navigate, useLocation, useNavigate } from 'react-router-dom';
import { Alert } from '../../components/ui/Alert';
import { Button } from '../../components/ui/Button';
import { Field } from '../../components/ui/Field';
import { Stepper } from '../../components/ui/Stepper';
import { ApiError, request } from '../../lib/http';
import { AuthLayout } from './AuthLayout';
import { resetSteps } from './ForgotPasswordPage';

interface ResetState {
  userId?: string;
  resetToken?: string;
  email?: string;
}

interface Rule {
  label: string;
  test: (value: string) => boolean;
}

const rules: Rule[] = [
  { label: 'Ít nhất 8 ký tự', test: (v) => v.length >= 8 },
  { label: 'Có chữ hoa và chữ thường', test: (v) => /[a-z]/.test(v) && /[A-Z]/.test(v) },
  { label: 'Ít nhất 1 chữ số', test: (v) => /\d/.test(v) },
  { label: 'Ký tự đặc biệt (@, #, $)', test: (v) => /[^A-Za-z0-9]/.test(v) },
];

export function ResetPasswordPage() {
  const location = useLocation();
  const navigate = useNavigate();
  const state = (location.state as ResetState | null) ?? {};

  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [show, setShow] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  const passed = useMemo(() => rules.map((rule) => rule.test(password)), [password]);
  const allPassed = passed.every(Boolean);
  const matches = confirm.length > 0 && password === confirm;

  if (!state.userId || !state.resetToken) {
    return <Navigate to="/forgot-password" replace />;
  }

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    setError(null);

    if (!allPassed) {
      setError('Mật khẩu chưa đáp ứng yêu cầu bảo mật.');
      return;
    }
    if (!matches) {
      setError('Xác nhận mật khẩu không khớp.');
      return;
    }

    setSubmitting(true);
    try {
      await request<void>('/api/account/reset-password', {
        method: 'POST',
        anonymous: true,
        body: { userId: state.userId, resetToken: state.resetToken, password },
      });
      setDone(true);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : 'Đặt lại mật khẩu thất bại.');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <AuthLayout>
      <div className="mx-auto flex max-w-md flex-col gap-6">
        <Stepper steps={resetSteps} current={done ? 3 : 2} />

        {done ? (
          <div className="flex flex-col gap-4">
            <Alert tone="success">
              Đặt lại mật khẩu thành công. Bạn có thể đăng nhập bằng mật khẩu mới.
            </Alert>
            <Button fullWidth onClick={() => navigate('/login', { replace: true })}>
              Đăng nhập ngay
            </Button>
          </div>
        ) : (
          <>
            <div>
              <h1 className="font-display text-2xl font-bold tracking-tight text-on-surface">
                Đặt mật khẩu mới
              </h1>
              <p className="mt-1.5 text-sm text-on-surface-variant">
                {state.email
                  ? `Tạo mật khẩu mới cho ${state.email}.`
                  : 'Tạo mật khẩu mới cho tài khoản của bạn.'}
              </p>
            </div>

            {error && <Alert tone="error">{error}</Alert>}

            <form className="flex flex-col gap-4" onSubmit={handleSubmit} noValidate>
              <Field
                label="Mật khẩu mới"
                required
                type={show ? 'text' : 'password'}
                autoComplete="new-password"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
                trailing={
                  <button
                    type="button"
                    onClick={() => setShow((value) => !value)}
                    className="text-xs font-semibold text-primary"
                  >
                    {show ? 'Ẩn' : 'Hiện'}
                  </button>
                }
              />

              <div className="h-1.5 w-full overflow-hidden rounded-full bg-surface-container">
                <div
                  className={
                    'h-full transition-all ' +
                    (allPassed ? 'bg-primary' : passed.filter(Boolean).length >= 2 ? 'bg-secondary' : 'bg-error')
                  }
                  style={{ width: `${(passed.filter(Boolean).length / rules.length) * 100}%` }}
                />
              </div>

              <Field
                label="Xác nhận mật khẩu mới"
                required
                type={show ? 'text' : 'password'}
                autoComplete="new-password"
                value={confirm}
                onChange={(event) => setConfirm(event.target.value)}
                error={confirm.length > 0 && !matches ? 'Mật khẩu chưa khớp.' : undefined}
              />

              <div className="rounded-[0.5rem] bg-surface-low p-3.5">
                <p className="mb-2 text-[11px] font-semibold tracking-wide text-on-surface-variant uppercase">
                  Yêu cầu bảo mật
                </p>
                <ul className="grid grid-cols-1 gap-1.5 sm:grid-cols-2">
                  {rules.map((rule, index) => (
                    <li
                      key={rule.label}
                      className={
                        'flex items-center gap-1.5 text-xs ' +
                        (passed[index] ? 'text-primary' : 'text-on-surface-variant')
                      }
                    >
                      <span aria-hidden>{passed[index] ? '✓' : '○'}</span>
                      {rule.label}
                    </li>
                  ))}
                </ul>
              </div>

              <div className="flex gap-3">
                <Button type="submit" loading={submitting} disabled={!allPassed || !matches}>
                  Lưu mật khẩu mới
                </Button>
                <Button type="button" variant="secondary" onClick={() => navigate('/login')}>
                  Hủy bỏ
                </Button>
              </div>
            </form>
          </>
        )}
      </div>
    </AuthLayout>
  );
}
