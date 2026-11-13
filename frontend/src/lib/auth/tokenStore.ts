/**
 * Token storage.
 *
 * Tokens live in memory and, only while the user asked to be remembered,
 * in sessionStorage (per-tab, cleared when the tab closes). They are never
 * written to localStorage and never logged.
 */
export interface TokenSet {
  accessToken: string;
  refreshToken: string | null;
  /** Unix epoch milliseconds. */
  expiresAt: number;
}

const STORAGE_KEY = 'ngoctrang.tokens';

let current: TokenSet | null = null;

function readStored(): TokenSet | null {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY);
    if (!raw) {
      return null;
    }
    const parsed = JSON.parse(raw) as Partial<TokenSet>;
    if (typeof parsed.accessToken !== 'string' || parsed.accessToken.length === 0) {
      return null;
    }
    return {
      accessToken: parsed.accessToken,
      refreshToken: typeof parsed.refreshToken === 'string' ? parsed.refreshToken : null,
      expiresAt: typeof parsed.expiresAt === 'number' ? parsed.expiresAt : 0,
    };
  } catch {
    return null;
  }
}

function writeStored(tokens: TokenSet | null): void {
  try {
    if (tokens) {
      sessionStorage.setItem(STORAGE_KEY, JSON.stringify(tokens));
    } else {
      sessionStorage.removeItem(STORAGE_KEY);
    }
  } catch {
    // Storage can be unavailable; in-memory state still works for this tab.
  }
}

export const tokenStore = {
  get(): TokenSet | null {
    current ??= readStored();
    return current;
  },

  set(tokens: TokenSet, persist: boolean): void {
    current = tokens;
    writeStored(persist ? tokens : null);
  },

  clear(): void {
    current = null;
    writeStored(null);
  },

  /** True when the token is missing or within 30s of expiry. */
  isExpired(skewMs = 30_000): boolean {
    const tokens = this.get();
    if (!tokens) {
      return true;
    }
    return tokens.expiresAt - skewMs <= Date.now();
  },
};
