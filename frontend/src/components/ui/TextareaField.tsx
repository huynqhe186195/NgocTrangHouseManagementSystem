import { useId } from 'react';
import type { ReactNode, TextareaHTMLAttributes } from 'react';

interface TextareaFieldProps extends Omit<TextareaHTMLAttributes<HTMLTextAreaElement>, 'id'> {
  label: string;
  required?: boolean;
  error?: string;
  hint?: ReactNode;
}

/** Textarea sibling of Field, sharing its label row, border and focus ring. */
export function TextareaField({
  label,
  required = false,
  error,
  hint,
  rows = 3,
  className = '',
  ...rest
}: TextareaFieldProps) {
  const id = useId();
  const errorId = `${id}-error`;

  return (
    <div className="flex flex-col gap-2">
      <label
        htmlFor={id}
        className="text-[11px] font-semibold tracking-[0.06em] text-on-surface-variant uppercase"
      >
        {label}
        {required && <span className="ml-1 text-error">*</span>}
      </label>
      <textarea
        id={id}
        rows={rows}
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? errorId : undefined}
        className={
          'w-full resize-y rounded-[0.5rem] border bg-surface-lowest px-3 py-2 text-sm text-on-surface ' +
          'placeholder:text-outline focus:outline-none focus:ring-3 ' +
          (error
            ? 'border-error focus:border-error focus:ring-error/15 '
            : 'border-outline-variant focus:border-primary focus:ring-primary/15 ') +
          className
        }
        {...rest}
      />
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
