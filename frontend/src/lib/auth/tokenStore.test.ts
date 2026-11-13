import { beforeEach, describe, expect, it } from 'vitest';
import { tokenStore } from './tokenStore';

describe('tokenStore', () => {
  beforeEach(() => {
    sessionStorage.clear();
    tokenStore.clear();
  });

  it('returns null when empty', () => {
    expect(tokenStore.get()).toBeNull();
  });

  it('isExpired is true without tokens', () => {
    expect(tokenStore.isExpired()).toBe(true);
  });

  it('keeps tokens in memory and sessionStorage only when persisting', () => {
    const tokens = { accessToken: 'a', refreshToken: 'r', expiresAt: Date.now() + 60_000 };
    tokenStore.set(tokens, true);
    expect(tokenStore.get()).toEqual(tokens);
    expect(JSON.parse(sessionStorage.getItem('ngoctrang.tokens')!)).toEqual(tokens);
    expect(localStorage.getItem('ngoctrang.tokens')).toBeNull();
  });

  it('does not persist to sessionStorage when remember is off', () => {
    const tokens = { accessToken: 'a', refreshToken: null, expiresAt: Date.now() + 60_000 };
    tokenStore.set(tokens, false);
    expect(tokenStore.get()).toEqual(tokens);
    expect(sessionStorage.getItem('ngoctrang.tokens')).toBeNull();
  });

  it('treats a token within the 30s skew as expired', () => {
    tokenStore.set(
      { accessToken: 'a', refreshToken: null, expiresAt: Date.now() + 10_000 },
      false,
    );
    expect(tokenStore.isExpired()).toBe(true);
  });

  it('clear removes memory and storage state', () => {
    tokenStore.set({ accessToken: 'a', refreshToken: null, expiresAt: Date.now() + 60_000 }, true);
    tokenStore.clear();
    expect(tokenStore.get()).toBeNull();
    expect(sessionStorage.getItem('ngoctrang.tokens')).toBeNull();
  });

  it('ignores corrupt storage payloads', () => {
    sessionStorage.setItem('ngoctrang.tokens', '{not-json');
    tokenStore.clear(); // reset the memoized current so get() re-reads storage
    // Clear sets current = null and removes the key; re-seed to simulate a
    // corrupted entry surviving from page load.
    sessionStorage.setItem('ngoctrang.tokens', '{not-json');
    const fresh = tokenStore as unknown as { get: () => unknown };
    // Force the memo to re-read by clearing only the in-memory slot:
    (tokenStore as unknown as { current: null }).current = null;
    expect(fresh.get()).toBeNull();
  });
});
