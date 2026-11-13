import type { ReactNode } from 'react';

interface EmptyStateProps {
  icon: string;
  title: string;
  description?: ReactNode;
  action?: ReactNode;
}

export function EmptyState({ icon, title, description, action }: EmptyStateProps) {
  return (
    <div className="flex flex-col items-center justify-center gap-3 rounded-xl border border-dashed border-outline-variant bg-surface-lowest px-6 py-14 text-center">
      <span className="material-symbols-outlined text-4xl text-outline">{icon}</span>
      <p className="font-display text-base font-semibold text-on-surface">{title}</p>
      {description && (
        <p className="max-w-md text-sm leading-relaxed text-on-surface-variant">{description}</p>
      )}
      {action}
    </div>
  );
}
