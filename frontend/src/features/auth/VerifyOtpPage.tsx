import { useEffect, useState } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { Alert } from '../../components/ui/Alert';
import { ApiError, request } from '../../lib/http';
import { AuthLayout } from './AuthLayout';
import { ResetStepper } from './ResetStepper';

interface VerifyState {
  email?: string;
}

/**
 * Step 2 of the reset flow — Docs/front-end/authentication & authorization/
 * forgot_password_FE_2 (OTP verify layout). Honest deviation kept visible:
 * ABP issues a long userId + resetToken pair by email, not a 6-digit SMS
 * OTP; dev has no SMTP, so the pair is pasted here or taken from a link.
 */
export function VerifyOtpPage() {
  const navigate = useNavigate();
  const location = useLocation();
  const [params] = useSearchParams();
  const email = (location.state as VerifyState | null)?.email ?? '';

  const [userId, setUserId] = useState(params.get('userId') ?? '');
  const [resetToken, setResetToken] = useState(params.get('resetToken') ?? '');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    const link = params.get('link');
    if (!link) {
      return;
    }
    try {
      const parsed = new URL(link);
      setUserId(parsed.searchParams.get('userId') ?? '');
      setResetToken(parsed.searchParams.get('resetToken') ?? '');
    } catch {
      // Not a URL; the user can paste the two values by hand.
    }
  }, [params]);

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    setError(null);

    if (!userId.trim() || !resetToken.trim()) {
      setError('Cần cả mã người dùng và mã khôi phục từ liên kết trong email.');
      return;
    }

    setSubmitting(true);
    try {
      const valid = await request<boolean>('/api/account/verify-password-reset-token', {
        method: 'POST',
        anonymous: true,
        body: { userId: userId.trim(), resetToken: resetToken.trim() },
      });
      if (valid !== true) {
        setError('Mã khôi phục không hợp lệ hoặc đã hết hạn.');
        return;
      }
      navigate('/reset-password', {
        state: { userId: userId.trim(), resetToken: resetToken.trim(), email },
      });
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : 'Không xác thực được mã khôi phục.');
    } finally {
      setSubmitting(false);
    }
  }

  const maskedEmail = maskEmail(email);

  return (
    <AuthLayout side={<VerifyArtwork />}>
      <div className="mx-auto w-full max-w-lg">
        <ResetStepper current={1} />

        <div className="space-y-6">
          <div className="mb-6 space-y-2">
            <h1 className="font-display text-2xl font-bold tracking-tight text-on-surface">
              Xác thực mã khôi phục
            </h1>
            <p className="text-sm leading-relaxed text-on-surface-variant">
              Yêu cầu khôi phục đã được ghi nhận cho tài khoản:{' '}
              {maskedEmail ? (
                <span className="rounded bg-surface-low px-2 py-0.5 text-sm font-semibold text-on-surface">
                  {maskedEmail}
                </span>
              ) : (
                <span className="text-on-surface">tài khoản của bạn</span>
              )}
              <span className="mt-0.5 block text-xs text-outline">
                (Liên kết khôi phục được đồng bộ tới hòm thư đã đăng ký)
              </span>
            </p>
          </div>

          <Alert tone="info">
            Hệ thống dùng <strong>liên kết khôi phục dài (userId + resetToken)</strong> do
            ABP cấp qua email, thay cho mã OTP 6 số như thiết kế gốc. Dán hai giá trị từ
            liên kết hoặc email vào bên dưới.
          </Alert>

          {error && <Alert tone="error">{error}</Alert>}

          <form className="space-y-6" onSubmit={handleSubmit} noValidate>
            <div className="space-y-4">
              <div className="flex items-center justify-between">
                <span className="flex items-center gap-1.5 text-sm font-semibold text-on-surface">
                  <span className="material-symbols-outlined text-lg text-primary">sms</span>
                  Nhập mã khôi phục
                </span>
              </div>

              <div className="space-y-2">
                <label
                  htmlFor="userId"
                  className="block text-xs font-semibold tracking-wider text-on-surface-variant uppercase"
                >
                  Mã người dùng (userId) <span className="font-bold text-error">*</span>
                </label>
                <div className="relative flex items-center rounded-lg bg-surface-low shadow-sm transition-colors focus-within:bg-surface-lowest">
                  <span className="material-symbols-outlined pointer-events-none absolute left-3.5 text-xl text-on-surface-variant">
                    badge
                  </span>
                  <input
                    id="userId"
                    type="text"
                    required
                    autoComplete="off"
                    spellCheck={false}
                    placeholder="3fa85f64-5717-4562-b3fc-2c963f66afa6"
                    value={userId}
                    onChange={(event) => setUserId(event.target.value)}
                    className="h-12 w-full rounded-lg bg-transparent pl-11 pr-4 text-sm text-on-surface transition-colors placeholder:text-outline focus:outline-none"
                  />
                </div>
              </div>

              <div className="space-y-2">
                <label
                  htmlFor="resetToken"
                  className="block text-xs font-semibold tracking-wider text-on-surface-variant uppercase"
                >
                  Mã khôi phục (resetToken) <span className="font-bold text-error">*</span>
                </label>
                <div className="relative flex items-center rounded-lg bg-surface-low shadow-sm transition-colors focus-within:bg-surface-lowest">
                  <span className="material-symbols-outlined pointer-events-none absolute left-3.5 text-xl text-on-surface-variant">
                    key
                  </span>
                  <input
                    id="resetToken"
                    type="text"
                    required
                    autoComplete="off"
                    spellCheck={false}
                    placeholder="Dán mã khôi phục từ email / liên kết"
                    value={resetToken}
                    onChange={(event) => setResetToken(event.target.value)}
                    className="h-12 w-full rounded-lg bg-transparent pl-11 pr-4 text-sm text-on-surface transition-colors placeholder:text-outline focus:outline-none"
                  />
                </div>
              </div>

              <div className="flex items-center justify-center gap-2 text-center text-sm text-on-surface-variant">
                <span>Chưa nhận được email?</span>
                <Link
                  to="/forgot-password"
                  className="inline-flex items-center gap-1 text-sm font-semibold text-primary transition-colors hover:text-primary-dark"
                >
                  <span className="material-symbols-outlined text-base">replay</span>
                  Gửi lại yêu cầu
                </Link>
              </div>
            </div>

            <div className="flex items-start gap-3 rounded-xl bg-surface-low p-3.5 text-on-surface-variant">
              <span className="material-symbols-outlined mt-0.5 text-lg text-secondary">
                shield_lock
              </span>
              <p className="text-sm leading-snug">
                <strong className="font-semibold text-on-surface">Lưu ý quan trọng:</strong>{' '}
                Tuyệt đối không chia sẻ mã khôi phục cho bất kỳ ai, kể cả nhân viên quản
                lý nhà trọ hay quản trị viên hệ thống.
              </p>
            </div>

            <div className="flex flex-col-reverse items-center gap-3 pt-2 sm:flex-row">
              <Link
                to="/forgot-password"
                className="flex h-12 w-full items-center justify-center gap-2 rounded-lg bg-surface-container px-5 text-sm font-semibold text-on-surface transition-colors hover:bg-surface-high sm:w-auto"
              >
                <span className="material-symbols-outlined text-base">arrow_back</span>
                <span>Quay lại Bước 1</span>
              </Link>
              <button
                type="submit"
                disabled={submitting}
                className="shadow-btn flex h-12 w-full flex-1 items-center justify-center gap-2 rounded-lg bg-primary px-6 text-sm font-bold text-white transition-all hover:bg-primary-dark active:scale-[0.99] disabled:pointer-events-none disabled:opacity-90"
              >
                {submitting ? (
                  <>
                    <span className="material-symbols-outlined animate-spin text-base">sync</span>
                    <span>Đang kiểm tra mã...</span>
                  </>
                ) : (
                  <>
                    <span>Xác nhận mã khôi phục</span>
                    <span className="material-symbols-outlined text-base">arrow_forward</span>
                  </>
                )}
              </button>
            </div>

            <div className="flex flex-wrap items-center justify-center gap-2 pt-2 text-xs text-outline">
              <span aria-hidden>•</span>
              <Link
                to="/forgot-password"
                className="transition-colors hover:text-primary"
              >
                Đổi địa chỉ email khác
              </Link>
            </div>
          </form>
        </div>
      </div>
    </AuthLayout>
  );
}

function maskEmail(email: string): string {
  if (!email) {
    return '';
  }
  const at = email.indexOf('@');
  if (at <= 0) {
    return email.length > 2 ? `${'•'.repeat(email.length)}` : email;
  }
  const local = email.slice(0, at);
  const domain = email.slice(at + 1);
  const head = local.slice(0, Math.min(4, local.length));
  return `${head}${'•••'}@${domain}`;
}

/** Blurred teal-tinted building photo, no text (FE_2 left pane). */
function VerifyArtwork() {
  return (
    <div className="relative h-full min-h-[580px] overflow-hidden bg-primary">
      <img
        src="/auth-artwork.jpg"
        alt=""
        aria-hidden
        className="absolute inset-0 h-full w-full object-cover opacity-35"
      />
      <div className="pointer-events-none absolute inset-0 bg-primary/80" />
      <div className="pointer-events-none absolute inset-0 backdrop-blur-[2px]" />
    </div>
  );
}
