import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Alert } from '../../components/ui/Alert';
import { Button } from '../../components/ui/Button';
import { Field } from '../../components/ui/Field';
import { Stepper } from '../../components/ui/Stepper';
import { ApiError, request } from '../../lib/http';
import { AuthLayout } from './AuthLayout';

export const resetSteps = ['Tài khoản', 'Mã xác thực', 'Mật khẩu'];

interface SendCodeResult {
  /** ABP returns nothing useful here; the token arrives out-of-band. */
  [key: string]: unknown;
}

export function ForgotPasswordPage() {
  const navigate = useNavigate();
  const [email, setEmail] = useState('');
  const [fieldError, setFieldError] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [sent, setSent] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    setError(null);
    setFieldError(null);

    if (!email.trim()) {
      setFieldError('Vui lòng nhập email đã đăng ký.');
      return;
    }

    setSubmitting(true);
    try {
      await request<SendCodeResult>('/api/account/send-password-reset-code', {
        method: 'POST',
        anonymous: true,
        body: { email: email.trim(), appName: 'NgocTrangHouseManagementSystem' },
      });
      setSent(true);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : 'Không gửi được mã xác thực.');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <AuthLayout>
      <div className="mx-auto flex max-w-md flex-col gap-6">
        <Stepper steps={resetSteps} current={0} />

        <div>
          <h1 className="font-display text-2xl font-bold tracking-tight text-on-surface">
            Quên mật khẩu
          </h1>
          <p className="mt-1.5 text-sm text-on-surface-variant">
            Vui lòng nhập địa chỉ email đã đăng ký để nhận mã xác thực khôi phục mật khẩu.
          </p>
        </div>

        {error && <Alert tone="error">{error}</Alert>}

        {sent ? (
          <div className="flex flex-col gap-4">
            <Alert tone="success">
              Yêu cầu đã được ghi nhận. Nếu email tồn tại, hệ thống sẽ gửi liên kết khôi phục tới hộp thư của bạn.
            </Alert>
            <Alert tone="warning">
              Môi trường phát triển đang tắt gửi email thật, nên hộp thư sẽ không nhận được thư. Mã khôi phục
              chỉ có khi cấu hình SMTP.
            </Alert>
            <Button fullWidth onClick={() => navigate('/verify-otp', { state: { email: email.trim() } })}>
              Tôi đã có mã xác thực
            </Button>
            <Button variant="secondary" fullWidth onClick={() => setSent(false)}>
              Gửi lại cho địa chỉ khác
            </Button>
          </div>
        ) : (
          <form className="flex flex-col gap-4" onSubmit={handleSubmit} noValidate>
            <Field
              label="Email đã đăng ký"
              required
              type="email"
              autoComplete="email"
              placeholder="dangthithuhang@gmail.com"
              value={email}
              error={fieldError ?? undefined}
              onChange={(event) => setEmail(event.target.value)}
              hint="Hệ thống dùng email để gửi liên kết khôi phục mật khẩu."
            />

            <Button type="submit" fullWidth loading={submitting}>
              Gửi mã xác thực
            </Button>
            <Link
              to="/login"
              className="flex items-center justify-center gap-1.5 rounded-[0.5rem] bg-surface-low px-4 py-2.5 text-sm font-semibold text-on-surface-variant transition hover:bg-surface-container"
            >
              Quay lại Đăng nhập
            </Link>
          </form>
        )}
      </div>
    </AuthLayout>
  );
}
