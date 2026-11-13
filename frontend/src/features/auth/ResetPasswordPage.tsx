import { useMemo, useState } from 'react';
import { Navigate, useLocation, useNavigate } from 'react-router-dom';
import { Alert } from '../../components/ui/Alert';
import { ApiError, request } from '../../lib/http';
import { AuthLayout } from './AuthLayout';
import { ResetStepper } from './ResetStepper';

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
  { label: 'Chữ hoa & chữ thường', test: (v) => /[a-z]/.test(v) && /[A-Z]/.test(v) },
  { label: 'Ít nhất 1 chữ số', test: (v) => /\d/.test(v) },
  { label: 'Ký tự đặc biệt (@, #, $)', test: (v) => /[^A-Za-z0-9]/.test(v) },
];

/**
 * Step 3 of the reset flow — Docs/front-end/authentication & authorization/
 * forgot_password_FE_3 (verified banner, strength bar, rules checklist).
 */
export function ResetPasswordPage() {
  const location = useLocation();
  const navigate = useNavigate();
  const state = (location.state as ResetState | null) ?? {};

  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [showNew, setShowNew] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  const passed = useMemo(() => rules.map((rule) => rule.test(password)), [password]);
  const passedCount = passed.filter(Boolean).length;
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

  const strength = strengthLabel(passedCount);

  return (
    <AuthLayout side={<ResetArtwork />}>
      <div className="mx-auto w-full max-w-lg">
        <ResetStepper current={2} />

        {done ? (
          <div className="flex flex-col gap-4">
            <div className="flex items-start gap-3 rounded-xl bg-surface-low p-3.5">
              <span
                className="material-symbols-outlined mt-0.5 shrink-0 text-xl text-primary"
                style={{ fontVariationSettings: '"FILL" 1' }}
              >
                check_circle
              </span>
              <div className="flex-1">
                <p className="text-sm font-semibold text-primary">Đặt lại thành công!</p>
                <p className="mt-0.5 text-sm text-on-surface-variant">
                  Mật khẩu đã được cập nhật. Bạn có thể đăng nhập bằng mật khẩu mới.
                </p>
              </div>
            </div>
            <button
              type="button"
              onClick={() => navigate('/login', { replace: true })}
              className="shadow-btn flex h-11 w-full items-center justify-center gap-2 rounded-lg bg-primary font-display text-sm font-bold text-white transition-all hover:bg-primary-dark active:scale-[0.99]"
            >
              <span>Đăng nhập ngay</span>
              <span className="material-symbols-outlined text-lg">arrow_forward</span>
            </button>
          </div>
        ) : (
          <form className="space-y-5" onSubmit={handleSubmit} noValidate>
            <div className="mb-6 flex items-start gap-3 rounded-xl bg-surface-low p-3.5">
              <span
                className="material-symbols-outlined mt-0.5 shrink-0 text-xl text-primary"
                style={{ fontVariationSettings: '"FILL" 1' }}
              >
                check_circle
              </span>
              <div className="flex-1">
                <p className="text-sm font-semibold text-primary">Xác thực thành công!</p>
                <p className="mt-0.5 text-sm text-on-surface-variant">
                  Đã xác thực mã khôi phục cho tài khoản{' '}
                  <strong className="font-semibold text-on-surface">
                    {state.email ?? 'của bạn'}
                  </strong>
                  . Vui lòng thiết lập mật khẩu mới bên dưới.
                </p>
              </div>
            </div>

            {error && <Alert tone="error">{error}</Alert>}

            <div className="space-y-1.5">
              <div className="flex items-center justify-between">
                <label
                  htmlFor="new-password"
                  className="block text-xs font-semibold tracking-wider text-on-surface-variant uppercase"
                >
                  Mật khẩu mới <span className="text-error">*</span>
                </label>
                {password.length > 0 && (
                  <span
                    className={
                      'flex items-center gap-1 text-xs font-semibold ' +
                      (strength.tone === 'error'
                        ? 'text-error'
                        : strength.tone === 'warning'
                          ? 'text-secondary'
                          : 'text-primary')
                    }
                  >
                    <span className="material-symbols-outlined text-sm">
                      {strength.icon}
                    </span>
                    {strength.label}
                  </span>
                )}
              </div>
              <div className="relative flex items-center">
                <input
                  id="new-password"
                  type={showNew ? 'text' : 'password'}
                  required
                  autoComplete="new-password"
                  placeholder="Nhập mật khẩu mới an toàn"
                  value={password}
                  onChange={(event) => setPassword(event.target.value)}
                  className="h-11 w-full rounded-lg border border-outline-variant bg-surface-lowest px-3.5 pr-10 text-sm text-on-surface shadow-sm outline-none transition-colors focus:ring-2 focus:ring-primary focus:outline-none"
                />
                <button
                  type="button"
                  aria-label={showNew ? 'Ẩn mật khẩu' : 'Hiện mật khẩu'}
                  onClick={() => setShowNew((value) => !value)}
                  className="absolute right-3 p-1 text-outline transition-colors hover:text-on-surface"
                >
                  <span className="material-symbols-outlined text-lg">
                    {showNew ? 'visibility_off' : 'visibility'}
                  </span>
                </button>
              </div>
              <div className="space-y-1 pt-1">
                <div className="grid h-1.5 w-full grid-cols-3 gap-1.5">
                  {[0, 1, 2].map((index) => (
                    <div
                      key={index}
                      className={
                        'h-full rounded-full transition-all duration-300 ' +
                        (index < strength.bars
                          ? strength.tone === 'error'
                            ? 'bg-error'
                            : strength.tone === 'warning'
                              ? 'bg-secondary'
                              : 'bg-primary'
                          : 'bg-surface-container')
                      }
                    />
                  ))}
                </div>
                <p className="flex items-center gap-1 text-xs text-on-surface-variant">
                  Độ bảo mật mật khẩu:{' '}
                  <span
                    className={
                      'font-semibold ' +
                      (strength.tone === 'error'
                        ? 'text-error'
                        : strength.tone === 'warning'
                          ? 'text-secondary'
                          : 'text-primary')
                    }
                  >
                    {strength.detail}
                  </span>
                </p>
              </div>
            </div>

            <div className="space-y-1.5">
              <label
                htmlFor="confirm-password"
                className="block text-xs font-semibold tracking-wider text-on-surface-variant uppercase"
              >
                Xác nhận mật khẩu mới <span className="text-error">*</span>
              </label>
              <div className="relative flex items-center">
                <input
                  id="confirm-password"
                  type={showConfirm ? 'text' : 'password'}
                  required
                  autoComplete="new-password"
                  placeholder="Nhập lại mật khẩu mới"
                  value={confirm}
                  onChange={(event) => setConfirm(event.target.value)}
                  className="h-11 w-full rounded-lg border border-outline-variant bg-surface-lowest px-3.5 pr-16 text-sm text-on-surface shadow-sm outline-none transition-colors focus:ring-2 focus:ring-primary focus:outline-none"
                />
                <div className="absolute right-3 flex items-center gap-1.5">
                  {matches && (
                    <span
                      className="material-symbols-outlined text-lg text-primary"
                      style={{ fontVariationSettings: '"FILL" 1' }}
                    >
                      check_circle
                    </span>
                  )}
                  <button
                    type="button"
                    aria-label={showConfirm ? 'Ẩn mật khẩu xác nhận' : 'Hiện mật khẩu xác nhận'}
                    onClick={() => setShowConfirm((value) => !value)}
                    className="p-1 text-outline transition-colors hover:text-on-surface"
                  >
                    <span className="material-symbols-outlined text-lg">
                      {showConfirm ? 'visibility_off' : 'visibility'}
                    </span>
                  </button>
                </div>
              </div>
              {confirm.length > 0 && (
                <p
                  className={
                    'flex items-center gap-1 text-xs font-medium ' +
                    (matches ? 'text-primary' : 'text-error')
                  }
                >
                  {matches ? '✓ Khớp hoàn toàn với mật khẩu vừa nhập' : 'Mật khẩu chưa khớp.'}
                </p>
              )}
            </div>

            <div className="space-y-2 rounded-xl bg-surface-low p-3.5">
              <span className="block text-xs font-bold tracking-wider text-on-surface uppercase">
                Yêu cầu bảo mật:
              </span>
              <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
                {rules.map((rule, index) => (
                  <div
                    key={rule.label}
                    className={
                      'flex items-center gap-1.5 text-xs font-medium ' +
                      (passed[index] ? 'text-primary' : 'text-on-surface-variant')
                    }
                  >
                    <span className="material-symbols-outlined text-base">
                      {passed[index] ? 'check_circle' : 'radio_button_unchecked'}
                    </span>
                    <span>{rule.label}</span>
                  </div>
                ))}
              </div>
            </div>

            <div className="flex flex-col items-center gap-3 pt-2 sm:flex-row">
              <button
                type="submit"
                disabled={submitting}
                className="shadow-btn flex h-11 w-full flex-1 items-center justify-center gap-2 rounded-lg bg-primary px-5 font-display text-sm font-bold text-white transition-all hover:bg-primary-dark active:scale-[0.99] disabled:pointer-events-none disabled:opacity-80"
              >
                {submitting ? (
                  <>
                    <span className="material-symbols-outlined animate-spin text-base">sync</span>
                    <span>Đang lưu...</span>
                  </>
                ) : (
                  <>
                    <span>Lưu mật khẩu mới &amp; Đăng nhập ngay</span>
                    <span className="material-symbols-outlined text-lg">arrow_forward</span>
                  </>
                )}
              </button>
              <button
                type="button"
                onClick={() => navigate('/login')}
                className="h-11 w-full rounded-lg bg-surface-container px-5 text-sm font-semibold text-on-surface transition-colors hover:bg-surface-high sm:w-auto"
              >
                Hủy bỏ
              </button>
            </div>
          </form>
        )}
      </div>
    </AuthLayout>
  );
}

function strengthLabel(passedCount: number): {
  bars: number;
  label: string;
  detail: string;
  icon: string;
  tone: 'error' | 'warning' | 'ok';
} {
  if (passedCount <= 1) {
    return { bars: 1, label: 'Yếu', detail: 'Yếu', icon: 'warning', tone: 'error' };
  }
  if (passedCount <= 3) {
    return {
      bars: 2,
      label: 'Khá',
      detail: 'Khá — nên thêm ký tự đặc biệt',
      icon: 'shield',
      tone: 'warning',
    };
  }
  return { bars: 3, label: 'Rất mạnh', detail: 'Tối ưu', icon: 'verified_user', tone: 'ok' };
}

/** Blurred building photo, lighter teal scrim (FE_3 left pane). */
function ResetArtwork() {
  return (
    <div className="relative h-full min-h-[580px] overflow-hidden bg-primary">
      <img
        src="/auth-artwork.jpg"
        alt=""
        aria-hidden
        className="absolute inset-0 h-full w-full object-cover"
      />
      <div className="pointer-events-none absolute inset-0 bg-gradient-to-t from-primary/95 via-primary/75 to-primary-dark/40 backdrop-blur-[1px]" />
    </div>
  );
}
