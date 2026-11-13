import { useEffect, useState } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { Alert } from '../../components/ui/Alert';
import { Button } from '../../components/ui/Button';
import { Field } from '../../components/ui/Field';
import { Stepper } from '../../components/ui/Stepper';
import { ApiError, request } from '../../lib/http';
import { AuthLayout } from './AuthLayout';
import { resetSteps } from './ForgotPasswordPage';

interface VerifyState {
  email?: string;
}

/**
 * ABP issues a long reset token delivered by email, not a six-digit SMS code.
 * Development has no SMTP configured (NullEmailSender), so this screen also
 * accepts the userId/resetToken pair pasted from a reset link.
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

  return (
    <AuthLayout>
      <div className="mx-auto flex max-w-md flex-col gap-6">
        <Stepper steps={resetSteps} current={1} />

        <div>
          <h1 className="font-display text-2xl font-bold tracking-tight text-on-surface">
            Xác thực mã khôi phục
          </h1>
          <p className="mt-1.5 text-sm text-on-surface-variant">
            Dán liên kết khôi phục hoặc hai giá trị đi kèm trong email để tiếp tục.
          </p>
        </div>

        <Alert tone="info">
          Hệ thống dùng liên kết khôi phục dài (userId + resetToken) thay cho mã OTP 6 số. Khi bật SMTP
          thật, người dùng sẽ nhận liên kết này qua email.
        </Alert>

        {error && <Alert tone="error">{error}</Alert>}

        <form className="flex flex-col gap-4" onSubmit={handleSubmit} noValidate>
          <Field
            label="Mã người dùng"
            required
            value={userId}
            autoComplete="off"
            placeholder="3fa85f64-5717-4562-b3fc-2c963f66afa6"
            onChange={(event) => setUserId(event.target.value)}
          />
          <Field
            label="Mã khôi phục"
            required
            value={resetToken}
            autoComplete="off"
            placeholder="Dán mã khôi phục từ email"
            onChange={(event) => setResetToken(event.target.value)}
          />

          <Button type="submit" fullWidth loading={submitting}>
            Xác thực và tiếp tục
          </Button>
          <Link
            to="/forgot-password"
            className="flex items-center justify-center rounded-[0.5rem] bg-surface-low px-4 py-2.5 text-sm font-semibold text-on-surface-variant transition hover:bg-surface-container"
          >
            Gửi lại yêu cầu
          </Link>
        </form>
      </div>
    </AuthLayout>
  );
}
