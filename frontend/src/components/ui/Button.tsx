import type { ButtonHTMLAttributes, ReactNode } from 'react';

type Variant = 'primary' | 'secondary' | 'ghost';

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
  loading?: boolean;
  fullWidth?: boolean;
  icon?: ReactNode;
}

const base =
  'inline-flex items-center justify-center gap-2 rounded-[0.5rem] px-4 py-2.5 text-sm font-semibold transition ' +
  'focus:outline-none focus-visible:ring-3 disabled:cursor-not-allowed disabled:opacity-60';

const variants: Record<Variant, string> = {
  primary:
    'bg-primary text-on-primary shadow-[0_4px_14px_0_rgb(15_118_110/0.35)] hover:bg-primary-active ' +
    'focus-visible:ring-primary/25',
  secondary:
    'border border-outline-variant bg-surface-low text-on-surface-variant hover:bg-surface-container ' +
    'focus-visible:ring-primary/20',
  ghost: 'text-primary hover:bg-primary/5 focus-visible:ring-primary/20',
};

export function Button({
  variant = 'primary',
  loading = false,
  fullWidth = false,
  icon,
  children,
  className = '',
  disabled,
  type = 'button',
  ...rest
}: ButtonProps) {
  return (
    <button
      type={type}
      className={`${base} ${variants[variant]} ${fullWidth ? 'w-full' : ''} ${className}`}
      disabled={disabled || loading}
      aria-busy={loading}
      {...rest}
    >
      {loading ? <span className="h-4 w-4 animate-spin rounded-full border-2 border-current border-r-transparent" /> : icon}
      {children}
    </button>
  );
}
