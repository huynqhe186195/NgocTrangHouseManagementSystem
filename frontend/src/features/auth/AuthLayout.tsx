import type { ReactNode } from 'react';
import { Brand } from '../../components/ui/Brand';

interface AuthLayoutProps {
  children: ReactNode;
  /** Optional artwork panel content; defaults to the photo artwork. */
  side?: ReactNode;
}

export function AuthLayout({ children, side }: AuthLayoutProps) {
  return (
    <div className="flex min-h-screen flex-col justify-between bg-surface text-on-surface selection:bg-primary/20 selection:text-primary">
      <header className="flex w-full items-center justify-between border-b border-outline-variant/60 bg-surface-lowest/80 px-6 py-4 backdrop-blur-md">
        <Brand />
      </header>

      <main className="flex flex-1 items-center justify-center p-4 sm:p-6 lg:p-8">
        <div className="grid min-h-[580px] w-full max-w-5xl grid-cols-1 overflow-hidden rounded-2xl border border-outline-variant/80 bg-surface-lowest shadow-card lg:grid-cols-12">
          <aside className="relative hidden min-h-[580px] overflow-hidden lg:col-span-5 lg:block">
            {side ?? <DefaultArtwork />}
          </aside>
          <section className="flex flex-col justify-center bg-surface-lowest p-8 sm:p-10 lg:col-span-7 lg:p-12">
            {children}
          </section>
        </div>
      </main>

      <footer className="flex w-full flex-col items-center justify-between gap-2 border-t border-outline-variant/50 bg-surface-lowest/40 px-6 py-4 text-xs text-on-surface-variant sm:flex-row">
        <div className="flex items-center gap-4">
          <a href="#" className="transition-colors hover:text-primary">Điều khoản sử dụng</a>
          <span aria-hidden>•</span>
          <a href="#" className="transition-colors hover:text-primary">Chính sách bảo mật</a>
          <span aria-hidden>•</span>
          <a href="#" className="transition-colors hover:text-primary">Hỗ trợ 24/7</a>
        </div>
      </footer>
    </div>
  );
}

function DefaultArtwork() {
  return (
    <div className="group relative flex h-full min-h-[580px] flex-col justify-between p-8 text-white lg:p-10">
      <img
        src="/auth-artwork.jpg"
        alt="Nhà trọ Ngọc Trang — cơ sở lưu trú thông minh hiện đại"
        className="absolute inset-0 h-full w-full object-cover transition-transform duration-700 ease-out group-hover:scale-105"
      />
      <div className="pointer-events-none absolute inset-0 bg-gradient-to-t from-[#042824] via-[#042824]/40 to-black/30" />
      <div className="pointer-events-none absolute inset-0 bg-primary/20 mix-blend-multiply" />

      <div className="relative z-10">
        <div className="inline-flex items-center gap-2 rounded-full border border-white/20 bg-surface-lowest/20 px-3 py-1.5 text-xs font-semibold tracking-wide text-white shadow-sm backdrop-blur-md">
          <span className="h-2 w-2 animate-pulse rounded-full bg-emerald-300" />
          Cơ sở lưu trú thông minh
        </div>
      </div>

      <div className="relative z-10 space-y-4 pt-12">
        <div className="space-y-2">
          <h2 className="font-display text-2xl leading-snug font-bold tracking-tight">
            Nhà Trọ Cao Cấp Ngọc Trang
          </h2>
          <p className="max-w-xs text-sm leading-relaxed text-white/85">
            Hệ thống quản lý tòa nhà, hợp đồng và hóa đơn minh bạch — vận hành
            chuyên nghiệp, an toàn tuyệt đối.
          </p>
        </div>
        <div className="text-[11px] font-medium tracking-widest text-white/60 uppercase">
          Property Management System
        </div>
      </div>
    </div>
  );
}
