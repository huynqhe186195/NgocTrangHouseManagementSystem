import type { ReactNode } from 'react';

type Tone = 'error' | 'info' | 'success' | 'warning';

const tones: Record<Tone, string> = {
  error: 'border-error/20 bg-error-container/60 text-on-surface',
  info: 'border-primary/20 bg-surface-low text-on-surface-variant',
  success: 'border-[#A7F3D0] bg-[#ECFDF5] text-[#065F46]',
  warning: 'border-[#FDE68A] bg-[#FFFBEB] text-[#92400E]',
};

export function Alert({ tone = 'info', children }: { tone?: Tone; children: ReactNode }) {
  return (
    <div role={tone === 'error' ? 'alert' : 'status'} className={`rounded-[0.5rem] border px-3.5 py-3 text-sm ${tones[tone]}`}>
      {children}
    </div>
  );
}
