/**
 * Display formatting shared by the screens.
 *
 * Money is stored as a plain decimal (VND has no minor unit), so no
 * currency library is involved: only grouping and the unit suffix differ.
 */

const vndFormatter = new Intl.NumberFormat('vi-VN', {
  maximumFractionDigits: 0,
  minimumFractionDigits: 0,
});

/** `3500000` -> `3.500.000`. Null/undefined renders as an em dash. */
export function formatNumber(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) {
    return '—';
  }
  return vndFormatter.format(value);
}

/** `3500000` -> `3.500.000 đ`. */
export function formatVnd(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) {
    return '—';
  }
  return `${vndFormatter.format(value)} đ`;
}

/**
 * Parse what the owner typed into a money field. Accepts the dots and spaces
 * the formatter itself produces, plus a trailing currency word, so a value
 * can be pasted back into an input without being rejected.
 * Returns null for an empty box, NaN for unparseable text.
 */
export function parseVndInput(raw: string): number | null {
  const cleaned = raw
    .replace(/\s/g, '')
    .replace(/đ|₫|vnd/gi, '')
    .replace(/[.,]/g, '');
  if (cleaned.length === 0) {
    return null;
  }
  if (!/^\d+$/.test(cleaned)) {
    return Number.NaN;
  }
  return Number(cleaned);
}

/** `3500000` -> `3,5` million, used where a compact figure reads better. */
export function formatMillions(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) {
    return '—';
  }
  const millions = value / 1_000_000;
  const rounded = Math.round(millions * 10) / 10;
  return `${rounded.toLocaleString('vi-VN')} tr`;
}
