import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { formatNumber, parseVndInput } from '../../lib/format';

interface MoneyInputProps {
  label: string;
  /** Value in VND; null when the owner left the box empty. */
  value: number | null;
  onChange: (value: number | null) => void;
  required?: boolean;
  error?: string;
  hint?: ReactNode;
  placeholder?: string;
  disabled?: boolean;
  /** Suffix rendered inside the box, "đ" unless a caller wants "tr". */
  unit?: string;
}

/**
 * Grouped money box. The text the owner sees is re-derived from the numeric
 * value after every keystroke, so what gets submitted is always the parsed
 * integer rather than the formatted string.
 */
export function MoneyInput({
  label,
  value,
  onChange,
  required = false,
  error,
  hint,
  placeholder = '0',
  disabled = false,
  unit = 'đ',
}: MoneyInputProps) {
  const [text, setText] = useState(() => (value === null ? '' : formatNumber(value)));
  const [shapeError, setShapeError] = useState<string | null>(null);

  // Keep the box in step when the caller replaces the value (a reset, or a
  // detail panel loading a different room).
  useEffect(() => {
    setText(value === null ? '' : formatNumber(value));
    setShapeError(null);
  }, [value]);

  function handleChange(raw: string) {
    setText(raw);
    const parsed = parseVndInput(raw);
    if (parsed === null) {
      setShapeError(null);
      onChange(null);
      return;
    }
    if (Number.isNaN(parsed)) {
      setShapeError('Chỉ nhập được chữ số.');
      return;
    }
    setShapeError(null);
    onChange(parsed);
  }

  const shownError = error ?? shapeError;

  return (
    <div className="flex flex-col gap-2">
      <label className="text-[11px] font-semibold tracking-[0.06em] text-on-surface-variant uppercase">
        {label}
        {required && <span className="ml-1 text-error">*</span>}
      </label>
      <div className="relative">
        <input
          inputMode="numeric"
          aria-invalid={shownError ? true : undefined}
          aria-label={label}
          value={text}
          disabled={disabled}
          placeholder={placeholder}
          onChange={(event) => handleChange(event.target.value)}
          className={
            'tnum h-10 w-full rounded-[0.5rem] border bg-surface-lowest px-3 text-sm text-on-surface ' +
            'placeholder:text-outline focus:outline-none focus:ring-3 disabled:cursor-not-allowed disabled:opacity-60 ' +
            (shownError
              ? 'border-error focus:border-error focus:ring-error/15 '
              : 'border-outline-variant focus:border-primary focus:ring-primary/15 ') +
            (unit ? 'pr-10 ' : '')
          }
        />
        {unit && (
          <span className="pointer-events-none absolute inset-y-0 right-0 flex items-center pr-3 text-sm text-on-surface-variant">
            {unit}
          </span>
        )}
      </div>
      {shownError ? (
        <p className="text-xs text-error">{shownError}</p>
      ) : (
        hint && <div className="text-xs text-on-surface-variant">{hint}</div>
      )}
    </div>
  );
}
