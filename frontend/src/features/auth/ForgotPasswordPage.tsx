import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Alert } from '../../components/ui/Alert';
import { ApiError, request } from '../../lib/http';
import { AuthLayout } from './AuthLayout';
import { ResetStepper } from './ResetStepper';

interface SendCodeResult {
  /** ABP returns nothing useful here; the token arrives out-of-band. */
  [key: string]: unknown;
}

/**
 * Step 1 of the reset flow — Docs/front-end/authentication & authorization/
 * forgot_password_FE_1 (identifier form, connector stepper, security footer).
 *
 * Deviation kept from the mockup: the backend (ABP) sends a long
 * userId + resetToken reset link by email, not a 6-digit SMS OTP.
 */
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
      setFieldError('Vui lòng nhập email hoặc số điện thoại đã đăng ký.');
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
    <AuthLayout side={<SecurityArtwork />}>
      <div className="mx-auto w-full max-w-lg">
        <ResetStepper current={0} />

        <div className="space-y-6">
          <div className="space-y-2">
            <div className="inline-flex items-center gap-1.5 text-sm font-medium text-primary">
              <span className="material-symbols-outlined text-lg">lock_reset</span>
              <span>Khôi phục tài khoản quản lý &amp; cư dân</span>
            </div>
            <h1 className="font-display text-2xl font-bold tracking-tight text-on-surface">
              Quên mật khẩu tài khoản
            </h1>
            <p className="text-sm leading-relaxed text-on-surface-variant">
              Vui lòng nhập địa chỉ email đã đăng ký để nhận yêu cầu khôi phục mật khẩu
              truy cập hệ thống.
            </p>
          </div>

          {error && <Alert tone="error">{error}</Alert>}

          {sent ? (
            <div className="flex flex-col gap-4">
              <Alert tone="success">
                Yêu cầu đã được ghi nhận. Nếu email tồn tại, hệ thống sẽ gửi liên kết khôi
                phục tới hộp thư của bạn.
              </Alert>
              <Alert tone="warning">
                Môi trường phát triển đang tắt gửi email thật (NullEmailSender), nên hộp
                thư sẽ không nhận được liên kết. ABP gửi liên kết khôi phục dài
                (userId + resetToken) thay cho mã OTP 6 số.
              </Alert>
              <button
                type="button"
                onClick={() => navigate('/verify-otp', { state: { email: email.trim() } })}
                className="shadow-btn flex h-12 w-full items-center justify-center gap-2 rounded-lg bg-primary font-display text-sm font-bold text-white transition-all hover:bg-primary-dark active:scale-[0.99]"
              >
                <span>Tôi đã có mã khôi phục</span>
                <span className="material-symbols-outlined text-lg">arrow_forward</span>
              </button>
              <button
                type="button"
                onClick={() => setSent(false)}
                className="flex h-11 w-full items-center justify-center gap-1.5 rounded-lg bg-surface-low text-sm font-semibold text-on-surface transition-colors hover:bg-surface-container"
              >
                <span className="material-symbols-outlined text-lg">arrow_back</span>
                <span>Gửi lại cho địa chỉ khác</span>
              </button>
            </div>
          ) : (
            <form className="space-y-5" onSubmit={handleSubmit} noValidate>
              <div className="space-y-2">
                <label
                  htmlFor="identifier"
                  className="block text-xs font-semibold tracking-wider text-on-surface-variant uppercase"
                >
                  Email đã đăng ký <span className="font-bold text-error">*</span>
                </label>
                <div className="relative flex items-center rounded-lg bg-surface-low shadow-sm transition-colors focus-within:bg-surface-lowest">
                  <span className="material-symbols-outlined pointer-events-none absolute left-3.5 text-xl text-on-surface-variant">
                    contact_mail
                  </span>
                  <input
                    id="identifier"
                    type="text"
                    required
                    autoComplete="email"
                    placeholder="Ví dụ: dangthithuhang@gmail.com"
                    value={email}
                    onChange={(event) => setEmail(event.target.value)}
                    className="h-12 w-full rounded-lg bg-transparent pl-11 pr-12 text-sm text-on-surface transition-colors placeholder:text-outline focus:outline-none"
                  />
                  {email.length > 0 && (
                    <button
                      type="button"
                      onClick={() => setEmail('')}
                      title="Xóa nhanh"
                      aria-label="Xóa nội dung"
                      className="absolute right-2 p-2 text-outline transition-colors hover:text-on-surface"
                    >
                      <span className="material-symbols-outlined text-lg">cancel</span>
                    </button>
                  )}
                </div>
                {fieldError && <p className="text-xs text-error">{fieldError}</p>}
                <p className="rounded-lg bg-surface-low p-3 text-xs leading-relaxed text-on-surface-variant">
                  <strong className="font-semibold text-on-surface">Lưu ý:</strong> hệ
                  thống gửi <em>liên kết khôi phục</em> qua email (không phải SMS OTP 6
                  số). Hộp thư thật chỉ nhận được khi SMTP được cấu hình.
                </p>
              </div>

              <div className="space-y-3 pt-2">
                <button
                  type="submit"
                  disabled={submitting}
                  className="shadow-btn flex h-12 w-full items-center justify-center gap-2 rounded-lg bg-primary font-display text-sm font-bold text-white transition-all hover:bg-primary-dark active:scale-[0.99] disabled:pointer-events-none disabled:opacity-80"
                >
                  {submitting ? (
                    <>
                      <span className="material-symbols-outlined animate-spin text-lg">sync</span>
                      <span>Đang xử lý gửi mã...</span>
                    </>
                  ) : (
                    <>
                      <span>Gửi mã xác thực</span>
                      <span className="material-symbols-outlined text-lg">arrow_forward</span>
                    </>
                  )}
                </button>
                <button
                  type="button"
                  onClick={() => navigate('/login')}
                  className="flex h-11 w-full items-center justify-center gap-1.5 rounded-lg bg-surface-low text-sm font-semibold text-on-surface transition-colors hover:bg-surface-container"
                >
                  <span className="material-symbols-outlined text-lg">arrow_back</span>
                  <span>Quay lại Đăng nhập</span>
                </button>
              </div>
            </form>
          )}

          <div className="mt-6 flex flex-col items-center justify-between gap-3 border-t-0 pt-8 text-xs text-on-surface-variant sm:flex-row">
            <div className="flex items-center gap-1.5">
              <span
                className="material-symbols-outlined text-base text-primary"
                style={{ fontVariationSettings: '"FILL" 1' }}
              >
                lock
              </span>
              <span>Mã hóa bảo vệ SSL 256-bit</span>
            </div>
            <div className="flex items-center gap-2">
              <span>Hỗ trợ kỹ thuật:</span>
              <span className="font-bold text-primary">1900 8868</span>
            </div>
          </div>
        </div>
      </div>
    </AuthLayout>
  );
}

/** Solid teal security panel from the FE_1 mockup left pane. */
function SecurityArtwork() {
  return (
    <div className="relative flex h-full min-h-[580px] flex-col justify-between overflow-hidden bg-primary-dark p-8 text-white lg:p-10">
      <img
        src="/auth-artwork.jpg"
        alt=""
        aria-hidden
        className="pointer-events-none absolute inset-0 h-full w-full scale-105 object-cover opacity-30 mix-blend-overlay"
      />
      <div className="pointer-events-none absolute inset-0 bg-gradient-to-t from-primary-dark via-primary-dark/80 to-transparent" />

      <div className="relative z-10 space-y-4">
        <div className="inline-flex items-center gap-2 rounded-full border border-white/20 bg-white/10 px-3 py-1.5 text-xs font-semibold tracking-wider text-white shadow-sm backdrop-blur-md">
          <span className="material-symbols-outlined text-sm">shield_lock</span>
          BẢO MẬT ĐỊNH DANH 256-BIT
        </div>
      </div>

      <div className="relative z-10 my-auto py-6">
        <div className="space-y-3 rounded-xl bg-white/10 p-4 backdrop-blur-md">
          <div className="flex items-center gap-3">
            <div className="flex h-8 w-8 items-center justify-center rounded-lg bg-white/20 text-primary-fixed">
              <span className="material-symbols-outlined text-lg">support_agent</span>
            </div>
            <div className="space-y-0.5">
              <p className="text-[11px] font-bold tracking-widest text-white/90 uppercase">
                Hỗ trợ khôi phục tức thì
              </p>
              <p className="text-xs leading-relaxed text-white/80">
                Xác minh danh tính qua email đã đăng ký và liên kết khôi phục an toàn.
              </p>
            </div>
          </div>
        </div>
      </div>

      <div className="relative z-10 flex items-center justify-between pt-4 text-sm text-white/90">
        <div className="flex items-center gap-2">
          <span className="material-symbols-outlined text-lg">headset_mic</span>
          <span>
            Hotline: <strong className="font-bold">1900 8868</strong>
          </span>
        </div>
        <span className="text-xs text-white/70">24/7 Service</span>
      </div>
    </div>
  );
}
