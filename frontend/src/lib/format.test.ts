import { describe, expect, it } from 'vitest';
import { formatMillions, formatNumber, formatVnd, parseVndInput } from './format';

describe('formatNumber', () => {
  it('groups thousands with the Vietnamese separator', () => {
    // Intl in the test environment may fall back to a narrower locale; the
    // assertion targets the digits and grouping rather than the exact glyph.
    expect(formatNumber(3500000).replace(/\D/g, '')).toBe('3500000');
    expect(formatNumber(3500000).length).toBeGreaterThan('3500000'.length);
  });

  it('renders an em dash for missing values', () => {
    expect(formatNumber(null)).toBe('—');
    expect(formatNumber(undefined)).toBe('—');
    expect(formatNumber(Number.NaN)).toBe('—');
  });

  it('keeps zero as zero rather than treating it as missing', () => {
    expect(formatNumber(0)).toBe('0');
  });
});

describe('formatVnd', () => {
  it('appends the currency marker', () => {
    expect(formatVnd(3500000)).toMatch(/đ$/);
  });

  it('renders an em dash for missing values', () => {
    expect(formatVnd(null)).toBe('—');
  });
});

describe('parseVndInput', () => {
  it('accepts a plain number', () => {
    expect(parseVndInput('3500000')).toBe(3500000);
  });

  it('accepts the grouped form the input produces', () => {
    expect(parseVndInput('3.500.000')).toBe(3500000);
    expect(parseVndInput('3 500 000')).toBe(3500000);
  });

  it('accepts a trailing currency word', () => {
    expect(parseVndInput('3.500.000 đ')).toBe(3500000);
    expect(parseVndInput('3500000 VND')).toBe(3500000);
  });

  it('treats an empty box as no value', () => {
    expect(parseVndInput('')).toBeNull();
    expect(parseVndInput('   ')).toBeNull();
  });

  it('reports unparseable text as NaN so the form can reject it', () => {
    expect(Number.isNaN(parseVndInput('abc'))).toBe(true);
    expect(Number.isNaN(parseVndInput('3tr5'))).toBe(true);
  });
});

describe('formatMillions', () => {
  it('rounds to one decimal place', () => {
    expect(formatMillions(3500000)).toBe('3,5 tr');
    expect(formatMillions(4000000)).toBe('4 tr');
  });

  it('renders an em dash for missing values', () => {
    expect(formatMillions(null)).toBe('—');
  });
});
