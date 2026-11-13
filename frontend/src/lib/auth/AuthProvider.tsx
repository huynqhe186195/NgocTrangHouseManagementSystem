import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { request, setAccessTokenProvider, unauthorizedEvent } from '../http';
import { authClient } from './authClient';
import { tokenStore } from './tokenStore';

interface ApplicationConfiguration {
  currentUser?: {
    id?: string;
    userName?: string | null;
    email?: string | null;
    name?: string | null;
    isAuthenticated: boolean;
    roles?: string[];
  } | null;
  auth?: {
    grantedPolicies?: Record<string, boolean>;
  };
}

export interface CurrentUser {
  id: string;
  userName: string;
  email: string | null;
  name: string | null;
  roles: string[];
}

export interface AuthContextValue {
  user: CurrentUser | null;
  grantedPolicies: Record<string, boolean>;
  /** False until the stored session has been validated. */
  ready: boolean;
  signIn: (username: string, password: string, remember: boolean) => Promise<void>;
  signOut: () => void;
  hasPermission: (policy: string) => boolean;
}

const AuthContext = createContext<AuthContextValue | null>(null);

function toCurrentUser(config: ApplicationConfiguration): CurrentUser | null {
  const current = config.currentUser;
  if (!current?.isAuthenticated || !current.id) {
    return null;
  }
  return {
    id: current.id,
    userName: current.userName ?? current.email ?? '',
    email: current.email ?? null,
    name: current.name ?? null,
    roles: current.roles ?? [],
  };
}

async function loadConfiguration(): Promise<ApplicationConfiguration> {
  return request<ApplicationConfiguration>('/api/abp/application-configuration', {
    suppressUnauthorizedEvent: true,
  });
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<CurrentUser | null>(null);
  const [grantedPolicies, setGrantedPolicies] = useState<Record<string, boolean>>({});
  const [ready, setReady] = useState(false);
  const signingOut = useRef(false);

  const signOut = useCallback(() => {
    if (signingOut.current) {
      return;
    }
    signingOut.current = true;
    tokenStore.clear();
    setUser(null);
    setGrantedPolicies({});
    signingOut.current = false;
  }, []);

  // The HTTP layer reads the token through this provider so nothing has to
  // pass it around by hand.
  useEffect(() => {
    setAccessTokenProvider(() => tokenStore.get()?.accessToken ?? null);
  }, []);

  // A 401 from any call ends the session in one place.
  useEffect(() => {
    const onUnauthorized = () => signOut();
    window.addEventListener(unauthorizedEvent, onUnauthorized);
    return () => window.removeEventListener(unauthorizedEvent, onUnauthorized);
  }, [signOut]);

  const hydrate = useCallback(async () => {
    if (!tokenStore.get()) {
      setReady(true);
      return;
    }
    if (tokenStore.isExpired()) {
      tokenStore.clear();
      setReady(true);
      return;
    }
    try {
      const config = await loadConfiguration();
      const nextUser = toCurrentUser(config);
      if (!nextUser) {
        tokenStore.clear();
      }
      setUser(nextUser);
      setGrantedPolicies(config.auth?.grantedPolicies ?? {});
    } catch {
      tokenStore.clear();
      setUser(null);
      setGrantedPolicies({});
    } finally {
      setReady(true);
    }
  }, []);

  useEffect(() => {
    void hydrate();
  }, [hydrate]);

  const signIn = useCallback(async (username: string, password: string, remember: boolean) => {
    const tokens = await authClient.login(username, password);
    tokenStore.set(tokens, remember);
    try {
      const config = await loadConfiguration();
      const nextUser = toCurrentUser(config);
      if (!nextUser) {
        throw new Error('Phiên đăng nhập không hợp lệ.');
      }
      setUser(nextUser);
      setGrantedPolicies(config.auth?.grantedPolicies ?? {});
    } catch (error) {
      tokenStore.clear();
      throw error;
    }
  }, []);

  const hasPermission = useCallback(
    (policy: string) => grantedPolicies[policy] === true,
    [grantedPolicies],
  );

  const value = useMemo<AuthContextValue>(
    () => ({ user, grantedPolicies, ready, signIn, signOut, hasPermission }),
    [user, grantedPolicies, ready, signIn, signOut, hasPermission],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error('useAuth phải dùng bên trong AuthProvider.');
  }
  return context;
}
