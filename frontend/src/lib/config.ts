/**
 * Runtime configuration.
 *
 * In development the Vite proxy forwards /api and /connect to the HTTPS
 * API host, so the base stays empty and every call is same-origin.
 * A deployed build sets VITE_API_BASE to the real API origin.
 */
export const apiBase: string = (import.meta.env.VITE_API_BASE ?? '').replace(/\/$/, '');

export const appConfig = {
  clientId: 'NgocTrangHouseManagementSystem_App',
  scopes: ['openid', 'profile', 'email', 'roles', 'NgocTrangHouseManagementSystem'],
  /** Where a protected route sends an anonymous visitor. */
  loginPath: '/login',
} as const;
