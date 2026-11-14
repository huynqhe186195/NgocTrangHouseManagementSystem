import { useId } from 'react';
import type { ReactNode, SelectHTMLAttributes } from 'react';

export interface SelectOption {
  value: string;
  label: string;
  disabled?: boolean;
}

interface SelectFieldProps extends Omit<SelectHTMLAttributes<HTMLSelectElement>, 'id'> {
  label: string;
  required?: boolean;
  error?: string;
  hint?: ReactNode;
  options: SelectOption[];
  placeholder?: string;
}

/** Select sibling of Field, sharing its label row, border and focus ring. */
export function SelectField({
  label,
  required = false,
  error,
  hint,
  options,
  placeholder,
  className = '',
  ...rest
}: SelectFieldProps) {
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
      <select
        id={id}
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? errorId : undefined}
        className={
          'h-10 w-full rounded-[0.5rem] border bg-surface-lowest px-3 text-sm text-on-surface ' +
          'focus:outline-none focus:ring-3 ' +
          (error
            ? 'border-error focus:border-error focus:ring-error/15 '
            : 'border-outline-variant focus:border-primary focus:ring-primary/15 ') +
          className
        }
        {...rest}
      >
        {placeholder && <option value="">{placeholder}</option>}
        {options.map((option) => (
          <option key={option.value} value={option.value} disabled={option.disabled}>
            {option.label}
          </option>
        ))}
      </select>
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
