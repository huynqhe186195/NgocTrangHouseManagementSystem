import { useId } from 'react';
import type { InputHTMLAttributes, ReactNode } from 'react';

interface FieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'id'> {
  label: string;
  required?: boolean;
  error?: string;
  hint?: ReactNode;
  /** Rendered on the right of the label row, e.g. a "Quên mật khẩu?" link. */
  labelAction?: ReactNode;
  trailing?: ReactNode;
}

export function Field({
  label,
  required = false,
  error,
  hint,
  labelAction,
  trailing,
  className = '',
  ...rest
}: FieldProps) {
  const id = useId();
  const errorId = `${id}-error`;

  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-baseline justify-between gap-3">
        <label htmlFor={id} className="text-[11px] font-semibold tracking-[0.06em] text-on-surface-variant uppercase">
          {label}
          {required && <span className="ml-1 text-error">*</span>}
        </label>
        {labelAction}
      </div>
      <div className="relative">
        <input
          id={id}
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? errorId : undefined}
          className={
            'h-10 w-full rounded-[0.5rem] border bg-surface-lowest px-3 text-sm text-on-surface ' +
            'placeholder:text-outline focus:outline-none focus:ring-3 ' +
            (error
              ? 'border-error focus:border-error focus:ring-error/15 '
              : 'border-outline-variant focus:border-primary focus:ring-primary/15 ') +
            (trailing ? 'pr-11 ' : '') +
            className
          }
          {...rest}
        />
        {trailing && (
          <div className="absolute inset-y-0 right-0 flex items-center pr-3">{trailing}</div>
        )}
      </div>
      {error ? (
        <p id={errorId} className="text-xs text-error">
          {error}
        </p>
      ) : (
        hint && <div className="text-xs text-on-surface-variant">{hint}</div>
      )}
    </div>
  );
}
