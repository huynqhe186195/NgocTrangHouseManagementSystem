import { useId } from 'react';
import type { ChangeEvent, ReactNode } from 'react';
import { Select } from 'antd';

export interface SelectOption {
  value: string;
  label: string;
  disabled?: boolean;
}

interface SelectFieldProps {
  label: string;
  required?: boolean;
  error?: string;
  hint?: ReactNode;
  options: SelectOption[];
  placeholder?: string;
  value?: string | number;
  onChange?: (event: ChangeEvent<HTMLSelectElement>) => void;
  disabled?: boolean;
  className?: string;
}

/**
 * Draws the app's dropdown with antd's Select, which brings keyboard
 * navigation, virtual scrolling and a searchable list.
 *
 * The props stay the native ones — callers still read `event.target.value` —
 * so the change is invisible to every form that uses it. The event handed to
 * `onChange` is only a carrier for the chosen value.
 */
export function SelectField({
  label,
  required = false,
  error,
  hint,
  options,
  placeholder,
  className = '',
  value,
  onChange,
  disabled,
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
      <Select
        id={id}
        className={`w-full ${className}`}
        value={value === undefined || value === '' ? undefined : String(value)}
        onChange={(next: string) =>
          onChange?.({ target: { value: next } } as ChangeEvent<HTMLSelectElement>)
        }
        options={options}
        placeholder={placeholder}
        disabled={disabled}
        status={error ? 'error' : undefined}
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? errorId : undefined}
        notFoundContent={null}
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
