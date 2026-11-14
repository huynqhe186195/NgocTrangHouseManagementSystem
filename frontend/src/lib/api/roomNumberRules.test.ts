import { describe, expect, it } from 'vitest';
import { suggestRoomNumber, validateRoomNumber } from './roomNumberRules';

describe('validateRoomNumber', () => {
  it('accepts a room number whose prefix matches the floor', () => {
    expect(validateRoomNumber('101', 1)).toBeNull();
    expect(validateRoomNumber('201', 2)).toBeNull();
    expect(validateRoomNumber('1205', 12)).toBeNull();
  });

  it('accepts the ground floor convention (0 -> 001)', () => {
    expect(validateRoomNumber('001', 0)).toBeNull();
  });

  it('rejects an empty value', () => {
    expect(validateRoomNumber('', 1)).toContain('Vui lòng nhập');
    expect(validateRoomNumber('   ', 1)).toContain('Vui lòng nhập');
  });

  it('rejects letters', () => {
    expect(validateRoomNumber('A201', 2)).toContain('chỉ được gồm chữ số');
    expect(validateRoomNumber('20a', 2)).toContain('chỉ được gồm chữ số');
  });

  it('rejects fewer than three digits', () => {
    expect(validateRoomNumber('01', 0)).toContain('ít nhất 3 chữ số');
    expect(validateRoomNumber('1', 1)).toContain('ít nhất 3 chữ số');
  });

  it('rejects a prefix that does not match the floor', () => {
    const message = validateRoomNumber('301', 2);
    expect(message).toContain('bắt đầu bằng số tầng');
    expect(message).toContain('201');
  });

  it('trims surrounding whitespace before validating', () => {
    expect(validateRoomNumber(' 201 ', 2)).toBeNull();
  });

  it('suggests a matching room number per floor', () => {
    expect(suggestRoomNumber(0)).toBe('001');
    expect(suggestRoomNumber(3)).toBe('301');
  });
});
