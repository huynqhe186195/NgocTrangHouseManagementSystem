interface BrandProps {
  /** Hide the wordmark when only the mark is wanted, e.g. the sidebar rail. */
  compact?: boolean;
}

export function Brand({ compact = false }: BrandProps) {
  return (
    <div className="flex items-center gap-3">
      <img
        src="/ngoc-trang-logo.png"
        alt="Ngọc Trang"
        className="h-10 w-10 shrink-0 rounded-lg object-contain"
      />
      {!compact && (
        <div className="flex flex-col leading-tight">
          <span className="font-display text-base font-extrabold tracking-tight text-primary uppercase">
            Nhà trọ Ngọc Trang
          </span>
          <span className="text-[11px] text-on-surface-variant">Property Management System</span>
        </div>
      )}
    </div>
  );
}
