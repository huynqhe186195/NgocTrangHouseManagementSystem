import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { appConfig } from '../config';
import { useAuth } from './AuthProvider';

/**
 * Blocks protected routes until the stored session has been validated.
 * While `ready` is false the previous screen is kept instead of flashing
 * the login page on every reload.
 */
export function RequireAuth() {
  const { user, ready } = useAuth();
  const location = useLocation();

  if (!ready) {
    return (
      <div className="flex min-h-screen items-center justify-center text-on-surface-variant">
        Đang kiểm tra phiên đăng nhập…
      </div>
    );
  }

  if (!user) {
    return <Navigate to={appConfig.loginPath} replace state={{ from: location.pathname }} />;
  }

  return <Outlet />;
}
