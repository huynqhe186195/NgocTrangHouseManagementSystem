import type { ReactNode } from 'react';
import { Brand } from '../../components/ui/Brand';

interface AuthLayoutProps {
  children: ReactNode;
  /** Optional artwork panel; the forgot-password screens use a tinted card. */
  side?: ReactNode;
}

export function AuthLayout({ children, side }: AuthLayoutProps) {
  return (
    <div className="flex min-h-screen flex-col bg-surface">
      <header className="flex items-center justify-between border-b border-outline-variant/60 bg-surface-lowest/80 px-6 py-4 backdrop-blur-md">
        <Brand />
      </header>

      <main className="flex flex-1 items-start justify-center px-4 py-10 sm:py-14">
        <div className="grid w-full max-w-5xl overflow-hidden rounded-[1rem] border border-outline-variant bg-surface-lowest shadow-card md:grid-cols-[minmax(0,0.95fr)_minmax(0,1.05fr)]">
          <aside className="relative hidden md:block">
            {side ?? <DefaultArtwork />}
          </aside>
          <section className="px-6 py-8 sm:px-10 sm:py-10">{children}</section>
        </div>
      </main>

      <footer className="flex flex-wrap items-center justify-center gap-x-6 gap-y-1 border-t border-outline-variant/60 px-6 py-4 text-xs text-on-surface-variant">
        <span>Điều khoản sử dụng</span>
        <span aria-hidden>·</span>
        <span>Chính sách bảo mật</span>
        <span aria-hidden>·</span>
        <span>Hỗ trợ 24/7</span>
      </footer>
    </div>
  );
}

function DefaultArtwork() {
  return (
    <div className="flex h-full min-h-[520px] flex-col justify-between bg-gradient-to-br from-primary-dark via-primary to-primary-active p-8 text-on-primary">
      <div>
        <div className="inline-flex items-center gap-2 rounded-full bg-white/12 px-3 py-1.5 text-[11px] font-semibold tracking-wide uppercase">
          <ShieldIcon />
          Bảo mật định danh
        </div>
        <h2 className="mt-6 font-display text-2xl leading-snug font-bold">
          Nhà Trọ Cao Cấp
          <br />
          Ngọc Trang
        </h2>
        <p className="mt-3 max-w-xs text-sm leading-relaxed text-white/80">
          Hệ thống xác thực định danh tính cư dân và phân quyền quản lý chuyên nghiệp, minh bạch và an toàn tuyệt đối.
        </p>
      </div>

      <dl className="grid grid-cols-2 gap-4 text-sm">
        <div>
          <dt className="text-white/65">Hotline</dt>
          <dd className="font-display text-lg font-bold">1900 8868</dd>
        </div>
        <div>
          <dt className="text-white/65">Hỗ trợ</dt>
          <dd className="font-display text-lg font-bold">24/7</dd>
        </div>
      </dl>
    </div>
  );
}

function ShieldIcon() {
  return (
    <svg viewBox="0 0 20 20" className="h-3.5 w-3.5" fill="none" stroke="currentColor" strokeWidth="1.8">
      <path d="M10 2l6 2.5v5c0 3.5-2.5 6.7-6 8.5-3.5-1.8-6-5-6-8.5v-5L10 2z" strokeLinejoin="round" />
      <path d="M7.5 10l1.8 1.8 3.4-3.6" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}
