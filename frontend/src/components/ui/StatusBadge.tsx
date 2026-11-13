type Tone = 'success' | 'warning' | 'error' | 'neutral' | 'info';

const tones: Record<Tone, string> = {
  success: 'bg-[#ECFDF5] text-[#047857] ring-[#A7F3D0]',
  warning: 'bg-[#FFFBEB] text-[#B45309] ring-[#FDE68A]',
  error: 'bg-error-container/70 text-error ring-error/20',
  neutral: 'bg-surface-container text-on-surface-variant ring-outline-variant',
  info: 'bg-surface-low text-primary ring-primary/20',
};

interface StatusBadgeProps {
  tone?: Tone;
  children: React.ReactNode;
  withDot?: boolean;
}

export function StatusBadge({ tone = 'neutral', children, withDot = false }: StatusBadgeProps) {
  return (
    <span
      className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-semibold ring-1 ring-inset ${tones[tone]}`}
    >
      {withDot && (
        <span
          aria-hidden
          className={
            'h-1.5 w-1.5 rounded-full ' +
            (tone === 'success'
              ? 'bg-[#059669]'
              : tone === 'warning'
                ? 'bg-[#D97706]'
                : tone === 'error'
                  ? 'bg-error'
                  : 'bg-outline')
          }
        />
      )}
      {children}
    </span>
  );
}
